//! Per-item read admission retained until delivery releases its reply bytes.

use std::collections::{BTreeSet, HashMap};
use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError, Weak};

use infinidysk_core::{BlobId, FileMeta, ItemId, repair::Patch};
use tokio::sync::{OwnedRwLockReadGuard, OwnedRwLockWriteGuard, RwLock, watch};

use crate::scope::{Effects, Scope};

type Entries = Mutex<State>;

#[derive(Default)]
struct State {
    entries: HashMap<ItemId, Weak<Inner>>,
    stamp: u64,
    active: Option<Active>,
}

struct Active {
    effects: Effects,
    items: Vec<ItemId>,
    done: watch::Sender<bool>,
}

/// Blocks new affected opens while the coordinator owns publication and invalidation.
#[must_use = "retain through SQL publication and delivery invalidation"]
pub struct Publication {
    entries: Arc<Entries>,
    fences: Vec<ReadFence>,
}

pub(crate) enum Admission {
    Retry,
    Wait(watch::Receiver<bool>),
    Ready(ReadFence),
}

#[derive(Default)]
pub(crate) struct Fences {
    entries: Arc<Entries>,
}

/// Every path and open handle of an item shares this fence.
#[derive(Clone)]
pub struct ReadFence {
    inner: Arc<Inner>,
}

struct Inner {
    item: ItemId,
    entries: Weak<Entries>,
    gate: Arc<RwLock<()>>,
    version: watch::Sender<u64>,
    scopes: Mutex<Vec<Scope>>,
    accessed: Mutex<BTreeSet<String>>,
    blocked: AtomicBool,
}

/// The item revision that produced buffered data or a queued damage report.
#[derive(Clone)]
pub struct ReadVersion {
    fence: ReadFence,
    value: u64,
}

/// Delivery must retain this lease through the transport's use of its bytes.
#[must_use = "keep the read lease until the reply bytes are released"]
pub struct ReadLease {
    // Unlock before releasing the registry owner.
    _guard: OwnedRwLockReadGuard<()>,
    _fence: ReadFence,
}

/// Exclusive admission; it does not itself refresh readers or invalidate kernel pages.
#[must_use = "keep the write lease throughout publication and invalidation"]
pub struct WriteLease {
    _guard: OwnedRwLockWriteGuard<()>,
    fence: ReadFence,
}

impl Fences {
    pub(crate) fn repair_pins(
        &self,
        cached_gap: impl Fn(ItemId) -> bool,
    ) -> (BTreeSet<BlobId>, BTreeSet<String>) {
        let live: Vec<_> = self
            .entries
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .entries
            .values()
            .filter_map(Weak::upgrade)
            .collect();
        let mut nzbs = BTreeSet::new();
        let mut articles = BTreeSet::new();
        for inner in live {
            // This snapshot and an idle gap entry own no delivery bytes.
            if Arc::strong_count(&inner) == 2 && cached_gap(inner.item) {
                continue;
            }
            nzbs.extend(
                inner
                    .scopes
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .iter()
                    .filter_map(|scope| scope.nzb),
            );
            articles.extend(
                inner
                    .accessed
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .iter()
                    .cloned(),
            );
        }
        (nzbs, articles)
    }
    pub(crate) fn get(&self, item: ItemId) -> ReadFence {
        let mut entries = self.entries.lock().unwrap_or_else(PoisonError::into_inner);
        let inner = entries
            .entries
            .get(&item)
            .and_then(Weak::upgrade)
            .unwrap_or_else(|| {
                let inner = Arc::new(Inner {
                    item,
                    entries: Arc::downgrade(&self.entries),
                    gate: Arc::new(RwLock::new(())),
                    version: watch::channel(0).0,
                    scopes: Mutex::new(Vec::new()),
                    accessed: Mutex::new(BTreeSet::new()),
                    blocked: AtomicBool::new(false),
                });
                entries.entries.insert(item, Arc::downgrade(&inner));
                inner
            });
        drop(entries);
        ReadFence { inner }
    }

    pub(crate) fn stamp(&self) -> u64 {
        self.entries
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .stamp
    }

    pub(crate) fn admit(&self, stamp: u64, item: ItemId, scope: Scope) -> Admission {
        let fence = self.get(item);
        let entries = self.entries.lock().unwrap_or_else(PoisonError::into_inner);
        if stamp != entries.stamp {
            return Admission::Retry;
        }
        if let Some(active) = &entries.active
            && active.effects.affects(&scope)
        {
            return Admission::Wait(active.done.subscribe());
        }
        fence.inner.remember(scope);
        drop(entries);
        Admission::Ready(fence)
    }

    pub(crate) fn publish(&self, nzb: BlobId, patches: &[Patch]) -> io::Result<Publication> {
        // Empty batches fence metadata-only publication for this NZB.
        if patches.len() > infinidysk_db::repair::MAX_PATCHES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid repair batch size",
            ));
        }
        let effects = Effects::new(nzb, patches);
        let mut entries = self.entries.lock().unwrap_or_else(PoisonError::into_inner);
        if entries.active.is_some() {
            return Err(io::Error::new(
                io::ErrorKind::WouldBlock,
                "repair publication is already active",
            ));
        }
        entries
            .stamp
            .checked_add(2)
            .ok_or_else(|| io::Error::other("publication revision exhausted"))?;
        let live: Vec<_> = entries.entries.values().filter_map(Weak::upgrade).collect();
        let mut fences: Vec<_> = live
            .iter()
            .filter(|inner| {
                effects.touches(
                    &inner
                        .accessed
                        .lock()
                        .unwrap_or_else(PoisonError::into_inner),
                ) || inner
                    .scopes
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .iter()
                    .any(|scope| effects.affects(scope))
            })
            .map(|inner| ReadFence {
                inner: Arc::clone(inner),
            })
            .collect();
        fences.sort_unstable_by_key(ReadFence::item);
        entries.stamp += 1;
        entries.active = Some(Active {
            effects,
            items: fences.iter().map(ReadFence::item).collect(),
            done: watch::channel(false).0,
        });
        drop(entries);
        drop(live);
        Ok(Publication {
            entries: Arc::clone(&self.entries),
            fences,
        })
    }
}

impl Publication {
    pub async fn writers(&self) -> Vec<WriteLease> {
        let mut writers = Vec::with_capacity(self.fences.len());
        for fence in &self.fences {
            writers.push(fence.write().await);
        }
        writers
    }
}

impl Drop for Publication {
    fn drop(&mut self) {
        let mut entries = self.entries.lock().unwrap_or_else(PoisonError::into_inner);
        entries.stamp += 1;
        let active = entries.active.take();
        drop(entries);
        if let Some(active) = active {
            active.done.send_replace(true);
        }
    }
}

impl ReadFence {
    pub fn item(&self) -> ItemId {
        self.inner.item
    }

    #[must_use]
    pub fn version(&self) -> ReadVersion {
        ReadVersion {
            fence: self.clone(),
            value: *self.inner.version.borrow(),
        }
    }

    pub async fn read(&self) -> io::Result<ReadLease> {
        let lease = ReadLease {
            _guard: Arc::clone(&self.inner.gate).read_owned().await,
            _fence: self.clone(),
        };
        if self.inner.blocked.load(Ordering::Acquire) {
            return Err(io::Error::other("repair publication needs reconciliation"));
        }
        Ok(lease)
    }

    /// Wait for previously admitted reads, then block new reads of this item.
    /// A coordinator must retire buffered data and complete kernel invalidation before
    /// releasing this lease. This is admission, not a publication acknowledgement.
    pub async fn write(&self) -> WriteLease {
        WriteLease {
            _guard: Arc::clone(&self.inner.gate).write_owned().await,
            fence: self.clone(),
        }
    }
}

impl ReadVersion {
    #[must_use]
    pub fn is_current(&self) -> bool {
        *self.fence.inner.version.borrow() == self.value
    }

    /// Recheck after admission and keep publication out while recording this report.
    pub async fn read(&self) -> Option<ReadLease> {
        let lease = self.fence.read().await.ok()?;
        self.is_current().then_some(lease)
    }

    pub(crate) fn latest(&self) -> Self {
        self.fence.version()
    }

    pub(crate) fn same(&self, other: &Self) -> bool {
        self.value == other.value && Arc::ptr_eq(&self.fence.inner, &other.fence.inner)
    }

    pub(crate) fn blocked(&self) -> bool {
        self.fence.inner.blocked.load(Ordering::Acquire)
    }

    pub(crate) async fn track(&self, nzb: Option<BlobId>, meta: &Arc<FileMeta>) -> bool {
        let Some(registry) = self.fence.inner.entries.upgrade() else {
            return true;
        };
        let scope = Scope::new(nzb, meta);
        let waiting = {
            let entries = registry.lock().unwrap_or_else(PoisonError::into_inner);
            if let Some(active) = &entries.active
                && active.effects.affects(&scope)
                && !active.items.contains(&self.fence.item())
            {
                Some(active.done.subscribe())
            } else {
                self.fence.inner.remember(scope);
                None
            }
        };
        if let Some(mut waiting) = waiting {
            let _ = waiting.changed().await;
            return false;
        }
        true
    }

    pub(crate) async fn access(&self, id: &str) -> io::Result<()> {
        loop {
            if self.blocked() || !self.is_current() {
                return Err(io::Error::other(
                    "file revision changed before segment access",
                ));
            }
            let Some(registry) = self.fence.inner.entries.upgrade() else {
                return Ok(());
            };
            let waiting = {
                let entries = registry.lock().unwrap_or_else(PoisonError::into_inner);
                if let Some(active) = &entries.active
                    && active.effects.contains(id)
                    && !active.items.contains(&self.fence.item())
                {
                    Some(active.done.subscribe())
                } else {
                    if !id.is_empty() {
                        let mut accessed = self
                            .fence
                            .inner
                            .accessed
                            .lock()
                            .unwrap_or_else(PoisonError::into_inner);
                        if !accessed.contains(id) {
                            accessed.insert(id.to_owned());
                        }
                    }
                    None
                }
            };
            let Some(mut waiting) = waiting else {
                return Ok(());
            };
            let _ = waiting.changed().await;
        }
    }

    pub(crate) async fn changed(self) {
        let mut receiver = self.fence.inner.version.subscribe();
        if *receiver.borrow_and_update() == self.value {
            let _ = receiver.changed().await;
        }
    }
}

impl Inner {
    fn remember(&self, scope: Scope) {
        let mut scopes = self.scopes.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(previous) = scopes.iter_mut().find(|old| old.nzb == scope.nzb) {
            *previous = scope;
        } else {
            scopes.push(scope);
        }
    }
}

impl WriteLease {
    pub fn item(&self) -> ItemId {
        self.fence.item()
    }

    /// Arm before COMMIT. Dropping a failed publication must not expose old buffers.
    pub fn block(&mut self) {
        self.fence.inner.blocked.store(true, Ordering::Release);
    }

    /// Clear only after reader retirement and every native alias acknowledgement.
    pub fn acknowledge(&mut self) {
        self.fence.inner.blocked.store(false, Ordering::Release);
    }

    /// Retire userspace buffers after publication. The caller must still confirm
    /// native invalidation before releasing exclusive admission.
    pub fn retire(&mut self) -> io::Result<()> {
        let version = &self.fence.inner.version;
        let next = (*version.borrow())
            .checked_add(1)
            .ok_or_else(|| io::Error::other("file revision exhausted"))?;
        version.send_replace(next);
        Ok(())
    }
}

impl Drop for Inner {
    fn drop(&mut self) {
        let Some(entries) = self.entries.upgrade() else {
            return;
        };
        let mut entries = entries.lock().unwrap_or_else(PoisonError::into_inner);
        // Another lookup can replace the expired weak entry before this drop runs.
        if entries
            .entries
            .get(&self.item)
            .is_some_and(|entry| std::ptr::eq(entry.as_ptr(), self))
        {
            entries.entries.remove(&self.item);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[tokio::test]
    async fn expired_metadata_keeps_only_accessed_articles_and_new_fetches_wait() {
        use infinidysk_core::{ByteRange, NzbFile, PatchId, Segment, Segments, repair::Source};
        let registry = Fences::default();
        let meta = Arc::new(FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: vec![Segment {
                    id: "shared@test".into(),
                    size: 1,
                    fallbacks: vec![],
                }],
                ..Segments::default()
            },
            proof: None,
        }));
        let weak = Arc::downgrade(&meta);
        let touched = registry.get(ItemId::new());
        let untouched = registry.get(ItemId::new());
        for fence in [&touched, &untouched] {
            assert!(matches!(
                registry.admit(registry.stamp(), fence.item(), Scope::new(None, &meta)),
                Admission::Ready(_)
            ));
        }
        touched.version().access("shared@test").await.unwrap();
        drop(meta);
        assert!(
            weak.upgrade().is_none(),
            "cached native aliases do not pin metadata"
        );
        let publication = registry
            .publish(
                BlobId::new(),
                &[Patch {
                    id: PatchId::new(),
                    source: Source {
                        set: [1; 16],
                        file: [2; 16],
                        range: ByteRange { start: 0, end: 1 },
                        file_length: 1,
                        message_id: Some("shared@test".into()),
                        part: None,
                        total: None,
                    },
                    sha256: [3; 32],
                }],
            )
            .unwrap();
        let writers = publication.writers().await;
        assert_eq!(writers.len(), 1);
        assert_eq!(writers[0].item(), touched.item());
        let version = untouched.version();
        let mut access = Box::pin(version.access("shared@test"));
        assert!(
            tokio::time::timeout(Duration::from_millis(20), &mut access)
                .await
                .is_err()
        );
        drop(writers);
        drop(publication);
        access.await.unwrap();
    }

    #[allow(clippy::significant_drop_tightening)] // The spawned report must outlive publication.
    #[tokio::test]
    async fn queued_reports_recheck_their_revision_after_admission() {
        let registry = Fences::default();
        let fence = registry.get(ItemId::new());
        let queued = fence.version();
        let mut publication = fence.write().await;
        let report = tokio::spawn(async move { queued.read().await });
        tokio::task::yield_now().await;
        assert!(!report.is_finished());
        publication.retire().unwrap();
        drop(publication);
        assert!(report.await.unwrap().is_none());
        let current = fence.version();
        let accepted = current.read().await.unwrap();
        assert!(
            tokio::time::timeout(Duration::from_millis(20), fence.write())
                .await
                .is_err()
        );
        drop(accepted);
        let mut publication = fence.write().await;
        publication.retire().unwrap();
        drop(publication);
        assert!(!current.is_current());
    }

    #[tokio::test]
    async fn aliases_share_admission_and_other_items_keep_reading() {
        let registry = Fences::default();
        let item = ItemId::new();
        let first = registry.get(item);
        let alias = registry.get(item);
        let unrelated = registry.get(ItemId::new());
        let read = first.read().await.unwrap();
        drop(first);
        assert!(
            tokio::time::timeout(Duration::from_millis(20), alias.write())
                .await
                .is_err()
        );
        let other = tokio::time::timeout(Duration::from_secs(1), unrelated.write())
            .await
            .unwrap();
        drop(other);
        drop(read);
        let write = tokio::time::timeout(Duration::from_secs(1), alias.write())
            .await
            .unwrap();
        let next = registry.get(item);
        assert!(
            tokio::time::timeout(Duration::from_millis(20), next.read())
                .await
                .is_err()
        );
        drop(write);
        let read = tokio::time::timeout(Duration::from_secs(1), next.read())
            .await
            .unwrap()
            .unwrap();
        drop(read);
        drop(next);
        drop(alias);
        drop(unrelated);
        assert_eq!(
            registry.entries.lock().unwrap().entries.len(),
            0,
            "expired items do not accumulate"
        );
    }

    #[tokio::test]
    async fn cancelled_waiters_and_registry_teardown_leave_no_owned_entries() {
        let registry = Fences::default();
        let fence = registry.get(ItemId::new());
        let weak = Arc::downgrade(&fence.inner);
        let write = fence.write().await;
        assert!(
            tokio::time::timeout(Duration::from_millis(20), fence.read())
                .await
                .is_err()
        );
        drop(write);
        let read = tokio::time::timeout(Duration::from_secs(1), fence.read())
            .await
            .unwrap()
            .unwrap();
        let entries = Arc::downgrade(&registry.entries);
        drop(registry);
        assert!(entries.upgrade().is_none());
        drop(fence);
        assert!(
            weak.upgrade().is_some(),
            "the lease retains its identity without the registry"
        );
        drop(read);
        assert!(weak.upgrade().is_none());
    }
}
