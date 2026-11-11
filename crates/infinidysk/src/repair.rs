//! Read and publish durable repairs. Installing the reader does not start repair jobs.

pub mod api;
mod publish;
mod scan;
mod storage;
mod worker;
pub use publish::Publisher;

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use infinidysk_core::repair::Patch;
use infinidysk_db::Db;
use infinidysk_nntp::{Body, Client, Error, RepairKey, patch::Store};
use tokio::sync::Semaphore;

const READERS: usize = 32;

pub struct Repairs {
    db: Db,
    path: PathBuf,
    store: Mutex<Option<Store>>,
    readers: Arc<Semaphore>,
    lifecycle: Arc<tokio::sync::Mutex<()>>,
}

/// Install once per client; publishers must retain this same service/store owner.
pub fn install(client: &Client, db: Db, path: PathBuf) -> Arc<Repairs> {
    let repairs = Arc::new(Repairs {
        db,
        path,
        store: Mutex::new(None),
        readers: Arc::new(Semaphore::new(READERS)),
        lifecycle: Arc::new(tokio::sync::Mutex::new(())),
    });
    client.set_repairs(Box::new({
        let repairs = Arc::clone(&repairs);
        move |id| {
            if !repairs.db.may_have_repairs() {
                return None;
            }
            let repairs = Arc::clone(&repairs);
            Some(Box::pin(async move { repairs.read(id).await }))
        }
    }));
    repairs
}

fn same_body(a: &Patch, b: &Patch) -> bool {
    a.sha256 == b.sha256
        && a.source.message_id == b.source.message_id
        && a.source.range == b.source.range
        && a.source.file_length == b.source.file_length
        && a.source.part == b.source.part
        && a.source.total == b.source.total
}

impl Repairs {
    pub async fn used_bytes(self: &Arc<Self>) -> anyhow::Result<u64> {
        let repairs = Arc::clone(self);
        tokio::task::spawn_blocking(move || {
            if !repairs.path.try_exists()? {
                return Ok(0);
            }
            Ok(repairs.store(u64::MAX)?.used_bytes()?)
        })
        .await?
    }

    /// Blocking: obtain a writer without opening a second directory lease.
    pub fn store(&self, capacity: u64) -> Result<Store, infinidysk_nntp::patch::Error> {
        let mut opened = self
            .store
            .lock()
            .map_err(|_| infinidysk_nntp::patch::Error::Recovery)?;
        if opened.is_none() {
            // Writers receive a per-attempt quota; keep the shared lease free of stale limits.
            *opened = Some(Store::open(&self.path, u64::MAX)?);
        }
        opened.as_ref().expect("opened above").limited(capacity)
    }

    async fn candidates(&self, key: RepairKey<'_>) -> Result<Vec<Patch>, Error> {
        match key {
            RepairKey::Article(id) => self.db.article_repairs(id).await,
            RepairKey::Omitted(key) => self
                .db
                .omitted_patch(key)
                .await
                .map(|patch| patch.into_iter().collect()),
        }
        .map_err(|_| Error::Repair("repair catalog cannot be read"))
    }

    async fn read(self: &Arc<Self>, key: RepairKey<'_>) -> Result<Option<Body>, Error> {
        let permit = Arc::clone(&self.readers)
            .acquire_owned()
            .await
            .map_err(|_| Error::Repair("local reader admission closed"))?;
        let patches = self.candidates(key).await?;
        let Some(first) = patches.first() else {
            return Ok(None);
        };
        if patches.iter().any(|p| !same_body(first, p)) {
            return Err(Error::Repair("committed article identities conflict"));
        }
        let repairs = Arc::clone(self);
        let (body, selected, _permit) = tokio::task::spawn_blocking(move || {
            // Keep admission inside the blocking task if its async caller is cancelled.
            let store = {
                let mut opened = repairs
                    .store
                    .lock()
                    .map_err(|_| Error::Repair("local store must be reopened"))?;
                if opened.is_none() {
                    if !repairs
                        .path
                        .try_exists()
                        .map_err(|_| Error::Repair("repair directory cannot be inspected"))?
                    {
                        return Err(Error::Repair("repair directory is missing"));
                    }
                    // This adapter never writes; worker integration must supply write admission.
                    *opened = Some(
                        Store::open(&repairs.path, u64::MAX)
                            .map_err(|_| Error::Repair("repair directory cannot be opened"))?,
                    );
                }
                opened.as_ref().expect("opened above").clone()
            };
            for patch in patches {
                if let Ok(body) = store.read(&patch) {
                    return Ok((body, patch, permit));
                }
            }
            Err(Error::Repair("stored article is missing or corrupt"))
        })
        .await
        .map_err(|_| Error::Repair("local reader stopped unexpectedly"))??;
        // Ownership or NZB bytes may have changed while the file was read and hashed.
        let current = self.candidates(key).await?;
        if current.iter().any(|patch| !same_body(patch, &selected)) {
            return Err(Error::Repair("committed article identities changed"));
        }
        Ok(current.contains(&selected).then_some(body))
    }
}

#[cfg(test)]
mod tests;
