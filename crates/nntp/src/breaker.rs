use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

/// Failures counted towards a trip must fall within this window.
const WINDOW: Duration = Duration::from_secs(30);
/// Failures closer together than this count once: a dying provider fails every
/// in-flight request at the same moment.
const COALESCE: Duration = Duration::from_secs(2);
const TRIP_FAILURES: u32 = 3;
const INITIAL_COOLDOWN: Duration = Duration::from_secs(60);
const MAX_COOLDOWN: Duration = Duration::from_secs(300);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Outcome {
    /// Body delivered.
    Success,
    /// The provider answered, but without a usable article.
    Miss,
    Failure,
    /// Trips at once (cold provider unreachable, auth rejected, 502).
    Fatal,
}

/// Per-provider circuit breaker. Open providers are skipped until their cooldown
/// ends; then one probe request decides whether they close or reopen with a doubled
/// cooldown.
pub(crate) struct Breaker(Mutex<State>);

impl Breaker {
    pub(crate) fn new() -> Self {
        Self(Mutex::new(State::closed(INITIAL_COOLDOWN)))
    }

    pub(crate) fn is_open(&self) -> bool {
        self.lock().open_until.is_some()
    }

    /// Cooldown left while open; zero once a probe is due.
    pub(crate) fn open_for(&self) -> Option<Duration> {
        self.lock()
            .open_until
            .map(|until| until.saturating_duration_since(Instant::now()))
    }

    /// Open, cooldown over, and no probe in flight.
    pub(crate) fn probe_due(&self) -> bool {
        let state = self.lock();
        !state.probing
            && state
                .open_until
                .is_some_and(|until| Instant::now() >= until)
    }

    /// `None` while open and not probing. `force` admits regardless.
    pub(crate) fn attempt(&self, force: bool) -> Option<Attempt<'_>> {
        let probe = self.lock().admit(force, Instant::now())?;
        Some(Attempt {
            breaker: self,
            probe,
        })
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Ends the cooldown now.
    #[cfg(test)]
    pub(crate) fn lapse(&self) {
        if let Some(until) = &mut self.lock().open_until {
            *until = Instant::now();
        }
    }
}

/// One admitted request. Dropping it unfinished (cancelled) frees the probe slot.
pub(crate) struct Attempt<'a> {
    breaker: &'a Breaker,
    probe: bool,
}

impl Attempt<'_> {
    /// `true` when this outcome tripped the breaker.
    pub(crate) fn finish(mut self, outcome: Outcome) -> bool {
        let tripped = self
            .breaker
            .lock()
            .record(outcome, self.probe, Instant::now());
        self.probe = false;
        tripped
    }
}

impl Drop for Attempt<'_> {
    fn drop(&mut self) {
        if self.probe {
            self.breaker.lock().probing = false;
        }
    }
}

#[derive(Debug)]
struct State {
    failures: u32,
    window_start: Option<Instant>,
    last_failure: Option<Instant>,
    open_until: Option<Instant>,
    probing: bool,
    next_cooldown: Duration,
}

impl State {
    const fn closed(next_cooldown: Duration) -> Self {
        Self {
            failures: 0,
            window_start: None,
            last_failure: None,
            open_until: None,
            probing: false,
            next_cooldown,
        }
    }

    /// `Some(is_probe)` when admitted.
    fn admit(&mut self, force: bool, now: Instant) -> Option<bool> {
        match self.open_until {
            None => Some(false),
            Some(until) if now >= until && !self.probing => {
                self.probing = true;
                Some(true)
            }
            _ => force.then_some(false),
        }
    }

    /// `true` when this trips the breaker.
    fn record(&mut self, outcome: Outcome, probe: bool, now: Instant) -> bool {
        // Requests started before the trip, or forced through it; only the probe decides.
        if self.open_until.is_some() && !probe {
            return false;
        }
        self.probing = false;
        match outcome {
            Outcome::Success => *self = Self::closed(INITIAL_COOLDOWN),
            // A clean answer proves liveness but keeps the cooldown ladder.
            Outcome::Miss => *self = Self::closed(self.next_cooldown),
            Outcome::Fatal => return self.trip(now),
            Outcome::Failure if probe => return self.trip(now),
            Outcome::Failure => {
                if self
                    .last_failure
                    .is_some_and(|last| now.duration_since(last) < COALESCE)
                {
                    return false;
                }
                if self
                    .window_start
                    .is_none_or(|start| now.duration_since(start) > WINDOW)
                {
                    self.failures = 0;
                    self.window_start = Some(now);
                }
                self.failures += 1;
                self.last_failure = Some(now);
                if self.failures >= TRIP_FAILURES {
                    return self.trip(now);
                }
            }
        }
        false
    }

    fn trip(&mut self, now: Instant) -> bool {
        let cooldown = self.next_cooldown;
        *self = Self::closed((cooldown * 2).min(MAX_COOLDOWN));
        self.open_until = Some(now + cooldown);
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn trips_probes_and_backs_off() {
        let t0 = Instant::now();
        let at = |secs| t0 + Duration::from_secs(secs);
        let mut state = State::closed(INITIAL_COOLDOWN);

        // A burst counts once; three spaced failures trip.
        for secs in [0, 0, 1, 3] {
            state.record(Outcome::Failure, false, at(secs));
        }
        assert_eq!(state.admit(false, at(4)), Some(false));
        state.record(Outcome::Failure, false, at(6));
        assert_eq!(state.admit(false, at(7)), None);
        assert_eq!(state.admit(true, at(7)), Some(false), "forced");

        // One probe after the cooldown; its failure doubles the next cooldown.
        assert_eq!(state.admit(false, at(66)), Some(true));
        assert_eq!(state.admit(false, at(66)), None, "one probe at a time");
        state.record(Outcome::Failure, true, at(67));
        assert_eq!(state.admit(false, at(67 + 119)), None);
        assert_eq!(state.admit(false, at(67 + 120)), Some(true));

        // A miss closes but keeps the ladder; a success resets it.
        state.record(Outcome::Miss, true, at(190));
        assert_eq!(state.admit(false, at(190)), Some(false));
        state.record(Outcome::Fatal, false, at(191));
        assert_eq!(state.admit(false, at(191 + 239)), None);
        assert_eq!(state.admit(false, at(191 + 240)), Some(true));
        state.record(Outcome::Success, true, at(432));
        state.record(Outcome::Fatal, false, at(433));
        assert_eq!(state.admit(false, at(433 + 60)), Some(true));
    }

    #[test]
    fn requests_finishing_after_a_trip_leave_it_open() {
        let now = Instant::now();
        let mut state = State::closed(INITIAL_COOLDOWN);
        state.record(Outcome::Fatal, false, now);
        state.record(Outcome::Success, false, now);
        state.record(Outcome::Miss, false, now);
        assert_eq!(state.admit(false, now), None);
    }
}
