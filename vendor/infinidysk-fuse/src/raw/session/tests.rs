//! Dispatcher tests over the in-memory connection. No mount, no `/dev/fuse`.

use std::ffi::OsStr;
use std::io::{ErrorKind, Result as IoResult};
use std::num::NonZeroU32;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;

use tokio::sync::Notify as Signal;
use tokio::task::JoinHandle;
use tokio::time::{sleep, timeout};

use super::{MountHandle, MountHandleInner, Session};
use crate::notify::Notify;
use crate::raw::abi::*;
use crate::raw::connection::{FuseConnection, KernelSide};
use crate::raw::filesystem::Filesystem;
#[cfg(feature = "file-lock")]
use crate::raw::reply::ReplyLock;
use crate::raw::reply::{
    FileAttr, ReplyAttr, ReplyCopyFileRange, ReplyData, ReplyEntry, ReplyInit, ReplyXAttr,
};
use crate::raw::request::Request;
use crate::{FileType, Inode, MountOptions, Result, Timestamp};

const MAX_WRITE: u32 = 16 * 1024;
const MAX_READAHEAD: u32 = 128 * 1024;
const ROOT: Inode = 1;
const HELLO: Inode = 2;
const UNKNOWN_OPCODE: u32 = 9999;
/// `copy_file_range` reports one byte more than it was asked to copy for this input handle.
const OVERREPORTING_FH: u64 = 2;
const WAIT: Duration = Duration::from_secs(5);
const XATTR_DATA: &[u8] = b"abcd";

#[derive(Debug, Default)]
struct TestFs {
    destroyed: AtomicBool,
    /// `init` fails with `EXDEV`.
    fail_init: AtomicBool,
    panic_in_ready: AtomicBool,
    copy_length: AtomicU64,
    notify: Mutex<Option<Notify>>,
    /// What `getattr` and `destroy` did, in order.
    events: Mutex<Vec<&'static str>>,
    /// `getattr` signals this on entry, then waits for `getattr_gate`.
    getattr_entered: Signal,
    getattr_gate: Signal,
}

impl TestFs {
    fn record(&self, event: &'static str) {
        self.events
            .lock()
            .expect("no test panics while holding the events lock")
            .push(event);
    }

    fn events(&self) -> Vec<&'static str> {
        self.events
            .lock()
            .expect("no test panics while holding the events lock")
            .clone()
    }
}

/// Records `getattr ended` when the `getattr` future completes or is dropped.
struct GetattrEnded<'a>(&'a TestFs);

impl Drop for GetattrEnded<'_> {
    fn drop(&mut self) {
        self.0.record("getattr ended");
    }
}

impl Filesystem for TestFs {
    async fn init(&self, _req: Request) -> Result<ReplyInit> {
        if self.fail_init.load(Ordering::SeqCst) {
            return Err(libc::EXDEV.into());
        }

        Ok(ReplyInit::new(
            NonZeroU32::new(MAX_WRITE).expect("MAX_WRITE is a nonzero constant"),
        ))
    }

    // Not `async fn`, so the panic happens before a future exists.
    fn ready(&self, notify: Notify) -> impl Future<Output = ()> + Send {
        assert!(
            !self.panic_in_ready.load(Ordering::SeqCst),
            "ready panics to test ready panics"
        );
        async move {
            *self
                .notify
                .lock()
                .expect("no test panics while holding the notify lock") = Some(notify);
        }
    }

    async fn destroy(&self, _req: Request) {
        self.record("destroy");
        self.destroyed.store(true, Ordering::SeqCst);
    }

    async fn getattr(
        &self,
        _req: Request,
        _inode: Inode,
        _fh: Option<u64>,
        _flags: u32,
    ) -> Result<ReplyAttr> {
        let _ended = GetattrEnded(self);
        self.record("getattr");
        self.getattr_entered.notify_one();
        self.getattr_gate.notified().await;
        self.record("getattr done");

        Err(libc::ENOENT.into())
    }

    async fn readlink(&self, _req: Request, _inode: Inode) -> Result<ReplyData> {
        panic!("readlink panics to test handler panics");
    }

    async fn copy_file_range(
        &self,
        _req: Request,
        _inode: Inode,
        fh_in: u64,
        _off_in: u64,
        _inode_out: Inode,
        _fh_out: u64,
        _off_out: u64,
        length: u64,
        _flags: u64,
    ) -> Result<ReplyCopyFileRange> {
        self.copy_length.store(length, Ordering::SeqCst);

        let copied = if fh_in == OVERREPORTING_FH {
            length + 1
        } else {
            length
        };

        Ok(ReplyCopyFileRange { copied })
    }

    async fn unlink(&self, _req: Request, _parent: Inode, name: &OsStr) -> Result<()> {
        match name.to_str() {
            Some("zero") => Err(0.into()),
            Some("negative") => Err(libc::c_int::MIN.into()),
            _ => Ok(()),
        }
    }

    /// `user.size` always replies with a size and anything else with 4 bytes of data,
    /// whatever size the kernel asked for.
    async fn getxattr(
        &self,
        _req: Request,
        _inode: Inode,
        name: &OsStr,
        _size: u32,
    ) -> Result<ReplyXAttr> {
        if name == "user.size" {
            Ok(ReplyXAttr::Size(4))
        } else {
            Ok(ReplyXAttr::Data(XATTR_DATA.into()))
        }
    }

    /// `HELLO` always replies with a size and anything else with 4 bytes of data,
    /// whatever size the kernel asked for.
    async fn listxattr(&self, _req: Request, inode: Inode, _size: u32) -> Result<ReplyXAttr> {
        if inode == HELLO {
            Ok(ReplyXAttr::Size(4))
        } else {
            Ok(ReplyXAttr::Data(XATTR_DATA.into()))
        }
    }

    async fn lookup(&self, _req: Request, parent: Inode, name: &OsStr) -> Result<ReplyEntry> {
        if parent != ROOT || name != "hello" {
            return Err(libc::ENOENT.into());
        }

        let time = Timestamp::new(0, 0);

        Ok(ReplyEntry {
            ttl: Duration::from_secs(1),
            attr: FileAttr {
                ino: HELLO,
                size: 0,
                blocks: 0,
                atime: time,
                mtime: time,
                ctime: time,
                #[cfg(target_os = "macos")]
                crtime: time,
                kind: FileType::RegularFile,
                perm: 0o644,
                nlink: 1,
                uid: 0,
                gid: 0,
                rdev: 0,
                #[cfg(target_os = "macos")]
                flags: 0,
                blksize: 4096,
            },
            generation: 0,
        })
    }

    #[cfg(feature = "file-lock")]
    async fn getlk(
        &self,
        _req: Request,
        _inode: Inode,
        _fh: u64,
        _lock_owner: u64,
        _start: u64,
        _end: u64,
        _type: u32,
        _pid: u32,
    ) -> Result<ReplyLock> {
        Err(libc::ENOSYS.into())
    }

    #[cfg(feature = "file-lock")]
    async fn setlk(
        &self,
        _req: Request,
        _inode: Inode,
        _fh: u64,
        _lock_owner: u64,
        _start: u64,
        _end: u64,
        _type: u32,
        _pid: u32,
        _block: bool,
    ) -> Result<()> {
        Err(libc::ENOSYS.into())
    }
}

struct Harness {
    kernel: KernelSide,
    session: JoinHandle<IoResult<()>>,
    fs: Arc<TestFs>,
}

impl Harness {
    fn start() -> Self {
        Self::start_with_grace(super::CALLBACK_GRACE)
    }

    fn start_with_grace(grace: Duration) -> Self {
        Self::start_with(TestFs::default(), grace)
    }

    fn start_with(fs: TestFs, grace: Duration) -> Self {
        let (connection, kernel) = FuseConnection::memory();
        let fs = Arc::new(fs);

        let mut session = Session::new(MountOptions::default());
        session.fuse_connection = Some(Arc::new(connection));
        session.filesystem = Some(fs.clone());
        session.callback_grace = grace;

        Self {
            kernel,
            session: tokio::spawn(session.inner_mount()),
            fs,
        }
    }

    fn send(&self, opcode: u32, unique: u64, nodeid: u64, body: &[u8]) {
        self.kernel
            .requests
            .send(Ok(request_frame(opcode, unique, nodeid, body)))
            .expect("the session holds the request receiver until it ends");
    }

    fn send_frame(&self, frame: Vec<u8>) {
        self.kernel
            .requests
            .send(Ok(frame))
            .expect("the session holds the request receiver until it ends");
    }

    fn send_read_error(&self, errno: i32) {
        self.kernel
            .requests
            .send(Err(std::io::Error::from_raw_os_error(errno)))
            .expect("the session holds the request receiver until it ends");
    }

    async fn reply(&mut self) -> Vec<u8> {
        timeout(WAIT, self.kernel.replies.recv())
            .await
            .expect("reply within the timeout")
            .expect("the session holds the reply sender until it ends")
    }

    async fn init(&mut self, offered_flags: u32) -> Vec<u8> {
        self.send(
            fuse_opcode::FUSE_INIT as u32,
            1,
            0,
            &init_body(offered_flags),
        );
        self.reply().await
    }

    /// Closes the device the way an unmount does and waits for the session to end.
    async fn finish(self) -> Arc<TestFs> {
        drop(self.kernel.requests);

        timeout(WAIT, self.session)
            .await
            .expect("session ends within the timeout")
            .expect("session task does not panic")
            .expect("session ends without an error");

        self.fs
    }
}

fn request_frame(opcode: u32, unique: u64, nodeid: u64, body: &[u8]) -> Vec<u8> {
    let len = u32::try_from(FUSE_IN_HEADER_SIZE + body.len()).expect("test frames are small");

    let mut frame = Vec::with_capacity(FUSE_IN_HEADER_SIZE + body.len());
    frame.extend_from_slice(&len.to_ne_bytes());
    frame.extend_from_slice(&opcode.to_ne_bytes());
    frame.extend_from_slice(&unique.to_ne_bytes());
    frame.extend_from_slice(&nodeid.to_ne_bytes());
    // uid, gid, pid, padding
    frame.extend_from_slice(&[0; 16]);
    frame.extend_from_slice(body);

    assert_eq!(frame.len(), len as usize);
    frame
}

fn u32_at(frame: &[u8], offset: usize) -> u32 {
    u32::from_ne_bytes(
        frame[offset..offset + 4]
            .try_into()
            .expect("slice is 4 bytes"),
    )
}

fn u64_at(frame: &[u8], offset: usize) -> u64 {
    u64::from_ne_bytes(
        frame[offset..offset + 8]
            .try_into()
            .expect("slice is 8 bytes"),
    )
}

/// `(len, error, unique)` from a `fuse_out_header`.
fn out_header(reply: &[u8]) -> (usize, i32, u64) {
    assert!(
        reply.len() >= FUSE_OUT_HEADER_SIZE,
        "reply shorter than its header"
    );

    (
        u32_at(reply, 0) as usize,
        u32_at(reply, 4) as i32,
        u64_at(reply, 8),
    )
}

#[tokio::test]
async fn init_replies_with_negotiated_parameters() {
    let mut harness = Harness::start();
    let offered = FUSE_ASYNC_READ | FUSE_BIG_WRITES;

    let reply = harness.init(offered).await;

    assert_eq!(reply.len(), FUSE_OUT_HEADER_SIZE + FUSE_INIT_OUT_SIZE);
    assert_eq!(out_header(&reply), (reply.len(), 0, 1));

    let init_out = &reply[FUSE_OUT_HEADER_SIZE..];
    assert_eq!(u32_at(init_out, 0), FUSE_KERNEL_VERSION);
    assert_eq!(u32_at(init_out, 4), FUSE_KERNEL_MINOR_VERSION);
    assert_eq!(u32_at(init_out, 8), MAX_READAHEAD);
    let flags = u32_at(init_out, 12);
    assert_eq!(
        flags & !offered,
        0,
        "replied flags {flags:#x} were not offered"
    );
    assert_ne!(flags & FUSE_ASYNC_READ, 0);
    assert_eq!(u32_at(init_out, 20), MAX_WRITE);

    let fs = harness.finish().await;
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn lookup_replies_with_entry_or_errno() {
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 2, ROOT, b"hello\0");
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 3, ROOT, b"missing\0");

    // Handlers run concurrently, so match replies by unique.
    let mut replies = [harness.reply().await, harness.reply().await];
    replies.sort_by_key(|reply| out_header(reply).2);
    let [found, missing] = replies;

    assert_eq!(found.len(), FUSE_OUT_HEADER_SIZE + FUSE_ENTRY_OUT_SIZE);
    assert_eq!(out_header(&found), (found.len(), 0, 2));
    assert_eq!(u64_at(&found, FUSE_OUT_HEADER_SIZE), HELLO);

    assert_eq!(
        out_header(&missing),
        (FUSE_OUT_HEADER_SIZE, -libc::ENOENT, 3)
    );

    harness.finish().await;
}

#[tokio::test]
async fn unknown_opcode_gets_enosys_and_dispatch_continues() {
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.send(UNKNOWN_OPCODE, 4, ROOT, &[]);
    assert_eq!(
        out_header(&harness.reply().await),
        (FUSE_OUT_HEADER_SIZE, -libc::ENOSYS, 4)
    );

    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 5, ROOT, b"hello\0");
    assert_eq!(out_header(&harness.reply().await).1, 0);

    harness.finish().await;
}

fn copy_file_range_body(fh_in: u64, len: u64) -> Vec<u8> {
    let mut body = Vec::new();
    // fh_in, off_in, nodeid_out, fh_out, off_out, len, flags
    for field in [fh_in, 0, HELLO, 1, 0, len, 0] {
        body.extend_from_slice(&field.to_ne_bytes());
    }
    body
}

#[tokio::test]
async fn copy_file_range_bounds_delegated_length() {
    let mut harness = Harness::start();
    harness.init(0).await;
    let wire_max = u64::from(u32::MAX);

    let body = copy_file_range_body(1, wire_max + 10);
    harness.send(fuse_opcode::FUSE_COPY_FILE_RANGE as u32, 6, HELLO, &body);
    let reply = harness.reply().await;

    assert_eq!(harness.fs.copy_length.load(Ordering::SeqCst), wire_max);
    assert_eq!(reply.len(), FUSE_OUT_HEADER_SIZE + FUSE_WRITE_OUT_SIZE);
    assert_eq!(out_header(&reply), (reply.len(), 0, 6));
    assert_eq!(u32_at(&reply, FUSE_OUT_HEADER_SIZE), u32::MAX);

    let body = copy_file_range_body(OVERREPORTING_FH, wire_max + 10);
    harness.send(fuse_opcode::FUSE_COPY_FILE_RANGE as u32, 7, HELLO, &body);
    assert_eq!(
        out_header(&harness.reply().await),
        (FUSE_OUT_HEADER_SIZE, -libc::EIO, 7)
    );

    harness.finish().await;
}

#[tokio::test]
async fn invalid_errno_becomes_eio() {
    let mut harness = Harness::start();
    harness.init(0).await;

    for (unique, name) in [(8, b"zero\0".as_slice()), (9, b"negative\0")] {
        harness.send(fuse_opcode::FUSE_UNLINK as u32, unique, ROOT, name);
        assert_eq!(
            out_header(&harness.reply().await),
            (FUSE_OUT_HEADER_SIZE, -libc::EIO, unique)
        );
    }

    harness.send(fuse_opcode::FUSE_UNLINK as u32, 10, ROOT, b"hello\0");
    assert_eq!(
        out_header(&harness.reply().await),
        (FUSE_OUT_HEADER_SIZE, 0, 10)
    );

    harness.finish().await;
}

#[tokio::test]
async fn malformed_init_gets_einval_and_ends_session() {
    let mut harness = Harness::start();

    harness.send(fuse_opcode::FUSE_INIT as u32, 1, 0, &[0; 4]);
    assert_eq!(
        out_header(&harness.reply().await),
        (FUSE_OUT_HEADER_SIZE, -libc::EINVAL, 1)
    );

    let result = timeout(WAIT, harness.session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic");
    assert!(result.is_err(), "session ended with {result:?}");
}

/// A `fuse_getxattr_in` asking for `size` bytes, followed by `name` for GETXATTR.
fn xattr_body(size: u32, name: &[u8]) -> Vec<u8> {
    let mut body = size.to_ne_bytes().to_vec();
    body.resize(size_of::<fuse_getxattr_in>(), 0);
    body.extend_from_slice(name);
    body
}

#[tokio::test]
async fn invalid_xattr_reply_is_eio() {
    let getxattr = fuse_opcode::FUSE_GETXATTR as u32;
    let listxattr = fuse_opcode::FUSE_LISTXATTR as u32;
    let data_len = XATTR_DATA.len() as u32;
    let mut harness = Harness::start();
    harness.init(0).await;

    // (opcode, nodeid, requested size, name): each reply breaks the request.
    let invalid: [(u32, Inode, u32, &[u8]); 6] = [
        (getxattr, HELLO, data_len - 1, b"user.data\0"),
        (getxattr, HELLO, 0, b"user.data\0"),
        (getxattr, HELLO, 8, b"user.size\0"),
        (listxattr, ROOT, data_len - 1, b""),
        (listxattr, ROOT, 0, b""),
        (listxattr, HELLO, 8, b""),
    ];

    for (unique, (opcode, nodeid, size, name)) in (11..).zip(invalid) {
        harness.send(opcode, unique, nodeid, &xattr_body(size, name));
        assert_eq!(
            out_header(&harness.reply().await),
            (FUSE_OUT_HEADER_SIZE, -libc::EIO, unique),
            "opcode {opcode}, nodeid {nodeid}, size {size}"
        );
    }

    harness.send(getxattr, 20, HELLO, &xattr_body(data_len, b"user.data\0"));
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 20));
    assert_eq!(&reply[FUSE_OUT_HEADER_SIZE..], XATTR_DATA);

    harness.send(listxattr, 21, HELLO, &xattr_body(0, b""));
    let reply = harness.reply().await;
    assert_eq!(reply.len(), FUSE_OUT_HEADER_SIZE + FUSE_GETXATTR_OUT_SIZE);
    assert_eq!(out_header(&reply), (reply.len(), 0, 21));
    assert_eq!(u32_at(&reply, FUSE_OUT_HEADER_SIZE), data_len);

    harness.finish().await;
}

fn init_body(offered_flags: u32) -> Vec<u8> {
    let mut body = Vec::new();
    for field in [
        FUSE_KERNEL_VERSION,
        FUSE_KERNEL_MINOR_VERSION,
        MAX_READAHEAD,
        offered_flags,
    ] {
        body.extend_from_slice(&field.to_ne_bytes());
    }
    body
}

/// Frames whose header `len` disagrees with the bytes delivered, plus one shorter than a header.
fn malformed_frames(
    opcode: u32,
    unique: u64,
    nodeid: u64,
    body: &[u8],
) -> Vec<(&'static str, Vec<u8>)> {
    let frame = request_frame(opcode, unique, nodeid, body);
    let with_len = |len: usize| {
        let mut frame = frame.clone();
        let len = u32::try_from(len).expect("test frames are small");
        frame[..4].copy_from_slice(&len.to_ne_bytes());
        frame
    };

    vec![
        ("short header", frame[..FUSE_IN_HEADER_SIZE - 1].to_vec()),
        ("len below header size", with_len(FUSE_IN_HEADER_SIZE - 1)),
        ("len below bytes read", with_len(frame.len() - 1)),
        ("len above bytes read", with_len(frame.len() + 8)),
    ]
}

#[tokio::test]
async fn malformed_init_frame_ends_session() {
    let init = fuse_opcode::FUSE_INIT as u32;

    for (case, frame) in malformed_frames(init, 1, 0, &init_body(0)) {
        let Harness {
            mut kernel,
            session,
            fs,
        } = Harness::start();
        kernel
            .requests
            .send(Ok(frame))
            .expect("the session holds the request receiver until it ends");

        let err = timeout(WAIT, session)
            .await
            .expect("session ends within the timeout")
            .expect("session task does not panic")
            .expect_err(case);

        assert_eq!(err.kind(), ErrorKind::InvalidData, "{case}: {err}");
        assert!(kernel.replies.try_recv().is_err(), "{case}: got a reply");
        assert!(!fs.destroyed.load(Ordering::SeqCst), "{case}");
    }
}

#[tokio::test]
async fn malformed_frame_ends_session() {
    let lookup = fuse_opcode::FUSE_LOOKUP as u32;

    for (case, frame) in malformed_frames(lookup, 2, ROOT, b"hello\0") {
        let mut harness = Harness::start();
        harness.init(0).await;
        harness.send_frame(frame);

        let Harness {
            mut kernel,
            session,
            fs,
        } = harness;
        let err = timeout(WAIT, session)
            .await
            .expect("session ends within the timeout")
            .expect("session task does not panic")
            .expect_err(case);

        assert_eq!(err.kind(), ErrorKind::InvalidData, "{case}: {err}");
        assert!(kernel.replies.try_recv().is_err(), "{case}: got a reply");
        assert!(fs.destroyed.load(Ordering::SeqCst), "{case}");
    }
}

#[tokio::test]
async fn transient_read_errors_are_retried() {
    let mut harness = Harness::start();
    for errno in [libc::EINTR, libc::EAGAIN, libc::ENOENT] {
        harness.send_read_error(errno);
    }
    harness.init(0).await;

    for errno in [libc::EINTR, libc::EAGAIN, libc::ENOENT] {
        harness.send_read_error(errno);
    }
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 2, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 2));

    harness.finish().await;
}

#[tokio::test]
async fn connection_abort_ends_session_cleanly() {
    let mut harness = Harness::start();
    harness.init(0).await;
    harness.send_read_error(libc::ECONNABORTED);

    timeout(WAIT, harness.session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect("an aborted connection ends the session without an error");
    assert!(harness.fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn persistent_read_error_ends_session() {
    let mut harness = Harness::start();
    harness.init(0).await;
    harness.send_read_error(libc::EIO);

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.raw_os_error(), Some(libc::EIO), "{err}");
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn read_error_before_init_ends_session() {
    let harness = Harness::start();
    harness.send_read_error(libc::EIO);

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.raw_os_error(), Some(libc::EIO), "{err}");
    assert!(!fs.destroyed.load(Ordering::SeqCst));
}

/// Inits the session and returns the notifier the filesystem got in `ready`.
async fn ready_notify(harness: &mut Harness) -> Notify {
    harness.init(0).await;
    // Dispatch starts after `ready` returns, so any reply proves `ready` ran.
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 2, ROOT, b"hello\0");
    harness.reply().await;

    harness
        .fs
        .notify
        .lock()
        .expect("no test panics while holding the notify lock")
        .clone()
        .expect("ready stores the notifier")
}

#[tokio::test]
async fn rejected_notification_keeps_session() {
    // An errno the platform's kernel returns for a rejected notification without ending the connection.
    #[cfg(target_os = "linux")]
    let rejected = libc::ENOTEMPTY;
    #[cfg(target_os = "freebsd")]
    let rejected = libc::ENOSYS;
    #[cfg(not(any(target_os = "linux", target_os = "freebsd")))]
    let rejected = libc::ENOENT;

    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;

    harness.kernel.fail_next_write(rejected);
    notify.invalid_entry(ROOT, "hello".into()).await;

    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));

    harness.finish().await;
}

#[tokio::test]
async fn checked_invalidation_reports_a_local_rejection() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;
    notify.clone().try_invalid_inode(HELLO, 0, 0).await.unwrap();
    let frame = harness.reply().await;
    assert_eq!(
        out_header(&frame),
        (
            FUSE_OUT_HEADER_SIZE + FUSE_NOTIFY_INVAL_INODE_OUT_SIZE,
            fuse_notify_code::FUSE_NOTIFY_INVAL_INODE as i32,
            0
        )
    );
    harness.kernel.fail_next_write(libc::ENOENT);
    let error = notify.try_invalid_inode(HELLO, 0, 0).await.unwrap_err();
    assert_eq!(error.raw_os_error(), Some(libc::ENOENT));
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));
    harness.finish().await;
}

#[tokio::test]
async fn checked_invalidation_reports_a_broken_connection() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;
    harness.kernel.fail_next_write(libc::EIO);
    let error = notify.try_invalid_inode(HELLO, 0, 0).await.unwrap_err();
    assert_eq!(error.raw_os_error(), Some(libc::EIO));
    let (_, fs) = session_error(harness).await;
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn checked_invalidation_rejects_an_incomplete_write() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;
    harness.kernel.short_next_write(16);
    let error = notify.try_invalid_inode(HELLO, 0, 0).await.unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::WriteZero);
    let (_, fs) = session_error(harness).await;
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn checked_invalidation_waits_for_the_write_without_blocking_replies() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;
    let mut gate = harness.kernel.gate_next_write();
    let notification = tokio::spawn(async move { notify.try_invalid_inode(HELLO, 0, 0).await });
    timeout(WAIT, &mut gate.entered).await.unwrap().unwrap();
    assert!(!notification.is_finished());
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));
    assert!(gate.release());
    timeout(WAIT, notification).await.unwrap().unwrap().unwrap();
    assert_eq!(out_header(&harness.reply().await).2, 0);
    harness.finish().await;
}

#[tokio::test]
async fn failed_notification_write_ends_session() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;

    harness.kernel.fail_next_write(libc::EIO);
    notify.invalid_entry(ROOT, "hello".into()).await;

    let err = timeout(WAIT, harness.session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect_err("an unclassified notification error ends the session");
    assert_eq!(err.kind(), ErrorKind::Other, "{err}");
    assert!(harness.fs.destroyed.load(Ordering::SeqCst));
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn blocked_notification_does_not_hold_back_replies() {
    let mut harness = Harness::start();
    let notify = ready_notify(&mut harness).await;

    let mut gate = harness.kernel.gate_next_write();
    let notification =
        tokio::spawn(async move { notify.invalid_entry(ROOT, "hello".into()).await });
    timeout(WAIT, &mut gate.entered)
        .await
        .expect("the notification reaches its write within the timeout")
        .expect("the gate is kept until the write enters it");

    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));

    assert!(
        gate.release(),
        "the reply waited for the blocked notification"
    );
    timeout(WAIT, notification)
        .await
        .expect("the notification finishes within the timeout")
        .expect("the notification task does not panic");
    let notification = harness.reply().await;
    assert_eq!(out_header(&notification).2, 0);

    harness.finish().await;
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn rejected_reply_is_answered_with_eio() {
    let lookup = fuse_opcode::FUSE_LOOKUP as u32;
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.kernel.fail_next_write(libc::EINVAL);
    harness.send(UNKNOWN_OPCODE, 2, ROOT, &[]);
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (FUSE_OUT_HEADER_SIZE, -libc::EIO, 2));

    harness.send(lookup, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));

    harness.finish().await;
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn reply_rejected_after_completion_keeps_session() {
    let lookup = fuse_opcode::FUSE_LOOKUP as u32;
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.kernel.fail_next_write(libc::EINVAL);
    harness.kernel.fail_next_write(libc::ENOENT);
    // The serve loop replies to an unknown opcode before it reads the next request.
    harness.send(UNKNOWN_OPCODE, 2, ROOT, &[]);

    harness.send(lookup, 3, ROOT, b"hello\0");
    let reply = harness.reply().await;
    assert_eq!(out_header(&reply), (reply.len(), 0, 3));

    harness.finish().await;
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn failed_reply_fallback_ends_session() {
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.kernel.fail_next_write(libc::EINVAL);
    harness.kernel.fail_next_write(libc::ENODEV);
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 2, ROOT, b"hello\0");

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.kind(), ErrorKind::Other, "{err}");
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[cfg(not(target_os = "linux"))]
#[tokio::test]
async fn rejected_reply_ends_session() {
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.kernel.fail_next_write(libc::EINVAL);
    harness.send(fuse_opcode::FUSE_LOOKUP as u32, 2, ROOT, b"hello\0");

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.kind(), ErrorKind::Other, "{err}");
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

/// Inits the session and leaves a GETATTR (unique 2) waiting in `getattr`.
async fn start_with_getattr_waiting(grace: Duration) -> Harness {
    let mut harness = Harness::start_with_grace(grace);
    harness.init(0).await;

    harness.send(fuse_opcode::FUSE_GETATTR as u32, 2, HELLO, &[0; 16]);
    timeout(WAIT, harness.fs.getattr_entered.notified())
        .await
        .expect("getattr starts within the timeout");

    harness
}

#[tokio::test]
async fn destroy_waits_for_request_handlers() {
    let Harness {
        mut kernel,
        session,
        fs,
    } = start_with_getattr_waiting(WAIT).await;

    drop(kernel.requests);
    sleep(Duration::from_millis(50)).await;
    assert!(!session.is_finished(), "session ended with getattr running");
    assert_eq!(fs.events(), ["getattr"]);

    fs.getattr_gate.notify_one();
    let reply = timeout(WAIT, kernel.replies.recv())
        .await
        .expect("reply within the timeout")
        .expect("the session holds the reply sender until it ends");
    assert_eq!(out_header(&reply), (FUSE_OUT_HEADER_SIZE, -libc::ENOENT, 2));

    timeout(WAIT, session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect("session ends without an error");
    assert_eq!(
        fs.events(),
        ["getattr", "getattr done", "getattr ended", "destroy"]
    );
}

#[tokio::test]
async fn handlers_past_the_grace_period_are_cancelled_before_destroy() {
    let Harness {
        mut kernel,
        session,
        fs,
    } = start_with_getattr_waiting(Duration::from_millis(50)).await;

    drop(kernel.requests);

    timeout(WAIT, session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect("session ends without an error");
    assert_eq!(fs.events(), ["getattr", "getattr ended", "destroy"]);
    assert!(
        kernel.replies.try_recv().is_err(),
        "cancelled getattr replied"
    );
}

#[tokio::test]
async fn handler_panic_ends_session_after_destroy() {
    let mut harness = Harness::start();
    harness.init(0).await;

    harness.send(fuse_opcode::FUSE_READLINK as u32, 2, HELLO, &[]);

    let err = timeout(WAIT, harness.session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect_err("a handler panic ends the session");
    assert_eq!(err.kind(), ErrorKind::Other, "{err}");
    assert!(harness.fs.destroyed.load(Ordering::SeqCst));
}

async fn session_error(harness: Harness) -> (std::io::Error, Arc<TestFs>) {
    let err = timeout(WAIT, harness.session)
        .await
        .expect("session ends within the timeout")
        .expect("session task does not panic")
        .expect_err("the session ends with an error");
    (err, harness.fs)
}

#[tokio::test]
async fn failed_backend_init_skips_destroy() {
    let fs = TestFs::default();
    fs.fail_init.store(true, Ordering::SeqCst);
    let mut harness = Harness::start_with(fs, super::CALLBACK_GRACE);

    let reply = harness.init(0).await;
    assert_eq!(out_header(&reply), (FUSE_OUT_HEADER_SIZE, -libc::EXDEV, 1));

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.raw_os_error(), Some(libc::EXDEV), "{err}");
    assert!(!fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn failed_init_reply_runs_destroy() {
    let harness = Harness::start();

    harness.kernel.fail_next_write(libc::EIO);
    harness.send(fuse_opcode::FUSE_INIT as u32, 1, 0, &init_body(0));

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.raw_os_error(), Some(libc::EIO), "{err}");
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

#[tokio::test]
async fn ready_panic_runs_destroy() {
    let fs = TestFs::default();
    fs.panic_in_ready.store(true, Ordering::SeqCst);
    let mut harness = Harness::start_with(fs, super::CALLBACK_GRACE);

    harness.init(0).await;

    let (err, fs) = session_error(harness).await;
    assert_eq!(err.kind(), ErrorKind::Other, "{err}");
    assert!(fs.destroyed.load(Ordering::SeqCst));
}

/// Nothing is mounted here, so the OS unmount fails.
const NOT_MOUNTED: &str = "/nonexistent/infinidysk-fuse-test";

/// A [`MountHandle`] for a session on the in-memory connection at [`NOT_MOUNTED`].
fn mount_handle(fs: TestFs) -> (MountHandle, KernelSide, Arc<TestFs>) {
    let (connection, kernel) = FuseConnection::memory();
    let fs = Arc::new(fs);

    let mut session = Session::new(MountOptions::default());
    session.fuse_connection = Some(Arc::new(connection));
    session.filesystem = Some(fs.clone());

    let handle = MountHandle {
        inner: Some(MountHandleInner {
            task: session.start(),
            mount_path: PathBuf::from(NOT_MOUNTED),
            destroy_notify: Arc::new(async_notify::Notify::new()),
            #[cfg(any(
                all(target_os = "linux", feature = "unprivileged"),
                target_os = "macos"
            ))]
            unprivileged: false,
        }),
    };

    (handle, kernel, fs)
}

fn send_init(kernel: &KernelSide) {
    kernel
        .requests
        .send(Ok(request_frame(
            fuse_opcode::FUSE_INIT as u32,
            1,
            0,
            &init_body(0),
        )))
        .expect("the session holds the request receiver until it ends");
}

async fn init_reply(kernel: &mut KernelSide) -> Vec<u8> {
    send_init(kernel);
    timeout(WAIT, kernel.replies.recv())
        .await
        .expect("reply within the timeout")
        .expect("the session holds the reply sender until it ends")
}

async fn failed_mount_handle() -> (MountHandle, KernelSide) {
    let fs = TestFs::default();
    fs.fail_init.store(true, Ordering::SeqCst);
    let (handle, mut kernel, _fs) = mount_handle(fs);
    init_reply(&mut kernel).await;
    (handle, kernel)
}

#[tokio::test]
async fn unmount_returns_the_session_error_over_the_unmount_error() {
    let (handle, _kernel) = failed_mount_handle().await;

    let err = timeout(WAIT, handle.unmount())
        .await
        .expect("unmount within the timeout")
        .expect_err("the session failed");
    assert_eq!(err.raw_os_error(), Some(libc::EXDEV), "{err}");
}

#[tokio::test]
async fn unmount_after_the_handle_returned_still_unmounts() {
    let (mut handle, _kernel) = failed_mount_handle().await;

    let err = timeout(WAIT, &mut handle)
        .await
        .expect("session ends within the timeout")
        .expect_err("the session failed");
    assert_eq!(err.raw_os_error(), Some(libc::EXDEV), "{err}");

    let err = timeout(WAIT, handle.unmount())
        .await
        .expect("unmount within the timeout")
        .expect_err("nothing is mounted at the path");
    assert_ne!(err.raw_os_error(), Some(libc::EXDEV), "{err}");
}

#[tokio::test]
async fn dropping_the_handle_off_the_runtime_does_not_panic() {
    let (handle, mut kernel, _fs) = mount_handle(TestFs::default());
    init_reply(&mut kernel).await;

    std::thread::spawn(move || drop(handle))
        .join()
        .expect("dropping the handle does not panic");
}
