//! Open reads of item files, for the dashboard, and their optional traces.

use std::collections::VecDeque;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::task::{Context, Poll};
use std::time::{Duration, Instant, SystemTime};

use infinidysk_core::ItemId;
use infinidysk_core::trace::{Event, Trace};
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
    /// Nanoseconds from the first read request to its first byte; `u64::MAX` before it.
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
}

struct Listed {
    id: u64,
    offset: u64,
    size: u32,
    /// [`now_ns`] stamp.
    arrived: u64,
    stage: ReadStage,
}

/// Where a FUSE read waits before the reader serves it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReadStage {
    /// For reads of earlier offsets on the same handle.
    Order,
    /// For the handle's reader, busy with another read.
    Lock,
    /// In the reader.
    Read,
}

impl ReadStage {
    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Order => "order",
            Self::Lock => "handle_lock",
            Self::Read => "read",
        }
    }
}

/// A read not answered yet.
#[derive(Clone, Copy, Debug)]
pub struct PendingRead {
    pub offset: u64,
    pub size: u32,
    pub age: Duration,
    pub stage: ReadStage,
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

    /// Time from the first read request to its first byte; a client's pause after open
    /// is not counted.
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
        let requests = self.lock_requests();
        let mut pending: Vec<_> = requests
            .iter()
            .map(|r| PendingRead {
                offset: r.offset,
                size: r.size,
                age: age(r.arrived).unwrap_or_default(),
                stage: r.stage,
            })
            .collect();
        drop(requests);
        pending.sort_by_key(|p| std::cmp::Reverse(p.age));
        pending
    }

    /// Lists FUSE read `id` until the returned guard drops.
    pub fn request(self: &Arc<Self>, id: u64, offset: u64, size: u32) -> Request {
        self.lock_requests().push(Listed {
            id,
            offset,
            size,
            arrived: now_ns(),
            stage: ReadStage::Order,
        });
        Request {
            session: Arc::clone(self),
            id,
        }
    }

    fn lock_requests(&self) -> MutexGuard<'_, Vec<Listed>> {
        self.requests.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// A FUSE read listed on its session until dropped.
pub struct Request {
    session: Arc<Session>,
    id: u64,
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
        }
    }
}

impl Drop for Request {
    fn drop(&mut self) {
        self.session.lock_requests().retain(|r| r.id != self.id);
    }
}

#[derive(Default)]
pub struct Sessions {
    next: AtomicU64,
    /// Bytes served since start, indexed like [`Delivery::ALL`].
    served: [AtomicU64; 2],
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
        self.lock().tracing = tracing;
    }

    /// The tracing window, if still running.
    pub fn tracing(&self) -> Option<Tracing> {
        self.lock().tracing.filter(|t| t.until > Instant::now())
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
}
