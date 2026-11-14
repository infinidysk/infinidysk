//! The SABnzbd API at `/api?mode=…`, as Sonarr, Radarr and friends call it.
//!
//! Parameters come from the query string, then the form body; empty values count as absent.
//! Every response is JSON, whatever `output` asks for.

use std::collections::{HashMap, HashSet};
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::time::Duration;

use axum::Router;
use axum::body::{Body, BodyDataStream, Bytes};
use axum::extract::{DefaultBodyLimit, FromRequest, Multipart, Query, Request, State};
use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use axum::{Form, Json};
use futures_util::StreamExt as _;
use infinidysk_core::JobId;
use infinidysk_db::{HistoryEntry, HistoryQuery, JobStatus, QueueEntry, now_ms};
use infinidysk_queue::import::Snapshot;
use infinidysk_queue::{Queue, Submission, service};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::sync::{RwLock, Semaphore, SemaphorePermit};
use tokio::time::Instant;
use uuid::Uuid;

use crate::arr::{Probe, Probes};
use crate::config::{ConfigStore, ImportStrategy, Sab as SabSettings, Settings};
use crate::fetch::Fetcher;

/// The SABnzbd version we answer as.
const VERSION: &str = "4.5.1";
pub(crate) const MAX_NZB: usize = infinidysk_queue::nzb::MAX_UPLOAD;
const MAX_NAME: usize = 255;
const HISTORY_CAP: usize = 10_000;
const AUTH_WARN_EVERY_MS: i64 = 5 * 60 * 1000;
const UNAUTH_BODY_LIMIT: usize = 64 * 1024;
const UNAUTH_CONCURRENCY: usize = 4;
const UNAUTH_FIELD_LIMIT: usize = 32;
/// Largest piece of a multipart body passed before its form API key matches.
const UNAUTH_CHUNK: usize = 4096;
/// How long a request may take to send its form API key.
const UNAUTH_READ_TIMEOUT: Duration = Duration::from_secs(30);
const FOLDER_GONE: &str = "Download folder no longer exists.";

pub struct Sab {
    pub queue: Arc<Queue>,
    pub config: Arc<RwLock<ConfigStore>>,
    /// Arr discovery probes our SAB calls answer.
    pub probes: Arc<Probes>,
    fetcher: Fetcher,
    last_auth_warning: AtomicI64,
    unauth_uploads: Semaphore,
}

impl Sab {
    pub fn new(
        queue: Arc<Queue>,
        config: Arc<RwLock<ConfigStore>>,
    ) -> Result<Self, reqwest::Error> {
        Ok(Self {
            queue,
            config,
            probes: Arc::default(),
            fetcher: Fetcher::new()?,
            last_auth_warning: AtomicI64::new(i64::MIN),
            unauth_uploads: Semaphore::new(UNAUTH_CONCURRENCY),
        })
    }
}

pub fn router(state: Arc<Sab>) -> Router {
    Router::new()
        .route("/api", axum::routing::get(api).post(api))
        .layer(DefaultBodyLimit::max(MAX_NZB))
        .with_state(state)
}

enum Fail {
    Bad(String),
    Auth(&'static str),
    Internal,
}

impl From<infinidysk_db::Error> for Fail {
    fn from(error: infinidysk_db::Error) -> Self {
        tracing::warn!(reason = %error, "SAB API request failed");
        Self::Internal
    }
}

impl IntoResponse for Fail {
    fn into_response(self) -> Response {
        let (code, error) = match self {
            Self::Bad(msg) => (StatusCode::BAD_REQUEST, msg),
            Self::Auth(msg) => (StatusCode::UNAUTHORIZED, msg.to_owned()),
            Self::Internal => (
                StatusCode::INTERNAL_SERVER_ERROR,
                "An internal server error occurred.".to_owned(),
            ),
        };
        (code, Json(json!({ "status": false, "error": error }))).into_response()
    }
}

fn bad(msg: &str) -> Fail {
    Fail::Bad(msg.to_owned())
}

#[derive(Default)]
struct Params {
    pairs: Vec<(String, String)>,
    nzb: Option<(String, Vec<u8>)>,
    /// `nzo_ids` from a JSON body.
    json_ids: Vec<String>,
}

#[derive(Deserialize)]
struct JsonIds {
    #[serde(default)]
    nzo_ids: Vec<String>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum FormKind {
    Multipart,
    UrlEncoded,
    Json,
    Other,
}

impl FormKind {
    fn from_request(req: &Request) -> Self {
        let media_type = req
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.split(';').next())
            .unwrap_or_default()
            .trim();
        if media_type.eq_ignore_ascii_case("multipart/form-data") {
            Self::Multipart
        } else if media_type.eq_ignore_ascii_case("application/x-www-form-urlencoded") {
            Self::UrlEncoded
        } else if media_type
            .to_ascii_lowercase()
            .starts_with("application/json")
        {
            Self::Json
        } else {
            Self::Other
        }
    }
}

/// What a request without a header or query key gets while its form body is read.
struct Preauth<'a> {
    /// The configured API key the form's `apikey` field must match.
    key: &'a str,
    /// The body read fails if the key has not arrived by then.
    deadline: Instant,
    /// One of the `UNAUTH_CONCURRENCY` slots; released once the key matches.
    permit: SemaphorePermit<'a>,
}

/// The state of [`gated_body`] between chunks.
struct Gated {
    input: Pin<Box<BodyDataStream>>,
    /// The rest of a chunk that was split at `UNAUTH_CHUNK`.
    pending: Option<Bytes>,
    /// Bytes passed while the gate was closed.
    used: usize,
    /// Opens when the form's `apikey` field matches.
    gate: Arc<AtomicBool>,
    deadline: Instant,
}

/// Limits a multipart body until its form API key opens `gate`.
///
/// While the gate is closed, chunks pass in pieces of at most `UNAUTH_CHUNK` bytes, the body
/// fails after `UNAUTH_BODY_LIMIT` bytes, and it fails if no chunk arrives before `deadline`.
/// Small pieces let the multipart reader see the key, and open the gate, before the limit.
/// Once the gate is open, chunks pass unchanged and without a deadline.
fn gated_body(body: Body, gate: Arc<AtomicBool>, deadline: Instant) -> Body {
    let state = Gated {
        input: Box::pin(body.into_data_stream()),
        pending: None,
        used: 0,
        gate,
        deadline,
    };
    Body::from_stream(futures_util::stream::unfold(state, Gated::next))
}

impl Gated {
    /// The next piece of the body, or an error once a closed gate hits its limit or deadline.
    async fn next(mut self) -> Option<(std::io::Result<Bytes>, Self)> {
        // A client body that is always ready never returns `Pending`, so the multipart reader
        // would hold the worker thread for the whole body. Yield once per chunk so one upload
        // cannot starve other tasks, including the timers that cancel it.
        tokio::task::yield_now().await;
        let bytes = if let Some(bytes) = self.pending.take() {
            bytes
        } else {
            let next = if self.gate.load(Ordering::Relaxed) {
                self.input.next().await
            } else if let Ok(next) = tokio::time::timeout_at(self.deadline, self.input.next()).await
            {
                next
            } else {
                let error = std::io::Error::other("Unauthenticated SAB body read timed out");
                return Some((Err(error), self));
            };
            match next {
                Some(Ok(bytes)) => bytes,
                Some(Err(_)) => {
                    let error = std::io::Error::other("Invalid SAB request body");
                    return Some((Err(error), self));
                }
                None => return None,
            }
        };
        if self.gate.load(Ordering::Relaxed) {
            return Some((Ok(bytes), self));
        }
        let room = UNAUTH_BODY_LIMIT - self.used;
        if room == 0 {
            let error = std::io::Error::other("Unauthenticated SAB body limit exceeded");
            return Some((Err(error), self));
        }
        let take = room.min(UNAUTH_CHUNK);
        if bytes.len() > take {
            self.pending = Some(bytes.slice(take..));
            self.used += take;
            return Some((Ok(bytes.slice(..take)), self));
        }
        self.used += bytes.len();
        Some((Ok(bytes), self))
    }
}

impl Params {
    async fn read(
        mut req: Request,
        pairs: Vec<(String, String)>,
        kind: FormKind,
        preauth: Option<Preauth<'_>>,
    ) -> Result<Self, Fail> {
        let mut params = Self {
            pairs,
            ..Self::default()
        };
        let (expected_key, deadline, mut permit) = preauth.map_or((None, None, None), |p| {
            (Some(p.key), Some(p.deadline), Some(p.permit))
        });
        if kind == FormKind::Multipart {
            let authenticated = Arc::new(AtomicBool::new(expected_key.is_none()));
            let req = if let Some(deadline) = deadline {
                let (parts, body) = req.into_parts();
                let body = gated_body(body, Arc::clone(&authenticated), deadline);
                Request::from_parts(parts, body)
            } else {
                req
            };
            let mut form = Multipart::from_request(req, &()).await.map_err(|_| {
                if expected_key.is_some() {
                    Fail::Auth("API Key Required")
                } else {
                    bad("Invalid multipart body")
                }
            })?;
            let mut preauth_remaining = UNAUTH_BODY_LIMIT;
            let mut authorized = expected_key.is_none();
            let mut fields = 0usize;
            while let Some(mut field) = form.next_field().await.map_err(|_| {
                if authorized {
                    bad("Invalid multipart body")
                } else {
                    Fail::Auth("API Key Required")
                }
            })? {
                fields += 1;
                if !authorized && fields > UNAUTH_FIELD_LIMIT {
                    return Err(Fail::Auth("API Key Required"));
                }
                let name = field.name().unwrap_or_default().to_owned();
                let file_name = field.file_name().map(str::to_owned);
                let is_nzb_file = name.eq_ignore_ascii_case("nzbFile");
                if !authorized {
                    let mut data = Vec::new();
                    while let Some(chunk) = field
                        .chunk()
                        .await
                        .map_err(|_| Fail::Auth("API Key Required"))?
                    {
                        if chunk.len() > preauth_remaining {
                            return Err(Fail::Auth("API Key Required"));
                        }
                        preauth_remaining -= chunk.len();
                        data.extend_from_slice(&chunk);
                    }
                    if let Some(file_name) =
                        file_name.filter(|_| is_nzb_file || name.eq_ignore_ascii_case("name"))
                    {
                        if is_nzb_file || params.nzb.is_none() {
                            params.nzb = Some((file_name, data));
                        }
                    } else {
                        let value =
                            String::from_utf8(data).map_err(|_| bad("Invalid form field"))?;
                        if name.eq_ignore_ascii_case("apikey") && !value.is_empty() {
                            if expected_key.is_some_and(|expected| {
                                !expected.is_empty() && same(value.as_bytes(), expected.as_bytes())
                            }) {
                                authorized = true;
                                authenticated.store(true, Ordering::Relaxed);
                                // The rest of the body is an authenticated upload.
                                drop(permit.take());
                            } else {
                                return Err(Fail::Auth("API Key Incorrect"));
                            }
                        }
                        params.pairs.push((name, value));
                    }
                    continue;
                }
                match file_name {
                    Some(file_name) if is_nzb_file || name.eq_ignore_ascii_case("name") => {
                        let data = field.bytes().await.map_err(|_| bad("Invalid nzbFile"))?;
                        if is_nzb_file || params.nzb.is_none() {
                            params.nzb = Some((file_name, data.to_vec()));
                        }
                    }
                    _ => {
                        let value = field.text().await.map_err(|_| bad("Invalid form field"))?;
                        params.pairs.push((name, value));
                    }
                }
            }
        } else if kind == FormKind::UrlEncoded {
            req.headers_mut().insert(
                header::CONTENT_TYPE,
                header::HeaderValue::from_static("application/x-www-form-urlencoded"),
            );
            if expected_key.is_some() {
                DefaultBodyLimit::max(UNAUTH_BODY_LIMIT).apply(&mut req);
            }
            let form = Form::<Vec<(String, String)>>::from_request(req, &());
            let form = if let Some(deadline) = deadline {
                tokio::time::timeout_at(deadline, form)
                    .await
                    .map_err(|_| Fail::Auth("API Key Required"))?
            } else {
                form.await
            };
            let Form(pairs) = form.map_err(|error| match error {
                axum::extract::rejection::FormRejection::BytesRejection(_)
                    if expected_key.is_some() =>
                {
                    Fail::Auth("API Key Required")
                }
                _ => bad("Invalid form body"),
            })?;
            params.pairs.extend(pairs);
        } else if kind == FormKind::Json {
            let Json(body) = Json::<JsonIds>::from_request(req, &())
                .await
                .map_err(|_| bad("Invalid JSON body"))?;
            params.json_ids = body.nzo_ids;
        }
        Ok(params)
    }

    /// Keys match case-insensitively, like the original's request collections.
    fn get(&self, key: &str) -> Option<&str> {
        self.pairs
            .iter()
            .find(|(k, v)| k.eq_ignore_ascii_case(key) && !v.is_empty())
            .map(|(_, v)| v.as_str())
    }

    /// Every `value` token plus JSON `nzo_ids`, trimmed.
    fn values(&self) -> impl Iterator<Item = &str> {
        self.pairs
            .iter()
            .filter(|(k, _)| k.eq_ignore_ascii_case("value"))
            .flat_map(|(_, v)| v.split(','))
            .chain(self.json_ids.iter().map(String::as_str))
            .map(str::trim)
            .filter(|v| !v.is_empty())
    }

    fn category(&self, settings: &SabSettings) -> Option<String> {
        let cat = self.get("cat").or_else(|| self.get("category"))?;
        Some(if cat == "*" {
            settings.manual_category.clone()
        } else {
            cat.to_owned()
        })
    }

    fn search(&self) -> Option<String> {
        self.get("search")
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_lowercase)
    }

    fn start(&self) -> Result<usize, Fail> {
        self.get("start").map_or(Ok(0), |s| {
            s.parse::<i64>()
                .map(|n| usize::try_from(n).unwrap_or(0))
                .map_err(|_| bad("Invalid start parameter"))
        })
    }

    /// `None` means unlimited.
    fn limit(&self, key: &str, msg: &str) -> Result<Option<usize>, Fail> {
        self.get(key).map_or(Ok(None), |s| {
            s.parse::<i64>()
                .map(|n| usize::try_from(n).ok().filter(|&n| n > 0))
                .map_err(|_| bad(msg))
        })
    }
}

struct Ctx {
    sab: SabSettings,
    /// Where the current import strategy hands jobs off.
    completed_dir: String,
    /// `/completed-symlinks` in the mount, where jobs without a stored path were handed off.
    links_dir: String,
}

impl Ctx {
    fn new(settings: &Settings) -> Self {
        let links_dir = format!(
            "{}/completed-symlinks",
            settings.mount.dir.trim_end_matches('/')
        );
        Self {
            sab: settings.sab.clone(),
            completed_dir: match settings.imports.strategy {
                ImportStrategy::Symlinks => links_dir.clone(),
                ImportStrategy::Strm => settings.imports.strm_dir.trim_end_matches('/').to_owned(),
            },
            links_dir,
        }
    }
}

async fn api(State(state): State<Arc<Sab>>, req: Request) -> Response {
    match handle(&state, req).await {
        Ok(body) => Json(body).into_response(),
        Err(fail) => fail.into_response(),
    }
}

async fn handle(state: &Sab, req: Request) -> Result<Value, Fail> {
    let header_key = req
        .headers()
        .get("x-api-key")
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);
    let query = Query::<Vec<(String, String)>>::try_from_uri(req.uri())
        .map_err(|_| bad("Invalid query string"))?
        .0;
    let query_key = query
        .iter()
        .find(|(name, value)| name.eq_ignore_ascii_case("apikey") && !value.is_empty())
        .map(|(_, value)| value.as_str());
    let mode_hint = query
        .iter()
        .find(|(name, value)| name.eq_ignore_ascii_case("mode") && !value.is_empty())
        .map(|(_, value)| value.as_str())
        .unwrap_or_default();
    let ctx = Ctx::new(state.config.read().await.settings());
    let given = header_key.as_deref().or(query_key);
    let authorized = mode_hint != "version" && given.is_some();
    if authorized {
        authorize(state, &ctx.sab, given)?;
    }
    if mode_hint == "version" {
        return Ok(json!({ "status": true, "error": null, "version": VERSION }));
    }
    let preauth = !authorized;
    let kind = FormKind::from_request(&req);
    if preauth && !matches!(kind, FormKind::Multipart | FormKind::UrlEncoded) {
        warn_rejected(state, "API Key Required");
        return Err(Fail::Auth("API Key Required"));
    }
    let preauth = if preauth {
        // Stays 401 like every other preauth rejection: SAB clients only know key errors.
        let permit = state.unauth_uploads.try_acquire().map_err(|_| {
            if should_warn(state) {
                tracing::warn!(
                    reason = "too many unauthenticated uploads at once",
                    limit = UNAUTH_CONCURRENCY,
                    "SAB API request rejected; send the API key in the query string or header"
                );
            }
            Fail::Auth("API Key Required")
        })?;
        Some(Preauth {
            key: ctx.sab.api_key.0.as_str(),
            deadline: Instant::now() + UNAUTH_READ_TIMEOUT,
            permit,
        })
    } else {
        None
    };
    let probe = req.extensions().get::<Probe>().cloned();
    let checked = preauth.is_some();
    let p = Params::read(req, query, kind, preauth)
        .await
        .inspect_err(|fail| {
            if let Fail::Auth(reason) = fail
                && checked
            {
                warn_rejected(state, reason);
            }
        })?;
    let mode = p.get("mode").unwrap_or_default();
    let name = p.get("name").unwrap_or_default();
    let known = matches!(
        mode,
        "version"
            | "get_config"
            | "get_cats"
            | "status"
            | "fullstatus"
            | "addfile"
            | "addurl"
            | "queue"
            | "history"
            | "retry"
            | "pause"
            | "resume"
    ) || (mode, name) == ("config", "speedlimit");
    if !known {
        return Err(bad("Invalid mode"));
    }
    if mode != "version" {
        if !authorized {
            authorize(state, &ctx.sab, p.get("apikey"))?;
        }
        if let Some(Probe(nonce)) = probe {
            state.probes.arrived(&nonce);
        }
    }
    match (mode, name) {
        ("version", _) => Ok(json!({ "status": true, "error": null, "version": VERSION })),
        ("get_config", _) => Ok(get_config(&ctx)),
        ("get_cats", _) => {
            let cats: Vec<&str> = std::iter::once("*")
                .chain(ctx.sab.categories().filter(|c| *c != "*"))
                .collect();
            Ok(json!({ "categories": cats }))
        }
        ("status" | "fullstatus", _) => Ok(json!({ "status": {
            "completedir": ctx.completed_dir,
            "paused": state.queue.is_paused(),
            "speedlimit": "0",
            "speedlimit_abs": "0",
        }})),
        ("addfile", _) => add_file(state, &ctx, p).await,
        ("addurl", _) => add_url(state, &ctx, &p).await,
        ("pause" | "resume", _) => {
            state
                .queue
                .set_paused(mode == "pause")
                .await
                .map_err(queue_failed)?;
            Ok(json!({ "status": true, "error": null }))
        }
        // Streaming has no download to throttle; accepted for clients that set it.
        ("config", _) => Ok(json!({ "status": true, "error": null })),
        ("queue", "delete") => delete_queue(state, &p).await,
        ("queue", "pause" | "resume") => {
            let ids = job_ids(p.get("value").unwrap_or_default().split(','));
            state
                .queue
                .pause_jobs(&ids, name == "pause")
                .await
                .map_err(queue_failed)?;
            let ids: Vec<String> = ids.iter().map(ToString::to_string).collect();
            Ok(json!({ "status": true, "error": null, "nzo_ids": ids }))
        }
        ("queue", _) => list_queue(state, &ctx, &p).await,
        ("history", "delete") => delete_history(state, &p).await,
        ("retry", _) => retry(state, &p).await,
        _ => list_history(state, &ctx, &p).await,
    }
}

fn authorize(state: &Sab, sab: &SabSettings, given: Option<&str>) -> Result<(), Fail> {
    let fail = match given {
        None => "API Key Required",
        Some(key)
            if !sab.api_key.0.is_empty() && same(key.as_bytes(), sab.api_key.0.as_bytes()) =>
        {
            return Ok(());
        }
        Some(_) => "API Key Incorrect",
    };
    warn_rejected(state, fail);
    Err(Fail::Auth(fail))
}

/// Logs a rejected API key, at most once per `AUTH_WARN_EVERY_MS`.
fn warn_rejected(state: &Sab, reason: &str) {
    if should_warn(state) {
        tracing::warn!(
            reason,
            "SAB API request rejected; update the API key in the download client"
        );
    }
}

/// True for at most one caller per `AUTH_WARN_EVERY_MS`, so rejections cannot flood the log.
fn should_warn(state: &Sab) -> bool {
    let now = now_ms();
    let last = state.last_auth_warning.load(Ordering::Relaxed);
    now.saturating_sub(last) >= AUTH_WARN_EVERY_MS
        && state
            .last_auth_warning
            .compare_exchange(last, now, Ordering::Relaxed, Ordering::Relaxed)
            .is_ok()
}

pub(crate) fn same(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn get_config(ctx: &Ctx) -> Value {
    let mut categories = vec![json!({
        "name": "*", "order": 0, "pp": "3", "script": "None", "dir": "", "newzbin": "", "priority": 0,
    })];
    categories.extend(ctx.sab.categories().map(|c| {
        json!({
            "name": c, "order": 0, "pp": "", "script": "Default", "dir": c, "newzbin": "", "priority": -100,
        })
    }));
    json!({ "config": {
        "misc": {
            "complete_dir": ctx.completed_dir,
            "pre_check": 0,
            "history_retention": "",
            "history_retention_option": "all",
            "history_retention_number": 1,
        },
        "categories": categories,
        "servers": [],
    }})
}

/// SABnzbd's NZB naming: trimmed, no `.gz`, ending in `.nzb`, one path segment.
pub(crate) fn nzb_file_name(raw: &str) -> Result<String, &'static str> {
    let name = raw.trim();
    let name = match name.len().checked_sub(3) {
        Some(cut)
            if name
                .get(cut..)
                .is_some_and(|e| e.eq_ignore_ascii_case(".gz")) =>
        {
            &name[..cut]
        }
        _ => name,
    };
    let has_ext = name
        .len()
        .checked_sub(4)
        .and_then(|cut| name.get(cut..))
        .is_some_and(|e| e.eq_ignore_ascii_case(".nzb"));
    let name = if has_ext {
        name.to_owned()
    } else {
        format!("{name}.nzb")
    };
    if name.contains(['/', '\\', '\0']) || name == "." || name == ".." {
        return Err("NZB filename must be a single path segment.");
    }
    if name.len() > MAX_NAME {
        return Err("NZB filename exceeds the maximum name length.");
    }
    Ok(name)
}

async fn add_file(state: &Sab, ctx: &Ctx, mut p: Params) -> Result<Value, Fail> {
    let Some((form_name, raw)) = p.nzb.take() else {
        return Err(bad("Invalid nzbFile/name param"));
    };
    let name = p
        .get("nzbname")
        .filter(|n| !n.trim().is_empty())
        .unwrap_or(&form_name);
    if name.trim().is_empty() {
        return Err(bad("NZB filename could not be determined."));
    }
    let file_name = nzb_file_name(name).map_err(bad)?;
    let (priority, post_processing, category) = options(ctx, &p)?;
    let job = Submission {
        file_name: &file_name,
        category: &category,
        priority,
        post_processing,
    };
    let _writing = state.queue.db().write_permit().await;
    match state.queue.enqueue(&job, &raw).await {
        Ok(id) => Ok(json!({ "status": true, "error": null, "nzo_ids": [id.to_string()] })),
        Err(error) => Err(queue_failed(error)),
    }
}

/// Fetches the NZB at `name` and queues it. Like SABnzbd, it answers with a job id even
/// when the fetch or the NZB fails; that job is in history as failed with the reason.
async fn add_url(state: &Sab, ctx: &Ctx, p: &Params) -> Result<Value, Fail> {
    let Some(url) = p.get("name").map(str::trim) else {
        return Err(bad("Invalid URL"));
    };
    let (priority, post_processing, category) = options(ctx, p)?;
    let given = p.get("nzbname").filter(|n| !n.trim().is_empty());
    let configured = {
        let store = state.config.read().await;
        reqwest::Url::parse(url).ok().and_then(|requested| {
            let matched = store
                .settings()
                .indexers
                .iter()
                .filter(|indexer| indexer.enabled)
                .find_map(|indexer| {
                    let endpoint = crate::newznab::endpoint(indexer).ok()?;
                    (endpoint.origin() == requested.origin())
                        .then(|| (endpoint, indexer.api_key.0.clone()))
                });
            matched.map(|(endpoint, key)| (requested, endpoint, key))
        })
    };
    let fetched = if let Some((requested, endpoint, key)) = configured {
        Fetcher::fetch_configured(requested, &endpoint, &key).await
    } else {
        state.fetcher.fetch(url).await
    };
    // ponytail: with no name anywhere the job is named by time; reading the NZB's own
    // `<meta type="name">` would do better if unnamed URLs turn up.
    let fallback = format!("Unnamed NZB {}", now_ms());
    let name = given
        .map(str::to_owned)
        .or_else(|| fetched.as_ref().ok().and_then(|f| f.file_name.clone()))
        .unwrap_or(fallback);
    let file_name = nzb_file_name(&name).map_err(bad)?;
    let job = Submission {
        file_name: &file_name,
        category: &category,
        priority,
        post_processing,
    };
    let writing = state.queue.db().write_permit().await;
    let queued = match fetched {
        Ok(fetched) => state.queue.enqueue(&job, &fetched.body).await,
        Err(reason) => state.queue.refuse(&job, 0, reason).await,
    };
    drop(writing);
    let id = match queued {
        Ok(id) | Err(service::Error::Invalid { job: id, .. }) => id,
        Err(error) => return Err(queue_failed(error)),
    };
    Ok(json!({ "status": true, "error": null, "nzo_ids": [id.to_string()] }))
}

/// `priority`, `pp` and `cat` of an add request.
fn options(ctx: &Ctx, p: &Params) -> Result<(i16, i16, String), Fail> {
    // -2 adds the job paused.
    let priority = match p.get("priority") {
        None | Some("-100" | "0") => 0,
        Some(s @ ("-3" | "-2" | "-1" | "1" | "2")) => s.parse().unwrap_or_default(),
        Some(_) => return Err(bad("Invalid priority")),
    };
    let post_processing = match p.get("pp") {
        None | Some("-1" | "0") => 0,
        Some(s @ ("1" | "2" | "3")) => s.parse().unwrap_or_default(),
        Some(_) => return Err(bad("Invalid pp param")),
    };
    let category = p
        .category(&ctx.sab)
        .unwrap_or_else(|| ctx.sab.manual_category.clone());
    Ok((priority, post_processing, category))
}

fn queue_failed(error: service::Error) -> Fail {
    match error {
        service::Error::Nzb(_)
        | service::Error::Invalid { .. }
        | service::Error::NotInHistory
        | service::Error::NotFailed
        | service::Error::NoNzb => Fail::Bad(error.to_string()),
        error => {
            tracing::warn!(reason = %error, "SAB API queue request failed");
            Fail::Internal
        }
    }
}

fn priority_name(priority: i16) -> &'static str {
    match priority {
        -3 => "Duplicate",
        -2 => "Paused",
        -1 => "Low",
        1 => "High",
        2 => "Force",
        _ => "Normal",
    }
}

fn mib(bytes: u64) -> String {
    #[allow(clippy::cast_precision_loss)] // Display only; two decimals of MiB.
    let mib = bytes as f64 / (1024.0 * 1024.0);
    format!("{mib:.2}")
}

fn matches_search(search: Option<&str>, job_name: &str, file_name: &str) -> bool {
    search
        .is_none_or(|s| job_name.to_lowercase().contains(s) || file_name.to_lowercase().contains(s))
}

fn page<T>(items: Vec<T>, start: usize, limit: Option<usize>) -> impl Iterator<Item = T> {
    items
        .into_iter()
        .skip(start)
        .take(limit.unwrap_or(usize::MAX))
}

// ponytail: `sort`/`dir` are ignored (queue order, newest history first); the download
// clients we serve never send them. Add when the UI needs server-side sorting.
async fn list_queue(state: &Sab, ctx: &Ctx, p: &Params) -> Result<Value, Fail> {
    let start = p.start()?;
    let limit = p.limit("limit", "Invalid limit parameter")?;
    let category = p.category(&ctx.sab);
    let search = p.search();
    let wanted = p.get("status").map(str::to_ascii_lowercase);
    let active = state.queue.active();
    let progress: HashMap<JobId, Snapshot> = state.queue.progress().into_iter().collect();
    let status = |job: &QueueEntry| {
        if active.contains(&job.id) {
            "Downloading"
        } else if job.paused {
            "Paused"
        } else {
            "Queued"
        }
    };

    let mut jobs: Vec<QueueEntry> = state
        .queue
        .db()
        .queue()
        .await?
        .into_iter()
        .filter(|j| category.as_ref().is_none_or(|c| &j.category == c))
        .filter(|j| matches_search(search.as_deref(), &j.job_name, &j.file_name))
        .filter(|j| {
            wanted
                .as_deref()
                .is_none_or(|w| status(j).eq_ignore_ascii_case(w))
        })
        .collect();
    // Importing jobs first, in start order; the sort is stable.
    jobs.sort_by_key(|j| active.iter().position(|a| *a == j.id).unwrap_or(usize::MAX));
    let total = jobs.len();
    let slots: Vec<Value> = page(jobs, start, limit)
        .enumerate()
        .map(|(i, j)| {
            let p = progress.get(&j.id);
            let percent = p.map_or(0, |p| p.percent).to_string();
            let left = j.total_bytes / 100 * u64::from(100 - p.map_or(0, |p| p.percent));
            json!({
                "index": start + i,
                "nzo_id": j.id.to_string(),
                "priority": priority_name(j.priority),
                "filename": j.job_name,
                "cat": j.category,
                "percentage": percent,
                "true_percentage": percent,
                "status": status(&j),
                "timeleft": time_left(p.and_then(|p| p.eta)),
                "mb": mib(j.total_bytes),
                "mbleft": mib(left),
                "indexer": null,
                "providers": null,
            })
        })
        .collect();
    let longest = progress.values().filter_map(|p| p.eta).max();
    Ok(json!({ "status": true, "error": null, "queue": {
        "paused": state.queue.is_paused(),
        "slots": slots,
        "noofslots": total,
        "speedlimit": "0",
        "speedlimit_abs": "0",
        "pause_int": "0",
        "speed": "0 ",
        "kbpersec": "0.00",
        "timeleft": time_left(longest),
    }}))
}

/// SAB's `H:MM:SS`, `D:HH:MM:SS` from a day on.
fn time_left(eta: Option<Duration>) -> String {
    let s = eta.map_or(0, |d| d.as_secs());
    let (days, h, m, s) = (s / 86_400, s % 86_400 / 3600, s % 3600 / 60, s % 60);
    if days > 0 {
        format!("{days}:{h:02}:{m:02}:{s:02}")
    } else {
        format!("{h}:{m:02}:{s:02}")
    }
}

fn job_ids<'a>(tokens: impl Iterator<Item = &'a str>) -> Vec<JobId> {
    let mut seen = HashSet::new();
    tokens
        .filter_map(|t| Uuid::parse_str(t).ok())
        .filter(|id| seen.insert(*id))
        .map(JobId)
        .collect()
}

async fn delete_queue(state: &Sab, p: &Params) -> Result<Value, Fail> {
    let db = state.queue.db();
    let ids = if p.values().any(|v| v.eq_ignore_ascii_case("all")) {
        let category = p
            .get("cat")
            .or_else(|| p.get("category"))
            .filter(|c| *c != "*");
        db.queue()
            .await?
            .into_iter()
            .filter(|j| category.is_none_or(|c| j.category == c))
            .map(|j| j.id)
            .collect()
    } else {
        job_ids(p.values())
    };
    // ponytail: removing the job being imported drops its result with a warning when the
    // import finishes; add cancellation once imports are long enough to matter.
    for id in ids {
        db.remove_job(id).await?;
    }
    Ok(json!({ "status": true, "error": null }))
}

/// Status as reported: a completed job whose folder is gone counts as failed.
fn reported(entry: &HistoryEntry, dir: Option<&String>) -> (&'static str, String) {
    match entry.status {
        JobStatus::Completed if dir.is_some() => ("Completed", String::new()),
        JobStatus::Completed => ("Failed", FOLDER_GONE.to_owned()),
        JobStatus::Failed => ("Failed", entry.fail_message.clone().unwrap_or_default()),
    }
}

async fn list_history(state: &Sab, ctx: &Ctx, p: &Params) -> Result<Value, Fail> {
    let start = p.start()?;
    // Like `limit`-ignoring SABnzbd setups: download clients ask for a short page and then
    // miss older jobs they still track, so only the explicit `pageSize` limits.
    let limit = p
        .limit("pageSize", "Invalid pageSize parameter")?
        .unwrap_or(HISTORY_CAP)
        .min(HISTORY_CAP);
    let ids = match p.get("nzo_ids") {
        None => None,
        Some(raw) => {
            let tokens: Vec<&str> = raw
                .split(',')
                .map(str::trim)
                .filter(|t| !t.is_empty())
                .collect();
            let invalid: Vec<&str> = tokens
                .iter()
                .copied()
                .filter(|t| Uuid::parse_str(t).is_err())
                .collect();
            if !invalid.is_empty() {
                let shown: Vec<String> = invalid.iter().take(5).map(|t| format!("'{t}'")).collect();
                let more = invalid.len().saturating_sub(5);
                let suffix = if more > 0 {
                    format!(" ... ({more} more)")
                } else {
                    String::new()
                };
                return Err(Fail::Bad(format!(
                    "Invalid nzo_ids parameter: {}{suffix}",
                    shown.join(", ")
                )));
            }
            Some(job_ids(tokens.into_iter()))
        }
    };
    let failed_only = matches!(p.get("failed_only"), Some("1" | "true" | "True"));
    let wanted = if failed_only {
        Some("failed".to_owned())
    } else {
        p.get("status").map(str::to_ascii_lowercase)
    };
    let status = match wanted.as_deref() {
        None => None,
        Some("completed") => Some(JobStatus::Completed),
        Some("failed") => Some(JobStatus::Failed),
        // No job is reported as anything else.
        Some(_) => {
            return Ok(json!({ "status": true, "error": null,
                "history": { "slots": [], "noofslots": 0 } }));
        }
    };
    let category = p.category(&ctx.sab);
    let search = p.search();

    let (rows, total) = state
        .queue
        .db()
        .history_page(&HistoryQuery {
            ids: ids.as_deref(),
            category: category.as_deref(),
            search: search.as_deref(),
            status,
            failed_since: None,
            offset: start,
            limit: Some(limit),
        })
        .await?;
    let slots: Vec<Value> = rows
        .into_iter()
        .map(|(h, dir)| {
            let (status, fail_message) = reported(&h, dir.as_ref());
            let storage = (status == "Completed").then_some(dir).flatten().map(|d| {
                h.storage.clone().unwrap_or_else(|| {
                    format!(
                        "{}{}",
                        ctx.links_dir,
                        d.strip_prefix("/content").unwrap_or(&d)
                    )
                })
            });
            json!({
                "nzo_id": h.id.to_string(),
                "nzb_name": h.file_name,
                "name": h.job_name,
                "category": h.category,
                "status": status,
                "bytes": h.total_bytes,
                "storage": storage,
                "download_time": h.download_seconds,
                "completed": h.created_at / 1000,
                "fail_message": fail_message,
                "nzb_blob_id": h.nzb_blob.map(|b| b.0.to_string()),
                "indexer": null,
                "providers": null,
            })
        })
        .collect();
    Ok(json!({ "status": true, "error": null, "history": { "slots": slots, "noofslots": total } }))
}

async fn delete_history(state: &Sab, p: &Params) -> Result<Value, Fail> {
    let db = state.queue.db();
    let all = p.values().any(|v| v.eq_ignore_ascii_case("all"));
    let failed = p.values().any(|v| v.eq_ignore_ascii_case("failed"));
    let ids: Vec<JobId> = if all || failed {
        db.history_ids(!all).await?
    } else {
        job_ids(p.values())
    };
    db.delete_history(&ids).await?;
    Ok(json!({ "status": true, "error": null }))
}

/// Queues failed jobs again from their stored NZB, as new jobs. Fails only when none could.
async fn retry(state: &Sab, p: &Params) -> Result<Value, Fail> {
    let ids = job_ids(p.values());
    if ids.is_empty() {
        return Err(bad("Missing or invalid value (nzo_id)."));
    }
    let mut queued = Vec::new();
    let mut failed = Vec::new();
    for id in ids {
        match retry_one(state, id).await {
            Ok(new) => queued.push(new.to_string()),
            Err(Fail::Bad(error)) => {
                failed.push(json!({ "nzo_id": id.to_string(), "error": error }));
            }
            Err(fail) => return Err(fail),
        }
    }
    if queued.is_empty() {
        let first = failed.first().and_then(|f| f["error"].as_str());
        return Err(bad(first.unwrap_or_default()));
    }
    let single = match queued.as_slice() {
        [one] => Some(one.clone()),
        _ => None,
    };
    Ok(json!({
        "status": true,
        "error": null,
        "nzo_id": single,
        "nzo_ids": queued,
        "failed": (!failed.is_empty()).then_some(failed),
    }))
}

async fn retry_one(state: &Sab, id: JobId) -> Result<JobId, Fail> {
    state.queue.retry(id).await.map_err(queue_failed)
}

#[cfg(test)]
mod tests {
    use std::convert::Infallible;

    use super::*;

    /// A body that sends `chunks` and then ends.
    fn body(chunks: Vec<Bytes>) -> Body {
        Body::from_stream(futures_util::stream::iter(
            chunks.into_iter().map(Ok::<_, Infallible>),
        ))
    }

    fn later() -> Instant {
        Instant::now() + Duration::from_secs(60)
    }

    /// Every chunk length the gated body yields, then whether it ended with an error.
    async fn drain(body: Body) -> (Vec<usize>, bool) {
        let mut stream = body.into_data_stream();
        let mut lengths = Vec::new();
        while let Some(item) = stream.next().await {
            match item {
                Ok(bytes) => lengths.push(bytes.len()),
                Err(_) => return (lengths, true),
            }
        }
        (lengths, false)
    }

    #[tokio::test]
    async fn a_body_ending_exactly_at_the_limit_passes() {
        let chunks = vec![Bytes::from(vec![b'a'; UNAUTH_BODY_LIMIT])];
        let gate = Arc::new(AtomicBool::new(false));
        let (lengths, failed) = drain(gated_body(body(chunks), gate, later())).await;
        assert!(!failed);
        assert_eq!(lengths.iter().sum::<usize>(), UNAUTH_BODY_LIMIT);
        assert!(lengths.iter().all(|&len| len <= UNAUTH_CHUNK));
    }

    #[tokio::test]
    async fn a_chunk_after_the_limit_fails_even_when_empty() {
        let chunks = vec![Bytes::from(vec![b'a'; UNAUTH_BODY_LIMIT]), Bytes::new()];
        let gate = Arc::new(AtomicBool::new(false));
        let (lengths, failed) = drain(gated_body(body(chunks), gate, later())).await;
        assert!(failed);
        assert_eq!(lengths.iter().sum::<usize>(), UNAUTH_BODY_LIMIT);
    }

    #[tokio::test]
    async fn a_chunk_crossing_the_limit_is_cut_and_fails() {
        let chunks = vec![Bytes::from(vec![b'a'; UNAUTH_BODY_LIMIT + 1])];
        let gate = Arc::new(AtomicBool::new(false));
        let (lengths, failed) = drain(gated_body(body(chunks), gate, later())).await;
        assert!(failed);
        assert_eq!(lengths.iter().sum::<usize>(), UNAUTH_BODY_LIMIT);
    }

    #[tokio::test]
    async fn opening_the_gate_passes_the_pending_remainder_whole() {
        let chunks = vec![
            Bytes::from(vec![b'a'; UNAUTH_CHUNK + 100]),
            Bytes::from(vec![b'b'; UNAUTH_BODY_LIMIT * 2]),
        ];
        let gate = Arc::new(AtomicBool::new(false));
        let mut stream = gated_body(body(chunks), Arc::clone(&gate), later()).into_data_stream();
        assert_eq!(stream.next().await.unwrap().unwrap().len(), UNAUTH_CHUNK);
        gate.store(true, Ordering::Relaxed);
        assert_eq!(stream.next().await.unwrap().unwrap().len(), 100);
        assert_eq!(
            stream.next().await.unwrap().unwrap().len(),
            UNAUTH_BODY_LIMIT * 2
        );
        assert!(stream.next().await.is_none());
    }

    #[tokio::test]
    async fn a_closed_gate_times_out_a_silent_body() {
        let silent =
            Body::from_stream(futures_util::stream::pending::<Result<Bytes, Infallible>>());
        let gate = Arc::new(AtomicBool::new(false));
        let deadline = Instant::now() + Duration::from_millis(20);
        let (lengths, failed) = drain(gated_body(silent, gate, deadline)).await;
        assert!(failed);
        assert_eq!(lengths, Vec::<usize>::new());
    }
}
