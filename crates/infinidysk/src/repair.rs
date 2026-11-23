//! Read and publish durable repairs. Installing the reader does not start repair jobs.

pub mod api;
mod publish;
mod storage;
mod worker;
pub use publish::Publisher;

use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, PoisonError};

use infinidysk_core::repair::Patch;
use infinidysk_db::Db;
use infinidysk_nntp::{Body, Client, Error, RepairKey, patch::Store};
use tokio::sync::Semaphore;

const READERS: usize = 32;

/// Message IDs with a stored repair, as of one repair epoch.
type Known = Option<(u64, Arc<HashSet<String>>)>;

pub struct Repairs {
    db: Db,
    path: PathBuf,
    store: Mutex<Option<Store>>,
    readers: Arc<Semaphore>,
    lifecycle: Arc<tokio::sync::Mutex<()>>,
    known: Mutex<Known>,
    refresh: Arc<tokio::sync::Mutex<()>>,
}

/// Install once per client; publishers must retain this same service/store owner.
pub fn install(client: &Client, db: Db, path: PathBuf) -> Arc<Repairs> {
    let repairs = Arc::new(Repairs {
        db,
        path,
        store: Mutex::new(None),
        readers: Arc::new(Semaphore::new(READERS)),
        lifecycle: Arc::new(tokio::sync::Mutex::new(())),
        known: Mutex::new(None),
        refresh: Arc::new(tokio::sync::Mutex::new(())),
    });
    client.set_repairs(Box::new({
        let repairs = Arc::clone(&repairs);
        move |key| {
            if !repairs.db.may_have_repairs()
                || matches!(key, RepairKey::Article(id) if repairs.cached(id) == Some(false))
            {
                return None;
            }
            let repairs = Arc::clone(&repairs);
            Some(Box::pin(async move { repairs.read(key).await }))
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

    /// `Some(false)` is a known miss: no stored repair names `id` in the current epoch.
    fn cached(&self, id: &str) -> Option<bool> {
        let (epoch, ids) = self
            .known
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()?;
        let _stable = self.db.repair_observation(epoch)?;
        Some(ids.contains(id))
    }

    /// A missing article must not wait for a reader permit or a full catalog read.
    async fn stored(self: &Arc<Self>, id: &str) -> Result<bool, Error> {
        if let Some(stored) = self.cached(id) {
            return Ok(stored);
        }
        // One background task rebuilds the set; this read asks for its own ID only.
        if let Ok(refresh) = Arc::clone(&self.refresh).try_lock_owned() {
            let repairs = Arc::clone(self);
            tokio::spawn(async move {
                let _refresh = refresh;
                repairs.refresh_known().await;
            });
        }
        Ok(!self.candidates(RepairKey::Article(id)).await?.is_empty())
    }

    async fn refresh_known(&self) {
        // Read the epoch first: a later publication or eviction makes this set stale.
        let epoch = self.db.repair_epoch();
        let current = self
            .known
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .as_ref()
            .is_some_and(|(known, _)| *known == epoch);
        if current {
            return;
        }
        match self.db.repair_message_ids().await {
            Ok(ids) => {
                let ids: HashSet<String> = ids.into_iter().collect();
                *self.known.lock().unwrap_or_else(PoisonError::into_inner) =
                    Some((epoch, Arc::new(ids)));
            }
            // Reads keep asking per ID until a later refresh succeeds.
            Err(error) => tracing::debug!(reason = %error, "repair catalog could not be cached"),
        }
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
        if let RepairKey::Article(id) = key
            && !self.stored(id).await?
        {
            return Ok(None);
        }
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
        // Every current copy has the selected body, so a replaced copy still serves it.
        // Revocation of every copy still reports a miss.
        Ok((!current.is_empty()).then_some(body))
    }
}

#[cfg(test)]
mod tests;
