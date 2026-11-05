//! Automatic queue management (import policy §10.3): every few seconds, read the queue of
//! each Arr holding one of our verified clients and clear the downloads whose import is
//! stuck for a reason we know how to settle.

use std::collections::BTreeMap;
use std::time::Duration;

use infinidysk_arr::queue::{self as arr_queue, Action, Entry};
use infinidysk_arr::{self as arr, Arr, Profile};
use infinidysk_core::JobId;
use infinidysk_db::{Db, now_ms};
use serde::Serialize;
use utoipa::ToSchema;
use uuid::Uuid;

use super::{ArrBinding, Discovery};

const POLL: Duration = Duration::from_secs(10);
/// How long a download must have been finished here before its queue entry is touched,
/// so the Arr gets its own chance at importing it.
const GRACE_MS: i64 = 5 * 60 * 1000;
/// Tracking states where the Arr is done trying; anything else is still in motion.
const SETTLED: &[&str] = &["importBlocked", "importPending", "importFailed", "imported"];

/// A download the Arr will not import for a reason only the user can settle.
#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct ArrQueueIssue {
    pub title: String,
    pub messages: Vec<String>,
}

impl Discovery {
    /// Polls the queues while `arr.queue_management` is on. Instances are polled one after
    /// another, so two polls of one instance never overlap.
    pub async fn manage_queues(&self) {
        let db = self.queue.db().clone();
        loop {
            tokio::time::sleep(POLL).await;
            let on = self.config.read().await.settings().arr.queue_management;
            let known: Vec<(Arr, Vec<String>)> = self
                .lock()
                .values_mut()
                .map(|k| {
                    if !on {
                        k.queue_issues.clear();
                    }
                    let ours = k
                        .clients
                        .iter()
                        .filter(|(_, b)| *b == ArrBinding::Verified)
                        .map(|(c, _)| c.name.clone())
                        .collect();
                    (k.arr.clone(), ours)
                })
                .collect();
            if !on {
                continue;
            }
            for (arr, ours) in known {
                let issues = if ours.is_empty() {
                    Vec::new()
                } else {
                    match arr_queue::queue(&self.http, &arr).await {
                        Ok(entries) => self.settle(&db, &arr, &ours, entries).await,
                        Err(error) => {
                            tracing::debug!(arr = %arr.name, reason = %error, "reading the Arr queue failed");
                            continue;
                        }
                    }
                };
                if let Some(k) = self.lock().get_mut(&arr.id) {
                    k.queue_issues = issues;
                }
            }
        }
    }

    /// Acts on the stuck downloads of one queue; returns those left to the user.
    async fn settle(
        &self,
        db: &Db,
        arr: &Arr,
        ours: &[String],
        entries: Vec<Entry>,
    ) -> Vec<ArrQueueIssue> {
        let _writing = db.write_permit().await;
        let mut downloads: BTreeMap<String, Vec<Entry>> = BTreeMap::new();
        for entry in entries {
            if ours.iter().any(|c| c.eq_ignore_ascii_case(&entry.client)) {
                downloads
                    .entry(entry.download_id.clone())
                    .or_default()
                    .push(entry);
            }
        }
        let mut issues = Vec::new();
        for (download_id, entries) in downloads {
            if !entries.iter().all(|e| {
                e.status.eq_ignore_ascii_case("completed")
                    && SETTLED.iter().any(|s| s.eq_ignore_ascii_case(&e.state))
            }) {
                continue;
            }
            let Some(job) = Uuid::try_parse(&download_id).ok().map(JobId) else {
                continue;
            };
            match db.history_entry(job).await {
                Ok(Some(h)) if now_ms().saturating_sub(h.created_at) >= GRACE_MS => {}
                Ok(_) => continue,
                Err(error) => {
                    tracing::warn!(reason = %error, "reading history failed");
                    continue;
                }
            }
            // On Sonarr's v3 API a season pack has one entry per episode, each repeating
            // the same messages.
            let mut messages: Vec<String> = Vec::new();
            for m in entries.iter().flat_map(|e| &e.messages) {
                if !messages.contains(m) {
                    messages.push(m.clone());
                }
            }
            let action = if messages.is_empty() {
                self.ghost(arr, &download_id)
                    .await
                    .then_some(Action::Remove)
            } else {
                arr_queue::action(&messages)
            };
            match action {
                Some(action) => self.act(db, arr, &entries, action).await,
                None if !messages.is_empty() => issues.push(ArrQueueIssue {
                    title: entries[0].title.clone(),
                    messages,
                }),
                None => {}
            }
        }
        issues
    }

    /// The Arr imported from this download, so a lingering entry for it is a leftover.
    async fn ghost(&self, arr: &Arr, download_id: &str) -> bool {
        match arr::imports(&self.http, arr, download_id).await {
            Ok(imports) => !imports.is_empty(),
            Err(error) => {
                tracing::debug!(arr = %arr.name, reason = %error, "reading Arr history failed");
                false
            }
        }
    }

    async fn act(&self, db: &Db, arr: &Arr, entries: &[Entry], action: Action) {
        let blocklist = match action {
            Action::Remove => None,
            Action::Replace(reason) => Some(reason),
        };
        let first = &entries[0];
        let reserve = self.reserve(db, arr, entries);
        match arr_queue::remove(&self.http, arr, first.id, blocklist, reserve).await {
            Ok(search) => tracing::info!(
                arr = %arr.name, download = %first.download_id, blocklist = blocklist.is_some(),
                search, "removed a stuck download from the Arr queue"
            ),
            Err(error) => tracing::warn!(
                arr = %arr.name, download = %first.download_id, reason = %error,
                "removing a stuck download from the Arr queue failed"
            ),
        }
    }

    /// Takes the shared search budget for the download's media; `false` (blocklist only)
    /// when it is spent, unknown, or the Arr cannot search.
    async fn reserve(&self, db: &Db, arr: &Arr, entries: &[Entry]) -> bool {
        if arr.profile == Profile::Sportarr || entries.iter().any(|e| e.media.is_empty()) {
            return false;
        }
        let mut media: Vec<(&str, i64)> = entries
            .iter()
            .flat_map(|e| e.media.iter().copied())
            .collect();
        media.sort_unstable();
        media.dedup();
        let Some(&(kind, _)) = media.first() else {
            return false;
        };
        if media.iter().any(|(k, _)| *k != kind) {
            return false;
        }
        let ids: Vec<i64> = media.iter().map(|(_, id)| *id).collect();
        match db.reserve_searches(arr.id, kind, &ids, now_ms()).await {
            Ok(reserved) => reserved,
            Err(error) => {
                tracing::warn!(reason = %error, "reserving Arr searches failed");
                false
            }
        }
    }
}
