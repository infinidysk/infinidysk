//! Opt-in event capture for one read stream, to explain why a playback stalled.
//!
//! A stream without a [`Trace`] records nothing. With one, each event is a short lock
//! and a copy of a fixed-size value; analysis ([`Trace::summary`]) runs only on demand.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Instant;

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

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Event {
    Open {
        size: u64,
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
    /// The reader paused `ns` between reads, ending now: time the client, not we, spent.
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
            head_cap: capacity / 2,
            tail_cap: capacity - capacity / 2,
            buf: Mutex::default(),
        }
    }

    pub fn record(&self, event: Event) {
        self.record_at(Instant::now(), event);
    }

    /// Records `event` as having happened at `at`.
    pub fn record_at(&self, at: Instant, event: Event) {
        let at_ns = u64::try_from(at.saturating_duration_since(self.started).as_nanos())
            .unwrap_or(u64::MAX);
        let record = Record { at_ns, event };
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
        let mut records: Vec<Record> = buf.head.iter().chain(&buf.tail).copied().collect();
        let dropped = buf.dropped;
        drop(buf);
        // Threads stamp before taking the lock, so neighbours can land out of order.
        records.sort_by_key(|r| r.at_ns);
        (records, dropped)
    }

    #[must_use]
    pub fn summary(&self) -> Summary {
        let (records, dropped) = self.records();
        Summary::of(&records, dropped)
    }

    fn lock(&self) -> MutexGuard<'_, Buf> {
        self.buf.lock().unwrap_or_else(PoisonError::into_inner)
    }
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
    pub dropped: u64,
    /// From the first read (its window) to its first byte; the client's pause after
    /// open is not ours.
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
    /// Time the client paused between reads.
    pub client_ns: u64,
}

impl Summary {
    /// `records` in time order.
    #[must_use]
    pub fn of(records: &[Record], dropped: u64) -> Self {
        let mut summary = Self {
            events: records.len(),
            dropped,
            ..Self::default()
        };
        // Keyed by segment and hedge: a hedge's steps interleave with the first fetch's.
        let mut steps: HashMap<(u32, bool), Vec<(u64, Cause)>> = HashMap::new();
        let mut stalls = Vec::new();
        // When each sample was taken, and whether it saw idle connections.
        let mut samples: Vec<(u64, bool)> = Vec::new();
        let mut asked = None;
        for record in records {
            match record.event {
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
                Event::Open { .. } | Event::End { .. } => {}
            }
        }
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
                _ => u64::MAX,
            })
            .collect();
        assert_eq!(kept, [0, 1, 8, 9]);
        assert_eq!(dropped, 6);
    }
}
