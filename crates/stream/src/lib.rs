//! Seekable reads over a file's Usenet segments, fetched ahead of the reader in order.

mod cache;
mod disk;
mod gaps;

use std::collections::{HashMap, VecDeque};
use std::future::Future;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::task::{Context, Poll};
use std::time::Duration;

use bytes::Bytes;
use infinidysk_core::repair::Omitted;
use infinidysk_core::trace::{Event, Probe, Step, Trace};
use infinidysk_core::wait::{Stage, Wait, Watch};
use infinidysk_nntp::{Client, Error, Progress, RepairKey, Workload};
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore, mpsc, oneshot};
use tokio::task::JoinHandle;
use tokio::time::{Instant, Sleep, sleep_until};

pub use cache::{Cache, CacheConfig, CacheStats, DiskConfig};
pub use disk::{DiskStats, free_space};
pub use gaps::Gaps;

/// Least segments fetched ahead right after opening or a far seek. Until the first one is
/// read the window is one per connection, so a probe read stays cheap; then it opens fully.
const MIN_INITIAL_AHEAD: usize = 8;
/// Cap on initial segment bytes queued before sequential reading ramps up.
const INITIAL_AHEAD_BYTES: u64 = 16 * 1024 * 1024;
/// Segments at least this large ramp the window up per segment read.
const RAMP_SEGMENT_BYTES: u64 = 1024 * 1024;
/// Reads shorter than this (to end of file) do not prewarm connections.
const MIN_PREWARM_BYTES: u64 = 8 * 1024 * 1024;
/// A head fetch slower than this multiple of the reader's median fetch time, and at
/// least [`MIN_HEDGE_DELAY`], is raced by a duplicate once a later segment has arrived,
/// or sooner while it still waits for a response and the preferred provider has room.
const HEDGE_FACTOR: u32 = 2;
const MIN_HEDGE_DELAY: Duration = Duration::from_millis(500);
/// Fetch times kept for the median.
const LATENCY_SAMPLES: usize = 32;
/// How often a late head checks again for a later arrival or spare capacity.
const HEDGE_RECHECK: Duration = Duration::from_millis(50);
/// Traced reads sample their read-ahead this often, while being read.
const SAMPLE_EVERY: Duration = Duration::from_secs(1);
/// Fetches at the front of the window whose bodies stream in as they arrive, so the
/// next segment's first bytes are ready when the reader gets there. The rest arrive
/// whole: streaming them too would hold a second copy of each body, outside the
/// budget, until it is read.
const STREAMED_FETCHES: usize = 2;

/// Why a segment was zero-filled: no provider has it, or every copy fails to decode.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Fault {
    Missing,
    Corrupt,
}

/// Told the layout index of every segment zero-filled for a [`Fault`].
pub type Report = Arc<dyn Fn(usize, Fault) + Send + Sync>;

/// Admit a segment before reading repairs, caches, filler or providers.
pub type Access = Arc<
    dyn for<'a> Fn(&'a str) -> Pin<Box<dyn Future<Output = io::Result<()>> + Send + 'a>>
        + Send
        + Sync,
>;

/// Bytes standing in for a segment no provider can serve.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Filler {
    #[default]
    Zeros,
    /// MPEG-TS null packets, which demuxers drop and resync after.
    Ts188,
    /// M2TS null packets: a zero 4-byte timestamp before each MPEG-TS packet.
    Ts192,
}

const TS_NULL_HEADER: [u8; 4] = [0x47, 0x1F, 0xFF, 0x10];

impl Filler {
    /// The filler for a file named `name`, by extension.
    pub fn for_name(name: &str) -> Self {
        let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
        if ext.eq_ignore_ascii_case("ts") {
            Self::Ts188
        } else if ext.eq_ignore_ascii_case("m2ts") || ext.eq_ignore_ascii_case("mts") {
            Self::Ts192
        } else {
            Self::Zeros
        }
    }

    /// `size` bytes for a gap at file offset `at`. Packets cut by the gap's edges stay
    /// zero: marking them null would make a demuxer drop real bytes around the gap.
    pub fn fill(self, at: u64, size: usize) -> Vec<u8> {
        let mut out = vec![0; size];
        let (packet, prefix) = match self {
            Self::Zeros => return out,
            Self::Ts188 => (188_usize, 0),
            Self::Ts192 => (192, 4),
        };
        let skew = usize::try_from(at % packet as u64).expect("below the packet size");
        let first = (packet - skew) % packet;
        for start in (first..).step_by(packet).take_while(|s| s + packet <= size) {
            let header = start + prefix;
            out[header..header + 4].copy_from_slice(&TS_NULL_HEADER);
            out[header + 4..start + packet].fill(0xFF);
        }
        out
    }
}

#[derive(Debug, Clone)]
pub struct Segment {
    /// Without angle brackets.
    pub message_id: String,
    /// Exact decoded size.
    pub size: u64,
    /// `=ypart` offset in the posted file, when known; a provider serving another part
    /// then fails over instead of being played.
    pub begin: Option<u64>,
}

/// A file's segments in order.
#[derive(Debug)]
pub struct Layout {
    segments: Vec<Segment>,
    /// Cumulative end offset of each segment.
    ends: Vec<u64>,
    omitted: HashMap<usize, Omitted>,
}

impl Layout {
    pub fn new(segments: Vec<Segment>) -> Self {
        let ends = segments
            .iter()
            .scan(0_u64, |end, segment| {
                *end = end.saturating_add(segment.size);
                Some(*end)
            })
            .collect();
        Self {
            segments,
            ends,
            omitted: HashMap::new(),
        }
    }

    /// Attach trusted source identities only to exact, omitted layout slots.
    pub fn with_omitted(
        mut self,
        keys: impl IntoIterator<Item = (usize, Omitted)>,
    ) -> io::Result<Self> {
        for (index, key) in keys {
            let Some(segment) = self.segments.get(index) else {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "omitted segment index",
                ));
            };
            if !segment.message_id.is_empty()
                || segment.begin != Some(key.range.start)
                || key.range.is_empty()
                || segment.size != key.range.len()
                || key.range.end > key.file_length
                || key.generation < 0
                || self.omitted.insert(index, key).is_some()
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "omitted segment identity",
                ));
            }
        }
        Ok(self)
    }

    pub fn len(&self) -> u64 {
        self.ends.last().copied().unwrap_or(0)
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    fn start(&self, index: usize) -> u64 {
        index.checked_sub(1).map_or(0, |prev| self.ends[prev])
    }

    /// Segment holding `offset`, which must be below [`len`](Self::len).
    fn locate(&self, offset: u64) -> usize {
        self.ends.partition_point(|&end| end <= offset)
    }
}

/// Decoded bytes all readers sharing it may hold, from before a segment's request until
/// the reader moves past it. Waiters are served first come, first served; a segment
/// larger than the whole budget waits until nothing else is held.
///
/// Only read-ahead waits: a segment a read is blocked on stops waiting and goes over
/// budget, so readers that hold their read-ahead without reading (an idle handle, a
/// client blocked on another file) can slow others but never freeze them.
#[derive(Debug, Clone)]
pub struct Budget {
    semaphore: Arc<Semaphore>,
    bytes: u32,
    waiting: Arc<AtomicUsize>,
}

impl Budget {
    pub fn new(bytes: usize) -> Self {
        let bytes = bytes.clamp(1, Semaphore::MAX_PERMITS);
        Self {
            semaphore: Arc::new(Semaphore::new(bytes)),
            // A single lease is a u32; any real segment is far below that.
            bytes: u32::try_from(bytes).unwrap_or(u32::MAX),
            waiting: Arc::default(),
        }
    }

    /// Bytes not held by any reader.
    pub fn available(&self) -> usize {
        self.semaphore.available_permits()
    }

    pub const fn capacity(&self) -> u32 {
        self.bytes
    }

    /// Readers waiting for bytes to free up.
    pub fn waiters(&self) -> usize {
        self.waiting.load(Ordering::Relaxed)
    }
}

/// Counts a reader waiting on the [`Budget`] until dropped.
struct Waiting<'a>(&'a AtomicUsize);

impl Drop for Waiting<'_> {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::Relaxed);
    }
}

/// Budget one segment holds, counted on its reader's [`Watch`] until dropped.
struct Held {
    _permit: OwnedSemaphorePermit,
    bytes: u64,
    watch: Option<Arc<Watch>>,
}

impl Drop for Held {
    fn drop(&mut self) {
        if let Some(watch) = &self.watch {
            watch.release(self.bytes);
        }
    }
}

/// One fetch's claim on the [`Budget`], taken only after the previous fetch of the same
/// reader took its own, so a reader's segments acquire in order and its head is never
/// starved by segments queued behind it.
struct Lease {
    budget: Budget,
    bytes: u32,
    previous: Option<oneshot::Receiver<()>>,
    acquired: oneshot::Sender<()>,
    /// Fires when the reader blocks on this segment: stop waiting, fetch unbudgeted.
    urgent: Arc<Notify>,
    watch: Option<Arc<Watch>>,
}

impl Lease {
    async fn acquire(self) -> Option<Held> {
        let Self {
            budget,
            bytes,
            previous,
            acquired,
            urgent,
            watch,
        } = self;
        let wait = async {
            if let Some(previous) = previous {
                // An error means the previous fetch is gone, which also unblocks this one.
                let _ = previous.await;
            }
            let semaphore = Arc::clone(&budget.semaphore);
            if let Ok(permit) = Arc::clone(&semaphore).try_acquire_many_owned(bytes) {
                return Some(permit);
            }
            budget.waiting.fetch_add(1, Ordering::Relaxed);
            let _waiting = Waiting(&budget.waiting);
            semaphore.acquire_many_owned(bytes).await.ok()
        };
        let permit = tokio::select! {
            biased;
            permit = wait => permit,
            () = urgent.notified() => None,
        };
        let _ = acquired.send(());
        let bytes = u64::from(bytes);
        permit.map(|permit| {
            if let Some(watch) = &watch {
                watch.hold(bytes);
            }
            Held {
                _permit: permit,
                bytes,
                watch,
            }
        })
    }
}

/// A segment's bytes, their budget lease, and whether they are filler.
type Fetched = (Bytes, Option<Held>, bool);

struct Fetch {
    /// Also yields how long the request itself took, for the hedge threshold.
    task: JoinHandle<io::Result<(Fetched, Duration)>>,
    started: Instant,
    /// The reader has needed this fetch as its head.
    waited: bool,
    /// Bytes of a fetch the reader was already waiting on when it started, served
    /// before the whole article is in.
    partial: Option<Partial>,
    /// Its lease's [`Lease::urgent`], until fired.
    urgent: Option<Arc<Notify>>,
    /// What it waits on, for pre-response hedging and the reader's [`Watch`].
    wait: Option<Arc<Wait>>,
}

struct Partial {
    rx: mpsc::UnboundedReceiver<(usize, Vec<u8>)>,
    data: Vec<u8>,
    /// The fetch's [`Progress::wanted`], set once it is among the first
    /// [`STREAMED_FETCHES`] of the window.
    wanted: Arc<AtomicBool>,
}

impl Partial {
    /// Bytes received so far.
    fn poll(&mut self, cx: &mut Context<'_>) -> &[u8] {
        while let Poll::Ready(Some((at, chunk))) = self.rx.poll_recv(cx) {
            // A retry on another provider resends from offset 0; keep only new bytes.
            if let Some(new) = self
                .data
                .len()
                .checked_sub(at)
                .and_then(|seen| chunk.get(seen..))
            {
                self.data.extend_from_slice(new);
            }
        }
        &self.data
    }
}

/// [`AsyncRead`] + [`AsyncSeek`] over a [`Layout`]. Keeps up to `prefetch` segments in
/// flight ahead of the read position; a seek inside that window keeps them.
pub struct SegmentReader {
    client: Arc<Client>,
    layout: Arc<Layout>,
    prefetch: usize,
    prewarm: bool,
    /// Background-priority reads that fail instead of filling, padding or cutting.
    strict: bool,
    budget: Option<Budget>,
    cache: Option<Arc<Cache>>,
    /// Segments from here on are fetched only when read: the caller's range ends before.
    end: usize,
    /// The caller's range ends before the file does, so it is read whole: the window
    /// opens fully at once.
    bounded: bool,
    /// Fires once the newest scheduled fetch holds its budget.
    acquired: Option<oneshot::Receiver<()>>,
    pos: u64,
    /// Fetches for segments `head..next`, in order.
    pending: VecDeque<Fetch>,
    head: usize,
    next: usize,
    /// Segments read or skipped since the window last reset.
    consumed: usize,
    /// Read-ahead before the first segment since a reset is read.
    initial: usize,
    current: Option<(usize, Fetched)>,
    /// The segment read before `current`, kept outside the budget so reads that arrive
    /// slightly out of order (kernel readahead) do not reset the window.
    previous: Option<(usize, Bytes)>,
    latencies: VecDeque<Duration>,
    /// Duplicate request racing a slow head fetch; unbudgeted.
    hedge: Option<JoinHandle<io::Result<(Bytes, bool)>>>,
    recheck: Option<Pin<Box<Sleep>>>,
    trace: Option<Arc<Trace>>,
    watch: Option<Arc<Watch>>,
    report: Option<Report>,
    access: Option<Access>,
    filler: Filler,
    gaps: Option<Arc<Gaps>>,
    /// Segment the reader has been waiting for, and since when; traced reads only.
    waiting: Option<(usize, Instant)>,
    /// When the last [`Event::Sample`] was recorded.
    sampled: Option<Instant>,
}

impl SegmentReader {
    pub fn new(client: Arc<Client>, layout: Arc<Layout>, prefetch: usize) -> Self {
        let end = layout.segments.len();
        let prefetch = prefetch.max(1);
        let initial = client
            .max_connections()
            .min(prefetch / 2)
            .max(MIN_INITIAL_AHEAD)
            .min(prefetch);
        Self {
            client,
            layout,
            prefetch,
            prewarm: true,
            strict: false,
            budget: None,
            cache: None,
            end,
            bounded: false,
            acquired: None,
            pos: 0,
            pending: VecDeque::new(),
            // Outside any window, so the first read starts one like a far seek.
            head: usize::MAX,
            next: usize::MAX,
            consumed: 0,
            initial,
            current: None,
            previous: None,
            latencies: VecDeque::with_capacity(LATENCY_SAMPLES),
            hedge: None,
            recheck: None,
            trace: None,
            watch: None,
            report: None,
            access: None,
            filler: Filler::Zeros,
            gaps: None,
            waiting: None,
            sampled: None,
        }
    }

    /// Whether opening or a far seek opens connections for the window ahead (default on).
    #[must_use]
    pub const fn with_prewarm(mut self, prewarm: bool) -> Self {
        self.prewarm = prewarm;
        self
    }

    /// Reads for analysis: health-priority fetches, no prewarm, hedges or partial bodies,
    /// and an error instead of any byte that is not exactly as posted.
    #[must_use]
    pub const fn with_strict(mut self) -> Self {
        self.strict = true;
        self.prewarm = false;
        self
    }

    /// Shares `budget` with other readers to bound decoded bytes held in memory.
    #[must_use]
    pub fn with_budget(mut self, budget: Budget) -> Self {
        self.budget = Some(budget);
        self
    }

    /// Serves and keeps segments through `cache`, shared with other readers.
    #[must_use]
    pub fn with_cache(mut self, cache: Arc<Cache>) -> Self {
        self.cache = Some(cache);
        self
    }

    /// Limits read-ahead to bytes before `end`, where the caller's range stops. Reads
    /// past it still work, one segment at a time.
    pub fn set_end(&mut self, end: u64) {
        self.bounded = end < self.layout.len();
        self.end = match end.min(self.layout.len()) {
            0 => 0,
            end => self.layout.locate(end - 1) + 1,
        };
    }

    /// Records every segment's fetch steps and the reader's waits into `trace`.
    #[must_use]
    pub fn with_trace(mut self, trace: Arc<Trace>) -> Self {
        self.trace = Some(trace);
        self
    }

    /// Keeps `watch` up to date with the segment the reader is blocked on and the budget
    /// it holds.
    #[must_use]
    pub fn with_watch(mut self, watch: Arc<Watch>) -> Self {
        self.watch = Some(watch);
        self
    }

    /// Tells `report` about every segment zero-filled for a [`Fault`].
    #[must_use]
    pub fn with_report(mut self, report: Report) -> Self {
        self.report = Some(report);
        self
    }

    /// Checks publication admission before fetching each segment, including cache hits.
    #[must_use]
    pub fn with_access(mut self, access: Access) -> Self {
        self.access = Some(access);
        self
    }

    /// Fills unavailable segments with `filler` instead of zeros. Layout offsets must be
    /// the file's own, or packets land off the file's grid.
    #[must_use]
    pub const fn with_filler(mut self, filler: Filler) -> Self {
        self.filler = filler;
        self
    }

    /// Shares the file's holes with its other readers: known holes are filled without
    /// asking providers, and reads fail once [`Gaps`] says the damage is too much.
    #[must_use]
    pub fn with_gaps(mut self, gaps: Arc<Gaps>) -> Self {
        self.gaps = Some(gaps);
        self
    }

    fn hole(&self, index: usize) -> Hole {
        Hole {
            index,
            at: self.layout.start(index),
            filler: self.filler,
            report: self.report.clone(),
            access: self.access.clone(),
            gaps: self.gaps.clone(),
            omitted: self.layout.omitted.get(&index).copied(),
        }
    }

    fn note(&self, event: Event) {
        if let Some(trace) = &self.trace {
            trace.record(event);
        }
    }

    /// Records the read-ahead's state at most once per [`SAMPLE_EVERY`], and as each
    /// stall begins.
    fn sample(&mut self) {
        let Some(trace) = &self.trace else { return };
        let now = Instant::now();
        if self
            .sampled
            .is_some_and(|at| now.duration_since(at) < SAMPLE_EVERY)
        {
            return;
        }
        self.sampled = Some(now);
        let ready = self.pending.iter().filter(|f| f.task.is_finished()).count();
        let count = |n: usize| u32::try_from(n).unwrap_or(u32::MAX);
        let (connections, idle) = self.client.connections();
        trace.record(Event::Sample {
            in_flight: count(self.pending.len() - ready),
            ready: count(ready),
            budget_free: self.budget.as_ref().map_or(0, |b| b.available() as u64),
            permits_free: count(self.client.free_permits().unwrap_or(0)),
            window: count(self.ahead()),
            connections: count(connections),
            idle_connections: count(idle),
        });
    }

    fn probe(&self, index: usize, wait: Option<Arc<Wait>>, hedge: bool) -> Probe {
        Probe {
            trace: self.trace.clone(),
            wait,
            watch: self.watch.clone(),
            segment: segment_id(index),
            hedge,
            size: u32::try_from(self.layout.segments[index].size).unwrap_or(u32::MAX),
        }
    }

    fn clear_head(&self) {
        if let Some(watch) = &self.watch {
            watch.set_head(None);
        }
    }

    /// Drops fetches before `index`, or all of them when `index` is outside the window.
    fn align(&mut self, index: usize) {
        if index < self.head || index > self.next {
            self.abort_all();
            (self.head, self.next, self.consumed) = (index, index, 0);
            self.note(Event::Window {
                segment: segment_id(index),
            });
            self.start_prewarm(index);
        } else if index > self.head {
            // Includes segments streamed out before their fetch finished.
            self.consumed = self.consumed.saturating_add(index - self.head);
        }
        while self.head < index {
            if let Some(fetch) = self.pending.pop_front() {
                self.cancel(fetch, self.head);
            }
            self.drop_hedge();
            self.head += 1;
        }
    }

    /// Aborts the fetch of segment `index`, unless the reader already streamed all of it:
    /// that one finishes in the background so a CRC failure is still reported.
    fn cancel(&self, fetch: Fetch, index: usize) {
        let size = usize::try_from(self.layout.segments[index].size).unwrap_or(usize::MAX);
        if fetch.partial.is_none_or(|p| p.data.len() < size) {
            fetch.task.abort();
        }
    }

    /// Opens connections in the background so the opening burst skips handshakes.
    fn start_prewarm(&self, index: usize) {
        let end = self.end.max(index + 1);
        let after = end - index - 1;
        let target = after.min(self.prefetch);
        if !self.prewarm
            || target < 2
            || self.layout.start(end) - self.layout.start(index) < MIN_PREWARM_BYTES
        {
            return;
        }
        let client = Arc::clone(&self.client);
        tokio::spawn(async move { client.prewarm(target).await });
    }

    /// Segments to keep in flight ahead of the reader. Large articles grow the window by
    /// the opening size per segment read, so an open does not queue the whole prefetch.
    fn ahead(&self) -> usize {
        if self.bounded {
            return self.prefetch;
        }
        let step = self.layout.segments.first().map_or(1, |s| s.size.max(1));
        if step >= RAMP_SEGMENT_BYTES {
            let base = usize::try_from((INITIAL_AHEAD_BYTES / step).max(4))
                .unwrap_or(usize::MAX)
                .min(self.initial);
            return base
                .saturating_add(self.consumed.saturating_mul(base))
                .min(self.prefetch);
        }
        if self.consumed > 0 {
            self.prefetch
        } else {
            self.initial
        }
    }

    fn schedule(&mut self, read_index: usize) {
        let ahead = self.ahead();
        // The segment being read is always fetched, even past the range end.
        let end = self.end.max(read_index + 1).min(self.layout.segments.len());
        while self.next < end && self.next - self.head < ahead {
            let (client, layout, cache, hole, index, strict) = (
                Arc::clone(&self.client),
                Arc::clone(&self.layout),
                self.cache.clone(),
                self.hole(self.next),
                self.next,
                self.strict,
            );
            if !strict
                && !client.may_repair(match &hole.omitted {
                    Some(key) => RepairKey::Omitted(key),
                    None => RepairKey::Article(&layout.segments[index].message_id),
                })
                && hole.gaps.as_ref().is_some_and(|g| g.is_hole(index))
            {
                self.pending.push_back(Fetch {
                    task: tokio::spawn(async move {
                        let (data, filled) = fetch(
                            &client,
                            Source::Direct,
                            &layout.segments[index],
                            None,
                            None,
                            &hole,
                            false,
                        )
                        .await?;
                        Ok(((data, None, filled), Duration::ZERO))
                    }),
                    started: Instant::now(),
                    waited: false,
                    partial: None,
                    urgent: None,
                    wait: None,
                });
                self.next += 1;
                continue;
            }
            let urgent = self.budget.as_ref().map(|_| Arc::new(Notify::new()));
            let lease = self
                .budget
                .as_ref()
                .zip(urgent.clone())
                .map(|(budget, urgent)| {
                    let (acquired, next) = oneshot::channel();
                    Lease {
                        budget: budget.clone(),
                        bytes: u32::try_from(layout.segments[index].size)
                            .unwrap_or(u32::MAX)
                            .min(budget.bytes),
                        previous: self.acquired.replace(next),
                        acquired,
                        urgent,
                        watch: self.watch.clone(),
                    }
                });
            let wait = (!strict || self.watch.is_some()).then(|| Arc::new(Wait::default()));
            let probe = self.probe(index, wait.clone(), false);
            probe.step(Step::Queued);
            // Bodies stream in once armed below, so a segment is readable before it
            // fully arrives.
            let wanted = Arc::new(AtomicBool::new(false));
            let (tx, rx) = mpsc::unbounded_channel();
            let progress = Progress {
                tx,
                wanted: Arc::clone(&wanted),
            };
            let partial = (!strict).then(|| Partial {
                rx,
                data: Vec::new(),
                wanted,
            });
            self.pending.push_back(Fetch {
                task: tokio::spawn(async move {
                    // Waiting for publication must not hold memory needed to drain other readers.
                    if let Some(access) = &hole.access {
                        access(&layout.segments[index].message_id).await?;
                    }
                    let leased = lease.is_some();
                    let permit = match lease {
                        Some(lease) => lease.acquire().await,
                        None => None,
                    };
                    probe.step(if leased && permit.is_none() {
                        Step::Bypassed
                    } else {
                        Step::Budgeted
                    });
                    let started = Instant::now();
                    let source = match &cache {
                        Some(cache) => Source::Shared(cache),
                        None => Source::Direct,
                    };
                    let (data, filled) = fetch(
                        &client,
                        source,
                        &layout.segments[index],
                        Some(&probe),
                        (!strict).then_some(progress),
                        &hole,
                        strict,
                    )
                    .await?;
                    Ok(((data, permit, filled), started.elapsed()))
                }),
                started: Instant::now(),
                waited: false,
                partial,
                urgent,
                wait,
            });
            self.next += 1;
        }
        // A finished body is served whole; its streamed copy only costs memory.
        for fetch in self.pending.iter_mut().skip(1) {
            if fetch.partial.is_some() && fetch.task.is_finished() {
                fetch.partial = None;
            }
        }
        // Only the segments read next stream in early: the rest are served whole, and
        // streaming them would hold a second copy of each body until it is read.
        for partial in self
            .pending
            .iter()
            .take(STREAMED_FETCHES)
            .filter_map(|fetch| fetch.partial.as_ref())
        {
            partial.wanted.store(true, Ordering::Relaxed);
        }
    }

    fn abort_all(&mut self) {
        for (i, fetch) in std::mem::take(&mut self.pending).into_iter().enumerate() {
            self.cancel(fetch, self.head + i);
        }
        self.drop_hedge();
        self.acquired = None;
        self.clear_head();
    }

    fn drop_hedge(&mut self) {
        if let Some(hedge) = self.hedge.take() {
            hedge.abort();
        }
    }

    fn hedge_delay(&self) -> Duration {
        let mut sorted: Vec<Duration> = self.latencies.iter().copied().collect();
        sorted.sort_unstable();
        sorted
            .get(sorted.len() / 2)
            .map_or(MIN_HEDGE_DELAY, |median| {
                (*median * HEDGE_FACTOR).max(MIN_HEDGE_DELAY)
            })
    }

    /// The head segment, from its fetch or from a hedge that beat it.
    fn poll_head(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<Fetched>> {
        let now = Instant::now();
        let head = self
            .pending
            .front_mut()
            .expect("index is below the segment count, so schedule queued it");
        if let Some(urgent) = head.urgent.take() {
            urgent.notify_one();
        }
        if !head.waited
            && let Some(watch) = &self.watch
        {
            watch.set_head(Some((segment_id(self.head), head.wait.clone())));
        }
        head.waited = true;
        let started = head.started;
        let waiting_for_response = head
            .wait
            .as_ref()
            .is_some_and(|wait| matches!(wait.view().stage, Stage::Pipeline | Stage::Reply));
        if let Poll::Ready(result) = Pin::new(&mut head.task).poll(cx) {
            self.pending.pop_front();
            self.drop_hedge();
            self.clear_head();
            let (fetched, took) = result.map_err(io::Error::other)??;
            if !fetched.2 {
                if self.latencies.len() == LATENCY_SAMPLES {
                    self.latencies.pop_front();
                }
                self.latencies.push_back(took);
            }
            return Poll::Ready(Ok(fetched));
        }
        if self.strict {
            return Poll::Pending;
        }
        if let Some(hedge) = &mut self.hedge {
            match Pin::new(hedge).poll(cx) {
                Poll::Ready(Ok(Ok((data, filled)))) => {
                    self.hedge = None;
                    if let Some(head) = self.pending.pop_front() {
                        head.task.abort();
                    }
                    self.clear_head();
                    self.note(Event::HedgeWon {
                        segment: segment_id(self.head),
                    });
                    return Poll::Ready(Ok((data, None, filled)));
                }
                // A failed hedge leaves the head fetch to finish on its own.
                Poll::Ready(_) => self.hedge = None,
                Poll::Pending => {}
            }
            return Poll::Pending;
        }
        let due = started + self.hedge_delay();
        // With nothing behind the head (end of file) there is no later arrival to wait for.
        let later =
            self.pending.len() == 1 || self.pending.iter().skip(1).any(|f| f.task.is_finished());
        if now >= due
            && (later
                || (waiting_for_response
                    && self.client.can_hedge(
                        u32::try_from(self.layout.segments[self.head].size).unwrap_or(u32::MAX),
                    )))
        {
            let (client, layout, cache, hole, index) = (
                Arc::clone(&self.client),
                Arc::clone(&self.layout),
                self.cache.clone(),
                self.hole(self.head),
                self.head,
            );
            tracing::debug!(segment = index, "hedging slow segment");
            self.note(Event::Hedge {
                segment: segment_id(index),
            });
            let probe = self.probe(index, None, true);
            self.hedge = Some(tokio::spawn(async move {
                let source = Source::Hedge(cache.as_ref());
                fetch(
                    &client,
                    source,
                    &layout.segments[index],
                    Some(&probe),
                    None,
                    &hole,
                    false,
                )
                .await
            }));
            cx.waker().wake_by_ref();
            return Poll::Pending;
        }
        let at = if now >= due { now + HEDGE_RECHECK } else { due };
        let recheck = self
            .recheck
            .get_or_insert_with(|| Box::pin(sleep_until(at)));
        recheck.as_mut().reset(at);
        if recheck.as_mut().poll(cx).is_ready() {
            cx.waker().wake_by_ref();
        }
        Poll::Pending
    }
}

impl AsyncRead for SegmentReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        self.get_mut().read_into(cx, buf)
    }
}

impl SegmentReader {
    fn read_into(&mut self, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<io::Result<()>> {
        if self.pos >= self.layout.len() || buf.remaining() == 0 {
            return Poll::Ready(Ok(()));
        }
        self.sample();
        let index = self.layout.locate(self.pos);
        let data = match (&self.current, &self.previous) {
            (Some((held, (data, _, _))), _) | (_, Some((held, data))) if *held == index => data,
            _ => {
                if let Some((held, (data, _, _))) = self.current.take() {
                    // Its budget must not block the segment replacing it.
                    self.previous = Some((held, data));
                }
                self.align(index);
                self.schedule(index);
                let Poll::Ready(result) = self.poll_head(cx) else {
                    if self.read_partial(cx, index, buf) {
                        // Bytes reached the reader: the wait ends here, not when the body does.
                        if let Some((at, since)) = self.waiting.take()
                            && at == index
                        {
                            let ns = u64::try_from(since.elapsed().as_nanos()).unwrap_or(u64::MAX);
                            self.note(Event::Stall {
                                segment: segment_id(index),
                                ns,
                            });
                        }
                        return Poll::Ready(Ok(()));
                    }
                    if self.trace.is_some() && self.waiting.is_none_or(|(at, _)| at != index) {
                        // Sampled first so the summary finds it at or before the stall.
                        self.sampled = None;
                        self.sample();
                        self.waiting = Some((index, Instant::now()));
                    }
                    return Poll::Pending;
                };
                self.head += 1;
                self.consumed = self.consumed.saturating_add(1);
                // A failed segment is fetched again by the next read at this position.
                let data = result?;
                if let Some(gaps) = self.gaps.as_ref().filter(|_| !self.strict) {
                    if data.2 {
                        let layout = &self.layout;
                        let sizes = || layout.segments.iter().map(|s| s.size).collect();
                        if let Err(error) = gaps.filled(index, sizes) {
                            if let Some(suppressed) = gaps.log() {
                                tracing::warn!(reason = %error, suppressed, "stopping a read over too much damage");
                            }
                            return Poll::Ready(Err(error));
                        }
                    } else {
                        gaps.good();
                    }
                }
                let segment = segment_id(index);
                if let Some((at, since)) = self.waiting.take()
                    && at == index
                {
                    let ns = u64::try_from(since.elapsed().as_nanos()).unwrap_or(u64::MAX);
                    self.note(Event::Stall { segment, ns });
                }
                self.note(Event::Served { segment });
                self.current = Some((index, data));
                self.schedule(index);
                &self.current.as_ref().expect("held segment set above").1.0
            }
        };
        let offset = usize::try_from(self.pos - self.layout.start(index))
            .expect("offset lies within a segment held in memory");
        let n = buf.remaining().min(data.len() - offset);
        buf.put_slice(&data[offset..offset + n]);
        self.pos += n as u64;
        Poll::Ready(Ok(()))
    }

    /// Serves the head segment's bytes already received; `false` when none are new.
    // ponytail: streamed bytes are served before the article's CRC is checked, so a
    // corrupt copy that a later provider replaces leaves its prefix served (a fetch the
    // reader streamed whole still finishes and reports a segment no provider has intact);
    // buffer the head whole if that shows up in practice.
    fn read_partial(&mut self, cx: &mut Context<'_>, index: usize, buf: &mut ReadBuf<'_>) -> bool {
        let Some(partial) = self.pending.front_mut().and_then(|f| f.partial.as_mut()) else {
            return false;
        };
        let data = partial.poll(cx);
        let size = usize::try_from(self.layout.segments[index].size).unwrap_or(usize::MAX);
        let offset = usize::try_from(self.pos - self.layout.start(index))
            .expect("offset lies within a segment held in memory");
        let available = data.len().min(size);
        if available <= offset {
            return false;
        }
        let n = buf.remaining().min(available - offset);
        buf.put_slice(&data[offset..offset + n]);
        self.pos += n as u64;
        true
    }
}

impl AsyncSeek for SegmentReader {
    fn start_seek(mut self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let pos = match position {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::End(delta) => self.layout.len().checked_add_signed(delta),
            SeekFrom::Current(delta) => self.pos.checked_add_signed(delta),
        };
        self.pos = pos.ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "seek before start of file")
        })?;
        Ok(())
    }

    fn poll_complete(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        Poll::Ready(Ok(self.pos))
    }
}

impl Drop for SegmentReader {
    fn drop(&mut self) {
        self.abort_all();
    }
}

/// Segment index as recorded in traces.
fn segment_id(index: usize) -> u32 {
    u32::try_from(index).unwrap_or(u32::MAX)
}

/// Where [`fetch`] gets a body.
enum Source<'a> {
    Direct,
    /// Through the cache, joining a fetch already in flight.
    Shared(&'a Arc<Cache>),
    /// From providers even when a fetch is in flight, keeping the result when cached.
    Hedge(Option<&'a Arc<Cache>>),
}

/// Where a segment sits, should it need filling.
struct Hole {
    index: usize,
    /// File offset of the segment's first byte.
    at: u64,
    filler: Filler,
    report: Option<Report>,
    access: Option<Access>,
    gaps: Option<Arc<Gaps>>,
    omitted: Option<Omitted>,
}

/// Body sized exactly to the segment. Articles unusable on every provider are filled
/// so playback continues and later offsets stay right, and reported with their layout
/// index; `strict` fails instead.
async fn fetch(
    client: &Arc<Client>,
    source: Source<'_>,
    segment: &Segment,
    probe: Option<&Probe>,
    progress: Option<Progress>,
    hole: &Hole,
    strict: bool,
) -> io::Result<(Bytes, bool)> {
    if let Some(access) = &hole.access {
        access(&segment.message_id).await?;
    }
    let size = usize::try_from(segment.size)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "segment too large"))?;
    let (id, begin) = (&segment.message_id, segment.begin);
    let repaired = match &hole.omitted {
        Some(key) => client.repaired_omitted(key).await,
        None => client.repaired(id, begin).await,
    }
    .map_err(io::Error::other)?;
    if let Some(body) = repaired {
        if body.data.len() != size {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "repair size differs from layout",
            ));
        }
        if let Some(gaps) = &hole.gaps {
            gaps.repaired(hole.index);
        }
        return Ok((body.data, false));
    }
    if !strict && hole.gaps.as_ref().is_some_and(|g| g.is_hole(hole.index)) {
        return Ok((hole.filler.fill(hole.at, size).into(), true));
    }
    let workload = if strict {
        Workload::Health
    } else {
        Workload::Stream
    };
    let body = if id.is_empty() {
        Err(Arc::new(Error::NotFound))
    } else {
        match source {
            Source::Direct => client
                .provider_body_with(id, workload, probe, progress.as_ref(), begin)
                .await
                .map(|body| body.data)
                .map_err(Arc::new),
            Source::Shared(cache) => cache.body(client, id, begin, probe, progress).await,
            Source::Hedge(Some(cache)) => cache.hedge(client, id, begin, probe).await,
            Source::Hedge(None) => client
                .hedge(id, probe, begin)
                .await
                .map(|body| body.data)
                .map_err(Arc::new),
        }
    };
    match body {
        Ok(data) if data.len() == size => Ok((data, false)),
        Ok(_) if strict => Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "decoded size differs from layout",
        )),
        Ok(data) => {
            if let Some(suppressed) = hole.gaps.as_ref().map_or(Some(0), |gaps| gaps.log()) {
                tracing::warn!(
                    message_id = %segment.message_id,
                    size,
                    decoded = data.len(),
                    reason = "decoded size differs from layout",
                    suppressed,
                    "aligning segment to layout size"
                );
            }
            // ponytail: with a known `begin` a wrong part already failed over; a size
            // that still disagrees is padded or cut with a warning.
            let mut data = data.to_vec();
            data.resize(size, 0);
            Ok((data.into(), false))
        }
        Err(error)
            if !strict
                && matches!(
                    *error,
                    Error::NotFound | Error::CrcMismatch { .. } | Error::Yenc(_)
                ) =>
        {
            let suppressed = hole.gaps.as_ref().map_or(Some(0), |gaps| {
                gaps.found(hole.index);
                gaps.log()
            });
            if let Some(suppressed) = suppressed {
                tracing::warn!(
                    message_id = %segment.message_id,
                    size,
                    reason = %error,
                    suppressed,
                    "filling unavailable segment"
                );
            }
            if let Some(report) = &hole.report {
                let fault = match *error {
                    Error::NotFound => Fault::Missing,
                    _ => Fault::Corrupt,
                };
                report(hole.index, fault);
            }
            Ok((hole.filler.fill(hole.at, size).into(), true))
        }
        Err(error) => Err(io::Error::other(error)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fills_whole_ts_packets_inside_the_gap_only() {
        assert_eq!(Filler::for_name("a.MKV").fill(5, 400), vec![0; 400]);

        // Gap 100..600: packets at 188 and 376 fit, 564 runs past the end.
        let ts = Filler::for_name("a.ts").fill(100, 500);
        let null = |at: usize| {
            ts[at..at + 4] == TS_NULL_HEADER && ts[at + 4..at + 188].iter().all(|&b| b == 0xFF)
        };
        assert!(null(88) && null(276));
        assert!(ts[..88].iter().all(|&b| b == 0));
        assert!(ts[464..].iter().all(|&b| b == 0));

        let m2ts = Filler::for_name("a.m2ts").fill(0, 384);
        assert_eq!(m2ts[..4], [0; 4]);
        assert_eq!(m2ts[4..8], TS_NULL_HEADER);
        assert_eq!(m2ts[196..200], TS_NULL_HEADER);
    }
}
