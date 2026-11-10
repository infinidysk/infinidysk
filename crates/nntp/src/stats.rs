//! Per-provider request counters, cheap enough to keep on for every request.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use infinidysk_core::trace::{Outcome, Reason};

use crate::{ConnectionStats, Error};

/// Who asked, so playback is not drowned out by imports and health checks.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Workload {
    Stream,
    Queue,
    Health,
}

impl Workload {
    pub const ALL: [Self; 3] = [Self::Stream, Self::Queue, Self::Health];

    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Stream => "stream",
            Self::Queue => "queue",
            Self::Health => "health",
        }
    }
}

/// Latency buckets: bucket `i` counts requests under `2^i` microseconds; the last also
/// takes everything slower (about 8.4 s and up).
pub const LATENCY_BUCKETS: usize = 24;

const OUTCOMES: usize = Outcome::ALL.len();
const WORKLOADS: usize = Workload::ALL.len();
/// One unexpected-reply warning per provider and reply code this often.
const LOG_UNEXPECTED_EVERY: Duration = Duration::from_hours(1);

#[derive(Default)]
pub(crate) struct Stats {
    attempts: [[AtomicU64; OUTCOMES]; WORKLOADS],
    failed: [AtomicU64; WORKLOADS],
    bytes: [AtomicU64; WORKLOADS],
    latency: [[AtomicU64; LATENCY_BUCKETS]; WORKLOADS],
    connect_failures: AtomicU64,
    trips: AtomicU64,
    /// Last warning and warnings held back since, by reply code (`None`: malformed).
    unexpected: Mutex<HashMap<Option<u16>, (Instant, u64)>>,
}

impl Stats {
    pub(crate) fn record(&self, workload: Workload, outcome: Outcome, bytes: u32, took: Duration) {
        let w = workload as usize;
        self.attempts[w][outcome.index()].fetch_add(1, Ordering::Relaxed);
        if bytes > 0 {
            self.bytes[w].fetch_add(bytes.into(), Ordering::Relaxed);
        }
        // A cancelled request's time says nothing about the provider.
        if outcome != Outcome::Cancelled {
            let micros = u64::try_from(took.as_micros()).unwrap_or(u64::MAX);
            let bucket = micros.bit_width() as usize;
            self.latency[w][bucket.min(LATENCY_BUCKETS - 1)].fetch_add(1, Ordering::Relaxed);
        }
    }

    pub(crate) fn failed(&self, workload: Workload) {
        self.failed[workload as usize].fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn connect_failed(&self) {
        self.connect_failures.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn tripped(&self) {
        self.trips.fetch_add(1, Ordering::Relaxed);
    }

    /// `Some(held back)` when an unexpected reply with this code should be logged now.
    pub(crate) fn unexpected(&self, code: Option<u16>, now: Instant) -> Option<u64> {
        let mut logged = self
            .unexpected
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let log = match logged.get_mut(&code) {
            None => {
                logged.insert(code, (now, 0));
                Some(0)
            }
            Some((at, held)) if now.duration_since(*at) < LOG_UNEXPECTED_EVERY => {
                *held += 1;
                None
            }
            Some((at, held)) => {
                *at = now;
                Some(std::mem::take(held))
            }
        };
        drop(logged);
        log
    }

    /// Connect failures, circuit trips and per-workload counters.
    pub(crate) fn snapshot(&self) -> (u64, u64, [WorkloadStats; WORKLOADS]) {
        let load = |a: &AtomicU64| a.load(Ordering::Relaxed);
        let workloads = std::array::from_fn(|w| WorkloadStats {
            attempts: self.attempts[w].each_ref().map(load),
            failed: load(&self.failed[w]),
            bytes: load(&self.bytes[w]),
            latency: self.latency[w].each_ref().map(load),
        });
        (load(&self.connect_failures), load(&self.trips), workloads)
    }
}

/// Totals since start for one workload on one provider.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct WorkloadStats {
    /// Indexed like [`Outcome::ALL`].
    pub attempts: [u64; OUTCOMES],
    /// Requests that failed for the caller after every retry and fallback, ending on
    /// this provider's error. Missing articles are not counted.
    pub failed: u64,
    /// Decoded bytes received, including replies whose caller had stopped waiting.
    pub bytes: u64,
    /// See [`LATENCY_BUCKETS`]; cancelled requests are left out.
    pub latency: [u64; LATENCY_BUCKETS],
}

#[derive(Clone, Debug)]
pub struct ProviderStats {
    /// Stable for the process lifetime; matches trace `provider` fields.
    pub id: u16,
    /// Stable across restarts and settings edits.
    pub key: String,
    pub name: String,
    pub priority: u32,
    /// Open connections.
    pub live: usize,
    /// Requests in flight or queued on a connection.
    pub busy: usize,
    /// Open connections with at least one request on them.
    pub active: usize,
    pub max_connections: usize,
    pub pipeline_depth: usize,
    /// Every open connection.
    pub connections: Vec<ConnectionStats>,
    /// Connects in progress.
    pub connecting: usize,
    /// Connects that failed, background ones included.
    pub connect_failures: u64,
    /// Times the circuit breaker opened.
    pub trips: u64,
    /// Cooldown left while the circuit is open.
    pub open_for: Option<Duration>,
    /// A connection opened since the provider was (re)configured.
    pub connected: bool,
    /// Why the last connect failed with no connection open; cleared when one opens.
    pub connect_error: Option<Arc<str>>,
    /// Indexed like [`Workload::ALL`].
    pub workloads: [WorkloadStats; WORKLOADS],
}

/// Segments one provider missed or failed and a later one served, since start.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rescue {
    /// Provider ids, as in [`ProviderStats::id`].
    pub from: u16,
    pub to: u16,
    /// How `from`'s attempt ended.
    pub reason: Outcome,
    pub count: u64,
}

/// How a request that got this error ended.
pub(crate) const fn outcome(error: &Error) -> Outcome {
    let reason = match error {
        Error::NotFound => return Outcome::NotFound,
        Error::Timeout => Reason::Timeout,
        Error::AuthRejected { .. } => Reason::Auth,
        Error::Io(_) | Error::Closed | Error::Tls(_) => Reason::Network,
        Error::Protocol | Error::Status { .. } => Reason::Protocol,
        Error::Yenc(_) | Error::CrcMismatch { .. } | Error::TooLarge => Reason::Corrupt,
        Error::Unavailable | Error::InvalidArgument(_) => Reason::Other,
    };
    Outcome::NotRetrieved(reason)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unexpected_replies_are_logged_once_an_hour_per_code() {
        let stats = Stats::default();
        let now = Instant::now();
        assert_eq!(stats.unexpected(Some(400), now), Some(0));
        assert_eq!(stats.unexpected(Some(400), now), None);
        assert_eq!(stats.unexpected(Some(400), now), None);
        assert_eq!(stats.unexpected(None, now), Some(0));
        let later = now + LOG_UNEXPECTED_EVERY;
        assert_eq!(stats.unexpected(Some(400), later), Some(2));
        assert_eq!(stats.unexpected(Some(400), later), None);
    }
}
