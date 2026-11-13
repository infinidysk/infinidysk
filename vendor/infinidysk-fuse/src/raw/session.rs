#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use std::ffi::OsStr;
use std::ffi::OsString;
use std::fmt::{Debug, Formatter};
use std::future::Future;
use std::io::Error as IoError;
use std::io::ErrorKind;
use std::io::IoSlice;
use std::io::Result as IoResult;
use std::num::NonZeroU32;
#[cfg(any(target_os = "linux", target_os = "freebsd"))]
use std::os::fd::AsFd;
use std::os::unix::ffi::OsStrExt;
use std::os::unix::ffi::OsStringExt;
#[cfg(any(target_os = "linux", target_os = "freebsd"))]
use std::os::unix::io::AsRawFd;
use std::panic::AssertUnwindSafe;
use std::path::{Path, PathBuf};
use std::pin::{Pin, pin};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::task::Context;
use std::task::Poll;
use std::task::ready;
use std::time::Duration;

#[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
use async_fs::read_dir;
#[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
use async_global_executor::{self as task, Task as JoinHandle};
#[cfg(all(
    target_os = "linux",
    not(feature = "tokio-runtime"),
    feature = "async-io-runtime",
    feature = "unprivileged"
))]
use async_process::Command;
use futures_util::future::{self, Either, FutureExt};
use futures_util::stream::StreamExt;
use nix::mount;
#[cfg(any(target_os = "freebsd", target_os = "macos"))]
use nix::mount::MntFlags;
#[cfg(all(
    target_os = "linux",
    not(feature = "async-io-runtime"),
    feature = "tokio-runtime",
    feature = "unprivileged"
))]
use tokio::process::Command;
#[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
use tokio::task::JoinHandle;
#[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
use tokio::{fs::read_dir, task};
use tracing::{Instrument, Span, debug, debug_span, error, instrument, warn};
use zerocopy::{FromBytes, IntoBytes};

use crate::MountOptions;
#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use crate::find_fusermount3;
use crate::helper::*;
use crate::notify::Notify;
use crate::raw::abi::*;
#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
use crate::raw::connection::FuseConnection;
use crate::raw::filesystem::Filesystem;
use crate::raw::reply::ReplyXAttr;
use crate::raw::request::Request;
use crate::{Errno, SetAttr};

/// A Future which returns when a file system is unmounted
///
/// when drop the [`MountHandle`], it will unmount Filesystem in background task, if user want to
/// wait unmount completely, use [`MountHandle::unmount`]
#[derive(Debug)]
pub struct MountHandle {
    inner: Option<MountHandleInner>,
}

impl MountHandle {
    /// Ends the session and unmounts the filesystem. A session error is returned even when the
    /// unmount succeeds.
    pub async fn unmount(mut self) -> IoResult<()> {
        self.inner
            .take()
            .expect("unmount call twice")
            .inner_unmount(false)
            .await
    }
}

impl Drop for MountHandle {
    fn drop(&mut self) {
        let Some(inner) = self.inner.take() else {
            return;
        };

        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        let runtime = inner.task.runtime.clone();

        let cleanup = async move {
            if let Err(err) = inner.inner_unmount(true).await {
                warn!(%err, "unmount on drop failed");
            }
        };

        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        task::spawn(cleanup).detach();

        // The stored handle works without a runtime context on this thread.
        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        drop(runtime.spawn(cleanup));
    }
}

/// A sender for sending response to kernel, it will send response immediately when call send method, and end the session when send failed.
#[derive(Debug)]
pub(crate) struct ResponseSender {
    /// Set when a write failed in a way that ends the session.
    failed: AtomicBool,
    /// connection to write response
    connection: Arc<FuseConnection>,
    /// Runs notification writes, which may be polled outside the runtime.
    #[cfg(all(
        target_os = "linux",
        not(feature = "async-io-runtime"),
        feature = "tokio-runtime"
    ))]
    runtime: tokio::runtime::Handle,
}

impl ResponseSender {
    fn fail(&self) {
        self.failed.store(true, Ordering::SeqCst);
        self.connection.shut_down();
    }

    /// send response with only header, no data
    pub(crate) async fn send1(&self, header: &fuse_out_header) {
        self.send(header.unique, &[IoSlice::new(header.as_bytes())])
            .await;
    }

    /// send response with header and data
    pub(crate) async fn send2(&self, header: &fuse_out_header, data: &[u8]) {
        self.send(
            header.unique,
            &[IoSlice::new(header.as_bytes()), IoSlice::new(data)],
        )
        .await;
    }

    /// send response with header and two parts of data.
    pub(crate) async fn send3(&self, header: &fuse_out_header, d1: &[u8], d2: &[u8]) {
        self.send(
            header.unique,
            &[
                IoSlice::new(header.as_bytes()),
                IoSlice::new(d1),
                IoSlice::new(d2),
            ],
        )
        .await;
    }

    /// Send the reply to request `unique` from borrowed buffers in a single write.
    async fn send(&self, unique: u64, bufs: &[IoSlice<'_>]) {
        let Err(err) = self.connection.write_vectored(bufs).await else {
            return;
        };

        match err.raw_os_error() {
            Some(libc::ENOENT) => {
                warn!(%err, unique, "no pending request for this reply, it may have been interrupted");
            }

            #[cfg(target_os = "linux")]
            Some(libc::EINVAL) => self.recover_rejected_reply(unique, &err).await,

            _ => {
                error!(%err, unique, "reply write failed");
                self.fail();
            }
        }
    }

    /// Linux v6.12 fuse_dev_do_write can reject a reply with EINVAL before matching its request.
    /// One header-only EIO completes the request if it is still pending.
    #[cfg(target_os = "linux")]
    async fn recover_rejected_reply(&self, unique: u64, err: &IoError) {
        let header = fuse_out_header {
            len: FUSE_OUT_HEADER_SIZE as u32,
            error: -libc::EIO,
            unique,
        };

        match self
            .connection
            .write_vectored(&[IoSlice::new(header.as_bytes())])
            .await
        {
            Ok(_) => warn!(%err, unique, "kernel rejected a reply, the request got EIO instead"),

            Err(fallback) if fallback.raw_os_error() == Some(libc::ENOENT) => {
                warn!(%err, unique, "kernel rejected a reply after completing its request");
            }

            Err(fallback) => {
                error!(%err, %fallback, unique, "kernel rejected a reply and its EIO fallback");
                self.fail();
            }
        }
    }

    /// send error response
    #[inline]
    async fn send_err(&self, request: &Request, err: Errno) {
        let out_header = fuse_out_header {
            len: FUSE_OUT_HEADER_SIZE as u32,
            error: err.reply_code(),
            unique: request.unique,
        };
        self.send1(&out_header).await
    }

    /// Send a notification in a single write. An error the kernel scopes to this notification
    /// is logged; any other error ends the session, as for replies.
    pub(crate) async fn send_notification(&self, bufs: &[IoSlice<'_>]) {
        let _ = self.try_send_notification(bufs).await;
    }

    pub(crate) async fn try_send_notification(&self, bufs: &[IoSlice<'_>]) -> IoResult<()> {
        #[cfg(target_os = "linux")]
        let result = self.write_notification(bufs).await;
        #[cfg(not(target_os = "linux"))]
        let result = self.connection.write_vectored(bufs).await;

        let err = match result {
            Ok(written) if written == bufs.iter().map(|buf| buf.len()).sum::<usize>() => {
                return Ok(());
            }
            Ok(_) => IoError::new(ErrorKind::WriteZero, "incomplete notification write"),
            Err(err) => err,
        };

        if notification_rejected_locally(&err) {
            warn!(%err, "kernel rejected a notification, the session continues");
        } else {
            error!(%err, "notification write failed");
            self.fail();
        }
        Err(err)
    }

    /// Linux v6.12 fuse_reverse_inval_entry waits for a parent inode lock that a pending lookup holds
    /// until its reply is written, so the write must not block an executor worker.
    #[cfg(target_os = "linux")]
    async fn write_notification(&self, bufs: &[IoSlice<'_>]) -> IoResult<usize> {
        let mut message = Vec::with_capacity(bufs.iter().map(|buf| buf.len()).sum());
        for buf in bufs {
            message.extend_from_slice(buf);
        }
        let connection = self.connection.clone();
        let write = move || connection.write_vectored_blocking(&[IoSlice::new(&message)]);

        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        return self
            .runtime
            .spawn_blocking(write)
            .await
            .map_err(|err| IoError::other(format!("notification write task failed: {err}")))?;

        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        task::spawn_blocking(write).await
    }
}

/// Whether a failed notification write left the connection usable.
fn notification_rejected_locally(err: &IoError) -> bool {
    // Linux v6.12 fuse_notify and fuse_reverse_inval_*: these fail only the notification.
    #[cfg(target_os = "linux")]
    const LOCAL: &[libc::c_int] = &[
        libc::ENOENT,
        libc::EINVAL,
        libc::ENAMETOOLONG,
        libc::ENOMEM,
        libc::ENOTDIR,
        libc::ENOTEMPTY,
        libc::EBUSY,
    ];
    // FreeBSD 15 fuse_device_write: unimplemented notification codes do not kill the session.
    #[cfg(target_os = "freebsd")]
    const LOCAL: &[libc::c_int] = &[libc::ENOENT, libc::ENOSYS];
    // ponytail: macFUSE notification errors are not verified; only ENOENT keeps the session, as before.
    #[cfg(not(any(target_os = "linux", target_os = "freebsd")))]
    const LOCAL: &[libc::c_int] = &[libc::ENOENT];

    err.raw_os_error()
        .is_some_and(|errno| LOCAL.contains(&errno))
}

/// How long shutdown waits for request handlers before cancelling them.
// ponytail: fixed until 0.11 can make it a mount option.
const CALLBACK_GRACE: Duration = Duration::from_secs(10);

/// Spawned handlers are kept until this many are tracked, then finished ones are dropped.
const REAP_AT: usize = 64;

/// The request handlers a session spawned, so shutdown can settle them before `destroy`.
#[derive(Debug)]
struct RequestTasks {
    running: Vec<JoinHandle<()>>,
    /// Reap when `running` reaches this length; doubling keeps reaping amortized O(1) per spawn.
    reap_at: usize,
    shared: Arc<RequestTasksShared>,
}

#[derive(Debug)]
struct RequestTasksShared {
    connection: Arc<FuseConnection>,
    panicked: AtomicBool,
}

impl RequestTasks {
    fn new(connection: Arc<FuseConnection>) -> Self {
        Self {
            running: Vec::new(),
            reap_at: REAP_AT,
            shared: Arc::new(RequestTasksShared {
                connection,
                panicked: AtomicBool::new(false),
            }),
        }
    }

    fn panicked(&self) -> bool {
        self.shared.panicked.load(Ordering::SeqCst)
    }

    /// A panic ends the session, because the handler's request never gets a reply.
    fn spawn<F>(&mut self, span: Span, fut: F)
    where
        F: Future<Output = ()> + Send + 'static,
    {
        if self.running.len() >= self.reap_at {
            self.running.retain(|task| !task.is_finished());
            self.reap_at = (self.running.len() * 2).max(REAP_AT);
        }

        let shared = self.shared.clone();
        let fut = AssertUnwindSafe(fut)
            .catch_unwind()
            .map(move |res| {
                if res.is_err() {
                    error!("request handler panicked, ending the session");
                    shared.panicked.store(true, Ordering::SeqCst);
                    shared.connection.shut_down();
                }
            })
            .instrument(span);

        self.running.push(task::spawn(fut));
    }

    async fn join_all(&mut self) {
        while let Some(task) = self.running.last_mut() {
            // The handler catches its own panic, so the join has nothing to report.
            let _ = task.await;
            self.running.pop();
        }
    }

    /// Awaits cancellation, so a cancelled handler no longer runs when this returns.
    #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
    async fn cancel_all(&mut self) {
        for task in &self.running {
            task.abort();
        }
        self.join_all().await;
    }

    /// Awaits cancellation, so a cancelled handler no longer runs when this returns.
    #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
    async fn cancel_all(&mut self) {
        for task in self.running.drain(..) {
            task.cancel().await;
        }
    }

    /// Waits up to `grace` for the handlers, then cancels the rest. Returns whether any panicked.
    async fn settle(&mut self, grace: Duration) -> bool {
        self.running.retain(|task| !task.is_finished());
        if self.running.is_empty() {
            return self.panicked();
        }

        let drained = {
            let join_all = pin!(self.join_all());
            let expired = pin!(grace_expired(grace));
            matches!(future::select(join_all, expired).await, Either::Left(_))
        };

        if !drained {
            warn!(
                remaining = self.running.len(),
                "request handlers outlived the shutdown grace period, cancelling them"
            );
            self.cancel_all().await;
        }

        self.panicked()
    }
}

/// Completes after `grace`. A thread, not a runtime timer, because the runtime may have timers disabled.
async fn grace_expired(grace: Duration) {
    let expired = Arc::new(async_notify::Notify::new());
    let notify = expired.clone();
    let spawned = std::thread::Builder::new()
        .name("fuse-shutdown-grace".into())
        .spawn(move || {
            std::thread::sleep(grace);
            notify.notify();
        });

    match spawned {
        Ok(_) => expired.notified().await,
        Err(err) => {
            warn!(%err, "no shutdown timer, waiting for request handlers without a deadline");
            future::pending().await
        }
    }
}

/// The spawned session task and how it ended.
#[derive(Debug)]
struct SessionTask {
    /// `None` once the [`MountHandle`] future has returned the session result.
    handle: Option<JoinHandle<IoResult<()>>>,
    /// Whether the session ended without an error, once the [`MountHandle`] future returned.
    ended_ok: Option<bool>,
    #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
    runtime: tokio::runtime::Handle,
}

#[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
fn session_result(result: IoResult<()>) -> IoResult<()> {
    result
}

#[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
fn session_result(result: Result<IoResult<()>, task::JoinError>) -> IoResult<()> {
    result.unwrap_or_else(|err| Err(IoError::other(format!("fuse session task failed: {err}"))))
}

#[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
async fn blocking<T, F>(f: F) -> IoResult<T>
where
    T: Send + 'static,
    F: FnOnce() -> T + Send + 'static,
{
    Ok(task::spawn_blocking(f).await)
}

#[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
async fn blocking<T, F>(f: F) -> IoResult<T>
where
    T: Send + 'static,
    F: FnOnce() -> T + Send + 'static,
{
    task::spawn_blocking(f)
        .await
        .map_err(|err| IoError::other(format!("blocking task failed: {err}")))
}

#[derive(Debug)]
struct MountHandleInner {
    task: SessionTask,
    mount_path: PathBuf,
    destroy_notify: Arc<async_notify::Notify>,
    #[cfg(any(
        all(target_os = "linux", feature = "unprivileged"),
        target_os = "macos"
    ))]
    unprivileged: bool,
}

impl MountHandleInner {
    /// Ends the session, then unmounts. A session error wins over an unmount error. With
    /// `keep_if_ended_ok`, a session that had already ended without an error is not unmounted.
    async fn inner_unmount(mut self, keep_if_ended_ok: bool) -> IoResult<()> {
        let ended = self
            .task
            .handle
            .as_ref()
            .is_none_or(|handle| handle.is_finished());
        self.destroy_notify.notify();

        let session = match self.task.handle.take() {
            Some(handle) => session_result(handle.await),
            // The MountHandle future already returned the session result.
            None => Ok(()),
        };
        let ended_ok = self.task.ended_ok.unwrap_or(session.is_ok());
        if keep_if_ended_ok && ended && ended_ok {
            return Ok(());
        }

        let unmounted = self.os_unmount().await;
        match session {
            Err(err) => {
                if let Err(unmount_err) = unmounted {
                    error!(%unmount_err, "unmount after a session error failed");
                }
                Err(err)
            }
            Ok(()) => unmounted,
        }
    }

    async fn os_unmount(&self) -> IoResult<()> {
        let mount_path = self.mount_path.clone();

        // TODO: freebsd mount is unprivileged, then unmount is unprivileged too?
        #[cfg(any(target_os = "freebsd", target_os = "macos"))]
        blocking(move || mount::unmount(&mount_path, MntFlags::MNT_SYNCHRONOUS)).await??;

        #[cfg(target_os = "linux")]
        {
            #[cfg(feature = "unprivileged")]
            if self.unprivileged {
                let binary_path = find_fusermount3()?;
                let mut child = Command::new(binary_path)
                    .args([OsStr::new("-u"), mount_path.as_os_str()])
                    .spawn()?;
                #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
                let status = child.status().await?;
                #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
                let status = child.wait().await?;
                if !status.success() {
                    return Err(IoError::other("call fusermount3 -u to unmount failed"));
                }

                return Ok(());
            }

            blocking(move || mount::umount(&mount_path)).await??;
        }

        Ok(())
    }
}

impl Future for MountHandle {
    type Output = IoResult<()>;

    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let task = &mut self.inner.as_mut().expect("inner should be Some()").task;
        let Some(handle) = task.handle.as_mut() else {
            return Poll::Ready(Err(IoError::other(
                "the session result was already returned",
            )));
        };

        let result = session_result(ready!(Pin::new(handle).poll(cx)));
        task.handle = None;
        task.ended_ok = Some(result.is_ok());

        Poll::Ready(result)
    }
}

#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
/// fuse filesystem session, inode based.
pub struct Session<FS> {
    fuse_connection: Option<Arc<FuseConnection>>,
    filesystem: Option<Arc<FS>>,
    response_sender: Option<Arc<ResponseSender>>,
    tasks: Option<RequestTasks>,
    callback_grace: Duration,
    /// Set once `Filesystem::init` succeeded, so a later failure still runs `destroy`.
    fs_initialized: bool,
    mount_options: MountOptions,
}

enum ReadResult {
    Destroy,
    Request {
        in_header: IoResult<fuse_in_header>,
        header_buffer: Vec<u8>,
        data_buffer: Vec<u8>,
    },
}

impl Debug for ReadResult {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            ReadResult::Destroy => f.debug_struct("ReadResult::Destroy").finish(),
            ReadResult::Request { in_header, .. } => f
                .debug_struct("ReadResult::Request")
                .field("in_header", in_header)
                .finish_non_exhaustive(),
        }
    }
}

#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
impl<FS> Session<FS> {
    /// new a fuse filesystem session.
    pub fn new(mount_options: MountOptions) -> Self {
        Self {
            fuse_connection: None,
            filesystem: None,
            response_sender: None,
            tasks: None,
            callback_grace: CALLBACK_GRACE,
            fs_initialized: false,
            mount_options,
        }
    }
}

#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
impl<FS: Filesystem + Send + Sync + 'static> Session<FS> {
    /// get a [`notify`].
    ///
    /// [`notify`]: Notify
    fn get_notify(&self) -> Notify {
        Notify::new(self.response_sender().clone())
    }

    async fn mount_empty_check(&self, mount_path: &Path) -> IoResult<()> {
        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        if !self.mount_options.nonempty
            && matches!(read_dir(mount_path).await?.next_entry().await, Ok(Some(_)))
        {
            return Err(IoError::new(
                ErrorKind::AlreadyExists,
                "mount point is not empty",
            ));
        }

        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        if !self.mount_options.nonempty && read_dir(mount_path).await?.next().await.is_some() {
            return Err(IoError::new(
                ErrorKind::AlreadyExists,
                "mount point is not empty",
            ));
        }

        Ok(())
    }

    /// mount the filesystem without root permission. This function will block
    /// until the filesystem is unmounted.
    // On FreeBSD, no special interface is required to mount unprivileged.
    // If vfs.usermount=1 and the user has access to the mountpoint, it will
    // just work.
    #[cfg(all(target_os = "freebsd", feature = "unprivileged"))]
    pub async fn mount_with_unprivileged<P: AsRef<Path>>(
        self,
        fs: FS,
        mount_path: P,
    ) -> IoResult<MountHandle> {
        self.mount(fs, mount_path).await
    }

    #[cfg(target_os = "macos")]
    pub async fn mount_with_unprivileged<P: AsRef<Path>>(
        mut self,
        fs: FS,
        mount_path: P,
    ) -> IoResult<MountHandle> {
        let mount_path = mount_path.as_ref();

        self.mount_empty_check(mount_path).await?;

        let notify = Arc::new(async_notify::Notify::new());
        let fuse_connection = FuseConnection::new_with_unprivileged(
            self.mount_options.clone(),
            mount_path,
            notify.clone(),
        )
        .await?;

        self.fuse_connection.replace(Arc::new(fuse_connection));

        self.filesystem.replace(Arc::new(fs));

        debug!("mount {:?} success", mount_path);

        Ok(MountHandle {
            inner: Some(MountHandleInner {
                task: self.start(),
                mount_path: mount_path.to_path_buf(),
                destroy_notify: notify,
                unprivileged: true,
            }),
        })
    }

    /// mount the filesystem without root permission.
    #[cfg(all(target_os = "linux", feature = "unprivileged"))]
    pub async fn mount_with_unprivileged<P: AsRef<Path>>(
        mut self,
        fs: FS,
        mount_path: P,
    ) -> IoResult<MountHandle> {
        let mount_path = mount_path.as_ref();

        self.mount_empty_check(mount_path).await?;

        let notify = Arc::new(async_notify::Notify::new());
        let fuse_connection = FuseConnection::new_with_unprivileged(
            self.mount_options.clone(),
            mount_path,
            notify.clone(),
        )
        .await?;

        self.fuse_connection.replace(Arc::new(fuse_connection));

        self.filesystem.replace(Arc::new(fs));

        debug!("mount {:?} success", mount_path);

        Ok(MountHandle {
            inner: Some(MountHandleInner {
                task: self.start(),
                mount_path: mount_path.to_path_buf(),
                destroy_notify: notify,
                unprivileged: true,
            }),
        })
    }

    /// mount the filesystem with root permission.
    #[cfg(target_os = "linux")]
    pub async fn mount<P: AsRef<Path>>(mut self, fs: FS, mount_path: P) -> IoResult<MountHandle> {
        let mount_path = mount_path.as_ref();

        self.mount_empty_check(mount_path).await?;

        let notify = Arc::new(async_notify::Notify::new());
        let fuse_connection = FuseConnection::new(notify.clone())?;

        let fd = fuse_connection.as_fd().as_raw_fd();

        let options = self.mount_options.build(fd);

        let fs_name = if let Some(fs_name) = self.mount_options.fs_name.as_ref() {
            Some(fs_name.as_str())
        } else {
            Some("fuse")
        };

        debug!("mount options {:?}", options);

        if let Err(err) = mount::mount(
            fs_name,
            mount_path,
            Some("fuse"),
            self.mount_options.flags(),
            Some(options.as_os_str()),
        ) {
            error!("mount {:?} failed", mount_path);

            return Err(err.into());
        }

        self.fuse_connection.replace(Arc::new(fuse_connection));

        self.filesystem.replace(Arc::new(fs));

        debug!("mount {:?} success", mount_path);

        Ok(MountHandle {
            inner: Some(MountHandleInner {
                task: self.start(),
                mount_path: mount_path.to_path_buf(),
                destroy_notify: notify,
                #[cfg(all(target_os = "linux", feature = "unprivileged"))]
                unprivileged: false,
            }),
        })
    }

    /// mount the filesystem
    #[cfg(target_os = "freebsd")]
    pub async fn mount<P: AsRef<Path>>(mut self, fs: FS, mount_path: P) -> IoResult<MountHandle> {
        let mount_path = mount_path.as_ref();

        self.mount_empty_check(mount_path).await?;

        let notify = Arc::new(async_notify::Notify::new());
        let fuse_connection = FuseConnection::new(notify.clone())?;

        let fd = fuse_connection.as_fd().as_raw_fd();

        {
            let mut nmount = self.mount_options.build();
            nmount
                .str_opt_owned(c"fspath", mount_path)
                .str_opt_owned(c"fd", format!("{fd}").as_str());
            debug!("mount options {:?}", &nmount);

            if let Err(err) = nmount.nmount(self.mount_options.flags()) {
                error!("mount {} failed: {}", mount_path.display(), err);

                return Err(std::io::Error::from(err));
            }
        }

        self.fuse_connection.replace(Arc::new(fuse_connection));

        self.filesystem.replace(Arc::new(fs));

        debug!("mount {:?} success", mount_path);

        Ok(MountHandle {
            inner: Some(MountHandleInner {
                task: self.start(),
                mount_path: mount_path.to_path_buf(),
                destroy_notify: notify,
            }),
        })
    }

    #[cfg(target_os = "macos")]
    pub async fn mount<P: AsRef<Path>>(self, fs: FS, mount_path: P) -> IoResult<MountHandle> {
        self.mount_with_unprivileged(fs, mount_path).await
    }

    /// Spawns the session. A panic in it becomes the session error.
    fn start(self) -> SessionTask {
        let session = AssertUnwindSafe(self.inner_mount())
            .catch_unwind()
            .map(|result| {
                result.unwrap_or_else(|_| {
                    error!("fuse session panicked");
                    Err(IoError::other("fuse session panicked"))
                })
            });

        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        let handle = task::spawn(session);

        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        let runtime = tokio::runtime::Handle::current();
        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        let handle = runtime.spawn(session);

        SessionTask {
            handle: Some(handle),
            ended_ok: None,
            #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
            runtime,
        }
    }

    /// get response sender, it should be called after mount, otherwise it will panic
    fn response_sender(&self) -> &Arc<ResponseSender> {
        self.response_sender
            .as_ref()
            .expect("response_sender should be Some()")
    }

    async fn inner_mount(mut self) -> IoResult<()> {
        let connection = self
            .fuse_connection
            .clone()
            .expect("mount stores the connection before spawning the session");

        let sender = Arc::new(ResponseSender {
            failed: AtomicBool::new(false),
            connection: connection.clone(),
            #[cfg(all(
                target_os = "linux",
                not(feature = "async-io-runtime"),
                feature = "tokio-runtime"
            ))]
            runtime: tokio::runtime::Handle::current(),
        });
        self.response_sender.replace(sender.clone());
        self.tasks.replace(RequestTasks::new(connection));

        let result = self.dispatch().await;

        if sender.failed.load(Ordering::SeqCst) {
            return Err(IoError::other("send response failed"));
        }

        result
    }

    fn spawn<F>(&mut self, span: Span, fut: F)
    where
        F: Future<Output = ()> + Send + 'static,
    {
        self.tasks
            .as_mut()
            .expect("inner_mount creates the request tasks before dispatch")
            .spawn(span, fut);
    }

    #[instrument(level = "debug", skip(self, fs), ret, err)]
    async fn init_filesystem(
        &mut self,
        fs: &FS,
        fuse_connection: &FuseConnection,
    ) -> IoResult<NonZeroU32> {
        let header_buffer = vec![0; FUSE_IN_HEADER_SIZE];
        let data_buffer = vec![0; FUSE_MIN_READ_BUFFER_SIZE];

        let (data_buffer, in_header) = match self
            .read_fuse_request(fuse_connection, header_buffer, data_buffer)
            .await
        {
            ReadResult::Destroy => {
                return Err(IoError::new(
                    ErrorKind::UnexpectedEof,
                    "init stage get destroy result",
                ));
            }

            ReadResult::Request {
                in_header,
                data_buffer,
                ..
            } => {
                let in_header = in_header?;
                (data_buffer, in_header)
            }
        };

        let request = Request::from(&in_header);

        let opcode = match fuse_opcode::try_from(in_header.opcode) {
            Err(err) => {
                debug!("receive unknown opcode {}", err.0);

                self.response_sender()
                    .send_err(&request, libc::ENOSYS.into())
                    .await;

                return Err(IoError::other(format!("receive unknown opcode {}", err.0)));
            }

            Ok(opcode) => opcode,
        };

        debug!("receive opcode {}", opcode);

        if opcode != fuse_opcode::FUSE_INIT {
            error!(?opcode, "received unexpected opcode");

            return Err(IoError::other(format!("unexpected opcode {opcode:?}")));
        }

        let data_size = in_header.len as usize - FUSE_IN_HEADER_SIZE;
        let data_ref = &data_buffer[..data_size];

        let max_write = self
            .handle_init(request, data_ref, fuse_connection, fs)
            .await?;
        let notify = self.get_notify();
        // An implementation can panic before it returns its future.
        if AssertUnwindSafe(async { fs.ready(notify).await })
            .catch_unwind()
            .await
            .is_err()
        {
            error!("filesystem ready panicked");
            return Err(IoError::other("filesystem ready panicked"));
        }
        Ok(max_write)
    }

    #[instrument(level = "debug", skip(self, header_buffer, data_buffer), ret)]
    async fn read_fuse_request(
        &mut self,
        fuse_connection: &FuseConnection,
        mut header_buffer: Vec<u8>,
        mut data_buffer: Vec<u8>,
    ) -> ReadResult {
        let n = loop {
            let res = match fuse_connection
                .read_vectored(header_buffer, data_buffer)
                .await
            {
                None => return ReadResult::Destroy,

                Some(((header_buf, data_buf), res)) => {
                    header_buffer = header_buf;
                    data_buffer = data_buf;

                    res
                }
            };

            match res {
                Ok(n) => break n,

                // libfuse 3.16.2 retries these too; ENOENT is a request interrupted before it was read.
                Err(err)
                    if matches!(
                        err.raw_os_error(),
                        Some(libc::EINTR | libc::EAGAIN | libc::ENOENT)
                    ) =>
                {
                    debug!(%err, "retrying /dev/fuse read");
                }

                // Linux v6.12 fuse_dev_do_read returns ECONNABORTED for a request dequeued as the
                // connection was aborted, which every unmount does.
                Err(err)
                    if matches!(err.raw_os_error(), Some(libc::ENODEV | libc::ECONNABORTED)) =>
                {
                    debug!(%err, "fuse connection closed");

                    return ReadResult::Destroy;
                }

                Err(err) => {
                    error!(%err, "read from /dev/fuse failed");

                    return ReadResult::Request {
                        in_header: Err(err),
                        header_buffer,
                        data_buffer,
                    };
                }
            }
        };

        debug!(n, "read fuse request done");

        if n < FUSE_IN_HEADER_SIZE {
            error!(
                n,
                FUSE_IN_HEADER_SIZE, "read_vectored n is less then FUSE_IN_HEADER_SIZE"
            );

            return ReadResult::Request {
                in_header: Err(IoError::new(
                    ErrorKind::InvalidData,
                    "read_vectored n is less then FUSE_IN_HEADER_SIZE",
                )),
                header_buffer,
                data_buffer,
            };
        }

        let in_header = match fuse_in_header::read_from_bytes(&header_buffer) {
            Err(err) => {
                error!("deserialize fuse_in_header failed {}", err);

                return ReadResult::Request {
                    in_header: Err(IoError::other("deserialize fuse_in_header failed")),
                    header_buffer,
                    data_buffer,
                };
            }

            Ok(in_header) => in_header,
        };

        // Dispatch slices the payload by `len`, so it must be exactly what was read.
        if in_header.len as usize != n {
            error!(
                n,
                len = in_header.len,
                unique = in_header.unique,
                "fuse_in_header len does not match the bytes read"
            );

            return ReadResult::Request {
                in_header: Err(IoError::new(
                    ErrorKind::InvalidData,
                    "fuse_in_header len does not match the bytes read",
                )),
                header_buffer,
                data_buffer,
            };
        }

        ReadResult::Request {
            in_header: Ok(in_header),
            header_buffer,
            data_buffer,
        }
    }

    async fn dispatch(&mut self) -> IoResult<()> {
        let fuse_connection = self.fuse_connection.take().unwrap();
        let fs = self.filesystem.take().expect("filesystem not init");

        let max_write = match self.init_filesystem(&fs, &fuse_connection).await {
            Ok(max_write) => max_write.get() as usize,
            Err(err) => {
                if self.fs_initialized {
                    fs.destroy(Request::default()).await;
                    debug!("fuse destroyed");
                }
                return Err(err);
            }
        };
        let buffer_size = (max_write + FUSE_WRITE_IN_SIZE).max(FUSE_MIN_READ_BUFFER_SIZE);

        let header_buffer = vec![0; FUSE_IN_HEADER_SIZE];
        let data_buffer = vec![0; buffer_size];

        let (destroy_request, mut result) = self
            .serve(&fuse_connection, fs.clone(), header_buffer, data_buffer)
            .await;

        let tasks = self
            .tasks
            .as_mut()
            .expect("inner_mount creates the request tasks before dispatch");
        if tasks.settle(self.callback_grace).await && result.is_ok() {
            result = Err(IoError::other("request handler panicked"));
        }

        fs.destroy(destroy_request).await;
        debug!("fuse destroyed");

        result
    }

    /// Reads and dispatches requests until the session should end. Returns the request to
    /// pass to `destroy` and the session result.
    async fn serve(
        &mut self,
        fuse_connection: &FuseConnection,
        fs: Arc<FS>,
        mut header_buffer: Vec<u8>,
        mut data_buffer: Vec<u8>,
    ) -> (Request, IoResult<()>) {
        loop {
            let in_header = match self
                .read_fuse_request(fuse_connection, header_buffer, data_buffer)
                .await
            {
                ReadResult::Destroy => return (Request::default(), Ok(())),

                ReadResult::Request {
                    in_header,
                    header_buffer: header_buf,
                    data_buffer: data_buf,
                } => {
                    header_buffer = header_buf;
                    data_buffer = data_buf;

                    match in_header {
                        Err(err) => return (Request::default(), Err(err)),

                        Ok(in_header) => in_header,
                    }
                }
            };

            let request = Request::from(&in_header);

            let opcode = match fuse_opcode::try_from(in_header.opcode) {
                Err(err) => {
                    debug!("receive unknown opcode {}", err.0);

                    self.response_sender()
                        .send_err(&request, libc::ENOSYS.into())
                        .await;

                    continue;
                }

                Ok(opcode) => opcode,
            };

            debug!("receive opcode {}", opcode);

            let data_size = in_header.len as usize - FUSE_IN_HEADER_SIZE;
            let data_ref = &data_buffer[..data_size];

            match opcode {
                fuse_opcode::FUSE_INIT => {
                    warn!("duplicated fuse init request");

                    if let Err(err) = self
                        .handle_init(request, data_ref, fuse_connection, &fs)
                        .await
                    {
                        return (Request::default(), Err(err));
                    }
                }

                fuse_opcode::FUSE_DESTROY => {
                    debug!("receive fuse destroy");

                    return (request, Ok(()));
                }

                fuse_opcode::FUSE_LOOKUP => {
                    self.handle_lookup(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_FORGET => {
                    self.handle_forget(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_GETATTR => {
                    self.handle_getattr(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_SETATTR => {
                    self.handle_setattr(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_READLINK => {
                    self.handle_readlink(request, in_header, &fs).await;
                }

                fuse_opcode::FUSE_SYMLINK => {
                    self.handle_symlink(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_MKNOD => {
                    self.handle_mknod(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_MKDIR => {
                    self.handle_mkdir(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_UNLINK => {
                    self.handle_unlink(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_RMDIR => {
                    self.handle_rmdir(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_RENAME => {
                    self.handle_rename(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_LINK => {
                    self.handle_link(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_OPEN => {
                    self.handle_open(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_READ => {
                    self.handle_read(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_WRITE => {
                    self.handle_write(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_STATFS => {
                    self.handle_statfs(request, in_header, &fs).await;
                }

                fuse_opcode::FUSE_RELEASE => {
                    self.handle_release(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_FSYNC => {
                    self.handle_fsync(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_SETXATTR => {
                    self.handle_setxattr(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_GETXATTR => {
                    self.handle_getxattr(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_LISTXATTR => {
                    self.handle_listxattr(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_REMOVEXATTR => {
                    self.handle_removexattr(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_FLUSH => {
                    self.handle_flush(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_OPENDIR => {
                    self.handle_opendir(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_READDIR => {
                    self.handle_readdir(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_RELEASEDIR => {
                    self.handle_releasedir(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_FSYNCDIR => {
                    self.handle_fsyncdir(request, in_header, data_ref, &fs)
                        .await;
                }

                #[cfg(feature = "file-lock")]
                fuse_opcode::FUSE_GETLK => {
                    self.handle_getlk(request, in_header, data_ref, &fs).await;
                }

                #[cfg(feature = "file-lock")]
                fuse_opcode::FUSE_SETLK | fuse_opcode::FUSE_SETLKW => {
                    self.handle_setlk(
                        request,
                        in_header,
                        data_ref,
                        opcode == fuse_opcode::FUSE_SETLKW,
                        &fs,
                    )
                    .await;
                }

                fuse_opcode::FUSE_ACCESS => {
                    self.handle_access(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_CREATE => {
                    self.handle_create(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_INTERRUPT => {
                    self.handle_interrupt(request, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_BMAP => {
                    self.handle_bmap(request, in_header, data_ref, &fs).await;
                }

                /*fuse_opcode::FUSE_IOCTL => {
                    let mut resp_sender = self.response_sender().clone();

                    let ioctl_in = match fuse_ioctl_in::read_from_prefix(data) {
                        Err(err) => {
                            error!("deserialize fuse_ioctl_in failed {}", err);

                             self.response_sender2.send_err(&request, libc::EINVAL.into()).await;

                            continue;
                        }

                        Ok(ioctl_in) => ioctl_in,
                    };

                    let ioctl_data = (&data[FUSE_IOCTL_IN_SIZE..]).to_vec();

                    let fs = fs.clone();
                }*/
                fuse_opcode::FUSE_POLL => {
                    self.handle_poll(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_NOTIFY_REPLY => {
                    self.handle_notify_reply(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_BATCH_FORGET => {
                    self.handle_batch_forget(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_FALLOCATE => {
                    self.handle_fallocate(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_READDIRPLUS => {
                    self.handle_readdirplus(request, in_header, data_ref, &fs)
                        .await;
                }

                fuse_opcode::FUSE_RENAME2 => {
                    self.handle_rename2(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_LSEEK => {
                    self.handle_lseek(request, in_header, data_ref, &fs).await;
                }

                fuse_opcode::FUSE_COPY_FILE_RANGE => {
                    self.handle_copy_file_range(request, in_header, data_ref, &fs)
                        .await;
                }

                #[cfg(target_os = "macos")]
                fuse_opcode::FUSE_SETVOLNAME => {}

                #[cfg(target_os = "macos")]
                fuse_opcode::FUSE_GETXTIMES => {}

                #[cfg(target_os = "macos")]
                fuse_opcode::FUSE_EXCHANGE => {} // fuse_opcode::CUSE_INIT => {}
            }
        }
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_init(
        &mut self,
        request: Request,
        data: &[u8],
        fuse_connection: &FuseConnection,
        fs: &FS,
    ) -> IoResult<NonZeroU32> {
        let init_in = match fuse_init_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_init_in failed {}, request unique {}",
                    err, request.unique
                );

                let init_out_header = fuse_out_header {
                    len: FUSE_OUT_HEADER_SIZE as u32,
                    error: -libc::EINVAL,
                    unique: request.unique,
                };

                if let Err(err) = fuse_connection
                    .write_vectored(&[IoSlice::new(init_out_header.as_bytes())])
                    .await
                {
                    error!("write error init out data to /dev/fuse failed {}", err);
                }

                return Err(IoError::from_raw_os_error(libc::EINVAL));
            }

            Ok((init_in, _)) => init_in,
        };

        debug!("fuse_init {:?}", init_in);

        let mut reply_flags = 0;

        // TODO: most of these FUSE_* flags should be controllable by the consuming crate.
        if init_in.flags & FUSE_ASYNC_READ > 0 {
            debug!("enable FUSE_ASYNC_READ");

            reply_flags |= FUSE_ASYNC_READ;
        }

        #[cfg(feature = "file-lock")]
        if init_in.flags & FUSE_POSIX_LOCKS > 0 {
            debug!("enable FUSE_POSIX_LOCKS");

            reply_flags |= FUSE_POSIX_LOCKS;
        }

        if init_in.flags & FUSE_FILE_OPS > 0 {
            debug!("enable FUSE_FILE_OPS");

            reply_flags |= FUSE_FILE_OPS;
        }

        if init_in.flags & FUSE_ATOMIC_O_TRUNC > 0 {
            debug!("enable FUSE_ATOMIC_O_TRUNC");

            reply_flags |= FUSE_ATOMIC_O_TRUNC;
        }

        if init_in.flags & FUSE_EXPORT_SUPPORT > 0 {
            debug!("enable FUSE_EXPORT_SUPPORT");

            reply_flags |= FUSE_EXPORT_SUPPORT;
        }

        if init_in.flags & FUSE_BIG_WRITES > 0 {
            debug!("enable FUSE_BIG_WRITES");

            reply_flags |= FUSE_BIG_WRITES;
        }

        if init_in.flags & FUSE_DONT_MASK > 0 && self.mount_options.dont_mask {
            debug!("enable FUSE_DONT_MASK");

            reply_flags |= FUSE_DONT_MASK;
        }

        #[cfg(not(target_os = "macos"))]
        if init_in.flags & FUSE_SPLICE_WRITE > 0 {
            debug!("enable FUSE_SPLICE_WRITE");

            reply_flags |= FUSE_SPLICE_WRITE;
        }

        #[cfg(not(target_os = "macos"))]
        if init_in.flags & FUSE_SPLICE_MOVE > 0 {
            debug!("enable FUSE_SPLICE_MOVE");

            reply_flags |= FUSE_SPLICE_MOVE;
        }

        #[cfg(not(target_os = "macos"))]
        if init_in.flags & FUSE_SPLICE_READ > 0 {
            debug!("enable FUSE_SPLICE_READ");

            reply_flags |= FUSE_SPLICE_READ;
        }

        // posix lock used, maybe we don't need bsd lock
        /*if init_in.flags&FUSE_FLOCK_LOCKS>0 {
            reply_flags |= FUSE_FLOCK_LOCKS;
        }*/

        /*if init_in.flags & FUSE_HAS_IOCTL_DIR > 0 {
            debug!("enable FUSE_HAS_IOCTL_DIR");

            reply_flags |= FUSE_HAS_IOCTL_DIR;
        }*/

        if init_in.flags & FUSE_AUTO_INVAL_DATA > 0 {
            debug!("enable FUSE_AUTO_INVAL_DATA");

            reply_flags |= FUSE_AUTO_INVAL_DATA;
        }

        if init_in.flags & FUSE_DO_READDIRPLUS > 0 || self.mount_options.force_readdir_plus {
            debug!("enable FUSE_DO_READDIRPLUS");

            reply_flags |= FUSE_DO_READDIRPLUS;
        }

        if init_in.flags & FUSE_READDIRPLUS_AUTO > 0 && !self.mount_options.force_readdir_plus {
            debug!("enable FUSE_READDIRPLUS_AUTO");

            reply_flags |= FUSE_READDIRPLUS_AUTO;
        }

        if init_in.flags & FUSE_ASYNC_DIO > 0 {
            debug!("enable FUSE_ASYNC_DIO");

            reply_flags |= FUSE_ASYNC_DIO;
        }

        if init_in.flags & FUSE_WRITEBACK_CACHE > 0 && self.mount_options.write_back {
            debug!("enable FUSE_WRITEBACK_CACHE");

            reply_flags |= FUSE_WRITEBACK_CACHE;
        }

        if init_in.flags & FUSE_NO_OPEN_SUPPORT > 0 && self.mount_options.no_open_support {
            debug!("enable FUSE_NO_OPEN_SUPPORT");

            reply_flags |= FUSE_NO_OPEN_SUPPORT;
        }

        if init_in.flags & FUSE_PARALLEL_DIROPS > 0 {
            debug!("enable FUSE_PARALLEL_DIROPS");

            reply_flags |= FUSE_PARALLEL_DIROPS;
        }

        if init_in.flags & FUSE_HANDLE_KILLPRIV > 0 && self.mount_options.handle_killpriv {
            debug!("enable FUSE_HANDLE_KILLPRIV");

            reply_flags |= FUSE_HANDLE_KILLPRIV;
        }

        if init_in.flags & FUSE_POSIX_ACL > 0 && self.mount_options.default_permissions {
            debug!("enable FUSE_POSIX_ACL");

            reply_flags |= FUSE_POSIX_ACL;
        }

        if init_in.flags & FUSE_MAX_PAGES > 0 {
            debug!("enable FUSE_MAX_PAGES");

            reply_flags |= FUSE_MAX_PAGES;
        }

        if init_in.flags & FUSE_CACHE_SYMLINKS > 0 {
            debug!("enable FUSE_CACHE_SYMLINKS");

            reply_flags |= FUSE_CACHE_SYMLINKS;
        }

        if init_in.flags & FUSE_NO_OPENDIR_SUPPORT > 0 && self.mount_options.no_open_dir_support {
            debug!("enable FUSE_NO_OPENDIR_SUPPORT");

            reply_flags |= FUSE_NO_OPENDIR_SUPPORT;
        }

        #[cfg(target_os = "macos")]
        if init_in.flags & FUSE_ALLOCATE > 0 {
            debug!("enable FUSE_ALLOCATE");

            reply_flags |= FUSE_ALLOCATE;
        }

        #[cfg(target_os = "macos")]
        if init_in.flags & FUSE_EXCHANGE_DATA > 0 {
            debug!("enable FUSE_EXCHANGE_DATA");

            reply_flags |= FUSE_EXCHANGE_DATA;
        }

        #[cfg(target_os = "macos")]
        if init_in.flags & FUSE_CASE_INSENSITIVE > 0 {
            debug!("enable FUSE_CASE_INSENSITIVE");

            reply_flags |= FUSE_CASE_INSENSITIVE;
        }

        #[cfg(target_os = "macos")]
        if init_in.flags & FUSE_VOL_RENAME > 0 {
            debug!("enable FUSE_VOL_RENAME");

            reply_flags |= FUSE_VOL_RENAME;
        }

        #[cfg(target_os = "macos")]
        if init_in.flags & FUSE_XTIMES > 0 {
            debug!("enable FUSE_XTIMES");

            reply_flags |= FUSE_XTIMES;
        }

        // TODO: pass init_in to init, so the file system will know which flags are in use.
        let reply = match fs.init(request).await {
            Err(err) => {
                let init_out_header = fuse_out_header {
                    len: FUSE_OUT_HEADER_SIZE as u32,
                    error: err.reply_code(),
                    unique: request.unique,
                };

                if let Err(err) = fuse_connection
                    .write_vectored(&[IoSlice::new(init_out_header.as_bytes())])
                    .await
                {
                    error!("write error init out data to /dev/fuse failed {}", err);
                }

                return Err(err.into());
            }

            Ok(reply) => reply,
        };
        self.fs_initialized = true;

        let init_out = fuse_init_out {
            major: FUSE_KERNEL_VERSION,
            minor: FUSE_KERNEL_MINOR_VERSION,
            max_readahead: init_in.max_readahead,
            flags: reply_flags,
            max_background: reply.max_background.unwrap_or(DEFAULT_MAX_BACKGROUND),
            congestion_threshold: reply
                .congestion_threshold
                .unwrap_or(DEFAULT_CONGESTION_THRESHOLD),
            max_write: reply.max_write.get(),
            time_gran: DEFAULT_TIME_GRAN,
            max_pages: DEFAULT_MAX_PAGES,
            map_alignment: DEFAULT_MAP_ALIGNMENT,
            unused: [0; 8],
        };

        debug!("fuse init out {:?}", init_out);

        let out_header = fuse_out_header {
            len: (FUSE_OUT_HEADER_SIZE + FUSE_INIT_OUT_SIZE) as u32,
            error: 0,
            unique: request.unique,
        };

        if let Err(err) = fuse_connection
            .write_vectored(&[
                IoSlice::new(out_header.as_bytes()),
                IoSlice::new(init_out.as_bytes()),
            ])
            .await
        {
            error!("write init out data to /dev/fuse failed {}", err);

            return Err(err);
        }

        debug!("fuse init done");

        Ok(reply.max_write)
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_lookup(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let name = match get_first_null_position(data) {
            None => {
                error!("lookup body has no null, request unique {}", request.unique);

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_lookup"), async move {
            debug!(
                "lookup unique {} name {:?} in parent {}",
                request.unique, name, in_header.nodeid
            );

            match fs.lookup(request, in_header.nodeid, &name).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }
                Ok(entry) => {
                    let entry_out: fuse_entry_out = entry.into();

                    debug!("lookup response {:?}", entry_out);

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, entry_out.as_bytes()).await;
                }
            };
        });
    }

    /// if Ok(true), quit the dispatch
    #[instrument(skip(self, data, fs))]
    async fn handle_forget(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let forget_in = match fuse_forget_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_forget_in failed {}, request unique {}",
                    err, request.unique
                );

                // no need to reply
                return;
            }

            Ok((forget_in, _)) => forget_in,
        };

        let fs = fs.clone();

        self.spawn(debug_span!("fuse_forget"), async move {
            debug!(
                "forget unique {} inode {} nlookup {}",
                request.unique, in_header.nodeid, forget_in.nlookup
            );

            fs.forget(request, in_header.nodeid, forget_in.nlookup)
                .await
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_getattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let getattr_in = match fuse_getattr_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_forget_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((getattr_in, _)) => getattr_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_getattr"), async move {
            debug!(
                "getattr unique {} inode {}",
                request.unique, in_header.nodeid
            );

            let fh = if getattr_in.getattr_flags & FUSE_GETATTR_FH > 0 {
                Some(getattr_in.fh)
            } else {
                None
            };

            match fs
                .getattr(request, in_header.nodeid, fh, getattr_in.getattr_flags)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }
                Ok(attr) => {
                    let attr_out = fuse_attr_out {
                        attr_valid: attr.ttl.as_secs(),
                        attr_valid_nsec: attr.ttl.subsec_nanos(),
                        dummy: getattr_in.dummy,
                        attr: attr.attr.into(),
                    };

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ATTR_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, attr_out.as_bytes()).await;
                }
            };
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_setattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let setattr_in = match fuse_setattr_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_setattr_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((setattr_in, _)) => setattr_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_setattr"), async move {
            let set_attr = SetAttr::from(&setattr_in);

            let fh = if setattr_in.valid & FATTR_FH > 0 {
                Some(setattr_in.fh)
            } else {
                None
            };

            debug!(
                "setattr unique {} inode {} set_attr {:?}",
                request.unique, in_header.nodeid, set_attr
            );

            match fs.setattr(request, in_header.nodeid, fh, set_attr).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }
                Ok(attr) => {
                    let attr_out: fuse_attr_out = attr.into();

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ATTR_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, attr_out.as_bytes()).await;
                }
            };
        });
    }

    #[instrument(skip(self, fs))]
    async fn handle_readlink(&mut self, request: Request, in_header: fuse_in_header, fs: &Arc<FS>) {
        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_readlink"), async move {
            debug!(
                "readlink unique {} inode {}",
                request.unique, in_header.nodeid
            );

            match fs.readlink(request, in_header.nodeid).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }
                Ok(data) => {
                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + data.data.len()) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, &data.data).await;
                }
            };
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_symlink(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        mut data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (name, first_null_index) = match get_first_null_position(data) {
            None => {
                error!("symlink has no null, request unique {}", request.unique);

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => (OsString::from_vec(data[..index].to_vec()), index),
        };

        data = &data[first_null_index + 1..];

        let link_name = match get_first_null_position(data) {
            None => {
                error!(
                    "symlink has no second null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_symlink"), async move {
            debug!(
                "symlink unique {} parent {} name {:?} link {:?}",
                request.unique, in_header.nodeid, name, link_name
            );

            match fs
                .symlink(request, in_header.nodeid, &name, &link_name)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }

                Ok(entry) => {
                    let entry_out: fuse_entry_out = entry.into();

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, entry_out.as_bytes()).await;
                }
            };
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_mknod(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (mknod_in, data) = match fuse_mknod_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_mknod_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_mknod_in body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_mknod"), async move {
            debug!(
                "mknod unique {} parent {} name {:?} {:?}",
                request.unique, in_header.nodeid, name, mknod_in
            );

            match fs
                .mknod(
                    request,
                    in_header.nodeid,
                    &name,
                    mknod_in.mode,
                    mknod_in.rdev,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }

                Ok(entry) => {
                    let entry_out: fuse_entry_out = entry.into();

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, entry_out.as_bytes()).await;
                }
            }
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_mkdir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (mkdir_in, data) = match fuse_mkdir_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_mknod_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "deserialize fuse_mknod_in doesn't have null unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_mkdir"), async move {
            debug!(
                "mkdir unique {} parent {} name {:?} {:?}",
                request.unique, in_header.nodeid, name, mkdir_in
            );

            match fs
                .mkdir(
                    request,
                    in_header.nodeid,
                    &name,
                    mkdir_in.mode,
                    mkdir_in.umask,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }

                Ok(entry) => {
                    let entry_out: fuse_entry_out = entry.into();

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    let mut data = Vec::with_capacity(FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE);

                    out_header.write_to_io(&mut data).unwrap();
                    entry_out.write_to_io(&mut data).unwrap();

                    resp_sender.send2(&out_header, entry_out.as_bytes()).await;
                }
            }
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_unlink(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "unlink body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_unlink"), async move {
            debug!(
                "unlink unique {} parent {} name {:?}",
                request.unique, in_header.nodeid, name
            );

            let resp_value = if let Err(err) = fs.unlink(request, in_header.nodeid, &name).await {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_rmdir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "rmdir body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_rmdir"), async move {
            debug!(
                "rmdir unique {} parent {} name {:?}",
                request.unique, in_header.nodeid, name
            );

            let resp_value = if let Err(err) = fs.rmdir(request, in_header.nodeid, &name).await {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_rename(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (rename_in, data) = match fuse_rename_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_rename_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let (name, first_null_index) = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_rename_in body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => (OsString::from_vec(data[..index].to_vec()), index),
        };

        let data = &data[first_null_index + 1..];

        let new_name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_rename_in body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_rename"), async move {
            debug!(
                "rename unique {} parent {} name {:?} new parent {} new name {:?}",
                request.unique, in_header.nodeid, name, rename_in.newdir, new_name
            );

            let resp_value = if let Err(err) = fs
                .rename(
                    request,
                    in_header.nodeid,
                    &name,
                    rename_in.newdir,
                    &new_name,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_link(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (link_in, data) = match fuse_link_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_link_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((link_in, data)) => (link_in, data),
        };

        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_link_in body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_link"), async move {
            debug!(
                "link unique {} inode {} new parent {} new name {:?}",
                request.unique, link_in.oldnodeid, in_header.nodeid, name
            );

            match fs
                .link(request, link_in.oldnodeid, in_header.nodeid, &name)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                }

                Ok(entry) => {
                    let entry_out: fuse_entry_out = entry.into();

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, entry_out.as_bytes()).await;
                }
            }
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_open(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let open_in = match fuse_open_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_open_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((open_in, _)) => open_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_open"), async move {
            debug!(
                "open unique {} inode {} flags {}",
                request.unique, in_header.nodeid, open_in.flags
            );

            let opened = match fs.open(request, in_header.nodeid, open_in.flags).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(opened) => opened,
            };

            let open_out: fuse_open_out = opened.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_OPEN_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, open_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_read(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let read_in = match fuse_read_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_read_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((read_in, _)) => read_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_read"), async move {
            debug!(
                "read unique {} inode {} {:?}",
                request.unique, in_header.nodeid, read_in
            );

            let mut reply_data = match fs
                .read(
                    request,
                    in_header.nodeid,
                    read_in.fh,
                    read_in.offset,
                    read_in.size,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_data) => reply_data.data,
            };

            if reply_data.len() > read_in.size as _ {
                reply_data.truncate(read_in.size as _);
            }

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + reply_data.len()) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, &reply_data).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_write(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (write_in, data) = match fuse_write_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_write_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        if write_in.size as usize != data.len() {
            error!("fuse_write_in body len is invalid");

            self.response_sender()
                .send_err(&request, libc::EINVAL.into())
                .await;

            return;
        }

        let data = data.to_vec();

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_write"), async move {
            debug!(
                "write unique {} inode {} {:?}",
                request.unique, in_header.nodeid, write_in
            );

            let reply_write = match fs
                .write(
                    request,
                    in_header.nodeid,
                    write_in.fh,
                    write_in.offset,
                    &data,
                    write_in.write_flags,
                    write_in.flags,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_write) => reply_write,
            };

            let write_out: fuse_write_out = reply_write.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_WRITE_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, write_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, fs))]
    async fn handle_statfs(&mut self, request: Request, in_header: fuse_in_header, fs: &Arc<FS>) {
        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_statfs"), async move {
            debug!(
                "statfs unique {} inode {}",
                request.unique, in_header.nodeid
            );

            let fs_stat = match fs.statfs(request, in_header.nodeid).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(fs_stat) => fs_stat,
            };

            let statfs_out: fuse_statfs_out = fs_stat.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_STATFS_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender.send2(&out_header, statfs_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_release(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let release_in = match fuse_release_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_release_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((release_in, _)) => release_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_release"), async move {
            let flush = release_in.release_flags & FUSE_RELEASE_FLUSH > 0;

            debug!(
                "release unique {} inode {} fh {} flags {} lock_owner {} flush {}",
                request.unique,
                in_header.nodeid,
                release_in.fh,
                release_in.flags,
                release_in.lock_owner,
                flush
            );

            let resp_value = if let Err(err) = fs
                .release(
                    request,
                    in_header.nodeid,
                    release_in.fh,
                    release_in.flags,
                    release_in.lock_owner,
                    flush,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_fsync(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let fsync_in = match fuse_fsync_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_fsync_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((fsync_in, _)) => fsync_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_fsync"), async move {
            let data_sync = fsync_in.fsync_flags & 1 > 0;

            debug!(
                "fsync unique {} inode {} fh {} data_sync {}",
                request.unique, in_header.nodeid, fsync_in.fh, data_sync
            );

            let resp_value = if let Err(err) = fs
                .fsync(request, in_header.nodeid, fsync_in.fh, data_sync)
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_setxattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (setxattr_in, data) = match fuse_setxattr_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_setxattr_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let (name, first_null_index) = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_setxattr_in body has no null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => (OsString::from_vec(data[..index].to_vec()), index),
        };

        let data = &data[first_null_index + 1..];

        // setxattr "size" field specifies size of only "Value" part of data
        if setxattr_in.size as usize != data.len() {
            error!(
                "fuse_setxattr_in value field data length is not right, request unique {} setxattr_in.size={} data.len={}",
                request.unique,
                setxattr_in.size,
                data.len()
            );

            self.response_sender()
                .send_err(&request, libc::EINVAL.into())
                .await;

            return;
        }

        let data = data.to_vec();

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_setxattr"), async move {
            debug!(
                "setxattr unique {} inode {}",
                request.unique, in_header.nodeid
            );

            // TODO handle os X argument
            let resp_value = if let Err(err) = fs
                .setxattr(
                    request,
                    in_header.nodeid,
                    &name,
                    &data,
                    setxattr_in.flags,
                    0,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_getxattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (getxattr_in, data) = match fuse_getxattr_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_getxattr_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let name = match get_first_null_position(data) {
            None => {
                error!("fuse_getxattr_in body has no null {}", request.unique);

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_getxattr"), async move {
            debug!(
                "getxattr unique {} inode {}",
                request.unique, in_header.nodeid
            );

            let xattr = match fs
                .getxattr(request, in_header.nodeid, &name, getxattr_in.size)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(xattr) => xattr,
            };

            if !xattr_reply_fits(getxattr_in.size, &xattr) {
                error!(
                    unique = request.unique,
                    size = getxattr_in.size,
                    data_len = ?xattr_data_len(&xattr),
                    "getxattr reply does not fit the requested size, replying EIO"
                );

                resp_sender.send_err(&request, libc::EIO.into()).await;
                return;
            }

            match xattr {
                ReplyXAttr::Size(size) => {
                    let getxattr_out = fuse_getxattr_out { size, _padding: 0 };

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_GETXATTR_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender
                        .send2(&out_header, getxattr_out.as_bytes())
                        .await;
                }

                ReplyXAttr::Data(xattr_data) => {
                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + xattr_data.len()) as u32,
                        error: 0,
                        unique: request.unique,
                    };
                    resp_sender.send2(&out_header, &xattr_data).await;
                }
            };
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_listxattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let listxattr_in = match fuse_getxattr_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_getxattr_in in listxattr failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((listxattr_in, _)) => listxattr_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_listxattr"), async move {
            debug!(
                "listxattr unique {} inode {} size {}",
                request.unique, in_header.nodeid, listxattr_in.size
            );

            let xattr = match fs
                .listxattr(request, in_header.nodeid, listxattr_in.size)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(xattr) => xattr,
            };

            if !xattr_reply_fits(listxattr_in.size, &xattr) {
                error!(
                    unique = request.unique,
                    size = listxattr_in.size,
                    data_len = ?xattr_data_len(&xattr),
                    "listxattr reply does not fit the requested size, replying EIO"
                );

                resp_sender.send_err(&request, libc::EIO.into()).await;
                return;
            }

            match xattr {
                ReplyXAttr::Size(size) => {
                    let getxattr_out = fuse_getxattr_out { size, _padding: 0 };

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_GETXATTR_OUT_SIZE) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender
                        .send2(&out_header, getxattr_out.as_bytes())
                        .await;
                }

                ReplyXAttr::Data(xattr_data) => {
                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + xattr_data.len()) as u32,
                        error: 0,
                        unique: request.unique,
                    };

                    resp_sender.send2(&out_header, &xattr_data).await;
                }
            };
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_removexattr(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse removexattr body has no null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_removexattr"), async move {
            debug!(
                "removexattr unique {} inode {}",
                request.unique, in_header.nodeid
            );

            let resp_value =
                if let Err(err) = fs.removexattr(request, in_header.nodeid, &name).await {
                    err.reply_code()
                } else {
                    0
                };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_flush(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let flush_in = match fuse_flush_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_flush_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((flush_in, _)) => flush_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_flush"), async move {
            debug!(
                "flush unique {} inode {} fh {} lock_owner {}",
                request.unique, in_header.nodeid, flush_in.fh, flush_in.lock_owner
            );

            let resp_value = if let Err(err) = fs
                .flush(request, in_header.nodeid, flush_in.fh, flush_in.lock_owner)
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_opendir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let open_in = match fuse_open_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_open_in in opendir failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((open_in, _)) => open_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_opendir"), async move {
            debug!(
                "opendir unique {} inode {} flags {}",
                request.unique, in_header.nodeid, open_in.flags
            );

            let reply_open = match fs.opendir(request, in_header.nodeid, open_in.flags).await {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_open) => reply_open,
            };

            let open_out: fuse_open_out = reply_open.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_OPEN_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender.send2(&out_header, open_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_readdir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        if self.mount_options.force_readdir_plus {
            self.response_sender()
                .send_err(&request, libc::ENOSYS.into())
                .await;

            return;
        }

        let read_in = match fuse_read_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_read_in in readdir failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;
                return;
            }

            Ok((read_in, _)) => read_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_readdir"), async move {
            debug!(
                "readdir unique {} inode {} fh {} offset {}",
                request.unique, in_header.nodeid, read_in.fh, read_in.offset
            );

            let reply_readdir = match fs
                .readdir(request, in_header.nodeid, read_in.fh, read_in.offset as i64)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_readdir) => reply_readdir,
            };

            let max_size = read_in.size as usize;

            let mut entry_data = Vec::with_capacity(max_size);

            let entries = reply_readdir.entries;
            let mut entries = pin!(entries);

            while let Some(entry) = entries.next().await {
                let entry = match entry {
                    Err(err) => {
                        resp_sender.send_err(&request, err).await;
                        return;
                    }

                    Ok(entry) => entry,
                };

                let name = &entry.name;

                let dir_entry_size = FUSE_DIRENT_SIZE + name.len();

                let padding_size = get_padding_size(dir_entry_size);

                // the padding is sent too, so it must fit as well
                if !dir_entry_fits(entry_data.len(), dir_entry_size, max_size) {
                    break;
                }

                let dir_entry = fuse_dirent {
                    ino: entry.inode,
                    off: entry.offset as u64,
                    namelen: name.len() as u32,
                    // learn from fuse-rs and golang bazil.org fuse DirentType
                    r#type: mode_from_kind_and_perm(entry.kind, 0) >> 12,
                };

                dir_entry.write_to_io(&mut entry_data).unwrap();

                entry_data.extend_from_slice(name.as_bytes());

                // padding
                entry_data.resize(entry_data.len() + padding_size, 0);
            }

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + entry_data.len()) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender.send2(&out_header, &entry_data).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_releasedir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let release_in = match fuse_release_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_release_in in releasedir failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((release_in, _)) => release_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_releasedir"), async move {
            debug!(
                "releasedir unique {} inode {} fh {} flags {}",
                request.unique, in_header.nodeid, release_in.fh, release_in.flags
            );

            let resp_value = if let Err(err) = fs
                .releasedir(request, in_header.nodeid, release_in.fh, release_in.flags)
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_fsyncdir(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let fsync_in = match fuse_fsync_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_fsync_in in fsyncdir failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((fsync_in, _)) => fsync_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_fsyncdir"), async move {
            let data_sync = fsync_in.fsync_flags & 1 > 0;

            debug!(
                "fsyncdir unique {} inode {} fh {} data_sync {}",
                request.unique, in_header.nodeid, fsync_in.fh, data_sync
            );

            let resp_value = if let Err(err) = fs
                .fsyncdir(request, in_header.nodeid, fsync_in.fh, data_sync)
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            resp_sender.send1(&out_header).await;
        });
    }

    #[cfg(feature = "file-lock")]
    #[instrument(skip(self, data, fs))]
    async fn handle_getlk(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let getlk_in = match fuse_lk_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_lk_in in getlk failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((getlk_in, _)) => getlk_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_getlk"), async move {
            debug!(
                "getlk unique {} inode {} {:?}",
                request.unique, in_header.nodeid, getlk_in
            );

            let reply_lock = match fs
                .getlk(
                    request,
                    in_header.nodeid,
                    getlk_in.fh,
                    getlk_in.owner,
                    getlk_in.lk.start,
                    getlk_in.lk.end,
                    getlk_in.lk.r#type,
                    getlk_in.lk.pid,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_lock) => reply_lock,
            };

            let getlk_out: fuse_lk_out = reply_lock.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_LK_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, getlk_out.as_bytes()).await;
        });
    }

    #[cfg(feature = "file-lock")]
    #[instrument(skip(self, data, fs))]
    async fn handle_setlk(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        block: bool,
        fs: &Arc<FS>,
    ) {
        let setlk_in = match fuse_lk_in::read_from_prefix(data) {
            Err(err) => {
                let opcode = if block {
                    fuse_opcode::FUSE_SETLKW
                } else {
                    fuse_opcode::FUSE_SETLK
                };

                error!(
                    "deserialize fuse_lk_in in {:?} failed {}, request unique {}",
                    opcode, err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((setlk_in, _)) => setlk_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_setlk"), async move {
            debug!(
                "setlk unique {} inode {} block {} {:?}",
                request.unique, in_header.nodeid, block, setlk_in
            );

            let resp = if let Err(err) = fs
                .setlk(
                    request,
                    in_header.nodeid,
                    setlk_in.fh,
                    setlk_in.owner,
                    setlk_in.lk.start,
                    setlk_in.lk.end,
                    setlk_in.lk.r#type,
                    setlk_in.lk.pid,
                    block,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_access(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let access_in = match fuse_access_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_access_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((access_in, _)) => access_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_access"), async move {
            debug!(
                "access unique {} inode {} mask {}",
                request.unique, in_header.nodeid, access_in.mask
            );

            let resp_value =
                if let Err(err) = fs.access(request, in_header.nodeid, access_in.mask).await {
                    err.reply_code()
                } else {
                    0
                };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            debug!("access response {}", resp_value);

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_create(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (create_in, data) = match fuse_create_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_create_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_create_in body has no null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_create"), async move {
            debug!(
                "create unique {} parent {} name {:?} mode {} flags {}",
                request.unique, in_header.nodeid, name, create_in.mode, create_in.flags
            );

            let created = match fs
                .create(
                    request,
                    in_header.nodeid,
                    &name,
                    create_in.mode,
                    create_in.flags,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(created) => created,
            };

            let (entry_out, open_out): (fuse_entry_out, fuse_open_out) = created.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE + FUSE_OPEN_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender
                .send3(&out_header, entry_out.as_bytes(), open_out.as_bytes())
                .await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_interrupt(&mut self, request: Request, data: &[u8], fs: &Arc<FS>) {
        let interrupt_in = match fuse_interrupt_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_interrupt_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((interrupt_in, _)) => interrupt_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_interrupt"), async move {
            debug!(
                "interrupt_in unique {} interrupt unique {}",
                request.unique, interrupt_in.unique
            );

            let resp_value = if let Err(err) = fs.interrupt(request, interrupt_in.unique).await {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            let _ = resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_bmap(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let bmap_in = match fuse_bmap_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_bmap_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((bmap_in, _)) => bmap_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_bmap"), async move {
            debug!(
                "bmap unique {} inode {} block size {} idx {}",
                request.unique, in_header.nodeid, bmap_in.blocksize, bmap_in.block
            );

            let reply_bmap = match fs
                .bmap(request, in_header.nodeid, bmap_in.blocksize, bmap_in.block)
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_bmap) => reply_bmap,
            };

            let bmap_out: fuse_bmap_out = reply_bmap.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_BMAP_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender.send2(&out_header, bmap_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_poll(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let poll_in = match fuse_poll_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_poll_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((poll_in, _)) => poll_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        let notify = self.get_notify();

        self.spawn(debug_span!("fuse_poll"), async move {
            debug!(
                "poll unique {} inode {} {:?}",
                request.unique, in_header.nodeid, poll_in
            );

            let kh = if poll_in.flags & FUSE_POLL_SCHEDULE_NOTIFY > 0 {
                Some(poll_in.kh)
            } else {
                None
            };

            let reply_poll = match fs
                .poll(
                    request,
                    in_header.nodeid,
                    poll_in.fh,
                    kh,
                    poll_in.flags,
                    poll_in.events,
                    &notify,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_poll) => reply_poll,
            };

            let poll_out: fuse_poll_out = reply_poll.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_POLL_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            let _ = resp_sender.send2(&out_header, poll_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_notify_reply(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let resp_sender = self.response_sender().clone();

        let (notify_retrieve_in, data) = match fuse_notify_retrieve_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_notify_retrieve_in failed {}, request unique {}",
                    err, request.unique
                );

                // TODO need to reply or not?
                return;
            }

            Ok(r) => r,
        };

        if data.len() < notify_retrieve_in.size as usize {
            error!(
                "fuse_notify_retrieve unique {} data size is not right",
                request.unique
            );

            // TODO need to reply or not?
            return;
        }

        let data = data[..notify_retrieve_in.size as usize].to_vec();

        let fs = fs.clone();

        self.spawn(debug_span!("fuse_notify_reply"), async move {
            if let Err(err) = fs
                .notify_reply(
                    request,
                    in_header.nodeid,
                    notify_retrieve_in.offset,
                    data.into(),
                )
                .await
            {
                resp_sender.send_err(&request, err).await;
            }
        });
    }

    #[instrument(level = "debug", skip(self, data, fs))]
    async fn handle_batch_forget(
        &mut self,
        request: Request,
        _in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (batch_forget_in, mut data) = match fuse_batch_forget_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_batch_forget_in failed {}, request unique {}",
                    err, request.unique
                );

                // no need to reply
                return;
            }

            Ok(r) => r,
        };

        let mut forgets = vec![];

        // TODO if has less data, should I return error?
        while data.len() >= FUSE_FORGET_ONE_SIZE {
            match fuse_forget_one::read_from_prefix(data) {
                Err(err) => {
                    error!(
                        "deserialize fuse_batch_forget_in body fuse_forget_one failed {}, request unique {}",
                        err, request.unique
                    );

                    // no need to reply
                    return;
                }

                Ok((forget_one, remaining_data)) => {
                    data = remaining_data;

                    forgets.push(forget_one);
                }
            }
        }

        if forgets.len() != batch_forget_in.count as usize {
            error!(
                "fuse_forget_one count != fuse_batch_forget_in.count, request unique {}",
                request.unique
            );

            return;
        }

        let fs = fs.clone();

        self.spawn(debug_span!("fuse_batch_forget"), async move {
            let inodes = forgets
                .into_iter()
                .map(|forget_one| (forget_one.nodeid, forget_one.nlookup))
                .collect::<Vec<_>>();

            debug!("batch_forget unique {} inodes {:?}", request.unique, inodes);

            fs.batch_forget(request, &inodes).await
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_fallocate(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let fallocate_in: fuse_fallocate_in = match fuse_fallocate_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_fallocate_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((fallocate_in, _)) => fallocate_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_fallocate"), async move {
            debug!(
                "fallocate unique {} inode {} {:?}",
                request.unique, in_header.nodeid, fallocate_in
            );

            let resp_value = if let Err(err) = fs
                .fallocate(
                    request,
                    in_header.nodeid,
                    fallocate_in.fh,
                    fallocate_in.offset,
                    fallocate_in.length,
                    fallocate_in.mode,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_readdirplus(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let readdirplus_in: fuse_read_in = match fuse_read_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_read_in in readdirplus failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((readdirplus_in, _)) => readdirplus_in,
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_readdirplus"), async move {
            debug!(
                "readdirplus unique {} parent {} {:?}",
                request.unique, in_header.nodeid, readdirplus_in
            );

            let directory_plus = match fs
                .readdirplus(
                    request,
                    in_header.nodeid,
                    readdirplus_in.fh,
                    readdirplus_in.offset,
                    readdirplus_in.lock_owner,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(directory_plus) => directory_plus,
            };

            let max_size = readdirplus_in.size as usize;

            let mut entry_data = Vec::with_capacity(max_size);

            let entries = directory_plus.entries;
            let mut entries = pin!(entries);

            while let Some(entry) = entries.next().await {
                let entry = match entry {
                    Err(err) => {
                        resp_sender.send_err(&request, err).await;
                        return;
                    }

                    Ok(entry) => entry,
                };

                let name = &entry.name;

                let dir_entry_size = FUSE_DIRENTPLUS_SIZE + name.len();

                let padding_size = get_padding_size(dir_entry_size);

                // the padding is sent too, so it must fit as well
                if !dir_entry_fits(entry_data.len(), dir_entry_size, max_size) {
                    break;
                }

                let attr = entry.attr;

                let dir_entry = fuse_direntplus {
                    entry_out: fuse_entry_out {
                        nodeid: attr.ino,
                        generation: entry.generation,
                        entry_valid: entry.entry_ttl.as_secs(),
                        attr_valid: entry.attr_ttl.as_secs(),
                        entry_valid_nsec: entry.entry_ttl.subsec_nanos(),
                        attr_valid_nsec: entry.attr_ttl.subsec_nanos(),
                        attr: attr.into(),
                    },
                    dirent: fuse_dirent {
                        ino: entry.inode,
                        off: entry.offset as u64,
                        namelen: name.len() as u32,
                        // learn from fuse-rs and golang bazil.org fuse DirentType
                        r#type: mode_from_kind_and_perm(entry.kind, 0) >> 12,
                    },
                };

                dir_entry.write_to_io(&mut entry_data).unwrap();

                entry_data.extend_from_slice(name.as_bytes());

                // padding
                entry_data.resize(entry_data.len() + padding_size, 0);
            }

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + entry_data.len()) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, &entry_data).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_rename2(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let (rename2_in, data) = match fuse_rename2_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_rename2_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok(r) => r,
        };

        let (old_name, index) = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_rename2_in body doesn't have null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => (OsString::from_vec(data[..index].to_vec()), index),
        };

        let data = &data[index + 1..];

        let new_name = match get_first_null_position(data) {
            None => {
                error!(
                    "fuse_rename2_in body doesn't have second null, request unique {}",
                    request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Some(index) => OsString::from_vec(data[..index].to_vec()),
        };

        let resp_sender = self.response_sender().clone();
        let fs = fs.clone();

        self.spawn(debug_span!("fuse_rename2"), async move {
            debug!(
                "rename2 unique {} parent {} name {:?} new parent {} new name {:?} flags {}",
                request.unique,
                in_header.nodeid,
                old_name,
                rename2_in.newdir,
                new_name,
                rename2_in.flags
            );

            let resp_value = if let Err(err) = fs
                .rename2(
                    request,
                    in_header.nodeid,
                    &old_name,
                    rename2_in.newdir,
                    &new_name,
                    rename2_in.flags,
                )
                .await
            {
                err.reply_code()
            } else {
                0
            };

            let out_header = fuse_out_header {
                len: FUSE_OUT_HEADER_SIZE as u32,
                error: resp_value,
                unique: request.unique,
            };

            resp_sender.send1(&out_header).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_lseek(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let resp_sender = self.response_sender().clone();

        let lseek_in = match fuse_lseek_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_lseek_in failed {}, request unique {}",
                    err, request.unique
                );
                resp_sender.send_err(&request, libc::EINVAL.into()).await;
                return;
            }

            Ok((lseek_in, _)) => lseek_in,
        };

        let fs = fs.clone();

        self.spawn(debug_span!("fuse_lseek"), async move {
            debug!(
                "lseek unique {} inode {} {:?}",
                request.unique, in_header.nodeid, lseek_in
            );

            let reply_lseek = match fs
                .lseek(
                    request,
                    in_header.nodeid,
                    lseek_in.fh,
                    lseek_in.offset,
                    lseek_in.whence,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_lseek) => reply_lseek,
            };

            let lseek_out: fuse_lseek_out = reply_lseek.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_LSEEK_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };
            resp_sender.send2(&out_header, lseek_out.as_bytes()).await;
        });
    }

    #[instrument(skip(self, data, fs))]
    async fn handle_copy_file_range(
        &mut self,
        request: Request,
        in_header: fuse_in_header,
        data: &[u8],
        fs: &Arc<FS>,
    ) {
        let resp_sender = self.response_sender().clone();

        let copy_file_range_in = match fuse_copy_file_range_in::read_from_prefix(data) {
            Err(err) => {
                error!(
                    "deserialize fuse_copy_file_range_in failed {}, request unique {}",
                    err, request.unique
                );

                self.response_sender()
                    .send_err(&request, libc::EINVAL.into())
                    .await;

                return;
            }

            Ok((copy_file_range_in, _)) => copy_file_range_in,
        };

        let fs = fs.clone();
        // The reply carries the copied count in a u32.
        let length = copy_file_range_in.len.min(u64::from(u32::MAX));

        self.spawn(debug_span!("fuse_copy_file_range"), async move {
            debug!(
                "reply_copy_file_range unique {} inode {} {:?}",
                request.unique, in_header.nodeid, copy_file_range_in
            );

            let reply_copy_file_range = match fs
                .copy_file_range(
                    request,
                    in_header.nodeid,
                    copy_file_range_in.fh_in,
                    copy_file_range_in.off_in,
                    copy_file_range_in.nodeid_out,
                    copy_file_range_in.fh_out,
                    copy_file_range_in.off_out,
                    length,
                    copy_file_range_in.flags,
                )
                .await
            {
                Err(err) => {
                    resp_sender.send_err(&request, err).await;
                    return;
                }

                Ok(reply_copy_file_range) => reply_copy_file_range,
            };

            if reply_copy_file_range.copied > length {
                error!(
                    copied = reply_copy_file_range.copied,
                    length,
                    unique = request.unique,
                    "copy_file_range reported more bytes than requested"
                );

                resp_sender.send_err(&request, libc::EIO.into()).await;
                return;
            }

            let write_out: fuse_write_out = reply_copy_file_range.into();

            let out_header = fuse_out_header {
                len: (FUSE_OUT_HEADER_SIZE + FUSE_WRITE_OUT_SIZE) as u32,
                error: 0,
                unique: request.unique,
            };

            resp_sender.send2(&out_header, write_out.as_bytes()).await;
        });
    }
}

/// Whether a GETXATTR or LISTXATTR reply matches what the kernel asked for.
///
/// A zero `size` asks for the length only; a nonzero `size` asks for data that fits in it.
fn xattr_reply_fits(size: u32, reply: &ReplyXAttr) -> bool {
    match reply {
        ReplyXAttr::Size(_) => size == 0,
        ReplyXAttr::Data(data) => size != 0 && data.len() <= size as usize,
    }
}

/// `None` for a size-only reply.
fn xattr_data_len(reply: &ReplyXAttr) -> Option<usize> {
    match reply {
        ReplyXAttr::Size(_) => None,
        ReplyXAttr::Data(data) => Some(data.len()),
    }
}

/// Run `fut` in the background on the runtime of the session.
pub(crate) fn spawn_task<F>(fut: F)
where
    F: Future + Send + 'static,
    F::Output: Send + 'static,
{
    spawn(debug_span!("fuse3_task"), fut)
}

#[inline]
fn spawn<F>(span: Span, fut: F)
where
    F: Future + Send + 'static,
    F::Output: Send + 'static,
{
    #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
    task::spawn(fut.instrument(span));

    #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
    task::spawn(fut.instrument(span)).detach()
}

#[cfg(test)]
mod tests;
