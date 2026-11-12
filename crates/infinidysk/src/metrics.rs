//! Usage metrics: a 2 s sampler for the live dashboard, one write a minute to the metrics
//! store, the overview history and Prometheus text.
//!
//! Metrics never get in the way of playback: a store that fails pauses history and says
//! so in the UI, while live numbers keep working.

use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use axum::Json;
use axum::extract::{Path as AxumPath, Query, State};
use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use infinidysk_core::ItemId;
use infinidysk_core::trace::Outcome;
use infinidysk_db::metrics::{
    BreakerEvent, Grain, Metrics as Store, Records, SessionRecord, Usage,
};
use infinidysk_health::Health;
use infinidysk_nntp::{Client, LATENCY_BUCKETS, Phase, ProviderStats, Workload};
use infinidysk_queue::Queue;
use infinidysk_stream::{Cache, CacheStats};
use infinidysk_vfs::{Delivery, Kernel, Session, Vfs};
use serde::{Deserialize, Serialize};
use tokio::sync::watch;
use tokio::time::MissedTickBehavior;
use tokio_util::sync::CancellationToken;
use tracing::{info, warn};
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

use crate::admin::{Admin, Fail};

const SAMPLE_EVERY: Duration = Duration::from_secs(2);
const SAMPLES_PER_MINUTE: usize = 30;
/// Swap and `/proc/self/status` are read this often, not every sample.
const SLOW_READ_EVERY: Duration = Duration::from_mins(1);
const MINUTE_MS: i64 = 60_000;
const HOUR_MS: i64 = 60 * MINUTE_MS;
const DAY_MS: i64 = 24 * HOUR_MS;
/// All-time history draws a bar per day up to this many days, then a bar per week.
const DAILY_BARS: i64 = 120;
/// Stale query planner statistics are refreshed at the first prune and then this often.
const OPTIMIZE_HOURS: i64 = 6;
/// Finished reads and breaker events kept in memory while the store is failing.
const MAX_PENDING: usize = 10_000;
const RECENT_READS: u32 = 200;
const TOP_READS: u32 = 10;
const FILE_READS: u32 = 100;
const TOP_APPS: usize = 20;
/// A read without data this long is logged as stalled.
const STALL_WARN_MS: u64 = 30_000;
/// Stalls logged one by one per sample; the rest are summed up.
const STALL_DETAILS: usize = 3;

/// What the sampler reads.
pub struct Sources {
    pub client: Arc<Client>,
    pub vfs: Arc<Vfs>,
    pub queue: Arc<Queue>,
    pub health: Arc<Health>,
    pub cache: Arc<Cache>,
    pub maintenance: Arc<crate::maintenance::Tracker>,
    /// FUSE mount state: the kernel's limits while mounted.
    pub mount: watch::Receiver<Result<Kernel, String>>,
}

pub struct Metrics {
    sources: Sources,
    path: PathBuf,
    /// Set by [`run`](Self::run) once the file is open and checked; history is off
    /// until then, and for good when it cannot be opened.
    store: OnceLock<Store>,
    /// Why CPU and memory are not reported when it is an error.
    cgroup: Result<Cgroup, String>,
    state: Mutex<Sampled>,
    live: watch::Sender<Arc<LiveView>>,
}

#[derive(Default)]
struct Sampled {
    /// Not yet written.
    pending: Pending,
    /// The minute `pending` is written as next.
    pending_minute: i64,
    /// Counters since start at the last sample.
    last: HashMap<String, u64>,
    last_at: Option<Instant>,
    /// Providers whose breaker is open, by key.
    open: HashMap<String, Outage>,
    /// Bytes served per open read at the last sample.
    served: HashMap<u64, u64>,
    /// Bytes each provider delivered per open read at the last sample.
    supplied: HashMap<(u64, u16), u64>,
    /// Requests, failed requests and bytes read per sample over the last minute.
    minute: VecDeque<(u64, u64, u64)>,
    /// CPU time in microseconds at the last sample.
    cpu: Option<(Instant, u64)>,
    error: Option<String>,
    /// Reads logged as stalled, and since when they stall.
    stalled: HashMap<u64, Instant>,
    /// Since when the kernel holds a full background window of FUSE requests we have not
    /// received, and whether that was logged.
    kernel_queued: Option<(Instant, bool)>,
    /// The last [`SlowRead`].
    slow: Option<SlowRead>,
}

#[derive(Clone, Default)]
struct Pending {
    counters: HashMap<String, u64>,
    sessions: Vec<SessionRecord>,
    events: Vec<BreakerEvent>,
}

impl Pending {
    /// `peak.` keys keep the largest value; the rest add up.
    fn add(&mut self, key: &str, value: u64) {
        if value == 0 {
            return;
        }
        let slot = self.counters.entry(key.to_owned()).or_default();
        *slot = if key.starts_with("peak.") {
            (*slot).max(value)
        } else {
            slot.saturating_add(value)
        };
    }

    /// Puts `newer` after `self`, dropping the oldest reads and events past the cap.
    fn merge(&mut self, newer: Self) {
        for (key, value) in newer.counters {
            self.add(&key, value);
        }
        self.sessions.extend(newer.sessions);
        self.events.extend(newer.events);
        let excess = self.sessions.len().saturating_sub(MAX_PENDING);
        self.sessions.drain(..excess);
        let excess = self.events.len().saturating_sub(MAX_PENDING);
        self.events.drain(..excess);
    }

    fn is_empty(&self) -> bool {
        self.counters.is_empty() && self.sessions.is_empty() && self.events.is_empty()
    }
}

/// The latest 2 s sample; also sent as the `live` server-sent event.
#[derive(Clone, Default, Serialize, ToSchema)]
pub struct LiveView {
    /// Unix milliseconds.
    at: i64,
    /// Bytes per second returned to readers.
    read_bps: u64,
    /// Bytes per second received from providers, all workloads.
    fetch_bps: u64,
    /// Over the last minute: finished requests, requests that failed for the caller
    /// (missing articles and recovered errors not counted) and bytes returned to readers.
    requests_minute: u64,
    failures_minute: u64,
    read_minute: u64,
    /// Decoded bytes readers hold now, and the most they may hold together.
    article_bytes: u64,
    article_cap: u64,
    /// Reads waiting for budget other reads hold.
    budget_waiters: usize,
    /// What the kernel applied to the FUSE mount; none while not mounted.
    fuse: Option<LiveFuse>,
    reads: Vec<LiveRead>,
    imports: Vec<LiveImport>,
    /// The maintenance job running now.
    maintenance: Option<crate::maintenance::LiveMaintenance>,
    providers: Vec<LiveProvider>,
    /// Global download permits not in use.
    permits_free: Option<usize>,
    resources: Option<Resources>,
    /// Why `resources` is missing.
    resources_unavailable: Option<String>,
    /// Why history is not being saved.
    writer_error: Option<String>,
}

/// A queue job importing now.
#[derive(Clone, Serialize, ToSchema)]
struct LiveImport {
    id: Uuid,
    /// 0 to 99 while importing.
    percent: u8,
    /// What the import is doing, for people.
    step: &'static str,
    /// Estimated time left; none until there is enough to go on.
    eta_ms: Option<u64>,
}

/// CPU and memory of infinidysk's cgroup (the whole container under Docker), and of this
/// process alone in `process_memory`. Apart from `cgroup_swap_bytes`, `cgroup_<kind>_bytes`
/// are the kinds in the cgroup's `memory.stat`, none when this kernel does not report one.
/// They overlap, so never add them to each other or to the `memory_*` fields: `kernel`
/// includes `slab`, `slab` includes `slab_reclaimable`, `anon_thp` is part of `anon` and
/// `file` includes `shmem`.
#[derive(Clone, Copy, Serialize, ToSchema)]
struct Resources {
    /// Cores busy on average since the previous sample; none on the first.
    cpu_cores: Option<f64>,
    /// The CPU quota, else the CPUs this process may run on.
    cpu_limit_cores: f64,
    /// Memory in use apart from the kernel cache: the process, kernel and socket buffers.
    memory_process_bytes: u64,
    /// Part of `memory_process_bytes` held by the in-memory segment cache.
    memory_segment_cache_bytes: u64,
    /// Configured size of the in-memory segment cache; 0 when it is off.
    memory_segment_cache_limit_bytes: u64,
    /// Page cache and reclaimable slab (dentries, inodes); the kernel may free it under
    /// memory pressure, but it still counts toward the cgroup limit.
    memory_kernel_cache_bytes: u64,
    /// `memory.max`, or the host's memory when that is lower or unset.
    memory_limit_bytes: u64,
    /// Whether `memory_limit_bytes` is a cgroup limit rather than the host's memory.
    memory_limited: bool,
    /// Sum of the segment lengths the in-memory cache holds, not capped like
    /// `memory_segment_cache_bytes`. Logical bytes, not resident memory.
    segment_cache_logical_bytes: u64,
    /// Configured size of the in-memory segment cache, also while it is off.
    segment_cache_capacity_bytes: u64,
    cgroup_anon_bytes: Option<u64>,
    cgroup_file_bytes: Option<u64>,
    cgroup_kernel_bytes: Option<u64>,
    cgroup_sock_bytes: Option<u64>,
    cgroup_slab_bytes: Option<u64>,
    cgroup_slab_reclaimable_bytes: Option<u64>,
    cgroup_anon_thp_bytes: Option<u64>,
    cgroup_shmem_bytes: Option<u64>,
    /// `memory.swap.current`, read once a minute with `process_memory`; none without swap
    /// accounting.
    cgroup_swap_bytes: Option<u64>,
    process_memory: ProcessMemory,
}

impl Resources {
    /// Fills in the in-memory segment cache fields from `cache`.
    fn set_segment_cache(&mut self, cache: &CacheStats) {
        self.memory_segment_cache_bytes = cache.memory_bytes.min(self.memory_process_bytes);
        self.memory_segment_cache_limit_bytes = if cache.memory_enabled {
            cache.memory_capacity
        } else {
            0
        };
        self.segment_cache_logical_bytes = cache.memory_bytes;
        self.segment_cache_capacity_bytes = cache.memory_capacity;
    }
}

/// This process alone, from `/proc/self/status` once a minute; none for a line this kernel
/// does not report.
#[derive(Clone, Copy, Default, Serialize, ToSchema)]
struct ProcessMemory {
    /// When it was read, Unix milliseconds.
    at: i64,
    /// Since the process last started; a restart from the UI resets it.
    uptime_ms: u64,
    /// `VmRSS`.
    rss_bytes: Option<u64>,
    /// `RssAnon`: the heap and other anonymous memory.
    rss_anon_bytes: Option<u64>,
    /// `RssFile`: mapped files such as the binary.
    rss_file_bytes: Option<u64>,
    /// `VmHWM`: the most `rss_bytes` has been since the process last started.
    rss_peak_bytes: Option<u64>,
    /// `VmSwap`.
    swap_bytes: Option<u64>,
    /// `Threads`.
    threads: Option<u32>,
}

#[derive(Clone, Serialize, ToSchema)]
struct LiveRead {
    id: u64,
    path: String,
    /// `fuse` or `webdav`.
    delivery: &'static str,
    client: String,
    opened_at: i64,
    size: u64,
    position: u64,
    served: u64,
    bps: u64,
    first_byte_ms: Option<u64>,
    stalls: u64,
    stall_ms: u64,
    seeks: u64,
    traced: bool,
    /// Read-ahead budget this read holds.
    budget_bytes: u64,
    /// How long the reader has waited for data; none while it is not waiting.
    waiting_ms: Option<u64>,
    /// FUSE requests on this read not answered yet, oldest first.
    pending: Vec<LivePending>,
    /// The segment the reader is blocked on.
    head: Option<LiveHead>,
    /// Providers that delivered articles to this read, in the order they first did.
    /// Cache hits and fetches shared with another read are not counted.
    sources: Vec<LiveSource>,
}

#[derive(Clone, Serialize, ToSchema)]
struct LiveSource {
    /// Provider display name.
    name: String,
    articles: u64,
    bytes: u64,
    /// Bytes per second delivered since the last sample.
    bps: u64,
}

#[derive(Clone, Serialize, ToSchema)]
struct LiveFuse {
    read_ahead_kb: Option<u64>,
    /// Requests the kernel hands over at once; the rest wait in the kernel.
    max_background: Option<u32>,
    congestion_threshold: Option<u32>,
    /// Requests the kernel holds that are not answered yet.
    waiting: Option<u64>,
}

#[derive(Clone, Serialize, ToSchema)]
struct LivePending {
    offset: u64,
    size: u32,
    age_ms: u64,
    /// `order` (behind earlier reads of the handle), `handle_lock` or `read`.
    stage: &'static str,
}

#[derive(Clone, Serialize, ToSchema)]
struct LiveHead {
    segment: u32,
    /// What its fetch waits on; none when it has no fetch of its own yet.
    stage: Option<&'static str>,
    /// Time in `stage`.
    stage_ms: Option<u64>,
    /// Provider key.
    provider: Option<String>,
    connection: Option<u32>,
    /// Waits on another read's fetch of the same article; the rest describes that fetch.
    joined: bool,
}

/// A provider connection with work on it.
#[derive(Clone, Serialize, ToSchema)]
struct LiveConnection {
    id: u32,
    /// `reply`, `transfer`, `bandwidth` or `draining`.
    phase: &'static str,
    phase_ms: u64,
    /// Since the last byte arrived; none before the first.
    since_byte_ms: Option<u64>,
    requests: usize,
}

#[derive(Clone, Serialize, ToSchema)]
struct LiveProvider {
    key: String,
    name: String,
    priority: u32,
    live: usize,
    busy: usize,
    /// Connections with at least one request on them; at most `max_connections`.
    active: usize,
    max_connections: usize,
    /// Cooldown left while the circuit breaker is open.
    open_for_ms: Option<u64>,
    bps: u64,
    /// Requests and requests that failed for the caller since the previous sample.
    requests: u64,
    failures: u64,
    /// Connections with work on them.
    connections: Vec<LiveConnection>,
    /// Connections being opened.
    connecting: usize,
}

/// How far back the overview looks.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, ToSchema)]
pub enum Window {
    #[serde(rename = "1h")]
    Hour,
    #[default]
    #[serde(rename = "24h")]
    Day,
    #[serde(rename = "7d")]
    Week,
    #[serde(rename = "30d")]
    Month,
    #[serde(rename = "all")]
    All,
}

impl Window {
    /// Span (none for all), row grain and bucket size; `all` sizes its buckets to the `kept`
    /// history.
    const fn shape(self, kept: i64) -> (Option<i64>, Grain, i64) {
        match self {
            Self::Hour => (Some(HOUR_MS), Grain::Minute, MINUTE_MS),
            Self::Day => (Some(DAY_MS), Grain::Minute, 15 * MINUTE_MS),
            Self::Week => (Some(7 * DAY_MS), Grain::Hour, 2 * HOUR_MS),
            Self::Month => (Some(30 * DAY_MS), Grain::Hour, 8 * HOUR_MS),
            Self::All if kept > DAILY_BARS * DAY_MS => (None, Grain::Hour, 7 * DAY_MS),
            Self::All => (None, Grain::Hour, DAY_MS),
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct Overview {
    window: Window,
    /// When this was built, Unix milliseconds: the last point's bucket has filled up to here.
    at: i64,
    bucket_ms: i64,
    points: Vec<Point>,
    totals: Totals,
    /// Since the metrics store was created.
    lifetime: Totals,
    providers: Vec<ProviderScore>,
    /// Provider traffic by who asked: `stream`, `queue` and `health`.
    workloads: Vec<WorkloadView>,
    rescues: Vec<RescueView>,
    /// Rescues in the window just before this one; none for `all`.
    previous_rescues: Option<u64>,
    records: RecordsView,
    catalogue: CatalogueView,
    /// Newest first.
    reads: Vec<RecentRead>,
    /// Files with the most bytes served in the window (at most 90 days), most first.
    top_reads: Vec<TopRead>,
    /// Reads in the window (at most 90 days) by program, most bytes first.
    apps: Vec<AppUsage>,
    /// Newest first.
    breaker_events: Vec<BreakerView>,
    writer_error: Option<String>,
}

/// Bytes and requests in one bucket starting at `ts`.
#[derive(Clone, Copy, Default, Serialize, ToSchema)]
struct Point {
    ts: i64,
    served: u64,
    fetched: u64,
    /// `fetched` by workload.
    stream_bytes: u64,
    queue_bytes: u64,
    health_bytes: u64,
    /// Fastest 2 s read rate in the bucket, bytes per second.
    peak_bps: u64,
    /// Fastest 2 s rate from providers in the bucket, bytes per second.
    peak_fetch_bps: u64,
    /// Finished requests by workload, cancelled ones left out.
    stream: u64,
    queue: u64,
    health: u64,
    /// Requests that failed for the caller after every retry and fallback; missing
    /// articles not counted.
    errors: u64,
    /// Articles a provider did not have.
    missing: u64,
    rescues: u64,
    /// Finished reads and their stalls.
    reads: u64,
    stalls: u64,
    imports: u64,
    import_failures: u64,
    /// Health checks, any result.
    checks: u64,
    unhealthy: u64,
    cache_hits: u64,
    cache_misses: u64,
}

/// One provider's bucket, aligned with [`Point`].
#[derive(Clone, Copy, Default, Serialize, ToSchema)]
struct ProviderPoint {
    requests: u64,
    bytes: u64,
    failures: u64,
    missing: u64,
    /// Circuit breaker openings.
    trips: u64,
}

#[derive(Default, Serialize, ToSchema)]
struct Mark {
    /// Unix milliseconds, start of the hour or UTC day.
    at: i64,
    bytes: u64,
}

/// Busiest kept hour and day by bytes from providers.
#[derive(Default, Serialize, ToSchema)]
struct RecordsView {
    busiest_hour: Option<Mark>,
    busiest_day: Option<Mark>,
    /// Oldest hour of history kept.
    since: Option<i64>,
}

#[derive(Default, Serialize, ToSchema)]
struct CatalogueView {
    files: u64,
    bytes: u64,
    largest: u64,
    added_7d: u64,
}

#[derive(Default, Serialize, ToSchema)]
struct Totals {
    served: u64,
    fetched: u64,
    /// Fastest 2 s rate from providers, bytes per second.
    peak_fetch_bps: u64,
    /// Finished requests, cancelled ones left out.
    requests: u64,
    retrieved: u64,
    missing: u64,
    failures: u64,
    reads: u64,
    /// Time files were open, summed over finished reads.
    read_ms: u64,
    stalls: u64,
    stall_ms: u64,
    seeks: u64,
    first_byte_ms_avg: Option<u64>,
    imports: u64,
    import_failures: u64,
    import_bytes: u64,
    /// Time spent importing, summed over completed and failed imports.
    import_ms: u64,
    /// Imports put back in the queue to try again.
    import_retries: u64,
    healthy: u64,
    unhealthy: u64,
    /// Checks that could not decide, for example when every provider failed.
    inconclusive: u64,
    repaired: u64,
    rescues: u64,
    trips: u64,
    cache_hits: u64,
    cache_misses: u64,
}

#[derive(Serialize, ToSchema)]
struct ProviderScore {
    key: String,
    /// The key when the provider is no longer configured.
    name: String,
    bytes: u64,
    /// Finished requests, cancelled ones left out.
    attempts: u64,
    missing: u64,
    /// Requests that failed for the caller, ending on this provider's error.
    failures: u64,
    /// Attempts that failed but were retried or sent to another provider.
    retried: u64,
    trips: u64,
    connect_failures: u64,
    /// Fastest 2 s rate, bytes per second.
    peak_bps: u64,
    /// Time spent receiving; `bytes` over this is the average rate while busy.
    active_ms: u64,
    /// Playback request latency percentiles, microseconds.
    p50_us: Option<u64>,
    p95_us: Option<u64>,
    p99_us: Option<u64>,
    /// Aligned with the overview's points.
    points: Vec<ProviderPoint>,
}

#[derive(Default, Serialize, ToSchema)]
struct WorkloadView {
    /// `stream`, `queue` or `health`.
    workload: &'static str,
    bytes: u64,
    /// Finished requests, cancelled ones left out.
    requests: u64,
    missing: u64,
    failures: u64,
    /// Request latency percentiles, microseconds.
    p50_us: Option<u64>,
    p95_us: Option<u64>,
}

#[derive(Serialize, ToSchema)]
struct RescueView {
    from: String,
    to: String,
    /// How the first provider's attempt ended.
    reason: String,
    count: u64,
}

#[derive(Serialize, ToSchema)]
pub struct RecentRead {
    /// None for reads recorded before items were.
    item_id: Option<Uuid>,
    opened_at: i64,
    closed_at: i64,
    path: String,
    delivery: String,
    client: String,
    size: i64,
    served: i64,
    first_byte_ms: Option<i64>,
    stalls: i64,
    stall_ms: i64,
    seeks: i64,
    /// `eof` when read to the end, else `closed`.
    end_reason: String,
}

#[derive(Serialize, ToSchema)]
struct TopRead {
    item_id: Uuid,
    /// Path of the newest read.
    path: String,
    reads: u64,
    served: u64,
    last_read_at: i64,
}

#[derive(Serialize, ToSchema)]
struct AppUsage {
    /// `fuse` or `webdav`.
    delivery: String,
    /// The process name, or the `WebDAV` user agent's first product without its
    /// version; empty when the process was hidden.
    app: String,
    reads: u64,
    served: u64,
    /// Time files were open, summed over reads.
    open_ms: u64,
}

#[derive(Serialize, ToSchema)]
struct BreakerView {
    at: i64,
    /// The provider's key; none on outages stored before keys were.
    provider_key: Option<String>,
    /// Display name when it opened.
    provider: String,
    /// How long it stayed open; up to now while it still is.
    open_ms: i64,
}

/// A provider's open breaker, since `at`.
#[derive(Clone)]
struct Outage {
    at: i64,
    name: String,
}

/// Starts outages for providers in `now_open` (key, name) and ends the rest at `at`.
fn track_outages<'a>(
    open: &mut HashMap<String, Outage>,
    now_open: impl IntoIterator<Item = (&'a str, &'a str)>,
    at: i64,
) -> Vec<BreakerEvent> {
    let now_open: HashSet<&str> = now_open
        .into_iter()
        .map(|(key, name)| {
            open.entry(key.to_owned()).or_insert_with(|| Outage {
                at,
                name: name.to_owned(),
            });
            key
        })
        .collect();
    let mut ended = Vec::new();
    open.retain(|key, o| {
        let still = now_open.contains(key.as_str());
        if !still {
            ended.push(o.event(key, at));
        }
        still
    });
    ended
}

impl Outage {
    fn event(&self, key: &str, now: i64) -> BreakerEvent {
        BreakerEvent {
            at: self.at,
            provider_key: Some(key.to_owned()),
            provider: self.name.clone(),
            open_ms: (now - self.at).max(0),
        }
    }
}

impl Metrics {
    /// Metrics with history in the store at `path`, opened by [`run`](Self::run).
    pub fn new(path: &Path, sources: Sources) -> Self {
        Self {
            sources,
            path: path.to_path_buf(),
            store: OnceLock::new(),
            cgroup: Cgroup::find(),
            state: Mutex::default(),
            live: watch::channel(Arc::default()).0,
        }
    }

    /// Opens the store; without it only live numbers work. Its integrity check reads the
    /// whole file, so this runs in the metrics task rather than holding up startup.
    async fn open(&self) {
        let path = &self.path;
        match Store::open(path).await {
            Ok(store) => {
                if store.recreated() {
                    warn!(path = %path.display(), reason = "the file was corrupt",
                        "started a new metrics store; the old file was kept as .unreadable");
                }
                // Only `run` sets it, once.
                let _ = self.store.set(store);
            }
            Err(error) => {
                let reason = match &error {
                    infinidysk_db::Error::Migrate(e) => format!(
                        "its schema does not match this version ({e}); the file was left as \
                         is, move it away to start a new store"
                    ),
                    error => error.to_string(),
                };
                warn!(path = %path.display(), reason = %reason,
                    "metrics history is off; live numbers still work");
                self.state().error = Some(format!(
                    "The metrics store at {} could not be opened: {reason}",
                    path.display()
                ));
            }
        }
    }

    pub fn live(&self) -> Arc<LiveView> {
        Arc::clone(&self.live.borrow())
    }

    /// The history store, once [`run`](Self::run) opened it.
    pub fn store(&self) -> Option<&Store> {
        self.store.get()
    }

    pub fn subscribe(&self) -> watch::Receiver<Arc<LiveView>> {
        self.live.subscribe()
    }

    /// Opens the store, then samples every 2 s and writes each finished minute until
    /// `cancel`, then writes the rest and closes the store.
    pub async fn run(&self, cancel: CancellationToken) {
        self.open().await;
        let mut tick = tokio::time::interval(SAMPLE_EVERY);
        tick.set_missed_tick_behavior(MissedTickBehavior::Delay);
        let mut minute = now_ms() / MINUTE_MS;
        self.state().pending_minute = minute * MINUTE_MS;
        let mut pruned = None;
        loop {
            tokio::select! {
                () = cancel.cancelled() => break,
                _ = tick.tick() => {}
            }
            self.sample();
            let now = now_ms();
            if now / MINUTE_MS == minute {
                continue;
            }
            self.write(minute * MINUTE_MS).await;
            minute = now / MINUTE_MS;
            self.state().pending_minute = minute * MINUTE_MS;
            if pruned != Some(now / HOUR_MS) {
                let first = pruned.is_none();
                pruned = Some(now / HOUR_MS);
                if let Some(store) = self.store.get() {
                    if let Err(error) = store.prune(now).await {
                        warn!(reason = %error, "could not prune old metrics");
                    }
                    if (first || (now / HOUR_MS) % OPTIMIZE_HOURS == 0)
                        && let Err(error) = store.optimize().await
                    {
                        warn!(reason = %error, "could not refresh metrics statistics");
                    }
                }
            }
        }
        self.sample();
        self.end_outages();
        self.write(minute * MINUTE_MS).await;
        if let Some(store) = self.store.get() {
            store.close().await;
        }
    }

    /// Saves the outages still open as ending now, so a shutdown keeps them.
    fn end_outages(&self) {
        let mut state = self.state();
        let ended = track_outages(&mut state.open, [], now_ms());
        state.pending.events.extend(ended);
    }

    fn state(&self) -> MutexGuard<'_, Sampled> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Counters since start, keyed like the store.
    fn totals(&self, providers: &[ProviderStats]) -> HashMap<String, u64> {
        let mut t = HashMap::new();
        let sessions = self.sources.vfs.sessions();
        for d in Delivery::ALL {
            t.insert(format!("served.{}", d.label()), sessions.served(d));
        }
        for p in providers {
            for (w, s) in Workload::ALL.iter().zip(&p.workloads) {
                let base = format!("p.{}.{}", p.key, w.label());
                t.insert(format!("{base}.bytes"), s.bytes);
                t.insert(format!("{base}.failed"), s.failed);
                for (o, n) in Outcome::ALL.iter().zip(s.attempts) {
                    t.insert(format!("{base}.{}", o.label()), n);
                }
                for (i, n) in s.latency.iter().enumerate() {
                    t.insert(format!("{base}.lat.{i}"), *n);
                }
            }
            t.insert(format!("p.{}.connect_failures", p.key), p.connect_failures);
            t.insert(format!("p.{}.trips", p.key), p.trips);
        }
        let keys: HashMap<u16, &str> = providers.iter().map(|p| (p.id, p.key.as_str())).collect();
        for r in self.sources.client.rescues() {
            if let (Some(from), Some(to)) = (keys.get(&r.from), keys.get(&r.to)) {
                *t.entry(format!("rescue.{from}.{to}.{}", r.reason.label()))
                    .or_default() += r.count;
            }
        }
        let c = self.sources.cache.stats();
        let q = self.sources.queue.stats();
        let h = self.sources.health.stats();
        for (key, value) in [
            ("cache.memory_hits", c.memory_hits),
            ("cache.disk_hits", c.disk_hits),
            ("cache.joined", c.joined),
            ("cache.misses", c.misses),
            ("cache.bytes_served", c.bytes_served),
            ("queue.completed", q.completed),
            ("queue.failed", q.failed),
            ("queue.retried", q.retried),
            ("queue.bytes", q.bytes),
            ("queue.import_ms", q.import_ms),
            ("health.healthy", h.healthy),
            ("health.unhealthy", h.unhealthy),
            ("health.inconclusive", h.inconclusive),
            ("health.repaired", h.repaired),
        ] {
            t.insert(key.to_owned(), value);
        }
        t
    }

    fn sample(&self) {
        let now = Instant::now();
        let at = now_ms();
        let providers = self.sources.client.stats();
        let totals = self.totals(&providers);
        let sessions = self.sources.vfs.sessions();
        let ended = sessions.take_ended();
        let open = sessions.open();
        let last = self.state().slow;
        let resources = self
            .cgroup
            .as_ref()
            .map_err(String::clone)
            .and_then(|cgroup| {
                let (usec, r) = cgroup.read()?;
                let status = Path::new("/proc/self/status");
                Ok((usec, r, cgroup.read_slow(status, last, now)))
            });

        let mut guard = self.state();
        let state = &mut *guard;
        let elapsed_ms = state.last_at.map_or(0, |then| {
            u64::try_from((now - then).as_millis()).unwrap_or(u64::MAX)
        });
        state.last_at = Some(now);
        let (resources, resources_unavailable) = match resources {
            Ok((usec, mut r, slow)) => {
                r.cpu_cores = state.cpu.replace((now, usec)).and_then(|(then, prev)| {
                    let wall = (now - then).as_secs_f64();
                    let busy = u32::try_from(usec.saturating_sub(prev)).unwrap_or(u32::MAX);
                    (wall > 0.0).then(|| f64::from(busy) / 1e6 / wall)
                });
                r.set_segment_cache(&self.sources.cache.stats());
                r.cgroup_swap_bytes = slow.cgroup_swap_bytes;
                r.process_memory = slow.process;
                state.slow = Some(slow);
                (Some(r), None)
            }
            Err(reason) => {
                state.cpu = None;
                (None, Some(reason))
            }
        };
        let rate = |bytes: u64| {
            bytes
                .saturating_mul(1000)
                .checked_div(elapsed_ms)
                .unwrap_or(0)
        };

        let mut deltas = HashMap::new();
        for (key, &value) in &totals {
            let prev = state.last.get(key).copied().unwrap_or(0);
            // A counter that went down restarted (providers were replaced).
            let delta = value.checked_sub(prev).unwrap_or(value);
            if delta > 0 {
                state.pending.add(key, delta);
                deltas.insert(key.as_str(), delta);
            }
        }
        let get = |key: &str| deltas.get(key).copied().unwrap_or(0);

        let read_bps = rate(
            Delivery::ALL
                .iter()
                .map(|d| get(&format!("served.{}", d.label())))
                .sum(),
        );
        let mut fetched = 0;
        let mut open_now = Vec::new();
        let mut live_providers = Vec::with_capacity(providers.len());
        for p in &providers {
            let (mut bytes, mut requests, mut failures) = (0, 0, 0);
            for w in Workload::ALL {
                let base = format!("p.{}.{}", p.key, w.label());
                bytes += get(&format!("{base}.bytes"));
                failures += get(&format!("{base}.failed"));
                for o in Outcome::ALL {
                    requests += get(&format!("{base}.{}", o.label()));
                }
            }
            fetched += bytes;
            if bytes > 0 {
                state
                    .pending
                    .add(&format!("p.{}.active_ms", p.key), elapsed_ms);
                state
                    .pending
                    .add(&format!("peak.p.{}.bps", p.key), rate(bytes));
            }
            if p.open_for.is_some() {
                open_now.push((p.key.as_str(), p.name.as_str()));
            }
            live_providers.push(LiveProvider {
                key: p.key.clone(),
                name: p.name.clone(),
                priority: p.priority,
                live: p.live,
                busy: p.busy,
                active: p.active,
                max_connections: p.max_connections,
                open_for_ms: p
                    .open_for
                    .map(|d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX)),
                bps: rate(bytes),
                requests,
                failures,
                connections: p
                    .connections
                    .iter()
                    .filter(|c| c.phase != Phase::Idle)
                    .map(|c| LiveConnection {
                        id: c.id,
                        phase: c.phase.label(),
                        phase_ms: ms(c.elapsed),
                        since_byte_ms: c.since_byte.map(ms),
                        requests: c.requests,
                    })
                    .collect(),
                connecting: p.connecting,
            });
        }
        for ended in track_outages(&mut state.open, open_now, at) {
            if state.pending.events.len() < MAX_PENDING {
                state.pending.events.push(ended);
            }
        }

        for s in &ended {
            let r = record(s);
            let pending = &mut state.pending;
            pending.add("reads.ended", 1);
            pending.add(
                "reads.open_ms",
                (r.closed_at - r.opened_at).max(0).cast_unsigned(),
            );
            pending.add("reads.stalls", r.stalls.cast_unsigned());
            pending.add("reads.stall_ms", r.stall_ms.cast_unsigned());
            pending.add("reads.seeks", r.seeks.cast_unsigned());
            if let Some(ms) = r.first_byte_ms {
                pending.add("reads.first_byte_ms", ms.cast_unsigned());
                pending.add("reads.first_bytes", 1);
            }
            if pending.sessions.len() < MAX_PENDING {
                pending.sessions.push(r);
            }
        }

        let mut served = HashMap::with_capacity(open.len());
        let mut supplied = HashMap::new();
        let names = self.sources.client.provider_names();
        let provider_key: HashMap<u16, &str> =
            providers.iter().map(|p| (p.id, p.key.as_str())).collect();
        let reads: Vec<LiveRead> = open
            .iter()
            .map(|s| {
                let now_served = s.served();
                let bps = state
                    .served
                    .get(&s.id)
                    .map_or(0, |&prev| rate(now_served.saturating_sub(prev)));
                served.insert(s.id, now_served);
                let (stalls, stall) = s.stalls();
                LiveRead {
                    id: s.id,
                    path: s.path.clone(),
                    delivery: s.delivery.label(),
                    client: s.client.clone(),
                    opened_at: unix_ms(s.opened_at),
                    size: s.size,
                    position: s.pos(),
                    served: now_served,
                    bps,
                    first_byte_ms: s
                        .first_byte()
                        .map(|d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX)),
                    stalls,
                    stall_ms: u64::try_from(stall.as_millis()).unwrap_or(u64::MAX),
                    seeks: s.seeks(),
                    traced: s.trace.is_some(),
                    budget_bytes: s.watch().budget(),
                    waiting_ms: s.waiting().map(ms),
                    pending: s
                        .requests()
                        .into_iter()
                        .map(|r| LivePending {
                            offset: r.offset,
                            size: r.size,
                            age_ms: ms(r.age),
                            stage: r.stage.label(),
                        })
                        .collect(),
                    head: s.watch().head().map(|(segment, wait)| LiveHead {
                        segment,
                        stage: wait.map(|w| w.stage.label()),
                        stage_ms: wait.map(|w| ms(w.elapsed)),
                        provider: wait
                            .and_then(|w| w.provider)
                            .and_then(|p| provider_key.get(&p))
                            .map(|k| (*k).to_owned()),
                        connection: wait.and_then(|w| w.connection),
                        joined: wait.is_some_and(|w| w.joined),
                    }),
                    sources: s
                        .watch()
                        .supplied()
                        .into_iter()
                        .map(|p| {
                            let key = (s.id, p.provider);
                            let bps = state
                                .supplied
                                .get(&key)
                                .map_or(0, |&prev| rate(p.bytes.saturating_sub(prev)));
                            supplied.insert(key, p.bytes);
                            LiveSource {
                                name: names
                                    .get(usize::from(p.provider))
                                    .cloned()
                                    .unwrap_or_default(),
                                articles: p.articles,
                                bytes: p.bytes,
                                bps,
                            }
                        })
                        .collect(),
                }
            })
            .collect();
        state.served = served;
        state.supplied = supplied;
        state.pending.add("peak.read_bps", read_bps);
        state.pending.add("peak.fetch_bps", rate(fetched));
        state.pending.add("peak.reads", open.len() as u64);
        let (requests, failures) = live_providers
            .iter()
            .fold((0, 0), |(r, f), p| (r + p.requests, f + p.failures));
        let read_bytes = Delivery::ALL
            .iter()
            .map(|d| get(&format!("served.{}", d.label())))
            .sum();
        state.last = totals;
        if state.minute.len() == SAMPLES_PER_MINUTE {
            state.minute.pop_front();
        }
        state.minute.push_back((requests, failures, read_bytes));
        let minute = state
            .minute
            .iter()
            .fold((0, 0, 0), |(r, f, b), m| (r + m.0, f + m.1, b + m.2));
        let budget = self.sources.vfs.budget();
        let article_cap = u64::from(budget.capacity());
        let budget_waiters = budget.waiters();
        let fuse = self.sources.mount.borrow().as_ref().ok().map(|k| LiveFuse {
            read_ahead_kb: k.read_ahead_kb,
            max_background: k.max_background,
            congestion_threshold: k.congestion_threshold,
            waiting: k.waiting(),
        });
        watch_stalls(
            state,
            &reads,
            &live_providers,
            fuse.as_ref(),
            budget_waiters,
            now,
        );
        let view = LiveView {
            at,
            read_bps,
            fetch_bps: rate(fetched),
            requests_minute: minute.0,
            failures_minute: minute.1,
            read_minute: minute.2,
            article_bytes: article_cap.saturating_sub(budget.available() as u64),
            article_cap,
            budget_waiters,
            fuse,
            reads,
            imports: self
                .sources
                .queue
                .progress()
                .into_iter()
                .map(|(id, p)| LiveImport {
                    id: id.0,
                    percent: p.percent,
                    step: p.step.label(),
                    eta_ms: p
                        .eta
                        .map(|d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX)),
                })
                .collect(),
            maintenance: self.sources.maintenance.get(),
            providers: live_providers,
            permits_free: self.sources.client.free_permits(),
            resources,
            resources_unavailable,
            writer_error: state.error.clone(),
        };
        drop(guard);
        self.live.send_replace(Arc::new(view));
    }

    /// Writes what is pending as the minute starting at `minute_ms`; on failure keeps it
    /// for the next try.
    async fn write(&self, minute_ms: i64) {
        let (pending, mut events) = {
            let mut state = self.state();
            let now = now_ms();
            // Open outages are saved too, so a crash keeps them up to the last write.
            let open: Vec<BreakerEvent> = state
                .open
                .iter()
                .map(|(key, o)| o.event(key, now))
                .collect();
            (std::mem::take(&mut state.pending), open)
        };
        let Some(store) = self.store.get() else {
            return;
        };
        if pending.is_empty() && events.is_empty() {
            return;
        }
        let counters: Vec<_> = pending
            .counters
            .iter()
            .map(|(k, v)| (k.clone(), *v))
            .collect();
        events.extend(pending.events.iter().cloned());
        let written = store
            .write(minute_ms, &counters, &pending.sessions, &events)
            .await;
        let mut state = self.state();
        match written {
            Ok(()) => {
                if state.error.take().is_some() {
                    info!("metrics history is being saved again");
                }
            }
            Err(error) => {
                if state.error.is_none() {
                    warn!(reason = %error,
                        "could not save metrics history; keeping it in memory and retrying");
                }
                state.error = Some(format!("Metrics history could not be saved: {error}"));
                let newer = std::mem::replace(&mut state.pending, pending);
                state.pending.merge(newer);
            }
        }
    }

    pub async fn overview(&self, window: Window) -> Result<Overview, infinidysk_db::Error> {
        let now = now_ms();
        let records = match self.store.get() {
            Some(store) => store.records().await?,
            None => Records::default(),
        };
        let kept = records.first_hour.map_or(0, |first| now - first);
        let (span, grain, bucket) = window.shape(kept);
        let since = span.map_or(0, |span| {
            let since = now - span;
            since - since.rem_euclid(bucket)
        });
        let (rows, lifetime, stored_reads, top, usage, events, previous_rescues) =
            match self.store.get() {
                Some(store) => (
                    store.series(grain, since, bucket).await?,
                    store.lifetime().await?,
                    store.sessions(RECENT_READS).await?,
                    store.top_items(since, TOP_READS).await?,
                    store.usage(since).await?,
                    store.breaker_events(since).await?,
                    match span {
                        Some(span) => Some(store.sum(grain, since - span, since, "rescue.").await?),
                        None => None,
                    },
                ),
                None => Default::default(),
            };
        let catalogue = self.sources.vfs.db().catalogue(now - 7 * DAY_MS).await?;
        let (pending, error, at, pending_minute, open) = {
            let state = self.state();
            (
                state.pending.clone(),
                state.error.clone(),
                now_ms(),
                state.pending_minute,
                state.open.clone(),
            )
        };

        let current = at - at.rem_euclid(bucket);
        let mut points = BTreeMap::new();
        let mut sparks: HashMap<&str, BTreeMap<i64, ProviderPoint>> = HashMap::new();
        let mut sums = HashMap::new();
        let unwritten_at = pending_minute - pending_minute.rem_euclid(bucket);
        let unwritten = pending.counters.iter().map(|(k, v)| (unwritten_at, k, *v));
        for (ts, key, value) in rows.iter().map(|(t, k, v)| (*t, k, *v)).chain(unwritten) {
            tally(points.entry(ts).or_default(), key, value);
            if let Some((provider, field)) = provider_field(key) {
                let spark = sparks.entry(provider).or_default().entry(ts).or_default();
                match field {
                    Field::Bytes(_) => spark.bytes += value,
                    Field::Attempt(_, Outcome::Cancelled) => {}
                    Field::Attempt(_, outcome) => {
                        spark.requests += value;
                        if outcome == Outcome::NotFound {
                            spark.missing += value;
                        }
                    }
                    Field::Failed(_) => spark.failures += value,
                    Field::Trips => spark.trips += value,
                    _ => {}
                }
            }
            merge(&mut sums, key, value);
        }
        let first = points.keys().next().copied().unwrap_or(current);
        let start = if span.is_some() { since } else { first };
        let stamps: Vec<i64> = (start..=current)
            .step_by(usize::try_from(bucket).unwrap_or(usize::MAX))
            .collect();
        let points = stamps
            .iter()
            .map(|&ts| Point {
                ts,
                ..points.get(&ts).copied().unwrap_or_default()
            })
            .collect();
        let name = name_of(&self.sources.client);
        let mut providers = scores(&sums, &name);
        for p in &mut providers {
            let spark = sparks.get(p.key.as_str());
            p.points = stamps
                .iter()
                .map(|ts| spark.and_then(|s| s.get(ts)).copied().unwrap_or_default())
                .collect();
        }

        let mut lifetime: HashMap<String, u64> = lifetime.into_iter().collect();
        for (key, value) in &pending.counters {
            merge(&mut lifetime, key, *value);
        }

        let mut reads: Vec<RecentRead> = pending
            .sessions
            .iter()
            .rev()
            .chain(&stored_reads)
            .take(RECENT_READS as usize)
            .map(recent_read)
            .collect();
        reads.sort_by_key(|r| std::cmp::Reverse(r.closed_at));
        let ongoing: Vec<BreakerEvent> = open.iter().map(|(key, o)| o.event(key, at)).collect();
        let mut seen = HashSet::new();
        let mut breaker_events: Vec<BreakerView> = pending
            .events
            .iter()
            .rev()
            .chain(&ongoing)
            .chain(&events)
            // The store holds open outages as of the last write; the copy in memory is newer.
            .filter(|e| e.provider_key.is_none() || seen.insert((e.provider_key.clone(), e.at)))
            .map(|e| BreakerView {
                at: e.at,
                provider_key: e.provider_key.clone(),
                provider: e.provider.clone(),
                open_ms: e.open_ms,
            })
            .collect();
        breaker_events.sort_by_key(|e| std::cmp::Reverse(e.at));

        Ok(Overview {
            window,
            at,
            bucket_ms: bucket,
            points,
            totals: totals(&sums),
            lifetime: totals(&lifetime),
            providers,
            workloads: workloads(&sums),
            rescues: rescues(&sums, &name),
            previous_rescues,
            records: RecordsView {
                busiest_hour: records.busiest_hour.map(|(at, bytes)| Mark { at, bytes }),
                busiest_day: records.busiest_day.map(|(at, bytes)| Mark { at, bytes }),
                since: records.first_hour,
            },
            catalogue: CatalogueView {
                files: catalogue.files,
                bytes: catalogue.bytes,
                largest: catalogue.largest,
                added_7d: catalogue.added,
            },
            reads,
            top_reads: top
                .into_iter()
                .map(|t| TopRead {
                    item_id: t.item.0,
                    path: t.path,
                    reads: t.reads,
                    served: t.served,
                    last_read_at: t.last_closed_at,
                })
                .collect(),
            apps: apps(usage),
            breaker_events,
            writer_error: error,
        })
    }

    /// The newest finished reads of one item, unsaved ones included.
    async fn item_reads(&self, item: ItemId) -> Result<Vec<RecentRead>, infinidysk_db::Error> {
        let stored = match self.store.get() {
            Some(store) => store.item_sessions(item, FILE_READS).await?,
            None => Vec::new(),
        };
        let pending: Vec<SessionRecord> = self
            .state()
            .pending
            .sessions
            .iter()
            .rev()
            .filter(|s| s.item == Some(item))
            .cloned()
            .collect();
        Ok(pending
            .iter()
            .chain(&stored)
            .take(FILE_READS as usize)
            .map(recent_read)
            .collect())
    }

    /// Prometheus text exposition, built from the counters since start.
    pub fn prometheus(&self) -> String {
        let mut out = String::new();
        let sessions = self.sources.vfs.sessions();
        family(
            &mut out,
            "served_bytes_total",
            "counter",
            "Bytes returned to readers.",
        );
        for d in Delivery::ALL {
            let _ = writeln!(
                out,
                "infinidysk_served_bytes_total{{delivery=\"{}\"}} {}",
                d.label(),
                sessions.served(d)
            );
        }
        family(&mut out, "open_reads", "gauge", "Files being read now.");
        let _ = writeln!(out, "infinidysk_open_reads {}", sessions.open().len());

        let providers = self.sources.client.stats();
        family(
            &mut out,
            "provider_bytes_total",
            "counter",
            "Decoded bytes received.",
        );
        for p in &providers {
            for (w, s) in Workload::ALL.iter().zip(&p.workloads) {
                let _ = writeln!(
                    out,
                    "infinidysk_provider_bytes_total{{provider=\"{}\",workload=\"{}\"}} {}",
                    escape(&p.name),
                    w.label(),
                    s.bytes
                );
            }
        }
        family(
            &mut out,
            "provider_requests_total",
            "counter",
            "Requests by outcome.",
        );
        for p in &providers {
            for (w, s) in Workload::ALL.iter().zip(&p.workloads) {
                for (o, n) in Outcome::ALL.iter().zip(s.attempts) {
                    let _ = writeln!(
                        out,
                        "infinidysk_provider_requests_total{{provider=\"{}\",workload=\"{}\",\
                         outcome=\"{}\"}} {n}",
                        escape(&p.name),
                        w.label(),
                        o.label()
                    );
                }
            }
        }
        family(
            &mut out,
            "provider_failed_requests_total",
            "counter",
            "Requests that failed for the caller after every retry and fallback, by the \
             provider whose error ended them. Missing articles are not counted.",
        );
        for p in &providers {
            for (w, s) in Workload::ALL.iter().zip(&p.workloads) {
                let _ = writeln!(
                    out,
                    "infinidysk_provider_failed_requests_total{{provider=\"{}\",workload=\"{}\"}} {}",
                    escape(&p.name),
                    w.label(),
                    s.failed
                );
            }
        }
        // ponytail: buckets carry no sum of durations, so `_sum` is left out; add one to
        // the provider stats if a dashboard needs averages.
        family(
            &mut out,
            "provider_latency_seconds",
            "histogram",
            "Request latency, cancelled requests left out.",
        );
        for p in &providers {
            for (w, s) in Workload::ALL.iter().zip(&p.workloads) {
                let labels = format!(
                    "provider=\"{}\",workload=\"{}\"",
                    escape(&p.name),
                    w.label()
                );
                let mut seen = 0;
                for (i, n) in s.latency.iter().enumerate().take(LATENCY_BUCKETS - 1) {
                    seen += n;
                    let le = f64::from(1_u32 << i) / 1e6;
                    let _ = writeln!(
                        out,
                        "infinidysk_provider_latency_seconds_bucket{{{labels},le=\"{le}\"}} {seen}"
                    );
                }
                let count: u64 = s.latency.iter().sum();
                let _ = writeln!(
                    out,
                    "infinidysk_provider_latency_seconds_bucket{{{labels},le=\"+Inf\"}} {count}"
                );
                let _ = writeln!(
                    out,
                    "infinidysk_provider_latency_seconds_count{{{labels}}} {count}"
                );
            }
        }
        for (name, kind, help, value) in [
            (
                "provider_connections",
                "gauge",
                "Open connections.",
                provider_gauge(|p| p.live as u64),
            ),
            (
                "provider_busy",
                "gauge",
                "Requests in flight or queued.",
                provider_gauge(|p| p.busy as u64),
            ),
            (
                "provider_max_connections",
                "gauge",
                "Connection limit.",
                provider_gauge(|p| p.max_connections as u64),
            ),
            (
                "provider_circuit_open",
                "gauge",
                "1 while the circuit breaker is open.",
                provider_gauge(|p| u64::from(p.open_for.is_some())),
            ),
            (
                "provider_circuit_trips_total",
                "counter",
                "Times the circuit breaker opened.",
                provider_gauge(|p| p.trips),
            ),
            (
                "provider_connect_failures_total",
                "counter",
                "Failed connection attempts.",
                provider_gauge(|p| p.connect_failures),
            ),
        ] {
            family(&mut out, name, kind, help);
            for p in &providers {
                let _ = writeln!(
                    out,
                    "infinidysk_{name}{{provider=\"{}\"}} {}",
                    escape(&p.name),
                    value(p)
                );
            }
        }

        let names: HashMap<u16, &str> = providers.iter().map(|p| (p.id, p.name.as_str())).collect();
        family(
            &mut out,
            "provider_rescues_total",
            "counter",
            "Segments a later provider served after an earlier one missed or failed.",
        );
        for r in self.sources.client.rescues() {
            if let (Some(from), Some(to)) = (names.get(&r.from), names.get(&r.to)) {
                let _ = writeln!(
                    out,
                    "infinidysk_provider_rescues_total{{from=\"{}\",to=\"{}\",reason=\"{}\"}} {}",
                    escape(from),
                    escape(to),
                    r.reason.label(),
                    r.count
                );
            }
        }
        if let Some(free) = self.sources.client.free_permits() {
            family(
                &mut out,
                "download_permits_free",
                "gauge",
                "Global download permits not in use.",
            );
            let _ = writeln!(out, "infinidysk_download_permits_free {free}");
        }

        let c = self.sources.cache.stats();
        family(
            &mut out,
            "cache_hits_total",
            "counter",
            "Segments served without a provider request of their own.",
        );
        for (tier, n) in [
            ("memory", c.memory_hits),
            ("disk", c.disk_hits),
            ("shared", c.joined),
        ] {
            let _ = writeln!(out, "infinidysk_cache_hits_total{{tier=\"{tier}\"}} {n}");
        }
        let q = self.sources.queue.stats();
        let h = self.sources.health.stats();
        for (name, help, value) in [
            (
                "cache_misses_total",
                "Segments fetched from providers.",
                c.misses,
            ),
            (
                "cache_bytes_served_total",
                "Bytes served from the cache.",
                c.bytes_served,
            ),
            (
                "health_repairs_total",
                "Unhealthy files an Arr is replacing.",
                h.repaired,
            ),
        ] {
            family(&mut out, name, "counter", help);
            let _ = writeln!(out, "infinidysk_{name} {value}");
        }
        family(
            &mut out,
            "imports_total",
            "counter",
            "Finished import attempts.",
        );
        for (result, n) in [
            ("completed", q.completed),
            ("failed", q.failed),
            ("retried", q.retried),
        ] {
            let _ = writeln!(out, "infinidysk_imports_total{{result=\"{result}\"}} {n}");
        }
        family(
            &mut out,
            "health_checks_total",
            "counter",
            "Health checks by result.",
        );
        for (result, n) in [
            ("healthy", h.healthy),
            ("unhealthy", h.unhealthy),
            ("inconclusive", h.inconclusive),
        ] {
            let _ = writeln!(
                out,
                "infinidysk_health_checks_total{{result=\"{result}\"}} {n}"
            );
        }
        out
    }
}

/// Lets the table in [`Metrics::prometheus`] hold plain function pointers.
const fn provider_gauge(f: fn(&ProviderStats) -> u64) -> fn(&ProviderStats) -> u64 {
    f
}

fn family(out: &mut String, name: &str, kind: &str, help: &str) {
    let _ = writeln!(
        out,
        "# HELP infinidysk_{name} {help}\n# TYPE infinidysk_{name} {kind}"
    );
}

fn escape(label: &str) -> String {
    label
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
}

fn merge(sums: &mut HashMap<String, u64>, key: &str, value: u64) {
    let slot = sums.entry(key.to_owned()).or_default();
    *slot = if key.starts_with("peak.") {
        (*slot).max(value)
    } else {
        slot.saturating_add(value)
    };
}

/// A provider counter, from keys like `p.<provider>.<workload>.<outcome>`.
#[derive(Clone, Copy)]
enum Field {
    Bytes(Workload),
    Attempt(Workload, Outcome),
    /// Requests that failed for the caller.
    Failed(Workload),
    Latency(Workload, usize),
    Trips,
    ConnectFailures,
    ActiveMs,
}

fn provider_field(key: &str) -> Option<(&str, Field)> {
    let (provider, rest) = key.strip_prefix("p.")?.split_once('.')?;
    let field = match rest.split_once('.') {
        Some((workload, field)) => {
            let workload = *Workload::ALL.iter().find(|w| w.label() == workload)?;
            match (field, field.strip_prefix("lat.")) {
                ("bytes", _) => Field::Bytes(workload),
                ("failed", _) => Field::Failed(workload),
                (_, Some(i)) => Field::Latency(workload, i.parse().ok()?),
                (_, None) => {
                    Field::Attempt(workload, *Outcome::ALL.iter().find(|o| o.label() == field)?)
                }
            }
        }
        None => match rest {
            "trips" => Field::Trips,
            "connect_failures" => Field::ConnectFailures,
            "active_ms" => Field::ActiveMs,
            _ => return None,
        },
    };
    Some((provider, field))
}

/// Adds counter `key` to the bucket `point`.
fn tally(point: &mut Point, key: &str, value: u64) {
    match key {
        "peak.read_bps" => point.peak_bps = point.peak_bps.max(value),
        "peak.fetch_bps" => point.peak_fetch_bps = point.peak_fetch_bps.max(value),
        "reads.ended" => point.reads += value,
        "reads.stalls" => point.stalls += value,
        "queue.completed" => point.imports += value,
        "queue.failed" => point.import_failures += value,
        "health.healthy" | "health.inconclusive" => point.checks += value,
        "health.unhealthy" => {
            point.checks += value;
            point.unhealthy += value;
        }
        "cache.memory_hits" | "cache.disk_hits" | "cache.joined" => {
            point.cache_hits += value;
        }
        "cache.misses" => point.cache_misses += value,
        k if k.starts_with("served.") => point.served += value,
        k if k.starts_with("rescue.") => point.rescues += value,
        _ => {}
    }
    match provider_field(key).map(|(_, field)| field) {
        Some(Field::Bytes(workload)) => {
            point.fetched += value;
            match workload {
                Workload::Stream => point.stream_bytes += value,
                Workload::Queue => point.queue_bytes += value,
                Workload::Health => point.health_bytes += value,
            }
        }
        Some(Field::Attempt(workload, outcome)) if outcome != Outcome::Cancelled => {
            match workload {
                Workload::Stream => point.stream += value,
                Workload::Queue => point.queue += value,
                Workload::Health => point.health += value,
            }
            if outcome == Outcome::NotFound {
                point.missing += value;
            }
        }
        Some(Field::Failed(_)) => point.errors += value,
        _ => {}
    }
}

/// Provider name by key; the key itself for providers no longer configured.
fn name_of(client: &Client) -> impl Fn(&str) -> String {
    let names: HashMap<String, String> = client
        .stats()
        .into_iter()
        .map(|p| (p.key, p.name))
        .collect();
    move |key| names.get(key).cloned().unwrap_or_else(|| key.to_owned())
}

fn totals(sums: &HashMap<String, u64>) -> Totals {
    let get = |key: &str| sums.get(key).copied().unwrap_or(0);
    let sum =
        |keep: &dyn Fn(&str) -> bool| sums.iter().filter(|(k, _)| keep(k)).map(|(_, v)| *v).sum();
    let first_bytes = get("reads.first_bytes");
    let mut t = Totals {
        served: sum(&|k| k.starts_with("served.")),
        peak_fetch_bps: get("peak.fetch_bps"),
        reads: get("reads.ended"),
        read_ms: get("reads.open_ms"),
        stalls: get("reads.stalls"),
        stall_ms: get("reads.stall_ms"),
        seeks: get("reads.seeks"),
        first_byte_ms_avg: get("reads.first_byte_ms").checked_div(first_bytes),
        imports: get("queue.completed"),
        import_failures: get("queue.failed"),
        import_bytes: get("queue.bytes"),
        import_ms: get("queue.import_ms"),
        import_retries: get("queue.retried"),
        healthy: get("health.healthy"),
        unhealthy: get("health.unhealthy"),
        inconclusive: get("health.inconclusive"),
        repaired: get("health.repaired"),
        rescues: sum(&|k| k.starts_with("rescue.")),
        cache_hits: get("cache.memory_hits") + get("cache.disk_hits") + get("cache.joined"),
        cache_misses: get("cache.misses"),
        ..Totals::default()
    };
    for (key, &value) in sums {
        match provider_field(key).map(|(_, f)| f) {
            Some(Field::Bytes(_)) => t.fetched += value,
            Some(Field::Trips) => t.trips += value,
            Some(Field::Failed(_)) => t.failures += value,
            Some(Field::Attempt(_, o)) if o != Outcome::Cancelled => {
                t.requests += value;
                match o {
                    Outcome::Retrieved => t.retrieved += value,
                    Outcome::NotFound => t.missing += value,
                    _ => {}
                }
            }
            _ => {}
        }
    }
    t
}

#[derive(Default)]
struct Score {
    bytes: u64,
    attempts: u64,
    missing: u64,
    failures: u64,
    retried: u64,
    trips: u64,
    connect_failures: u64,
    peak_bps: u64,
    active_ms: u64,
    latency: [u64; LATENCY_BUCKETS],
}

fn scores(sums: &HashMap<String, u64>, name: &dyn Fn(&str) -> String) -> Vec<ProviderScore> {
    let mut by: BTreeMap<&str, Score> = BTreeMap::new();
    for (key, &value) in sums {
        if let Some(provider) = key
            .strip_prefix("peak.p.")
            .and_then(|k| k.strip_suffix(".bps"))
        {
            let score = by.entry(provider).or_default();
            score.peak_bps = score.peak_bps.max(value);
            continue;
        }
        let Some((provider, field)) = provider_field(key) else {
            continue;
        };
        let score = by.entry(provider).or_default();
        match field {
            Field::Bytes(_) => score.bytes += value,
            Field::Latency(Workload::Stream, i) => {
                if let Some(slot) = score.latency.get_mut(i) {
                    *slot += value;
                }
            }
            Field::Latency(..) | Field::Attempt(_, Outcome::Cancelled) => {}
            Field::Attempt(_, o) => {
                match o {
                    Outcome::NotFound => score.missing += value,
                    Outcome::NotRetrieved(_) => score.retried += value,
                    _ => {}
                }
                score.attempts += value;
            }
            Field::Failed(_) => score.failures += value,
            Field::Trips => score.trips += value,
            Field::ConnectFailures => score.connect_failures += value,
            Field::ActiveMs => score.active_ms += value,
        }
    }
    by.into_iter()
        .map(|(key, s)| ProviderScore {
            key: key.to_owned(),
            name: name(key),
            bytes: s.bytes,
            attempts: s.attempts,
            missing: s.missing,
            failures: s.failures,
            retried: s.retried.saturating_sub(s.failures),
            trips: s.trips,
            connect_failures: s.connect_failures,
            peak_bps: s.peak_bps,
            active_ms: s.active_ms,
            p50_us: percentile(&s.latency, 500),
            p95_us: percentile(&s.latency, 950),
            p99_us: percentile(&s.latency, 990),
            points: Vec::new(),
        })
        .collect()
}

fn workloads(sums: &HashMap<String, u64>) -> Vec<WorkloadView> {
    let fields: Vec<(Field, u64)> = sums
        .iter()
        .filter_map(|(key, &value)| Some((provider_field(key)?.1, value)))
        .collect();
    Workload::ALL
        .iter()
        .map(|&w| {
            let mut view = WorkloadView {
                workload: w.label(),
                ..WorkloadView::default()
            };
            let mut latency = [0; LATENCY_BUCKETS];
            for &(field, value) in &fields {
                match field {
                    Field::Bytes(x) if x == w => view.bytes += value,
                    Field::Latency(x, i) if x == w => {
                        if let Some(slot) = latency.get_mut(i) {
                            *slot += value;
                        }
                    }
                    Field::Attempt(x, o) if x == w && o != Outcome::Cancelled => {
                        view.requests += value;
                        if o == Outcome::NotFound {
                            view.missing += value;
                        }
                    }
                    Field::Failed(x) if x == w => view.failures += value,
                    _ => {}
                }
            }
            view.p50_us = percentile(&latency, 500);
            view.p95_us = percentile(&latency, 950);
            view
        })
        .collect()
}

fn rescues(sums: &HashMap<String, u64>, name: &dyn Fn(&str) -> String) -> Vec<RescueView> {
    let mut out: Vec<_> = sums
        .iter()
        .filter_map(|(key, &count)| {
            let mut parts = key.strip_prefix("rescue.")?.splitn(3, '.');
            Some(RescueView {
                from: name(parts.next()?),
                to: name(parts.next()?),
                reason: parts.next()?.to_owned(),
                count,
            })
        })
        .collect();
    out.sort_by_key(|r| std::cmp::Reverse(r.count));
    out
}

/// Usage by app: a program's first word (`Plex Media Scan` → `Plex`, `silo › ffmpeg` →
/// `silo`), and for `WebDAV`
/// agents the product before its version (`rclone/1.69` → `rclone`), so one app's
/// processes and versions add up.
fn apps(usage: Vec<Usage>) -> Vec<AppUsage> {
    let mut merged: BTreeMap<(String, String), AppUsage> = BTreeMap::new();
    for u in usage {
        let first = u.program.split_whitespace().next().unwrap_or_default();
        let app = first.split('/').next().unwrap_or_default().to_owned();
        let entry = merged
            .entry((u.delivery.clone(), app.clone()))
            .or_insert(AppUsage {
                delivery: u.delivery,
                app,
                reads: 0,
                served: 0,
                open_ms: 0,
            });
        entry.reads += u.reads;
        entry.served += u.served;
        entry.open_ms += u.open_ms;
    }
    let mut out: Vec<AppUsage> = merged.into_values().collect();
    out.sort_by_key(|a| std::cmp::Reverse(a.served));
    out.truncate(TOP_APPS);
    out
}

/// The `permille`th latency in microseconds, interpolated inside its bucket.
fn percentile(buckets: &[u64; LATENCY_BUCKETS], permille: u64) -> Option<u64> {
    let total: u64 = buckets.iter().sum();
    let target = total.checked_mul(permille)?.div_ceil(1000).max(1);
    let mut seen = 0;
    for (i, &n) in buckets.iter().enumerate() {
        if n > 0 && seen + n >= target {
            let low = if i == 0 { 0 } else { 1_u64 << (i - 1) };
            let high = 1_u64 << i;
            return Some(low + (high - low) * (target - seen) / n);
        }
        seen += n;
    }
    None
}

fn ms(d: Duration) -> u64 {
    u64::try_from(d.as_millis()).unwrap_or(u64::MAX)
}

/// How long a read has gone without data: its reader's wait or its oldest FUSE request.
fn stalled_ms(r: &LiveRead) -> u64 {
    let oldest = r.pending.first().map_or(0, |p| p.age_ms);
    r.waiting_ms.unwrap_or(0).max(oldest)
}

/// Tracks stall episodes in `stalled`: returns reads stalled past the threshold for the
/// first time, and episodes that ended as (session, how long, still open).
fn stall_episodes<'a>(
    stalled: &mut HashMap<u64, Instant>,
    reads: &'a [LiveRead],
    now: Instant,
) -> (Vec<&'a LiveRead>, Vec<(u64, Duration, bool)>) {
    let mut started = Vec::new();
    let mut still = HashSet::new();
    for r in reads {
        let for_ms = stalled_ms(r);
        if for_ms >= STALL_WARN_MS {
            still.insert(r.id);
            stalled.entry(r.id).or_insert_with(|| {
                started.push(r);
                now.checked_sub(Duration::from_millis(for_ms))
                    .unwrap_or(now)
            });
        }
    }
    let mut ended = Vec::new();
    stalled.retain(|id, since| {
        let keep = still.contains(id);
        if !keep {
            ended.push((*id, now - *since, reads.iter().any(|r| r.id == *id)));
        }
        keep
    });
    (started, ended)
}

/// What a stalled read waits on, for people.
fn stall_reason(r: &LiveRead) -> &'static str {
    match (r.head.as_ref().and_then(|h| h.stage), r.pending.first()) {
        (Some("budget"), _) => "read-ahead budget is held by other reads",
        (Some("permit"), _) => "no download permit is free",
        (Some("connection"), _) => "no provider connection is free",
        (Some("pipeline"), _) => "earlier requests on its provider connection are not done",
        (Some("reply"), _) => "the provider has not answered",
        (Some("transfer"), _) => "the article is still arriving from the provider",
        (Some("disk"), _) => "the disk cache has not returned the segment",
        (Some("done"), _) => "the segment is in but the reader has not taken it",
        (_, Some(p)) if p.stage == "order" => "FUSE reads wait for earlier reads of the file",
        (_, Some(p)) if p.stage == "handle_lock" => "the file's reader is busy with another read",
        (_, Some(_)) => "the reader has not returned",
        _ => "the reader is waiting without a known cause",
    }
}

/// Logs reads without data for [`STALL_WARN_MS`] once per episode, and FUSE requests
/// the kernel holds back for as long.
fn watch_stalls(
    state: &mut Sampled,
    reads: &[LiveRead],
    providers: &[LiveProvider],
    fuse: Option<&LiveFuse>,
    budget_waiters: usize,
    now: Instant,
) {
    let kernel_waiting = fuse.and_then(|f| f.waiting);
    let (started, ended) = stall_episodes(&mut state.stalled, reads, now);
    for r in started.iter().take(STALL_DETAILS) {
        let head = r.head.as_ref();
        let connection = head.and_then(|h| {
            let (key, id) = (h.provider.as_deref()?, h.connection?);
            providers
                .iter()
                .find(|p| p.key == key)?
                .connections
                .iter()
                .find(|c| c.id == id)
        });
        let oldest = r.pending.first();
        warn!(
            session = r.id,
            delivery = r.delivery,
            stalled_ms = stalled_ms(r),
            position = r.position,
            segment = head.map(|h| h.segment),
            stage = head.and_then(|h| h.stage),
            stage_ms = head.and_then(|h| h.stage_ms),
            provider = head.and_then(|h| h.provider.as_deref()),
            connection = head.and_then(|h| h.connection),
            connection_phase = connection.map(|c| c.phase),
            since_byte_ms = connection.and_then(|c| c.since_byte_ms),
            joined = head.is_some_and(|h| h.joined),
            budget_bytes = r.budget_bytes,
            budget_waiters,
            pending = r.pending.len(),
            pending_stage = oldest.map(|p| p.stage),
            kernel_waiting,
            reason = stall_reason(r),
            "read stalled"
        );
    }
    if started.len() > STALL_DETAILS {
        let mut stages = BTreeMap::new();
        for r in &started[STALL_DETAILS..] {
            let stage = r.head.as_ref().and_then(|h| h.stage).unwrap_or("none");
            *stages.entry(stage).or_insert(0_usize) += 1;
        }
        let stages = stages
            .iter()
            .map(|(s, n)| format!("{s}={n}"))
            .collect::<Vec<_>>()
            .join(" ");
        warn!(
            count = started.len() - STALL_DETAILS,
            stages, budget_waiters, kernel_waiting, "more reads stalled"
        );
    }
    for (session, stalled, open) in ended {
        let stalled_ms = ms(stalled);
        if open {
            info!(session, stalled_ms, "stalled read recovered");
        } else {
            info!(session, stalled_ms, "stalled read closed");
        }
    }

    // More requests in the kernel than we hold, by a whole background window: the
    // kernel queues reads we never see.
    let listed = reads.iter().map(|r| r.pending.len() as u64).sum::<u64>();
    let queued = fuse.and_then(|f| {
        let excess = f.waiting?.saturating_sub(listed);
        (excess >= u64::from(f.max_background?)).then_some(excess)
    });
    match (queued, &mut state.kernel_queued) {
        (Some(excess), Some((since, logged))) => {
            if !*logged && now - *since >= Duration::from_millis(STALL_WARN_MS) {
                *logged = true;
                warn!(
                    kernel_waiting,
                    listed,
                    excess,
                    max_background = fuse.and_then(|f| f.max_background),
                    reason = "the kernel holds FUSE requests infinidysk has not received",
                    "FUSE requests are queued in the kernel"
                );
            }
        }
        (Some(_), slot) => *slot = Some((now, false)),
        (None, slot) => {
            if slot.take().is_some_and(|(_, logged)| logged) {
                info!(kernel_waiting, listed, "FUSE kernel queue drained");
            }
        }
    }
}

fn record(s: &Session) -> SessionRecord {
    let (stalls, stall) = s.stalls();
    let ms = |d: Duration| i64::try_from(d.as_millis()).unwrap_or(i64::MAX);
    SessionRecord {
        item: Some(s.item),
        opened_at: unix_ms(s.opened_at),
        closed_at: s.closed_at().map_or_else(now_ms, unix_ms),
        path: s.path.clone(),
        delivery: s.delivery.label().to_owned(),
        client: s.client.clone(),
        size: s.size.cast_signed(),
        served: s.served().cast_signed(),
        first_byte_ms: s.first_byte().map(ms),
        stalls: stalls.cast_signed(),
        stall_ms: ms(stall),
        seeks: s.seeks().cast_signed(),
        end_reason: if s.pos() >= s.size { "eof" } else { "closed" }.to_owned(),
    }
}

fn recent_read(r: &SessionRecord) -> RecentRead {
    RecentRead {
        item_id: r.item.map(|i| i.0),
        opened_at: r.opened_at,
        closed_at: r.closed_at,
        path: r.path.clone(),
        delivery: r.delivery.clone(),
        client: r.client.clone(),
        size: r.size,
        served: r.served,
        first_byte_ms: r.first_byte_ms,
        stalls: r.stalls,
        stall_ms: r.stall_ms,
        seeks: r.seeks,
        end_reason: r.end_reason.clone(),
    }
}

pub fn unix_ms(at: SystemTime) -> i64 {
    at.duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

fn now_ms() -> i64 {
    unix_ms(SystemTime::now())
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
pub struct OverviewQuery {
    /// Default `24h`.
    window: Option<Window>,
}

/// Throughput, provider scores and recent reads over a window, unsaved minutes included.
#[utoipa::path(get, path = "/api/v1/overview", tag = "metrics", params(OverviewQuery),
    responses((status = 200, body = Overview)))]
pub async fn get_overview(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<OverviewQuery>,
) -> Result<Json<Overview>, Fail> {
    Ok(Json(
        admin
            .metrics
            .overview(query.window.unwrap_or_default())
            .await?,
    ))
}

/// The newest reads of one file (up to 100, kept 90 days), newest first.
#[utoipa::path(get, path = "/api/v1/files/{id}/reads", tag = "metrics",
    params(("id" = Uuid, Path)),
    responses((status = 200, body = Vec<RecentRead>)))]
pub async fn get_file_reads(
    State(admin): State<Arc<Admin>>,
    AxumPath(id): AxumPath<Uuid>,
) -> Result<Json<Vec<RecentRead>>, Fail> {
    Ok(Json(admin.metrics.item_reads(ItemId(id)).await?))
}

/// The latest 2 s sample; `/api/v1/events` sends each new one as a `live` event.
#[utoipa::path(get, path = "/api/v1/live", tag = "metrics",
    responses((status = 200, body = LiveView)))]
pub async fn get_live(State(admin): State<Arc<Admin>>) -> Json<LiveView> {
    Json(LiveView::clone(&admin.metrics.live()))
}

/// Prometheus metrics when `metrics.prometheus` is on; scrapers authenticate with
/// `Authorization: Bearer <api key>`.
#[utoipa::path(get, path = "/metrics", tag = "metrics", responses(
    (status = 200, content_type = "text/plain", body = String),
    (status = 404, description = "Prometheus metrics are turned off")))]
pub async fn get_prometheus(State(admin): State<Arc<Admin>>) -> Response {
    if !admin.config.read().await.settings().metrics.prometheus {
        return StatusCode::NOT_FOUND.into_response();
    }
    (
        [(
            header::CONTENT_TYPE,
            "text/plain; version=0.0.4; charset=utf-8",
        )],
        admin.metrics.prometheus(),
    )
        .into_response()
}

/// infinidysk's cgroup v2 directory and the host's memory.
struct Cgroup {
    dir: PathBuf,
    host_memory: u64,
}

/// Swap and this process's memory, read once a minute rather than every sample.
#[derive(Clone, Copy)]
struct SlowRead {
    at: Instant,
    cgroup_swap_bytes: Option<u64>,
    process: ProcessMemory,
}

impl Cgroup {
    fn find() -> Result<Self, String> {
        if !cfg!(target_os = "linux") {
            return Err("CPU and memory are reported on Linux only.".to_owned());
        }
        let own = fs::read_to_string("/proc/self/cgroup")
            .map_err(|e| format!("/proc/self/cgroup is unreadable: {e}"))?;
        let path =
            cgroup_path(&own).ok_or("This host uses cgroup v1; CPU and memory need cgroup v2.")?;
        let root = Path::new("/sys/fs/cgroup");
        // Without a private cgroup namespace the path can point outside the mount.
        let dir = [root.join(path.trim_start_matches('/')), root.to_owned()]
            .into_iter()
            .find(|d| d.join("memory.current").is_file())
            .ok_or("The cgroup memory controller is not available to this process.")?;
        let host_memory = fs::read_to_string("/proc/meminfo")
            .ok()
            .as_deref()
            .and_then(|meminfo| kb_field(meminfo, "MemTotal"))
            .ok_or("/proc/meminfo has no MemTotal.")?;
        Ok(Self { dir, host_memory })
    }

    /// CPU time used so far in microseconds, and resources without `cpu_cores`, the segment
    /// cache, swap and process memory.
    fn read(&self) -> Result<(u64, Resources), String> {
        let read = |name: &str| {
            fs::read_to_string(self.dir.join(name))
                .map_err(|e| format!("cgroup {name} is unreadable: {e}"))
        };
        let usec = stat_field(&read("cpu.stat")?, "usage_usec")
            .ok_or("cgroup cpu.stat has no usage_usec.")?;
        let current =
            limit(&read("memory.current")?).ok_or("cgroup memory.current is not a number.")?;
        let stat = read("memory.stat")?;
        let cache = kernel_cache(&stat).min(current);
        let max = limit(&read("memory.max")?);
        // cpu.max is missing when the cpu controller is not delegated.
        let quota = read("cpu.max").ok().as_deref().and_then(cpu_quota);
        let parallel = std::thread::available_parallelism().map_or(1, std::num::NonZero::get);
        let parallel = f64::from(u32::try_from(parallel).unwrap_or(u32::MAX));
        // available_parallelism rounds the quota down; a tighter cpuset still wins.
        let cpu_limit_cores = match quota {
            Some(q) if parallel >= q.floor().max(1.0) => q,
            _ => parallel,
        };
        Ok((
            usec,
            Resources {
                cpu_cores: None,
                cpu_limit_cores,
                memory_process_bytes: current - cache,
                memory_segment_cache_bytes: 0,
                memory_segment_cache_limit_bytes: 0,
                memory_kernel_cache_bytes: cache,
                memory_limit_bytes: max.map_or(self.host_memory, |m| m.min(self.host_memory)),
                memory_limited: max.is_some_and(|m| m < self.host_memory),
                segment_cache_logical_bytes: 0,
                segment_cache_capacity_bytes: 0,
                cgroup_anon_bytes: stat_field(&stat, "anon"),
                cgroup_file_bytes: stat_field(&stat, "file"),
                cgroup_kernel_bytes: stat_field(&stat, "kernel"),
                cgroup_sock_bytes: stat_field(&stat, "sock"),
                cgroup_slab_bytes: stat_field(&stat, "slab"),
                cgroup_slab_reclaimable_bytes: stat_field(&stat, "slab_reclaimable"),
                cgroup_anon_thp_bytes: stat_field(&stat, "anon_thp"),
                cgroup_shmem_bytes: stat_field(&stat, "shmem"),
                cgroup_swap_bytes: None,
                process_memory: ProcessMemory::default(),
            },
        ))
    }

    /// `last` while it is younger than [`SLOW_READ_EVERY`], else swap and `status`
    /// (`/proc/self/status`) read again; a missing or unreadable file leaves its values none.
    fn read_slow(&self, status: &Path, last: Option<SlowRead>, now: Instant) -> SlowRead {
        if let Some(last) = last
            && now - last.at < SLOW_READ_EVERY
        {
            return last;
        }
        let cgroup_swap_bytes = fs::read_to_string(self.dir.join("memory.swap.current"))
            .ok()
            .as_deref()
            .and_then(limit);
        let status = fs::read_to_string(status).unwrap_or_default();
        SlowRead {
            at: now,
            cgroup_swap_bytes,
            process: process_memory(&status, now_ms(), crate::logs::started_ms()),
        }
    }
}

/// The v2 entry of `/proc/self/cgroup`.
fn cgroup_path(text: &str) -> Option<&str> {
    text.lines().find_map(|l| l.strip_prefix("0::"))
}

/// A `key value` line of `cpu.stat` or `memory.stat`.
fn stat_field(text: &str, key: &str) -> Option<u64> {
    text.lines()
        .find_map(|l| l.strip_prefix(key)?.strip_prefix(' ')?.parse().ok())
}

/// Page cache plus reclaimable slab from `memory.stat`, the parts `MemAvailable` also counts.
fn kernel_cache(stat: &str) -> u64 {
    ["file", "slab_reclaimable"]
        .into_iter()
        .filter_map(|key| stat_field(stat, key))
        .fold(0, u64::saturating_add)
}

/// A number, or `None` for `max`.
fn limit(text: &str) -> Option<u64> {
    text.trim().parse().ok()
}

/// Cores from `cpu.max` (`<quota> <period>` or `max <period>`).
fn cpu_quota(text: &str) -> Option<f64> {
    let (quota, period) = text.trim().split_once(' ')?;
    let (quota, period) = (quota.parse::<u32>().ok()?, period.parse::<u32>().ok()?);
    (period > 0).then(|| f64::from(quota) / f64::from(period))
}

/// A `<key>: <n> kB` line of `/proc/meminfo` or `/proc/self/status` in bytes; `kB` is 1024
/// bytes there.
fn kb_field(text: &str, key: &str) -> Option<u64> {
    let kib: u64 = text
        .lines()
        .find_map(|l| l.strip_prefix(key)?.strip_prefix(':'))?
        .trim()
        .strip_suffix("kB")?
        .trim()
        .parse()
        .ok()?;
    kib.checked_mul(1024)
}

/// This process's memory from `/proc/self/status`, read at `at` by a process that started at
/// `started`, both Unix milliseconds.
fn process_memory(status: &str, at: i64, started: i64) -> ProcessMemory {
    ProcessMemory {
        at,
        uptime_ms: (at - started).max(0).cast_unsigned(),
        rss_bytes: kb_field(status, "VmRSS"),
        rss_anon_bytes: kb_field(status, "RssAnon"),
        rss_file_bytes: kb_field(status, "RssFile"),
        rss_peak_bytes: kb_field(status, "VmHWM"),
        swap_bytes: kb_field(status, "VmSwap"),
        threads: status
            .lines()
            .find_map(|l| l.strip_prefix("Threads:")?.trim().parse().ok()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_helper_counts_toward_the_app_that_started_it() {
        let usage = |program: &str, served| Usage {
            delivery: "fuse".to_owned(),
            program: program.to_owned(),
            reads: 1,
            served,
            open_ms: 0,
        };
        let got: Vec<_> = apps(vec![
            usage("silo", 10),
            usage("silo › ffmpeg", 5),
            usage("other › ffmpeg", 3),
            usage("ffmpeg", 1),
        ])
        .into_iter()
        .map(|a| (a.app, a.reads, a.served))
        .collect();
        assert_eq!(
            got,
            [
                ("silo".to_owned(), 2, 15),
                ("other".to_owned(), 1, 3),
                ("ffmpeg".to_owned(), 1, 1),
            ]
        );
    }

    #[test]
    fn a_stall_is_reported_once_until_it_ends() {
        let read = |id, waiting_ms, oldest_ms: Option<u64>| LiveRead {
            id,
            path: String::new(),
            delivery: "fuse",
            client: String::new(),
            opened_at: 0,
            size: 0,
            position: 0,
            served: 0,
            bps: 0,
            first_byte_ms: None,
            stalls: 0,
            stall_ms: 0,
            seeks: 0,
            traced: false,
            budget_bytes: 0,
            waiting_ms,
            pending: oldest_ms
                .map(|age_ms| LivePending {
                    offset: 0,
                    size: 0,
                    age_ms,
                    stage: "order",
                })
                .into_iter()
                .collect(),
            head: None,
            sources: Vec::new(),
        };
        let mut stalled = HashMap::new();
        let now = Instant::now();
        // Read 1 waits in its reader, read 2 in FUSE ordering, read 3 is fine.
        let reads = [
            read(1, Some(STALL_WARN_MS), None),
            read(2, None, Some(STALL_WARN_MS + 1)),
            read(3, Some(10), Some(10)),
        ];
        let ids = |started: Vec<&LiveRead>| started.iter().map(|r| r.id).collect::<Vec<_>>();
        let (started, ended) = stall_episodes(&mut stalled, &reads, now);
        assert_eq!(ids(started), [1, 2]);
        assert_eq!(ended.len(), 0);
        let (started, _) = stall_episodes(&mut stalled, &reads, now + SAMPLE_EVERY);
        assert_eq!(ids(started), [0; 0], "warned once per episode");
        // Read 1 got data, read 2 closed.
        let reads = [read(1, None, None)];
        let (_, mut ended) = stall_episodes(&mut stalled, &reads, now + SAMPLE_EVERY);
        ended.sort_by_key(|e| e.0);
        let ended: Vec<(u64, bool)> = ended.iter().map(|e| (e.0, e.2)).collect();
        assert_eq!(ended, [(1, true), (2, false)]);
        assert_eq!(stalled.len(), 0);
    }

    #[test]
    fn cgroup_files_parse() {
        assert_eq!(
            cgroup_path("0::/system.slice/x.service\n"),
            Some("/system.slice/x.service")
        );
        assert_eq!(cgroup_path("12:memory:/docker/abc\n"), None);
        let stat = "anon 100\nfile 2048\nfile_mapped 7\n";
        assert_eq!(stat_field(stat, "file"), Some(2048));
        let stat = "file 2048\nslab_reclaimable 512\nslab_unreclaimable 64\n";
        assert_eq!(kernel_cache(stat), 2560);
        assert_eq!(kernel_cache("anon 1\n"), 0);
        assert_eq!(
            stat_field("usage_usec 42\nuser_usec 1\n", "usage_usec"),
            Some(42)
        );
        assert_eq!(limit("max\n"), None);
        assert_eq!(limit("4000000000\n"), Some(4_000_000_000));
        assert_eq!(cpu_quota("150000 100000\n"), Some(1.5));
        assert_eq!(cpu_quota("max 100000\n"), None);
        assert_eq!(
            kb_field("MemTotal:       16 kB\nMemFree: 1 kB\n", "MemTotal"),
            Some(16 * 1024)
        );
        // Any order, unknown lines ignored, a missing line is none; kB is 1024 bytes.
        let status = "Name:\tinfinidysk\nVmHWM:\t    3000 kB\nVmRSS:\t    2000 kB\n\
            RssAnon:\t    1500 kB\nRssFile:\t     500 kB\nRssShmem:\t       0 kB\nThreads:\t42\n";
        assert_eq!(
            serde_json::to_value(process_memory(status, 61_000, 1_000))
                .expect("process memory serializes"),
            serde_json::json!({
                "at": 61_000,
                "uptime_ms": 60_000,
                "rss_bytes": 2000 * 1024,
                "rss_anon_bytes": 1500 * 1024,
                "rss_file_bytes": 500 * 1024,
                "rss_peak_bytes": 3000 * 1024,
                "swap_bytes": null,
                "threads": 42,
            })
        );
        assert_eq!(
            kb_field(&format!("VmSwap:\t{} kB\n", u64::MAX), "VmSwap"),
            None,
            "a value that overflows bytes is unavailable"
        );
    }

    #[test]
    fn cgroup_read_splits_memory_into_app_and_kernel_cache() {
        let dir = std::env::temp_dir().join(format!("infinidysk-cgroup-{}", Uuid::new_v4()));
        fs::create_dir_all(&dir).expect("temp dir is writable");
        let write = |name: &str, text: &str| {
            fs::write(dir.join(name), text).expect("temp dir is writable");
        };
        write("cpu.stat", "usage_usec 42\n");
        write("cpu.max", "max 100000\n");
        write("memory.max", "max\n");
        write(
            "memory.stat",
            "slab 576\nanon_thp 64\nfile_mapped 7\nanon 100\nshmem 32\nfile 2048\n\
             slab_reclaimable 512\nslab_unreclaimable 64\nsock 16\n",
        );
        let cgroup = Cgroup {
            dir: dir.clone(),
            host_memory: 1 << 40,
        };

        write("memory.current", "4096\n");
        let (usec, mut r) = cgroup.read().expect("all cgroup files exist");
        assert_eq!(usec, 42);
        assert_eq!(r.memory_kernel_cache_bytes, 2560);
        assert_eq!(r.memory_process_bytes, 1536);
        assert_eq!(r.memory_limit_bytes, 1 << 40);
        assert!(!r.memory_limited);
        assert_eq!(
            [
                r.cgroup_anon_bytes,
                r.cgroup_file_bytes,
                r.cgroup_kernel_bytes,
                r.cgroup_sock_bytes,
                r.cgroup_slab_bytes,
                r.cgroup_slab_reclaimable_bytes,
                r.cgroup_anon_thp_bytes,
                r.cgroup_shmem_bytes,
            ],
            [
                Some(100),
                Some(2048),
                None,
                Some(16),
                Some(576),
                Some(512),
                Some(64),
                Some(32)
            ]
        );
        let json = serde_json::to_value(r).expect("resources serialize");
        let mut keys: Vec<&str> = json
            .as_object()
            .expect("resources serialize as an object")
            .keys()
            .map(String::as_str)
            .collect();
        keys.sort_unstable();
        assert_eq!(
            keys,
            [
                "cgroup_anon_bytes",
                "cgroup_anon_thp_bytes",
                "cgroup_file_bytes",
                "cgroup_kernel_bytes",
                "cgroup_shmem_bytes",
                "cgroup_slab_bytes",
                "cgroup_slab_reclaimable_bytes",
                "cgroup_sock_bytes",
                "cgroup_swap_bytes",
                "cpu_cores",
                "cpu_limit_cores",
                "memory_kernel_cache_bytes",
                "memory_limit_bytes",
                "memory_limited",
                "memory_process_bytes",
                "memory_segment_cache_bytes",
                "memory_segment_cache_limit_bytes",
                "process_memory",
                "segment_cache_capacity_bytes",
                "segment_cache_logical_bytes",
            ]
        );
        // More cached than app memory: the old field stays capped, the new one does not.
        r.set_segment_cache(&CacheStats {
            memory_enabled: true,
            memory_bytes: 4096,
            memory_capacity: 8192,
            ..CacheStats::default()
        });
        assert_eq!(
            (r.memory_segment_cache_bytes, r.segment_cache_logical_bytes),
            (1536, 4096)
        );
        assert_eq!(
            (
                r.memory_segment_cache_limit_bytes,
                r.segment_cache_capacity_bytes
            ),
            (8192, 8192)
        );
        // Turned off: the limit reads 0, the raw capacity stays.
        r.set_segment_cache(&CacheStats {
            memory_capacity: 8192,
            ..CacheStats::default()
        });
        assert_eq!(
            (
                r.memory_segment_cache_limit_bytes,
                r.segment_cache_capacity_bytes
            ),
            (0, 8192)
        );

        // The stat can briefly exceed memory.current; the cache is clamped.
        write("memory.current", "2000\n");
        let (_, r) = cgroup.read().expect("all cgroup files exist");
        assert_eq!(r.memory_kernel_cache_bytes, 2000);
        assert_eq!(r.memory_process_bytes, 0);

        fs::remove_dir_all(&dir).expect("temp dir is removable");
    }

    #[test]
    fn swap_and_process_memory_are_read_once_a_minute() {
        let dir = std::env::temp_dir().join(format!("infinidysk-cgroup-{}", Uuid::new_v4()));
        fs::create_dir_all(&dir).expect("temp dir is writable");
        let cgroup = Cgroup {
            dir: dir.clone(),
            host_memory: 1 << 40,
        };
        let start = Instant::now();
        let swap = dir.join("memory.swap.current");
        let status = dir.join("status");

        // Missing files are unavailable, not 0.
        let first = cgroup.read_slow(&status, None, start);
        assert_eq!(
            (first.cgroup_swap_bytes, first.process.rss_bytes),
            (None, None)
        );
        fs::write(&swap, "4096\n").expect("temp dir is writable");
        fs::write(&status, "VmRSS:\t       2 kB\n").expect("temp dir is writable");
        let kept = cgroup.read_slow(&status, Some(first), start + SAMPLE_EVERY);
        assert_eq!(
            (kept.at, kept.cgroup_swap_bytes, kept.process.rss_bytes),
            (start, None, None),
            "a sample 2 s later reuses the last read"
        );
        let next = cgroup.read_slow(&status, Some(kept), start + SLOW_READ_EVERY);
        assert_eq!(
            (next.at, next.cgroup_swap_bytes, next.process.rss_bytes),
            (start + SLOW_READ_EVERY, Some(4096), Some(2048))
        );
        // Missing or unreadable after a good read: the old values are not kept.
        fs::remove_file(&swap).expect("temp file is removable");
        fs::remove_file(&status).expect("temp file is removable");
        fs::create_dir(&status).expect("temp dir is writable");
        let failed = cgroup.read_slow(&status, Some(next), start + 2 * SLOW_READ_EVERY);
        assert_eq!(
            (failed.cgroup_swap_bytes, failed.process.rss_bytes),
            (None, None)
        );

        fs::remove_dir_all(&dir).expect("temp dir is removable");
    }

    #[test]
    fn a_bucket_splits_provider_traffic_by_workload() {
        let (ok, missing) = (Outcome::Retrieved.label(), Outcome::NotFound.label());
        let mut point = Point::default();
        for (key, value) in [
            ("p.a.stream.bytes".to_owned(), 100),
            ("p.b.stream.bytes".to_owned(), 50),
            ("p.a.queue.bytes".to_owned(), 7),
            ("p.a.health.bytes".to_owned(), 3),
            (format!("p.a.queue.{ok}"), 2),
            (format!("p.b.stream.{missing}"), 1),
            ("served.fuse".to_owned(), 90),
        ] {
            tally(&mut point, &key, value);
        }
        let bytes = (point.stream_bytes, point.queue_bytes, point.health_bytes);
        assert_eq!(bytes, (150, 7, 3));
        assert_eq!((point.fetched, point.served), (160, 90));
        assert_eq!(
            (point.stream, point.queue, point.health, point.missing),
            (1, 2, 0, 1)
        );
    }

    #[test]
    fn an_outage_lasts_until_the_breaker_closes_and_keeps_its_key() {
        let mut open = HashMap::new();
        let none: Vec<BreakerEvent> = Vec::new();
        assert_eq!(track_outages(&mut open, [("k1", "Name")], 1_000), none);
        // Failed probes keep it open well past the first cooldown; a rename changes nothing.
        assert_eq!(track_outages(&mut open, [("k1", "Renamed")], 200_000), none);
        let ended = track_outages(&mut open, [], 250_000);
        assert_eq!(
            ended,
            [BreakerEvent {
                at: 1_000,
                provider_key: Some("k1".to_owned()),
                provider: "Name".to_owned(),
                open_ms: 249_000,
            }]
        );
        assert!(open.is_empty());
    }

    #[test]
    fn all_time_history_draws_weeks_once_days_would_crowd_it() {
        assert_eq!(Window::All.shape(0).2, DAY_MS);
        assert_eq!(Window::All.shape(DAILY_BARS * DAY_MS).2, DAY_MS);
        assert_eq!(Window::All.shape(DAILY_BARS * DAY_MS + 1).2, 7 * DAY_MS);
        assert_eq!(Window::Month.shape(400 * DAY_MS).2, 8 * HOUR_MS);
    }

    #[test]
    fn percentile_interpolates_inside_the_bucket() {
        let mut buckets = [0; LATENCY_BUCKETS];
        // 10 requests in [512, 1024) µs, 10 in [1024, 2048) µs.
        buckets[10] = 10;
        buckets[11] = 10;
        assert_eq!(percentile(&buckets, 500), Some(1024));
        assert_eq!(percentile(&buckets, 950), Some(1024 + 1024 * 9 / 10));
        assert_eq!(percentile(&[0; LATENCY_BUCKETS], 500), None);
    }

    #[test]
    fn workloads_split_provider_traffic_by_who_asked() {
        let (ok, missing, cancelled) = (
            Outcome::Retrieved.label(),
            Outcome::NotFound.label(),
            Outcome::Cancelled.label(),
        );
        let sums: HashMap<String, u64> = [
            ("p.a.stream.bytes".to_owned(), 100),
            ("p.b.stream.bytes".to_owned(), 50),
            ("p.a.health.bytes".to_owned(), 7),
            (format!("p.a.stream.{ok}"), 3),
            (format!("p.b.stream.{missing}"), 2),
            (format!("p.a.stream.{cancelled}"), 9),
            ("p.a.trips".to_owned(), 1),
        ]
        .into_iter()
        .collect();
        let split: Vec<_> = workloads(&sums)
            .iter()
            .map(|w| (w.workload, w.bytes, w.requests, w.missing))
            .collect();
        assert_eq!(
            split,
            [
                ("stream", 150, 5, 2),
                ("queue", 0, 0, 0),
                ("health", 7, 0, 0)
            ]
        );
    }
}
