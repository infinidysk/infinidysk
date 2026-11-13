//! Opt-in Linux check that a notification waiting for a kernel lock does not hold back replies.
#![cfg(all(
    target_os = "linux",
    feature = "tokio-runtime",
    not(feature = "async-io-runtime"),
    feature = "unprivileged"
))]
use std::ffi::OsStr;
use std::num::NonZeroU32;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, UNIX_EPOCH};

use infinidysk_fuse::{FileType, MountOptions, Result, raw::prelude::*};
use tokio::sync::Notify as Signal;
use tokio::time::{sleep, timeout};

const WAIT: Duration = Duration::from_secs(10);

#[derive(Default)]
struct State {
    ready: Signal,
    notifier: Mutex<Option<Notify>>,
    lookup_entered: Signal,
    lookup_release: Signal,
}

/// A lookup of `gated` waits for the test; every other lookup misses at once.
struct GatedLookup(Arc<State>);

impl Filesystem for GatedLookup {
    async fn init(&self, _: Request) -> Result<ReplyInit> {
        Ok(ReplyInit::new(NonZeroU32::new(65536).unwrap()))
    }

    async fn ready(&self, notify: Notify) {
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

    async fn lookup(&self, _: Request, _: u64, name: &OsStr) -> Result<ReplyEntry> {
        if name == "gated" {
            self.0.lookup_entered.notify_one();
            self.0.lookup_release.notified().await;
        }
        Err(libc::ENOENT.into())
    }

    async fn getattr(&self, _: Request, ino: u64, _: Option<u64>, _: u32) -> Result<ReplyAttr> {
        if ino != 1 {
            return Err(libc::ENOENT.into());
        }
        Ok(ReplyAttr {
            ttl: Duration::from_secs(60),
            attr: FileAttr {
                ino,
                size: 0,
                blocks: 0,
                atime: UNIX_EPOCH.into(),
                mtime: UNIX_EPOCH.into(),
                ctime: UNIX_EPOCH.into(),
                kind: FileType::Directory,
                perm: 0o755,
                nlink: 2,
                uid: nix::unistd::geteuid().as_raw(),
                gid: nix::unistd::getegid().as_raw(),
                rdev: 0,
                blksize: 4096,
            },
        })
    }
}

/// Whether a thread of this process sleeps in the notification's wait for the parent inode lock.
fn waiting_for_parent_lock() -> bool {
    std::fs::read_dir("/proc/self/task")
        .unwrap()
        .filter_map(|task| std::fs::read_to_string(task.ok()?.path().join("wchan")).ok())
        // Linux 7.0 reports the caller, older kernels the rwsem slow path.
        .any(|wchan| wchan.contains("fuse_reverse_inval_entry") || wchan.contains("down_write"))
}

/// Aborts the connection through fusectl if the test panics, so a deadlocked run still exits.
struct AbortOnPanic(PathBuf);

impl Drop for AbortOnPanic {
    fn drop(&mut self) {
        if std::thread::panicking() {
            abort_connection(&self.0);
        }
    }
}

fn abort_connection(mount_path: &Path) {
    let mountinfo = std::fs::read_to_string("/proc/self/mountinfo").unwrap();
    let path = mount_path.to_str().unwrap();
    let Some(device) = mountinfo.lines().find_map(|line| {
        let fields = line.split(' ').collect::<Vec<_>>();
        (fields.get(4) == Some(&path)).then(|| fields[2].to_owned())
    }) else {
        return;
    };
    let minor = device.split(':').nth(1).unwrap();
    let _ = std::fs::write(format!("/sys/fs/fuse/connections/{minor}/abort"), "1");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn notification_waiting_for_a_parent_lock_does_not_hold_back_replies() {
    let directory = tempfile::tempdir().unwrap();
    let root = directory.path().to_owned();
    let state = Arc::new(State::default());
    let mut options = MountOptions::default();
    options.fs_name("infinidysk-fuse-lock-cycle-test");
    let handle = Session::new(options)
        .mount_with_unprivileged(GatedLookup(state.clone()), &root)
        .await
        .expect("explicitly requested kernel test requires working FUSE");
    let _abort = AbortOnPanic(root.clone());
    timeout(WAIT, state.ready.notified())
        .await
        .expect("ready within the timeout");
    let notify = state.notifier.lock().unwrap().clone().unwrap();

    // The lookup holds the root directory's inode lock until its reply is written.
    let gated = root.join("gated");
    let lookup = std::thread::spawn(move || std::fs::metadata(gated).map(|_| ()));
    timeout(WAIT, state.lookup_entered.notified())
        .await
        .expect("the lookup reaches the filesystem within the timeout");

    let notification = tokio::spawn(async move { notify.invalid_entry(1, "gated".into()).await });
    timeout(WAIT, async {
        while !waiting_for_parent_lock() {
            sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("the notification waits for the root directory's inode lock");
    assert!(!notification.is_finished());

    state.lookup_release.notify_one();
    timeout(WAIT, notification)
        .await
        .expect("the lookup reply does not wait for the blocked notification")
        .unwrap();

    let err = lookup.join().unwrap().expect_err("the gated lookup misses");
    assert_eq!(err.raw_os_error(), Some(libc::ENOENT), "{err}");

    let other = root.join("other");
    let err = timeout(
        WAIT,
        tokio::task::spawn_blocking(move || std::fs::metadata(other)),
    )
    .await
    .expect("a later lookup replies within the timeout")
    .unwrap()
    .expect_err("the later lookup misses");
    assert_eq!(err.raw_os_error(), Some(libc::ENOENT), "{err}");

    timeout(WAIT, handle.unmount())
        .await
        .expect("unmount within the timeout")
        .unwrap();
}
