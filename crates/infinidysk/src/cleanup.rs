//! The daily cleanup (import policy §8, §9, §10.4): forgets history older than 30 days,
//! deletes NZBs past their retention window, reconciles with each Arr's library and
//! removes handed-off STRM files no Arr imported. Downloads always stay.

use std::sync::Arc;
use std::time::Duration;

use infinidysk_arr::Arr;
use infinidysk_db::{Db, now_ms};
use tokio::sync::{Notify, watch};
use tracing::{info, warn};

use crate::arr::Discovery;
use crate::handoff::Writer;

const HISTORY_MS: i64 = 30 * DAY_MS;
const DAY_MS: i64 = 24 * 60 * 60 * 1000;
const RETRY_MS: i64 = 60 * 60 * 1000;
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
    loop {
        let wait = last + DAY_MS - now_ms();
        if wait <= 0 {
            let writing = db.write_permit().await;
            daily(&db).await;
            drop(writing);
            let enabled = arrs.borrow().clone();
            let writing = db.write_permit().await;
            retry = discovery.reconcile(enabled).await;
            drop(writing);
            let writing = db.write_permit().await;
            handoff.sweep(&db).await;
            drop(writing);
            last = now_ms();
            continue;
        }
        // ponytail: not aligned with the hourly discovery, so a recovered Arr can wait
        // up to two hours; wake on discovery if that proves too slow.
        let sleep = if retry.is_empty() {
            wait
        } else {
            wait.min(RETRY_MS)
        };
        tokio::select! {
            () = tokio::time::sleep(Duration::from_millis(sleep.cast_unsigned())) => {
                if !retry.is_empty() && last + DAY_MS > now_ms() {
                    let due: Vec<Arr> = arrs
                        .borrow()
                        .iter()
                        .filter(|a| retry.contains(&a.id))
                        .cloned()
                        .collect();
                    let _writing = db.write_permit().await;
                    retry = discovery.reconcile(due).await;
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
                for id in discovery.reconcile_each(due).await {
                    if !retry.contains(&id) {
                        retry.push(id);
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
