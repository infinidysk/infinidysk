//! A mount's checked inode invalidation, called while item writers hold admission.

use std::collections::BTreeSet;
use std::io;
use std::sync::{Arc, Mutex};

use futures_util::future::BoxFuture;
use infinidysk_core::ItemId;
use infinidysk_vfs::WriteLease;
use tokio_util::sync::CancellationToken;

use super::{Handle, Inode, Inodes, lock};

type Invalidate = Box<dyn Fn(Inode) -> BoxFuture<'static, io::Result<()>> + Send + Sync>;
type Handles = Arc<Mutex<std::collections::HashMap<u64, Handle>>>;

struct Mounted {
    send: Invalidate,
    inodes: Arc<Mutex<Inodes>>,
    handles: Handles,
    stopping: CancellationToken,
}

/// One built-in mount, including remounts. An unavailable notifier never succeeds.
#[derive(Default)]
pub struct Invalidator {
    mounted: Mutex<Option<Arc<Mounted>>>,
}

/// A session and its aliases captured before SQL publication, with writers retained.
#[must_use = "apply after publication; retain writers on failure or cancellation"]
pub struct Invalidation<'a> {
    registry: &'a Invalidator,
    mounted: Arc<Mounted>,
    aliases: BTreeSet<Inode>,
    writers: &'a mut [WriteLease],
}

impl Invalidator {
    pub(super) fn ready(
        &self,
        notify: fuse3::notify::Notify,
        inodes: Arc<Mutex<Inodes>>,
        handles: Handles,
        stopping: CancellationToken,
    ) {
        *lock(&self.mounted) = Some(Arc::new(Mounted {
            send: Box::new(move |inode| {
                let notify = notify.clone();
                Box::pin(async move { notify.try_invalid_inode(inode, 0, 0).await })
            }),
            inodes,
            handles,
            stopping,
        }));
    }

    pub(super) fn stopped(&self, inodes: &Arc<Mutex<Inodes>>) {
        let mut mounted = lock(&self.mounted);
        if mounted
            .as_ref()
            .is_some_and(|mount| Arc::ptr_eq(&mount.inodes, inodes))
        {
            *mounted = None;
        }
    }

    /// Capture before publication, after the writers drain old replies.
    /// A remount during publication must fail instead of acknowledging another session.
    pub fn prepare<'a>(&'a self, writers: &'a mut [WriteLease]) -> io::Result<Invalidation<'a>> {
        let mounted = lock(&self.mounted).clone().ok_or_else(unavailable)?;
        if mounted.stopping.is_cancelled() {
            return Err(unavailable());
        }
        let items: BTreeSet<_> = writers.iter().map(WriteLease::item).collect();
        let aliases = mounted.aliases(&items);
        Ok(Invalidation {
            registry: self,
            mounted,
            aliases,
            writers,
        })
    }
}

impl Invalidation<'_> {
    /// Arm before SQL. Failed or cancelled finalization leaves delivery closed.
    pub fn block(&mut self) {
        for writer in self.writers.iter_mut() {
            writer.block();
        }
    }
    /// Retire reader buffers and invalidate every captured inode alias.
    /// An error or cancellation is not an acknowledgement. The caller must keep the
    /// writers until reconciliation completes, including after uncertain SQL commits.
    pub async fn apply(self) -> io::Result<()> {
        for writer in self.writers.iter_mut() {
            writer.retire()?;
        }
        for &inode in &self.aliases {
            self.current()?;
            (self.mounted.send)(inode).await?;
        }
        self.current()?;
        for writer in self.writers.iter_mut() {
            writer.acknowledge();
        }
        Ok(())
    }

    fn current(&self) -> io::Result<()> {
        let active = lock(&self.registry.mounted)
            .as_ref()
            .is_some_and(|current| Arc::ptr_eq(current, &self.mounted));
        if self.mounted.stopping.is_cancelled() || !active {
            return Err(unavailable());
        }
        Ok(())
    }
}

impl Mounted {
    fn aliases(&self, items: &BTreeSet<ItemId>) -> BTreeSet<Inode> {
        let mut aliases: BTreeSet<_> = lock(&self.inodes)
            .owners
            .iter()
            .filter_map(|(&inode, fence)| items.contains(&fence.item()).then_some(inode))
            .collect();
        // A forgotten lookup can still have an open handle until release completes.
        aliases.extend(lock(&self.handles).values().filter_map(|handle| {
            match handle {
                Handle::File(file, attr)
                    if file
                        .fence
                        .as_ref()
                        .is_some_and(|fence| items.contains(&fence.item())) =>
                {
                    Some(attr.ino)
                }
                _ => None,
            }
        }));
        aliases
    }
}

fn unavailable() -> io::Error {
    io::Error::new(
        io::ErrorKind::NotConnected,
        "FUSE invalidation is unavailable",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_db::Db;
    use infinidysk_nntp::Client;
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::time::Duration;
    use tokio::sync::Notify;

    fn mounted(send: Invalidate, item: ItemId, vfs: &Vfs) -> Arc<Mounted> {
        Arc::new(Mounted {
            send,
            inodes: Arc::new(Mutex::new(Inodes {
                next: 40,
                by_ino: std::collections::HashMap::new(),
                by_path: std::collections::HashMap::new(),
                owners: [
                    (11, vfs.read_fence(item)),
                    (22, vfs.read_fence(item)),
                    (33, vfs.read_fence(ItemId::new())),
                ]
                .into(),
            })),
            handles: Arc::default(),
            stopping: CancellationToken::new(),
        })
    }

    async fn vfs() -> Vfs {
        Vfs::new(
            Db::connect("sqlite::memory:").await.unwrap(),
            Arc::new(Client::new([])),
            Budget::new(1),
            0,
            "/mnt",
        )
    }

    #[tokio::test]
    async fn every_alias_is_acknowledged_and_other_items_keep_reading() {
        let vfs = Arc::new(vfs().await);
        let item = ItemId::new();
        let calls = Arc::new(Mutex::new(Vec::new()));
        let mount = mounted(
            Box::new({
                let calls = Arc::clone(&calls);
                move |inode| {
                    lock(&calls).push(inode);
                    Box::pin(async { Ok(()) })
                }
            }),
            item,
            &vfs,
        );
        let invalidator = Invalidator {
            mounted: Mutex::new(Some(Arc::clone(&mount))),
        };
        let fence = vfs.read_fence(item);
        let fs = super::super::Fs::new(
            Arc::clone(&vfs),
            Arc::default(),
            0,
            0,
            CancellationToken::new(),
        );
        let file = vfs
            .open(
                &super::super::Node::Readme,
                infinidysk_vfs::Origin {
                    delivery: infinidysk_vfs::Delivery::Fuse,
                    client: "test".into(),
                    player: None,
                },
            )
            .await
            .unwrap()
            .unwrap();
        let attr = fs.attr(44, &super::super::Node::Readme);
        let open = Arc::new(super::super::OpenFile {
            path: "/README".into(),
            fence: Some(fence.clone()),
            session: None,
            reader: tokio::sync::Mutex::new(Some(file)),
            pos: super::super::AtomicU64::new(0),
            waiting: Mutex::default(),
            order_span: 0,
            moved: Notify::new(),
        });
        lock(&mount.handles).insert(1, Handle::File(open, attr));
        let mut writers = vec![fence.write().await];
        invalidator
            .prepare(&mut writers)
            .unwrap()
            .apply()
            .await
            .unwrap();
        assert_eq!(*lock(&calls), [11, 22, 44]);
        lock(&mount.handles).clear();
        lock(&calls).clear();
        invalidator
            .prepare(&mut writers)
            .unwrap()
            .apply()
            .await
            .unwrap();
        assert_eq!(*lock(&calls), [11, 22]);
        assert!(
            tokio::time::timeout(Duration::from_millis(10), fence.read())
                .await
                .is_err()
        );
        drop(vfs.read_fence(ItemId::new()).read().await.unwrap());
        drop(writers);
        drop(fence.read().await.unwrap());
    }

    #[tokio::test]
    async fn failed_or_cancelled_notifications_never_acknowledge_the_batch() {
        let vfs = vfs().await;
        let item = ItemId::new();
        let fence = vfs.read_fence(item);
        let mut writers = vec![fence.write().await];
        let invalidator = Invalidator::default();
        assert_eq!(
            invalidator.prepare(&mut writers).err().unwrap().kind(),
            io::ErrorKind::NotConnected
        );
        let calls = Arc::new(AtomicUsize::new(0));
        let mount = mounted(
            Box::new({
                let calls = Arc::clone(&calls);
                move |_| {
                    let next = calls.fetch_add(1, Ordering::Relaxed);
                    Box::pin(async move {
                        match next {
                            1 => Err(io::Error::from(io::ErrorKind::BrokenPipe)),
                            2 => std::future::pending().await,
                            _ => Ok(()),
                        }
                    })
                }
            }),
            item,
            &vfs,
        );
        *lock(&invalidator.mounted) = Some(Arc::clone(&mount));
        assert_eq!(
            invalidator
                .prepare(&mut writers)
                .unwrap()
                .apply()
                .await
                .unwrap_err()
                .kind(),
            io::ErrorKind::BrokenPipe
        );
        assert!(
            tokio::time::timeout(
                Duration::from_millis(10),
                invalidator.prepare(&mut writers).unwrap().apply()
            )
            .await
            .is_err()
        );
        assert!(
            tokio::time::timeout(Duration::from_millis(10), fence.read())
                .await
                .is_err()
        );
        invalidator
            .prepare(&mut writers)
            .unwrap()
            .apply()
            .await
            .unwrap();
        mount.stopping.cancel();
        assert_eq!(
            invalidator.prepare(&mut writers).err().unwrap().kind(),
            io::ErrorKind::NotConnected
        );
        drop(writers);
    }

    #[tokio::test]
    async fn an_old_session_cannot_acknowledge_or_clear_a_remounted_session() {
        let vfs = vfs().await;
        let item = ItemId::new();
        let entered = Arc::new(Notify::new());
        let release = Arc::new(Notify::new());
        let old = mounted(
            Box::new({
                let (entered, release) = (Arc::clone(&entered), Arc::clone(&release));
                move |_| {
                    let (entered, release) = (Arc::clone(&entered), Arc::clone(&release));
                    Box::pin(async move {
                        entered.notify_one();
                        release.notified().await;
                        Ok(())
                    })
                }
            }),
            item,
            &vfs,
        );
        let invalidator = Arc::new(Invalidator {
            mounted: Mutex::new(Some(Arc::clone(&old))),
        });
        let fence = vfs.read_fence(item);
        let task = tokio::spawn({
            let invalidator = Arc::clone(&invalidator);
            async move {
                invalidator
                    .prepare(&mut [fence.write().await])
                    .unwrap()
                    .apply()
                    .await
            }
        });
        tokio::time::timeout(Duration::from_secs(1), entered.notified())
            .await
            .unwrap();
        let new = mounted(Box::new(|_| Box::pin(async { Ok(()) })), item, &vfs);
        *lock(&invalidator.mounted) = Some(Arc::clone(&new));
        invalidator.stopped(&old.inodes);
        release.notify_one();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(1), task)
                .await
                .unwrap()
                .unwrap()
                .unwrap_err()
                .kind(),
            io::ErrorKind::NotConnected
        );
        assert!(
            lock(&invalidator.mounted)
                .as_ref()
                .is_some_and(|active| Arc::ptr_eq(active, &new))
        );
        invalidator.stopped(&new.inodes);
        assert!(lock(&invalidator.mounted).is_none());
    }

    #[tokio::test]
    async fn publication_cannot_switch_to_a_session_created_after_its_snapshot() {
        let vfs = vfs().await;
        let item = ItemId::new();
        let calls = Arc::new(AtomicUsize::new(0));
        let old = mounted(
            Box::new({
                let calls = Arc::clone(&calls);
                move |_| {
                    calls.fetch_add(1, Ordering::Relaxed);
                    Box::pin(async { Ok(()) })
                }
            }),
            item,
            &vfs,
        );
        let invalidator = Invalidator {
            mounted: Mutex::new(Some(old)),
        };
        let fence = vfs.read_fence(item);
        let mut writers = [fence.write().await];
        let publication = invalidator.prepare(&mut writers).unwrap();
        *lock(&invalidator.mounted) = Some(mounted(
            Box::new(|_| Box::pin(async { Ok(()) })),
            item,
            &vfs,
        ));
        assert_eq!(
            publication.apply().await.unwrap_err().kind(),
            io::ErrorKind::NotConnected
        );
        assert_eq!(calls.load(Ordering::Relaxed), 0);
        assert!(
            tokio::time::timeout(Duration::from_millis(10), fence.read())
                .await
                .is_err()
        );
        drop(writers);
    }
}
