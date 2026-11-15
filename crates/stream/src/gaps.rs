//! Holes filled in one file, shared by every reader of it, and when to stop filling.

use std::collections::BTreeSet;
use std::io;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use infinidysk_core::media::{self, Exceeded};
use tokio::time::Instant;

/// Fills in a row that fail the read in a file without a damage budget.
const MAX_IN_A_ROW: usize = 2;
/// Fills further apart than this are not in a row, so ranges reopened by a player count
/// together while one seeking around does not.
const IN_A_ROW_WINDOW: Duration = Duration::from_secs(30);
/// Holes found by playback are forgotten after this long without a fill or good read.
const EXPIRY: Duration = Duration::from_mins(5);
/// One fill or alignment warning per file this often.
const LOG_EVERY: Duration = Duration::from_secs(60);

#[derive(Debug, Default)]
pub struct Gaps {
    state: Mutex<State>,
}

#[derive(Debug, Default)]
struct State {
    /// Holes recorded in the file's metadata.
    known: BTreeSet<usize>,
    /// Holes playback found that the metadata did not record when opened.
    found: BTreeSet<usize>,
    /// Damage is capped as health caps a `degraded` file.
    budget: bool,
    in_a_row: Vec<Instant>,
    last_event: Option<Instant>,
    logged: Option<Instant>,
    suppressed: u64,
}

impl Gaps {
    /// Takes the file's recorded holes (flat segment indices) as of this open; `budget`
    /// when the file may play with damage up to health's caps.
    pub fn refresh(&self, known: BTreeSet<usize>, budget: bool) {
        let mut state = self.lock();
        state.expire();
        state.found.retain(|i| !known.contains(i));
        state.known = known;
        state.budget = budget;
    }

    /// Nothing happened for so long that the state may be dropped.
    pub fn stale(&self) -> bool {
        self.lock()
            .last_event
            .is_none_or(|at| at.elapsed() >= EXPIRY)
    }

    /// A hole providers need not be asked about again.
    pub(crate) fn is_hole(&self, index: usize) -> bool {
        let state = self.lock();
        state.known.contains(&index) || state.found.contains(&index)
    }

    pub(crate) fn found(&self, index: usize) {
        let mut state = self.lock();
        state.expire();
        state.found.insert(index);
        state.last_event = Some(Instant::now());
    }

    pub(crate) fn repaired(&self, index: usize) {
        let mut state = self.lock();
        state.known.remove(&index);
        state.found.remove(&index);
    }

    /// The reader moved onto a good segment.
    pub(crate) fn good(&self) {
        let mut state = self.lock();
        state.in_a_row.clear();
        state.last_event = Some(Instant::now());
    }

    /// The reader moved onto the filled segment `index` of a file with segment `sizes`;
    /// fails once the file has more damage than playback should paper over.
    pub(crate) fn filled(&self, index: usize, sizes: impl FnOnce() -> Vec<u64>) -> io::Result<()> {
        let mut state = self.lock();
        state.expire();
        let now = Instant::now();
        state.found.insert(index);
        state.last_event = Some(now);
        state
            .in_a_row
            .retain(|&at| now.duration_since(at) < IN_A_ROW_WINDOW);
        state.in_a_row.push(now);
        let limit = if state.budget {
            media::MAX_RUN
        } else {
            MAX_IN_A_ROW
        };
        let fills = state.in_a_row.len();
        if fills > limit {
            return Err(io::Error::other(format!(
                "{fills} unavailable segments in a row"
            )));
        }
        if state.budget {
            let damaged: Vec<usize> = state.known.union(&state.found).copied().collect();
            drop(state);
            if let Err(exceeded) = media::within_caps(&damaged, &sizes()) {
                let reason = match exceeded {
                    Exceeded::First => "the first segment is unavailable".to_owned(),
                    Exceeded::Run(run) => format!("{run} unavailable segments in a row"),
                    Exceeded::Count => format!("{} segments unavailable", damaged.len()),
                    Exceeded::Bytes => "more than 2 % of the file is unavailable".to_owned(),
                };
                return Err(io::Error::other(reason));
            }
        }
        Ok(())
    }

    /// Whether to warn about this file now, and how many warnings were held back before it.
    pub(crate) fn log(&self) -> Option<u64> {
        let mut state = self.lock();
        let now = Instant::now();
        if state
            .logged
            .is_some_and(|at| now.duration_since(at) < LOG_EVERY)
        {
            state.suppressed += 1;
            return None;
        }
        state.logged = Some(now);
        Some(std::mem::take(&mut state.suppressed))
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl State {
    fn expire(&mut self) {
        if self.last_event.is_some_and(|at| at.elapsed() >= EXPIRY) {
            self.found.clear();
            self.in_a_row.clear();
            self.last_event = None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SIZES: fn() -> Vec<u64> = || vec![10; 10_000];

    #[tokio::test(start_paused = true)]
    async fn fails_fills_in_a_row_until_a_good_read_or_expiry() {
        let gaps = Gaps::default();
        gaps.refresh(BTreeSet::new(), false);
        assert!(gaps.filled(5, SIZES).is_ok());
        assert!(gaps.filled(9, SIZES).is_ok());
        gaps.good();
        assert!(gaps.filled(20, SIZES).is_ok());
        assert!(gaps.filled(21, SIZES).is_ok());
        assert!(gaps.filled(22, SIZES).is_err(), "third in a row");
        assert!(gaps.is_hole(22));

        tokio::time::advance(IN_A_ROW_WINDOW).await;
        assert!(gaps.filled(23, SIZES).is_ok(), "older fills left the run");
        tokio::time::advance(EXPIRY).await;
        assert!(gaps.stale());
        gaps.refresh(BTreeSet::new(), false);
        assert!(!gaps.is_hole(22), "found holes expire");
    }

    #[tokio::test(start_paused = true)]
    async fn caps_damage_like_health_when_budgeted() {
        let gaps = Gaps::default();
        gaps.refresh((1..=63).map(|i| i * 10).collect(), true);
        assert!(
            gaps.filled(700, SIZES).is_ok(),
            "64 holes are within the cap"
        );
        gaps.good();
        assert!(gaps.filled(710, SIZES).is_err(), "65 are not");

        gaps.refresh(BTreeSet::new(), true);
        assert!(gaps.filled(0, SIZES).is_err(), "the first segment");
    }
}
