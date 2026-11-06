//! The daily cleanup (import policy §8, §9, §10.4): forgets history older than 30 days,
//! deletes NZBs past their retention window, reconciles with each Arr's library and
//! removes handed-off STRM files no Arr imported. Downloads always stay.

use std::sync::Arc;
use std::time::Duration;

use infinidysk_arr::Arr;
use infinidysk_db::{Db, now_ms};
use tokio::sync::{Notify, watch};
use tokio::time::Instant;
use tracing::{info, warn};
use uuid::Uuid;

use crate::arr::Discovery;
use crate::handoff::Writer;

const HISTORY_MS: i64 = 30 * DAY_MS;
const DAY_MS: i64 = 24 * 60 * 60 * 1000;
const RETRY: Duration = Duration::from_hours(1);
/// When the last full run finished, so a restart does not run it again early.
const LAST_KEY: &str = "cleanup.last_run";

/// Runs when due, then daily. `wake` (a retention change) deletes expired NZBs at once.
pub async fn run(
    db: Db,
    wake: Arc<Notify>,
    discovery: Arc<Discovery>,
    arrs: watch::Receiver<Vec<Arr>>,
    handoff: Arc<Writer>,
) {
    let mut last = match db.config().await {
        Ok(config) => config
            .iter()
            .find(|(k, _)| k == LAST_KEY)
            .and_then(|(_, v)| v.parse().ok())
            .unwrap_or(0),
        Err(error) => {
            warn!(reason = %error, "could not read when cleanup last ran");
            0
        }
    };
    // Arrs whose reconciliation failed, retried hourly instead of at the next daily run.
    let mut retry = Vec::new();
    // Failed requested runs; kept apart because only daily retries may mark the backfill done.
    let mut retry_asked: Vec<Uuid> = Vec::new();
    let mut retry_at = RetryClock::default();
    loop {
        let wait = last + DAY_MS - now_ms();
        if wait <= 0 {
            let writing = db.write_permit().await;
            daily(&db).await;
            drop(writing);
            let enabled = arrs.borrow().clone();
            let writing = db.write_permit().await;
            retry = discovery.reconcile(enabled).await;
            retry_asked.clear();
            retry_at.restart();
            drop(writing);
            let writing = db.write_permit().await;
            handoff.sweep(&db).await;
            drop(writing);
            last = now_ms();
            continue;
        }
        // ponytail: not aligned with the hourly discovery, so a recovered Arr can wait
        // up to two hours; wake on discovery if that proves too slow.
        let now = Instant::now();
        retry_at.track(!retry.is_empty() || !retry_asked.is_empty(), now);
        let daily_at = now + Duration::from_millis(wait.cast_unsigned());
        let until = retry_at.0.map_or(daily_at, |at| at.min(daily_at));
        tokio::select! {
            () = tokio::time::sleep_until(until) => {
                if retry_at.due(Instant::now()) && last + DAY_MS > now_ms() {
                    retry_at.restart();
                    let pick = |ids: &[Uuid]| -> Vec<Arr> {
                        arrs.borrow().iter().filter(|a| ids.contains(&a.id)).cloned().collect()
                    };
                    let due = pick(&retry);
                    retry_asked.retain(|id| !retry.contains(id));
                    let asked = pick(&retry_asked);
                    let _writing = db.write_permit().await;
                    retry = discovery.reconcile(due).await;
                    retry_asked = discovery.reconcile_each(asked).await;
                }
            }
            () = wake.notified() => {
                let _writing = db.write_permit().await;
                expire(&db).await;
            }
            asked = discovery.requested_reconciles() => {
                let due: Vec<Arr> = arrs
                    .borrow()
                    .iter()
                    .filter(|a| asked.contains(&a.id))
                    .cloned()
                    .collect();
                let _writing = db.write_permit().await;
                let failed = discovery.reconcile_each(due).await;
                retry_asked.retain(|id| !asked.contains(id));
                for id in failed {
                    if !retry_asked.contains(&id) {
                        retry_asked.push(id);
                    }
                }
            }
        }
    }
}

async fn daily(db: &Db) {
    match db.prune_history(now_ms() - HISTORY_MS).await {
        Ok(0) => {}
        Ok(n) => info!(jobs = n, "forgot history older than 30 days"),
        Err(error) => warn!(reason = %error, "could not prune history"),
    }
    expire(db).await;
    let now = now_ms().to_string();
    if let Err(error) = db.write_config(&[(LAST_KEY, &now)], &[]).await {
        warn!(reason = %error, "could not record the cleanup run");
    }
}

async fn expire(db: &Db) {
    match db.delete_expired_nzbs().await {
        Ok(0) => {}
        Ok(n) => info!(nzbs = n, "deleted NZBs past their retention"),
        Err(error) => warn!(reason = %error, "could not delete expired NZBs"),
    }
}

/// When the hourly retry is due; other wakes of the cleanup loop must not push it back.
#[derive(Default)]
struct RetryClock(Option<Instant>);

impl RetryClock {
    /// Starts the hour when retries appear, keeps a running hour, and stops when none are left.
    fn track(&mut self, pending: bool, now: Instant) {
        if !pending {
            self.0 = None;
        } else if self.0.is_none() {
            self.0 = Some(now + RETRY);
        }
    }

    fn restart(&mut self) {
        self.0 = None;
    }

    fn due(&self, now: Instant) -> bool {
        self.0.is_some_and(|at| at <= now)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wakes_for_other_work_do_not_postpone_a_retry() {
        let start = Instant::now();
        let mut clock = RetryClock::default();
        clock.track(true, start);
        // A webhook request every 30 minutes re-enters the loop.
        for half_hours in 1..6 {
            clock.track(true, start + half_hours * RETRY / 2);
        }
        assert!(!clock.due(start + RETRY - Duration::from_secs(1)));
        assert!(clock.due(start + RETRY));

        clock.restart();
        clock.track(true, start + RETRY);
        assert!(clock.due(start + 2 * RETRY));
        clock.track(false, start + 2 * RETRY);
        assert!(!clock.due(start + 3 * RETRY));
    }
}
