//! Open reads of item files, for the dashboard, and their optional traces.

use std::collections::{HashMap, VecDeque};
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::task::{Context, Poll};
use std::time::{Duration, Instant, SystemTime};

use infinidysk_core::ItemId;
pub use infinidysk_core::trace::ReadStage;
use infinidysk_core::trace::{Event, Reply, Trace};
use infinidysk_core::wait::{Watch, age, now_ns};
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};

use crate::Reader;

/// Finished traced reads kept for inspection.
const KEPT_TRACES: usize = 20;
/// Finished reads kept for [`Sessions::take_ended`] before the oldest are dropped.
const KEPT_ENDED: usize = 10_000;
/// Shorter waits for data are covered by a player's buffer, not stalls.
const MIN_STALL: Duration = Duration::from_secs(1);
/// Shorter pauses between a reader's requests are not traced as idle.
const MIN_IDLE: Duration = Duration::from_millis(50);
/// Faster FUSE metadata calls are only counted, not listed, while tracing.
const MIN_OP: Duration = Duration::from_millis(1);
/// Slow FUSE metadata calls listed while tracing; the oldest go first.
const KEPT_OPS: usize = 1000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Delivery {
    Fuse,
    WebDav,
}

impl Delivery {
    pub const ALL: [Self; 2] = [Self::Fuse, Self::WebDav];

    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Fuse => "fuse",
            Self::WebDav => "webdav",
        }
    }
}

/// Who opened a file.
#[derive(Clone, Debug)]
pub struct Origin {
    pub delivery: Delivery,
    /// Process, pid and uid/gid (FUSE), or user agent and address (`WebDAV`). Best effort.
    pub client: String,
    /// Player id from a `/view` link, which lets the player find its own reads.
    pub player: Option<String>,
}

/// While set and unexpired, every new item read is traced with `capacity` events.
#[derive(Clone, Copy, Debug)]
pub struct Tracing {
    pub until: Instant,
    pub capacity: usize,
}

/// One open of one item file.
pub struct Session {
    pub id: u64,
    pub item: ItemId,
    pub path: String,
    pub delivery: Delivery,
    pub client: String,
    pub player: Option<String>,
    pub opened_at: SystemTime,
    pub size: u64,
    pub trace: Option<Arc<Trace>>,
    served: AtomicU64,
    pos: AtomicU64,
    /// Nanoseconds the first read that returned bytes waited in the stream: from its
    /// first pending poll to its bytes. `u64::MAX` before it.
    first_byte_ns: AtomicU64,
    stall_ns: AtomicU64,
    stalls: AtomicU64,
    seeks: AtomicU64,
    closed_at: OnceLock<SystemTime>,
    watch: Arc<Watch>,
    /// [`now_ns`] stamps: since when a read waits for data, and when one last returned.
    waiting_since: AtomicU64,
    last_read: AtomicU64,
    /// FUSE reads not answered yet.
    requests: Mutex<Vec<Listed>>,
    /// The FUSE file handle, once the open replied.
    handle: OnceLock<u64>,
}

struct Listed {
    id: u64,
    offset: u64,
    size: u32,
    arrived: Instant,
    stage: ReadStage,
    /// When `stage` began.
    since: Instant,
}

/// A read not answered yet.
#[derive(Clone, Copy, Debug)]
pub struct PendingRead {
    /// The FUSE request id (`unique`).
    pub request: u64,
    pub offset: u64,
    pub size: u32,
    /// Since the request reached the daemon.
    pub age: Duration,
    pub stage: ReadStage,
    /// Since it entered `stage`.
    pub stage_age: Duration,
}

impl Session {
    /// Bytes returned to the reader.
    pub fn served(&self) -> u64 {
        self.served.load(Ordering::Relaxed)
    }

    /// Read position.
    pub fn pos(&self) -> u64 {
        self.pos.load(Ordering::Relaxed)
    }

    pub fn closed_at(&self) -> Option<SystemTime> {
        self.closed_at.get().copied()
    }

    /// Stream wait of the first read that returned bytes: from its first pending poll to
    /// its bytes, zero when they were ready. Excludes time before that read reached the
    /// stream (FUSE ordering and handle-lock waits, the kernel, the player): not
    /// click-to-play time.
    pub fn first_byte(&self) -> Option<Duration> {
        match self.first_byte_ns.load(Ordering::Relaxed) {
            u64::MAX => None,
            ns => Some(Duration::from_nanos(ns)),
        }
    }

    /// Waits for data of at least 1 s, other than for the first byte and the first byte
    /// after a seek: how many, and their total.
    pub fn stalls(&self) -> (u64, Duration) {
        (
            self.stalls.load(Ordering::Relaxed),
            Duration::from_nanos(self.stall_ns.load(Ordering::Relaxed)),
        )
    }

    pub fn seeks(&self) -> u64 {
        self.seeks.load(Ordering::Relaxed)
    }

    /// The segment the reader is blocked on and the read-ahead budget it holds.
    pub fn watch(&self) -> &Watch {
        &self.watch
    }

    /// How long the current read has waited for data.
    pub fn waiting(&self) -> Option<Duration> {
        age(self.waiting_since.load(Ordering::Relaxed))
    }

    /// Time since a read last returned data.
    pub fn since_read(&self) -> Option<Duration> {
        age(self.last_read.load(Ordering::Relaxed))
    }

    /// FUSE reads not answered yet, oldest first.
    pub fn requests(&self) -> Vec<PendingRead> {
        let now = Instant::now();
        let requests = self.lock_requests();
        let mut pending: Vec<_> = requests
            .iter()
            .map(|r| PendingRead {
                request: r.id,
                offset: r.offset,
                size: r.size,
                age: now.saturating_duration_since(r.arrived),
                stage: r.stage,
                stage_age: now.saturating_duration_since(r.since),
            })
            .collect();
        drop(requests);
        pending.sort_by_key(|p| std::cmp::Reverse(p.age));
        pending
    }

    /// The FUSE file handle, once the open replied.
    pub fn handle(&self) -> Option<u64> {
        self.handle.get().copied()
    }

    /// FUSE open `request`, which reached the daemon at `arrived`, replied with `handle`.
    pub fn opened(&self, request: u64, handle: u64, inode: u64, arrived: Instant) {
        let _ = self.handle.set(handle);
        if let Some(trace) = &self.trace {
            let before = trace.started().saturating_duration_since(arrived);
            trace.record(Event::Opened {
                request,
                handle,
                inode,
                before_ns: u64::try_from(before.as_nanos()).unwrap_or(u64::MAX),
            });
        }
    }

    /// Lists FUSE read `id`, which reached the daemon at `arrived`, until the returned
    /// guard ends or drops.
    pub fn request(self: &Arc<Self>, id: u64, offset: u64, size: u32, arrived: Instant) -> Request {
        if let Some(trace) = &self.trace {
            trace.record_at(
                arrived,
                Event::Request {
                    request: id,
                    offset,
                    size,
                },
            );
        }
        self.lock_requests().push(Listed {
            id,
            offset,
            size,
            arrived,
            stage: ReadStage::Order,
            since: arrived,
        });
        Request {
            session: Arc::clone(self),
            id,
            ended: false,
        }
    }

    fn lock_requests(&self) -> MutexGuard<'_, Vec<Listed>> {
        self.requests.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// A FUSE read listed on its session until it ends or drops. Dropped without
/// [`end`](Self::end), it was cancelled (or failed, when dropped by a panic).
pub struct Request {
    session: Arc<Session>,
    id: u64,
    ended: bool,
}

impl Request {
    pub fn stage(&self, stage: ReadStage) {
        if let Some(request) = self
            .session
            .lock_requests()
            .iter_mut()
            .find(|r| r.id == self.id)
        {
            request.stage = stage;
            request.since = Instant::now();
        }
        if let Some(trace) = &self.session.trace {
            trace.record(Event::RequestStage {
                request: self.id,
                stage,
            });
        }
    }

    /// The daemon answers with `bytes`, or an error.
    pub fn end(mut self, bytes: Option<usize>) {
        self.ended = true;
        let result = if bytes.is_some() {
            Reply::Data
        } else {
            Reply::Error
        };
        self.close(result, bytes.unwrap_or(0));
    }

    fn close(&self, result: Reply, bytes: usize) {
        if let Some(trace) = &self.session.trace {
            trace.record(Event::Replied {
                request: self.id,
                bytes: u32::try_from(bytes).unwrap_or(u32::MAX),
                result,
            });
        }
        self.session.lock_requests().retain(|r| r.id != self.id);
    }
}

impl Drop for Request {
    fn drop(&mut self) {
        if !self.ended {
            let result = if std::thread::panicking() {
                Reply::Error
            } else {
                Reply::Cancelled
            };
            self.close(result, 0);
        }
    }
}

/// A FUSE metadata call listed while tracing.
#[derive(Clone, Copy, Debug)]
pub struct FuseOp {
    pub op: &'static str,
    /// The FUSE request id (`unique`).
    pub request: u64,
    /// For lookup, the parent directory.
    pub inode: u64,
    /// For lookup, the inode found: the one a later open names.
    pub child: Option<u64>,
    /// Wall clock when it reached the daemon.
    pub arrived: SystemTime,
    pub took: Duration,
    /// `0` for success.
    pub errno: i32,
}

/// A FUSE metadata call in the daemon without a reply yet, while tracing.
#[derive(Clone, Copy, Debug)]
pub struct PendingOp {
    pub op: &'static str,
    pub request: u64,
    pub inode: u64,
    /// Wall clock when it reached the daemon.
    pub arrived: SystemTime,
    pub age: Duration,
}

/// Lists a metadata call as pending until dropped; see [`Sessions::begin_op`].
#[must_use]
pub struct OpInFlight<'a> {
    sessions: &'a Sessions,
    request: u64,
}

impl Drop for OpInFlight<'_> {
    fn drop(&mut self) {
        self.sessions.lock().pending.remove(&self.request);
    }
}

/// Slow FUSE metadata calls while tracing, with counts of what was not listed.
#[derive(Clone, Debug, Default)]
pub struct FuseOps {
    /// Oldest first.
    pub listed: VecDeque<FuseOp>,
    /// Calls seen while tracing, listed or not.
    pub seen: u64,
    /// Calls faster than the listing threshold.
    pub fast: u64,
    /// Listed calls pushed out by newer ones.
    pub dropped: u64,
}

impl FuseOps {
    /// Calls from this long are listed.
    pub const MIN: Duration = MIN_OP;
    /// Listed calls kept at most.
    pub const KEPT: usize = KEPT_OPS;
}

#[derive(Default)]
pub struct Sessions {
    next: AtomicU64,
    /// Bytes served since start, indexed like [`Delivery::ALL`].
    served: [AtomicU64; 2],
    /// A tracing window was set, so metadata calls take the lock to be noted.
    traced: AtomicBool,
    inner: Mutex<Inner>,
}

#[derive(Default)]
struct Inner {
    open: Vec<Arc<Session>>,
    /// Newest last.
    finished: VecDeque<Arc<Session>>,
    /// Every finished read since the last [`Sessions::take_ended`].
    ended: VecDeque<Arc<Session>>,
    tracing: Option<Tracing>,
    ops: FuseOps,
    /// Metadata calls without a reply, by request id: op, inode, arrival.
    pending: HashMap<u64, (&'static str, u64, Instant)>,
}

impl Sessions {
    /// Bytes served since start by `delivery`.
    pub fn served(&self, delivery: Delivery) -> u64 {
        self.served[delivery as usize].load(Ordering::Relaxed)
    }

    /// Reads finished since the last call, oldest first.
    pub fn take_ended(&self) -> Vec<Arc<Session>> {
        self.lock().ended.drain(..).collect()
    }

    pub fn set_tracing(&self, tracing: Option<Tracing>) {
        self.traced.store(tracing.is_some(), Ordering::Relaxed);
        self.lock().tracing = tracing;
    }

    /// The tracing window, if still running.
    pub fn tracing(&self) -> Option<Tracing> {
        self.lock().tracing.filter(|t| t.until > Instant::now())
    }

    /// Lists FUSE metadata call `op` as pending while tracing, until the guard drops, so
    /// a pack exported during a hang shows calls that never finished.
    pub fn begin_op(
        &self,
        op: &'static str,
        request: u64,
        inode: u64,
        arrived: Instant,
    ) -> Option<OpInFlight<'_>> {
        if !self.traced.load(Ordering::Relaxed) {
            return None;
        }
        let mut inner = self.lock();
        if inner.tracing.is_none_or(|t| t.until <= Instant::now()) {
            return None;
        }
        // The kernel bounds the requests in flight, so this map stays small.
        inner.pending.insert(request, (op, inode, arrived));
        drop(inner);
        Some(OpInFlight {
            sessions: self,
            request,
        })
    }

    /// Metadata calls without a reply, oldest first.
    pub fn pending_ops(&self) -> Vec<PendingOp> {
        let now = Instant::now();
        let wall = SystemTime::now();
        let mut pending: Vec<_> = self
            .lock()
            .pending
            .iter()
            .map(|(&request, &(op, inode, arrived))| {
                let age = now.saturating_duration_since(arrived);
                PendingOp {
                    op,
                    request,
                    inode,
                    arrived: wall.checked_sub(age).unwrap_or(SystemTime::UNIX_EPOCH),
                    age,
                }
            })
            .collect();
        pending.sort_by_key(|p| std::cmp::Reverse(p.age));
        pending
    }

    /// Notes FUSE metadata call `op` that reached the daemon at `arrived`, while tracing.
    pub fn fuse_op(
        &self,
        op: &'static str,
        request: u64,
        inode: u64,
        child: Option<u64>,
        arrived: Instant,
        errno: i32,
    ) {
        if !self.traced.load(Ordering::Relaxed) {
            return;
        }
        let now = Instant::now();
        let took = now.saturating_duration_since(arrived);
        let op = (took >= MIN_OP).then(|| FuseOp {
            op,
            request,
            inode,
            child,
            arrived: SystemTime::now()
                .checked_sub(took)
                .unwrap_or(SystemTime::UNIX_EPOCH),
            took,
            errno,
        });
        let mut inner = self.lock();
        if inner.tracing.is_none_or(|t| t.until <= now) {
            return;
        }
        let ops = &mut inner.ops;
        ops.seen += 1;
        let Some(op) = op else {
            ops.fast += 1;
            return;
        };
        if ops.listed.len() == KEPT_OPS {
            ops.listed.pop_front();
            ops.dropped += 1;
        }
        ops.listed.push_back(op);
        drop(inner);
    }

    /// Slow FUSE metadata calls noted while tracing.
    pub fn fuse_ops(&self) -> FuseOps {
        self.lock().ops.clone()
    }

    pub fn open(&self) -> Vec<Arc<Session>> {
        self.lock().open.clone()
    }

    /// Traced reads, open and finished, newest first.
    pub fn traced(&self) -> Vec<Arc<Session>> {
        let inner = self.lock();
        let mut traced: Vec<_> = inner
            .finished
            .iter()
            .chain(inner.open.iter().filter(|s| s.trace.is_some()))
            .cloned()
            .collect();
        drop(inner);
        traced.sort_by_key(|s| std::cmp::Reverse(s.id));
        traced
    }

    pub fn get(&self, id: u64) -> Option<Arc<Session>> {
        let inner = self.lock();
        inner
            .open
            .iter()
            .chain(&inner.finished)
            .find(|s| s.id == id)
            .cloned()
    }

    pub(crate) fn start(
        self: &Arc<Self>,
        item: ItemId,
        path: String,
        size: u64,
        origin: Origin,
    ) -> Guard {
        let trace = self.tracing().map(|t| Arc::new(Trace::new(t.capacity)));
        if let Some(trace) = &trace {
            trace.record(Event::Open { size });
        }
        let session = Arc::new(Session {
            id: self.next.fetch_add(1, Ordering::Relaxed),
            item,
            path,
            delivery: origin.delivery,
            client: origin.client,
            player: origin.player,
            opened_at: SystemTime::now(),
            size,
            trace,
            served: AtomicU64::new(0),
            pos: AtomicU64::new(0),
            first_byte_ns: AtomicU64::new(u64::MAX),
            stall_ns: AtomicU64::new(0),
            stalls: AtomicU64::new(0),
            seeks: AtomicU64::new(0),
            closed_at: OnceLock::new(),
            watch: Arc::default(),
            waiting_since: AtomicU64::new(0),
            last_read: AtomicU64::new(0),
            requests: Mutex::default(),
            handle: OnceLock::new(),
        });
        self.lock().open.push(Arc::clone(&session));
        Guard {
            session,
            sessions: Arc::clone(self),
        }
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Keeps a session listed as open until dropped.
pub(crate) struct Guard {
    session: Arc<Session>,
    sessions: Arc<Sessions>,
}

impl Guard {
    pub(crate) fn trace(&self) -> Option<Arc<Trace>> {
        self.session.trace.clone()
    }

    pub(crate) fn watch(&self) -> Arc<Watch> {
        Arc::clone(&self.session.watch)
    }
}

impl Drop for Guard {
    fn drop(&mut self) {
        let session = &self.session;
        let _ = session.closed_at.set(SystemTime::now());
        let mut inner = self.sessions.lock();
        inner.open.retain(|s| !Arc::ptr_eq(s, session));
        if inner.ended.len() == KEPT_ENDED {
            inner.ended.pop_front();
        }
        inner.ended.push_back(Arc::clone(session));
        if let Some(trace) = &session.trace {
            trace.record(Event::End {
                bytes: session.served(),
            });
            if inner.finished.len() == KEPT_TRACES {
                inner.finished.pop_front();
            }
            inner.finished.push_back(Arc::clone(session));
        }
    }
}

/// An open file: its [`Reader`], and for item files the session counting its reads.
pub struct Handle {
    reader: Reader,
    session: Option<Guard>,
    /// Since when the reader has been waiting for data.
    waiting: Option<Instant>,
    /// When the last read returned, to trace pauses between reads.
    returned: Option<Instant>,
    /// A seek moved the position and no byte came since: the wait is seek latency.
    seeked: bool,
}

impl Handle {
    pub(crate) const fn new(reader: Reader, session: Option<Guard>) -> Self {
        Self {
            reader,
            session,
            waiting: None,
            returned: None,
            seeked: false,
        }
    }

    pub fn session(&self) -> Option<&Arc<Session>> {
        self.session.as_ref().map(|g| &g.session)
    }

    /// The caller reads no further than `end`, so read-ahead stops there.
    pub fn set_end(&mut self, end: u64) {
        self.reader.set_end(end);
    }
}

#[allow(clippy::significant_drop_tightening)] // False positive: `this` borrows, it drops nothing.
impl AsyncRead for Handle {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let this = self.get_mut();
        let before = buf.filled().len();
        let result = Pin::new(&mut this.reader).poll_read(cx, buf);
        let Some(guard) = &this.session else {
            return result;
        };
        let session = &guard.session;
        let now = Instant::now();
        if let (Some(trace), Some(returned)) = (&session.trace, this.returned.take()) {
            let idle = now.saturating_duration_since(returned);
            if idle >= MIN_IDLE {
                trace.record(Event::Idle {
                    ns: u64::try_from(idle.as_nanos()).unwrap_or(u64::MAX),
                });
            }
        }
        match &result {
            Poll::Pending => {
                if this.waiting.is_none() {
                    session.waiting_since.store(now_ns(), Ordering::Relaxed);
                }
                this.waiting.get_or_insert(now);
            }
            Poll::Ready(Ok(())) => {
                session.waiting_since.store(0, Ordering::Relaxed);
                session.last_read.store(now_ns(), Ordering::Relaxed);
                let n = (buf.filled().len() - before) as u64;
                session.served.fetch_add(n, Ordering::Relaxed);
                session.pos.fetch_add(n, Ordering::Relaxed);
                guard.sessions.served[session.delivery as usize].fetch_add(n, Ordering::Relaxed);
                let waited = this
                    .waiting
                    .take()
                    .map(|at| now.saturating_duration_since(at));
                if n > 0 && session.first_byte_ns.load(Ordering::Relaxed) == u64::MAX {
                    let ns = waited.unwrap_or_default().as_nanos();
                    session
                        .first_byte_ns
                        .store(u64::try_from(ns).unwrap_or(u64::MAX - 1), Ordering::Relaxed);
                } else if let Some(waited) = waited.filter(|w| *w >= MIN_STALL && !this.seeked) {
                    let ns = u64::try_from(waited.as_nanos()).unwrap_or(u64::MAX);
                    session.stall_ns.fetch_add(ns, Ordering::Relaxed);
                    session.stalls.fetch_add(1, Ordering::Relaxed);
                }
                if n > 0 {
                    this.seeked = false;
                }
                this.returned = Some(now);
            }
            Poll::Ready(Err(_)) => {
                session.waiting_since.store(0, Ordering::Relaxed);
                this.waiting = None;
            }
        }
        result
    }
}

#[allow(clippy::significant_drop_tightening)] // False positive: `this` borrows, it drops nothing.
impl AsyncSeek for Handle {
    fn start_seek(self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        Pin::new(&mut self.get_mut().reader).start_seek(position)
    }

    fn poll_complete(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        let this = self.get_mut();
        let result = Pin::new(&mut this.reader).poll_complete(cx);
        if let (Some(guard), Poll::Ready(Ok(to))) = (&this.session, &result) {
            let session = &guard.session;
            let from = session.pos.swap(*to, Ordering::Relaxed);
            if from != *to {
                session.seeks.fetch_add(1, Ordering::Relaxed);
                // A seek is the reader's request, not a pause.
                this.returned = None;
                this.seeked = true;
                if let Some(trace) = &session.trace {
                    trace.record(Event::Seek { from, to: *to });
                }
            }
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use std::io::Cursor;

    use tokio::io::{AsyncReadExt, AsyncSeekExt};

    use super::*;

    /// Reads one byte after a simulated wait of `MIN_STALL`.
    async fn read_after_wait(handle: &mut Handle) {
        handle.waiting = Instant::now().checked_sub(MIN_STALL);
        handle.read_exact(&mut [0; 1]).await.unwrap();
    }

    #[tokio::test]
    async fn the_wait_after_a_seek_is_not_a_stall() {
        let sessions = Arc::new(Sessions::default());
        let origin = Origin {
            delivery: Delivery::WebDav,
            client: "test".into(),
            player: None,
        };
        let guard = sessions.start(ItemId::new(), "/f".into(), 8, origin);
        let mut handle = Handle::new(Reader::Memory(Cursor::new(vec![0; 8])), Some(guard));
        let session = Arc::clone(handle.session().unwrap());
        handle.read_exact(&mut [0; 1]).await.unwrap();

        handle.seek(SeekFrom::Start(4)).await.unwrap();
        read_after_wait(&mut handle).await;
        assert_eq!(session.stalls().0, 0, "first byte after a seek");

        read_after_wait(&mut handle).await;
        assert_eq!(session.stalls().0, 1, "bytes clear the seek");
    }

    fn traced_session(sessions: &Arc<Sessions>) -> Guard {
        sessions.set_tracing(Some(Tracing {
            until: Instant::now() + Duration::from_secs(60),
            capacity: 100,
        }));
        let origin = Origin {
            delivery: Delivery::Fuse,
            client: "test".into(),
            player: None,
        };
        sessions.start(ItemId::new(), "/f".into(), 8, origin)
    }

    #[test]
    fn requests_trace_their_stages_and_a_drop_is_a_cancellation() {
        let sessions = Arc::new(Sessions::default());
        let guard = traced_session(&sessions);
        let session = &guard.session;
        let arrived = Instant::now();
        session.opened(7, 3, 42, arrived);
        assert_eq!(session.handle(), Some(3));

        let done = session.request(8, 0, 4096, Instant::now());
        let dropped = session.request(9, 4096, 4096, Instant::now());
        done.stage(ReadStage::Lock);
        let pending = session.requests();
        assert_eq!(pending.len(), 2);
        let first = pending.iter().find(|p| p.request == 8).unwrap();
        assert_eq!(first.stage, ReadStage::Lock);
        assert!(first.stage_age <= first.age);

        done.stage(ReadStage::Read);
        done.end(Some(4096));
        drop(dropped);
        assert!(session.requests().is_empty());

        let summary = session.trace.as_ref().unwrap().summary();
        assert_eq!(summary.requests, 2);
        assert_eq!(summary.requests_cancelled, 1);
        assert_eq!(summary.requests_failed, 0);
        assert_eq!(summary.requests_pending, 0);
        assert!(summary.first_reply_ns.is_some());
        assert!(summary.open_before_ns.is_some());
    }

    #[test]
    fn metadata_calls_are_listed_only_while_tracing_and_when_slow() {
        let sessions = Arc::new(Sessions::default());
        let slow = Instant::now()
            .checked_sub(Duration::from_millis(5))
            .unwrap();
        sessions.fuse_op("lookup", 1, 1, None, slow, 0);
        assert_eq!(sessions.fuse_ops().seen, 0, "not tracing");
        assert!(sessions.begin_op("lookup", 1, 1, slow).is_none());

        let _guard = traced_session(&sessions);
        sessions.fuse_op("lookup", 2, 1, Some(7), Instant::now(), 0);
        sessions.fuse_op("lookup", 3, 5, Some(8), slow, 2);
        let ops = sessions.fuse_ops();
        assert_eq!((ops.seen, ops.fast), (2, 1));
        let op = ops.listed.front().unwrap();
        assert_eq!(
            (op.op, op.request, op.inode, op.child, op.errno),
            ("lookup", 3, 5, Some(8), 2)
        );
        assert!(op.took >= Duration::from_millis(5));
    }

    #[test]
    fn metadata_calls_without_a_reply_are_pending_until_they_finish() {
        let sessions = Arc::new(Sessions::default());
        let _guard = traced_session(&sessions);
        let slow = Instant::now()
            .checked_sub(Duration::from_millis(5))
            .unwrap();
        let stuck = sessions.begin_op("open", 4, 9, slow).unwrap();
        let fresh = sessions.begin_op("getattr", 5, 9, Instant::now()).unwrap();
        let pending = sessions.pending_ops();
        let listed: Vec<_> = pending.iter().map(|p| (p.op, p.request, p.inode)).collect();
        assert_eq!(listed, [("open", 4, 9), ("getattr", 5, 9)], "oldest first");
        assert!(pending[0].age >= Duration::from_millis(5));
        drop(fresh);
        drop(stuck);
        assert!(sessions.pending_ops().is_empty());
    }
}
