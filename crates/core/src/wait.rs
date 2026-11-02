//! Always-on state of reads in progress: what each one waits on right now, so a stall
//! can be explained while it lasts. Fed by the same steps as traces; atomics only.

use std::sync::atomic::{AtomicU8, AtomicU16, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::time::{Duration, Instant};

use serde::Serialize;

use crate::trace::{Outcome, Step};

/// Monotonic nanoseconds since the first call; never 0, which stands for "never".
#[must_use]
pub fn now_ns() -> u64 {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    let epoch = *EPOCH.get_or_init(Instant::now);
    u64::try_from(epoch.elapsed().as_nanos())
        .unwrap_or(u64::MAX)
        .max(1)
}

/// Time since a [`now_ns`] stamp; `None` for 0.
#[must_use]
pub fn age(stamp: u64) -> Option<Duration> {
    (stamp != 0).then(|| Duration::from_nanos(now_ns().saturating_sub(stamp)))
}

/// What a segment fetch waits on.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Stage {
    /// Decoded-bytes budget held by other reads.
    Budget,
    /// A global download permit.
    Permit,
    /// A provider connection.
    Connection,
    /// Earlier pipelined replies on its connection.
    Pipeline,
    /// The provider to answer.
    Reply,
    /// The body to arrive and decode.
    Transfer,
    /// The disk cache.
    Disk,
    /// Another reader's fetch of the same article.
    Shared,
    /// Nothing: its bytes are in.
    Done,
}

impl Stage {
    const ALL: [Self; 9] = [
        Self::Budget,
        Self::Permit,
        Self::Connection,
        Self::Pipeline,
        Self::Reply,
        Self::Transfer,
        Self::Disk,
        Self::Shared,
        Self::Done,
    ];

    /// The wait that starts with `step`.
    #[must_use]
    pub const fn after(step: Step) -> Self {
        match step {
            Step::Queued => Self::Budget,
            Step::Done {
                outcome: Outcome::Retrieved | Outcome::Cancelled,
                ..
            } => Self::Done,
            // A failed attempt is followed by the next provider's permit wait.
            Step::Budgeted | Step::Bypassed | Step::Done { .. } => Self::Permit,
            Step::Permitted { .. } => Self::Connection,
            Step::Connected { .. } => Self::Pipeline,
            Step::Reading { .. } => Self::Reply,
            Step::Receiving { .. } => Self::Transfer,
            Step::DiskRead => Self::Disk,
            Step::Joined { .. } => Self::Shared,
        }
    }

    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Budget => "budget",
            Self::Permit => "permit",
            Self::Connection => "connection",
            Self::Pipeline => "pipeline",
            Self::Reply => "reply",
            Self::Transfer => "transfer",
            Self::Disk => "disk",
            Self::Shared => "shared",
            Self::Done => "done",
        }
    }
}

const NONE16: u16 = u16::MAX;
const NONE32: u32 = u32::MAX;

/// What one segment fetch waits on now. Hedges keep none, so they never hide it.
pub struct Wait {
    stage: AtomicU8,
    since: AtomicU64,
    provider: AtomicU16,
    connection: AtomicU32,
    /// The shared fetch this one started or joined; 0 for none.
    flight: AtomicU64,
    /// The fetch this one joined, whose stage is the real one.
    follow: OnceLock<Arc<Self>>,
}

impl Default for Wait {
    fn default() -> Self {
        Self {
            stage: AtomicU8::new(Stage::Budget as u8),
            since: AtomicU64::new(now_ns()),
            provider: AtomicU16::new(NONE16),
            connection: AtomicU32::new(NONE32),
            flight: AtomicU64::new(0),
            follow: OnceLock::new(),
        }
    }
}

impl Wait {
    pub fn step(&self, step: Step) {
        match step {
            Step::Connected {
                provider,
                connection,
                ..
            } => {
                self.provider.store(provider, Ordering::Relaxed);
                self.connection.store(connection, Ordering::Relaxed);
            }
            Step::Permitted { provider } => {
                self.provider.store(provider, Ordering::Relaxed);
                self.connection.store(NONE32, Ordering::Relaxed);
            }
            Step::Reading { provider }
            | Step::Receiving { provider }
            | Step::Done { provider, .. } => self.provider.store(provider, Ordering::Relaxed),
            Step::Joined { flight } => self.flight.store(flight, Ordering::Relaxed),
            Step::Queued | Step::Budgeted | Step::Bypassed | Step::DiskRead => {}
        }
        self.since.store(now_ns(), Ordering::Relaxed);
        self.stage
            .store(Stage::after(step) as u8, Ordering::Relaxed);
    }

    /// This fetch started the shared fetch `flight`.
    pub fn lead(&self, flight: u64) {
        self.flight.store(flight, Ordering::Relaxed);
    }

    /// This fetch waits on `leader`'s instead of its own.
    pub fn follow(&self, leader: Arc<Self>) {
        let _ = self.follow.set(leader);
    }

    #[must_use]
    pub fn view(&self) -> WaitView {
        let flight = self.flight.load(Ordering::Relaxed);
        let (from, joined) = match self.follow.get() {
            Some(leader) => (&**leader, true),
            None => (self, false),
        };
        let stage = Stage::ALL
            .get(usize::from(from.stage.load(Ordering::Relaxed)))
            .copied()
            .unwrap_or(Stage::Done);
        WaitView {
            stage,
            elapsed: age(from.since.load(Ordering::Relaxed)).unwrap_or_default(),
            provider: Some(from.provider.load(Ordering::Relaxed)).filter(|p| *p != NONE16),
            connection: Some(from.connection.load(Ordering::Relaxed)).filter(|c| *c != NONE32),
            flight: (flight != 0).then_some(flight),
            joined,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct WaitView {
    pub stage: Stage,
    /// How long it has been in `stage`.
    pub elapsed: Duration,
    pub provider: Option<u16>,
    pub connection: Option<u32>,
    pub flight: Option<u64>,
    /// Waits on another reader's fetch; the rest describes that fetch.
    pub joined: bool,
}

/// Articles one provider delivered to a reader.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Supplied {
    pub provider: u16,
    pub articles: u64,
    pub bytes: u64,
}

/// Live state of one reader: the segment it is blocked on, the read-ahead budget it
/// holds and what each provider delivered to it.
#[derive(Default)]
pub struct Watch {
    head: Mutex<Option<(u32, Option<Arc<Wait>>)>>,
    budget: AtomicU64,
    supplied: Mutex<Vec<Supplied>>,
}

impl Watch {
    /// The reader is blocked on `segment`, fetched as `wait` describes; `None` once not.
    pub fn set_head(&self, head: Option<(u32, Option<Arc<Wait>>)>) {
        *self.lock() = head;
    }

    /// The segment the reader is blocked on, and its fetch's wait if it has one.
    #[must_use]
    pub fn head(&self) -> Option<(u32, Option<WaitView>)> {
        let (segment, wait) = self.lock().clone()?;
        Some((segment, wait.map(|w| w.view())))
    }

    pub fn hold(&self, bytes: u64) {
        self.budget.fetch_add(bytes, Ordering::Relaxed);
    }

    pub fn release(&self, bytes: u64) {
        self.budget.fetch_sub(bytes, Ordering::Relaxed);
    }

    /// Read-ahead budget held, bytes.
    #[must_use]
    pub fn budget(&self) -> u64 {
        self.budget.load(Ordering::Relaxed)
    }

    /// `provider` delivered an article of `bytes` to this reader.
    pub fn supply(&self, provider: u16, bytes: u32) {
        let mut supplied = self.supplied.lock().unwrap_or_else(PoisonError::into_inner);
        match supplied.iter_mut().find(|s| s.provider == provider) {
            Some(s) => {
                s.articles += 1;
                s.bytes += u64::from(bytes);
            }
            None => supplied.push(Supplied {
                provider,
                articles: 1,
                bytes: u64::from(bytes),
            }),
        }
    }

    /// What each provider delivered so far, in the order they first did.
    #[must_use]
    pub fn supplied(&self) -> Vec<Supplied> {
        self.supplied
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    fn lock(&self) -> MutexGuard<'_, Option<(u32, Option<Arc<Wait>>)>> {
        self.head.lock().unwrap_or_else(PoisonError::into_inner)
    }
}
