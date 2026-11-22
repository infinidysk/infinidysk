//! Separate durable availability and repair workers. Playback keeps priority.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result};
use infinidysk_core::{BlobId, PatchId, blob, repair::Patch};
use infinidysk_db::{
    now_ms,
    repair::MAX_PATCHES,
    repair_jobs::{DAMAGED, EXHAUSTED, Job},
};
use infinidysk_health::{Availability, AvailabilityOptions, AvailabilityProgress};
use infinidysk_nntp::{Client, patch};
use infinidysk_queue::{
    nzb,
    repair::{self, Limits},
};
use tokio::sync::OwnedSemaphorePermit;
use tokio_util::sync::CancellationToken;

use super::{Publisher, READERS, Repairs};

/// Health policy §7.2: three attempts, six hours apart.
const ATTEMPTS: i32 = 3;
const COOLDOWN_MS: i64 = 6 * 3_600_000;
/// Health policy §7.3: least recently read repairs make room for new ones.
pub(super) const STORE_BYTES: u64 = 4_000_000_000;
const CHECK_CONCURRENCY: usize = 64;
const MEMORY_BYTES: usize = 256_000_000;
const TRANSFER_BYTES: u64 = 100_000_000_000;
const TIMEOUT: Duration = Duration::from_hours(1);
/// Room for one stored file's descriptor and framing; descriptors are at most 8 KB.
const HEADER_RESERVE: u64 = 16_384;

const AVAILABLE: &str =
    "Articles are available. Body spot checks passed; full byte integrity was not checked.";
const HASH_ONLY: &str =
    "Whole-file hashes matched. Some files have no slice checksums for locating damage.";

/// Retrying cannot help: content, metadata, ownership or a fixed limit.
#[derive(Debug, thiserror::Error)]
#[error("repair unavailable: {0}")]
pub(super) struct Unavailable(pub(super) String);

fn unavailable(reason: impl Into<String>) -> Unavailable {
    Unavailable(reason.into())
}

/// One attempt's result. [`Outcome::finish`] alone decides state, retry and stored outcome.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Outcome {
    Available,
    Verified {
        hash_only: bool,
    },
    Repaired {
        hash_only: bool,
    },
    /// Missing or corrupt articles, with no repair requested.
    Damaged,
    /// `escalate_par2` already finished the check and queued its repair.
    Escalated,
    /// Playback preempted a check; the attempt does not count.
    Yielded,
    /// Shutdown stopped the attempt; it resumes at once and does not count.
    Interrupted,
    /// Another repair changed stored articles during a check of an unchanged file.
    /// The check runs again at once and does not count.
    Restarted,
    Cancelled,
    Unavailable(String),
    /// Provider, storage or time-limit failure, retried after the cooldown.
    Failed(String),
}

#[derive(Debug, PartialEq, Eq)]
pub(super) struct Finish {
    pub(super) state: i16,
    pub(super) reason: String,
    pub(super) retry: Option<i64>,
    pub(super) outcome: i16,
    /// Return the claimed attempt.
    pub(super) yielded: bool,
}

impl Outcome {
    pub(super) fn finish(self, attempts: i32, now: i64) -> Option<Finish> {
        let yielded = matches!(self, Self::Yielded | Self::Interrupted | Self::Restarted);
        let hashes = |hash_only| if hash_only { HASH_ONLY } else { "" }.to_owned();
        let (state, reason, retry, outcome) = match self {
            Self::Escalated => return None,
            Self::Available => (2, AVAILABLE.to_owned(), None, 0),
            Self::Verified { hash_only } => (2, hashes(hash_only), None, 0),
            Self::Repaired { hash_only } => (3, hashes(hash_only), None, 0),
            Self::Damaged => (
                5,
                "Missing or corrupt articles found.".to_owned(),
                None,
                DAMAGED,
            ),
            Self::Yielded => (
                4,
                "Waiting for playback; availability check will resume.".to_owned(),
                Some(now),
                0,
            ),
            Self::Interrupted => (4, "interrupted; retrying".to_owned(), Some(now), 0),
            Self::Restarted => (
                4,
                "Another repair changed saved articles; checking again.".to_owned(),
                Some(now),
                0,
            ),
            Self::Cancelled => (6, "cancelled".to_owned(), None, 0),
            Self::Unavailable(reason) => (5, reason, None, 0),
            Self::Failed(reason) if attempts < ATTEMPTS => (4, reason, Some(now + COOLDOWN_MS), 0),
            Self::Failed(reason) => (5, format!("Retry limit reached: {reason}"), None, EXHAUSTED),
        };
        Some(Finish {
            state,
            reason,
            retry,
            outcome,
            yielded,
        })
    }
}

pub(super) struct Progress {
    pub(super) memory: usize,
    pub(super) store_bytes: u64,
    phase: Mutex<&'static str>,
    watch: Arc<infinidysk_core::wait::Watch>,
    stored: AtomicU64,
    verified: AtomicU64,
    availability: Arc<AvailabilityProgress>,
    requests: Arc<repair::Progress>,
}

impl Default for Progress {
    fn default() -> Self {
        Self {
            memory: MEMORY_BYTES,
            store_bytes: STORE_BYTES,
            phase: Mutex::new(""),
            watch: Arc::default(),
            stored: AtomicU64::default(),
            verified: AtomicU64::default(),
            availability: Arc::default(),
            requests: Arc::default(),
        }
    }
}

impl Progress {
    fn phase(&self, phase: &'static str) {
        *self.phase.lock().unwrap_or_else(PoisonError::into_inner) = phase;
    }

    fn current_phase(&self) -> &'static str {
        *self.phase.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn transferred(&self) -> u64 {
        self.watch.supplied().iter().map(|s| s.bytes).sum::<u64>()
            + self.availability.body_bytes.load(Ordering::Relaxed)
    }

    fn counters(&self, job: &Job) -> (i64, i64) {
        (
            job.transferred_bytes
                .saturating_add(i64::try_from(self.transferred()).unwrap_or(i64::MAX)),
            job.stored_bytes.saturating_add(
                i64::try_from(self.stored.load(Ordering::Relaxed)).unwrap_or(i64::MAX),
            ),
        )
    }

    fn articles(&self) -> (i64, i64) {
        (
            i64::try_from(self.availability.checked.load(Ordering::Acquire)).unwrap_or(i64::MAX),
            i64::try_from(self.availability.total.load(Ordering::Relaxed)).unwrap_or(i64::MAX),
        )
    }

    fn probe(&self) -> infinidysk_core::trace::Probe {
        infinidysk_core::trace::Probe {
            trace: None,
            wait: None,
            watch: Some(Arc::clone(&self.watch)),
            segment: 0,
            hedge: false,
            size: 0,
        }
    }

    fn requests(&self, job: &Job) -> (i64, i64) {
        let (completed, missing) = self.requests.requests();
        (
            job.completed_requests
                .saturating_add(i64::try_from(completed).unwrap_or(i64::MAX)),
            job.missing_requests
                .saturating_add(i64::try_from(missing).unwrap_or(i64::MAX)),
        )
    }

    /// Persist live progress; a cancelled or finished claim ignores it.
    async fn save(&self, db: &infinidysk_db::Db, job: &Job) {
        let claim = job.claim();
        let (checked, total) = self.articles();
        if let Err(error) = db.update_availability_progress(claim, checked, total).await {
            tracing::warn!(reason = %error, "article progress could not be saved");
        }
        let (completed, missing) = self.requests(job);
        if let Err(error) = db.update_par2_requests(claim, completed, missing).await {
            tracing::warn!(reason = %error, "repair request progress could not be saved");
        }
        let (transferred, stored) = self.counters(job);
        if let Err(error) = db
            .update_par2_progress(claim, self.current_phase(), transferred, stored, now_ms())
            .await
        {
            tracing::warn!(job = %job.id, reason = %error, "repair progress could not be saved");
        }
    }
}

impl Repairs {
    pub async fn run(
        self: Arc<Self>,
        client: Arc<Client>,
        publisher: Publisher,
        health: Arc<infinidysk_health::Health>,
        cancel: CancellationToken,
    ) {
        while let Err(error) = self.db.resume_par2(now_ms()).await {
            tracing::warn!(reason = %error, "repair requests could not be resumed");
            tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
        }
        tokio::join!(
            self.run_lane(&client, &publisher, &health, &cancel, true),
            self.run_lane(&client, &publisher, &health, &cancel, false),
        );
    }

    async fn run_lane(
        self: &Arc<Self>,
        client: &Arc<Client>,
        publisher: &Publisher,
        health: &Arc<infinidysk_health::Health>,
        cancel: &CancellationToken,
        verify: bool,
    ) {
        let mut cleaned = None;
        loop {
            if cancel.is_cancelled() {
                return;
            }
            if verify && health.playback_active() {
                tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_millis(250)) => {} }
                continue;
            }
            match self.db.claim_par2_lane(now_ms(), verify, false).await {
                Ok(Some(job)) => {
                    self.attempt(client, publisher, health, &job, cancel).await;
                    continue;
                }
                Ok(None) if !verify => {
                    let full_pass = self
                        .db
                        .par2_pending()
                        .await
                        .is_ok_and(|pending| pending == 0);
                    if let Err(error) =
                        infinidysk_import::legacy_repairs::finish_rewrite(&self.db, full_pass).await
                    {
                        tracing::warn!(reason = %error, "legacy repair retirement will retry later");
                    }
                    if cleaned
                        .is_none_or(|last: Instant| last.elapsed() >= Duration::from_secs(3600))
                    {
                        if let Err(error) = publisher.cleanup(cancel).await {
                            tracing::warn!(reason = %error, "unused repair files could not be cleaned up");
                        }
                        cleaned = Some(Instant::now());
                    }
                }
                Ok(None) => {}
                Err(error) => tracing::warn!(reason = %error, "repair requests could not be read"),
            }
            tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
        }
    }

    async fn attempt(
        self: &Arc<Self>,
        client: &Arc<Client>,
        publisher: &Publisher,
        health: &Arc<infinidysk_health::Health>,
        job: &Job,
        cancel: &CancellationToken,
    ) {
        let stopped = cancel.child_token();
        let progress = Progress::default();
        let _stop = stopped.clone().drop_guard();
        let work = self.perform(client, publisher, health, job, &stopped, &progress);
        let deadline = tokio::time::sleep(TIMEOUT);
        tokio::pin!(work, deadline);
        // Every stop is cooperative: a publication admitted before it still reports.
        let mut expired = false;
        let result = tokio::select! {
            result = &mut work => result,
            () = &mut deadline => { expired = true; stopped.cancel(); work.await },
            () = async {
                loop {
                    tokio::select! { () = cancel.cancelled() => break, () = tokio::time::sleep(Duration::from_secs(2)) => {} }
                    if self.db.par2_cancelled(job.id).await.unwrap_or(false) { break; }
                    progress.save(&self.db, job).await;
                }
            } => { stopped.cancel(); work.await },
        };
        let outcome = match result {
            Ok(outcome) => outcome,
            _ if stopped.is_cancelled() && cancel.is_cancelled() => Outcome::Interrupted,
            _ if expired => Outcome::Failed("repair time limit reached".to_owned()),
            _ if stopped.is_cancelled() => Outcome::Cancelled,
            Err(error) => match error.downcast_ref::<Unavailable>() {
                Some(Unavailable(detail)) => match progress.current_phase() {
                    "" => Outcome::Unavailable(format!("repair unavailable: {detail}")),
                    phase => Outcome::Unavailable(format!("repair unavailable: {phase}: {detail}")),
                },
                None => Outcome::Failed(format!("{error:#}")),
            },
        };
        let failed = matches!(outcome, Outcome::Unavailable(_) | Outcome::Failed(_));
        let verified = if matches!(outcome, Outcome::Verified { .. } | Outcome::Repaired { .. }) {
            i64::try_from(progress.verified.load(Ordering::Relaxed)).unwrap_or(i64::MAX)
        } else {
            0
        };
        let Some(finish) = outcome.finish(job.attempts, now_ms()) else {
            return;
        };
        if failed {
            tracing::warn!(job = %job.id, reason = %finish.reason, "repair did not finish");
        }
        let claim = job.claim();
        loop {
            let saved = async {
                let (completed, missing) = progress.requests(job);
                self.db
                    .update_par2_requests(claim, completed, missing)
                    .await?;
                self.db.set_par2_verified(claim, verified).await?;
                let (checked, total) = progress.articles();
                self.db
                    .update_availability_progress(claim, checked, total)
                    .await?;
                let (transferred, stored) = progress.counters(job);
                if finish.yielded {
                    // One statement, so a failed save cannot defer without the refund.
                    return self
                        .db
                        .yield_par2(
                            claim,
                            job.attempts,
                            &finish.reason,
                            transferred,
                            stored,
                            now_ms(),
                        )
                        .await;
                }
                self.db
                    .finish_par2(
                        claim,
                        finish.state,
                        &finish.reason,
                        transferred,
                        stored,
                        now_ms(),
                        finish.retry,
                        finish.outcome,
                    )
                    .await
            }
            .await;
            match saved {
                Ok(()) => return,
                Err(error) => {
                    tracing::warn!(job = %job.id, reason = %error, "repair result could not be saved; retrying");
                }
            }
            tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
        }
    }

    async fn check(
        &self,
        health: &infinidysk_health::Health,
        job: &Job,
        cancel: &CancellationToken,
        progress: &Progress,
    ) -> Result<Outcome> {
        let id = infinidysk_core::ItemId(job.item_id.context(unavailable("file is missing"))?);
        let owner = infinidysk_db::repair::Owner::Item(id);
        anyhow::ensure!(
            self.claimed(owner, job).await?,
            unavailable("availability source changed")
        );
        progress.phase("Checking article availability");
        let result = health
            .check_availability(
                id,
                cancel,
                AvailabilityOptions {
                    concurrency: CHECK_CONCURRENCY,
                    memory_bytes: progress.memory,
                    decoded_bytes: TRANSFER_BYTES,
                    evidence: None,
                    progress: Arc::clone(&progress.availability),
                },
            )
            .await?;
        // The scan validates against its own snapshot. Generations only grow, so a claim
        // that still matches now also matched what the scan read.
        let current = match result {
            Availability::Available { .. }
            | Availability::Damaged { .. }
            | Availability::Changed => self.claimed(owner, job).await?,
            _ => true,
        };
        match result {
            Availability::Available { .. } | Availability::Damaged { .. } if !current => {
                Err(unavailable("availability source changed").into())
            }
            Availability::Available { .. } => Ok(Outcome::Available),
            Availability::Damaged { .. } if job.repair_on_failure && job.nzb_blob.is_some() => {
                progress.phase("Queuing repair");
                progress.save(&self.db, job).await;
                // A deadline or cancel during the save wins; once admitted, the handoff runs.
                anyhow::ensure!(
                    !cancel.is_cancelled(),
                    "repair stopped before repair handoff"
                );
                anyhow::ensure!(
                    self.db.escalate_par2(job.claim(), now_ms()).await?,
                    unavailable("source changed before repair handoff")
                );
                Ok(Outcome::Escalated)
            }
            Availability::Damaged { .. } => Ok(Outcome::Damaged),
            Availability::Source(error) => Err(anyhow::Error::new(error)),
            Availability::Unavailable(reason) => Err(unavailable(reason.to_string()).into()),
            // Repair epochs are global; only a change to this file's source ends the check.
            Availability::Changed if current => Ok(Outcome::Restarted),
            Availability::Changed => Err(unavailable("availability source changed").into()),
            Availability::Cancelled => anyhow::bail!("repair cancelled"),
            Availability::Yielded => Ok(Outcome::Yielded),
        }
    }

    /// Whether `job` is still the current check for the item's metadata and NZB.
    async fn claimed(&self, owner: infinidysk_db::repair::Owner, job: &Job) -> Result<bool> {
        Ok(self
            .db
            .par2_result(owner, true)
            .await?
            .is_some_and(|current| current.id == job.id))
    }

    pub(super) async fn perform(
        self: &Arc<Self>,
        client: &Arc<Client>,
        publisher: &Publisher,
        health: &infinidysk_health::Health,
        job: &Job,
        cancel: &CancellationToken,
        progress: &Progress,
    ) -> Result<Outcome> {
        if job.verify_only {
            return self.check(health, job, cancel, progress).await;
        }
        let legacy = self
            .db
            .config()
            .await?
            .iter()
            .any(|(key, value)| key == "legacy.repair.enabled" && value == "1");
        let nzb = job
            .nzb_blob
            .context(unavailable("original NZB is missing"))?;
        let owner = if let Some(id) = job.item_id {
            infinidysk_db::repair::Owner::Item(infinidysk_core::ItemId(id))
        } else {
            self.db
                .par2_owner(BlobId(nzb))
                .await?
                .context(unavailable("source no longer owned"))?
        };
        let source = self
            .db
            .repair_source(owner)
            .await?
            .context(unavailable("source disappeared"))?;
        anyhow::ensure!(
            source.generation() == job.generation,
            unavailable("original NZB changed")
        );
        let raw = self
            .db
            .repair_blob(source.nzb(), 16_000_000)
            .await?
            .context(unavailable(
                "original NZB is missing or exceeds the metadata allowance",
            ))?;
        let candidates: Option<Arc<infinidysk_nntp::RepairLookup>> = if legacy {
            let db = self.db.clone();
            let lookup: infinidysk_nntp::RepairLookup = Box::new(move |key| {
                let infinidysk_nntp::RepairKey::Article(id) = key else {
                    return None;
                };
                if id.len() > 8192 {
                    return None;
                }
                let id = id.to_owned();
                let db = db.clone();
                Some(Box::pin(async move {
                    infinidysk_import::legacy_repairs::read(&db, &id).await
                }))
            });
            Some(Arc::new(lookup))
        } else {
            None
        };
        let mut context = repair::Context {
            source: Some((source.nzb(), source.generation())),
            candidates,
            probe: Some(progress.probe()),
            progress: Some(Arc::clone(&progress.requests)),
            metadata: None,
        };
        let metadata = if let infinidysk_db::repair::Owner::Item(id) = owner
            && let Some(item) = self.db.item(id).await?
            && let Some(meta) = item.meta_blob
        {
            self.db
                .repair_blob(meta, 16_000_000)
                .await?
                .map(|raw| blob::decode_meta_limited(&raw, 16_000_000))
                .transpose()?
        } else {
            None
        };
        let mut metadata_changed = false;
        context.metadata = metadata.map(Arc::new);
        let mut covered = Vec::new();
        let client_for_discovery = Arc::clone(client);
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        let discovery_context = context.clone();
        let decoded_bytes = TRANSFER_BYTES.saturating_sub(progress.transferred());
        let memory_bytes = progress.memory.saturating_sub(96_000_000);
        // Eviction makes room before writes, so the whole store is the allowance.
        let retained_bytes = progress.store_bytes;
        progress.phase("Discovering PAR2 files");
        let (nzb, reader, mut limits) = tokio::task::spawn_blocking(move || -> Result<_> {
            let decoded = blob::decode_nzb_limited(&raw, 16_000_000).context(unavailable(
                "original NZB exceeds its allowance or is unreadable",
            ))?;
            let nzb =
                Arc::new(nzb::parse(&decoded).context(unavailable("original NZB is invalid"))?);
            // Covers compressed/XML buffers, parsed NZB strings and the pending stored metadata.
            let limits = Limits {
                requests: 100_000,
                decoded_bytes,
                memory_bytes,
                comparisons: 10_000_000,
                retained_bytes,
            };
            let (reader, limits, _) = runtime
                .block_on(repair::discover_with_context(
                    &client_for_discovery,
                    &nzb,
                    limits,
                    discovery_context,
                    &stopped,
                ))
                .map_err(classify)?;
            Ok((nzb, Arc::new(reader), limits))
        })
        .await??;
        let mut hash_only = false;
        let mut receipts = Vec::new();
        let mut stored_bytes = 0;
        for set in reader.sets() {
            progress.phase("Verifying source files and reconstructing damage");
            let result = repair::execute(
                client,
                Arc::clone(&nzb),
                Arc::clone(&reader),
                set,
                limits,
                false,
                context.clone(),
                cancel,
            )
            .await
            .map_err(classify)?;
            limits = result.remaining;
            hash_only |= result
                .sources
                .iter()
                .any(|source| source.proof.file_length > 0 && source.proof.slice_md5.is_empty());
            progress
                .verified
                .fetch_add(result.verified_bytes, Ordering::Relaxed);
            if let Some(metadata) = &mut context.metadata {
                let metadata = Arc::get_mut(metadata)
                    .context(unavailable("metadata snapshot is still in use"))?;
                metadata_changed |=
                    repair::refresh_metadata(metadata, &result.sources, &mut covered);
            }
            if !result.patches.is_empty() {
                anyhow::ensure!(
                    receipts.len() + result.patches.len() <= MAX_PATCHES,
                    unavailable(format!("more than {MAX_PATCHES} repaired articles"))
                );
                progress.phase("Saving repaired articles");
                stored_bytes += result
                    .patches
                    .iter()
                    .map(|(_, bytes)| bytes.len() as u64)
                    .sum::<u64>();
                // Durable but unpublished: receipts keep these files from eviction.
                receipts.extend(
                    self.stage(result.patches, progress.store_bytes, cancel)
                        .await?,
                );
            }
        }
        anyhow::ensure!(
            self.db.repair_source(owner).await?.as_ref() == Some(&source),
            unavailable("source changed during verification")
        );
        if context.metadata.is_some() {
            anyhow::ensure!(
                !covered.is_empty() && covered.iter().all(|covered| *covered),
                unavailable("PAR2 data does not cover every part of this file")
            );
        }
        let metadata = match context.metadata.as_deref() {
            Some(metadata) if metadata_changed => Some(blob::encode_meta(metadata)?),
            _ => None,
        };
        let repaired = !receipts.is_empty();
        if repaired || metadata.is_some() {
            anyhow::ensure!(
                !cancel.is_cancelled(),
                "repair cancelled before publication"
            );
            progress.phase("Publishing repaired articles");
            anyhow::ensure!(
                publisher
                    .publish(source.clone(), receipts, metadata, cancel)
                    .await?,
                unavailable("source changed before publication")
            );
            progress.stored.fetch_add(stored_bytes, Ordering::Relaxed);
        }
        // The repair stands either way; waiting work then runs at its scheduled time.
        if let Err(error) = self.db.wake_par2(source.nzb(), now_ms()).await {
            tracing::warn!(reason = %error, "could not wake work waiting for this repair");
        }
        Ok(if repaired {
            Outcome::Repaired { hash_only }
        } else {
            Outcome::Verified { hash_only }
        })
    }

    /// Write one set's articles, evicting least recently read repairs for room.
    pub(super) async fn stage(
        self: &Arc<Self>,
        patches: Vec<(Patch, Vec<u8>)>,
        capacity: u64,
        cancel: &CancellationToken,
    ) -> Result<Vec<patch::Durable>> {
        let staging = Arc::clone(&self.lifecycle).lock_owned().await;
        // A retained older database may still read any stored repair, so keep them all.
        let retained = self.retains_old_database().await?;
        let full = move || -> anyhow::Error {
            unavailable(if retained {
                "the repair store is full, and a retained older database still uses its repairs"
            } else {
                "repaired articles exceed the repair store"
            })
            .into()
        };
        let repairs = Arc::clone(self);
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        tokio::task::spawn_blocking(move || -> Result<_> {
            let _staging = staging;
            let store = repairs.store(capacity)?;
            let needed = patches
                .iter()
                .map(|(_, bytes)| bytes.len() as u64 + HEADER_RESERVE)
                .sum();
            match store.evict(
                needed,
                |id| {
                    if retained {
                        Ok(None)
                    } else {
                        repairs.evict(&runtime, id)
                    }
                },
                || stopped.is_cancelled(),
            ) {
                Err(patch::Error::Capacity) => return Err(full()),
                result => result?,
            };
            patches
                .into_iter()
                .map(|(patch, bytes)| {
                    store
                        .write(&patch, &bytes, || stopped.is_cancelled())
                        .map_err(|error| match error {
                            patch::Error::Capacity => full(),
                            error => error.into(),
                        })
                })
                .collect()
        })
        .await?
    }

    /// Blocking: drop one stored repair's reference. Readers finish before its unlink.
    fn evict(
        &self,
        runtime: &tokio::runtime::Handle,
        id: PatchId,
    ) -> Result<Option<OwnedSemaphorePermit>, patch::Error> {
        runtime.block_on(async {
            let items = self
                .db
                .evict_patch(id)
                .await
                .map_err(|_| patch::Error::Integrity("repair references cannot be updated"))?;
            for (item, path) in items {
                tracing::info!(item = %item.0, path, "evicted a stored repair to make room; the file will be checked again");
            }
            Arc::clone(&self.readers)
                .acquire_many_owned(u32::try_from(READERS).expect("32 readers fit u32"))
                .await
                .map(Some)
                .map_err(|_| patch::Error::Recovery)
        })
    }
}

fn classify(error: repair::Error) -> anyhow::Error {
    match error {
        repair::Error::Source(_)
        | repair::Error::Changed(_)
        | repair::Error::Worker(_)
        | repair::Error::Cancelled => anyhow::Error::new(error),
        error => unavailable(error.to_string()).into(),
    }
}
