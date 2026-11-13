//! Opt-in event capture for one read stream, to explain why a playback stalled.
//!
//! A stream without a [`Trace`] records nothing. With one, each event is a short lock
//! and a copy of a fixed-size value; analysis ([`Trace::summary`]) runs only on demand.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::wait::{Wait, Watch};

/// Why a request returned no article.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Reason {
    Timeout,
    Auth,
    Network,
    Protocol,
    /// The provider sent the article damaged (bad yEnc or CRC).
    Corrupt,
    Other,
}

/// How one provider request ended.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Outcome {
    Retrieved,
    /// The provider answered that it lacks the article.
    NotFound,
    /// The caller stopped waiting; the connection drained the reply.
    Cancelled,
    NotRetrieved(Reason),
}

impl Outcome {
    pub const ALL: [Self; 9] = [
        Self::Retrieved,
        Self::NotFound,
        Self::Cancelled,
        Self::NotRetrieved(Reason::Timeout),
        Self::NotRetrieved(Reason::Auth),
        Self::NotRetrieved(Reason::Network),
        Self::NotRetrieved(Reason::Protocol),
        Self::NotRetrieved(Reason::Corrupt),
        Self::NotRetrieved(Reason::Other),
    ];

    /// Position in [`ALL`](Self::ALL).
    #[must_use]
    pub const fn index(self) -> usize {
        match self {
            Self::Retrieved => 0,
            Self::NotFound => 1,
            Self::Cancelled => 2,
            Self::NotRetrieved(reason) => 3 + reason as usize,
        }
    }

    /// Stable name for metrics: the outcome, or the reason it was not retrieved.
    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Retrieved => "retrieved",
            Self::NotFound => "not_found",
            Self::Cancelled => "cancelled",
            Self::NotRetrieved(Reason::Timeout) => "timeout",
            Self::NotRetrieved(Reason::Auth) => "auth",
            Self::NotRetrieved(Reason::Network) => "network",
            Self::NotRetrieved(Reason::Protocol) => "protocol",
            Self::NotRetrieved(Reason::Corrupt) => "corrupt",
            Self::NotRetrieved(Reason::Other) => "other",
        }
    }
}

/// Where one segment fetch is; each step ends the wait named by the previous one.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "step", rename_all = "snake_case")]
pub enum Step {
    /// Scheduled ahead of the reader; next it waits for decoded-bytes budget.
    Queued,
    /// Holds its budget; next it waits for a global download permit.
    Budgeted,
    /// The reader blocked on it, so it stopped waiting for budget; next a permit.
    Bypassed,
    /// The disk cache has it; next it waits for the read.
    DiskRead,
    /// Joined `flight`, another reader's fetch of the same article; next it waits for it.
    Joined { flight: u64 },
    /// Holds a permit for an attempt on `provider`; next it waits for a connection.
    Permitted { provider: u16 },
    /// Queued on `connection` behind `ahead` earlier pipelined requests.
    Connected {
        provider: u16,
        ahead: u16,
        connection: u32,
    },
    /// Its reply is next on the connection; next it waits for the provider to answer.
    Reading { provider: u16 },
    /// The provider's answer arrived; next it waits for the transfer and decode.
    Receiving { provider: u16 },
    Done {
        provider: u16,
        outcome: Outcome,
        bytes: u32,
        /// Of the time since [`Receiving`](Self::Receiving), spent decoding.
        decode_ns: u32,
    },
}

/// Where a read request is inside the daemon; each stage ends the one before.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ReadStage {
    /// Waiting for earlier reads of the same handle, so reads reach the stream in order.
    Order,
    /// Waiting for the handle's reader, held by another request.
    #[serde(rename = "handle_lock")]
    Lock,
    /// In the stream reader: seeking and reading, including any wait for segments.
    Read,
}

impl ReadStage {
    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Order => "order",
            Self::Lock => "handle_lock",
            Self::Read => "read",
        }
    }
}

/// How the daemon answered a read request.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Reply {
    Data,
    Error,
    /// The kernel interrupted the request, or the daemon dropped it, before a reply.
    Cancelled,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Event {
    Open {
        size: u64,
    },
    /// The FUSE open that created this stream replied with `handle`, now. It spent
    /// `before_ns` before the trace started (path, caller and metadata lookups); the time
    /// from the trace start to now is the rest of the open.
    Opened {
        request: u64,
        handle: u64,
        inode: u64,
        before_ns: u64,
    },
    /// FUSE read `request` reached the daemon. It first waits in [`ReadStage::Order`].
    Request {
        request: u64,
        offset: u64,
        size: u32,
    },
    /// `request` entered `stage`.
    RequestStage {
        request: u64,
        stage: ReadStage,
    },
    /// The daemon finished `request` with `bytes`.
    Replied {
        request: u64,
        bytes: u32,
        result: Reply,
    },
    Seek {
        from: u64,
        to: u64,
    },
    /// The prefetch window restarted at `segment`.
    Window {
        segment: u32,
    },
    Segment {
        segment: u32,
        /// The step belongs to the hedge racing this segment, not its first fetch.
        #[serde(skip_serializing_if = "is_false")]
        hedge: bool,
        #[serde(flatten)]
        step: Step,
    },
    /// A duplicate request raced a slow `segment`.
    Hedge {
        segment: u32,
    },
    /// The duplicate for `segment` arrived first.
    HedgeWon {
        segment: u32,
    },
    /// `segment` came from the segment cache or another reader's fetch, not a provider.
    Cached {
        segment: u32,
        tier: Tier,
    },
    /// The reader got `segment`'s bytes.
    Served {
        segment: u32,
    },
    /// The reader waited `ns` for `segment`, ending now.
    Stall {
        segment: u32,
        ns: u64,
    },
    /// `ns` passed between two stream reads, ending now (kept from 50 ms). Not proof
    /// the client paused: for FUSE it includes request ordering and handle-lock waits,
    /// kernel and player time. Request events split the daemon's part out.
    Idle {
        ns: u64,
    },
    /// Periodic state of the read-ahead.
    Sample {
        /// Segments requested and not yet ready.
        in_flight: u32,
        /// Segments ready ahead of the reader.
        ready: u32,
        /// Decoded-bytes budget not held by any reader.
        budget_free: u64,
        /// Download permits not in use.
        permits_free: u32,
        /// Segments the reader may keep in flight right now.
        window: u32,
        /// Open provider connections, and those with no request on them.
        connections: u32,
        idle_connections: u32,
    },
    End {
        bytes: u64,
    },
    /// Not recorded: [`Trace::records`] marks where `events` were dropped, so a summary
    /// does not take the missing time for a gap.
    Dropped {
        events: u64,
    },
}

#[allow(clippy::trivially_copy_pass_by_ref)] // serde's skip_serializing_if passes a reference.
const fn is_false(b: &bool) -> bool {
    !*b
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Tier {
    Memory,
    Disk,
    /// Joined a fetch another reader had in flight.
    Shared,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
pub struct Record {
    /// Since the trace started.
    pub at_ns: u64,
    #[serde(flatten)]
    pub event: Event,
}

/// Events of one stream. Keeps the first half of `capacity` and the newest other half,
/// so both a slow start and a late stall survive a long playback.
pub struct Trace {
    started: Instant,
    /// Wall clock when `started` was taken, in ns since the Unix epoch.
    started_unix_ns: u64,
    head_cap: usize,
    tail_cap: usize,
    buf: Mutex<Buf>,
}

#[derive(Default)]
struct Buf {
    head: Vec<Record>,
    tail: VecDeque<Record>,
    dropped: u64,
}

impl Trace {
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        let capacity = capacity.max(2);
        Self {
            started: Instant::now(),
            started_unix_ns: unix_ns(SystemTime::now()),
            head_cap: capacity / 2,
            tail_cap: capacity - capacity / 2,
            buf: Mutex::default(),
        }
    }

    pub fn record(&self, event: Event) {
        self.record_at(Instant::now(), event);
    }

    /// The monotonic instant every `at_ns` counts from.
    #[must_use]
    pub const fn started(&self) -> Instant {
        self.started
    }

    /// The wall clock at [`started`](Self::started), in ns since the Unix epoch. A record's
    /// wall time is this plus its `at_ns`; durations come from the monotonic clock, so a
    /// wall-clock step after the start does not bend them.
    #[must_use]
    pub const fn started_unix_ns(&self) -> u64 {
        self.started_unix_ns
    }

    /// Events kept at most: the first half, then the newest.
    #[must_use]
    pub const fn capacity(&self) -> usize {
        self.head_cap + self.tail_cap
    }

    /// `at` in this trace's `at_ns`.
    #[must_use]
    pub fn at_ns(&self, at: Instant) -> u64 {
        u64::try_from(at.saturating_duration_since(self.started).as_nanos()).unwrap_or(u64::MAX)
    }

    /// Records `event` as having happened at `at`.
    pub fn record_at(&self, at: Instant, event: Event) {
        let record = Record {
            at_ns: self.at_ns(at),
            event,
        };
        let mut buf = self.lock();
        if buf.head.len() < self.head_cap {
            buf.head.push(record);
            return;
        }
        if buf.tail.len() == self.tail_cap {
            buf.tail.pop_front();
            buf.dropped += 1;
        }
        buf.tail.push_back(record);
    }

    /// Kept events in time order, and how many were dropped between head and tail.
    #[must_use]
    pub fn records(&self) -> (Vec<Record>, u64) {
        let buf = self.lock();
        let mut records: Vec<Record> = buf.head.clone();
        let dropped = buf.dropped;
        // The tail is in insertion order; the cut goes before its earliest stamp.
        if let Some(first) = buf
            .tail
            .iter()
            .map(|r| r.at_ns)
            .min()
            .filter(|_| dropped > 0)
        {
            records.push(Record {
                at_ns: first,
                event: Event::Dropped { events: dropped },
            });
        }
        records.extend(buf.tail.iter().copied());
        drop(buf);
        // Threads stamp before taking the lock, so neighbours can land out of order.
        records.sort_by_key(|r| r.at_ns);
        (records, dropped)
    }

    /// The summary up to now: a read still open counts its unfinished waits to now.
    #[must_use]
    pub fn summary(&self) -> Summary {
        let now = self.at_ns(Instant::now());
        let (records, dropped) = self.records();
        Summary::until(&records, dropped, Some(now))
    }

    fn lock(&self) -> MutexGuard<'_, Buf> {
        self.buf.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// `at` in ns since the Unix epoch; `0` before it.
#[must_use]
pub fn unix_ns(at: SystemTime) -> u64 {
    at.duration_since(UNIX_EPOCH)
        .map_or(0, |d| u64::try_from(d.as_nanos()).unwrap_or(u64::MAX))
}

/// A playback segment fetch's size, trace and live wait, passed down to the provider client.
#[derive(Clone)]
pub struct Probe {
    pub trace: Option<Arc<Trace>>,
    pub wait: Option<Arc<Wait>>,
    /// The reader's live state, which counts the articles each provider delivers.
    pub watch: Option<Arc<Watch>>,
    pub segment: u32,
    pub hedge: bool,
    /// Expected decoded bytes, `0` when unknown.
    pub size: u32,
}

impl Probe {
    pub fn step(&self, step: Step) {
        self.record(Event::Segment {
            segment: self.segment,
            hedge: self.hedge,
            step,
        });
        if let Some(wait) = &self.wait {
            wait.step(step);
        }
        if let (
            Some(watch),
            Step::Done {
                provider,
                outcome: Outcome::Retrieved,
                bytes,
                ..
            },
        ) = (&self.watch, step)
        {
            watch.supply(provider, bytes);
        }
    }

    /// Records `event` in the trace only.
    pub fn record(&self, event: Event) {
        if let Some(trace) = &self.trace {
            trace.record(event);
        }
    }
}

/// What the reader was waiting on, in nanoseconds of stall.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
pub struct Causes {
    /// Decoded-bytes budget held by other reads.
    pub budget: u64,
    /// Global download permits, all in use.
    pub permit: u64,
    /// A provider connection (pool full or connecting).
    pub connection: u64,
    /// Earlier pipelined replies on the same connection.
    pub pipeline: u64,
    /// The provider answering.
    pub latency: u64,
    /// The reply arriving.
    pub transfer: u64,
    /// yEnc decode and CRC.
    pub decode: u64,
    /// The disk cache.
    pub disk: u64,
    /// Another reader's fetch of the same article.
    pub shared: u64,
    pub other: u64,
}

#[derive(Clone, Copy)]
enum Cause {
    Budget,
    Permit,
    Connection,
    Pipeline,
    Latency,
    /// Transfer, of which this many parts per million were decode.
    Transfer(u32),
    Disk,
    Shared,
    Other,
}

impl Causes {
    /// Adds every fetch step's wait in `records` (in time order), stalled on or not: where
    /// requests, rather than a reader, spent their time.
    pub fn add_waits(&mut self, records: &[Record]) {
        let mut last: HashMap<(u32, bool), (u64, Cause)> = HashMap::new();
        for record in records {
            let Event::Segment {
                segment,
                hedge,
                step,
            } = record.event
            else {
                continue;
            };
            let Some((since, cause)) =
                last.insert((segment, hedge), (record.at_ns, waiting_after(step)))
            else {
                continue;
            };
            let ns = record.at_ns.saturating_sub(since);
            let cause = match (cause, step) {
                (Cause::Transfer(_), Step::Done { decode_ns, .. }) => {
                    let ppm = (u64::from(decode_ns) * 1_000_000 / ns.max(1)).min(1_000_000);
                    Cause::Transfer(u32::try_from(ppm).unwrap_or(1_000_000))
                }
                (cause, _) => cause,
            };
            self.add(cause, ns);
        }
    }

    fn add(&mut self, cause: Cause, ns: u64) {
        let slot = match cause {
            Cause::Budget => &mut self.budget,
            Cause::Permit => &mut self.permit,
            Cause::Connection => &mut self.connection,
            Cause::Pipeline => &mut self.pipeline,
            Cause::Latency => &mut self.latency,
            Cause::Transfer(decode_ppm) => {
                let decode = u64::try_from(u128::from(ns) * u128::from(decode_ppm) / 1_000_000)
                    .unwrap_or(ns);
                self.decode = self.decode.saturating_add(decode);
                self.transfer = self.transfer.saturating_add(ns - decode);
                return;
            }
            Cause::Disk => &mut self.disk,
            Cause::Shared => &mut self.shared,
            Cause::Other => &mut self.other,
        };
        *slot = slot.saturating_add(ns);
    }
}

/// Read requests in the daemon while a [`Summary`] walks the records.
#[derive(Default)]
struct Requests {
    /// Request id to its arrival, current stage (none when its stage events were
    /// dropped) and when that stage began.
    open: HashMap<u64, (u64, Option<ReadStage>, u64)>,
    /// When the last request left the daemon with none left in it; none when that is
    /// unknown (dropped events).
    emptied: Option<u64>,
    /// When requests were last counted into `in_request_ns`.
    busy_since: u64,
    first: Option<u64>,
    /// Requests seen without their arrival, counted once.
    unmatched: HashSet<u64>,
    /// Unmatched requests seen in a stage and not replied yet: still in the daemon.
    unmatched_open: HashSet<u64>,
    /// Requests open at a cut, to their arrival: their reply may be among the dropped
    /// events, so whether they are still in the daemon is unknown.
    uncertain: HashMap<u64, u64>,
}

impl Requests {
    fn arrive(&mut self, summary: &mut Summary, request: u64, at: u64) {
        summary.requests += 1;
        summary.first_request_ns.get_or_insert(at);
        self.first.get_or_insert(request);
        if self.open.is_empty() {
            if let Some(emptied) = self.emptied {
                Self::gap(summary, at.saturating_sub(emptied));
            }
            self.busy_since = at;
        }
        self.open.insert(request, (at, Some(ReadStage::Order), at));
    }

    fn stage(&mut self, summary: &mut Summary, request: u64, stage: ReadStage, at: u64) {
        if let Some(arrived) = self.uncertain.remove(&request) {
            // Seen again: it is in the daemon from here.
            if self.open.is_empty() {
                self.busy_since = at;
            }
            self.open.insert(request, (arrived, Some(stage), at));
            return;
        }
        let Some((_, current, since)) = self.open.get_mut(&request) else {
            self.unmatched(summary, request);
            // The daemon holds a request: no gap until it replies.
            self.unmatched_open.insert(request);
            self.emptied = None;
            return;
        };
        Self::spent(summary, *current, at.saturating_sub(*since));
        (*current, *since) = (Some(stage), at);
    }

    fn unmatched(&mut self, summary: &mut Summary, request: u64) {
        if self.unmatched.insert(request) {
            summary.requests_unmatched += 1;
        }
    }

    /// Events were dropped here: stages, gaps and request time across this point are
    /// unknown.
    fn cut(&mut self) {
        self.uncertain.extend(
            self.open
                .drain()
                .map(|(request, (arrived, _, _))| (request, arrived)),
        );
        self.emptied = None;
    }

    /// No request the daemon may still hold outside `open`.
    fn settled(&self) -> bool {
        self.unmatched_open.is_empty() && self.uncertain.is_empty()
    }

    fn took(&self, summary: &mut Summary, request: u64, ns: u64) {
        summary.longest_request_ns = summary.longest_request_ns.max(ns);
        if self.first == Some(request) {
            summary.first_reply_ns = Some(ns);
        }
    }

    fn reply(&mut self, summary: &mut Summary, request: u64, result: Reply, at: u64) {
        match result {
            Reply::Data => {}
            Reply::Error => summary.requests_failed += 1,
            Reply::Cancelled => summary.requests_cancelled += 1,
        }
        let Some((arrived, stage, since)) = self.open.remove(&request) else {
            if let Some(arrived) = self.uncertain.remove(&request) {
                self.took(summary, request, at.saturating_sub(arrived));
            } else {
                self.unmatched(summary, request);
                self.unmatched_open.remove(&request);
            }
            if self.open.is_empty() && self.settled() {
                self.emptied = Some(at);
            }
            return;
        };
        Self::spent(summary, stage, at.saturating_sub(since));
        self.took(summary, request, at.saturating_sub(arrived));
        if self.open.is_empty() {
            summary.in_request_ns = summary
                .in_request_ns
                .saturating_add(at.saturating_sub(self.busy_since));
            self.emptied = self.settled().then_some(at);
        }
    }

    /// The stream ended: a gap since the last reply runs to here.
    fn end(&mut self, summary: &mut Summary, at: u64) {
        if self.open.is_empty()
            && let Some(emptied) = self.emptied.take()
        {
            Self::gap(summary, at.saturating_sub(emptied));
        }
    }

    /// A snapshot at `at` of a stream still open: unfinished waits run to it.
    fn snapshot(&self, summary: &mut Summary, at: u64) {
        if self.open.is_empty() {
            if let Some(emptied) = self.emptied {
                Self::gap(summary, at.saturating_sub(emptied));
            }
            return;
        }
        for &(_, stage, since) in self.open.values() {
            Self::spent(summary, stage, at.saturating_sub(since));
        }
        summary.in_request_ns = summary
            .in_request_ns
            .saturating_add(at.saturating_sub(self.busy_since));
    }

    fn gap(summary: &mut Summary, ns: u64) {
        summary.no_request_ns = summary.no_request_ns.saturating_add(ns);
        summary.longest_no_request_ns = summary.longest_no_request_ns.max(ns);
    }

    fn spent(summary: &mut Summary, stage: Option<ReadStage>, ns: u64) {
        let (total, longest) = match stage {
            Some(ReadStage::Order) => {
                (&mut summary.request_order_ns, &mut summary.longest_order_ns)
            }
            Some(ReadStage::Lock) => (&mut summary.request_lock_ns, &mut summary.longest_lock_ns),
            Some(ReadStage::Read) => (&mut summary.request_read_ns, &mut summary.longest_read_ns),
            None => return,
        };
        *total = total.saturating_add(ns);
        *longest = (*longest).max(ns);
    }
}

/// The wait that starts with `step`.
const fn waiting_after(step: Step) -> Cause {
    match step {
        Step::Queued => Cause::Budget,
        // A failed attempt is followed by the next provider's permit wait.
        Step::Budgeted | Step::Bypassed | Step::Done { .. } => Cause::Permit,
        Step::Permitted { .. } => Cause::Connection,
        Step::Connected { .. } => Cause::Pipeline,
        Step::Reading { .. } => Cause::Latency,
        Step::Receiving { .. } => Cause::Transfer(0),
        Step::DiskRead => Cause::Disk,
        Step::Joined { .. } => Cause::Shared,
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize)]
pub struct Summary {
    pub events: usize,
    /// Events lost between the kept head and tail. When not zero, request counts and
    /// gap totals cover only the kept events.
    pub dropped: u64,
    /// Stream fetch latency: from the reader's first prefetch window to the first bytes
    /// it got from it. Not the daemon's request latency ([`first_reply_ns`](Self::first_reply_ns))
    /// and not click-to-play time.
    pub first_byte_ns: Option<u64>,
    pub stalls: u32,
    pub stall_ns: u64,
    pub longest_stall_ns: u64,
    pub causes: Causes,
    pub served: u32,
    /// Provider requests, including failovers and hedges.
    pub attempts: u32,
    /// Attempts that ended without the article (cancellations excluded).
    pub failed: u32,
    pub hedges: u32,
    pub hedges_won: u32,
    /// Attempts sent behind other requests on their connection.
    pub queued_behind: u32,
    /// Bytes and connection time spent reading replies nobody waited for anymore.
    pub drained_bytes: u64,
    pub drained_ns: u64,
    /// Stall time in stalls that began with idle connections: the read-ahead asked too little.
    pub stall_idle_ns: u64,
    /// Segments from the cache or another reader's fetch.
    pub cached: u32,
    pub seeks: u32,
    /// Time between stream reads, summed from [`Event::Idle`]. Includes the daemon's
    /// ordering and handle-lock waits; not proof the client paused.
    pub client_ns: u64,
    /// Open work before the trace started (path, caller and metadata lookups).
    pub open_before_ns: Option<u64>,
    /// From the trace start to the open's reply.
    pub open_reply_ns: Option<u64>,
    /// FUSE read requests that reached the daemon. Reads over HTTP are not timed per request.
    pub requests: u32,
    pub requests_failed: u32,
    pub requests_cancelled: u32,
    /// Requests with no reply in the kept events: still in the daemon, or (with dropped
    /// events) a reply that was not kept.
    pub requests_pending: u32,
    /// Replies or stages whose request arrival was not kept.
    pub requests_unmatched: u32,
    /// From the trace start to the first read request reaching the daemon.
    pub first_request_ns: Option<u64>,
    /// From the first read request reaching the daemon to the daemon's reply to it.
    pub first_reply_ns: Option<u64>,
    /// Request time per stage, summed over requests (concurrent requests overlap).
    pub request_order_ns: u64,
    pub request_lock_ns: u64,
    pub request_read_ns: u64,
    pub longest_order_ns: u64,
    pub longest_lock_ns: u64,
    pub longest_read_ns: u64,
    /// Longest arrival-to-reply of one request.
    pub longest_request_ns: u64,
    /// From the first request on, time with at least one read request in the daemon.
    pub in_request_ns: u64,
    /// From the first request to the last reply (or the end), time with no read request
    /// in the daemon. The kernel, its page cache or the player had the time; the trace
    /// cannot tell which.
    pub no_request_ns: u64,
    pub longest_no_request_ns: u64,
}

impl Summary {
    /// `records` in time order, up to the last of them.
    #[must_use]
    pub fn of(records: &[Record], dropped: u64) -> Self {
        Self::until(records, dropped, None)
    }

    /// `records` in time order; a stream without an end counts its unfinished request
    /// waits and its gap without requests to `now_ns`.
    #[must_use]
    pub fn until(records: &[Record], dropped: u64, now_ns: Option<u64>) -> Self {
        let mut summary = Self {
            events: records
                .iter()
                .filter(|r| !matches!(r.event, Event::Dropped { .. }))
                .count(),
            dropped,
            ..Self::default()
        };
        // Keyed by segment and hedge: a hedge's steps interleave with the first fetch's.
        let mut steps: HashMap<(u32, bool), Vec<(u64, Cause)>> = HashMap::new();
        let mut stalls = Vec::new();
        // When each sample was taken, and whether it saw idle connections.
        let mut samples: Vec<(u64, bool)> = Vec::new();
        let mut asked = None;
        let mut requests = Requests::default();
        let mut ended = false;
        for record in records {
            match record.event {
                Event::Opened { before_ns, .. } => {
                    summary.open_before_ns = Some(before_ns);
                    summary.open_reply_ns = Some(record.at_ns);
                }
                Event::Request { request, .. } => {
                    requests.arrive(&mut summary, request, record.at_ns);
                }
                Event::RequestStage { request, stage } => {
                    requests.stage(&mut summary, request, stage, record.at_ns);
                }
                Event::Replied {
                    request, result, ..
                } => requests.reply(&mut summary, request, result, record.at_ns),
                Event::End { .. } => {
                    ended = true;
                    requests.end(&mut summary, record.at_ns);
                }
                Event::Dropped { .. } => requests.cut(),
                Event::Segment {
                    segment,
                    hedge,
                    step,
                } => {
                    let timeline = steps.entry((segment, hedge)).or_default();
                    if let Step::Connected { ahead, .. } = step
                        && ahead > 0
                    {
                        summary.queued_behind += 1;
                    }
                    if let Step::Done {
                        outcome,
                        decode_ns,
                        bytes,
                        ..
                    } = step
                    {
                        summary.attempts += 1;
                        if !matches!(outcome, Outcome::Retrieved | Outcome::Cancelled) {
                            summary.failed += 1;
                        }
                        if outcome == Outcome::Cancelled && bytes > 0 {
                            summary.drained_bytes += u64::from(bytes);
                            if let Some((read_at, _)) =
                                timeline.iter().rfind(|(_, c)| matches!(c, Cause::Latency))
                            {
                                summary.drained_ns += record.at_ns.saturating_sub(*read_at);
                            }
                        }
                        // The transfer wait it ends learns its decode share.
                        if let Some((since, cause @ Cause::Transfer(_))) = timeline.last_mut() {
                            let total = record.at_ns.saturating_sub(*since).max(1);
                            let ppm = (u64::from(decode_ns) * 1_000_000 / total).min(1_000_000);
                            *cause = Cause::Transfer(u32::try_from(ppm).unwrap_or(1_000_000));
                        }
                    }
                    timeline.push((record.at_ns, waiting_after(step)));
                }
                Event::Served { .. } => {
                    summary.served += 1;
                    let asked = asked.unwrap_or(0);
                    summary
                        .first_byte_ns
                        .get_or_insert_with(|| record.at_ns.saturating_sub(asked));
                }
                Event::Stall { segment, ns } => {
                    // A wait ends with bytes, possibly a streamed prefix before `Served`.
                    let asked = asked.unwrap_or(0);
                    summary
                        .first_byte_ns
                        .get_or_insert_with(|| record.at_ns.saturating_sub(asked));
                    summary.stalls += 1;
                    summary.stall_ns = summary.stall_ns.saturating_add(ns);
                    summary.longest_stall_ns = summary.longest_stall_ns.max(ns);
                    stalls.push((segment, record.at_ns.saturating_sub(ns), record.at_ns));
                }
                Event::Sample {
                    idle_connections, ..
                } => samples.push((record.at_ns, idle_connections > 0)),
                Event::Hedge { .. } => summary.hedges += 1,
                Event::HedgeWon { .. } => summary.hedges_won += 1,
                Event::Cached { .. } => summary.cached += 1,
                Event::Seek { .. } => summary.seeks += 1,
                Event::Idle { ns } => summary.client_ns = summary.client_ns.saturating_add(ns),
                Event::Window { .. } => {
                    asked.get_or_insert(record.at_ns);
                }
                Event::Open { .. } => {}
            }
        }
        if let Some(now) = now_ns.filter(|_| !ended) {
            requests.snapshot(&mut summary, now);
        }
        summary.requests_pending = u32::try_from(
            requests.open.len() + requests.unmatched_open.len() + requests.uncertain.len(),
        )
        .unwrap_or(u32::MAX);
        for (segment, start, end) in stalls {
            // The reader samples as a stall begins; later samples see it half over.
            let began = samples.partition_point(|&(at, _)| at <= start);
            if began > 0 && samples[began - 1].1 {
                summary.stall_idle_ns = summary.stall_idle_ns.saturating_add(end - start);
            }
            // Charged to the first fetch; a winning hedge only cuts the wait short.
            let timeline = steps.get(&(segment, false)).map_or(&[][..], Vec::as_slice);
            // Each wait runs from its step to the next; before the first, nothing is known.
            let mut cause = Cause::Other;
            let mut at = start;
            for &(step_at, next) in timeline {
                if step_at > at {
                    summary.causes.add(cause, step_at.min(end) - at);
                    at = step_at.min(end);
                }
                cause = next;
                if at == end {
                    break;
                }
            }
            summary.causes.add(cause, end - at);
        }
        summary
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;
    use crate::wait::Supplied;

    fn at(at_ns: u64, event: Event) -> Record {
        Record { at_ns, event }
    }

    fn step(segment: u32, step: Step) -> Event {
        Event::Segment {
            segment,
            hedge: false,
            step,
        }
    }

    #[test]
    fn the_watch_counts_articles_each_provider_delivered() {
        let watch = Arc::new(Watch::default());
        let probe = Probe {
            trace: None,
            wait: None,
            watch: Some(Arc::clone(&watch)),
            segment: 0,
            hedge: false,
            size: 0,
        };
        let done = |provider, outcome| Step::Done {
            provider,
            outcome,
            bytes: 100,
            decode_ns: 0,
        };
        probe.step(done(2, Outcome::Retrieved));
        probe.step(done(0, Outcome::NotFound));
        probe.step(done(0, Outcome::Retrieved));
        probe.step(done(2, Outcome::Retrieved));
        probe.step(done(1, Outcome::Cancelled));
        probe.step(done(1, Outcome::NotRetrieved(Reason::Timeout)));
        let supplied = |provider, articles| Supplied {
            provider,
            articles,
            bytes: articles * 100,
        };
        assert_eq!(watch.supplied(), [supplied(2, 2), supplied(0, 1)]);
    }

    #[test]
    fn stalls_are_charged_to_the_step_being_waited_on() {
        let records = [
            at(0, step(0, Step::Queued)),
            at(10, step(0, Step::Budgeted)),
            at(20, step(0, Step::Permitted { provider: 0 })),
            at(
                50,
                step(
                    0,
                    Step::Connected {
                        provider: 0,
                        ahead: 0,
                        connection: 1,
                    },
                ),
            ),
            at(60, step(0, Step::Reading { provider: 0 })),
            at(80, step(0, Step::Receiving { provider: 0 })),
            at(
                100,
                step(
                    0,
                    Step::Done {
                        provider: 0,
                        outcome: Outcome::Retrieved,
                        bytes: 1,
                        decode_ns: 5,
                    },
                ),
            ),
            at(100, Event::Served { segment: 0 }),
            at(100, Event::Stall { segment: 0, ns: 95 }),
        ];
        let summary = Summary::of(&records, 0);
        assert_eq!(
            summary.causes,
            Causes {
                budget: 5,
                permit: 10,
                connection: 30,
                pipeline: 10,
                latency: 20,
                transfer: 15,
                decode: 5,
                disk: 0,
                shared: 0,
                other: 0,
            }
        );
        assert_eq!(summary.stall_ns, 95);
        assert_eq!(summary.first_byte_ns, Some(100));
        assert_eq!((summary.attempts, summary.failed), (1, 0));
        // Unlike the stall, the request's waits include the budget wait before it.
        let mut waited = Causes::default();
        waited.add_waits(&records);
        assert_eq!(
            waited,
            Causes {
                budget: 10,
                ..summary.causes
            }
        );
    }

    #[test]
    fn cache_waits_are_charged_to_disk_and_shared_fetches() {
        let records = [
            at(0, step(0, Step::Queued)),
            at(10, step(0, Step::Joined { flight: 7 })),
            at(30, Event::Stall { segment: 0, ns: 30 }),
            at(30, step(1, Step::Bypassed)),
            at(35, step(1, Step::DiskRead)),
            at(50, Event::Stall { segment: 1, ns: 20 }),
        ];
        let causes = Summary::of(&records, 0).causes;
        assert_eq!(
            (causes.budget, causes.shared, causes.permit, causes.disk),
            (10, 20, 5, 15)
        );
    }

    #[test]
    fn counts_drained_replies_queued_attempts_and_idle_stalls() {
        let done = |outcome, bytes| Step::Done {
            provider: 0,
            outcome,
            bytes,
            decode_ns: 0,
        };
        let sample = |idle_connections| Event::Sample {
            in_flight: 0,
            ready: 0,
            budget_free: 0,
            permits_free: 0,
            window: 0,
            connections: 1,
            idle_connections,
        };
        let hedge = |step| Event::Segment {
            segment: 0,
            hedge: true,
            step,
        };
        let records = [
            at(
                0,
                step(
                    0,
                    Step::Connected {
                        provider: 0,
                        ahead: 2,
                        connection: 1,
                    },
                ),
            ),
            at(10, step(0, Step::Reading { provider: 0 })),
            at(20, hedge(Step::Reading { provider: 0 })),
            // The first fetch lost to its hedge; its drain began at its own reading.
            at(40, step(0, done(Outcome::Cancelled, 100))),
            at(50, sample(1)),
            // Began at 55, after an idle sample; the busy one at 70 came mid-stall.
            at(70, sample(0)),
            at(80, Event::Stall { segment: 1, ns: 25 }),
            at(100, Event::Stall { segment: 1, ns: 10 }),
            at(110, Event::HedgeWon { segment: 1 }),
        ];
        let summary = Summary::of(&records, 0);
        assert_eq!(summary.queued_behind, 1);
        assert_eq!((summary.drained_bytes, summary.drained_ns), (100, 30));
        assert_eq!((summary.stall_ns, summary.stall_idle_ns), (35, 25));
        assert_eq!(summary.hedges_won, 1);
    }

    #[test]
    fn keeps_the_start_and_the_newest_events() {
        let trace = Trace::new(4);
        for bytes in 0..10 {
            trace.record(Event::End { bytes });
        }
        let (records, dropped) = trace.records();
        let kept: Vec<_> = records
            .iter()
            .map(|r| match r.event {
                Event::End { bytes } => bytes,
                Event::Dropped { events } => 100 + events,
                _ => u64::MAX,
            })
            .collect();
        assert_eq!(kept, [0, 1, 106, 8, 9], "the cut is marked");
        assert_eq!(dropped, 6);
    }

    #[test]
    fn request_time_splits_into_order_lock_and_read_and_gaps_without_requests() {
        let arrive = |request| Event::Request {
            request,
            offset: 0,
            size: 4096,
        };
        let stage = |request, stage| Event::RequestStage { request, stage };
        let reply = |request, result| Event::Replied {
            request,
            bytes: 0,
            result,
        };
        let records = [
            at(0, Event::Open { size: 8 }),
            at(
                30,
                Event::Opened {
                    request: 1,
                    handle: 3,
                    inode: 9,
                    before_ns: 20,
                },
            ),
            at(100, arrive(2)),
            at(110, stage(2, ReadStage::Lock)),
            at(130, stage(2, ReadStage::Read)),
            // Overlaps request 2: both in the daemon at once count once.
            at(150, arrive(3)),
            at(200, reply(2, Reply::Data)),
            at(250, reply(3, Reply::Cancelled)),
            // No request in the daemon from 250 to 1250.
            at(1250, arrive(4)),
            at(1260, stage(4, ReadStage::Lock)),
            at(1270, stage(4, ReadStage::Read)),
            at(1300, reply(4, Reply::Error)),
            // A reply whose arrival was dropped, and a request still in the daemon.
            at(1400, reply(5, Reply::Data)),
            at(1500, arrive(6)),
        ];
        let s = Summary::of(&records, 0);
        assert_eq!((s.open_before_ns, s.open_reply_ns), (Some(20), Some(30)));
        assert_eq!(s.requests, 4);
        assert_eq!((s.requests_failed, s.requests_cancelled), (1, 1));
        assert_eq!((s.requests_pending, s.requests_unmatched), (1, 1));
        assert_eq!(
            (s.first_request_ns, s.first_reply_ns),
            (Some(100), Some(100))
        );
        // Request 3 never left the order stage: 100 ns.
        assert_eq!(s.request_order_ns, 10 + 100 + 10);
        assert_eq!(s.request_lock_ns, 20 + 10);
        assert_eq!(s.request_read_ns, 70 + 30);
        assert_eq!(s.longest_order_ns, 100);
        assert_eq!(s.longest_request_ns, 100);
        assert_eq!(s.in_request_ns, 150 + 50);
        // Request 5 was in the daemon before 1400 since an unknown time: only the gap
        // from its reply to the arrival at 1500 counts.
        assert_eq!(s.no_request_ns, 1000 + 100);
        assert_eq!(s.longest_no_request_ns, 1000);
    }

    fn requests_until(records: &[Record], dropped: u64, now: u64) -> Summary {
        Summary::until(records, dropped, Some(now))
    }

    #[test]
    fn a_live_summary_counts_unfinished_waits_to_now() {
        let arrive = |request| Event::Request {
            request,
            offset: 0,
            size: 4096,
        };
        let waiting = [
            at(0, Event::Open { size: 8 }),
            at(100, arrive(1)),
            at(110, stage_of(1, ReadStage::Lock)),
            at(120, stage_of(1, ReadStage::Read)),
        ];
        let s = requests_until(&waiting, 0, 1120);
        assert_eq!(
            s.request_read_ns, 1000,
            "the read still waiting on the backend"
        );
        assert_eq!(s.longest_read_ns, 1000);
        assert_eq!(s.in_request_ns, 1020);
        assert_eq!(s.requests_pending, 1);
        assert_eq!(
            Summary::of(&waiting, 0).request_read_ns,
            0,
            "no snapshot time"
        );

        let quiet = [
            at(0, Event::Open { size: 8 }),
            at(100, arrive(1)),
            at(
                120,
                Event::Replied {
                    request: 1,
                    bytes: 0,
                    result: Reply::Data,
                },
            ),
        ];
        let s = requests_until(&quiet, 0, 1120);
        assert_eq!(s.no_request_ns, 1000, "no request since the last reply");

        let mut ended = quiet.to_vec();
        ended.push(at(150, Event::End { bytes: 0 }));
        assert_eq!(requests_until(&ended, 0, 1120).no_request_ns, 30);
    }

    #[test]
    fn dropped_events_are_not_counted_as_time_without_requests() {
        let arrive = |request| Event::Request {
            request,
            offset: 0,
            size: 4096,
        };
        let reply = |request| Event::Replied {
            request,
            bytes: 0,
            result: Reply::Data,
        };
        // Request 2 arrived somewhere in the dropped part.
        let records = [
            at(0, Event::Open { size: 8 }),
            at(10, arrive(1)),
            at(20, reply(1)),
            at(90, Event::Dropped { events: 1 }),
            at(90, stage_of(2, ReadStage::Read)),
            at(95, stage_of(2, ReadStage::Read)),
            at(100, reply(2)),
            at(110, arrive(3)),
            at(120, reply(3)),
        ];
        let s = Summary::of(&records, 1);
        assert_eq!(s.no_request_ns, 10, "only 100 to 110 is known");
        assert_eq!(s.requests_unmatched, 1, "counted once");
        assert_eq!(s.events, records.len() - 1, "the marker is no event");

        // A request open across the cut keeps its total but loses its stage split.
        let records = [
            at(10, arrive(1)),
            at(20, stage_of(1, ReadStage::Read)),
            at(50, Event::Dropped { events: 3 }),
            at(80, reply(1)),
        ];
        let s = Summary::of(&records, 3);
        assert_eq!(
            s.request_read_ns, 0,
            "its stage events may be the dropped ones"
        );
        assert_eq!(s.longest_request_ns, 70);

        // Its reply may be dropped: no request time runs on from before the cut.
        let records = [at(10, arrive(1)), at(90, Event::Dropped { events: 1 })];
        let s = requests_until(&records, 1, 1000);
        assert_eq!(s.in_request_ns, 0);
        assert_eq!(s.requests_pending, 1);

        // Request 1 is still in the daemon when request 2 replies: no gap after it.
        let records = [
            at(50, Event::Dropped { events: 2 }),
            at(50, stage_of(1, ReadStage::Read)),
            at(60, arrive(2)),
            at(70, reply(2)),
        ];
        let s = requests_until(&records, 2, 1070);
        assert_eq!(s.no_request_ns, 0);
        assert_eq!(s.requests_pending, 1);
        let mut replied = records.to_vec();
        replied.push(at(80, reply(1)));
        assert_eq!(requests_until(&replied, 2, 1080).no_request_ns, 1000);
    }

    #[test]
    fn the_cut_goes_before_the_earliest_kept_tail_event() {
        let trace = Trace::new(4);
        let start = trace.started();
        let request = |request| Event::Request {
            request,
            offset: 0,
            size: 4096,
        };
        trace.record_at(start + Duration::from_nanos(10), request(1));
        trace.record_at(start + Duration::from_nanos(15), request(2));
        trace.record_at(
            start + Duration::from_nanos(20),
            stage_of(1, ReadStage::Lock),
        );
        trace.record_at(
            start + Duration::from_nanos(100),
            Event::Window { segment: 0 },
        );
        // Stamped before the sample, recorded after it.
        trace.record_at(
            start + Duration::from_nanos(90),
            stage_of(1, ReadStage::Read),
        );
        let (records, dropped) = trace.records();
        assert_eq!(dropped, 1, "the lock stage");
        let cut = records
            .iter()
            .position(|r| matches!(r.event, Event::Dropped { .. }))
            .unwrap();
        assert_eq!(records[cut].at_ns, 90);
        assert!(records[cut + 1..].iter().all(|r| r.at_ns >= 90));
        let s = Summary::of(&records, dropped);
        assert_eq!(
            s.request_order_ns, 0,
            "order time across the cut is unknown"
        );
    }

    fn stage_of(request: u64, stage: ReadStage) -> Event {
        Event::RequestStage { request, stage }
    }

    #[test]
    fn a_stream_without_requests_has_no_request_timing() {
        let records = [
            at(0, Event::Open { size: 8 }),
            at(9, Event::End { bytes: 0 }),
        ];
        let s = Summary::of(&records, 0);
        assert_eq!(s.requests, 0);
        assert_eq!((s.first_request_ns, s.first_reply_ns), (None, None));
        assert_eq!((s.in_request_ns, s.no_request_ns), (0, 0));
    }

    #[test]
    fn the_wall_clock_anchor_is_taken_at_the_start() {
        let before = unix_ns(SystemTime::now());
        let trace = Trace::new(4);
        let after = unix_ns(SystemTime::now());
        assert!((before..=after).contains(&trace.started_unix_ns()));
        assert_eq!(trace.capacity(), 4);
        assert_eq!(trace.at_ns(trace.started()), 0);
    }
}
