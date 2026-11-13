//! Opt-in Linux kernel regression, independent of path bridge and directory replies.
#![cfg(all(
    target_os = "linux",
    feature = "tokio-runtime",
    not(feature = "async-io-runtime"),
    feature = "unprivileged"
))]
use std::{
    ffi::OsStr,
    io::{Read, Seek, SeekFrom},
    num::NonZeroU32,
    os::unix::fs::MetadataExt,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, UNIX_EPOCH},
};

use bytes::Bytes;
use infinidysk_fuse::{FileType, MountOptions, Result, raw::prelude::*};
struct State {
    content: Mutex<Vec<u8>>,
    entry: Mutex<Option<u64>>,
    lookups: AtomicUsize,
    notifier: Mutex<Option<Notify>>,
    initialized: AtomicUsize,
    callbacks: AtomicUsize,
    ready: tokio::sync::Notify,
}
struct Probe(Arc<State>);
fn attr(ino: u64) -> FileAttr {
    FileAttr {
        ino,
        size: if ino == 2 { 6 } else { 0 },
        blocks: 1,
        atime: UNIX_EPOCH.into(),
        mtime: UNIX_EPOCH.into(),
        ctime: UNIX_EPOCH.into(),
        kind: if ino == 1 {
            FileType::Directory
        } else {
            FileType::RegularFile
        },
        perm: if ino == 1 { 0o755 } else { 0o644 },
        nlink: if ino == 1 { 2 } else { 1 },
        uid: unsafe { libc::geteuid() },
        gid: unsafe { libc::getegid() },
        rdev: 0,
        blksize: 4096,
    }
}
impl Filesystem for Probe {
    async fn init(&self, _: Request) -> Result<ReplyInit> {
        self.0.initialized.fetch_add(1, Ordering::SeqCst);
        Ok(ReplyInit::new(NonZeroU32::new(65536).unwrap()))
    }
    async fn ready(&self, notify: Notify) {
        assert_eq!(self.0.initialized.load(Ordering::SeqCst), 1);
        self.0.callbacks.fetch_add(1, Ordering::SeqCst);
        *self.0.notifier.lock().unwrap() = Some(notify);
        self.0.ready.notify_one();
    }
    async fn destroy(&self, _: Request) {}
    #[cfg(feature = "file-lock")]
    async fn getlk(
        &self,
        _: Request,
        _: u64,
        _: u64,
        _: u64,
        _: u64,
        _: u64,
        _: u32,
        _: u32,
    ) -> Result<ReplyLock> {
        Err(libc::ENOSYS.into())
    }
    #[cfg(feature = "file-lock")]
    async fn setlk(
        &self,
        _: Request,
        _: u64,
        _: u64,
        _: u64,
        _: u64,
        _: u64,
        _: u32,
        _: u32,
        _: bool,
    ) -> Result<()> {
        Err(libc::ENOSYS.into())
    }
    async fn lookup(&self, _: Request, parent: u64, name: &OsStr) -> Result<ReplyEntry> {
        self.0.lookups.fetch_add(1, Ordering::SeqCst);
        if parent != 1 || name != "file" {
            return Err(libc::ENOENT.into());
        }
        let ino = self.0.entry.lock().unwrap().ok_or(libc::ENOENT)?;
        Ok(ReplyEntry {
            ttl: Duration::from_secs(60),
            attr: attr(ino),
            generation: 0,
        })
    }
    async fn getattr(&self, _: Request, ino: u64, _: Option<u64>, _: u32) -> Result<ReplyAttr> {
        if ino != 1 && ino != 2 && ino != 3 {
            return Err(libc::ENOENT.into());
        }
        Ok(ReplyAttr {
            ttl: Duration::from_secs(60),
            attr: attr(ino),
        })
    }
    async fn open(&self, _: Request, _: u64, _: u32) -> Result<ReplyOpen> {
        Ok(ReplyOpen { fh: 1, flags: 0 })
    }
    async fn read(
        &self,
        _: Request,
        ino: u64,
        _: u64,
        offset: u64,
        size: u32,
    ) -> Result<ReplyData> {
        if ino != 2 {
            return Err(libc::EINVAL.into());
        }
        let content = self.0.content.lock().unwrap();
        let start = (offset as usize).min(content.len());
        let end = start.saturating_add(size as usize).min(content.len());
        Ok(ReplyData {
            data: Bytes::copy_from_slice(&content[start..end]),
        })
    }
    async fn flush(&self, _: Request, _: u64, _: u64, _: u64) -> Result<()> {
        Ok(())
    }
    async fn release(&self, _: Request, _: u64, _: u64, _: u32, _: u64, _: bool) -> Result<()> {
        Ok(())
    }
}
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn invalidate_cached_data_without_poll() {
    with_mounted_probe(|root, state| {
        let mut file = std::fs::File::open(root.join("file")).unwrap();
        let mut bytes = [0; 6];
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"before");
        *state.content.lock().unwrap() = b"after!".to_vec();
        file.seek(SeekFrom::Start(0)).unwrap();
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(
            &bytes, b"before",
            "the page cache must be demonstrated before invalidation"
        );
        // No poll implementation and no lock held while notifying the kernel.
        let notify = state.notifier.lock().unwrap().clone().unwrap();
        tokio::runtime::Handle::current().block_on(notify.invalid_inode(2, 0, 0));
        file.seek(SeekFrom::Start(0)).unwrap();
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"after!");
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn store_cached_data_without_poll() {
    with_mounted_probe(|root, state| {
        let mut file = std::fs::File::open(root.join("file")).unwrap();
        let mut bytes = [0; 6];
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"before");

        let notify = state.notifier.lock().unwrap().clone().unwrap();
        tokio::runtime::Handle::current().block_on(notify.store(
            2,
            2,
            Bytes::from_static(b"\0\xff!"),
        ));

        file.seek(SeekFrom::Start(0)).unwrap();
        file.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"be\0\xff!e");
        assert_eq!(&*state.content.lock().unwrap(), b"before");
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn invalidate_cached_entry_without_poll() {
    with_mounted_probe(|root, state| {
        let path = root.join("file");
        assert_eq!(std::fs::metadata(&path).unwrap().ino(), 2);
        let lookups = state.lookups.load(Ordering::SeqCst);
        assert!(lookups > 0, "the initial lookup must reach the filesystem");

        *state.entry.lock().unwrap() = Some(3);
        assert_eq!(std::fs::metadata(&path).unwrap().ino(), 2);
        assert_eq!(
            state.lookups.load(Ordering::SeqCst),
            lookups,
            "the old directory entry must be cached before invalidation"
        );

        let notify = state.notifier.lock().unwrap().clone().unwrap();
        tokio::runtime::Handle::current().block_on(notify.invalid_entry(1, "file".into()));
        assert_eq!(std::fs::metadata(&path).unwrap().ino(), 3);
        assert!(
            state.lookups.load(Ordering::SeqCst) > lookups,
            "invalidating the entry must force a fresh lookup"
        );
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn delete_cached_entry_without_poll() {
    with_mounted_probe(|root, state| {
        let path = root.join("file");
        assert_eq!(std::fs::metadata(&path).unwrap().ino(), 2);
        let lookups = state.lookups.load(Ordering::SeqCst);
        assert!(lookups > 0, "the initial lookup must reach the filesystem");

        *state.entry.lock().unwrap() = None;
        assert_eq!(std::fs::metadata(&path).unwrap().ino(), 2);
        assert_eq!(
            state.lookups.load(Ordering::SeqCst),
            lookups,
            "the deleted directory entry must still be cached before notification"
        );

        let notify = state.notifier.lock().unwrap().clone().unwrap();
        tokio::runtime::Handle::current().block_on(notify.delete(1, 2, "file".into()));
        assert_eq!(
            std::fs::metadata(&path).unwrap_err().raw_os_error(),
            Some(libc::ENOENT)
        );
        assert!(
            state.lookups.load(Ordering::SeqCst) > lookups,
            "deleting the entry must force a fresh lookup"
        );
    })
    .await;
}

// Keep blocking filesystem calls off the runtime workers serving FUSE requests,
// and unmount even when an assertion in the probe fails.
async fn with_mounted_probe(probe: impl FnOnce(PathBuf, Arc<State>) + Send + 'static) {
    let directory = tempfile::tempdir().unwrap();
    let state = Arc::new(State {
        content: Mutex::new(b"before".to_vec()),
        entry: Mutex::new(Some(2)),
        lookups: AtomicUsize::new(0),
        notifier: Mutex::new(None),
        initialized: AtomicUsize::new(0),
        callbacks: AtomicUsize::new(0),
        ready: tokio::sync::Notify::new(),
    });
    let mut options = MountOptions::default();
    options.fs_name("fuse3-notifier-test").write_back(false);
    let mount = Session::new(options)
        .mount_with_unprivileged(Probe(state.clone()), directory.path())
        .await
        .expect("explicitly requested kernel test requires working FUSE");
    let initialized = tokio::time::timeout(Duration::from_secs(10), state.ready.notified()).await;
    let result = if initialized.is_ok() {
        let root = directory.path().to_path_buf();
        let state = state.clone();
        Some(tokio::task::spawn_blocking(move || probe(root, state)).await)
    } else {
        None
    };
    let unmounted = tokio::time::timeout(Duration::from_secs(10), mount.unmount()).await;
    assert!(initialized.is_ok(), "notifier callback did not run");
    result.expect("initialized mount").unwrap();
    unmounted.unwrap().unwrap();
    assert_eq!(state.callbacks.load(Ordering::SeqCst), 1);
}
