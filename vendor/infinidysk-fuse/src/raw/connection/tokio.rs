#[cfg(target_os = "macos")]
use std::env;
#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use std::ffi::OsString;
#[cfg(any(target_os = "linux", target_os = "macos"))]
use std::fs::File;
#[cfg(any(target_os = "linux", target_os = "freebsd"))]
use std::fs::OpenOptions;
use std::io;
#[cfg(any(target_os = "linux", target_os = "macos"))]
use std::io::Write;
use std::io::{IoSlice, IoSliceMut};
use std::ops::DerefMut;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
use std::os::fd::OwnedFd;
use std::os::fd::{AsFd, BorrowedFd};
#[cfg(target_os = "freebsd")]
use std::os::unix::fs::OpenOptionsExt;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos"
))]
use std::os::unix::io::RawFd;
#[cfg(target_os = "macos")]
use std::os::unix::io::{AsRawFd, FromRawFd};
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos",
))]
use std::path::Path;
use std::pin::pin;
use std::sync::Arc;

use async_notify::Notify;
use futures_util::lock::Mutex;
use futures_util::{FutureExt, select};
#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use nix::fcntl::{FcntlArg, OFlag};
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos",
))]
use nix::sys::socket::{self, AddressFamily, ControlMessageOwned, MsgFlags, SockFlag, SockType};
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
use nix::sys::uio;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
use tokio::io::Interest;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
use tokio::io::unix::AsyncFd;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos"
))]
use tokio::process::Command;
#[cfg(any(target_os = "linux", target_os = "macos"))]
use tokio::task;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos"
))]
use tracing::debug;
#[cfg(target_os = "freebsd")]
use tracing::warn;

use super::CompleteIoResult;
#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "macos"
))]
use crate::MountOptions;
#[cfg(all(target_os = "linux", feature = "unprivileged"))]
use crate::find_fusermount3;

#[derive(Debug)]
pub struct FuseConnection {
    unmount_notify: Arc<Notify>,
    mode: ConnectionMode,
}

impl FuseConnection {
    #[cfg(any(target_os = "linux", target_os = "freebsd"))]
    pub fn new(unmount_notify: Arc<Notify>) -> io::Result<Self> {
        #[cfg(target_os = "freebsd")]
        {
            let connection = NonBlockFuseConnection::new()?;

            Ok(Self {
                unmount_notify,
                mode: ConnectionMode::NonBlock(connection),
            })
        }

        #[cfg(target_os = "linux")]
        {
            let connection = BlockFuseConnection::new()?;

            Ok(Self {
                unmount_notify,
                mode: ConnectionMode::Block(connection),
            })
        }
    }

    #[cfg(all(target_os = "linux", feature = "unprivileged"))]
    pub async fn new_with_unprivileged(
        mount_options: MountOptions,
        mount_path: impl AsRef<Path>,
        unmount_notify: Arc<Notify>,
    ) -> io::Result<Self> {
        let connection =
            NonBlockFuseConnection::new_with_unprivileged(mount_options, mount_path).await?;

        Ok(Self {
            unmount_notify,
            mode: ConnectionMode::NonBlock(connection),
        })
    }

    #[cfg(target_os = "macos")]
    pub async fn new_with_unprivileged(
        mount_options: MountOptions,
        mount_path: impl AsRef<Path>,
        unmount_notify: Arc<Notify>,
    ) -> io::Result<Self> {
        let connection =
            BlockFuseConnection::new_with_unprivileged(mount_options, mount_path).await?;

        Ok(Self {
            unmount_notify,
            mode: ConnectionMode::Block(connection),
        })
    }

    #[cfg(test)]
    pub(crate) fn memory() -> (Self, super::KernelSide) {
        let (connection, kernel) = super::memory::MemoryConnection::new();

        (
            Self {
                unmount_notify: Arc::new(Notify::new()),
                mode: ConnectionMode::Memory(connection),
            },
            kernel,
        )
    }

    /// The current or next `read_vectored` returns `None`, which ends dispatch.
    pub(crate) fn shut_down(&self) {
        self.unmount_notify.notify();
    }

    pub async fn read_vectored<T: DerefMut<Target = [u8]> + Send + 'static>(
        &self,
        header_buf: Vec<u8>,
        data_buf: T,
    ) -> Option<CompleteIoResult<(Vec<u8>, T), usize>> {
        let mut unmount_fut = pin!(self.unmount_notify.notified().fuse());
        let mut read_fut = pin!(self.inner_read_vectored(header_buf, data_buf).fuse());

        select! {
            _ = unmount_fut => None,
            res = read_fut => Some(res)
        }
    }

    async fn inner_read_vectored<T: DerefMut<Target = [u8]> + Send + 'static>(
        &self,
        header_buf: Vec<u8>,
        data_buf: T,
    ) -> CompleteIoResult<(Vec<u8>, T), usize> {
        match &self.mode {
            #[cfg(any(target_os = "linux", target_os = "macos"))]
            ConnectionMode::Block(connection) => {
                connection.read_vectored(header_buf, data_buf).await
            }
            #[cfg(any(
                all(target_os = "linux", feature = "unprivileged"),
                target_os = "freebsd",
            ))]
            ConnectionMode::NonBlock(connection) => {
                connection.read_vectored(header_buf, data_buf).await
            }
            #[cfg(test)]
            ConnectionMode::Memory(connection) => {
                connection.read_vectored(header_buf, data_buf).await
            }
        }
    }

    pub async fn write_vectored(&self, bufs: &[IoSlice<'_>]) -> io::Result<usize> {
        match &self.mode {
            #[cfg(any(target_os = "linux", target_os = "macos"))]
            ConnectionMode::Block(connection) => connection.write_vectored(bufs).await,
            #[cfg(any(
                all(target_os = "linux", feature = "unprivileged"),
                target_os = "freebsd",
            ))]
            ConnectionMode::NonBlock(connection) => connection.write_vectored(bufs).await,
            #[cfg(test)]
            ConnectionMode::Memory(connection) => connection.write_vectored(bufs),
        }
    }

    /// Writes one whole message from the calling thread, which the write may block.
    #[cfg(target_os = "linux")]
    pub(crate) fn write_vectored_blocking(&self, bufs: &[IoSlice<'_>]) -> io::Result<usize> {
        match &self.mode {
            ConnectionMode::Block(connection) => (&connection.file).write_vectored(bufs),
            #[cfg(feature = "unprivileged")]
            ConnectionMode::NonBlock(connection) => {
                uio::writev(&connection.fd, bufs).map_err(Into::into)
            }
            #[cfg(test)]
            ConnectionMode::Memory(connection) => connection.write_vectored(bufs),
        }
    }
}

#[derive(Debug)]
enum ConnectionMode {
    #[cfg(any(target_os = "linux", target_os = "macos"))]
    Block(BlockFuseConnection),
    #[cfg(any(
        all(target_os = "linux", feature = "unprivileged"),
        target_os = "freebsd",
    ))]
    NonBlock(NonBlockFuseConnection),
    #[cfg(test)]
    Memory(super::memory::MemoryConnection),
}

#[cfg(any(target_os = "linux", target_os = "macos"))]
#[derive(Debug)]
struct BlockFuseConnection {
    file: File,
    read: Mutex<()>,
    // Linux v6.12 fuse_dev_write handles concurrent whole-message writes independently.
    #[cfg(not(target_os = "linux"))]
    write: Mutex<()>,
}

#[cfg(any(target_os = "linux", target_os = "macos"))]
impl BlockFuseConnection {
    #[cfg(target_os = "linux")]
    pub fn new() -> io::Result<Self> {
        const DEV_FUSE: &str = "/dev/fuse";

        let file = OpenOptions::new().write(true).read(true).open(DEV_FUSE)?;

        Ok(Self {
            file,
            read: Mutex::new(()),
        })
    }

    #[cfg(target_os = "macos")]
    async fn new_with_unprivileged(
        mount_options: MountOptions,
        mount_path: impl AsRef<Path>,
    ) -> io::Result<Self> {
        use std::{thread, time::Duration};

        use crate::find_macfuse_mount;

        let (sock0, sock1) = match socket::socketpair(
            AddressFamily::Unix,
            SockType::Stream,
            None,
            SockFlag::empty(),
        ) {
            Err(err) => return Err(err.into()),

            Ok((sock0, sock1)) => (sock0, sock1),
        };

        let binary_path = find_macfuse_mount()?;

        const ENV: &str = "_FUSE_COMMFD";

        let options = mount_options.build();

        debug!("mount options {:?}", options);

        let exec_path = match env::current_exe() {
            Ok(path) => path,
            Err(err) => return Err(err),
        };

        let mount_path = mount_path.as_ref().as_os_str().to_os_string();
        // macfuse_mound will block until fuse init done, so we can not join it in the current function
        tokio::spawn(async move {
            debug!("mount_thread start");
            let fd0 = sock0.as_raw_fd();
            let mut binding = Command::new(binary_path);
            let child = binding
                .env(ENV, fd0.to_string())
                .env("_FUSE_CALL_BY_LIB", "1")
                .env("_FUSE_COMMVERS", "2")
                .env("_FUSE_DAEMON_PATH", exec_path)
                .args(vec![options, mount_path]);
            let status = child.spawn()?.wait().await?;

            if status.success() {
                Ok(())
            } else {
                Err(io::Error::other("fusermount run failed"))
            }
        });

        let fd1 = sock1.as_raw_fd();
        // wait for macfuse mount
        let fd = task::spawn_blocking(move || {
            debug!("wait_thread start");
            // wait for macfuse mount command start
            // it seems that socket::recvmsg will not block to wait for the message
            // so we need to sleep for a while
            thread::sleep(Duration::from_secs(1));
            // let mut buf = vec![0; 10000]; // buf should large enough
            let mut buf = vec![]; // it seems 0 len still works well

            let mut cmsg_buf = nix::cmsg_space!([RawFd; 1]);

            let mut bufs = [IoSliceMut::new(&mut buf)];

            let msg = match socket::recvmsg::<()>(
                fd1,
                &mut bufs[..],
                Some(&mut cmsg_buf),
                MsgFlags::empty(),
            ) {
                Err(err) => return Err(err.into()),

                Ok(msg) => msg,
            };

            let mut cmsgs = match msg.cmsgs() {
                Err(err) => return Err(err.into()),
                Ok(cmsgs) => cmsgs,
            };
            let fd = if let Some(ControlMessageOwned::ScmRights(fds)) = cmsgs.next() {
                if fds.is_empty() {
                    return Err(io::Error::other("no fuse fd"));
                }

                fds[0]
            } else {
                return Err(io::Error::other("get fuse fd failed"));
            };

            Ok(fd)
        })
        .await
        .unwrap()?;

        let file = unsafe { File::from_raw_fd(fd) };
        Ok(Self {
            file,
            read: Mutex::new(()),
            write: Mutex::new(()),
        })
    }

    async fn read_vectored<T: DerefMut<Target = [u8]> + Send + 'static>(
        &self,
        mut header_buf: Vec<u8>,
        mut data_buf: T,
    ) -> CompleteIoResult<(Vec<u8>, T), usize> {
        use std::io::Read;
        use std::mem::ManuallyDrop;
        use std::os::fd::{AsRawFd, FromRawFd};

        let _guard = self.read.lock().await;
        let fd = self.file.as_raw_fd();

        let ((header_buf, data_buf), res) = task::spawn_blocking(move || {
            // Safety: when we call read, the fd is still valid, when fd is closed and file is
            // dropped, the read operation will return error
            let file = unsafe { File::from_raw_fd(fd) };
            // avoid close the file
            let mut file = ManuallyDrop::new(file);

            let res = file.read_vectored(&mut [
                IoSliceMut::new(&mut header_buf),
                IoSliceMut::new(&mut data_buf),
            ]);

            ((header_buf, data_buf), res)
        })
        .await
        .unwrap();

        ((header_buf, data_buf), res)
    }

    async fn write_vectored(&self, bufs: &[IoSlice<'_>]) -> io::Result<usize> {
        #[cfg(not(target_os = "linux"))]
        let _guard = self.write.lock().await;
        (&self.file).write_vectored(bufs)
    }
}

#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
#[derive(Debug)]
struct NonBlockFuseConnection {
    fd: AsyncFd<OwnedFd>,
    read: Mutex<()>,
    #[cfg(target_os = "freebsd")]
    write: Mutex<()>,
}

#[cfg(any(
    all(target_os = "linux", feature = "unprivileged"),
    target_os = "freebsd",
))]
impl NonBlockFuseConnection {
    #[cfg(target_os = "freebsd")]
    fn new() -> io::Result<Self> {
        #[cfg(target_os = "freebsd")]
        const DEV_FUSE: &str = "/dev/fuse";

        match OpenOptions::new()
            .write(true)
            .read(true)
            .custom_flags(libc::O_NONBLOCK)
            .open(DEV_FUSE)
        {
            Err(e) => {
                if e.kind() == io::ErrorKind::NotFound {
                    warn!("Cannot open {}.  Is the module loaded?", DEV_FUSE);
                }
                warn!("Cannot open {}.  err: {:?}", DEV_FUSE, e);
                Err(e)
            }
            Ok(file) => Ok(Self {
                fd: AsyncFd::new(file.into())?,
                read: Mutex::new(()),
                write: Mutex::new(()),
            }),
        }
    }

    #[cfg(all(target_os = "linux", feature = "unprivileged"))]
    async fn new_with_unprivileged(
        mount_options: MountOptions,
        mount_path: impl AsRef<Path>,
    ) -> io::Result<Self> {
        use std::os::fd::{AsRawFd, FromRawFd};

        let (sock0, sock1) = match socket::socketpair(
            AddressFamily::Unix,
            SockType::SeqPacket,
            None,
            SockFlag::empty(),
        ) {
            Err(err) => return Err(err.into()),

            Ok((sock0, sock1)) => (sock0, sock1),
        };

        let binary_path = find_fusermount3()?;

        const ENV: &str = "_FUSE_COMMFD";

        let options = mount_options.build_with_unprivileged();

        debug!("mount options {:?}", options);

        let mount_path = mount_path.as_ref().as_os_str().to_os_string();

        let fd0 = sock0.as_raw_fd();
        let mut child = Command::new(binary_path)
            .env(ENV, fd0.to_string())
            .args(vec![OsString::from("-o"), options, mount_path])
            .spawn()?;

        if !child.wait().await?.success() {
            return Err(io::Error::other("fusermount run failed"));
        }

        let fd1 = sock1.as_raw_fd();
        let fd = task::spawn_blocking(move || {
            // let mut buf = vec![0; 10000]; // buf should large enough
            let mut buf = vec![]; // it seems 0 len still works well

            let mut cmsg_buf = nix::cmsg_space!([RawFd; 1]);

            let mut bufs = [IoSliceMut::new(&mut buf)];

            let msg = match socket::recvmsg::<()>(
                fd1,
                &mut bufs[..],
                Some(&mut cmsg_buf),
                MsgFlags::empty(),
            ) {
                Err(err) => return Err(err.into()),

                Ok(msg) => msg,
            };

            let fd = if let Some(ControlMessageOwned::ScmRights(fds)) = msg.cmsgs()?.next() {
                if fds.is_empty() {
                    return Err(io::Error::other("no fuse fd"));
                }

                fds[0]
            } else {
                return Err(io::Error::other("get fuse fd failed"));
            };

            Ok(fd)
        })
        .await
        .unwrap()?;

        // Safety: fd is valid
        let fd = unsafe { OwnedFd::from_raw_fd(fd) };

        Self::set_fd_non_blocking(fd.as_fd())?;

        Ok(Self {
            // SAFETY: OwnedFd retains the same open descriptor until AsyncFd drops it.
            fd: unsafe { AsyncFd::register(fd)? },
            read: Mutex::new(()),
        })
    }

    #[cfg(all(target_os = "linux", feature = "unprivileged"))]
    fn set_fd_non_blocking(fd: BorrowedFd) -> io::Result<()> {
        let flags = nix::fcntl::fcntl(fd, FcntlArg::F_GETFL).map_err(io::Error::from)?;
        debug!(
            "set fd {:?} to non-blocking",
            OFlag::from_bits_truncate(flags)
        );
        let flags = OFlag::from_bits_truncate(flags) | OFlag::O_NONBLOCK;

        debug!("set fd {:?} to non-blocking", flags);
        nix::fcntl::fcntl(fd, FcntlArg::F_SETFL(flags)).map_err(io::Error::from)?;

        Ok(())
    }

    async fn read_vectored<T: DerefMut<Target = [u8]> + Send>(
        &self,
        mut header_buf: Vec<u8>,
        mut data_buf: T,
    ) -> CompleteIoResult<(Vec<u8>, T), usize> {
        let _guard = self.read.lock().await;

        loop {
            let mut read_guard = match self.fd.ready(Interest::READABLE | Interest::ERROR).await {
                Err(err) => return ((header_buf, data_buf), Err(err)),
                Ok(read_guard) => read_guard,
            };

            if let Ok(result) = read_guard.try_io(|fd| {
                uio::readv(
                    fd,
                    &mut [
                        IoSliceMut::new(&mut header_buf),
                        IoSliceMut::new(&mut data_buf),
                    ],
                )
                .map_err(io::Error::from)
            }) {
                return ((header_buf, data_buf), result);
            } else {
                continue;
            }
        }
    }

    async fn write_vectored(&self, bufs: &[IoSlice<'_>]) -> io::Result<usize> {
        #[cfg(target_os = "freebsd")]
        let _guard = self.write.lock().await;
        uio::writev(&self.fd, bufs).map_err(Into::into)
    }
}

impl AsFd for FuseConnection {
    fn as_fd(&self) -> BorrowedFd<'_> {
        match &self.mode {
            #[cfg(any(target_os = "linux", target_os = "macos"))]
            ConnectionMode::Block(connection) => connection.file.as_fd(),

            #[cfg(any(
                all(target_os = "linux", feature = "unprivileged"),
                target_os = "freebsd",
            ))]
            ConnectionMode::NonBlock(connection) => connection.fd.as_fd(),

            #[cfg(test)]
            ConnectionMode::Memory(_) => unreachable!("only mount code asks for the device fd"),
        }
    }
}
