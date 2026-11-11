#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use std::ffi::OsStr;
use std::ffi::OsString;
use std::fmt::{Debug, Formatter};
use std::future::Future;
use std::io::Error as IoError;
use std::io::ErrorKind;
use std::io::Result as IoResult;
use std::num::NonZeroU32;
#[cfg(any(target_os = "linux", target_os = "freebsd"))]
use std::os::fd::AsFd;
use std::os::unix::ffi::OsStrExt;
use std::os::unix::ffi::OsStringExt;
#[cfg(any(target_os = "linux", target_os = "freebsd"))]
use std::os::unix::io::AsRawFd;
use std::path::{Path, PathBuf};
use std::pin::{Pin, pin};
use std::sync::Arc;
use std::task::Context;
use std::task::Poll;

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
use futures_util::future::FutureExt;
use futures_util::select;
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
    pub async fn unmount(mut self) -> IoResult<()> {
        self.inner
            .take()
            .expect("unmount call twice")
            .inner_unmount()
            .await
    }
}

impl Drop for MountHandle {
    fn drop(&mut self) {
        if let Some(inner) = self.inner.take() {
            if inner.task.is_finished() {
                return;
            }

            #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
            {
                task::spawn(inner.inner_unmount()).detach();
            }

            #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
            {
                task::spawn(inner.inner_unmount());
            }
        }
    }
}

/// A sender for sending response to kernel, it will send response immediately when call send method, and notify session when send failed.
#[derive(Debug)]
pub(crate) struct ResponseSender {
    /// notify session when send failed, session will stop and unmount when receive this notify
    send_failed: Arc<async_notify::Notify>,
    /// connection to write response
    connection: Arc<FuseConnection>,
}

fn written(result: IoResult<usize>, expected: usize) -> IoResult<()> {
    match result? {
        written if written == expected => Ok(()),
        _ => Err(IoError::new(
            ErrorKind::WriteZero,
            "incomplete FUSE notification",
        )),
    }
}

impl ResponseSender {
    /// Return only after the complete notification was accepted by the device.
    pub(crate) async fn try_send2(&self, header: &fuse_out_header, data: &[u8]) -> IoResult<()> {
        let result = self
            .connection
            .write_vectored::<_, &[u8]>(header.as_bytes(), Some(data))
            .await
            .1;
        written(result, FUSE_OUT_HEADER_SIZE + data.len())
    }

    #[cfg(all(test, feature = "tokio-runtime", target_os = "linux"))]
    pub(crate) fn for_test(file: std::fs::File, nonblocking: bool) -> IoResult<Arc<Self>> {
        Ok(Arc::new(Self {
            send_failed: Arc::new(async_notify::Notify::new()),
            connection: Arc::new(FuseConnection::for_test(file, nonblocking)?),
        }))
    }

    /// send response with only header, no data
    pub(crate) async fn send1(&self, header: &fuse_out_header) {
        if let Err(err) = self
            .connection
            .write_vectored::<_, &[u8]>(header.as_bytes(), None)
            .await
            .1
        {
            if err.kind() == ErrorKind::NotFound {
                warn!(
                    "may reply interrupted fuse request, ignore this error {}",
                    err
                );
            } else {
                error!("reply fuse failed {}", err);
                self.send_failed.notify();
            }
        }
    }

    /// send response with header and data
    pub(crate) async fn send2(&self, header: &fuse_out_header, data: &[u8]) {
        if let Err(err) = self
            .connection
            .write_vectored::<_, &[u8]>(header.as_bytes(), Some(data))
            .await
            .1
        {
            if err.kind() == ErrorKind::NotFound {
                warn!(
                    "may reply interrupted fuse request, ignore this error {}",
                    err
                );
            } else {
                error!("reply fuse failed {}", err);
                self.send_failed.notify();
            }
        }
    }

    /// send response with header and two parts of data.
    pub(crate) async fn send3(&self, header: &fuse_out_header, d1: &[u8], d2: &[u8]) {
        // TODO: Implement a write_vectored in FuseConnection to avoid this copy
        let mut data = Vec::with_capacity(d1.len() + d2.len());
        data.extend_from_slice(d1);
        data.extend_from_slice(d2);
        if let Err(err) = self
            .connection
            .write_vectored::<_, &[u8]>(header.as_bytes(), Some(&data))
            .await
            .1
        {
            if err.kind() == ErrorKind::NotFound {
                warn!(
                    "may reply interrupted fuse request, ignore this error {}",
                    err
                );
            } else {
                error!("reply fuse failed {}", err);
                self.send_failed.notify();
            }
        }
    }

    /// send error response
    #[inline]
    async fn send_err(&self, request: &Request, err: Errno) {
        let out_header = fuse_out_header {
            len: FUSE_OUT_HEADER_SIZE as u32,
            error: err.into(),
            unique: request.unique,
        };
        self.send1(&out_header).await
    }
}

#[derive(Debug)]
struct MountHandleInner {
    task: JoinHandle<IoResult<()>>,
    mount_path: PathBuf,
    destroy_notify: Arc<async_notify::Notify>,
    #[cfg(any(
        all(target_os = "linux", feature = "unprivileged"),
        target_os = "macos"
    ))]
    unprivileged: bool,
}

impl MountHandleInner {
    async fn inner_unmount(self) -> IoResult<()> {
        self.destroy_notify.notify();

        #[cfg(all(not(feature = "tokio-runtime"), feature = "async-io-runtime"))]
        {
            // wait destroy done
            self.task.await?;

            // TODO: freebsd mount is unprivileged, then unmount is unprivileged too?
            #[cfg(target_os = "freebsd")]
            {
                task::spawn_blocking(move || {
                    mount::unmount(&self.mount_path, MntFlags::MNT_SYNCHRONOUS)
                })
                .await?;
            }

            #[cfg(target_os = "macos")]
            {
                task::spawn_blocking(move || {
                    mount::unmount(&self.mount_path, MntFlags::MNT_SYNCHRONOUS)
                })
                .await?;
            }

            #[cfg(target_os = "linux")]
            {
                #[cfg(all(target_os = "linux", feature = "unprivileged"))]
                if self.unprivileged {
                    let binary_path = find_fusermount3()?;
                    let mut child = Command::new(binary_path)
                        .args([OsStr::new("-u"), self.mount_path.as_os_str()])
                        .spawn()?;
                    if !child.status().await?.success() {
                        return Err(IoError::other("call fusermount3 -u to unmount failed"));
                    }

                    return Ok(());
                }

                task::spawn_blocking(move || mount::umount(&self.mount_path)).await?;
            }
        }

        #[cfg(all(not(feature = "async-io-runtime"), feature = "tokio-runtime"))]
        {
            // wait destroy done
            self.task.await.unwrap()?;

            // TODO: freebsd mount is unprivileged, then unmount is unprivileged too?
            #[cfg(target_os = "freebsd")]
            {
                task::spawn_blocking(move || {
                    mount::unmount(&self.mount_path, MntFlags::MNT_SYNCHRONOUS)
                })
                .await
                .unwrap()?;
            }
            #[cfg(target_os = "macos")]
            {
                task::spawn_blocking(move || {
                    mount::unmount(&self.mount_path, MntFlags::MNT_SYNCHRONOUS)
                })
                .await
                .unwrap()?;
            }

            #[cfg(target_os = "linux")]
            {
                #[cfg(all(target_os = "linux", feature = "unprivileged"))]
                if self.unprivileged {
                    let binary_path = find_fusermount3()?;
                    let mut child = Command::new(binary_path)
                        .args([OsStr::new("-u"), self.mount_path.as_os_str()])
                        .spawn()?;
                    if !child.wait().await?.success() {
                        return Err(IoError::other("call fusermount3 -u to unmount failed"));
                    }

                    return Ok(());
                }

                task::spawn_blocking(move || mount::umount(&self.mount_path))
                    .await
                    .unwrap()?;
            }
        }

        Ok(())
    }
}

impl Future for MountHandle {
    type Output = IoResult<()>;

    #[cfg(feature = "async-io-runtime")]
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        Pin::new(&mut self.inner.as_mut().expect("inner should be Some()").task).poll(cx)
    }

    #[cfg(feature = "tokio-runtime")]
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        // The unwrap is necessary in order to provide the same API for both runtimes, and actually
        // unwrap should not panic, when MountHandle is canceled by unmount method, user has no
        // chance to poll again
        Pin::new(&mut self.inner.as_mut().expect("inner should be Some()").task)
            .poll(cx)
            .map(Result::unwrap)
    }
}

#[cfg(any(feature = "async-io-runtime", feature = "tokio-runtime"))]
/// fuse filesystem session, inode based.
pub struct Session<FS> {
    fuse_connection: Option<Arc<FuseConnection>>,
    filesystem: Option<Arc<FS>>,
    response_sender: Option<Arc<ResponseSender>>,
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
                task: task::spawn(self.inner_mount()),
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
                task: task::spawn(self.inner_mount()),
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
                task: task::spawn(self.inner_mount()),
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
                task: task::spawn(self.inner_mount()),
                mount_path: mount_path.to_path_buf(),
                destroy_notify: notify,
            }),
        })
    }

    #[cfg(target_os = "macos")]
    pub async fn mount<P: AsRef<Path>>(self, fs: FS, mount_path: P) -> IoResult<MountHandle> {
        self.mount_with_unprivileged(fs, mount_path).await
    }

    /// get response sender, it should be called after mount, otherwise it will panic
    fn response_sender(&self) -> &Arc<ResponseSender> {
        self.response_sender
            .as_ref()
            .expect("response_sender should be Some()")
    }

    async fn inner_mount(mut self) -> IoResult<()> {
        let fuse_write_connection = self.fuse_connection.as_ref().unwrap().clone();

        let send_failed = Arc::new(async_notify::Notify::new());
        self.response_sender.replace(Arc::new(ResponseSender {
            send_failed: send_failed.clone(),
            connection: fuse_write_connection,
        }));

        let dispatch_task = self.dispatch().fuse();
        let mut dispatch_task = pin!(dispatch_task);

        let send_failed = send_failed.notified().fuse();
        let mut send_failed = pin!(send_failed);

        select! {
            _ = send_failed => {
                return Err(std::io::Error::other("send response failed"))
            }

            dispatch_result = dispatch_task => {
                dispatch_result?;
            }
        }

        Ok(())
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

        self.handle_init(request, data_ref, fuse_connection, fs)
            .await
    }

    #[instrument(level = "debug", skip(self, header_buffer, data_buffer), ret)]
    async fn read_fuse_request(
        &mut self,
        fuse_connection: &FuseConnection,
        mut header_buffer: Vec<u8>,
        mut data_buffer: Vec<u8>,
    ) -> ReadResult {
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
        let n = match res {
            Err(err) => {
                if let Some(errno) = err.raw_os_error()
                    && errno == libc::ENODEV
                {
                    debug!("read from /dev/fuse failed with ENODEV");

                    return ReadResult::Destroy;
                }

                error!("read from /dev/fuse failed {}", err);

                return ReadResult::Request {
                    in_header: Err(err),
                    header_buffer,
                    data_buffer,
                };
            }

            Ok(n) => n,
        };

        debug!(n, "read fuse request done");

        if n < FUSE_IN_HEADER_SIZE {
            error!(
                n,
                FUSE_IN_HEADER_SIZE, "read_vectored n is less then FUSE_IN_HEADER_SIZE"
            );

            return ReadResult::Request {
                in_header: Err(IoError::other(
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

        ReadResult::Request {
            in_header: Ok(in_header),
            header_buffer,
            data_buffer,
        }
    }

    async fn dispatch(&mut self) -> IoResult<()> {
        let fuse_connection = self.fuse_connection.take().unwrap();
        let fs = self.filesystem.take().expect("filesystem not init");

        let max_write = self.init_filesystem(&fs, &fuse_connection).await?.get() as usize;
        fs.ready(self.get_notify()).await;
        let buffer_size = (max_write + FUSE_WRITE_IN_SIZE).max(FUSE_MIN_READ_BUFFER_SIZE);

        let mut header_buffer = vec![0; FUSE_IN_HEADER_SIZE];
        let mut data_buffer = vec![0; buffer_size];

        loop {
            let in_header = match self
                .read_fuse_request(&fuse_connection, header_buffer, data_buffer)
                .await
            {
                ReadResult::Destroy => {
                    fs.destroy(Request {
                        unique: 0,
                        uid: 0,
                        gid: 0,
                        pid: 0,
                    })
                    .await;

                    return Ok(());
                }

                ReadResult::Request {
                    in_header,
                    header_buffer: header_buf,
                    data_buffer: data_buf,
                } => {
                    header_buffer = header_buf;
                    data_buffer = data_buf;

                    match in_header {
                        Err(_) => continue,

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

                    self.handle_init(request, data_ref, &fuse_connection, &fs)
                        .await?;
                }

                fuse_opcode::FUSE_DESTROY => {
                    debug!("receive fuse destroy");

                    fs.destroy(request).await;

                    debug!("fuse destroyed");

                    return Ok(());
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
                    error: libc::EINVAL,
                    unique: request.unique,
                };

                if let Err(err) = fuse_connection
                    .write_vectored::<_, &[u8]>(init_out_header.as_bytes(), None)
                    .await
                    .1
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
                    error: err.into(),
                    unique: request.unique,
                };

                if let Err(err) = fuse_connection
                    .write_vectored::<_, &[u8]>(init_out_header.as_bytes(), None)
                    .await
                    .1
                {
                    error!("write error init out data to /dev/fuse failed {}", err);
                }

                return Err(err.into());
            }

            Ok(reply) => reply,
        };

        let init_out = fuse_init_out {
            major: FUSE_KERNEL_VERSION,
            minor: FUSE_KERNEL_MINOR_VERSION,
            max_readahead: init_in.max_readahead,
            flags: reply_flags,
            max_background: DEFAULT_MAX_BACKGROUND,
            congestion_threshold: DEFAULT_CONGESTION_THRESHOLD,
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

        if let Err(err) = written(
            fuse_connection
                .write_vectored(out_header.as_bytes(), Some(init_out.as_bytes()))
                .await
                .1,
            FUSE_OUT_HEADER_SIZE + FUSE_INIT_OUT_SIZE,
        ) {
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

        spawn(debug_span!("fuse_lookup"), async move {
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

        spawn(debug_span!("fuse_forget"), async move {
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

        spawn(debug_span!("fuse_getattr"), async move {
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

        spawn(debug_span!("fuse_setattr"), async move {
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

        spawn(debug_span!("fuse_readlink"), async move {
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

        spawn(debug_span!("fuse_symlink"), async move {
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

        spawn(debug_span!("fuse_mknod"), async move {
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

        spawn(debug_span!("fuse_mkdir"), async move {
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

        spawn(debug_span!("fuse_unlink"), async move {
            debug!(
                "unlink unique {} parent {} name {:?}",
                request.unique, in_header.nodeid, name
            );

            let resp_value = if let Err(err) = fs.unlink(request, in_header.nodeid, &name).await {
                err.into()
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

        spawn(debug_span!("fuse_rmdir"), async move {
            debug!(
                "rmdir unique {} parent {} name {:?}",
                request.unique, in_header.nodeid, name
            );

            let resp_value = if let Err(err) = fs.rmdir(request, in_header.nodeid, &name).await {
                err.into()
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

        spawn(debug_span!("fuse_rename"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_link"), async move {
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

        spawn(debug_span!("fuse_open"), async move {
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

        spawn(debug_span!("fuse_read"), async move {
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

        spawn(debug_span!("fuse_write"), async move {
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

        spawn(debug_span!("fuse_statfs"), async move {
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

        spawn(debug_span!("fuse_release"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_fsync"), async move {
            let data_sync = fsync_in.fsync_flags & 1 > 0;

            debug!(
                "fsync unique {} inode {} fh {} data_sync {}",
                request.unique, in_header.nodeid, fsync_in.fh, data_sync
            );

            let resp_value = if let Err(err) = fs
                .fsync(request, in_header.nodeid, fsync_in.fh, data_sync)
                .await
            {
                err.into()
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

        spawn(debug_span!("fuse_setxattr"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_getxattr"), async move {
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

            match xattr {
                ReplyXAttr::Size(size) => {
                    let getxattr_out = fuse_getxattr_out { size, _padding: 0 };

                    let out_header = fuse_out_header {
                        len: (FUSE_OUT_HEADER_SIZE + FUSE_GETXATTR_OUT_SIZE) as u32,
                        error: libc::ERANGE,
                        unique: request.unique,
                    };

                    resp_sender
                        .send2(&out_header, getxattr_out.as_bytes())
                        .await;
                }

                ReplyXAttr::Data(xattr_data) => {
                    // TODO check is right way or not
                    // TODO should we check data length or not
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

        spawn(debug_span!("fuse_listxattr"), async move {
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
                    // TODO check is right way or not
                    // TODO should we check data length or not
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

        spawn(debug_span!("fuse_removexattr"), async move {
            debug!(
                "removexattr unique {} inode {}",
                request.unique, in_header.nodeid
            );

            let resp_value =
                if let Err(err) = fs.removexattr(request, in_header.nodeid, &name).await {
                    err.into()
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

        spawn(debug_span!("fuse_flush"), async move {
            debug!(
                "flush unique {} inode {} fh {} lock_owner {}",
                request.unique, in_header.nodeid, flush_in.fh, flush_in.lock_owner
            );

            let resp_value = if let Err(err) = fs
                .flush(request, in_header.nodeid, flush_in.fh, flush_in.lock_owner)
                .await
            {
                err.into()
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

        spawn(debug_span!("fuse_opendir"), async move {
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

        spawn(debug_span!("fuse_readdir"), async move {
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

                if entry_data.len() + dir_entry_size > max_size {
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

        spawn(debug_span!("fuse_releasedir"), async move {
            debug!(
                "releasedir unique {} inode {} fh {} flags {}",
                request.unique, in_header.nodeid, release_in.fh, release_in.flags
            );

            let resp_value = if let Err(err) = fs
                .releasedir(request, in_header.nodeid, release_in.fh, release_in.flags)
                .await
            {
                err.into()
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

        spawn(debug_span!("fuse_fsyncdir"), async move {
            let data_sync = fsync_in.fsync_flags & 1 > 0;

            debug!(
                "fsyncdir unique {} inode {} fh {} data_sync {}",
                request.unique, in_header.nodeid, fsync_in.fh, data_sync
            );

            let resp_value = if let Err(err) = fs
                .fsyncdir(request, in_header.nodeid, fsync_in.fh, data_sync)
                .await
            {
                err.into()
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

        spawn(debug_span!("fuse_getlk"), async move {
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

        spawn(debug_span!("fuse_setlk"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_access"), async move {
            debug!(
                "access unique {} inode {} mask {}",
                request.unique, in_header.nodeid, access_in.mask
            );

            let resp_value =
                if let Err(err) = fs.access(request, in_header.nodeid, access_in.mask).await {
                    err.into()
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

        spawn(debug_span!("fuse_create"), async move {
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

        spawn(debug_span!("fuse_interrupt"), async move {
            debug!(
                "interrupt_in unique {} interrupt unique {}",
                request.unique, interrupt_in.unique
            );

            let resp_value = if let Err(err) = fs.interrupt(request, interrupt_in.unique).await {
                err.into()
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

        spawn(debug_span!("fuse_bmap"), async move {
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

        spawn(debug_span!("fuse_poll"), async move {
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

        spawn(debug_span!("fuse_notify_reply"), async move {
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

        spawn(debug_span!("fuse_batch_forget"), async move {
            let inodes = forgets
                .into_iter()
                .map(|forget_one| forget_one.nodeid)
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

        spawn(debug_span!("fuse_fallocate"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_readdirplus"), async move {
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

                if entry_data.len() + dir_entry_size > max_size {
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

        spawn(debug_span!("fuse_rename2"), async move {
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
                err.into()
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

        spawn(debug_span!("fuse_lseek"), async move {
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

        spawn(debug_span!("fuse_copy_file_range"), async move {
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
                    copy_file_range_in.len,
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

#[cfg(all(test, feature = "tokio-runtime", target_os = "linux"))]
mod notification_tests {
    use super::*;
    use crate::raw::reply::ReplyInit;
    use std::os::unix::net::UnixDatagram;
    use std::sync::atomic::{AtomicU8, Ordering};

    #[test]
    fn partial_notifications_are_not_successful() {
        assert!(written(Ok(40), 40).is_ok());
        for count in [0, 1, 39, 41] {
            assert_eq!(
                written(Ok(count), 40).unwrap_err().kind(),
                ErrorKind::WriteZero
            );
        }
        assert_eq!(
            written(Err(IoError::from_raw_os_error(libc::ENOENT)), 40)
                .unwrap_err()
                .raw_os_error(),
            Some(libc::ENOENT)
        );
    }

    struct Prepared(Arc<AtomicU8>, bool);

    impl Filesystem for Prepared {
        async fn init(&self, _: Request) -> crate::Result<ReplyInit> {
            assert_eq!(self.0.swap(1, Ordering::SeqCst), 0);
            if self.1 {
                return Err(libc::EIO.into());
            }
            Ok(ReplyInit {
                max_write: NonZeroU32::MIN.saturating_add(4095),
            })
        }
        async fn ready(&self, notify: Notify) {
            assert_eq!(self.0.swap(2, Ordering::SeqCst), 1);
            notify.try_invalid_inode(77, 0, 0).await.unwrap();
        }
        async fn destroy(&self, _: Request) {
            assert_eq!(self.0.swap(3, Ordering::SeqCst), 2);
        }
    }

    fn request(opcode: fuse_opcode, body: &[u8]) -> Vec<u8> {
        let mut packet = vec![0; FUSE_IN_HEADER_SIZE];
        let len = (packet.len() + body.len()) as u32;
        packet[..4].copy_from_slice(&len.to_ne_bytes());
        packet[4..8].copy_from_slice(&(opcode as u32).to_ne_bytes());
        packet.extend_from_slice(body);
        packet
    }

    #[tokio::test]
    async fn notifier_is_ready_only_after_the_init_reply_and_before_dispatch() {
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            for fail in [false, true] {
                let (socket, peer) = UnixDatagram::pair().unwrap();
                socket.set_nonblocking(true).unwrap();
                peer.set_nonblocking(true).unwrap();
                let peer = tokio::net::UnixDatagram::from_std(peer).unwrap();
                let fd: std::os::fd::OwnedFd = socket.into();
                let sender = ResponseSender::for_test(std::fs::File::from(fd), true).unwrap();
                let phase = Arc::new(AtomicU8::new(0));
                let mut session = Session::new(MountOptions::default());
                session.fuse_connection = Some(Arc::clone(&sender.connection));
                session.response_sender = Some(sender);
                session.filesystem = Some(Arc::new(Prepared(Arc::clone(&phase), fail)));
                let dispatch = tokio::spawn(async move { session.dispatch().await });
                let init: Vec<u8> = [FUSE_KERNEL_VERSION, FUSE_KERNEL_MINOR_VERSION, 4096, 0]
                    .into_iter()
                    .flat_map(u32::to_ne_bytes)
                    .collect();
                peer.send(&request(fuse_opcode::FUSE_INIT, &init))
                    .await
                    .unwrap();
                let mut packet = [0; 256];
                let len = peer.recv(&mut packet).await.unwrap();
                assert_eq!(
                    i32::from_ne_bytes(packet[4..8].try_into().unwrap()),
                    if fail { -libc::EIO } else { 0 }
                );
                if fail {
                    assert_eq!(len, FUSE_OUT_HEADER_SIZE);
                    assert!(dispatch.await.unwrap().is_err());
                    assert_eq!(phase.load(Ordering::SeqCst), 1);
                } else {
                    assert_eq!(len, FUSE_OUT_HEADER_SIZE + FUSE_INIT_OUT_SIZE);
                    assert_eq!(peer.recv(&mut packet).await.unwrap(), 40);
                    assert_eq!(
                        i32::from_ne_bytes(packet[4..8].try_into().unwrap()),
                        fuse_notify_code::FUSE_NOTIFY_INVAL_INODE as i32
                    );
                    peer.send(&request(fuse_opcode::FUSE_DESTROY, &[]))
                        .await
                        .unwrap();
                    dispatch.await.unwrap().unwrap();
                    assert_eq!(phase.load(Ordering::SeqCst), 3);
                }
            }
        })
        .await
        .unwrap();
    }
}
