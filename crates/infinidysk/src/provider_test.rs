//! Provider speed tests and pipeline auto-tuning. Both download a public test NZB from
//! one provider while nothing else uses the providers, and stop when playback starts.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use futures_util::StreamExt as _;
use futures_util::stream::FuturesUnordered;
use infinidysk_db::Db;
use infinidysk_nntp::{Downloads, Endpoint, Error as NntpError, Lease, Pool, PoolConfig, Usage};
use infinidysk_queue::nzb;
use serde::{Deserialize, Serialize};
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;
use uuid::Uuid;

use crate::admin::{Admin, ApiError, Fail};
use crate::app;
use crate::config;
use crate::metrics::unix_ms;

const MANIFEST: &str = "https://sabnzbd.org/tests/test_download_1GB.nzb";
const MANIFEST_MAX: usize = 4 << 20;
/// How long running work gets to finish before a test gives up as busy.
const DRAIN: Duration = Duration::from_secs(10);
/// Connections ramp up and TCP windows open before the measurement starts. Tests run for
/// a fixed time, cycling through the test articles, so fast links are measured as well as
/// slow ones.
const SPEED_WARMUP: Duration = Duration::from_secs(5);
const SPEED_WINDOW: Duration = Duration::from_secs(20);
const DEPTHS: [usize; 4] = [1, 4, 8, 16];
const TUNE_CONNECTIONS: usize = 4;
const TUNE_WARMUP: Duration = Duration::from_secs(3);
const TUNE_WINDOW: Duration = Duration::from_secs(10);
/// A deeper pipeline must beat depth 1 by this much to be picked.
const TUNE_GAIN: f64 = 1.10;
/// Config key of the latest speed per provider; not part of [`config::Settings`], so
/// recording one never rebuilds the provider pools.
const SPEEDS_KEY: &str = "provider_speeds";
const MB: f64 = 1_000_000.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum Kind {
    Speed,
    Tune,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum RunState {
    Pending,
    Running,
    Done,
    Failed,
    Cancelled,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct Job {
    id: Uuid,
    kind: Kind,
    state: RunState,
    providers: Vec<Run>,
}

/// One provider's part of a job.
#[derive(Clone, Serialize, ToSchema)]
pub struct Run {
    provider_id: Uuid,
    name: String,
    state: RunState,
    /// What the test is doing now.
    phase: String,
    /// Connections the test opens.
    connections: usize,
    /// Speed test progress and result.
    sample: Option<Sample>,
    /// Auto-tune: one sample per pipeline depth tried, in order.
    depths: Vec<Sample>,
    current_depth: u8,
    recommended_depth: Option<u8>,
    /// Whether `recommended_depth` was saved.
    applied: bool,
    error: Option<String>,
}

/// Decoded article data downloaded in a timed run: a warm-up, then the measured window.
#[derive(Clone, Copy, Debug, Default, Serialize, ToSchema)]
pub struct Sample {
    depth: usize,
    /// Downloaded so far, warm-up included; it counts against a quota.
    bytes: u64,
    elapsed_ms: u64,
    /// Planned length of the run, warm-up included.
    duration_ms: u64,
    /// Still warming up; `mb_per_s` is then the rate since the start.
    warming: bool,
    articles: usize,
    failed: usize,
    /// Over the measured window.
    mb_per_s: f64,
}

#[derive(Clone, Serialize, Deserialize, ToSchema)]
pub struct Speed {
    mb_per_s: f64,
    /// Unix milliseconds.
    at: i64,
}

#[derive(Serialize, ToSchema)]
pub struct TestStatus {
    /// The running or last job since start.
    job: Option<Job>,
    /// Latest speed test result by provider id.
    speeds: HashMap<Uuid, Speed>,
}

#[derive(Serialize, ToSchema)]
pub struct Started {
    id: Uuid,
}

pub struct Tests {
    current: Mutex<Option<Current>>,
    speeds: Mutex<HashMap<Uuid, Speed>>,
    /// Message-ids of the test NZB, fetched once.
    manifest: tokio::sync::Mutex<Option<Arc<[String]>>>,
    http: reqwest::Client,
    db: Db,
}

struct Current {
    job: Arc<Mutex<Job>>,
    cancel: CancellationToken,
}

impl Tests {
    pub async fn load(db: Db) -> anyhow::Result<Self> {
        let speeds = db
            .config()
            .await?
            .into_iter()
            .find(|(key, _)| key == SPEEDS_KEY)
            .and_then(|(_, value)| serde_json::from_str(&value).ok())
            .unwrap_or_default();
        let http = reqwest::Client::builder()
            .user_agent(crate::fetch::USER_AGENT)
            .timeout(Duration::from_secs(30))
            .https_only(true)
            .redirect(reqwest::redirect::Policy::limited(3))
            .build()?;
        Ok(Self {
            current: Mutex::default(),
            speeds: Mutex::new(speeds),
            manifest: tokio::sync::Mutex::default(),
            http,
            db,
        })
    }

    async fn manifest(&self) -> Result<Arc<[String]>, String> {
        let mut cached = self.manifest.lock().await;
        if let Some(ids) = &*cached {
            return Ok(Arc::clone(ids));
        }
        let failed = |e: reqwest::Error| format!("could not download the test NZB: {e}");
        let mut response = self
            .http
            .get(MANIFEST)
            .send()
            .await
            .and_then(reqwest::Response::error_for_status)
            .map_err(failed)?;
        let mut raw = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(failed)? {
            raw.extend_from_slice(&chunk);
            if raw.len() > MANIFEST_MAX {
                return Err("the test NZB is too large".to_owned());
            }
        }
        let parsed = nzb::parse(&raw).map_err(|e| format!("the test NZB is invalid: {e}"))?;
        let ids: Arc<[String]> = parsed
            .files
            .into_iter()
            .flat_map(|f| f.segments)
            .map(|s| s.id)
            .collect();
        *cached = Some(Arc::clone(&ids));
        drop(cached);
        Ok(ids)
    }

    async fn record(&self, id: Uuid, mb_per_s: f64) {
        let json = {
            let mut speeds = self.speeds.lock().unwrap_or_else(PoisonError::into_inner);
            speeds.insert(
                id,
                Speed {
                    mb_per_s,
                    at: unix_ms(std::time::SystemTime::now()),
                },
            );
            serde_json::to_string(&*speeds)
        };
        let stored = match json {
            Ok(json) => self
                .db
                .write_config(&[(SPEEDS_KEY, &json)], &[])
                .await
                .map_err(|e| e.to_string()),
            Err(e) => Err(e.to_string()),
        };
        if let Err(reason) = stored {
            tracing::warn!(%reason, "could not store the provider speed");
        }
    }

    fn status(&self) -> TestStatus {
        let job = self
            .current
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .as_ref()
            .map(|c| lock(&c.job).clone());
        TestStatus {
            job,
            speeds: self
                .speeds
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .clone(),
        }
    }
}

fn lock(job: &Mutex<Job>) -> std::sync::MutexGuard<'_, Job> {
    job.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Starts a speed test of one saved provider, enabled or not, at its saved settings.
#[utoipa::path(post, path = "/api/v1/providers/{id}/speed-test", tag = "settings",
    params(("id" = Uuid, Path)),
    responses((status = 202, body = Started), (status = 404, body = ApiError),
        (status = 409, body = ApiError, description = "A test is already running")))]
pub async fn post_speed_test(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<(StatusCode, Json<Started>), Fail> {
    let provider = admin
        .config
        .read()
        .await
        .settings()
        .providers
        .iter()
        .find(|p| p.id == id)
        .cloned()
        .ok_or(Fail::NotFound)?;
    start(&admin, Kind::Speed, vec![provider])
}

/// Measures pipeline depths 1, 4, 8 and 16 on every enabled provider in turn and saves
/// each provider's best depth.
#[utoipa::path(post, path = "/api/v1/providers/tune-pipeline", tag = "settings",
    responses((status = 202, body = Started), (status = 400, body = ApiError),
        (status = 409, body = ApiError, description = "A test is already running")))]
pub async fn post_tune_pipeline(
    State(admin): State<Arc<Admin>>,
) -> Result<(StatusCode, Json<Started>), Fail> {
    let providers: Vec<_> = admin
        .config
        .read()
        .await
        .settings()
        .providers
        .iter()
        .filter(|p| p.enabled)
        .cloned()
        .collect();
    if providers.is_empty() {
        return Err(Fail::Bad("no provider is enabled".to_owned()));
    }
    if infinidysk_core::experiments()
        .iter()
        .any(|e| e.starts_with("pipe"))
    {
        return Err(Fail::Bad(
            "a pipeline experiment overrides the depth; turn it off first".to_owned(),
        ));
    }
    start(&admin, Kind::Tune, providers)
}

/// The running or last test job and the latest speed of each provider.
#[utoipa::path(get, path = "/api/v1/provider-tests", tag = "settings",
    responses((status = 200, body = TestStatus)))]
pub async fn get_provider_tests(State(admin): State<Arc<Admin>>) -> Json<TestStatus> {
    Json(admin.tests.status())
}

/// Stops the running test job; depths already saved stay.
#[utoipa::path(delete, path = "/api/v1/provider-tests", tag = "settings",
    responses((status = 204)))]
pub async fn delete_provider_tests(State(admin): State<Arc<Admin>>) -> StatusCode {
    if let Some(current) = &*admin
        .tests
        .current
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
    {
        current.cancel.cancel();
    }
    StatusCode::NO_CONTENT
}

fn start(
    admin: &Arc<Admin>,
    kind: Kind,
    providers: Vec<config::Provider>,
) -> Result<(StatusCode, Json<Started>), Fail> {
    let mut current = admin
        .tests
        .current
        .lock()
        .unwrap_or_else(PoisonError::into_inner);
    if current
        .as_ref()
        .is_some_and(|c| lock(&c.job).state == RunState::Running)
    {
        return Err(Fail::Conflict(
            "a provider test is already running".to_owned(),
        ));
    }
    let id = Uuid::now_v7();
    let job = Arc::new(Mutex::new(Job {
        id,
        kind,
        state: RunState::Running,
        providers: providers
            .iter()
            .map(|p| Run {
                provider_id: p.id,
                name: if p.name.is_empty() {
                    p.hostname.clone()
                } else {
                    p.name.clone()
                },
                state: RunState::Pending,
                phase: String::new(),
                connections: match kind {
                    Kind::Speed => p.max_connections.into(),
                    Kind::Tune => TUNE_CONNECTIONS.min(p.max_connections.into()),
                },
                sample: None,
                depths: Vec::new(),
                current_depth: p.pipeline_depth,
                recommended_depth: None,
                applied: false,
                error: None,
            })
            .collect(),
    }));
    let cancel = admin.cancel.child_token();
    *current = Some(Current {
        job: Arc::clone(&job),
        cancel: cancel.clone(),
    });
    drop(current);
    tokio::spawn(run(Arc::clone(admin), job, cancel, kind, providers));
    Ok((StatusCode::ACCEPTED, Json(Started { id })))
}

async fn run(
    admin: Arc<Admin>,
    job: Arc<Mutex<Job>>,
    cancel: CancellationToken,
    kind: Kind,
    providers: Vec<config::Provider>,
) {
    let edit = |index: usize, f: &dyn Fn(&mut Run)| f(&mut lock(&job).providers[index]);
    edit(0, &|r| "downloading the test NZB".clone_into(&mut r.phase));
    let ids = tokio::select! {
        ids = admin.tests.manifest() => ids,
        () = cancel.cancelled() => Err("cancelled".to_owned()),
    };
    let ids = match ids {
        Ok(ids) if ids.is_empty() => Err("the test NZB has no articles".to_owned()),
        ids => ids,
    };
    let ids = match ids {
        Ok(ids) => ids,
        Err(reason) => {
            tracing::warn!(%reason, "provider test failed");
            let mut job = lock(&job);
            job.state = RunState::Failed;
            for r in &mut job.providers {
                r.state = RunState::Failed;
                r.phase.clear();
                r.error = Some(reason.clone());
            }
            drop(job);
            return;
        }
    };

    let mut state = RunState::Done;
    for (index, provider) in providers.iter().enumerate() {
        if cancel.is_cancelled() {
            state = RunState::Cancelled;
        }
        if state == RunState::Cancelled {
            edit(index, &|r| r.state = RunState::Cancelled);
            continue;
        }
        edit(index, &|r| {
            r.state = RunState::Running;
            "waiting for the providers to be idle".clone_into(&mut r.phase);
        });
        let result = match admin
            .client
            .exclusive(&provider.id.to_string(), DRAIN)
            .await
        {
            None => Err(Stop::Failed(
                "the providers are busy; try again when nothing is playing or importing".to_owned(),
            )),
            Some(lease) => {
                let progress = |sample: Sample| {
                    edit(index, &|r| match kind {
                        Kind::Speed => r.sample = Some(sample),
                        Kind::Tune => match r.depths.last_mut() {
                            Some(last) if last.depth == sample.depth => *last = sample,
                            _ => r.depths.push(sample),
                        },
                    });
                };
                let phase = |text: String| edit(index, &|r| r.phase.clone_from(&text));
                let usage = admin.live.quotas().usage(provider.id);
                let measured = measure(kind, provider, usage, &ids, &progress, &phase);
                tokio::select! {
                    result = measured => result.map_err(Stop::Failed),
                    reason = stopped(&cancel, &lease) => Err(reason),
                }
            }
        };
        let outcome = match result {
            Ok(Measured::Speed(sample)) => {
                admin.tests.record(provider.id, sample.mb_per_s).await;
                Ok(())
            }
            Ok(Measured::Depth(depth)) => {
                edit(index, &|r| r.recommended_depth = Some(depth));
                match save_depth(&admin, provider, depth).await {
                    Ok(()) => {
                        edit(index, &|r| r.applied = true);
                        Ok(())
                    }
                    Err(reason) => Err(Stop::Failed(reason)),
                }
            }
            Ok(Measured::Inconclusive) => Err(Stop::Failed(
                "no depth could be measured; nothing was saved".to_owned(),
            )),
            Err(stop) => Err(stop),
        };
        let name = lock(&job).providers[index].name.clone();
        match outcome {
            Ok(()) => edit(index, &|r| r.state = RunState::Done),
            Err(Stop::Failed(reason)) => {
                tracing::warn!(provider = %name, %reason, "provider test failed");
                state = RunState::Failed;
                edit(index, &|r| {
                    r.state = RunState::Failed;
                    r.error = Some(reason.clone());
                });
            }
            Err(Stop::Cancelled(reason)) => {
                tracing::info!(provider = %name, %reason, "provider test stopped");
                state = RunState::Cancelled;
                edit(index, &|r| {
                    r.state = RunState::Cancelled;
                    r.error = Some(reason.to_owned());
                });
            }
        }
        edit(index, &|r| r.phase.clear());
    }
    lock(&job).state = state;
}

enum Stop {
    Failed(String),
    /// By the user, playback or shutdown.
    Cancelled(&'static str),
}

enum Measured {
    Speed(Sample),
    Depth(u8),
    Inconclusive,
}

async fn stopped(cancel: &CancellationToken, lease: &Lease) -> Stop {
    tokio::select! {
        () = cancel.cancelled() => Stop::Cancelled("cancelled"),
        () = lease.token().cancelled() => Stop::Cancelled("playback started"),
    }
}

async fn measure(
    kind: Kind,
    provider: &config::Provider,
    usage: Arc<Usage>,
    ids: &[String],
    progress: &(dyn Fn(Sample) + Sync),
    phase: &(dyn Fn(String) + Sync),
) -> Result<Measured, String> {
    let endpoint = app::endpoint(provider)
        .map_err(|e| e.to_string())?
        .with_usage(usage);
    let connections = usize::from(provider.max_connections);
    match kind {
        Kind::Speed => {
            phase("measuring download speed".to_owned());
            let depth = provider.pipeline_depth.into();
            let timing = (0, SPEED_WARMUP, SPEED_WINDOW);
            sample(endpoint, connections, depth, ids, timing, progress)
                .await
                .map(Measured::Speed)
        }
        Kind::Tune => {
            let mut samples = Vec::with_capacity(DEPTHS.len());
            for (i, depth) in DEPTHS.into_iter().enumerate() {
                phase(format!("testing pipeline depth {depth}"));
                let connections = TUNE_CONNECTIONS.min(connections);
                // Each depth starts on other articles than the last.
                let timing = (i * ids.len() / DEPTHS.len(), TUNE_WARMUP, TUNE_WINDOW);
                let sample =
                    sample(endpoint.clone(), connections, depth, ids, timing, progress).await?;
                samples.push(sample);
            }
            Ok(best_depth(&samples)
                .and_then(|d| u8::try_from(d).ok())
                .map_or(Measured::Inconclusive, Measured::Depth))
        }
    }
}

/// Ends every test connection when the sample stops, finished or not.
struct Abort<'a>(&'a Pool);

impl Drop for Abort<'_> {
    fn drop(&mut self) {
        self.0.abort();
    }
}

/// Downloads `ids` round and round from `offset` through a pool of its own, with
/// `connections × depth` requests in flight, for `warmup` then `window`. Handshakes are
/// not timed. Fails once the provider's quota is used up.
// ponytail: counts decoded bytes of whole articles, so yEnc overhead and the article cut
// off by the window are left out; meter the socket if that skew ever matters.
#[allow(clippy::significant_drop_tightening)] // False positive: `_abort` borrows `pool` to the end.
async fn sample(
    endpoint: Endpoint,
    connections: usize,
    depth: usize,
    ids: &[String],
    (offset, warmup, window): (usize, Duration, Duration),
    progress: &(dyn Fn(Sample) + Sync),
) -> Result<Sample, String> {
    let config = PoolConfig {
        max_connections: connections,
        pipeline_depth: depth,
        idle_timeout: warmup + window,
        min_connections: connections,
    };
    let pool = Pool::new(endpoint, config, Downloads::new(connections * depth))
        .map_err(|e| e.to_string())?;
    let _abort = Abort(&pool);
    let used_up = || Err("the provider's download quota is used up".to_owned());
    if pool.exhausted() {
        return used_up();
    }
    pool.prewarm(connections).await;
    let in_flight = connections * depth;
    let mut ids = ids.iter().cycle().skip(offset);
    let mut fetches = FuturesUnordered::new();
    fetches.extend(ids.by_ref().take(in_flight).map(|id| pool.body(id)));
    let start = Instant::now();
    let warmed = start + warmup;
    let warm = tokio::time::sleep_until(warmed.into());
    let deadline = tokio::time::sleep_until((warmed + window).into());
    tokio::pin!(warm, deadline);
    let mut sample = Sample {
        depth,
        duration_ms: millis(warmup + window),
        warming: true,
        ..Sample::default()
    };
    // Bytes downloaded during the warm-up.
    let mut base = None;
    let mut last_error = None;
    loop {
        tokio::select! {
            biased;
            () = &mut warm, if base.is_none() => {
                base = Some(sample.bytes);
                sample.warming = false;
            }
            () = &mut deadline => break,
            Some(next) = fetches.next() => {
                match next {
                    Ok(body) => {
                        sample.articles += 1;
                        sample.bytes += body.data.len() as u64;
                    }
                    Err(NntpError::Unavailable) => {
                        return Err("cannot connect to the provider; the log has the reason".to_owned());
                    }
                    Err(error @ NntpError::AuthRejected { .. }) => return Err(error.to_string()),
                    Err(error) => {
                        sample.failed += 1;
                        last_error = Some(error);
                    }
                }
                if pool.exhausted() {
                    return used_up();
                }
                if let Some(id) = ids.next() {
                    fetches.push(pool.body(id));
                }
            }
        }
        sample.elapsed_ms = millis(start.elapsed());
        sample.mb_per_s = match base {
            None => rate(sample.bytes, start.elapsed()),
            Some(base) => rate(sample.bytes - base, warmed.elapsed()),
        };
        progress(sample);
    }
    sample.elapsed_ms = millis(start.elapsed());
    sample.mb_per_s = rate(sample.bytes - base.unwrap_or(0), warmed.elapsed());
    if sample.articles == 0 || sample.failed * 10 > sample.articles + sample.failed {
        let reason = last_error.map_or_else(|| "no article arrived".to_owned(), |e| e.to_string());
        return Err(format!(
            "{} of {} test articles failed: {reason}",
            sample.failed,
            sample.articles + sample.failed
        ));
    }
    Ok(sample)
}

fn millis(duration: Duration) -> u64 {
    u64::try_from(duration.as_millis()).unwrap_or(u64::MAX)
}

#[allow(clippy::cast_precision_loss)] // Byte counts stay far below 2^52.
fn rate(bytes: u64, elapsed: Duration) -> f64 {
    let secs = elapsed.as_secs_f64();
    if secs > 0.0 {
        bytes as f64 / MB / secs
    } else {
        0.0
    }
}

/// The fastest depth at least [`TUNE_GAIN`] times faster than depth 1, else 1; lower
/// depths win ties. `None` without a depth 1 measurement.
fn best_depth(samples: &[Sample]) -> Option<usize> {
    let base = samples
        .iter()
        .find(|s| s.depth == 1)
        .map(|s| s.mb_per_s)
        .filter(|&r| r > 0.0)?;
    let mut sorted = samples.to_vec();
    sorted.sort_by_key(|s| s.depth);
    Some(
        sorted
            .iter()
            .filter(|s| s.mb_per_s >= base * TUNE_GAIN)
            .fold((1, base), |best, s| {
                if s.mb_per_s > best.1 {
                    (s.depth, s.mb_per_s)
                } else {
                    best
                }
            })
            .0,
    )
}

/// Saves `depth` for `provider` unless its connection settings changed meanwhile.
async fn save_depth(admin: &Admin, provider: &config::Provider, depth: u8) -> Result<(), String> {
    let mut store = admin.config.write().await;
    let mut next = store.settings().clone();
    let Some(target) = next.providers.iter_mut().find(|p| p.id == provider.id) else {
        return Err("the provider was removed during the test".to_owned());
    };
    if (
        &target.hostname,
        target.port,
        target.tls,
        &target.username,
        target.max_connections,
    ) != (
        &provider.hostname,
        provider.port,
        provider.tls,
        &provider.username,
        provider.max_connections,
    ) {
        return Err("the provider was edited during the test; nothing was saved".to_owned());
    }
    if target.pipeline_depth == depth {
        return Ok(());
    }
    target.pipeline_depth = depth;
    store.save(next).await.map_err(|e| e.to_string())?;
    admin
        .live
        .apply(store.settings())
        .await
        .map_err(|e| format!("saved, but could not apply: {e:#}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn best_depth_needs_a_ten_percent_gain_and_prefers_lower_depths() {
        let samples = |rates: &[f64]| -> Vec<Sample> {
            DEPTHS
                .iter()
                .zip(rates)
                .map(|(&depth, &mb_per_s)| Sample {
                    depth,
                    mb_per_s,
                    ..Sample::default()
                })
                .collect()
        };
        assert_eq!(best_depth(&samples(&[10.0, 10.9, 10.5, 10.0])), Some(1));
        assert_eq!(best_depth(&samples(&[10.0, 11.0, 12.0, 12.0])), Some(8));
        assert_eq!(best_depth(&samples(&[10.0, 30.0, 20.0, 25.0])), Some(4));
        assert_eq!(best_depth(&samples(&[0.0, 30.0, 20.0, 25.0])), None);
        assert_eq!(best_depth(&samples(&[])), None);
    }
}
