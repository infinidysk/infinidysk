//! The commit boundary owns background admission, reader fences and finalization.

use std::sync::Arc;
#[cfg(target_os = "linux")]
use std::time::Duration;

use anyhow::{Context as _, Result};
use infinidysk_core::repair::Patch;
use infinidysk_db::repair::{MAX_PATCHES, Source};
use infinidysk_nntp::patch::Durable;
use infinidysk_vfs::{Vfs, WriteLease};

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
    /// Once accepted, cancellation of this caller does not cancel COMMIT/finalization.
    pub async fn publish(
        &self,
        source: Source,
        receipts: Vec<Durable>,
        metadata: Option<Vec<u8>>,
    ) -> Result<bool> {
        anyhow::ensure!(
            receipts.len() <= MAX_PATCHES && (!receipts.is_empty() || metadata.is_some()),
            "invalid repair batch size"
        );
        let publisher = self.clone();
        tokio::spawn(async move {
            let result = publisher.complete(source, receipts, metadata).await;
            if let Err(error) = &result {
                tracing::warn!(reason = %error, "repair publication did not finish");
            }
            result
        })
        .await
        .context("repair publication task stopped unexpectedly")?
    }

    #[allow(clippy::significant_drop_tightening)] // Native plans span COMMIT and are consumed by finalization.
    async fn complete(
        &self,
        source: Source,
        receipts: Vec<Durable>,
        metadata: Option<Vec<u8>>,
    ) -> Result<bool> {
        let _lifecycle = self.repairs.lifecycle.lock().await;
        let repairs = Arc::clone(&self.repairs);
        let permit = Arc::clone(&repairs.readers).acquire_owned().await?;
        let (patches, receipts) = tokio::task::spawn_blocking(move || {
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
        .context("repair file verification stopped")??;

        // Drain old health/import observations before taking any item writers.
        let paused = self.repairs.db.pause_writes().await;
        let publication = self.vfs.begin_publication(source.nzb(), &patches)?;
        let mut writers = publication.writers().await;
        #[cfg(target_os = "linux")]
        if let Some(mount) = &self.mount {
            let mut native = mount.prepare(&mut writers)?;
            if let Err(error) = native.block().await {
                native.abort();
                return Err(error).context("repair publication could not close delivery");
            }
            let committed = self
                .commit(receipts, &source, &patches, metadata.as_deref())
                .await;
            // Writes resume once SQL settles; delivery stays fenced until invalidation.
            drop(paused);
            // An uncertain COMMIT may be visible. Reconcile delivery even on SQL errors.
            let mut result = native.apply().await;
            let mut warned = false;
            while let Err(error) = result {
                if !warned {
                    tracing::warn!(reason = %error, "repair publication is waiting for FUSE invalidation");
                    warned = true;
                }
                tokio::time::sleep(Duration::from_millis(250)).await;
                result = match mount.prepare(&mut writers) {
                    Ok(plan) => plan.apply().await,
                    Err(error) => Err(error),
                };
            }
            // Wake affected new opens only after every old delivery has retired.
            drop(writers);
            drop(publication);
            return committed;
        }
        let armed = WriteLease::arm(&mut writers);
        let committed = self
            .commit(receipts, &source, &patches, metadata.as_deref())
            .await;
        drop(paused);
        armed.retire()?.acknowledge_after(async { Ok(()) }).await?;
        drop(writers);
        drop(publication);
        committed
    }

    /// Receipts keep staged files from eviction until the patch rows commit.
    async fn commit(
        &self,
        receipts: Vec<Durable>,
        source: &Source,
        patches: &[Patch],
        metadata: Option<&[u8]>,
    ) -> Result<bool> {
        let db = &self.repairs.db;
        let mut committed = true;
        if !patches.is_empty() {
            committed = db
                .publish_patches(receipts, source, patches)
                .await
                .context("repair references could not be published")?;
        }
        if committed && let Some(metadata) = metadata {
            committed = db
                .publish_repair_metadata(source, metadata, None)
                .await
                .context("repaired file metadata could not be published")?;
        }
        Ok(committed)
    }
}
