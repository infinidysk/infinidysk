//! Separate durable availability and repair workers. Playback keeps priority.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result};
use infinidysk_core::{BlobId, blob};
use infinidysk_db::{now_ms, repair_jobs::Job};
use infinidysk_health::{Availability, AvailabilityOptions, AvailabilityProgress, Evidence};
use infinidysk_nntp::Client;
use infinidysk_queue::{
    nzb,
    repair::{self, Limits},
};
use tokio_util::sync::CancellationToken;

use super::{Publisher, Repairs};

const HASH_ONLY: &str =
    "Whole-file hashes matched. Some files have no slice checksums for locating damage.";

#[derive(Default)]
pub(super) struct Progress {
    limits: crate::config::Repair,
    phase: Mutex<&'static str>,
    watch: Arc<infinidysk_core::wait::Watch>,
    stored: AtomicU64,
    verified: AtomicU64,
    availability: Arc<AvailabilityProgress>,
}

impl Progress {
    pub(super) fn new(limits: crate::config::Repair) -> Self {
        Self {
            limits,
            ..Self::default()
        }
    }
    fn phase(&self, phase: &'static str) {
        *self.phase.lock().unwrap_or_else(PoisonError::into_inner) = phase;
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
}

impl Repairs {
    pub async fn run(
        self: Arc<Self>,
        client: Arc<Client>,
        publisher: Publisher,
        health: Arc<infinidysk_health::Health>,
        config: Arc<tokio::sync::RwLock<crate::config::ConfigStore>>,
        cancel: CancellationToken,
    ) {
        while let Err(error) = self.db.resume_par2(now_ms()).await {
            tracing::warn!(reason = %error, "repair requests could not be resumed");
            tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
        }
        tokio::join!(
            self.run_lane(&client, &publisher, &health, &config, &cancel, true),
            self.run_lane(&client, &publisher, &health, &config, &cancel, false),
        );
    }

    async fn run_lane(
        self: &Arc<Self>,
        client: &Arc<Client>,
        publisher: &Publisher,
        health: &Arc<infinidysk_health::Health>,
        config: &Arc<tokio::sync::RwLock<crate::config::ConfigStore>>,
        cancel: &CancellationToken,
        verify: bool,
    ) {
        let mut turn = 0usize;
        let mut cleaned = None;
        loop {
            if cancel.is_cancelled() {
                return;
            }
            if verify && health.playback_active() {
                tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_millis(250)) => {} }
                continue;
            }
            if verify && let Err(error) = super::scan::step(&self.db).await {
                tracing::warn!(reason = %error, "library verification scan will retry later");
            }
            let result = self
                .db
                .claim_par2_lane(now_ms(), verify, turn % 4 == 3)
                .await;
            match result {
                Ok(Some(job)) => {
                    turn = turn.wrapping_add(1);
                    let stopped = cancel.child_token();
                    let progress = Progress::new(config.read().await.settings().repair.clone());
                    let _stop = stopped.clone().drop_guard();
                    let work = tokio::time::timeout(
                        Duration::from_secs(u64::from(progress.limits.timeout_minutes) * 60),
                        self.perform(client, publisher, health, &job, &stopped, &progress),
                    );
                    tokio::pin!(work);
                    let result = tokio::select! {
                        result = &mut work => result,
                        () = async {
                            loop {
                                tokio::select! { () = cancel.cancelled() => break, () = tokio::time::sleep(Duration::from_secs(2)) => {} }
                                if self.db.par2_cancelled(job.id).await.unwrap_or(false) { break; }
                                if verify && let Err(error) = super::scan::step(&self.db).await {
                                    tracing::warn!(reason = %error, "library verification scan will retry later");
                                }
                                if let Err(error) = self.db.update_availability_progress(job.id,
                                    i64::try_from(progress.availability.checked.load(Ordering::Acquire)).unwrap_or(i64::MAX),
                                    i64::try_from(progress.availability.total.load(Ordering::Relaxed)).unwrap_or(i64::MAX)).await {
                                    tracing::warn!(reason = %error, "article progress could not be saved");
                                }
                                let phase = *progress.phase.lock().unwrap_or_else(PoisonError::into_inner);
                                let (transferred, stored) = progress.counters(&job);
                                if let Err(error) = self.db.update_par2_progress(job.id, phase, transferred, stored, now_ms()).await {
                                    tracing::warn!(job = %job.id, reason = %error, "repair progress could not be saved");
                                }
                            }
                        } => { stopped.cancel(); work.await },
                    };
                    let (state, reason, retry) = match result {
                        Ok(Ok((state, reason))) => (
                            state,
                            reason.to_owned(),
                            (state == 4).then(|| now_ms() + 1000),
                        ),
                        other => {
                            let mut reason = match other {
                                Ok(Err(error)) => error.to_string(),
                                Err(_) => "repair time limit reached".to_owned(),
                                Ok(Ok(_)) => unreachable!(),
                            };
                            // Resource/metadata/content failures are terminal; provider/storage failures retry.
                            let transient = !reason.starts_with("repair unavailable:")
                                && job.attempts < i32::from(progress.limits.attempts);
                            if !transient && !reason.starts_with("repair unavailable:") {
                                reason = format!("Retry limit reached: {reason}");
                            }
                            tracing::warn!(job = %job.id, reason = %reason, "repair did not finish");
                            (
                                if transient { 4 } else { 5 },
                                reason,
                                transient.then(|| now_ms() + 60_000 * i64::from(job.attempts)),
                            )
                        }
                    };
                    let (transferred, stored) = progress.counters(&job);
                    let verified = if matches!(state, 2 | 3) {
                        i64::try_from(progress.verified.load(Ordering::Relaxed)).unwrap_or(i64::MAX)
                    } else {
                        0
                    };
                    loop {
                        let saved = async {
                            self.db.set_par2_verified(job.id, verified).await?;
                            self.db
                                .update_availability_progress(
                                    job.id,
                                    i64::try_from(
                                        progress.availability.checked.load(Ordering::Acquire),
                                    )
                                    .unwrap_or(i64::MAX),
                                    i64::try_from(
                                        progress.availability.total.load(Ordering::Relaxed),
                                    )
                                    .unwrap_or(i64::MAX),
                                )
                                .await?;
                            self.db
                                .finish_par2(
                                    job.id,
                                    state,
                                    &reason,
                                    transferred,
                                    stored,
                                    now_ms(),
                                    retry,
                                )
                                .await
                        }
                        .await;
                        match saved {
                            Ok(()) => break,
                            Err(error) => {
                                tracing::warn!(job = %job.id, reason = %error, "repair result could not be saved; retrying");
                            }
                        }
                        tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
                    }
                    continue;
                }
                Ok(None) if !verify => {
                    let full_pass = super::scan::load(&self.db)
                        .await
                        .ok()
                        .flatten()
                        .is_some_and(|scan| scan.complete && !scan.cancelled)
                        && self.db.par2_pending().await.unwrap_or(1) == 0;
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

    pub(super) async fn perform(
        self: &Arc<Self>,
        client: &Arc<Client>,
        publisher: &Publisher,
        health: &infinidysk_health::Health,
        job: &Job,
        cancel: &CancellationToken,
        progress: &Progress,
    ) -> Result<(i16, &'static str)> {
        let capacity = u64::from(progress.limits.storage_gb) * 1_000_000_000;
        let transfer = u64::from(progress.limits.transfer_gb) * 1_000_000_000;
        let memory = usize::try_from(progress.limits.memory_mb)
            .unwrap_or(usize::MAX)
            .saturating_mul(1_000_000);
        if job.verify_only {
            let id = infinidysk_core::ItemId(
                job.item_id.context("repair unavailable: file is missing")?,
            );
            let owner = infinidysk_db::repair::Owner::Item(id);
            anyhow::ensure!(
                self.db
                    .par2_result(owner, true)
                    .await?
                    .is_some_and(|current| current.id == job.id),
                "repair unavailable: availability source changed"
            );
            let evidence = job.scan_at.map(|stamp| {
                let mut saved = self.evidence.lock().unwrap_or_else(PoisonError::into_inner);
                if saved.as_ref().is_none_or(|(old, _)| *old != stamp) {
                    *saved = Some((stamp, Arc::new(Evidence::default())));
                }
                Arc::clone(&saved.as_ref().expect("scan evidence installed above").1)
            });
            progress.phase("Checking article availability");
            let result = health
                .check_availability(
                    id,
                    cancel,
                    AvailabilityOptions {
                        concurrency: usize::from(progress.limits.check_concurrency),
                        memory_bytes: memory,
                        decoded_bytes: transfer,
                        evidence,
                        progress: Arc::clone(&progress.availability),
                    },
                )
                .await?;
            return match result {
                Availability::Available { .. } => Ok((
                    2,
                    "Articles are available. Body spot checks passed; full byte integrity was not checked.",
                )),
                Availability::Damaged { .. } if job.repair_on_failure && job.nzb_blob.is_some() => {
                    self.db
                        .update_availability_progress(
                            job.id,
                            i64::try_from(progress.availability.checked.load(Ordering::Acquire))
                                .unwrap_or(i64::MAX),
                            i64::try_from(progress.availability.total.load(Ordering::Relaxed))
                                .unwrap_or(i64::MAX),
                        )
                        .await?;
                    let (transferred, stored) = progress.counters(job);
                    self.db
                        .update_par2_progress(
                            job.id,
                            "Queuing repair",
                            transferred,
                            stored,
                            now_ms(),
                        )
                        .await?;
                    anyhow::ensure!(
                        self.db.escalate_par2(job.id, now_ms()).await?,
                        "repair unavailable: source changed before repair handoff"
                    );
                    Ok((
                        5,
                        "Missing or corrupt articles found; PAR2 repair queued separately.",
                    ))
                }
                Availability::Damaged { .. } => Ok((5, "Missing or corrupt articles found.")),
                Availability::Source(error) => Err(anyhow::Error::new(error)),
                Availability::Unavailable(reason) => anyhow::bail!("repair unavailable: {reason}"),
                Availability::Changed => {
                    anyhow::bail!("repair unavailable: availability source changed")
                }
                Availability::Cancelled => anyhow::bail!("repair cancelled"),
                Availability::Yielded => {
                    self.db.yield_par2_attempt(job.id).await?;
                    Ok((4, "Waiting for playback; availability check will resume."))
                }
            };
        }
        let legacy = self
            .db
            .config()
            .await?
            .iter()
            .any(|(key, value)| key == "legacy.repair.enabled" && value == "1");
        let nzb = job
            .nzb_blob
            .context("repair unavailable: original NZB is missing")?;
        let owner = if let Some(id) = job.item_id {
            infinidysk_db::repair::Owner::Item(infinidysk_core::ItemId(id))
        } else {
            self.db
                .par2_owner(BlobId(nzb))
                .await?
                .context("repair unavailable: source no longer owned")?
        };
        let source = self
            .db
            .repair_source(owner)
            .await?
            .context("repair unavailable: source disappeared")?;
        anyhow::ensure!(
            source.generation() == job.generation,
            "repair unavailable: original NZB changed"
        );
        let raw = self
            .db
            .repair_blob(source.nzb(), 16_000_000)
            .await?
            .context(
                "repair unavailable: original NZB is missing or exceeds the metadata allowance",
            )?;
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
        let context = repair::Context {
            source: Some((source.nzb(), source.generation())),
            candidates,
            probe: Some(progress.probe()),
        };
        let mut metadata = if let infinidysk_db::repair::Owner::Item(id) = owner {
            if let Some(item) = self.db.item(id).await? {
                if let Some(meta) = item.meta_blob {
                    self.db
                        .repair_blob(meta, 16_000_000)
                        .await?
                        .map(|raw| blob::decode_meta_limited(&raw, 16_000_000))
                        .transpose()?
                } else {
                    None
                }
            } else {
                None
            }
        } else {
            None
        };
        let mut metadata_changed = false;
        let mut covered = Vec::new();
        let client_for_discovery = Arc::clone(client);
        let repairs = Arc::clone(self);
        let checking = Arc::clone(&self.lifecycle).lock_owned().await;
        let remaining_storage = tokio::task::spawn_blocking(move || -> Result<u64> {
            let _checking = checking;
            Ok(capacity.saturating_sub(repairs.store(capacity)?.used_bytes()?))
        })
        .await??;
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        let discovery_context = context.clone();
        let decoded_bytes = transfer.saturating_sub(progress.transferred());
        let memory_bytes = memory.saturating_sub(96_000_000);
        progress.phase("Discovering PAR2 files");
        let (nzb, reader, mut limits) = tokio::task::spawn_blocking(move || -> Result<_> {
            let decoded = blob::decode_nzb_limited(&raw, 16_000_000).context(
                "repair unavailable: original NZB exceeds its allowance or is unreadable",
            )?;
            let nzb = Arc::new(
                nzb::parse(&decoded).context("repair unavailable: original NZB is invalid")?,
            );
            // Covers compressed/XML buffers, parsed NZB strings and the pending stored metadata.
            let limits = Limits {
                requests: 100_000,
                decoded_bytes,
                memory_bytes,
                comparisons: 10_000_000,
                retained_bytes: remaining_storage,
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
        let mut repaired = false;
        let mut hash_only = false;
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
            if let Some(metadata) = &mut metadata {
                metadata_changed |=
                    repair::refresh_metadata(metadata, &result.sources, &mut covered);
            }
            if !result.patches.is_empty() {
                progress.phase("Saving and publishing repaired articles");
                let staging = Arc::clone(&self.lifecycle).lock_owned().await;
                let repairs = Arc::clone(self);
                let stopped = cancel.clone();
                let patch_bytes = result
                    .patches
                    .iter()
                    .map(|(_, bytes)| bytes.len() as u64)
                    .sum::<u64>();
                let receipts = tokio::task::spawn_blocking(move || -> Result<_> {
                    let _staging = staging;
                    let store = repairs.store(capacity)?;
                    result
                        .patches
                        .into_iter()
                        .map(|(patch, bytes)| {
                            store
                                .write(&patch, &bytes, || stopped.is_cancelled())
                                .map_err(Into::into)
                        })
                        .collect::<Result<Vec<_>>>()
                })
                .await??;
                anyhow::ensure!(
                    !cancel.is_cancelled(),
                    "repair cancelled before publication"
                );
                anyhow::ensure!(
                    publisher.publish(source.clone(), receipts).await?,
                    "repair unavailable: source changed before publication"
                );
                repaired = true;
                progress.stored.fetch_add(patch_bytes, Ordering::Relaxed);
            }
        }
        anyhow::ensure!(
            self.db.repair_source(owner).await?.as_ref() == Some(&source),
            "repair unavailable: source changed during verification"
        );
        if metadata.is_some() {
            anyhow::ensure!(
                !covered.is_empty() && covered.iter().all(|covered| *covered),
                "repair unavailable: PAR2 data does not cover every part of this file"
            );
        }
        if metadata_changed && let Some(metadata) = metadata {
            anyhow::ensure!(
                publisher
                    .metadata(source.clone(), blob::encode_meta(&metadata)?)
                    .await?,
                "repair unavailable: source changed before metadata publication"
            );
        }
        self.db.wake_par2(source.nzb(), now_ms()).await?;
        if legacy {
            self.db.retire_legacy_repairs().await?;
        }
        Ok((
            if repaired { 3 } else { 2 },
            if hash_only { HASH_ONLY } else { "" },
        ))
    }
}

fn classify(error: repair::Error) -> anyhow::Error {
    match error {
        repair::Error::Source(_)
        | repair::Error::Changed(_)
        | repair::Error::Worker(_)
        | repair::Error::Cancelled => anyhow::Error::new(error),
        error => anyhow::anyhow!("repair unavailable: {error}"),
    }
}
