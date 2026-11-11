//! One durable worker. Source reads and CPU/storage admission are independent of playback.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use anyhow::{Context as _, Result};
use infinidysk_core::{BlobId, blob};
use infinidysk_db::{now_ms, repair_jobs::Job};
use infinidysk_health::Verification;
use infinidysk_nntp::Client;
use infinidysk_queue::{
    nzb,
    repair::{self, Limits},
};
use tokio_util::sync::CancellationToken;

use super::{Publisher, Repairs};

const MEMORY: usize = 256_000_000;
const TRANSFER: u64 = 100_000_000_000;
const CAPACITY: u64 = 10_000_000_000;
const HASH_ONLY: &str =
    "Whole-file hashes matched. Some files have no slice checksums for locating damage.";

#[derive(Default)]
pub(super) struct Progress {
    watch: Arc<infinidysk_core::wait::Watch>,
    stored: AtomicU64,
    verified: AtomicU64,
}

impl Progress {
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
        cancel: CancellationToken,
    ) {
        while let Err(error) = self.db.resume_par2(now_ms()).await {
            tracing::warn!(reason = %error, "repair requests could not be resumed");
            tokio::select! { () = cancel.cancelled() => return, () = tokio::time::sleep(Duration::from_secs(5)) => {} }
        }
        let mut cleaned = None;
        loop {
            if cancel.is_cancelled() {
                return;
            }
            if let Err(error) = super::scan::step(&self.db).await {
                tracing::warn!(reason = %error, "library verification scan will retry later");
            }
            let result = self.db.claim_par2(now_ms()).await;
            match result {
                Ok(Some(job)) => {
                    let stopped = cancel.child_token();
                    let progress = Progress::default();
                    let _stop = stopped.clone().drop_guard();
                    let work = tokio::time::timeout(
                        Duration::from_secs(3600),
                        self.perform(&client, &publisher, &health, &job, &stopped, &progress),
                    );
                    tokio::pin!(work);
                    let result = tokio::select! {
                        result = &mut work => result,
                        () = async {
                            loop {
                                tokio::select! { () = cancel.cancelled() => break, () = tokio::time::sleep(Duration::from_secs(1)) => {} }
                                if self.db.par2_cancelled(job.id).await.unwrap_or(false) { break; }
                            }
                        } => { stopped.cancel(); work.await },
                    };
                    let (state, reason, retry) = match result {
                        Ok(Ok((state, reason))) => (state, reason.to_owned(), None),
                        other => {
                            let reason = match other {
                                Ok(Err(error)) => error.to_string(),
                                Err(_) => "repair time limit reached".to_owned(),
                                Ok(Ok(_)) => unreachable!(),
                            };
                            // Resource/metadata/content failures are terminal; provider/storage failures retry.
                            let transient =
                                !reason.starts_with("repair unavailable:") && job.attempts < 3;
                            tracing::warn!(job = %job.id, reason = %reason, "repair did not finish");
                            (
                                if transient { 4 } else { 5 },
                                reason,
                                transient.then(|| now_ms() + 60_000 * i64::from(job.attempts)),
                            )
                        }
                    };
                    let transferred = job.transferred_bytes.saturating_add(
                        i64::try_from(
                            progress
                                .watch
                                .supplied()
                                .iter()
                                .map(|s| s.bytes)
                                .sum::<u64>(),
                        )
                        .unwrap_or(i64::MAX),
                    );
                    let stored = job.stored_bytes.saturating_add(
                        i64::try_from(progress.stored.load(Ordering::Relaxed)).unwrap_or(i64::MAX),
                    );
                    let verified = if matches!(state, 2 | 3) {
                        i64::try_from(progress.verified.load(Ordering::Relaxed)).unwrap_or(i64::MAX)
                    } else {
                        0
                    };
                    loop {
                        let saved = async {
                            self.db.set_par2_verified(job.id, verified).await?;
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
                }
                Ok(None) => {
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
                        if let Err(error) = publisher.cleanup(&cancel).await {
                            tracing::warn!(reason = %error, "unused repair files could not be cleaned up");
                        }
                        cleaned = Some(Instant::now());
                    }
                }
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
        let legacy = self
            .db
            .config()
            .await?
            .iter()
            .any(|(key, value)| key == "legacy.repair.enabled" && value == "1");
        if let Some(id) = job.item_id {
            let owner = infinidysk_db::repair::Owner::Item(infinidysk_core::ItemId(id));
            anyhow::ensure!(
                self.db
                    .par2_result(owner, true)
                    .await?
                    .is_some_and(|current| current.id == job.id),
                "repair unavailable: verification source changed"
            );
            match health
                .verify_existing_with_probe(
                    infinidysk_core::ItemId(id),
                    cancel,
                    Some(progress.probe()),
                )
                .await?
            {
                Verification::Verified { bytes, slices } => {
                    progress.verified.store(bytes, Ordering::Relaxed);
                    return Ok((
                        2,
                        if bytes != 0 && slices == 0 {
                            HASH_ONLY
                        } else {
                            ""
                        },
                    ));
                }
                Verification::Unavailable(_) if job.nzb_blob.is_some() => {}
                Verification::Source(
                    infinidysk_nntp::Error::NotFound
                    | infinidysk_nntp::Error::Yenc(_)
                    | infinidysk_nntp::Error::CrcMismatch { .. },
                ) if legacy && job.nzb_blob.is_some() => {}
                Verification::Source(error) => return Err(anyhow::Error::new(error)),
                result => anyhow::bail!("repair unavailable: {result:?}"),
            }
        }
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
            Ok(CAPACITY.saturating_sub(repairs.store(CAPACITY)?.used_bytes()?))
        })
        .await??;
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        let discovery_context = context.clone();
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
                decoded_bytes: TRANSFER,
                memory_bytes: MEMORY - 96_000_000,
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
            let result = repair::execute(
                client,
                Arc::clone(&nzb),
                Arc::clone(&reader),
                set,
                limits,
                job.verify_only,
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
                    let store = repairs.store(CAPACITY)?;
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
        if job.verify_only || metadata.is_some() {
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
