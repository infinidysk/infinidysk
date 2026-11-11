//! The commit boundary owns background admission, reader fences and finalization.

use std::sync::Arc;
#[cfg(target_os = "linux")]
use std::time::Duration;

use anyhow::{Context as _, Result};
use infinidysk_db::repair::{MAX_PATCHES, Source};
use infinidysk_nntp::patch::Durable;
use infinidysk_vfs::{Vfs, WriteLease};

use super::Repairs;

#[derive(Clone)]
pub struct Publisher {
    repairs: Arc<Repairs>,
    vfs: Arc<Vfs>,
    #[cfg(target_os = "linux")]
    mount: Option<Arc<infinidysk_fuse::Invalidator>>,
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
    pub fn with_mount(mut self, mount: Arc<infinidysk_fuse::Invalidator>) -> Self {
        self.mount = Some(mount);
        self
    }

    /// Caller verifies complete protected sources with PAR2 before issuing receipts.
    /// Once accepted, cancellation of this caller does not cancel COMMIT/finalization.
    pub async fn publish(&self, source: Source, receipts: Vec<Durable>) -> Result<bool> {
        anyhow::ensure!(
            !receipts.is_empty() && receipts.len() <= MAX_PATCHES,
            "invalid repair batch size"
        );
        let publisher = self.clone();
        tokio::spawn(async move {
            let result = publisher.complete(source, receipts, None).await;
            if let Err(error) = &result {
                tracing::warn!(reason = %error, "repair publication did not finish");
            }
            result
        })
        .await
        .context("repair publication task stopped unexpectedly")?
    }

    /// Proof/layout upgrades retire old delivery buffers even when article bytes agree.
    pub async fn metadata(&self, source: Source, data: Vec<u8>) -> Result<bool> {
        let publisher = self.clone();
        tokio::spawn(async move { publisher.complete(source, Vec::new(), Some(data)).await })
            .await
            .context("repair metadata publication stopped unexpectedly")?
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
        let (patches, _receipts) = tokio::task::spawn_blocking(move || {
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
        let mut native = if let Some(mount) = &self.mount {
            Some(mount.prepare(&mut writers)?)
        } else {
            for writer in &mut writers {
                writer.block();
            }
            None
        };
        #[cfg(target_os = "linux")]
        if let Some(native) = &mut native {
            native.block();
        }
        #[cfg(not(target_os = "linux"))]
        for writer in &mut writers {
            writer.block();
        }

        let committed = if let Some(metadata) = metadata {
            self.repairs
                .db
                .publish_repair_metadata(&source, &metadata)
                .await
        } else {
            self.repairs.db.publish_patches(&source, &patches).await
        };
        // An uncertain COMMIT may be visible. Reconcile delivery even on SQL errors.
        #[cfg(target_os = "linux")]
        match native {
            Some(native) => {
                let mut result = native.apply().await;
                let mut warned = false;
                while let Err(error) = result {
                    if !warned {
                        tracing::warn!(reason = %error, "repair publication is waiting for FUSE invalidation");
                        warned = true;
                    }
                    tokio::time::sleep(Duration::from_millis(250)).await;
                    let mount = self.mount.as_ref().expect("native plan requires a mount");
                    result = match mount.prepare(&mut writers) {
                        Ok(plan) => plan.apply().await,
                        Err(error) => Err(error),
                    };
                }
            }
            None => acknowledge(&mut writers)?,
        }
        #[cfg(not(target_os = "linux"))]
        acknowledge(&mut writers)?;
        // Wake affected new opens only after every old delivery has retired.
        drop(writers);
        drop(publication);
        drop(paused);
        committed.context("repair references could not be published")
    }
}

fn acknowledge(writers: &mut [WriteLease]) -> Result<()> {
    for writer in writers.iter_mut() {
        writer.retire()?;
    }
    for writer in writers {
        writer.acknowledge();
    }
    Ok(())
}
