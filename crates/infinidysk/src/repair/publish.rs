//! The commit boundary owns background admission, reader fences and finalization.

use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context as _, Result};
use infinidysk_core::repair::Patch;
use infinidysk_db::repair::{MAX_PATCHES, Source};
use infinidysk_nntp::patch::Durable;
use infinidysk_vfs::{Vfs, WriteLease};
use tokio::sync::oneshot;
use tokio_util::sync::CancellationToken;

use super::Repairs;

#[derive(Clone)]
pub struct Publisher {
    repairs: Arc<Repairs>,
    vfs: Arc<Vfs>,
    #[cfg(target_os = "linux")]
    mount: Option<Arc<infinidysk_mount::Invalidator>>,
}

impl Publisher {
    pub(super) async fn cleanup(
        &self,
        cancel: &tokio_util::sync::CancellationToken,
    ) -> Result<u64> {
        self.repairs.cleanup(&self.vfs, cancel).await
    }
    /// Both services must use clones of the same database owner.
    pub fn new(repairs: Arc<Repairs>, vfs: Arc<Vfs>) -> Result<Self> {
        anyhow::ensure!(
            vfs.uses_database(&repairs.db),
            "repair and delivery database owners differ"
        );
        Ok(Self {
            repairs,
            vfs,
            #[cfg(target_os = "linux")]
            mount: None,
        })
    }

    #[cfg(target_os = "linux")]
    #[must_use]
    pub fn with_mount(mut self, mount: Arc<infinidysk_mount::Invalidator>) -> Self {
        self.mount = Some(mount);
        self
    }

    /// Publish one job's repaired articles and refreshed metadata under one fence.
    /// Caller verifies complete protected sources with PAR2 before issuing receipts.
    /// `stopped` refuses publication only until COMMIT starts. After that, neither it
    /// nor cancellation of this caller cancels COMMIT, and the SQL result is returned.
    pub async fn publish(
        &self,
        source: Source,
        receipts: Vec<Durable>,
        metadata: Option<Vec<u8>>,
        stopped: &CancellationToken,
    ) -> Result<bool> {
        anyhow::ensure!(
            receipts.len() <= MAX_PATCHES && (!receipts.is_empty() || metadata.is_some()),
            "invalid repair batch size"
        );
        let (publisher, stopped) = (self.clone(), stopped.clone());
        let (settle, settled) = oneshot::channel();
        tokio::spawn(async move {
            let mut settle = Some(settle);
            let result = publisher
                .complete(source, receipts, metadata, &stopped, &mut settle)
                .await;
            if let Err(error) = &result {
                tracing::warn!(reason = %error, "repair publication did not finish");
            }
            if let Some(settle) = settle {
                // The caller may have stopped waiting; the publication stands either way.
                let _ = settle.send(result);
            }
        });
        settled
            .await
            .context("repair publication task stopped unexpectedly")?
    }

    #[allow(clippy::significant_drop_tightening)] // Native plans span COMMIT and are consumed by finalization.
    async fn complete(
        &self,
        source: Source,
        receipts: Vec<Durable>,
        metadata: Option<Vec<u8>>,
        stopped: &CancellationToken,
        // Only native invalidation can report before finalization ends.
        #[cfg_attr(not(target_os = "linux"), allow(unused_variables))] settle: &mut Option<
            oneshot::Sender<Result<bool>>,
        >,
    ) -> Result<bool> {
        let mut lifecycle = tokio::select! {
            lifecycle = self.repairs.lifecycle.lock() => lifecycle,
            () = stopped.cancelled() => anyhow::bail!("repair stopped before publication"),
        };
        let repairs = Arc::clone(&self.repairs);
        let (patches, receipts) = if receipts.is_empty() {
            (Vec::new(), receipts)
        } else {
            let permit = Arc::clone(&repairs.readers).acquire_owned().await?;
            tokio::task::spawn_blocking(move || {
                let _permit = permit;
                let store = repairs.store(u64::MAX)?;
                let mut patches = Vec::with_capacity(receipts.len());
                for receipt in &receipts {
                    store.read(receipt.patch())?;
                    patches.push(receipt.patch().clone());
                }
                Ok::<_, infinidysk_nntp::patch::Error>((patches, receipts))
            })
            .await
            .context("repair file verification stopped")??
        };
        anyhow::ensure!(!stopped.is_cancelled(), "repair stopped before publication");

        let refused = || anyhow::anyhow!("repair stopped before publication");
        let (paused, publication) = loop {
            // Drain old health/import observations before taking any item writers.
            let paused = tokio::select! {
                paused = self.repairs.db.pause_writes() => paused,
                () = stopped.cancelled() => return Err(refused()),
            };
            match self.vfs.begin_publication(source.nzb(), &patches) {
                Ok(publication) => break (paused, publication),
                // An earlier publication still waiting for FUSE invalidation holds the slot.
                // Wait without the write pause or the lifecycle lock; this repair has not
                // failed, and receipts keep its verified files from eviction meanwhile.
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    drop(paused);
                    drop(lifecycle);
                    tokio::select! {
                        () = stopped.cancelled() => return Err(refused()),
                        () = tokio::time::sleep(Duration::from_millis(250)) => {}
                    }
                    lifecycle = tokio::select! {
                        lifecycle = self.repairs.lifecycle.lock() => lifecycle,
                        () = stopped.cancelled() => return Err(refused()),
                    };
                }
                Err(error) => return Err(error.into()),
            }
        };
        let mut writers = tokio::select! {
            writers = publication.writers() => writers,
            () = stopped.cancelled() => return Err(refused()),
        };
        #[cfg(target_os = "linux")]
        if let Some(mount) = &self.mount {
            let mut native = mount.prepare(&mut writers)?;
            let blocked = tokio::select! {
                result = native.block() => result,
                () = stopped.cancelled() => {
                    native.abort();
                    return Err(refused());
                }
            };
            if let Err(error) = blocked {
                native.abort();
                return Err(error).context("repair publication could not close delivery");
            }
            // The last point that may refuse. From here on the caller observes the result.
            if stopped.is_cancelled() {
                native.abort();
                return Err(refused());
            }
            let committed = self
                .commit(receipts, &source, &patches, metadata.as_deref())
                .await;
            // Writes resume once SQL settles; delivery stays fenced until invalidation.
            drop(paused);
            // Other publications, eviction and backups do not wait for invalidation.
            drop(lifecycle);
            // An uncertain COMMIT may be visible. Reconcile delivery even on SQL errors.
            let mut committed = Some(committed);
            let mut result = native.apply().await;
            if let Err(error) = &result {
                tracing::warn!(reason = %error, "repair publication is waiting for FUSE invalidation");
                // Report the SQL result now. Only these items' writers wait for a session,
                // so their old bytes stay fenced; shutdown drops this task and them.
                if let (Some(settle), Some(committed)) = (settle.take(), committed.take()) {
                    let _ = settle.send(committed);
                }
            }
            while result.is_err() {
                tokio::time::sleep(Duration::from_millis(250)).await;
                result = match mount.prepare(&mut writers) {
                    Ok(plan) => plan.apply().await,
                    Err(error) => Err(error),
                };
            }
            // Wake affected new opens only after every old delivery has retired.
            drop(writers);
            drop(publication);
            // `None`: the result was already reported above.
            return committed.unwrap_or(Ok(true));
        }
        // The last point that may refuse. From here on the caller observes the result.
        anyhow::ensure!(!stopped.is_cancelled(), "repair stopped before publication");
        let armed = WriteLease::arm(&mut writers);
        let committed = self
            .commit(receipts, &source, &patches, metadata.as_deref())
            .await;
        drop(paused);
        drop(lifecycle);
        armed.retire()?.acknowledge_after(async { Ok(()) }).await?;
        drop(writers);
        drop(publication);
        committed
    }

    /// Receipts keep staged files from eviction until the transaction settles.
    /// References and metadata commit together, so neither is visible alone.
    async fn commit(
        &self,
        receipts: Vec<Durable>,
        source: &Source,
        patches: &[Patch],
        metadata: Option<&[u8]>,
    ) -> Result<bool> {
        let db = &self.repairs.db;
        if !patches.is_empty() {
            let replacements = receipts
                .iter()
                .filter_map(|receipt| receipt.replaces().map(|old| (receipt.patch().id, old)))
                .collect();
            return db
                .publish_replacements(receipts, source, patches, metadata, replacements)
                .await
                .context("repair references could not be published");
        }
        let Some(metadata) = metadata else {
            return Ok(true);
        };
        db.publish_repair_metadata(source, metadata, None)
            .await
            .context("repaired file metadata could not be published")
    }
}
