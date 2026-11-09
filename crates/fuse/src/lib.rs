//! FUSE mount of the virtual tree. Linux only.
//!
//! Read-write only so apps can delete their links after an import (accepted, nothing
//! changes); every other write fails with `EROFS`.
//!
//! Inodes map to tree paths in a short-held table, so a slow lookup never blocks reads;
//! each open file owns one engine [`File`].
#![cfg(target_os = "linux")]

use std::collections::HashMap;
use std::ffi::{OsStr, OsString};
use std::io::{Read as _, SeekFrom};
use std::num::NonZeroU32;
use std::os::unix::fs::MetadataExt;
use std::panic::AssertUnwindSafe;
use std::path::{Path, PathBuf};
use std::pin::pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant, UNIX_EPOCH};

use bytes::{BufMut, BytesMut};
use fuse3::raw::MountHandle;
use fuse3::raw::prelude::*;
use fuse3::{Errno, Inode, MountOptions, Result};
use futures_util::stream::{self, Stream};
use futures_util::{FutureExt as _, StreamExt as _};
use infinidysk_vfs::{Delivery, Handle as File, Kernel, Kind, Node, Origin, ReadStage, Vfs};
use nix::libc;
use nix::mount::{MntFlags, MsFlags, umount2};
use nix::unistd::SysconfVar;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncSeek, AsyncSeekExt};
use tokio::sync::{Notify, watch};
use tokio_util::sync::CancellationToken;
use tracing::{debug, error, info, trace, warn};

/// Per-request log target: `RUST_LOG=infinidysk_fuse::ops=debug` logs every kernel request
/// but reads, `=trace` adds reads.
const OPS: &str = "infinidysk_fuse::ops";

/// New and removed names show within this.
const ENTRY_TTL: Duration = Duration::from_secs(1);
/// Files never change in place, so attributes can live longer.
const ATTR_TTL: Duration = Duration::from_secs(30);
/// Names and attributes computed from the path alone never change or go away, so a
/// library symlink walk through `/.ids` revalidates only its final, database-backed name.
const FIXED_TTL: Duration = Duration::from_hours(1);
const ROOT: Inode = 1;
/// Kernel `FOPEN_KEEP_CACHE`: content at a path never changes, keep its page cache.
const FOPEN_KEEP_CACHE: u32 = 1 << 1;
/// `st_blksize`; stdio and copy tools size their reads from it.
const BLOCK_SIZE: u32 = 1 << 20;
const MAX_BACKOFF: Duration = Duration::from_secs(60);
/// `/proc/<pid>/status` is a few KB; never read more.
const MAX_PROC: u64 = 16 * 1024;
/// Characters of a process name kept in read history.
const MAX_NAME: usize = 64;
/// Tools that read for another app; read history also names the app that started them.
const HELPERS: [&str; 3] = ["ffmpeg", "ffprobe", "mediainfo"];
/// Shells a helper may run under; the parent walk looks past them.
const SHELLS: [&str; 4] = ["sh", "ash", "bash", "dash"];
/// Parents that only start processes; reaching one means no app is known.
const LAUNCHERS: [&str; 8] = [
    "init",
    "systemd",
    "tini",
    "docker-init",
    "dumb-init",
    "s6-supervise",
    "s6-svscan",
    "supervisord",
];
/// Parents looked at to find the app behind a helper.
const MAX_PARENT_HOPS: usize = 4;
/// The kernel sends a handle's reads concurrently, so one can overtake the read before
/// it. A read close ahead of an idle handle waits this long for the gap to fill instead
/// of seeking away from the stream's prefetch.
const ORDER_WAIT: Duration = Duration::from_millis(20);
/// How long an interrupt waits for its request to show up, and the pace at which the
/// kernel resends one we cannot act on yet.
const INTERRUPT_RECHECK: Duration = Duration::from_millis(20);
/// A handle position nobody knows: after an error.
const UNKNOWN: u64 = u64::MAX;

pub struct Config {
    pub mount_dir: PathBuf,
    /// Owner of every file (PUID/PGID).
    pub uid: u32,
    pub gid: u32,
    /// Largest read request, bytes.
    pub max_read: u32,
    /// Kernel readahead, bytes.
    pub max_readahead: u32,
    /// Kernel requests in flight before it throttles.
    pub max_background: u16,
    /// Media files report 1970 times, as an rclone mount with `--no-modtime` did.
    pub epoch_times: bool,
}

/// Keeps the tree mounted until `cancel` fires: clears a stale mount first, remounts with
/// backoff when the session dies and unmounts on the way out. Without `/dev/fuse` it logs
/// one warning and returns; the rest of the server keeps running.
///
/// `state` holds what the kernel applied while mounted, otherwise why not mounted.
pub async fn run(
    vfs: Arc<Vfs>,
    config: Config,
    state: watch::Sender<std::result::Result<Kernel, String>>,
    cancel: CancellationToken,
) {
    let dir = config.mount_dir.clone();
    if !Path::new("/dev/fuse").exists() {
        state.send_modify(|s| *s = Err("/dev/fuse is missing".to_owned()));
        warn!(
            reason = "/dev/fuse is missing",
            "FUSE mount disabled; in docker compose add `devices: [/dev/fuse]`, \
             `cap_add: [SYS_ADMIN]`, `security_opt: [apparmor:unconfined]` and mount the \
             volume with `:rshared`; or, using WebDAV only, turn the built-in mount off \
             in Settings → Mount or set `INFINIDYSK__MOUNT__FUSE=false`"
        );
        return;
    }
    // ponytail: container detection is the docker/podman marker files only.
    let in_container =
        Path::new("/.dockerenv").exists() || Path::new("/run/.containerenv").exists();
    if in_container
        && let Ok(mountinfo) = tokio::fs::read_to_string("/proc/self/mountinfo").await
        && shared(&mountinfo, &dir) == Some(false)
    {
        warn!(
            path = %dir.display(),
            reason = "its parent mount is private",
            "FUSE mount is visible only inside this container; bind its parent directory with \
             `:rshared` (e.g. `/mnt:/mnt:rshared`) so the host and other containers see it"
        );
    }
    let mut delay = Duration::from_secs(1);
    loop {
        if unmount_stale(&dir).await {
            info!(path = %dir.display(), "cleared a stale FUSE mount; the previous session did not unmount");
        }
        let started = Instant::now();
        match mount(Arc::clone(&vfs), &config, cancel.child_token()).await {
            Err(error) => {
                warn!(path = %dir.display(), reason = %error, "FUSE mount failed");
                state.send_modify(|s| *s = Err(error.to_string()));
            }
            Ok(mut handle) => {
                info!(path = %dir.display(), "FUSE mounted");
                let kernel = tune(&config).await;
                state.send_modify(|s| *s = Ok(kernel));
                // Stopping cancels reads in flight, so the unmount does not wait on Usenet.
                tokio::select! {
                    ended = &mut handle => {
                        let reason = ended.err().map_or_else(|| "unmounted externally".to_owned(), |e| e.to_string());
                        warn!(path = %dir.display(), reason = %reason, "FUSE session ended; remounting");
                        state.send_modify(|s| *s = Err(reason));
                    }
                    () = cancel.cancelled() => {
                        let open_files = vfs.sessions().open().len();
                        info!(path = %dir.display(), open_files, "unmounting FUSE");
                        let started = Instant::now();
                        match handle.unmount().await {
                            Ok(()) => info!(path = %dir.display(), ms = started.elapsed().as_millis(), "FUSE unmounted"),
                            Err(error) => {
                                warn!(path = %dir.display(), reason = %error, "FUSE unmount failed; detaching");
                                unmount_stale(&dir).await;
                            }
                        }
                        return;
                    }
                }
            }
        }
        if started.elapsed() > MAX_BACKOFF {
            delay = Duration::from_secs(1);
        }
        tokio::select! {
            () = tokio::time::sleep(delay) => {}
            () = cancel.cancelled() => return,
        }
        delay = (delay * 2).min(MAX_BACKOFF);
    }
}

async fn mount(
    vfs: Arc<Vfs>,
    config: &Config,
    stopping: CancellationToken,
) -> std::io::Result<MountHandle> {
    tokio::fs::create_dir_all(&config.mount_dir).await?;
    let mut options = MountOptions::default();
    options
        .fs_name("infinidysk")
        .allow_other(true)
        .default_permissions(true)
        .nonempty(true)
        .force_readdir_plus(true)
        .custom_options(format!("max_read={}", config.max_read));
    let mut fs = Fs::new(vfs, config.uid, config.gid, stopping);
    // The kernel keeps up to two readahead windows of reads in flight.
    fs.order_span = 2 * u64::from(config.max_readahead) + u64::from(config.max_read);
    fs.epoch_times = config.epoch_times;
    let session = Session::new(options);
    // The helper's connection reads requests without a blocking-pool hop each, so root
    // uses it too and mounts directly only without fusermount3.
    if nix::unistd::geteuid().is_root() && !has_fusermount3() {
        session.mount(fs, &config.mount_dir).await
    } else {
        session.mount_with_unprivileged(fs, &config.mount_dir).await
    }
}

fn has_fusermount3() -> bool {
    std::env::var_os("PATH").is_some_and(|path| {
        std::env::split_paths(&path).any(|dir| dir.join("fusermount3").is_file())
    })
}

/// Asks the filesystem at `dir` for its usage and lists its root, the way a reader would.
/// Fails when `dir` is not a FUSE mount; blocks for as long as the FUSE server does not
/// answer, so run it on a thread that may hang.
pub fn answers(dir: &Path) -> std::io::Result<()> {
    use nix::sys::statfs::{FUSE_SUPER_MAGIC, statfs};
    if statfs(dir)?.filesystem_type() != FUSE_SUPER_MAGIC {
        return Err(std::io::Error::other("not a FUSE mount"));
    }
    for entry in std::fs::read_dir(dir)? {
        entry?;
    }
    Ok(())
}

/// Whether the mount holding `dir` propagates new mounts to its peers; `None` when no
/// mount holds it.
fn shared(mountinfo: &str, dir: &Path) -> Option<bool> {
    mountinfo
        .lines()
        .filter_map(|line| {
            let mut fields = line.split(' ');
            let point = unescape(fields.nth(4)?);
            let shared = fields
                .skip(1)
                .take_while(|f| *f != "-")
                .any(|f| f.starts_with("shared:"));
            dir.starts_with(&point)
                .then(|| (point.as_os_str().len(), shared))
        })
        // Last of equals wins: a later line at the same point is mounted on top.
        .max_by_key(|(depth, _)| *depth)
        .map(|(_, shared)| shared)
}

/// Undoes mountinfo's octal escapes (`\040` for a space).
fn unescape(field: &str) -> PathBuf {
    use std::os::unix::ffi::OsStringExt as _;
    let bytes = field.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if let Some(byte) = field
            .get(i + 1..i + 4)
            .filter(|_| bytes[i] == b'\\')
            .and_then(|o| u8::from_str_radix(o, 8).ok())
        {
            out.push(byte);
            i += 4;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    PathBuf::from(OsString::from_vec(out))
}

/// Lazily detaches whatever is mounted at `dir`; true when something was.
async fn unmount_stale(dir: &Path) -> bool {
    let result = if nix::unistd::geteuid().is_root() {
        umount2(dir, MntFlags::MNT_DETACH).map_err(std::io::Error::from)
    } else {
        tokio::process::Command::new("fusermount3")
            .arg("-uqz")
            .arg(dir)
            .status()
            .await
            // Quiet mode exits non-zero when nothing is mounted.
            .and_then(|s| {
                s.success()
                    .then_some(())
                    .ok_or_else(|| std::io::Error::from_raw_os_error(libc::EINVAL))
            })
    };
    match result {
        Ok(()) => true,
        Err(error) if matches!(error.raw_os_error(), Some(libc::EINVAL | libc::ENOENT)) => false,
        Err(error) => {
            debug!(path = %dir.display(), reason = %error, "stale FUSE unmount failed");
            false
        }
    }
}

/// Readahead and background limits live in sysfs, not the init reply. Best effort: a
/// container's read-only `/sys` keeps the kernel's readahead. Returns what the kernel
/// ended up with.
async fn tune(config: &Config) -> Kernel {
    let dev = match tokio::fs::metadata(&config.mount_dir).await {
        Ok(meta) => meta.dev(),
        Err(error) => {
            debug!(reason = %error, "FUSE tuning skipped");
            return Kernel::default();
        }
    };
    // glibc `dev_t` layout; the kernel names the connection by its own (major << 20 | minor).
    let major = ((dev >> 8) & 0xfff) | ((dev >> 32) & !0xfff);
    let minor = (dev & 0xff) | ((dev >> 12) & !0xff);
    let readahead = format!("/sys/class/bdi/{major}:{minor}/read_ahead_kb");
    let written = match page_size() {
        Some(page) => {
            let kb = read_ahead_kb(config.max_readahead, page);
            tokio::fs::write(&readahead, kb.to_string()).await
        }
        None => Err(std::io::Error::other("the kernel page size is unknown")),
    };
    if let Err(error) = written {
        warn!(
            reason = %error,
            "FUSE readahead could not be set from this process; in rootful Docker add the volume \
             `/sys/devices/virtual/bdi:/sys/devices/virtual/bdi:rw`; rootless Docker may need \
             host-side tuning by an administrator"
        );
    }
    let connection = connection_dir((major << 20) | minor);
    let (background, congestion) = (
        connection.join("max_background"),
        connection.join("congestion_threshold"),
    );
    let written = async {
        tokio::fs::write(&background, config.max_background.to_string()).await?;
        let threshold = u32::from(config.max_background) * 3 / 4;
        tokio::fs::write(&congestion, threshold.to_string()).await
    };
    if let Err(error) = written.await {
        warn!(
            reason = %error,
            "FUSE background limits could not all be set from this process; fusectl must be \
             accessible and writable; in rootful Docker use `cap_add: [SYS_ADMIN]`; \
             rootless Docker may need host-side tuning by an administrator"
        );
    }
    let kernel = Kernel {
        read_ahead_kb: read_number(Path::new(&readahead)).await,
        max_background: read_number(&background).await,
        congestion_threshold: read_number(&congestion).await,
        connection: Some(connection),
    };
    info!(
        read_ahead_kb = kernel.read_ahead_kb,
        max_background = kernel.max_background,
        congestion_threshold = kernel.congestion_threshold,
        "FUSE kernel limits"
    );
    kernel
}

async fn read_number<T: std::str::FromStr>(path: &Path) -> Option<T> {
    tokio::fs::read_to_string(path)
        .await
        .ok()?
        .trim()
        .parse()
        .ok()
}

/// The kernel keeps readahead in whole pages and rounds down, so round up: the setting is
/// a floor, not a ceiling.
fn read_ahead_kb(bytes: u32, page: u64) -> u64 {
    u64::from(bytes).div_ceil(page) * (page / 1024)
}

fn page_size() -> Option<u64> {
    let page = nix::unistd::sysconf(SysconfVar::PAGE_SIZE).ok()??;
    u64::try_from(page)
        .ok()
        .filter(|page| *page >= 1024 && page % 1024 == 0)
}

/// A container's `/sys` lacks the FUSE control filesystem, but with `SYS_ADMIN` it can
/// mount its own, which accepts the background limits.
fn connection_dir(id: u64) -> PathBuf {
    let host = PathBuf::from(format!("/sys/fs/fuse/connections/{id}"));
    if host.exists() || !nix::unistd::geteuid().is_root() {
        return host;
    }
    let own = std::env::temp_dir().join("infinidysk-fusectl");
    if !own.join(id.to_string()).exists() {
        let flags = MsFlags::MS_NOSUID | MsFlags::MS_NODEV | MsFlags::MS_NOEXEC;
        // A failure here surfaces as the mount's ENOENT.
        let _ = std::fs::create_dir_all(&own);
        let mounted =
            nix::mount::mount(Some("fusectl"), &own, Some("fusectl"), flags, None::<&str>);
        if let Err(error) = mounted {
            debug!(path = %own.display(), reason = %error, "fusectl mount failed");
        }
    }
    own.join(id.to_string())
}

struct Fs {
    vfs: Arc<Vfs>,
    uid: u32,
    gid: u32,
    inodes: Mutex<Inodes>,
    handles: Mutex<HashMap<u64, Handle>>,
    next_fh: AtomicU64,
    /// Whether the hint about readers in other PID namespaces was logged.
    hinted: AtomicBool,
    /// Requests the kernel may interrupt, by request id: their cancel token and caller.
    live: Mutex<HashMap<u64, (CancellationToken, u32)>>,
    /// Cancels every live request when the mount stops.
    stopping: CancellationToken,
    /// How far ahead of its handle a read waits for the reads before it.
    order_span: u64,
    epoch_times: bool,
}

#[derive(Clone)]
enum Handle {
    /// An open file and its attributes at open, which stay right while it is open:
    /// content at a path never changes.
    File(Arc<OpenFile>, FileAttr),
    /// Listing snapshot, so paging through a directory is one query.
    Dir(Arc<[Node]>),
}

struct OpenFile<R = File> {
    path: Arc<str>,
    /// `None` once a read was cut short mid-way (interrupt or panic): the reader's state is
    /// unknown, so later reads fail rather than return wrong bytes.
    reader: tokio::sync::Mutex<Option<R>>,
    /// Where the reader stands or the read holding it will end, or [`UNKNOWN`].
    pos: AtomicU64,
    /// Offsets of reads not yet holding the reader.
    waiting: Mutex<Vec<u64>>,
    order_span: u64,
    /// Fires when a read ends.
    moved: Notify,
    /// Lists reads in progress for live status.
    session: Option<Arc<infinidysk_vfs::Session>>,
}

/// A read's place among those waiting on its handle; leaves on drop.
struct Turn<'a> {
    waiting: &'a Mutex<Vec<u64>>,
    offset: u64,
}

impl Turn<'_> {
    fn lowest(&self) -> bool {
        lock(self.waiting).iter().all(|o| *o >= self.offset)
    }
}

impl Drop for Turn<'_> {
    fn drop(&mut self) {
        let mut waiting = lock(self.waiting);
        if let Some(i) = waiting.iter().position(|o| *o == self.offset) {
            waiting.swap_remove(i);
        }
    }
}

impl<R: AsyncRead + AsyncSeek + Unpin> OpenFile<R> {
    async fn read(&self, id: u64, offset: u64, size: u32) -> std::io::Result<BytesMut> {
        let listed = self.session.as_ref().map(|s| s.request(id, offset, size));
        let stage = |stage| {
            if let Some(listed) = &listed {
                listed.stage(stage);
            }
        };
        lock(&self.waiting).push(offset);
        let turn = Turn {
            waiting: &self.waiting,
            offset,
        };
        let mut deadline = tokio::time::Instant::now() + ORDER_WAIT;
        loop {
            let mut moved = pin!(self.moved.notified());
            moved.as_mut().enable();
            if !should_wait(self.pos.load(Ordering::Acquire), offset, self.order_span) {
                break;
            }
            // Behind a busy reader the wait is free, and joining its queue early would
            // overtake the reads before this one, turning a slow fetch into seeks. The probe
            // also fails while another waiter probes, which no read ending will follow, so
            // the wait stays bounded and probes again.
            let busy = self.reader.try_lock().is_err();
            if tokio::time::timeout_at(deadline, moved).await.is_err() && !busy && turn.lowest() {
                // The gap did not fill: the lowest waiting read goes, the rest follow it.
                break;
            }
            deadline = tokio::time::Instant::now() + ORDER_WAIT;
        }
        stage(ReadStage::Lock);
        let mut slot = self.reader.lock().await;
        stage(ReadStage::Read);
        let mut reader = slot
            .take()
            .ok_or_else(|| std::io::Error::other("an earlier read on this handle was cut short"))?;
        let at = self
            .pos
            .swap(offset.saturating_add(u64::from(size)), Ordering::AcqRel);
        drop(turn);
        let size = size as usize;
        let mut buf = BytesMut::with_capacity(size);
        let result = async {
            if at != offset {
                reader.seek(SeekFrom::Start(offset)).await?;
            }
            while buf.len() < size {
                let want = size - buf.len();
                if reader.read_buf(&mut (&mut buf).limit(want)).await? == 0 {
                    break;
                }
            }
            std::io::Result::Ok(())
        }
        .await;
        // Before the unlock, so the next read sees where this one ended.
        let end = if result.is_ok() {
            offset + buf.len() as u64
        } else {
            UNKNOWN
        };
        self.pos.store(end, Ordering::Release);
        *slot = Some(reader);
        drop(slot);
        result?;
        Ok(buf)
    }
}

/// Whether a read at `offset` is less than `span` ahead of the handle at `pos`, so the
/// reads before it are likely still on their way.
fn should_wait(pos: u64, offset: u64, span: u64) -> bool {
    pos != UNKNOWN && offset > pos && offset - pos < span
}

/// Whether `/proc/<tid>/status` shows a pending `SIGKILL`, which the kernel also sets for
/// any fatal signal: the caller is dying, not merely signalled. Other signals leave its
/// read running, as programs rarely expect `EINTR` from a regular file.
fn dying(status: &str) -> bool {
    const KILL: u64 = 1 << (libc::SIGKILL - 1);
    status
        .lines()
        .filter_map(|line| {
            line.strip_prefix("SigPnd:")
                .or_else(|| line.strip_prefix("ShdPnd:"))
        })
        .filter_map(|mask| u64::from_str_radix(mask.trim(), 16).ok())
        .any(|mask| mask & KILL != 0)
}

/// Runs one request, turning a panic into `EIO`: the session reads requests on and a
/// panicking request would otherwise never get a reply, hanging its caller.
async fn guarded<T>(op: &'static str, request: impl Future<Output = Result<T>>) -> Result<T> {
    AssertUnwindSafe(request)
        .catch_unwind()
        .await
        .unwrap_or_else(|_| {
            error!(op, "FUSE request panicked; replying EIO");
            Err(libc::EIO.into())
        })
}

/// Removes a live request when it ends.
struct Live<'a> {
    live: &'a Mutex<HashMap<u64, (CancellationToken, u32)>>,
    unique: u64,
}

impl Drop for Live<'_> {
    fn drop(&mut self) {
        lock(self.live).remove(&self.unique);
    }
}

struct Inodes {
    next: Inode,
    by_ino: HashMap<Inode, (Arc<str>, u64)>,
    by_path: HashMap<Arc<str>, Inode>,
}

impl Inodes {
    fn path(&self, ino: Inode) -> Option<Arc<str>> {
        self.by_ino.get(&ino).map(|(path, _)| Arc::clone(path))
    }

    /// The inode for `path`, counting one kernel lookup.
    fn remember(&mut self, path: &str) -> Inode {
        if let Some(&ino) = self.by_path.get(path) {
            if let Some((_, lookups)) = self.by_ino.get_mut(&ino) {
                *lookups += 1;
            }
            return ino;
        }
        let ino = self.next;
        self.next += 1;
        let path: Arc<str> = path.into();
        self.by_path.insert(Arc::clone(&path), ino);
        self.by_ino.insert(ino, (path, 1));
        ino
    }

    fn forget(&mut self, ino: Inode, n: u64) {
        if ino == ROOT {
            return;
        }
        if let Some((path, lookups)) = self.by_ino.get_mut(&ino) {
            *lookups = lookups.saturating_sub(n);
            if *lookups == 0 {
                self.by_path.remove(path);
                self.by_ino.remove(&ino);
            }
        }
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// A FUSE caller as `/proc` shows it.
struct Process {
    name: String,
    pid: u32,
    /// The app that started a helper such as ffmpeg; best effort, for display only.
    parent: Option<String>,
}

/// The process thread `tid` belongs to; `None` when `/proc` hides it.
fn process(tid: u32) -> Option<Process> {
    let status = read_proc(&format!("/proc/{tid}/status"))?;
    let (name, pid) = parse_status(&status)?;
    let name = if pid == tid {
        name
    } else {
        // A thread may rename itself; the process goes by its main thread's name.
        read_proc(&format!("/proc/{pid}/comm"))
            .map(|comm| comm.trim_end().to_owned())
            .filter(|comm| !comm.is_empty())
            .unwrap_or(name)
    };
    // Threads share their process's parent.
    let parent = HELPERS
        .contains(&name.as_str())
        .then(|| {
            parent_app(pid, parse_ppid(&status), |p| {
                let status = read_proc(&format!("/proc/{p}/status"))?;
                Some((parse_status(&status)?.0, parse_ppid(&status)))
            })
        })
        .flatten();
    Some(Process { name, pid, parent })
}

/// The first app above `reader`, walking past shells and helpers; `None` at a launcher,
/// pid 1, a loop, a hidden parent, or after [`MAX_PARENT_HOPS`].
fn parent_app(
    reader: u32,
    ppid: Option<u32>,
    mut lookup: impl FnMut(u32) -> Option<(String, Option<u32>)>,
) -> Option<String> {
    let mut seen = vec![reader];
    let mut next = ppid;
    for _ in 0..MAX_PARENT_HOPS {
        let pid = next.filter(|&p| p > 1 && !seen.contains(&p))?;
        seen.push(pid);
        let (name, ppid) = lookup(pid)?;
        let name = name.as_str();
        if name.is_empty() || LAUNCHERS.contains(&name) || name.starts_with("containerd-shim") {
            return None;
        }
        if !SHELLS.contains(&name) && !HELPERS.contains(&name) {
            return Some(name.to_owned());
        }
        next = ppid;
    }
    None
}

fn read_proc(path: &str) -> Option<String> {
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .ok()?
        .take(MAX_PROC)
        .read_to_end(&mut bytes)
        .ok()?;
    Some(String::from_utf8_lossy(&bytes).into_owned())
}

/// `Name` and `Tgid` (the process id) from `/proc/<tid>/status`.
fn parse_status(status: &str) -> Option<(String, u32)> {
    let field = |key: &str| {
        status
            .lines()
            .find_map(|line| line.strip_prefix(key)?.strip_prefix(':'))
            .map(str::trim)
    };
    Some((field("Name")?.to_owned(), field("Tgid")?.parse().ok()?))
}

/// `PPid` from `/proc/<tid>/status`.
fn parse_ppid(status: &str) -> Option<u32> {
    status
        .lines()
        .find_map(|line| line.strip_prefix("PPid:"))?
        .trim()
        .parse()
        .ok()
}

/// `[app › ]name (pid P, tid T) · uid U gid G`, or the ids alone when the process is hidden.
fn caller_label(process: Option<Process>, tid: u32, uid: u32, gid: u32) -> String {
    let ids = format!("uid {uid} gid {gid}");
    let clean = |name: &str| -> String {
        name.chars()
            .filter(|&c| !c.is_control() && c != '›')
            .take(MAX_NAME)
            .collect()
    };
    match process {
        None if tid == 0 => format!("pid unavailable · {ids}"),
        None => format!("pid {tid} · {ids}"),
        Some(Process { name, pid, parent }) => {
            let name = match parent.map(|app| clean(&app)) {
                Some(app) if !app.is_empty() => format!("{app} › {}", clean(&name)),
                _ => clean(&name),
            };
            let thread = if pid == tid {
                String::new()
            } else {
                format!(", tid {tid}")
            };
            format!("{name} (pid {pid}{thread}) · {ids}")
        }
    }
}

fn child_path(parent: &str, name: &OsStr) -> Result<String> {
    let name = name.to_str().ok_or_else(Errno::new_not_exist)?;
    Ok(if parent == "/" {
        format!("/{name}")
    } else {
        format!("{parent}/{name}")
    })
}

impl Fs {
    fn new(vfs: Arc<Vfs>, uid: u32, gid: u32, stopping: CancellationToken) -> Self {
        let root: Arc<str> = "/".into();
        Self {
            vfs,
            uid,
            gid,
            inodes: Mutex::new(Inodes {
                next: ROOT + 1,
                by_ino: HashMap::from([(ROOT, (Arc::clone(&root), 1))]),
                by_path: HashMap::from([(root, ROOT)]),
            }),
            handles: Mutex::new(HashMap::new()),
            next_fh: AtomicU64::new(1),
            hinted: AtomicBool::new(false),
            live: Mutex::new(HashMap::new()),
            stopping,
            order_span: 0,
            epoch_times: false,
        }
    }

    /// Runs `request` so [`Filesystem::interrupt`] or a stopping mount can end it with
    /// `EINTR`.
    async fn interruptible<T>(
        &self,
        req: &Request,
        request: impl Future<Output = Result<T>>,
    ) -> Result<T> {
        let token = self.stopping.child_token();
        lock(&self.live).insert(req.unique, (token.clone(), req.pid));
        let _live = Live {
            live: &self.live,
            unique: req.unique,
        };
        tokio::select! {
            biased;
            () = token.cancelled() => Err(libc::EINTR.into()),
            result = request => result,
        }
    }

    fn path(&self, ino: Inode) -> Result<Arc<str>> {
        lock(&self.inodes)
            .path(ino)
            .ok_or_else(Errno::new_not_exist)
    }

    async fn node(&self, path: &str) -> Result<Node> {
        match self.vfs.lookup(path).await {
            Ok(Some(node)) => Ok(node),
            Ok(None) => Err(Errno::new_not_exist()),
            Err(error) => {
                warn!(path, reason = %error, "FUSE lookup failed");
                Err(libc::EIO.into())
            }
        }
    }

    fn handle(&self, fh: u64) -> Result<Handle> {
        lock(&self.handles)
            .get(&fh)
            .cloned()
            .ok_or_else(|| libc::EBADF.into())
    }

    /// Logs one kernel request on [`OPS`]; fields are only built when that is enabled.
    fn traced<T>(
        &self,
        req: &Request,
        op: &'static str,
        ino: Inode,
        name: Option<&OsStr>,
        started: Instant,
        result: Result<T>,
    ) -> Result<T> {
        debug!(
            target: OPS,
            op,
            path = %self.describe(ino, name),
            // procfs reads never wait on a disk.
            caller = %caller_label(process(req.pid), req.pid, req.uid, req.gid),
            errno = result.as_ref().err().map_or(0, |e| -libc::c_int::from(*e)),
            us = started.elapsed().as_micros(),
        );
        result
    }

    /// The path of `ino` (joined with `name`), or the inode when this session never saw it.
    fn describe(&self, ino: Inode, name: Option<&OsStr>) -> String {
        let base = lock(&self.inodes)
            .path(ino)
            .map_or_else(|| format!("<unknown inode {ino}>"), |p| p.to_string());
        match name {
            Some(name) => format!("{}/{}", base.trim_end_matches('/'), name.to_string_lossy()),
            None => base,
        }
    }

    fn insert(&self, handle: Handle) -> u64 {
        let fh = self.next_fh.fetch_add(1, Ordering::Relaxed);
        lock(&self.handles).insert(fh, handle);
        fh
    }

    /// Who opened a file, for read history. The kernel reports the caller's thread as seen
    /// from the PID namespace that mounted, or 0 when the caller is outside it.
    async fn caller(&self, req: &Request) -> String {
        let tid = req.pid;
        let process = if tid == 0 {
            if !self.hinted.swap(true, Ordering::Relaxed) {
                info!(
                    "FUSE reader runs outside this container's PID namespace (another \
                     container or the host); read history shows its uid/gid only. \
                     Run this container with `pid: host` to see process names"
                );
            }
            None
        } else {
            tokio::task::spawn_blocking(move || process(tid))
                .await
                .ok()
                .flatten()
        };
        caller_label(process, tid, req.uid, req.gid)
    }

    async fn delete(&self, parent: Inode, name: &OsStr) -> Result<()> {
        let path = child_path(&self.path(parent)?, name)?;
        if self.vfs.delete_link(&self.node(&path).await?) {
            Ok(())
        } else {
            Err(libc::EROFS.into())
        }
    }

    fn attr(&self, ino: Inode, node: &Node) -> FileAttr {
        let (kind, perm, nlink) = match node.kind() {
            // Owner- and group-writable so the kernel lets apps delete links in it.
            Kind::Dir if matches!(node, Node::Links(Some(_))) => (FileType::Directory, 0o775, 2),
            Kind::Dir => (FileType::Directory, 0o555, 2),
            // Writable mode so .NET apps (Arr) never see ReadOnly and chmod before a move.
            Kind::File => (FileType::RegularFile, 0o644, 1),
            Kind::Symlink => (FileType::Symlink, 0o777, 1),
        };
        let size = node.size();
        let time = (UNIX_EPOCH
            + Duration::from_millis(u64::try_from(modified(node, self.epoch_times)).unwrap_or(0)))
        .into();
        FileAttr {
            ino,
            size,
            blocks: size.div_ceil(512),
            atime: time,
            mtime: time,
            ctime: time,
            kind,
            perm,
            nlink,
            uid: self.uid,
            gid: self.gid,
            rdev: 0,
            blksize: BLOCK_SIZE,
        }
    }
}

#[allow(clippy::unused_async_trait_impl)] // The trait is async; sync bodies read clearer than `ready()`.
impl Filesystem for Fs {
    async fn init(&self, _req: Request) -> Result<ReplyInit> {
        // Writes always fail, so the write size is moot; any valid value will do.
        Ok(ReplyInit {
            max_write: NonZeroU32::MIN.saturating_add(128 * 1024 - 1),
        })
    }

    async fn destroy(&self, _req: Request) {
        let open_handles = lock(&self.handles).len();
        info!(open_handles, "FUSE session closed");
    }

    async fn lookup(&self, req: Request, parent: Inode, name: &OsStr) -> Result<ReplyEntry> {
        let started = Instant::now();
        let result = guarded("lookup", async {
            let path = child_path(&self.path(parent)?, name)?;
            let node = self.node(&path).await?;
            let ino = lock(&self.inodes).remember(&path);
            Ok(ReplyEntry {
                // fuse3 uses this for both the name and its attributes.
                ttl: ttls(&node).0,
                attr: self.attr(ino, &node),
                generation: 0,
            })
        })
        .await;
        self.traced(&req, "lookup", parent, Some(name), started, result)
    }

    async fn forget(&self, _req: Request, inode: Inode, nlookup: u64) {
        lock(&self.inodes).forget(inode, nlookup);
    }

    async fn batch_forget(&self, _req: Request, inodes: &[Inode]) {
        // ponytail: the batch arrives without lookup counts. The kernel forgets an inode
        // only when evicting it, so dropping it whole is right unless a lookup races the
        // eviction; that inode then answers ENOENT and the kernel looks the name up again.
        let mut table = lock(&self.inodes);
        for &inode in inodes {
            table.forget(inode, u64::MAX);
        }
    }

    async fn getattr(
        &self,
        req: Request,
        inode: Inode,
        fh: Option<u64>,
        _flags: u32,
    ) -> Result<ReplyAttr> {
        let started = Instant::now();
        let result = guarded("getattr", async {
            // A buffered read refreshes expired attributes through here, naming its handle.
            // Answer from the handle so reads never wait on, or fail with, the database.
            if let Some(fh) = fh
                && let Handle::File(_, attr) = self.handle(fh)?
            {
                return Ok(ReplyAttr {
                    ttl: ATTR_TTL,
                    attr,
                });
            }
            let node = self.node(&self.path(inode)?).await?;
            Ok(ReplyAttr {
                ttl: ttls(&node).1,
                attr: self.attr(inode, &node),
            })
        })
        .await;
        self.traced(&req, "getattr", inode, None, started, result)
    }

    async fn readlink(&self, req: Request, inode: Inode) -> Result<ReplyData> {
        let started = Instant::now();
        let result = guarded("readlink", async {
            match self.node(&self.path(inode)?).await? {
                Node::Link { target, .. } => Ok(ReplyData {
                    data: target.into(),
                }),
                _ => Err(libc::EINVAL.into()),
            }
        })
        .await;
        self.traced(&req, "readlink", inode, None, started, result)
    }

    async fn unlink(&self, req: Request, parent: Inode, name: &OsStr) -> Result<()> {
        let started = Instant::now();
        let result = guarded("unlink", self.delete(parent, name)).await;
        self.traced(&req, "unlink", parent, Some(name), started, result)
    }

    async fn rmdir(&self, req: Request, parent: Inode, name: &OsStr) -> Result<()> {
        let started = Instant::now();
        let result = guarded("rmdir", self.delete(parent, name)).await;
        self.traced(&req, "rmdir", parent, Some(name), started, result)
    }

    /// Mode, owner and time changes are accepted and ignored, as apps treat them as
    /// best effort after an import; resizing would change content.
    async fn setattr(
        &self,
        _req: Request,
        inode: Inode,
        _fh: Option<u64>,
        set_attr: SetAttr,
    ) -> Result<ReplyAttr> {
        if set_attr.size.is_some() {
            return Err(libc::EROFS.into());
        }
        guarded("setattr", async {
            let node = self.node(&self.path(inode)?).await?;
            Ok(ReplyAttr {
                ttl: ttls(&node).1,
                attr: self.attr(inode, &node),
            })
        })
        .await
    }

    async fn symlink(
        &self,
        _req: Request,
        _parent: Inode,
        _name: &OsStr,
        _link: &OsStr,
    ) -> Result<ReplyEntry> {
        Err(libc::EROFS.into())
    }

    async fn mknod(
        &self,
        _req: Request,
        _parent: Inode,
        _name: &OsStr,
        _mode: u32,
        _rdev: u32,
    ) -> Result<ReplyEntry> {
        Err(libc::EROFS.into())
    }

    async fn mkdir(
        &self,
        _req: Request,
        _parent: Inode,
        _name: &OsStr,
        _mode: u32,
        _umask: u32,
    ) -> Result<ReplyEntry> {
        Err(libc::EROFS.into())
    }

    async fn rename(
        &self,
        _req: Request,
        _parent: Inode,
        _name: &OsStr,
        _new_parent: Inode,
        _new_name: &OsStr,
    ) -> Result<()> {
        Err(libc::EROFS.into())
    }

    async fn link(
        &self,
        _req: Request,
        _inode: Inode,
        _new_parent: Inode,
        _new_name: &OsStr,
    ) -> Result<ReplyEntry> {
        Err(libc::EROFS.into())
    }

    async fn open(&self, req: Request, inode: Inode, flags: u32) -> Result<ReplyOpen> {
        let started = Instant::now();
        let open = async {
            if flags & libc::O_ACCMODE.cast_unsigned() != libc::O_RDONLY.cast_unsigned() {
                return Err(libc::EROFS.into());
            }
            let path = self.path(inode)?;
            let node = self.node(&path).await?;
            if node.kind() == Kind::Dir {
                return Err(Errno::new_is_dir());
            }
            let attr = self.attr(inode, &node);
            let origin = Origin {
                delivery: Delivery::Fuse,
                client: self.caller(&req).await,
                player: None,
            };
            let file = match self.vfs.open(&node, origin).await {
                Ok(Some(reader)) => OpenFile {
                    path,
                    session: reader.session().cloned(),
                    reader: tokio::sync::Mutex::new(Some(reader)),
                    pos: AtomicU64::new(0),
                    waiting: Mutex::new(Vec::new()),
                    order_span: self.order_span,
                    moved: Notify::new(),
                },
                Ok(None) => return Err(Errno::new_not_exist()),
                Err(error) => {
                    warn!(path = &*path, reason = %error, "FUSE open failed");
                    return Err(libc::EIO.into());
                }
            };
            Ok(ReplyOpen {
                fh: self.insert(Handle::File(Arc::new(file), attr)),
                flags: FOPEN_KEEP_CACHE,
            })
        };
        let result = guarded("open", self.interruptible(&req, open)).await;
        self.traced(&req, "open", inode, None, started, result)
    }

    async fn read(
        &self,
        req: Request,
        _inode: Inode,
        fh: u64,
        offset: u64,
        size: u32,
    ) -> Result<ReplyData> {
        let started = Instant::now();
        let Handle::File(file, _) = self.handle(fh)? else {
            return Err(Errno::new_is_dir());
        };
        let read = async {
            file.read(req.unique, offset, size).await.map_err(|error| {
                warn!(path = &*file.path, offset, reason = %error, "FUSE read failed");
                Errno::from(libc::EIO)
            })
        };
        let result = guarded("read", self.interruptible(&req, read)).await;
        // Reads waiting their turn recheck however this one ended.
        file.moved.notify_waiters();
        let buf = result?;
        trace!(target: OPS, op = "read", path = &*file.path, offset, bytes = buf.len(), us = started.elapsed().as_micros());
        Ok(ReplyData { data: buf.freeze() })
    }

    /// Ends a read or open of a dying caller with `EINTR`. Anything else, including a
    /// request not seen yet, goes back to the kernel with `EAGAIN`, which resends it: a
    /// later fatal signal still gets through.
    async fn interrupt(&self, _req: Request, unique: u64) -> Result<()> {
        for recheck in [false, true] {
            if recheck {
                tokio::time::sleep(INTERRUPT_RECHECK).await;
            }
            let Some((token, tid)) = lock(&self.live).get(&unique).cloned() else {
                continue;
            };
            // tid 0: a caller in another PID namespace, which `/proc` cannot show.
            if tid != 0 && read_proc(&format!("/proc/{tid}/status")).is_some_and(|s| dying(&s)) {
                debug!(target: OPS, op = "interrupt", unique, tid, "cancelled the request of a dying caller");
                token.cancel();
                return Ok(());
            }
        }
        Err(libc::EAGAIN.into())
    }

    async fn release(
        &self,
        req: Request,
        inode: Inode,
        fh: u64,
        _flags: u32,
        _lock_owner: u64,
        _flush: bool,
    ) -> Result<()> {
        let started = Instant::now();
        lock(&self.handles).remove(&fh);
        self.traced(&req, "release", inode, None, started, Ok(()))
    }

    async fn statfs(&self, _req: Request, _inode: Inode) -> Result<ReplyStatFs> {
        Ok(ReplyStatFs {
            blocks: 0,
            bfree: 0,
            bavail: 0,
            files: 0,
            ffree: 0,
            bsize: BLOCK_SIZE,
            namelen: 255,
            frsize: BLOCK_SIZE,
        })
    }

    async fn opendir(&self, req: Request, inode: Inode, _flags: u32) -> Result<ReplyOpen> {
        let started = Instant::now();
        let result = guarded("opendir", async {
            let path = self.path(inode)?;
            let node = self.node(&path).await?;
            if node.kind() != Kind::Dir {
                return Err(Errno::new_is_not_dir());
            }
            let entries = self.vfs.list(&node).await.map_err(|error| {
                warn!(path = &*path, reason = %error, "FUSE listing failed");
                Errno::from(libc::EIO)
            })?;
            Ok(ReplyOpen {
                fh: self.insert(Handle::Dir(entries.into())),
                flags: 0,
            })
        })
        .await;
        self.traced(&req, "opendir", inode, None, started, result)
    }

    async fn releasedir(&self, _req: Request, _inode: Inode, fh: u64, _flags: u32) -> Result<()> {
        lock(&self.handles).remove(&fh);
        Ok(())
    }

    async fn readdirplus(
        &self,
        _req: Request,
        parent: Inode,
        fh: u64,
        offset: u64,
        _lock_owner: u64,
    ) -> Result<ReplyDirectoryPlus<impl Stream<Item = Result<DirectoryEntryPlus>> + Send + '_>>
    {
        let Handle::Dir(entries) = self.handle(fh)? else {
            return Err(Errno::new_is_not_dir());
        };
        let parent = self.path(parent)?;
        let start = usize::try_from(offset).unwrap_or(usize::MAX);
        debug!(target: OPS, op = "readdirplus", path = &*parent, offset, entries = entries.len().saturating_sub(start));
        // The stream is pulled lazily, so only entries the reply reaches count a lookup.
        // ponytail: the one entry pulled but cut by a full reply buffer is over-counted;
        // that only keeps its inode alive, never drops one the kernel still holds.
        let entries = (start..entries.len()).map(move |i| {
            let node = &entries[i];
            let name = OsString::from(node.name());
            let ino = lock(&self.inodes).remember(&child_path(&parent, &name)?);
            let attr = self.attr(ino, node);
            let (entry_ttl, attr_ttl) = ttls(node);
            Ok(DirectoryEntryPlus {
                inode: ino,
                generation: 0,
                kind: attr.kind,
                attr,
                name,
                offset: i64::try_from(i + 1).unwrap_or(i64::MAX),
                entry_ttl,
                attr_ttl,
            })
        });
        Ok(ReplyDirectoryPlus {
            // The session pulls entries after this returns, outside `guarded`; a panic
            // ends the listing with one EIO.
            // ponytail: lookups counted before the panic stay counted; that only keeps
            // those inodes alive.
            entries: AssertUnwindSafe(stream::iter(entries))
                .catch_unwind()
                .map(|entry| {
                    entry.unwrap_or_else(|_| {
                        error!(op = "readdirplus", "FUSE request panicked; replying EIO");
                        Err(libc::EIO.into())
                    })
                }),
        })
    }
}

/// Entry and attribute TTLs; a fixed directory's children still use the short ones.
const fn ttls(node: &Node) -> (Duration, Duration) {
    match node {
        Node::Root | Node::Readme | Node::Shard(_) | Node::Nzbs | Node::Links(None) => {
            (FIXED_TTL, FIXED_TTL)
        }
        _ => (ENTRY_TTL, ATTR_TTL),
    }
}

// ponytail: no kernel notify invalidation; the 1 s entry TTL bounds how long a new or
// removed name stays stale. Send `Notify` entry invalidations from the import path if
// that ever proves too slow.

/// Unix milliseconds for `node`'s times; `epoch` zeroes media files and leaves the rest.
fn modified(node: &Node, epoch: bool) -> i64 {
    let media = matches!(node, Node::Item(_) | Node::IdFile(_)) && node.kind() == Kind::File;
    if epoch && media { 0 } else { node.modified() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_path_computed_nodes_keep_long_ttls() {
        let shard = Node::Shard("ab".to_owned());
        assert_eq!(ttls(&shard), (FIXED_TTL, FIXED_TTL));
        let category = Node::NzbCategory("tv".to_owned());
        assert_eq!(ttls(&category), (ENTRY_TTL, ATTR_TTL));
    }

    #[test]
    fn epoch_times_zero_media_files_only() {
        use infinidysk_core::ItemKind;
        use infinidysk_db::{HealthState, Item};
        let item = |kind| Item {
            id: infinidysk_core::ItemId::new(),
            parent: None,
            name: "x".into(),
            path: "/content/x".into(),
            kind,
            size: Some(1),
            meta_blob: None,
            nzb_blob: None,
            created_at: 1_785_506_487_481,
            release_date: None,
            health_state: HealthState::Unchecked,
            encrypted: false,
        };
        let real = 1_785_506_487_000;
        for kind in [ItemKind::NzbFile, ItemKind::Multipart] {
            for node in [Node::Item(item(kind)), Node::IdFile(item(kind))] {
                assert_eq!(modified(&node, false), real);
                assert_eq!(modified(&node, true), 0);
            }
        }
        let dir = Node::Item(item(ItemKind::Directory));
        let link = Node::Link {
            item: item(ItemKind::NzbFile),
            target: "/x".into(),
        };
        for node in [dir, link, Node::Readme] {
            assert_eq!(modified(&node, true), node.modified());
        }
    }

    #[test]
    fn mount_visibility_follows_the_deepest_holding_mount() {
        let info = "22 1 0:21 / / rw shared:1 - overlay overlay rw\n\
                    30 22 8:1 /mnt /mnt rw - ext4 /dev/sda1 rw\n\
                    31 22 8:1 /data /my\\040media rw shared:7 - ext4 /dev/sda1 rw\n";
        assert_eq!(shared(info, Path::new("/mnt/remote/x")), Some(false));
        assert_eq!(shared(info, Path::new("/my media/x")), Some(true));
        assert_eq!(shared(info, Path::new("/mntx")), Some(true));
    }

    #[test]
    fn callers_are_named_by_process_or_by_ids() {
        let status =
            "Name:\tPlex Transcoder\nUmask:\t0022\nState:\tS (sleeping)\nTgid:\t412\nPid:\t419\n";
        assert_eq!(parse_status(status), Some(("Plex Transcoder".into(), 412)));
        assert_eq!(parse_status("Name:\tx\n"), None);
        assert_eq!(
            caller_label(Some(named("player\u{1b}[31m", 412, None)), 419, 1000, 100),
            "player[31m (pid 412, tid 419) \u{b7} uid 1000 gid 100"
        );
        assert_eq!(
            caller_label(Some(named("player", 412, None)), 412, 0, 0),
            "player (pid 412) \u{b7} uid 0 gid 0"
        );
        assert_eq!(caller_label(None, 77, 1, 2), "pid 77 \u{b7} uid 1 gid 2");
        assert_eq!(
            caller_label(None, 0, 1, 2),
            "pid unavailable \u{b7} uid 1 gid 2"
        );
        let me = std::process::id();
        let found = process(me).expect("this test's own process is visible");
        assert_eq!((found.pid, found.parent), (me, None));
    }

    fn named(name: &str, pid: u32, parent: Option<&str>) -> Process {
        Process {
            name: name.to_owned(),
            pid,
            parent: parent.map(str::to_owned),
        }
    }

    #[test]
    fn a_helper_is_labelled_with_the_app_that_started_it() {
        assert_eq!(
            caller_label(Some(named("ffmpeg", 50, Some("silo"))), 50, 1000, 1000),
            "silo \u{203a} ffmpeg (pid 50) \u{b7} uid 1000 gid 1000"
        );
        assert_eq!(
            caller_label(Some(named("ffmpeg", 50, Some("silo"))), 51, 0, 0),
            "silo \u{203a} ffmpeg (pid 50, tid 51) \u{b7} uid 0 gid 0"
        );
        assert_eq!(
            caller_label(
                Some(named("ff\u{203a}\u{7}mpeg", 50, Some("a\u{203a}b"))),
                50,
                0,
                0
            ),
            "ab \u{203a} ffmpeg (pid 50) \u{b7} uid 0 gid 0",
            "each part loses control characters and the separator"
        );
        let long = "x".repeat(200);
        assert_eq!(
            caller_label(Some(named("ffmpeg", 50, Some(&long))), 50, 0, 0),
            format!(
                "{} \u{203a} ffmpeg (pid 50) \u{b7} uid 0 gid 0",
                "x".repeat(MAX_NAME)
            ),
            "a long app name cannot push out the helper"
        );
        assert_eq!(
            caller_label(Some(named("ffmpeg", 50, Some("\u{203a}\u{7}"))), 50, 0, 0),
            "ffmpeg (pid 50) \u{b7} uid 0 gid 0",
            "an app name that cleans to nothing is left out"
        );
    }

    #[test]
    fn the_parent_app_is_the_first_one_past_shells_and_helpers() {
        let table: HashMap<u32, (&str, Option<u32>)> = HashMap::from([
            (10, ("silo", Some(1))),
            (11, ("sh", Some(10))),
            (12, ("bash", Some(11))),
            (13, ("dash", Some(12))),
            (14, ("sh", Some(13))),
            (15, ("ffprobe", Some(10))),
            (16, ("ash", Some(10))),
            (20, ("tini", Some(1))),
            (21, ("containerd-shim", Some(1))),
            (22, ("supervisord", Some(1))),
            (30, ("sh", Some(31))),
            (31, ("sh", Some(30))),
            (40, ("sh", Some(41))),
            (50, ("sh", Some(99))),
            (60, ("", Some(10))),
        ]);
        let walk = |ppid| {
            parent_app(100, ppid, |p| {
                table.get(&p).map(|&(n, pp)| (n.to_owned(), pp))
            })
        };
        assert_eq!(walk(Some(10)).as_deref(), Some("silo"));
        assert_eq!(walk(Some(11)).as_deref(), Some("silo"), "through a shell");
        assert_eq!(walk(Some(15)).as_deref(), Some("silo"), "through a helper");
        assert_eq!(walk(Some(16)).as_deref(), Some("silo"), "through ash");
        assert_eq!(
            walk(Some(13)).as_deref(),
            Some("silo"),
            "three shells, then the app"
        );
        assert_eq!(walk(Some(14)), None, "four shells use up the hops");
        assert_eq!(walk(Some(20)), None, "a launcher");
        assert_eq!(walk(Some(21)), None, "a launcher with a cut name");
        assert_eq!(walk(Some(22)), None, "a process supervisor");
        assert_eq!(walk(Some(1)), None, "pid 1");
        assert_eq!(walk(None), None, "no PPid");
        assert_eq!(walk(Some(30)), None, "a loop between parents");
        assert_eq!(walk(Some(100)), None, "a loop back to the reader");
        assert_eq!(walk(Some(40)), None, "a parent that went away");
        assert_eq!(walk(Some(50)), None, "a hidden grandparent");
        assert_eq!(walk(Some(60)), None, "an empty name");
    }

    #[test]
    fn a_live_helper_is_named_with_the_process_that_started_it() {
        let me = std::process::id();
        let dir = std::env::temp_dir().join(format!("infinidysk-fuse-helper-{me}"));
        std::fs::create_dir_all(&dir).unwrap();
        // The kernel names a process after the path it was started from.
        let helper = dir.join("ffmpeg");
        let _ = std::fs::remove_file(&helper);
        std::os::unix::fs::symlink("/bin/sleep", &helper).unwrap();
        let mut child = std::process::Command::new(&helper)
            .arg("30")
            .spawn()
            .unwrap();
        let pid = child.id();
        let deadline = Instant::now() + Duration::from_secs(5);
        let found = loop {
            match process(pid) {
                Some(p) if p.name == "ffmpeg" => break p,
                _ if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(10));
                }
                _ => panic!("the helper never showed up under its own name"),
            }
        };
        let _ = child.kill();
        let _ = child.wait();
        let _ = std::fs::remove_dir_all(&dir);
        let mine = parse_status(&read_proc(&format!("/proc/{me}/status")).unwrap()).unwrap();
        assert_eq!(found.parent, Some(mine.0));
    }

    #[test]
    fn ppid_is_read_apart_from_the_name() {
        let status = "Name:\tffmpeg\nTgid:\t50\nPid:\t50\nPPid:\t10\n";
        assert_eq!(parse_ppid(status), Some(10));
        assert_eq!(parse_ppid("Name:\tx\nTgid:\t5\n"), None);
        assert_eq!(parse_ppid("Name:\tx\nTgid:\t5\nPPid:\tno\n"), None);
        assert_eq!(
            parse_status("Name:\tx\nTgid:\t5\nPPid:\tno\n"),
            Some(("x".into(), 5)),
            "a bad PPid keeps the name"
        );
    }

    #[test]
    fn readahead_rounds_up_to_whole_pages() {
        // 5000 KB on 4 KiB pages: the kernel would round 4882.8 KiB down to 4880.
        assert_eq!(read_ahead_kb(5_000_000, 4096), 4884);
        assert_eq!(read_ahead_kb(8192, 4096), 8);
        assert_eq!(read_ahead_kb(5_000_000, 65_536), 4928);
        for bytes in [128_000, 1_250_000, 65_536_000] {
            for page in [4096, 16_384, 65_536] {
                let effective = read_ahead_kb(bytes, page) * 1024;
                assert!(effective >= u64::from(bytes) && effective < u64::from(bytes) + page);
            }
        }
        assert!(page_size().is_some());
    }

    #[test]
    fn only_reads_just_ahead_wait_their_turn() {
        let span = 11 << 20;
        assert!(should_wait(0, 1 << 20, span));
        assert!(should_wait(0, span - 1, span));
        assert!(!should_wait(0, span, span));
        assert!(!should_wait(0, 0, span), "sequential");
        assert!(!should_wait(1 << 20, 0, span), "backward");
        assert!(!should_wait(UNKNOWN, 1 << 20, span));
    }

    #[tokio::test]
    async fn a_read_waiting_its_turn_goes_once_the_reader_frees_without_a_read_ending() {
        let file = Arc::new(OpenFile {
            path: "f".into(),
            reader: tokio::sync::Mutex::new(Some(std::io::Cursor::new(vec![7_u8; 8192]))),
            pos: AtomicU64::new(0),
            waiting: Mutex::new(Vec::new()),
            order_span: 1 << 20,
            moved: Notify::new(),
            session: None,
        });
        // Another waiter's busy probe, which no `moved` notify follows.
        let probe = file.reader.lock().await;
        let read = tokio::spawn({
            let file = Arc::clone(&file);
            async move { file.read(1, 4096, 4096).await }
        });
        tokio::time::sleep(ORDER_WAIT * 3).await;
        drop(probe);
        let read = tokio::time::timeout(Duration::from_secs(1), read).await;
        let bytes = read
            .expect("the read went")
            .expect("no panic")
            .expect("read");
        assert_eq!(bytes.len(), 4096);
    }

    #[test]
    fn only_a_pending_kill_means_dying() {
        let status = |sig: &str, shd: &str| format!("Name:\tx\nSigPnd:\t{sig}\nShdPnd:\t{shd}\n");
        assert!(!dying(&status("0000000000000000", "0000000000000000")));
        // SIGURG (23) alone, as runtimes send for preemption.
        assert!(!dying(&status("0000000000400000", "0000000000000000")));
        assert!(dying(&status("0000000000000100", "0000000000000000")));
        assert!(dying(&status("0000000000000000", "0000000000000100")));
    }

    #[tokio::test]
    async fn a_panicking_request_replies_eio() {
        let result: Result<()> = guarded("test", async {
            assert!(!std::hint::black_box(true), "handler bug");
            Ok(())
        })
        .await;
        assert_eq!(result.err().map(libc::c_int::from), Some(-libc::EIO));
    }
}
