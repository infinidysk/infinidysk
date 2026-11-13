//! Opt-in Linux checks that a failed session still unmounts.
#![cfg(all(
    target_os = "linux",
    feature = "tokio-runtime",
    not(feature = "async-io-runtime"),
    feature = "unprivileged"
))]
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use infinidysk_fuse::raw::MountHandle;
use infinidysk_fuse::{MountOptions, Result, raw::prelude::*};
use tokio::sync::Notify;
use tokio::time::{sleep, timeout};

const WAIT: Duration = Duration::from_secs(10);

/// Fails INIT, which leaves a disconnected mount behind until someone unmounts it.
struct FailingInit(Arc<Notify>);

impl Filesystem for FailingInit {
    async fn init(&self, _: Request) -> Result<ReplyInit> {
        self.0.notify_one();
        Err(libc::EXDEV.into())
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
}

fn is_mounted(path: &Path) -> bool {
    let mountinfo = std::fs::read_to_string("/proc/self/mountinfo").unwrap();
    let path = path.to_str().unwrap();
    mountinfo
        .lines()
        .any(|line| line.split(' ').nth(4) == Some(path))
}

/// Mounts [`FailingInit`] and returns once INIT reached it.
async fn mount_failing(path: &Path) -> MountHandle {
    let init_called = Arc::new(Notify::new());
    let mut options = MountOptions::default();
    options.fs_name("infinidysk-fuse-unmount-test");
    let handle = Session::new(options)
        .mount_with_unprivileged(FailingInit(init_called.clone()), path)
        .await
        .expect("explicitly requested kernel test requires working FUSE");
    timeout(WAIT, init_called.notified())
        .await
        .expect("INIT within the timeout");
    assert!(is_mounted(path));
    handle
}

#[tokio::test]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn unmount_after_a_session_error_unmounts() {
    let directory = tempfile::tempdir().unwrap();
    let handle = mount_failing(directory.path()).await;

    let err = timeout(WAIT, handle.unmount())
        .await
        .expect("unmount within the timeout")
        .expect_err("init failed");
    assert_eq!(err.raw_os_error(), Some(libc::EXDEV), "{err}");
    assert!(!is_mounted(directory.path()));
}

#[tokio::test]
#[ignore = "requires Linux /dev/fuse and fusermount3; run explicitly with --ignored"]
async fn dropping_the_handle_after_a_session_error_unmounts() {
    let directory = tempfile::tempdir().unwrap();
    let mut handle = mount_failing(directory.path()).await;

    let err = timeout(WAIT, &mut handle)
        .await
        .expect("session ends within the timeout")
        .expect_err("init failed");
    assert_eq!(err.raw_os_error(), Some(libc::EXDEV), "{err}");
    assert!(is_mounted(directory.path()));

    drop(handle);
    timeout(WAIT, async {
        while is_mounted(directory.path()) {
            sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("dropping the handle unmounts within the timeout");
}
