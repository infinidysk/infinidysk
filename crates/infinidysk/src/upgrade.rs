//! Settings carried over from a C# install; deleted together with the importer.

use std::collections::{HashMap, HashSet};
use std::fmt::Write as _;
use std::future::IntoFuture as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, PoisonError};
use std::time::{Duration, Instant};

use axum::extract::{Request, State};
use axum::http::{HeaderMap, StatusCode, header};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use infinidysk_core::ItemId;
use infinidysk_db::Db;
use infinidysk_import::{ArrKind, Config, ProviderKind, Source};
use infinidysk_nntp::{Bandwidth, Client, catalog};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use tokio::net::TcpListener;
use tokio::sync::{Notify, watch};
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;
use uuid::Uuid;

use crate::admin::{Admin, Fail};
use crate::config::{
    ArrInstance, ArrProfile, ConfigStore, ImportStrategy, Oidc, Provider, Quota, QuotaPeriod,
    Secret, Settings, UnownedAction,
};
use crate::quota::Quotas;

/// Config key of the C# `WebDAV` password hash waiting for its first use.
pub const WEBDAV_KEY: &str = "upgrade.webdav";
/// Failed attempts remembered, so retrying clients do not cost a hash each.
const MAX_FAILED: usize = 1024;

/// C#'s `WebDAV` password hash, accepted until a client signs in with it once.
#[derive(Serialize, Deserialize)]
pub struct LegacyWebDav {
    /// In our stored form ([`infinidysk_import::legacy_hash`]).
    pub hash: String,
    /// The password generated in its place; once the user changes it, the hash is dropped.
    pub replaced: String,
}

pub struct WebDavAdoption {
    admin: Arc<Admin>,
    pending: AtomicBool,
    legacy: tokio::sync::Mutex<Option<LegacyWebDav>>,
    failed: std::sync::Mutex<HashSet<[u8; 32]>>,
}

impl WebDavAdoption {
    pub async fn load(admin: Arc<Admin>) -> Self {
        let legacy: Option<LegacyWebDav> = match admin.queue.db().config().await {
            Ok(config) => config
                .into_iter()
                .find(|(k, _)| k == WEBDAV_KEY)
                .and_then(|(_, v)| serde_json::from_str(&v).ok()),
            Err(error) => {
                tracing::warn!(reason = %error, "reading the C# WebDAV password failed");
                None
            }
        };
        Self {
            admin,
            pending: AtomicBool::new(legacy.is_some()),
            legacy: tokio::sync::Mutex::new(legacy),
            failed: std::sync::Mutex::default(),
        }
    }

    /// Keeps `password` as ours when it is the one C# hashed.
    #[allow(
        clippy::significant_drop_tightening,
        reason = "the lock makes concurrent sign-ins check the hash once"
    )]
    async fn adopt(&self, user: &str, password: &str) {
        let mut legacy = self.legacy.lock().await;
        let Some(l) = legacy.as_ref() else {
            return;
        };
        let current = self.admin.config.read().await.settings().webdav.clone();
        if current.password.0 != l.replaced {
            self.finish(&mut legacy).await;
            return;
        }
        if user != current.user || password == current.password.0 {
            return;
        }
        let attempt: [u8; 32] = Sha256::digest(format!("{user}\0{password}")).into();
        if self.failed().contains(&attempt) {
            return;
        }
        let (hash, given) = (l.hash.clone(), password.to_owned());
        let ok = tokio::task::spawn_blocking(move || {
            infinidysk_import::verify_legacy(&hash, &given) == Some(true)
        })
        .await
        .unwrap_or(false);
        if !ok {
            self.remember_failed(attempt);
            return;
        }
        let mut store = self.admin.config.write().await;
        let mut next = store.settings().clone();
        next.webdav.password = Secret(password.to_owned());
        if let Err(error) = store.save(next).await {
            tracing::warn!(reason = %error, "could not keep the C# WebDAV password");
            return;
        }
        if let Err(error) = self.admin.live.apply(store.settings()).await {
            tracing::warn!(reason = %error, "could not apply the C# WebDAV password");
        }
        drop(store);
        tracing::info!("WebDAV password carried over from the C# install");
        self.finish(&mut legacy).await;
    }

    async fn finish(&self, legacy: &mut Option<LegacyWebDav>) {
        *legacy = None;
        self.pending.store(false, Ordering::Relaxed);
        if let Err(error) = self.admin.queue.db().write_config(&[], &[WEBDAV_KEY]).await {
            tracing::warn!(reason = %error, "could not drop the C# WebDAV password");
        }
    }

    fn failed(&self) -> std::sync::MutexGuard<'_, HashSet<[u8; 32]>> {
        self.failed.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn remember_failed(&self, attempt: [u8; 32]) {
        let mut failed = self.failed();
        if failed.len() >= MAX_FAILED {
            failed.clear();
        }
        failed.insert(attempt);
    }
}

/// Before `WebDAV` checks credentials, adopts the C# password a client signs in with.
pub async fn adopt_webdav(
    State(adoption): State<Arc<WebDavAdoption>>,
    request: Request,
    next: Next,
) -> Response {
    if adoption.pending.load(Ordering::Relaxed)
        && let Some((user, password)) = basic(request.headers())
    {
        adoption.adopt(&user, &password).await;
    }
    next.run(request).await
}

fn basic(headers: &HeaderMap) -> Option<(String, String)> {
    let (scheme, encoded) = headers
        .get(header::AUTHORIZATION)?
        .to_str()
        .ok()?
        .split_once(' ')?;
    if !scheme.eq_ignore_ascii_case("basic") {
        return None;
    }
    let decoded = String::from_utf8(STANDARD.decode(encoded.trim()).ok()?).ok()?;
    let (user, password) = decoded.split_once(':')?;
    Some((user.to_owned(), password.to_owned()))
}

/// C# settings with a counterpart or their own note here.
const MAPPED: &[&str] = &[
    "api.key",
    "api.categories",
    "api.manual-category",
    "api.download-file-blocklist",
    "general.base-url",
    "webdav.user",
    "webdav.pass",
    "webdav.enforce-readonly",
    "rclone.mount-dir",
    "queue.worker-count",
    "repair.enable",
    "repair.degraded-tolerance-enabled",
    "usenet.providers",
    "usenet.bandwidth-limit-mbps",
    "arr.instances",
    "api.strm-key",
    "api.import-strategy",
    "api.completed-downloads-dir",
    "media.library-dir",
    "maintenance.remove-orphaned-schedule-enabled",
    "maintenance.remove-orphaned-schedule-time",
];
/// C#'s built-in blocklist, used while its setting was empty.
const CSHARP_BLOCKLIST: [&str; 5] = ["*.nfo", "*.par2", "*.sfv", "*unpack.mkv", "*.unpack.mp4"];
const CSHARP_MOUNT_DIR: &str = "/mnt/nzbdav";
const CSHARP_DOWNLOAD_LIMIT: &str = "usenet.bandwidth-limit-mbps";
/// The download limit slider's stops as `(Mbit/s, stored MB/s)`: 25, 50, 75, 100 and
/// 200 % of a gigabit connection.
const DOWNLOAD_LIMIT_STOPS: [(u32, u32); 5] =
    [(250, 31), (500, 63), (750, 94), (1000, 125), (2000, 250)];

pub struct Upgrade {
    pub settings: Settings,
    /// C#'s `WebDAV` password hash, accepted at the first sign-in ([`LegacyWebDav`]).
    pub webdav_hash: Option<String>,
    /// Bytes already counted against each provider's download limit.
    pub quota_used: Vec<(Uuid, u64)>,
    /// What the user should know: values not carried over, and why.
    pub notes: Vec<String>,
}

/// These settings in place of the C# ones in `cs`.
pub fn settings(cs: &Config, default_cache_dir: &str) -> anyhow::Result<Upgrade> {
    let mut s = Settings::default();
    // C# often did not record a file's download, so no Arr can be asked to replace it.
    s.health.unowned_action = UnownedAction::ActionNeeded;
    let mut notes = Vec::new();
    if let Some(key) = cs
        .get("api.key")
        .or_else(|| cs.var("FRONTEND_BACKEND_API_KEY"))
    {
        s.sab.api_key = Secret(key.to_owned());
    }
    if let Some(manual) = cs.get("api.manual-category") {
        manual.clone_into(&mut s.sab.manual_category);
    }
    if let Some(categories) = cs.get("api.categories").or_else(|| cs.var("CATEGORIES")) {
        s.sab.categories = split(categories)
            .filter(|c| *c != s.sab.manual_category)
            .map(str::to_owned)
            .collect();
    }
    if let Some(list) = cs.get("api.download-file-blocklist") {
        s.queue.extra_blocklist = split(list)
            .map(str::to_lowercase)
            .filter(|g| !CSHARP_BLOCKLIST.contains(&g.as_str()))
            .collect();
    }
    if let Some(url) = cs.get("general.base-url") {
        url.clone_into(&mut s.network.public_url);
    }
    for cidr in cs
        .var("TRUSTED_PROXY_CIDRS")
        .map(split)
        .into_iter()
        .flatten()
    {
        if !s.network.trusted_proxies.iter().any(|p| p == cidr) {
            s.network.trusted_proxies.push(cidr.to_owned());
        }
    }
    if let Some(user) = cs.get("webdav.user").or_else(|| cs.var("WEBDAV_USER")) {
        user.clone_into(&mut s.webdav.user);
    }
    let mut webdav_hash = None;
    if let Some(pass) = cs.var("WEBDAV_PASSWORD") {
        s.webdav.password = Secret(pass.to_owned());
    } else if let Some(hash) = cs.get("webdav.pass") {
        webdav_hash = Some(infinidysk_import::legacy_hash(hash, ""));
    }
    if flag(cs, "webdav.enforce-readonly") == Some(false) {
        notes.push(
            "WebDAV is read-only now: deleting under /content or a completed folder under \
             /completed-symlinks no longer removes files or history. Delete files on the \
             Explorer page; Arr deletes, health checks and maintenance still remove them"
                .to_owned(),
        );
    }
    let mount = cs
        .get("rclone.mount-dir")
        .or_else(|| cs.var("MOUNT_DIR"))
        .unwrap_or(CSHARP_MOUNT_DIR)
        .trim_end_matches('/');
    if !mount.is_empty() {
        mount.clone_into(&mut s.mount.dir);
    }
    if let Some(n) = cs
        .get("queue.worker-count")
        .and_then(|v| v.parse::<i64>().ok())
    {
        s.queue.concurrency = u8::try_from(n.clamp(1, 10)).unwrap_or(s.queue.concurrency);
    }
    if let Some(on) = flag(cs, "repair.enable") {
        s.health.enabled = on;
    }
    if let Some(on) = flag(cs, "repair.degraded-tolerance-enabled") {
        s.health.degraded_tolerance = on;
    }
    if let Some(mbps) = cs.get(CSHARP_DOWNLOAD_LIMIT) {
        match download_limit(mbps) {
            Ok(limit) => s.network.download_limit_mb_per_s = limit,
            Err(_) => notes.push(format!(
                "download limit not carried over: {mbps:?} is not a number of Mbit/s"
            )),
        }
    }
    if let Some(key) = cs.get("api.strm-key") {
        s.imports.strm_key = Secret(key.to_owned());
    }
    if cs
        .get("api.import-strategy")
        .is_some_and(|v| v.eq_ignore_ascii_case("strm"))
    {
        s.imports.strategy = ImportStrategy::Strm;
    }
    if let Some(dir) = cs.get("api.completed-downloads-dir") {
        dir.clone_into(&mut s.imports.strm_dir);
    }
    if let Some(dir) = cs.get("media.library-dir") {
        s.imports.library_dirs = vec![crate::config::LibraryDir {
            path: dir.to_owned(),
            arr_path: String::new(),
        }];
    }
    if flag(cs, "maintenance.remove-orphaned-schedule-enabled") == Some(true) {
        s.maintenance
            .scheduled
            .push(crate::config::MaintenanceJob::RemoveUnlinked);
        // C# ran it daily at this local minute (invalid meant midnight); the window
        // opens then for three hours.
        let start = cs
            .get("maintenance.remove-orphaned-schedule-time")
            .and_then(|v| v.parse::<u16>().ok())
            .filter(|m| *m < 24 * 60)
            .unwrap_or(0);
        let hhmm = |m: u16| format!("{:02}:{:02}", m / 60, m % 60);
        s.maintenance.window = crate::config::MaintenanceWindow {
            start: hhmm(start),
            end: hhmm((start + 180) % (24 * 60)),
        };
    }
    let mut quota_used = Vec::new();
    match cs.providers() {
        Ok(list) => {
            s.providers = providers(list, |p| cs.quota_used(p), &mut quota_used, &mut notes);
        }
        Err(error) => notes.push(format!(
            "Usenet providers: the C# list is unreadable ({error}); add them again"
        )),
    }
    match cs.arr_instances() {
        Ok(list) => s.arr_instances = arr_instances(list, &mut notes),
        Err(error) => notes.push(format!(
            "Arr instances: the C# list is unreadable ({error}); add them again"
        )),
    }
    s.auth.oidc = oidc(|n| cs.var(n), cs.admin.is_some(), &mut notes)?;
    keep_valid(&mut s, &mut notes, default_cache_dir);
    let dropped: Vec<&str> = cs.keys().filter(|k| !MAPPED.contains(k)).collect();
    if !dropped.is_empty() {
        notes.push(format!(
            "not carried over (fixed by policy or not in this version): {}",
            dropped.join(", ")
        ));
    }
    Ok(Upgrade {
        settings: s,
        webdav_hash,
        quota_used,
        notes,
    })
}

/// 1.x sign-in through OIDC, set only by the frontend's `OIDC_*` variables; 1.x had it on
/// only with all three required ones. Without a 1.x admin account, settings 2.x rejects
/// stop the import: dropping them would open setup to anyone.
fn oidc<'a>(
    var: impl Fn(&str) -> Option<&'a str>,
    has_admin: bool,
    notes: &mut Vec<String>,
) -> anyhow::Result<Oidc> {
    let mut o = Oidc::default();
    let (Some(issuer), Some(id), Some(secret)) = (
        var("OIDC_ISSUER"),
        var("OIDC_CLIENT_ID"),
        var("OIDC_CLIENT_SECRET"),
    ) else {
        return Ok(o);
    };
    issuer.clone_into(&mut o.issuer);
    id.clone_into(&mut o.client_id);
    o.client_secret = Secret(secret.to_owned());
    if let Some(v) = var("OIDC_REDIRECT_URI") {
        v.clone_into(&mut o.redirect_uri);
    }
    if let Some(v) = var("OIDC_SCOPES") {
        v.clone_into(&mut o.scopes);
    }
    if let Some(v) = var("OIDC_USERNAME_CLAIM") {
        v.clone_into(&mut o.username_claim);
    }
    if let Err(error) = o.validate() {
        anyhow::ensure!(
            has_admin,
            "single sign-on from 1.x cannot be carried over: {error}. 1.x had no local admin \
             account, so the import stops instead of letting anyone create one. Fix the OIDC_* \
             variables, or remove them to create a password account instead, then restart"
        );
        notes.push(format!(
            "single sign-on not carried over: {error}; sign in with your 1.x password and set \
             it up again under Settings → Access"
        ));
        return Ok(Oidc::default());
    }
    if let Some(claim) = var("OIDC_ADMIN_CLAIM") {
        notes.push(format!(
            "OIDC_ADMIN_CLAIM ({claim}) not carried over: every SSO user is an admin in 2.x, so \
             users 1.x showed as read-only can now change settings; limit who may use this \
             app in your identity provider"
        ));
    }
    notes.push(
        "single sign-on carried over from the OIDC_* variables, which 2.x no longer reads"
            .to_owned(),
    );
    Ok(o)
}

/// Leaves the C# value of one field out of the settings.
type Fallback = (&'static str, fn(&mut Settings));

/// Saving invalid settings keeps none of them, so this leaves out the values C# accepted
/// and we reject, one at a time, until the rest is valid.
fn keep_valid(s: &mut Settings, notes: &mut Vec<String>, default_cache_dir: &str) {
    let fallbacks: [Fallback; 3] = [
        ("library folders", |s| s.imports.library_dirs.clear()),
        ("public URL", |s| s.network.public_url.clear()),
        ("trusted proxies", |s| {
            s.network.trusted_proxies = Settings::default().network.trusted_proxies;
        }),
    ];
    let mut fallbacks = fallbacks.into_iter();
    while let Err(error) = s.validate_in(default_cache_dir) {
        let Some((what, reset)) = fallbacks.next() else {
            notes.push(format!("the carried-over settings are invalid: {error}"));
            return;
        };
        let mut next = s.clone();
        reset(&mut next);
        if next
            .validate_in(default_cache_dir)
            .map_err(|e| e.to_string())
            != Err(error.to_string())
        {
            notes.push(format!("{what} not carried over: {error}"));
            *s = next;
        }
    }
}

fn split(list: &str) -> impl Iterator<Item = &str> {
    list.split(',').map(str::trim).filter(|v| !v.is_empty())
}

fn flag(cs: &Config, key: &str) -> Option<bool> {
    cs.get(key).map(|v| v.eq_ignore_ascii_case("true"))
}

/// The slider stop at or below C#'s Mbit/s limit, so the headroom kept under a line's
/// speed stays; the lowest stop below them all. `None` for no limit (0 or empty).
fn download_limit(mbps: &str) -> Result<Option<u32>, std::num::ParseFloatError> {
    let mbps = mbps.trim();
    if mbps.is_empty() {
        return Ok(None);
    }
    let mbps: f64 = mbps.parse()?;
    if mbps.is_nan() || mbps <= 0.0 {
        return Ok(None);
    }
    let (_, lowest) = DOWNLOAD_LIMIT_STOPS[0];
    Ok(Some(
        DOWNLOAD_LIMIT_STOPS
            .iter()
            .rev()
            .find(|(stop, _)| f64::from(*stop) <= mbps)
            .map_or(lowest, |(_, mb)| *mb),
    ))
}

/// Pooled providers form the first level; backups follow, one level per C# priority. A
/// provider outside the catalog takes the backbone of a catalog provider in its C#
/// storage group.
fn providers(
    list: Vec<infinidysk_import::Provider>,
    used: impl Fn(&infinidysk_import::Provider) -> u64,
    quota_used: &mut Vec<(Uuid, u64)>,
    notes: &mut Vec<String>,
) -> Vec<Provider> {
    let server = |host: &str| {
        catalog::PROFILES.iter().find_map(|profile| {
            profile
                .servers
                .iter()
                .find(|s| s.hostname.eq_ignore_ascii_case(host))
                .map(|s| (profile, s))
        })
    };
    let group = |p: &infinidysk_import::Provider| p.storage_group.trim().to_lowercase();
    let group_backbones: HashMap<String, &str> = list
        .iter()
        .filter(|p| !group(p).is_empty())
        .filter_map(|p| server(&p.host).map(|(_, s)| (group(p), s.backbone)))
        .collect();
    let mut backups: Vec<i64> = list
        .iter()
        .filter(|p| p.kind == ProviderKind::Backup)
        .map(|p| p.priority)
        .collect();
    backups.sort_unstable();
    backups.dedup();
    let defaults = Provider::default();
    list.into_iter()
        .map(|p| {
            let name = p
                .nickname
                .as_deref()
                .filter(|n| !n.trim().is_empty())
                .unwrap_or(&p.host)
                .to_owned();
            let port = u16::try_from(p.port).unwrap_or(defaults.port);
            // A catalog profile fixes the port; on another port the provider stays
            // custom but keeps the catalog backbone.
            let (profile, backbone) = match server(&p.host) {
                Some((profile, s)) if s.port == port => (Some(profile.id.to_owned()), None),
                Some((_, s)) => (None, Some(s.backbone.to_owned())),
                None => {
                    let backbone = group_backbones.get(&group(&p)).map(|b| (*b).to_owned());
                    if !group(&p).is_empty() {
                        notes.push(backbone.as_ref().map_or_else(
                            || {
                                format!(
                                    "provider {name}: not in the catalog, so its storage group \
                                     {:?} no longer applies; pick its backbone if you know it",
                                    p.storage_group
                                )
                            },
                            |b| {
                                format!(
                                    "provider {name}: not in the catalog; backbone {b} taken \
                                     from its storage group {:?}",
                                    p.storage_group
                                )
                            },
                        ));
                    }
                    (None, backbone)
                }
            };
            if p.skip_tls_verification {
                notes.push(format!(
                    "provider {name}: TLS certificates are now always verified"
                ));
            }
            let quota = p
                .byte_limit
                .and_then(|b| u64::try_from(b).ok())
                .filter(|b| *b > 0)
                .map(|b| {
                    notes.push(format!(
                        "provider {name}: download limit carried over as a prepaid block; \
                         make it monthly if it resets"
                    ));
                    Quota {
                        gb: u32::try_from(b.div_ceil(1_000_000_000)).unwrap_or(u32::MAX),
                        period: QuotaPeriod::Block,
                        reset_day: 1,
                    }
                });
            let priority = match p.kind {
                ProviderKind::Backup => backups
                    .iter()
                    .position(|b| *b == p.priority)
                    .and_then(|i| u32::try_from(i + 1).ok())
                    .unwrap_or(1),
                ProviderKind::Pooled | ProviderKind::Disabled => 0,
            };
            let id = if p.provider_id.is_nil() {
                Uuid::now_v7()
            } else {
                p.provider_id
            };
            if quota.is_some() {
                quota_used.push((id, used(&p)));
            }
            Provider {
                id,
                name,
                profile,
                hostname: p.host,
                port,
                tls: p.use_ssl,
                username: p.user,
                password: Secret(p.pass),
                max_connections: u16::try_from(p.max_connections.clamp(1, i64::from(u16::MAX)))
                    .unwrap_or(defaults.max_connections),
                pipeline_depth: p
                    .pipelining_depth
                    .and_then(|d| u8::try_from(d.clamp(1, 16)).ok())
                    .unwrap_or(defaults.pipeline_depth),
                priority,
                backbone,
                quota,
                enabled: p.kind != ProviderKind::Disabled,
            }
        })
        .collect()
}

fn arr_instances(
    list: Vec<(ArrKind, infinidysk_import::ArrInstance)>,
    notes: &mut Vec<String>,
) -> Vec<ArrInstance> {
    let mut out: Vec<ArrInstance> = Vec::new();
    for (kind, a) in list {
        let (profile, label) = match kind {
            ArrKind::Radarr => (ArrProfile::Radarr, "Radarr"),
            ArrKind::Sonarr => (ArrProfile::Sonarr, "Sonarr"),
            ArrKind::Lidarr => (ArrProfile::Lidarr, "Lidarr"),
            ArrKind::Whisparr => (ArrProfile::Whisparr, "Whisparr"),
        };
        let base = a
            .name
            .filter(|n| !n.trim().is_empty())
            .unwrap_or_else(|| label.to_owned());
        let mut name = base.clone();
        for n in 2.. {
            if !out.iter().any(|o| o.name == name) {
                break;
            }
            name = format!("{base} {n}");
        }
        let url = a.host.trim().trim_end_matches('/').to_owned();
        if !(url.starts_with("http://") || url.starts_with("https://")) || a.api_key.is_empty() {
            notes.push(format!(
                "Arr instance {name}: skipped, it has no http(s) URL or API key"
            ));
            continue;
        }
        out.push(ArrInstance {
            id: Uuid::now_v7(),
            name,
            profile,
            url,
            api_key: Secret(a.api_key),
            enabled: a.enabled,
        });
    }
    out
}

/// The survey of the C# `/config` at `config` plus the settings it would become, as
/// text without secrets or usernames.
pub async fn report(
    config: &Path,
    default_cache_dir: &str,
    progress: impl Fn(infinidysk_import::Progress),
) -> anyhow::Result<String> {
    let vars: Vec<(String, String)> = std::env::vars().collect();
    if !matches!(
        infinidysk_import::source(config, &vars),
        Ok(Source::None | Source::Sqlite)
    ) {
        return Ok(POSTGRES_REPORT.to_owned());
    }
    // A copy left by an earlier PostgreSQL attempt would be read instead of db.sqlite.
    infinidysk_import::postgres::remove_staged(config)?;
    let survey = infinidysk_import::survey_with_progress(config, progress).await?;
    let cs = Config::read(config, std::env::vars()).await?;
    describe(&survey, &cs, &settings(&cs, default_cache_dir)?)
}

/// Config key: `"started"` while an import from C# runs, `"done"` once it finished.
const STATE_KEY: &str = "upgrade.state";
/// Config key: what the import carried over and what it left behind, as text.
pub const REPORT_KEY: &str = "upgrade.report";
/// What `import-report` prints for a 1.x install on PostgreSQL.
const POSTGRES_REPORT: &str = "This 1.x install keeps its data in PostgreSQL. import-report reads only \
    1.x installs on SQLite.\nStart 2.x with DATABASE_PROVIDER and DATABASE_CONNECTION_STRING set as \
    they were for 1.x. It imports the data; the report is then on the System page and in the \
    support pack.\n";
/// Added to the import report when 1.x ran on PostgreSQL and 2.x runs on SQLite.
const POSTGRES_NOTE: &str = "Your 1.x install kept its data in PostgreSQL. This install keeps it in \
    SQLite. To move it to PostgreSQL, open Settings > System > Database and enter a new, empty \
    database; the 1.x database cannot be used for that. The import only read the 1.x database, \
    so 1.x can still use it. DATABASE_PROVIDER and DATABASE_CONNECTION_STRING are no longer \
    needed and can be removed.";
/// Config key, set once the admin has read the import report.
const REPORT_SEEN_KEY: &str = "upgrade.report_seen";
/// Config key, set on installs imported from C#: `/view` still accepts STRM keys in
/// C#'s earlier form, and counts their use ([`LegacyStrm`]).
pub const LEGACY_STRM_KEY: &str = "upgrade.legacy_strm";

/// Use of C#'s earlier STRM key form, `hex(SHA-256(".ids/…_" + key))`, which retires
/// once nothing uses it (`infinidysk-lab:docs/strm-support-proposal.md` §5).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct LegacyStrm {
    pub uses: u64,
    /// Unix milliseconds.
    pub last_used_ms: Option<i64>,
}

/// On an install imported from C#, the counter `/view` bumps for each STRM key in C#'s
/// earlier form, with a task adding its uses to [`LEGACY_STRM_KEY`] every minute.
// ponytail: uses in the last minute before a shutdown are not saved; the count only
// decides when the form retires.
pub async fn legacy_strm(db: &Db) -> Option<Arc<AtomicU64>> {
    let stored = |config: Vec<(String, String)>| {
        config
            .into_iter()
            .find(|(k, _)| k == LEGACY_STRM_KEY)
            .map(|(_, v)| serde_json::from_str::<LegacyStrm>(&v).unwrap_or_default())
    };
    let config = match db.config().await {
        Ok(config) => config,
        Err(error) => {
            tracing::warn!(reason = %error, "reading the C# STRM key state failed");
            return None;
        }
    };
    stored(config)?;
    let uses = Arc::new(AtomicU64::new(0));
    let (counter, db) = (Arc::clone(&uses), db.clone());
    tokio::spawn(async move {
        let mut tick = tokio::time::interval(std::time::Duration::from_secs(60));
        loop {
            tick.tick().await;
            let n = counter.swap(0, Ordering::Relaxed);
            if n == 0 {
                continue;
            }
            let writing = db.write_permit().await;
            let saved = async {
                let mut state = stored(db.config().await?).unwrap_or_default();
                state.uses += n;
                state.last_used_ms = Some(infinidysk_db::now_ms());
                let value = serde_json::to_string(&state).unwrap_or_default();
                db.write_config(&[(LEGACY_STRM_KEY, &value)], &[]).await
            };
            if let Err(error) = saved.await {
                counter.fetch_add(n, Ordering::Relaxed);
                tracing::warn!(reason = %error, "could not save C# STRM key use");
            }
            drop(writing);
        }
    });
    Some(uses)
}

/// Imports the C# install whose `/config` is `config` when this database is new, or
/// finishes an import a restart interrupted.
pub async fn import(db: &Db, config: &Path) -> anyhow::Result<()> {
    let vars: Vec<(String, String)> = std::env::vars().collect();
    import_with_progress(db, config, &vars, crate::config::DEFAULT_CACHE_DIR, |_| {}).await
}

/// Whether an import runs. An interrupted import always resumes, so a source that
/// disappeared shows its reason instead of a half-imported start. A 1.x install whose
/// source cannot be read counts too, so its reason shows on the upgrade screen.
async fn import_needed(db: &Db, config: &Path, vars: &[(String, String)]) -> anyhow::Result<bool> {
    let state = db
        .config()
        .await?
        .into_iter()
        .find_map(|(k, v)| (k == STATE_KEY).then_some(v));
    match state.as_deref() {
        Some("\"done\"") => Ok(false),
        Some(_) => Ok(true),
        None if matches!(infinidysk_import::source(config, vars), Ok(Source::None)) => Ok(false),
        None => Ok(db.is_fresh().await?),
    }
}

async fn import_with_progress(
    db: &Db,
    config: &Path,
    vars: &[(String, String)],
    default_cache_dir: &str,
    progress: impl Fn(infinidysk_import::Progress) + Sync,
) -> anyhow::Result<()> {
    if !import_needed(db, config, vars).await? {
        return Ok(());
    }
    tracing::info!(config = %config.display(), "importing or resuming the C# install");
    let from_postgres = match infinidysk_import::source(config, vars)? {
        Source::Postgres(connection) => {
            let copied = Box::pin(copy_v1_postgres(
                config,
                &connection,
                vars,
                crate::database::LIMITS.stall,
                &progress,
            ))
            .await;
            if let Err(error) = copied {
                if let Err(error) = infinidysk_import::postgres::remove_staged(config) {
                    tracing::warn!(reason = %error, "could not remove a partial copy of the 1.x PostgreSQL database");
                }
                return Err(error);
            }
            true
        }
        Source::Sqlite | Source::None => {
            infinidysk_import::postgres::remove_staged(config)?;
            false
        }
    };
    let survey = infinidysk_import::survey_with_progress(config, &progress).await?;
    anyhow::ensure!(
        survey.supported(),
        "the C# install is too old to import; update it to v1.3.0 or later, \
         start it once, then switch"
    );
    let cs = Config::read(config, vars.iter().cloned()).await?;
    let mut upgrade = settings(&cs, default_cache_dir)?;
    if from_postgres && !db.is_postgres() {
        upgrade.notes.push(POSTGRES_NOTE.to_owned());
    }
    db.write_config(&[(STATE_KEY, "\"started\"")], &[]).await?;

    let quotas = Quotas::load(db.clone()).await?;
    quotas.configure(&upgrade.settings.providers);
    quotas.seed(&upgrade.quota_used).await;
    let client = Client::new(crate::app::providers(
        &upgrade.settings.providers,
        &quotas,
        &Arc::new(Bandwidth::new(crate::app::download_limit(
            &upgrade.settings,
        ))),
    )?);
    let imports = &upgrade.settings.imports;
    let storage_root = match imports.strategy {
        ImportStrategy::Strm => imports.strm_dir.clone(),
        ImportStrategy::Symlinks => format!("{}/completed-symlinks", upgrade.settings.mount.dir),
    };
    let imported =
        infinidysk_import::apply_with_progress(config, db, &client, &storage_root, progress)
            .await?;
    drop(client);

    let mut store =
        ConfigStore::load_in(db.clone(), std::iter::empty(), default_cache_dir.to_owned()).await?;
    if let Err(error) = store.save(upgrade.settings.clone()).await {
        // Defaults without an admin account would open setup to anyone.
        anyhow::ensure!(
            !upgrade.settings.auth.oidc.enabled() || cs.admin.is_some(),
            "saving the settings carried over from 1.x failed ({error}); without them \
             single sign-on would be off and 1.x had no local admin account"
        );
        upgrade.notes.push(format!(
            "settings not carried over ({error}); set them up again"
        ));
    }
    store.generate_missing_secrets().await?;
    if let Some((user, hash)) = &cs.admin {
        db.create_admin(user, hash).await?;
    }
    let legacy = upgrade.webdav_hash.clone().map(|hash| LegacyWebDav {
        hash,
        replaced: store.settings().webdav.password.0.clone(),
    });
    let mut text = describe(&survey, &cs, &upgrade)?;
    text.push_str(&imported.to_string());
    let mut set = vec![
        (STATE_KEY, "\"done\"".to_owned()),
        (REPORT_KEY, serde_json::to_string(&text)?),
        (
            LEGACY_STRM_KEY,
            serde_json::to_string(&LegacyStrm::default())?,
        ),
        (DOWNLOAD_IDS_KEY, "true".to_owned()),
        (UNOWNED_KEPT_KEY, "true".to_owned()),
        (SKIPPED_RETRIED_KEY, "true".to_owned()),
    ];
    if let Some(legacy) = legacy {
        set.push((WEBDAV_KEY, serde_json::to_string(&legacy)?));
    }
    let set: Vec<(&str, &str)> = set.iter().map(|(k, v)| (*k, v.as_str())).collect();
    // Before "done": a later import never runs again to remove it.
    infinidysk_import::postgres::remove_staged(config)?;
    db.write_config(&set, &[]).await?;
    tracing::info!(
        files = imported.files,
        skipped = imported.skipped_count,
        history = imported.history,
        "imported the C# install"
    );
    Ok(())
}

/// Copies the 1.x PostgreSQL tables; fails when no progress is reported for `stall`.
async fn copy_v1_postgres(
    config: &Path,
    connection: &str,
    vars: &[(String, String)],
    stall: Duration,
    progress: &(impl Fn(infinidysk_import::Progress) + Sync),
) -> anyhow::Result<()> {
    let env = |name: &str| vars.iter().find(|(k, _)| k == name).map(|(_, v)| v.clone());
    let beat = crate::database::Beat::new();
    let report = |p| {
        beat.touch();
        progress(p);
    };
    let copy = infinidysk_import::postgres::stage(config, connection, &env, &report);
    crate::database::watchdog(stall, &beat, async { Ok(copy.await?) }).await
}

/// Config key set once a finished C# import has had its folders' downloads stamped.
const DOWNLOAD_IDS_KEY: &str = "upgrade.download_ids";

/// Stamps the downloads of C# folders on installs imported before the importer did it.
// ponytail: remove before beta 1; every alpha install has run it by then.
pub async fn stamp_migrated(db: &Db, config: &Path) {
    let stored = match db.config().await {
        Ok(stored) => stored,
        Err(error) => {
            tracing::warn!(reason = %error, "could not read the C# import state");
            return;
        }
    };
    let done = stored
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"");
    if !done || stored.iter().any(|(k, _)| k == DOWNLOAD_IDS_KEY) {
        return;
    }
    if config.join("db.sqlite").is_file() {
        match infinidysk_import::stamp_download_ids(config, db).await {
            Ok(n) => tracing::info!(
                folders = n,
                "found the downloads of C# folders without history"
            ),
            Err(error) => {
                tracing::warn!(reason = %error, "could not find the downloads of C# folders");
                return;
            }
        }
    } else {
        tracing::warn!(
            reason = "the C# database is gone",
            "could not find the downloads of C# folders; broken ones stay unowned"
        );
    }
    if let Err(error) = db.write_config(&[(DOWNLOAD_IDS_KEY, "true")], &[]).await {
        tracing::warn!(reason = %error, "could not record the C# folder downloads");
    }
}

/// Config key set once a finished C# import keeps broken unowned files for review.
const UNOWNED_KEPT_KEY: &str = "upgrade.unowned_kept";

/// Keeps broken files no Arr imported in Needs attention on installs imported from C#
/// while the default was to delete them.
// ponytail: remove before beta 1; every alpha install has run it by then.
pub async fn keep_unowned_migrated(db: &Db, store: &mut ConfigStore) {
    let stored = match db.config().await {
        Ok(stored) => stored,
        Err(error) => {
            tracing::warn!(reason = %error, "could not read the C# import state");
            return;
        }
    };
    let done = stored
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"");
    if !done || stored.iter().any(|(k, _)| k == UNOWNED_KEPT_KEY) {
        return;
    }
    let mut next = store.settings().clone();
    next.health.unowned_action = UnownedAction::ActionNeeded;
    match store.save(next).await {
        Ok(()) => tracing::info!("broken files not imported by an Arr now stay in Needs attention"),
        Err(error @ crate::config::Error::Locked(_)) => {
            tracing::warn!(reason = %error, "kept the configured action for broken unowned files");
        }
        Err(error) => {
            tracing::warn!(reason = %error, "could not keep broken unowned files for review");
            return;
        }
    }
    if let Err(error) = db.write_config(&[(UNOWNED_KEPT_KEY, "true")], &[]).await {
        tracing::warn!(reason = %error, "could not record the broken-file setting change");
    }
}

/// Config key set once files an earlier C# import skipped were retried. Bumped when the
/// importer learns to carry over files it used to skip, so installs retry once more.
const SKIPPED_RETRIED_KEY: &str = "upgrade.skipped_retried.5";

/// Whether files an earlier C# import skipped may still be brought back.
pub(crate) async fn skipped_pending(db: &Db) -> Result<bool, infinidysk_db::Error> {
    let stored = db.config().await?;
    Ok(stored
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"")
        && !stored.iter().any(|(k, _)| k == SKIPPED_RETRIED_KEY))
}

/// Brings back files an earlier C# import skipped that a library symlink or STRM file
/// still points at, now that the importer carries more over. Runs once, in the
/// background; a run cut short, without a readable library or with provider errors
/// runs again on the next start.
// ponytail: remove before beta 1; every alpha install has run it by then. Files written
// by a run that hit provider errors are left out of the import report.
pub async fn retry_skipped(
    db: Db,
    config: PathBuf,
    client: Arc<Client>,
    settings: Settings,
    cancel: CancellationToken,
) {
    let stored = match db.config().await {
        Ok(stored) => stored,
        Err(error) => {
            tracing::warn!(reason = %error, "could not read the C# import state");
            return;
        }
    };
    let done = stored
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"");
    if !done || stored.iter().any(|(k, _)| k == SKIPPED_RETRIED_KEY) {
        return;
    }
    let report: String = stored
        .iter()
        .find(|(k, _)| k == REPORT_KEY)
        .and_then(|(_, v)| serde_json::from_str(v).ok())
        .unwrap_or_default();
    let mut set = vec![(SKIPPED_RETRIED_KEY, "true".to_owned())];
    if config.join("db.sqlite").is_file() {
        match infinidysk_import::restore_nzb_downloads(&config, &db).await {
            Ok(restored) => tracing::info!(restored, "restored missing C# NZB download links"),
            Err(error) => {
                tracing::warn!(reason = %error, "could not restore C# NZB download links");
                return;
            }
        }
        let roots: Vec<PathBuf> = settings
            .imports
            .library_dirs
            .iter()
            .map(|d| PathBuf::from(&d.path))
            .collect();
        if roots.is_empty() {
            return;
        }
        let mounts = [PathBuf::from(&settings.mount.dir)];
        let walking = cancel.clone();
        let read = tokio::task::spawn_blocking(move || {
            crate::maintenance::read_folders(&roots, Path::new(""), &mounts, &walking, &|_| {})
        })
        .await;
        let pointers = match read {
            Ok(Ok(pointers)) => pointers,
            Ok(Err(crate::maintenance::Stop::Reason(reason))) => {
                tracing::warn!(%reason, "could not read the library to bring back files the C# import skipped");
                return;
            }
            Ok(Err(_)) => return,
            Err(error) => {
                tracing::warn!(reason = %error, "could not read the library to bring back files the C# import skipped");
                return;
            }
        };
        let retried = tokio::select! {
            () = cancel.cancelled() => return,
            r = infinidysk_import::retry_files(&config, &db, &client, |id, path| {
                pointers.keep(ItemId(id), path)
            }) => r,
        };
        let imported = match retried {
            Ok(imported) => imported,
            Err(error) => {
                tracing::warn!(reason = %error, "could not bring back files the C# import skipped");
                return;
            }
        };
        tracing::info!(
            files = imported.files,
            skipped = imported.skipped_count,
            "brought back files the C# import skipped that the library links to"
        );
        if imported.provider_errors > 0 {
            tracing::warn!(
                errors = imported.provider_errors,
                "providers failed while bringing back files the C# import skipped; \
                 trying again on the next start"
            );
            return;
        }
        let text = format!("{report}\nFiles the library links to, retried:\n{imported}");
        set.push((REPORT_KEY, serde_json::to_string(&text).unwrap_or_default()));
    } else {
        tracing::warn!(
            reason = "the C# database is gone",
            "could not bring back files the C# import skipped"
        );
    }
    let set: Vec<(&str, &str)> = set.iter().map(|(k, v)| (*k, v.as_str())).collect();
    if let Err(error) = db.write_config(&set, &[]).await {
        tracing::warn!(reason = %error, "could not record the retry of skipped C# files");
    }
}

#[derive(Clone, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum UpgradeState {
    /// Not imported from C#.
    None,
    Importing,
    Done,
    /// Nothing but the UI is served until a retry succeeds.
    Failed,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct UpgradeStatus {
    pub state: UpgradeState,
    /// Why the import from C# failed.
    pub error: Option<String>,
    /// When that attempt failed, Unix milliseconds.
    pub failed_at_ms: Option<i64>,
    pub progress: Option<UpgradeProgress>,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum UpgradeStage {
    /// Copying the 1.x tables from PostgreSQL.
    Copying,
    /// Reading the C# database and every file's metadata.
    Checking,
    Directories,
    Nzbs,
    Files,
    /// History, queue and settings.
    Finishing,
}

impl From<infinidysk_import::Stage> for UpgradeStage {
    fn from(stage: infinidysk_import::Stage) -> Self {
        use infinidysk_import::Stage;
        match stage {
            Stage::Copying => Self::Copying,
            Stage::Checking => Self::Checking,
            Stage::Directories => Self::Directories,
            Stage::Nzbs => Self::Nzbs,
            Stage::Files => Self::Files,
            Stage::Finishing => Self::Finishing,
        }
    }
}

#[derive(Clone, Serialize, ToSchema)]
pub struct UpgradeProgress {
    pub stage: UpgradeStage,
    /// Items this stage has finished; in `files`, files written.
    pub done: u64,
    /// Items this stage has to do in this attempt, excluding earlier attempts' work;
    /// absent while counting.
    pub total: Option<u64>,
    /// Files not carried over in `files`.
    pub skipped: u64,
    /// Estimated seconds left in this stage; absent until measurable.
    pub eta_seconds: Option<u64>,
}

impl UpgradeProgress {
    /// `elapsed` is the time since this stage began.
    fn new(progress: infinidysk_import::Progress, elapsed: Duration) -> Self {
        let completed = progress.done.saturating_add(progress.skipped);
        let remaining = progress
            .total
            .map_or(0, |total| total.saturating_sub(completed));
        let eta_seconds = if completed > 0 && remaining > 0 {
            elapsed
                .as_millis()
                .checked_mul(u128::from(remaining))
                .map(|millis| millis.div_ceil(u128::from(completed) * 1000))
                .and_then(|seconds| u64::try_from(seconds).ok())
                .map(|seconds| seconds.max(1))
        } else {
            None
        };
        Self {
            stage: progress.stage.into(),
            done: progress.done,
            total: progress.total,
            skipped: progress.skipped,
            eta_seconds,
        }
    }
}

#[derive(Default)]
struct ProgressClock {
    stage: Option<infinidysk_import::Stage>,
    started: Option<Instant>,
}

impl ProgressClock {
    fn elapsed(&mut self, progress: infinidysk_import::Progress, now: Instant) -> Duration {
        if self.stage != Some(progress.stage) || progress.total.is_none() {
            self.stage = Some(progress.stage);
            self.started = None;
        }
        if progress.total.is_none() {
            return Duration::ZERO;
        }
        now.duration_since(*self.started.get_or_insert(now))
    }
}

impl UpgradeStatus {
    fn importing() -> Self {
        Self {
            state: UpgradeState::Importing,
            error: None,
            failed_at_ms: None,
            progress: None,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct ImportReport {
    /// What the import from C# carried over and left behind; absent on other installs.
    pub report: Option<String>,
    pub seen: bool,
}

/// Whether this install was imported from C#, or its import failed. No sign-in needed.
#[utoipa::path(get, path = "/api/v1/upgrade/status", tag = "upgrade", security(()),
    responses((status = 200, body = UpgradeStatus)))]
pub(crate) async fn get_upgrade_status(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<UpgradeStatus>, Fail> {
    let done = admin
        .queue
        .db()
        .config()
        .await?
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"");
    Ok(Json(UpgradeStatus {
        state: if done {
            UpgradeState::Done
        } else {
            UpgradeState::None
        },
        error: None,
        failed_at_ms: None,
        progress: None,
    }))
}

#[utoipa::path(get, path = "/api/v1/upgrade/report", tag = "upgrade",
    responses((status = 200, body = ImportReport)))]
pub(crate) async fn get_upgrade_report(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<ImportReport>, Fail> {
    let config = admin.queue.db().config().await?;
    let get = |key: &str| {
        config
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v.as_str())
    };
    Ok(Json(ImportReport {
        report: get(REPORT_KEY).and_then(|v| serde_json::from_str(v).ok()),
        seen: get(REPORT_SEEN_KEY).is_some(),
    }))
}

#[utoipa::path(post, path = "/api/v1/upgrade/report/seen", tag = "upgrade",
    responses((status = 204)))]
pub(crate) async fn post_upgrade_report_seen(
    State(admin): State<Arc<Admin>>,
) -> Result<StatusCode, Fail> {
    admin
        .queue
        .db()
        .write_config(&[(REPORT_SEEN_KEY, "true")], &[])
        .await?;
    Ok(StatusCode::NO_CONTENT)
}

/// Runs the import again; only while it has failed. No sign-in needed: the install has
/// no accounts yet.
#[utoipa::path(post, path = "/api/v1/upgrade/retry", tag = "upgrade", security(()),
    responses((status = 202), (status = 503)))]
pub(crate) async fn post_upgrade_retry(State(server): State<Arc<UpgradeServer>>) -> Response {
    let accepted = server.status.send_if_modified(|status| {
        if matches!(status.state, UpgradeState::Failed) {
            *status = UpgradeStatus::importing();
            true
        } else {
            false
        }
    });
    if accepted {
        server.retry.notify_one();
        StatusCode::ACCEPTED.into_response()
    } else {
        upgrade_unavailable()
    }
}

pub(crate) struct UpgradeServer {
    status: watch::Sender<UpgradeStatus>,
    retry: Notify,
}

fn upgrade_unavailable() -> Response {
    (
        StatusCode::SERVICE_UNAVAILABLE,
        [(header::RETRY_AFTER, "60")],
        Json(serde_json::json!({
            "error": "The C# import is in progress or awaiting retry; open the web UI for its status."
        })),
    )
        .into_response()
}

fn upgrade_router(server: Arc<UpgradeServer>) -> Router {
    Router::new()
        .route(
            "/api/v1/upgrade/status",
            get(|State(server): State<Arc<UpgradeServer>>| async move {
                (
                    [(header::CACHE_CONTROL, "no-store")],
                    Json(server.status.borrow().clone()),
                )
            }),
        )
        .route("/api/v1/upgrade/retry", post(post_upgrade_retry))
        .route(
            "/api/v1/database/cutover",
            get(crate::database::get_cutover_none),
        )
        .route(
            crate::healthcheck::PATH,
            get(|State(server): State<Arc<UpgradeServer>>| async move {
                use crate::healthcheck::{Check, Report};
                let failed = matches!(server.status.borrow().state, UpgradeState::Failed);
                Report::new([("import", if failed { Check::Fail } else { Check::Pending })])
            }),
        )
        .fallback(|| async { upgrade_unavailable() })
        .with_state(server)
        .layer(axum::middleware::from_fn(crate::ui::serve))
}

/// Serves import progress and retries until the upgrade finishes or `cancel` fires.
/// The SAB API and `WebDAV` answer 503 until the returned listener serves the full app.
pub async fn serve_import(
    listener: TcpListener,
    db: &Db,
    config: &Path,
    default_cache_dir: &str,
    cancel: &CancellationToken,
) -> anyhow::Result<TcpListener> {
    let vars: Vec<(String, String)> = std::env::vars().collect();
    if matches!(import_needed(db, config, &vars).await, Ok(false)) {
        return Ok(listener);
    }
    let listener = listener.into_std()?;
    let next = listener.try_clone()?;
    let server = Arc::new(UpgradeServer {
        status: watch::channel(UpgradeStatus::importing()).0,
        retry: Notify::new(),
    });
    let stop = CancellationToken::new();
    let serving = axum::serve(
        TcpListener::from_std(listener)?,
        upgrade_router(Arc::clone(&server)),
    )
    .with_graceful_shutdown(stop.clone().cancelled_owned())
    .into_future();
    tokio::pin!(serving);
    let importing = async {
        loop {
            let clock = std::sync::Mutex::new(ProgressClock::default());
            let result = import_with_progress(db, config, &vars, default_cache_dir, |progress| {
                let elapsed = clock
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .elapsed(progress, Instant::now());
                server.status.send_modify(|status| {
                    status.progress = Some(UpgradeProgress::new(progress, elapsed));
                });
            })
            .await;
            match result {
                Ok(()) => break,
                Err(error) => {
                    let reason = format!("{error:#}");
                    tracing::warn!(%reason, "importing the C# install failed; the web UI offers a retry");
                    server.status.send_modify(|status| {
                        status.state = UpgradeState::Failed;
                        status.error = Some(reason);
                        status.failed_at_ms = Some(infinidysk_db::now_ms());
                    });
                    server.retry.notified().await;
                    tracing::info!("retrying the import from C#");
                }
            }
        }
    };
    tokio::select! {
        () = cancel.cancelled() => {},
        () = importing => {},
        result = &mut serving => {
            result?;
            anyhow::bail!("the upgrade HTTP server stopped unexpectedly");
        }
    }
    stop.cancel();
    serving.await?;
    Ok(TcpListener::from_std(next)?)
}

fn describe(
    survey: &infinidysk_import::Survey,
    cs: &Config,
    upgrade: &Upgrade,
) -> anyhow::Result<String> {
    let mut out = survey.to_string();
    let Upgrade {
        settings: s,
        webdav_hash,
        quota_used,
        notes,
    } = upgrade;
    let on = |b: bool| if b { "on" } else { "off" };
    let kept = |secret: &Secret| if secret.0.is_empty() { "new" } else { "kept" };
    let webdav_password = match (&webdav_hash, s.webdav.password.0.is_empty()) {
        (Some(_), _) => "accepted from C# at the first sign-in",
        (None, false) => "kept",
        (None, true) => "new",
    };
    writeln!(out, "Settings:")?;
    writeln!(
        out,
        "  admin login: {}",
        if cs.admin.is_some() {
            "accepted from C# at the first sign-in"
        } else if s.auth.oidc.enabled() {
            "none; sign in with SSO"
        } else {
            "created at first visit"
        }
    )?;
    writeln!(out, "  SAB API key {}", kept(&s.sab.api_key))?;
    writeln!(
        out,
        "  categories: {} (manual: {})",
        s.sab.categories.join(", "),
        s.sab.manual_category
    )?;
    writeln!(out, "  mount dir: {}", s.mount.dir)?;
    writeln!(out, "  public URL: {}", s.network.public_url)?;
    writeln!(
        out,
        "  single sign-on: {}",
        if s.auth.oidc.enabled() {
            s.auth.oidc.issuer.as_str()
        } else {
            "off"
        }
    )?;
    writeln!(
        out,
        "  import strategy {:?}; STRM directory {}; STRM key {}; library folders: {}",
        s.imports.strategy,
        s.imports.strm_dir,
        kept(&s.imports.strm_key),
        s.imports
            .library_dirs
            .iter()
            .map(|d| d.path.as_str())
            .collect::<Vec<_>>()
            .join(", ")
    )?;
    writeln!(
        out,
        "  WebDAV user {}, password {webdav_password}",
        s.webdav.user,
    )?;
    writeln!(
        out,
        "  imports at once: {}; extra blocklist: {}",
        s.queue.concurrency,
        s.queue.extra_blocklist.join(", ")
    )?;
    writeln!(
        out,
        "  health checks {}, degraded tolerance {}",
        on(s.health.enabled),
        on(s.health.degraded_tolerance)
    )?;
    let csharp_limit = cs
        .get(CSHARP_DOWNLOAD_LIMIT)
        .and_then(|v| v.trim().parse::<f64>().ok())
        .map_or_else(String::new, |mbps| format!(" ({} MB/s in C#)", mbps / 8.0));
    match s.network.download_limit_mb_per_s {
        Some(mb) => writeln!(out, "  download limit: {mb} MB/s{csharp_limit}")?,
        None => writeln!(out, "  download limit: none")?,
    }
    for p in &s.providers {
        let source = p.profile.as_deref().map_or_else(
            || format!("custom {}:{}", p.hostname, p.port),
            |id| format!("catalog {id}"),
        );
        let quota = p.quota.map_or_else(String::new, |q| {
            let used = quota_used
                .iter()
                .find(|(id, _)| *id == p.id)
                .map_or(0, |(_, b)| *b);
            format!(
                ", {} GB block, {} GB used",
                q.gb,
                used.div_ceil(1_000_000_000)
            )
        });
        writeln!(
            out,
            "  provider {}: {source}, backbone {}, level {}, {} connections{quota}{}",
            p.name,
            p.backbone.as_deref().unwrap_or("own"),
            p.priority,
            p.max_connections,
            if p.enabled { "" } else { ", disabled" }
        )?;
    }
    for a in &s.arr_instances {
        writeln!(
            out,
            "  Arr {}: {:?} at {}{}",
            a.name,
            a.profile,
            a.url,
            if a.enabled { "" } else { ", disabled" }
        )?;
    }
    for note in notes {
        writeln!(out, "Note: {note}")?;
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn upgrade_clock_excludes_preparation_and_resets_between_stages_and_attempts() {
        use infinidysk_import::{Progress, Stage};
        let mut clock = ProgressClock::default();
        let mut now = Instant::now();
        for stage in [
            Stage::Checking,
            Stage::Directories,
            Stage::Nzbs,
            Stage::Files,
        ] {
            let mut progress = Progress {
                stage,
                ..Progress::default()
            };
            assert_eq!(clock.elapsed(progress, now), Duration::ZERO);
            now += Duration::from_secs(60);
            progress.total = Some(100);
            assert_eq!(clock.elapsed(progress, now), Duration::ZERO);
            now += Duration::from_secs(5);
            assert_eq!(clock.elapsed(progress, now), Duration::from_secs(5));
            now += Duration::from_secs(5);
            progress.done = 50;
            assert_eq!(
                UpgradeProgress::new(progress, clock.elapsed(progress, now)).eta_seconds,
                Some(10)
            );
        }
        let progress = Progress {
            stage: Stage::Files,
            total: Some(100),
            ..Progress::default()
        };
        let mut retry = ProgressClock::default();
        assert_eq!(retry.elapsed(progress, now), Duration::ZERO);
        assert_eq!(
            retry.elapsed(progress, now + Duration::from_secs(5)),
            Duration::from_secs(5)
        );
    }

    #[test]
    fn upgrade_stages_have_stable_wire_names() {
        use infinidysk_import::Stage;
        for (stage, name) in [
            (Stage::Copying, "copying"),
            (Stage::Checking, "checking"),
            (Stage::Directories, "directories"),
            (Stage::Nzbs, "nzbs"),
            (Stage::Files, "files"),
            (Stage::Finishing, "finishing"),
        ] {
            assert_eq!(
                serde_json::to_value(UpgradeStage::from(stage)).unwrap(),
                name
            );
        }
    }

    #[test]
    fn upgrade_eta_counts_skips_and_waits_for_measurable_work() {
        use infinidysk_import::{Progress, Stage};
        let elapsed = Duration::from_secs(10);
        let files = |done, total, skipped| Progress {
            stage: Stage::Files,
            done,
            total,
            skipped,
        };
        for progress in [
            Progress::default(),
            files(0, Some(100), 0),
            files(90, Some(100), 10),
            files(40, None, 0),
        ] {
            assert_eq!(UpgradeProgress::new(progress, elapsed).eta_seconds, None);
        }
        assert_eq!(
            UpgradeProgress::new(files(40, Some(100), 10), elapsed).eta_seconds,
            Some(10)
        );
    }

    #[tokio::test]
    async fn active_upgrade_serves_progress_and_keeps_clients_busy() {
        use axum::body::Body;
        use http_body_util::BodyExt as _;
        use tower::ServiceExt as _;

        let server = Arc::new(UpgradeServer {
            status: watch::channel(UpgradeStatus::importing()).0,
            retry: Notify::new(),
        });
        let app = upgrade_router(Arc::clone(&server));
        for (method, path) in [
            ("GET", "/api?mode=queue"),
            ("POST", "/api?mode=addfile"),
            ("PROPFIND", "/"),
            ("GET", "/view/file.mkv"),
            ("HEAD", "/content/file.mkv"),
            ("GET", "/.ids/file.mkv"),
            ("PUT", "/nzbs/file.nzb"),
            ("GET", "/completed-symlinks/file.mkv"),
            ("GET", "/api/v1/auth"),
            ("POST", "/api/v1/upgrade/retry"),
        ] {
            let response = app
                .clone()
                .oneshot(
                    Request::builder()
                        .method(method)
                        .uri(path)
                        .body(Body::empty())
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(
                response.status(),
                StatusCode::SERVICE_UNAVAILABLE,
                "{method} {path}"
            );
            assert_eq!(response.headers()[header::RETRY_AFTER], "60");
        }
        for path in ["/", "/overview", "/mark.svg"] {
            let response = app
                .clone()
                .oneshot(Request::builder().uri(path).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK, "{path}");
        }
        server.status.send_modify(|status| {
            status.progress = Some(UpgradeProgress::new(
                infinidysk_import::Progress {
                    stage: infinidysk_import::Stage::Files,
                    done: 40,
                    total: Some(100),
                    skipped: 10,
                },
                Duration::from_secs(10),
            ));
        });
        let response = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri("/api/v1/upgrade/status")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.headers()[header::CACHE_CONTROL], "no-store");
        let body = response.into_body().collect().await.unwrap().to_bytes();
        let status: serde_json::Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(status["state"], "importing");
        assert_eq!(
            status["progress"],
            serde_json::json!({
                "stage": "files", "done": 40, "total": 100, "skipped": 10, "eta_seconds": 10,
            })
        );
        // Docker counts the import as starting; consumers wait for it.
        let healthz = || {
            let app = app.clone();
            async move {
                let response = app
                    .oneshot(
                        Request::builder()
                            .uri("/healthz")
                            .body(Body::empty())
                            .unwrap(),
                    )
                    .await
                    .unwrap();
                assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
                let body = response.into_body().collect().await.unwrap().to_bytes();
                serde_json::from_slice::<serde_json::Value>(&body).unwrap()
            }
        };
        assert_eq!(
            healthz().await,
            serde_json::json!({"status": "starting", "checks": {"import": "pending"}})
        );
        server
            .status
            .send_modify(|status| status.state = UpgradeState::Failed);
        assert_eq!(
            healthz().await,
            serde_json::json!({"status": "unhealthy", "checks": {"import": "fail"}})
        );
        let response = app
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/api/v1/upgrade/retry")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::ACCEPTED);
        assert!(matches!(
            server.status.borrow().state,
            UpgradeState::Importing
        ));
        assert!(server.status.borrow().progress.is_none());
        tokio::time::timeout(Duration::from_secs(1), server.retry.notified())
            .await
            .unwrap();
    }

    #[tokio::test]
    async fn import_router_reports_no_cutover() {
        use axum::body::Body;
        use http_body_util::BodyExt as _;
        use tower::ServiceExt as _;

        let server = Arc::new(UpgradeServer {
            status: watch::channel(UpgradeStatus::importing()).0,
            retry: Notify::new(),
        });
        let app = upgrade_router(Arc::clone(&server));
        for state in [UpgradeState::Importing, UpgradeState::Failed] {
            server.status.send_modify(|status| status.state = state);
            let response = app
                .clone()
                .oneshot(
                    Request::builder()
                        .uri("/api/v1/database/cutover")
                        .body(Body::empty())
                        .unwrap(),
                )
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let body = response.into_body().collect().await.unwrap().to_bytes();
            let status: serde_json::Value = serde_json::from_slice(&body).unwrap();
            assert_eq!(status["state"], "none");
        }
    }

    #[tokio::test]
    async fn skipped_files_are_retried_after_the_previous_pass() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        assert!(!skipped_pending(&db).await.unwrap());
        db.write_config(
            &[
                (STATE_KEY, "\"done\""),
                ("upgrade.skipped_retried.4", "true"),
            ],
            &[],
        )
        .await
        .unwrap();
        assert!(skipped_pending(&db).await.unwrap());
        db.write_config(&[(SKIPPED_RETRIED_KEY, "true")], &[])
            .await
            .unwrap();
        assert!(!skipped_pending(&db).await.unwrap());
        db.close().await;
    }

    fn v1_dir() -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("infinidysk-v1pg-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn vars(pairs: &[(&str, &str)]) -> Vec<(String, String)> {
        pairs
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect()
    }

    #[tokio::test]
    async fn an_interrupted_import_resumes_after_its_source_is_gone() {
        let dir = v1_dir();
        let db = Db::connect("sqlite::memory:").await.unwrap();
        db.write_config(&[(STATE_KEY, "\"started\"")], &[])
            .await
            .unwrap();
        assert!(import_needed(&db, &dir, &[]).await.unwrap());
        assert!(
            import_with_progress(&db, &dir, &[], crate::config::DEFAULT_CACHE_DIR, |_| {})
                .await
                .is_err()
        );
        db.close().await;
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_postgres_install_is_not_imported_as_empty() {
        let dir = v1_dir();
        std::fs::write(dir.join("db-contract.json"), r#"{"provider":"postgres"}"#).unwrap();
        let db = Db::connect("sqlite::memory:").await.unwrap();
        assert!(import_needed(&db, &dir, &[]).await.unwrap());
        let error = import_with_progress(&db, &dir, &[], crate::config::DEFAULT_CACHE_DIR, |_| {})
            .await
            .unwrap_err();
        assert!(
            format!("{error:#}").contains("DATABASE_CONNECTION_STRING"),
            "{error:#}"
        );

        let vars = vars(&[
            ("DATABASE_PROVIDER", "postgres"),
            (
                "DATABASE_CONNECTION_STRING",
                "Host=127.0.0.1;Port=1;Username=u;Password=hunter2",
            ),
        ]);
        let error =
            import_with_progress(&db, &dir, &vars, crate::config::DEFAULT_CACHE_DIR, |_| {})
                .await
                .unwrap_err();
        let error = format!("{error:#}");
        assert!(error.contains("PostgreSQL"), "{error}");
        assert!(!error.contains("hunter2"), "{error}");
        assert!(db.is_fresh().await.unwrap());
        assert!(!dir.join(infinidysk_import::postgres::STAGED).exists());
        db.close().await;
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn import_report_points_postgres_installs_to_the_app() {
        let dir = v1_dir();
        std::fs::write(dir.join("db-contract.json"), r#"{"provider":"postgres"}"#).unwrap();
        std::fs::write(dir.join("db.sqlite"), "not a database").unwrap();
        assert_eq!(
            report(&dir, crate::config::DEFAULT_CACHE_DIR, |_| {})
                .await
                .unwrap(),
            POSTGRES_REPORT
        );
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_sqlite_attempt_removes_a_leftover_copy() {
        let dir = v1_dir();
        std::fs::write(dir.join("db.sqlite"), "not a database").unwrap();
        let staged = dir.join(infinidysk_import::postgres::STAGED);
        std::fs::write(&staged, "left over").unwrap();
        let db = Db::connect("sqlite::memory:").await.unwrap();
        import_with_progress(&db, &dir, &[], crate::config::DEFAULT_CACHE_DIR, |_| {})
            .await
            .unwrap_err();
        assert!(!staged.exists());
        db.close().await;
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_silent_1x_server_fails_the_copy() {
        use tokio::io::{AsyncReadExt as _, AsyncWriteExt as _};
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let len = socket.read_u32().await.unwrap();
            let mut startup = vec![0; len as usize - 4];
            socket.read_exact(&mut startup).await.unwrap();
            let mut reply = Vec::new();
            reply.extend_from_slice(b"R\0\0\0\x08\0\0\0\0");
            reply.extend_from_slice(b"K\0\0\0\x0c\0\0\0\x01\0\0\0\x02");
            reply.extend_from_slice(b"Z\0\0\0\x05I");
            socket.write_all(&reply).await.unwrap();
            let mut buf = [0; 1024];
            while socket.read(&mut buf).await.is_ok_and(|n| n > 0) {}
        });
        let dir = v1_dir();
        let connection = format!("Host=127.0.0.1;Port={port};Username=u;SSL Mode=Disable");
        let copy = copy_v1_postgres(&dir, &connection, &[], Duration::from_secs(1), &|_| {});
        let error = tokio::time::timeout(Duration::from_secs(5), copy)
            .await
            .expect("the watchdog ends the copy")
            .unwrap_err();
        assert!(
            error.to_string().contains("stopped responding"),
            "{error:#}"
        );
        tokio::time::timeout(Duration::from_secs(5), server)
            .await
            .expect("the connection is closed")
            .unwrap();
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_postgres_install_upgrades_and_drops_its_copy() {
        const FIXTURE: &str = include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../import/tests/fixtures/v1-postgres.sql"
        ));
        let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
            return;
        };
        let name = format!("infinidysk_test_{}", uuid::Uuid::new_v4().simple());
        let admin = sqlx::PgPool::connect(&admin_url).await.unwrap();
        sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&admin)
            .await
            .unwrap();
        let (base, _) = admin_url.rsplit_once('/').unwrap();
        let pool = sqlx::PgPool::connect(&format!("{base}/{name}"))
            .await
            .unwrap();
        sqlx::raw_sql(FIXTURE).execute(&pool).await.unwrap();
        pool.close().await;
        let rest = base.strip_prefix("postgres://").unwrap();
        let (credentials, address) = rest.split_once('@').unwrap();
        let (user, password) = credentials.split_once(':').unwrap();
        let (host, port) = address.split_once(':').unwrap_or((address, "5432"));
        let connection = format!(
            "Host={host};Port={port};Database={name};Username={user};Password={password};\
             SSL Mode=Disable"
        );

        let dir = v1_dir();
        // The cache folder this import checks; a 1.x mount at /config/cache does not hold it.
        let cache = dir.join("cache").display().to_string();
        let vars = vars(&[
            ("DATABASE_PROVIDER", "postgres"),
            ("DATABASE_CONNECTION_STRING", &connection),
            ("MOUNT_DIR", "/config/cache"),
        ]);
        let target = format!("infinidysk_test_{}", uuid::Uuid::new_v4().simple());
        sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {target}")))
            .execute(&admin)
            .await
            .unwrap();
        for destination in ["sqlite::memory:".to_owned(), format!("{base}/{target}")] {
            let db = Db::connect(&destination).await.unwrap();
            let stages = std::sync::Mutex::new(Vec::new());
            import_with_progress(&db, &dir, &vars, &cache, |p| {
                stages.lock().unwrap().push(p.stage);
            })
            .await
            .unwrap();
            let stages = stages.into_inner().unwrap();
            assert_eq!(stages.first(), Some(&infinidysk_import::Stage::Copying));
            assert!(stages.contains(&infinidysk_import::Stage::Checking));
            let config: HashMap<String, String> = db.config().await.unwrap().into_iter().collect();
            assert_eq!(config[STATE_KEY], "\"done\"");
            let report: String = serde_json::from_str(&config[REPORT_KEY]).unwrap();
            assert_eq!(
                report.contains("Settings > System > Database"),
                !db.is_postgres(),
                "{report}"
            );
            assert!(
                !report.contains("carried-over settings are invalid")
                    && !report.contains("settings not carried over"),
                "{report}"
            );
            let store = ConfigStore::load_in(db.clone(), std::iter::empty(), cache.clone())
                .await
                .unwrap();
            assert_eq!(store.settings().mount.dir, "/config/cache");
            assert!(!dir.join(infinidysk_import::postgres::STAGED).exists());
            let job = db.item_by_path("/content/tv/job").await.unwrap().unwrap();
            let history = db.history().await.unwrap();
            assert_eq!(history.len(), 1);
            assert_eq!(history[0].0.download_dir, Some(job.id));
            db.close().await;
        }
        std::fs::remove_dir_all(dir).unwrap();
        for database in [name, target] {
            sqlx::query(sqlx::AssertSqlSafe(format!(
                "DROP DATABASE {database} WITH (FORCE)"
            )))
            .execute(&admin)
            .await
            .unwrap();
        }
        admin.close().await;
    }

    fn provider(kind: ProviderKind, priority: i64) -> infinidysk_import::Provider {
        serde_json::from_value(serde_json::json!({
            "Type": match kind { ProviderKind::Disabled => 0, ProviderKind::Pooled => 1, ProviderKind::Backup => 3 },
            "Host": "news.example.net", "Port": 563, "UseSsl": true,
            "MaxConnections": 20, "Priority": priority,
        }))
        .unwrap()
    }

    #[test]
    fn pooled_providers_share_the_first_level_and_backups_follow_by_priority() {
        let mut notes = Vec::new();
        let levels: Vec<(u32, bool)> = providers(
            vec![
                provider(ProviderKind::Pooled, 2),
                provider(ProviderKind::Backup, 7),
                provider(ProviderKind::Pooled, 0),
                provider(ProviderKind::Backup, 3),
                provider(ProviderKind::Disabled, 1),
            ],
            |_| 0,
            &mut Vec::new(),
            &mut notes,
        )
        .iter()
        .map(|p| (p.priority, p.enabled))
        .collect();
        assert_eq!(
            levels,
            [(0, true), (2, true), (0, true), (1, true), (0, false)]
        );
    }

    #[test]
    fn a_csharp_download_limit_takes_the_stop_at_or_below_it() {
        let limit = |mbps: &str| download_limit(mbps).unwrap();
        assert_eq!(limit("940"), Some(94));
        assert_eq!(limit("500"), Some(63));
        assert_eq!(limit("250"), Some(31));
        assert_eq!(limit("100"), Some(31));
        assert_eq!(limit("1000"), Some(125));
        assert_eq!(limit("5000"), Some(250));
        assert_eq!(limit("0"), None);
        assert_eq!(limit(""), None);
        assert!(download_limit("fast").is_err());
    }

    #[test]
    fn a_rejected_value_is_left_out_instead_of_every_setting() {
        let mut s = Settings::default();
        s.mount.dir = "/mnt/nzbdav".to_owned();
        s.imports.library_dirs = vec![crate::config::LibraryDir {
            path: "/mnt/nzbdav/library".to_owned(),
            arr_path: String::new(),
        }];
        s.network.public_url = "not a url".to_owned();
        s.sab.categories = vec!["kept".to_owned()];
        let mut notes = Vec::new();
        keep_valid(&mut s, &mut notes, crate::config::DEFAULT_CACHE_DIR);
        assert!(s.clone().validate().is_ok(), "{notes:?}");
        assert!(s.imports.library_dirs.is_empty() && s.network.public_url.is_empty());
        assert_eq!(s.sab.categories, ["kept"]);
        assert_eq!(notes.len(), 2);
    }

    #[test]
    fn a_custom_provider_takes_the_backbone_of_its_storage_group() {
        let p = |host: &str, group: &str, limit: Option<i64>| {
            serde_json::from_value::<infinidysk_import::Provider>(serde_json::json!({
                "Type": 1, "Host": host, "Port": 563, "UseSsl": true, "MaxConnections": 20,
                "StorageGroup": group, "ByteLimit": limit, "BytesUsedOffset": 7,
            }))
            .unwrap()
        };
        let (mut used, mut notes) = (Vec::new(), Vec::new());
        let out = providers(
            vec![
                p("news.newshosting.com", "Omicron", None),
                p("custom.example.net", "omicron", Some(5_000_000_000)),
            ],
            |p| u64::try_from(p.bytes_used_offset).unwrap(),
            &mut used,
            &mut notes,
        );
        assert_eq!(out[1].profile, None);
        assert_eq!(out[1].backbone.as_deref(), Some("omicron"));
        assert_eq!(used, [(out[1].id, 7)]);
    }

    fn oidc_vars(extra: &[(&'static str, &'static str)]) -> HashMap<&'static str, &'static str> {
        [
            (
                "OIDC_ISSUER",
                "https://auth.example.com/application/o/dysk/",
            ),
            ("OIDC_CLIENT_ID", "dysk"),
            ("OIDC_CLIENT_SECRET", "sso-secret"),
        ]
        .into_iter()
        .chain(extra.iter().copied())
        .collect()
    }

    #[test]
    fn oidc_variables_carry_over_with_a_note_for_role_mapping() {
        let vars = oidc_vars(&[
            (
                "OIDC_REDIRECT_URI",
                "https://dysk.example.com/auth/oidc/callback",
            ),
            ("OIDC_ADMIN_CLAIM", "groups"),
        ]);
        let mut notes = Vec::new();
        let o = oidc(|n| vars.get(n).copied(), false, &mut notes).unwrap();
        assert_eq!(o.issuer, "https://auth.example.com/application/o/dysk/");
        assert_eq!(o.client_id, "dysk");
        assert_eq!(o.client_secret, Secret("sso-secret".into()));
        assert_eq!(
            o.redirect_uri,
            "https://dysk.example.com/auth/oidc/callback"
        );
        let defaults = Oidc::default();
        assert_eq!(
            (o.scopes, o.username_claim),
            (defaults.scopes, defaults.username_claim)
        );
        assert_eq!(notes.len(), 2, "{notes:?}");
        assert!(notes.iter().any(|n| n.contains("OIDC_ADMIN_CLAIM")));
    }

    #[test]
    fn partial_oidc_variables_leave_sso_off() {
        let vars = oidc_vars(&[]);
        let mut notes = Vec::new();
        let o = oidc(
            |n| {
                (n != "OIDC_CLIENT_SECRET")
                    .then(|| vars.get(n).copied())
                    .flatten()
            },
            false,
            &mut notes,
        )
        .unwrap();
        assert_eq!(o, Oidc::default());
        assert_eq!(notes, Vec::<String>::new());
    }

    #[test]
    fn invalid_1x_sso_without_an_admin_stops_the_import() {
        let mut vars = oidc_vars(&[]);
        vars.insert("OIDC_ISSUER", "http://auth.example.com/");
        let error = oidc(|n| vars.get(n).copied(), false, &mut Vec::new())
            .unwrap_err()
            .to_string();
        assert!(
            error.contains("http://auth.example.com/") && error.contains("OIDC_*"),
            "{error}"
        );
        let mut notes = Vec::new();
        let o = oidc(|n| vars.get(n).copied(), true, &mut notes).unwrap();
        assert_eq!(o, Oidc::default());
        assert_eq!(notes.len(), 1);
        assert!(notes[0].contains("not carried over"), "{notes:?}");
    }
}
