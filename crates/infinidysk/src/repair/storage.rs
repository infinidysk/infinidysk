//! Standalone SQLite backups carry verified patch bytes; the live store stays separate.

use std::path::Path;
use std::sync::Arc;

use anyhow::{Context as _, Result};
use infinidysk_db::Db;
use infinidysk_vfs::Vfs;
use tokio_util::sync::CancellationToken;

use super::{READERS, Repairs};

impl Repairs {
    pub async fn backup_into(
        self: &Arc<Self>,
        path: &Path,
        cancel: &CancellationToken,
    ) -> Result<()> {
        let _lifecycle = self.lifecycle.lock().await;
        let stopped = cancel.clone();
        self.db
            .backup_into(path, move || stopped.is_cancelled())
            .await?;
        let snapshot = Db::connect(&format!("sqlite:{}", path.display())).await?;
        let result = async {
            let mut after = None;
            loop {
                let patches = snapshot.repair_files(after).await?;
                if patches.is_empty() {
                    break;
                }
                for patch in patches {
                    anyhow::ensure!(!cancel.is_cancelled(), "backup cancelled");
                    after = Some(patch.id);
                    let repairs = Arc::clone(self);
                    let (patch, data) = tokio::task::spawn_blocking(move || -> Result<_> {
                        let body = repairs.store(u64::MAX)?.read(&patch)?;
                        Ok((patch, body.data))
                    })
                    .await??;
                    snapshot.set_repair_payload(&patch, Some(&data)).await?;
                }
            }
            Ok::<_, anyhow::Error>(())
        }
        .await;
        snapshot.close().await;
        result?;
        let path = path.to_owned();
        tokio::task::spawn_blocking(move || std::fs::File::open(path)?.sync_all()).await??;
        Ok(())
    }

    /// Called before delivery starts. Keep embedded bytes until the store is durable.
    pub async fn restore_embedded(self: &Arc<Self>) -> Result<()> {
        let mut after = None;
        let mut damaged = false;
        loop {
            let patches = self.db.repair_files(after).await?;
            if patches.is_empty() {
                break;
            }
            for patch in patches {
                after = Some(patch.id);
                let data = self.db.repair_payload(patch.id).await?;
                let embedded = data.is_some();
                let repairs = Arc::clone(self);
                let result = tokio::task::spawn_blocking(move || -> Result<_> {
                    let store = repairs.store(u64::MAX)?;
                    if let Some(data) = data {
                        store.restore(&patch, &data, || false)?;
                    }
                    store.read(&patch)?;
                    Ok(patch)
                })
                .await?;
                match result {
                    Ok(patch) if embedded => self.db.set_repair_payload(&patch, None).await?,
                    Ok(_) => {}
                    Err(error) => {
                        tracing::warn!(reason = %error, "a saved repair is missing or corrupt; affected files need a check");
                        damaged = true;
                    }
                }
            }
        }
        if damaged {
            self.db.recheck_missing_repairs().await?;
        }
        Ok(())
    }

    pub async fn cleanup(self: &Arc<Self>, vfs: &Vfs, cancel: &CancellationToken) -> Result<u64> {
        let _lifecycle = self.lifecycle.lock().await;
        let _paused = self.db.pause_writes().await;
        // The retained SQLite database shares the repair directory after a cutover.
        if !self.path.try_exists()?
            || self
                .path
                .with_file_name(crate::database::PRE_POSTGRES)
                .try_exists()?
        {
            return Ok(0);
        }
        if let Some(stats) = self.db.sqlite_stats().await? {
            let undo = std::path::PathBuf::from(format!("{}.before-restore", stats.path.display()));
            // The old live database pins its patches while undo remains available.
            if undo.try_exists()? {
                return Ok(0);
            }
        }
        let (nzbs, articles) = vfs.repair_pins();
        // Reconcile uncertain commits before taking the authoritative keep snapshot.
        self.db.drain_repair_writes().await?;
        let mut after = None;
        loop {
            let patches = self.db.repair_files(after).await?;
            if patches.is_empty() {
                break;
            }
            for patch in patches {
                anyhow::ensure!(!cancel.is_cancelled(), "cleanup cancelled");
                after = Some(patch.id);
                // File/reader identities can outlive SQL owners and cache eviction.
                let source = self.db.patch_nzb(patch.id).await?;
                let pinned = source.is_some_and(|source| nzbs.contains(&source))
                    || patch
                        .source
                        .message_id
                        .as_ref()
                        .is_some_and(|id| articles.contains(id));
                if !pinned {
                    self.db.remove_unused_patch(patch.id).await?;
                }
            }
        }
        let repairs = Arc::clone(self);
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        tokio::task::spawn_blocking(move || {
            repairs.store(u64::MAX)?.prune(
                |id| {
                    runtime.block_on(async {
                        use infinidysk_nntp::patch::Error;
                        let source =
                            repairs.db.patch_nzb(id).await.map_err(|_| {
                                Error::Integrity("repair references cannot be read")
                            })?;
                        if source.is_some() {
                            return Ok(None);
                        }
                        // A lookup may already hold a descriptor whose SQL owner was just deleted.
                        Arc::clone(&repairs.readers)
                            .acquire_many_owned(u32::try_from(READERS).expect("32 readers fit u32"))
                            .await
                            .map(Some)
                            .map_err(|_| Error::Recovery)
                    })
                },
                || stopped.is_cancelled(),
            )
        })
        .await
        .context("repair cleanup stopped")?
        .map_err(Into::into)
    }
}
