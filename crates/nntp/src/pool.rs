use std::collections::VecDeque;
use std::future::{Future, poll_fn};
use std::pin::{Pin, pin};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, PoisonError};
use std::task::Poll;
use std::time::{Duration, Instant};

use infinidysk_core::trace::{Outcome, Probe, Reason, Step};
use tokio::sync::{Notify, mpsc, oneshot};
use tokio::time::timeout;

use crate::article::Body;
use crate::client::BodySink;
use crate::connection::{ConnectionStats, Link, Phase, Timing, validate_message_id};
use crate::downloads::{Downloads, Permit};
use crate::stats::{Stats, Workload, outcome};
use crate::{Connection, Endpoint, Error, Progress, Usage};

/// Retries after this request's own read failed at the connection level (stale idle
/// socket, stall) before giving up.
const MAX_RETRIES: usize = 2;
/// Re-dispatches of requests lost unanswered when their connection died under another
/// request; bounds a provider that drops every connection.
const MAX_UNANSWERED: usize = 16;
/// After a connect fails with no other connection live or connecting, requests fail
/// fast for this long instead of each waiting out its own connect.
const CONNECT_BACKOFF: Duration = Duration::from_secs(1);
/// A connect that never reached the provider while others are open holds back new
/// connects for [`CONNECT_BACKOFF`], doubling up to this while they keep failing.
const MAX_CONNECT_BACKOFF: Duration = Duration::from_secs(30);
/// A refused connect shrinks the limit to the connections we have; it then regrows by
/// one per step.
const THROTTLE_STEP: Duration = Duration::from_secs(5);
/// Playback gives up waiting for a download slot and connection after this long.
const STREAM_WAIT: Duration = Duration::from_secs(30);
/// Playback waits at most this long (or half the endpoint timeout) for a new connection
/// while another is live, then pipelines on that one.
const STREAM_CONNECT_WAIT: Duration = Duration::from_secs(3);
/// From this effective limit up, background work leaves one connection slot for playback.
const STREAM_HEADROOM_MIN_CONNECTIONS: usize = 4;

#[derive(Debug, Clone, Copy)]
pub struct PoolConfig {
    /// Provider connection limit; never exceeded.
    pub max_connections: usize,
    /// BODY requests in flight per connection.
    pub pipeline_depth: usize,
    /// Connections with nothing to do for this long are closed, down to
    /// `min_connections`; those are kept alive with keepalives instead.
    pub idle_timeout: Duration,
    /// Warm floor: idle connections are not closed below this many while the quota
    /// lasts. [`Client::warm`](crate::Client::warm) reopens the ones that die.
    pub min_connections: usize,
}

/// Connections to one provider. Each connection is owned by its own task, which
/// pipelines requests and reads every reply to completion, so a caller that stops
/// waiting never desynchronises the connection.
pub struct Pool {
    endpoint: Endpoint,
    config: PoolConfig,
    downloads: Arc<Downloads>,
    /// Requests holding a ticket.
    busy: Arc<AtomicUsize>,
    /// Connection tasks running.
    live: Arc<AtomicUsize>,
    state: Arc<Mutex<State>>,
    changed: Arc<Notify>,
    /// Counts every failed connect, background ones included.
    stats: Arc<Stats>,
    stream_wait: Duration,
    /// `weight-large` experiment: articles of [`LARGE_ARTICLE`] or more weigh a full depth.
    weight_large: bool,
}

/// Articles this large are never pipelined under `weight-large`.
const LARGE_ARTICLE: u32 = 2_097_152;

#[derive(Default)]
struct State {
    workers: Vec<Worker>,
    connecting: usize,
    /// `(limit, since)` after the provider turned a connect away while others were up.
    throttle: Option<(usize, Instant)>,
    /// Set when a connect could not reach the provider while others were up.
    backoff: Option<Backoff>,
    /// Last connect failure on a pool with nothing else live.
    failed_at: Option<Instant>,
    /// Why that connect failed; cleared once one succeeds.
    connect_error: Option<Arc<str>>,
    connected: bool,
}

#[derive(Clone, Copy)]
struct Backoff {
    until: Instant,
    delay: Duration,
    failures: u32,
}

impl State {
    /// Until when new connects wait; never while nothing is open, so an outage still
    /// gets noticed.
    fn held(&self, open: usize) -> Option<Instant> {
        self.backoff
            .map(|backoff| backoff.until)
            .filter(|&until| open > 0 && Instant::now() < until)
    }

    /// Effective connection limit, regrowing one per [`THROTTLE_STEP`] after a throttle.
    fn limit(&self, max: usize) -> usize {
        self.throttle.map_or(max, |(limit, since)| {
            let steps = since.elapsed().as_secs() / THROTTLE_STEP.as_secs();
            limit
                .saturating_add(usize::try_from(steps).unwrap_or(usize::MAX))
                .min(max)
        })
    }

    /// Holds back new connects after one that `started` failed to reach the provider;
    /// `true` when this starts a new run of failures.
    fn back_off(&mut self, started: Instant, now: Instant) -> bool {
        match &mut self.backoff {
            // Judged by start time: a slow failure is still a retry, not a new run.
            Some(backoff) if started < backoff.until + backoff.delay => {
                backoff.failures += 1;
                // A burst started before the hold fails within it; only a retry after it lengthens it.
                if started >= backoff.until {
                    backoff.delay = (backoff.delay * 2).min(MAX_CONNECT_BACKOFF);
                    backoff.until = now + backoff.delay;
                }
                false
            }
            _ => {
                self.backoff = Some(Backoff {
                    until: now + CONNECT_BACKOFF,
                    delay: CONNECT_BACKOFF,
                    failures: 1,
                });
                true
            }
        }
    }
}

/// Why [`Pool::fetch`] failed.
pub(crate) enum Failure {
    /// No connection could be opened and none was live: the provider is unreachable.
    Connect(Error),
    Request(Error),
}

struct Worker {
    tx: mpsc::Sender<Request>,
    load: Arc<AtomicUsize>,
    task: tokio::task::AbortHandle,
    link: Arc<Link>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum Command {
    Body,
    Stat,
}

/// Who a request is for, so its completion lands in the right counters and trace.
#[derive(Clone)]
pub(crate) struct Tag {
    pub(crate) workload: Workload,
    pub(crate) provider: u16,
    pub(crate) stats: Option<Arc<Stats>>,
    pub(crate) probe: Option<Probe>,
    pub(crate) progress: Option<Progress>,
    /// Expected `=ypart` offset; see [`Connection::read_body_with`].
    pub(crate) begin: Option<u64>,
    /// Gets bodies whose caller left before they arrived.
    pub(crate) sink: Option<Arc<OnceLock<BodySink>>>,
    /// Waits for a connection with nothing in flight rather than pipeline behind
    /// another request: a hedge queued behind a slow article is as slow as it.
    pub(crate) fresh: bool,
}

impl Tag {
    pub(crate) const UNTRACKED: Self = Self {
        workload: Workload::Stream,
        provider: 0,
        stats: None,
        probe: None,
        progress: None,
        begin: None,
        sink: None,
        fresh: false,
    };

    fn step(&self, step: Step) {
        if let Some(probe) = &self.probe {
            probe.step(step);
        }
    }
}

type Reply = Result<Option<Body>, Error>;

/// One attempt on one connection. Records its outcome exactly once: when answered,
/// before the caller sees the reply, or else when dropped. The ticket then releases
/// the permit and connection load.
struct Request {
    command: Command,
    message_id: String,
    /// `None` answers a STAT. Taken by [`answer`](Self::answer).
    reply: Option<oneshot::Sender<Reply>>,
    tag: Tag,
    started: Instant,
    recorded: bool,
    _ticket: Ticket,
}

impl Request {
    fn answer(mut self, result: Reply, decode: Duration) {
        let reply = self.reply.take();
        let bytes = match &result {
            Ok(Some(body)) => u32::try_from(body.data.len()).unwrap_or(u32::MAX),
            _ => 0,
        };
        let ended = match &result {
            // The caller stopped waiting; the reply was drained anyway.
            _ if reply.as_ref().is_none_or(oneshot::Sender::is_closed) => Outcome::Cancelled,
            Ok(_) => Outcome::Retrieved,
            Err(error) => outcome(error),
        };
        self.record(ended, bytes, decode);
        if let Some(reply) = reply.filter(|r| !r.is_closed()) {
            let _ = reply.send(result);
        } else if let (Ok(Some(body)), Some(sink)) =
            (&result, self.tag.sink.as_ref().and_then(|s| s.get()))
        {
            sink(&self.message_id, self.tag.workload, body, true);
        }
    }

    fn record(&mut self, outcome: Outcome, bytes: u32, decode: Duration) {
        self.recorded = true;
        if let Some(stats) = &self.tag.stats {
            stats.record(self.tag.workload, outcome, bytes, self.started.elapsed());
        }
        self.tag.step(Step::Done {
            provider: self.tag.provider,
            outcome,
            bytes,
            decode_ns: u32::try_from(decode.as_nanos()).unwrap_or(u32::MAX),
        });
    }
}

impl Drop for Request {
    fn drop(&mut self) {
        if self.recorded {
            return;
        }
        // Unanswered: the connection died, or the caller left before it was sent.
        if self.reply.as_ref().is_none_or(oneshot::Sender::is_closed) {
            self.record(Outcome::Cancelled, 0, Duration::ZERO);
        } else {
            // Re-dispatched, so not a provider failure; the error that ended the
            // connection is counted on the request it hit.
            self.tag.stats = None;
            self.record(Outcome::NotRetrieved(Reason::Network), 0, Duration::ZERO);
        }
    }
}

/// Held for exactly the life of one request; dropping it is the completion.
struct Ticket {
    _download: Permit,
    _load: Load,
    _busy: Load,
}

pub(crate) struct Load(Arc<AtomicUsize>, Option<Arc<Notify>>);

impl Load {
    pub(crate) fn acquire(load: &Arc<AtomicUsize>) -> Self {
        load.fetch_add(1, Ordering::Relaxed);
        Self(Arc::clone(load), None)
    }

    /// A connection's load; its release wakes requests waiting for an idle one.
    fn watched(load: &Arc<AtomicUsize>, changed: &Arc<Notify>) -> Self {
        load.fetch_add(1, Ordering::Relaxed);
        Self(Arc::clone(load), Some(Arc::clone(changed)))
    }
}

impl Drop for Load {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::Relaxed);
        if let Some(changed) = &self.1 {
            changed.notify_waiters();
        }
    }
}

impl Pool {
    /// `downloads` caps in-flight requests across every provider sharing it.
    pub fn new(
        endpoint: Endpoint,
        config: PoolConfig,
        downloads: Arc<Downloads>,
    ) -> Result<Self, Error> {
        if config.max_connections == 0 || config.pipeline_depth == 0 {
            return Err(Error::InvalidArgument("pool config"));
        }
        Ok(Self {
            endpoint,
            config,
            downloads,
            busy: Arc::default(),
            live: Arc::default(),
            state: Arc::default(),
            changed: Arc::default(),
            stats: Arc::default(),
            stream_wait: STREAM_WAIT,
            weight_large: false,
        })
    }

    pub(crate) fn set_stats(&mut self, stats: Arc<Stats>) {
        self.stats = stats;
    }

    #[cfg(test)]
    pub(crate) const fn with_stream_wait(mut self, wait: Duration) -> Self {
        self.stream_wait = wait;
        self
    }

    /// The `weight-large` experiment: articles of 2,097,152 bytes or more take a whole
    /// connection's permits whatever the depth.
    #[must_use]
    pub const fn with_weight_large(mut self, on: bool) -> Self {
        self.weight_large = on;
        self
    }

    /// Fetches and decodes one body (message-id without angle brackets).
    pub async fn body(&self, message_id: &str) -> Result<Body, Error> {
        validate_message_id(message_id)?;
        match self.fetch(Command::Body, message_id, &Tag::UNTRACKED).await {
            Ok(body) => body.ok_or(Error::Protocol),
            Err(Failure::Connect(error) | Failure::Request(error)) => Err(error),
        }
    }

    /// Whether the provider has the article: `Ok` if so, [`Error::NotFound`] if not.
    pub async fn stat(&self, message_id: &str) -> Result<(), Error> {
        validate_message_id(message_id)?;
        match self.fetch(Command::Stat, message_id, &Tag::UNTRACKED).await {
            Ok(_) => Ok(()),
            Err(Failure::Connect(error) | Failure::Request(error)) => Err(error),
        }
    }

    /// Opens connections concurrently until `connections` (capped at the limit) are open
    /// or connecting. Best effort: failures are left to the next request.
    pub async fn prewarm(&self, connections: usize) {
        let reserved = {
            let mut state = self.lock();
            state.workers.retain(|w| !w.tx.is_closed());
            let open = state.workers.len() + state.connecting;
            let backoff = open == 0
                && state
                    .failed_at
                    .is_some_and(|at| at.elapsed() < CONNECT_BACKOFF);
            let reserved = if backoff || state.held(open).is_some() {
                0
            } else {
                connections.min(self.limit(&state)).saturating_sub(open)
            };
            state.connecting += reserved;
            reserved
        };
        // Dropping a new connection's load leaves it idle in the pool.
        join_all((0..reserved).map(|_| self.open(self.connecting()))).await;
    }

    /// Ends every connection now, abandoning the requests on them.
    pub fn abort(&self) {
        let workers = std::mem::take(&mut self.lock().workers);
        for worker in workers {
            worker.task.abort();
        }
    }

    /// Closes connections with no request on them.
    pub(crate) fn close_idle(&self) {
        self.lock()
            .workers
            .retain(|w| !w.tx.is_closed() && w.load.load(Ordering::Relaxed) > 0);
    }

    pub(crate) const fn max_connections(&self) -> usize {
        self.config.max_connections
    }

    pub(crate) const fn min_connections(&self) -> usize {
        self.config.min_connections
    }

    /// Whether the endpoint's [`Usage`](crate::Usage) reached its cap.
    #[must_use]
    pub fn exhausted(&self) -> bool {
        self.endpoint.usage().is_some_and(|u| u.exhausted())
    }

    pub(crate) const fn pipeline_depth(&self) -> usize {
        self.config.pipeline_depth
    }

    /// Download slots one request holds: one per started MB of article, up to the pipeline
    /// depth, so large articles keep about one connection's worth of bytes in flight each
    /// (a seek then waits behind less) while small ones still pipeline.
    fn weight(&self, size: Option<u32>) -> usize {
        const UNIT: u32 = 1_000_000;
        size.map_or(1, |size| {
            if self.weight_large && size >= LARGE_ARTICLE {
                return self.config.pipeline_depth;
            }
            (size.div_ceil(UNIT) as usize).clamp(1, self.config.pipeline_depth)
        })
    }

    /// Whether a playback hedge of `size` bytes would get a permit and an idle or new
    /// connection right now. A snapshot, not a reservation.
    pub(crate) fn can_hedge(&self, size: u32) -> bool {
        if !self.downloads.can_acquire(true, self.weight(Some(size))) {
            return false;
        }
        let state = self.lock();
        let live = state.workers.iter().filter(|w| !w.tx.is_closed());
        let idle = live.clone().any(|w| w.load.load(Ordering::Relaxed) == 0);
        let open = live.count() + state.connecting;
        let backed_off = open == 0
            && state
                .failed_at
                .is_some_and(|at| at.elapsed() < CONNECT_BACKOFF);
        let spare = open < self.limit(&state) && !backed_off && state.held(open).is_none();
        drop(state);
        idle || spare
    }

    /// Download permits not in use, across every pool sharing them.
    pub(crate) fn free_permits(&self) -> usize {
        self.downloads.available_permits()
    }

    /// In-flight requests per mille of capacity.
    pub(crate) fn busy_permille(&self) -> usize {
        let capacity = self.config.max_connections * self.config.pipeline_depth;
        self.busy().saturating_mul(1000) / capacity
    }

    /// Requests in flight or queued on a connection.
    pub(crate) fn busy(&self) -> usize {
        self.busy.load(Ordering::Relaxed)
    }

    /// Open connections.
    pub(crate) fn live(&self) -> usize {
        self.live.load(Ordering::Relaxed)
    }

    /// Connection tasks taking requests, and how many of them have none.
    pub(crate) fn connections(&self) -> (usize, usize) {
        let state = self.lock();
        let open = state.workers.iter().filter(|w| !w.tx.is_closed());
        let idle = open
            .clone()
            .filter(|w| w.load.load(Ordering::Relaxed) == 0)
            .count();
        let open = open.count();
        drop(state);
        (open, idle)
    }

    /// Whether a connection ever opened, and why the last connect failed while none was
    /// open (cleared when one opens).
    pub(crate) fn connect_status(&self) -> (bool, Option<Arc<str>>) {
        let state = self.lock();
        (state.connected, state.connect_error.clone())
    }

    /// Open connections, and connects in progress.
    pub(crate) fn links(&self) -> (Vec<ConnectionStats>, usize) {
        let state = self.lock();
        let links = state
            .workers
            .iter()
            .filter(|w| !w.tx.is_closed())
            .map(|w| w.link.view(w.load.load(Ordering::Relaxed)))
            .collect();
        let connecting = state.connecting;
        drop(state);
        (links, connecting)
    }

    /// [`body`](Self::body) or [`stat`](Self::stat) for a validated message-id.
    #[allow(clippy::significant_drop_tightening)] // False positive: the permit moves into the ticket.
    pub(crate) async fn fetch(
        &self,
        command: Command,
        message_id: &str,
        tag: &Tag,
    ) -> Result<Option<Body>, Failure> {
        let (mut failed, mut unanswered) = (0, 0);
        let provider = tag.provider;
        let stream = tag.workload == Workload::Stream;
        let deadline = tokio::time::Instant::now() + self.stream_wait;
        loop {
            let wait = async {
                let download = self
                    .downloads
                    .acquire(
                        stream,
                        self.weight(tag.probe.as_ref().map(|probe| probe.size)),
                    )
                    .await
                    .ok_or(Failure::Request(Error::Closed))?;
                tag.step(Step::Permitted { provider });
                let (tx, load, ahead, connection) = self.worker(stream, tag.fresh).await?;
                Ok::<_, Failure>((download, tx, load, ahead, connection))
            };
            let (download, tx, load, ahead, connection) = if stream {
                tokio::time::timeout_at(deadline, wait)
                    .await
                    .map_err(|_| Failure::Request(Error::Unavailable))??
            } else {
                wait.await?
            };
            tag.step(Step::Connected {
                provider,
                ahead: u16::try_from(ahead).unwrap_or(u16::MAX),
                connection,
            });
            let ticket = Ticket {
                _download: download,
                _load: load,
                _busy: Load::acquire(&self.busy),
            };
            let request = Request {
                command,
                message_id: message_id.to_owned(),
                reply: None,
                tag: tag.clone(),
                started: Instant::now(),
                recorded: false,
                _ticket: ticket,
            };
            match dispatch(&tx, request).await {
                // Playback fails over to the next provider rather than wait out another stall.
                Some(Err(Error::Timeout)) if stream => {
                    return Err(Failure::Request(Error::Timeout));
                }
                // A 400 ends the session (RFC 3977 §3.2.1); the server closes after it.
                Some(Err(
                    Error::Closed | Error::Io(_) | Error::Timeout | Error::Status { code: 400, .. },
                )) if failed < MAX_RETRIES => {
                    failed += 1;
                }
                Some(result) => return result.map_err(Failure::Request),
                None if unanswered < MAX_UNANSWERED => unanswered += 1,
                None => return Err(Failure::Request(Error::Closed)),
            }
        }
    }

    /// An idle connection, else a new one while the limit allows, else the least loaded;
    /// with the requests already on it and its id. A `stream` request that tires of a
    /// connect leaves it running in its own task and pipelines on a live connection
    /// instead. A `fresh` request never pipelines: it waits for an idle connection instead.
    /// For limits of four or more, background work leaves one idle or unopened slot for
    /// playback and waits for pending connections before pipelining.
    async fn worker(
        &self,
        stream: bool,
        fresh: bool,
    ) -> Result<(mpsc::Sender<Request>, Load, usize, u32), Failure> {
        let mut impatient = false;
        loop {
            let mut changed = pin!(self.changed.notified());
            changed.as_mut().enable();
            let (reserved, live, held) = {
                let mut state = self.lock();
                state.workers.retain(|w| !w.tx.is_closed());
                let open = state.workers.len() + state.connecting;
                let limit = self.limit(&state);
                let held = state.held(open);
                let hold_headroom = !stream && limit >= STREAM_HEADROOM_MIN_CONNECTIONS && {
                    let idle = state
                        .workers
                        .iter()
                        .filter(|worker| worker.load.load(Ordering::Relaxed) == 0)
                        .count();
                    idle + limit.saturating_sub(open) <= 1
                };
                let spare = open < limit && !hold_headroom && held.is_none();
                let live = !state.workers.is_empty();
                let best = state
                    .workers
                    .iter()
                    .filter(|worker| !hold_headroom || worker.load.load(Ordering::Relaxed) > 0)
                    .min_by_key(|w| w.load.load(Ordering::Relaxed));
                let reserved = match best {
                    // Spread before pipelining: providers cap bandwidth per connection,
                    // and a slot still connecting is idle soon (a cold start's prewarm).
                    Some(w)
                        if (!hold_headroom && w.load.load(Ordering::Relaxed) == 0)
                            || (!fresh && (impatient || (!spare && state.connecting == 0))) =>
                    {
                        let ahead = w.load.load(Ordering::Relaxed);
                        return Ok((
                            w.tx.clone(),
                            Load::watched(&w.load, &self.changed),
                            ahead,
                            w.link.id,
                        ));
                    }
                    _ if open == 0
                        && state
                            .failed_at
                            .is_some_and(|at| at.elapsed() < CONNECT_BACKOFF) =>
                    {
                        return Err(Failure::Connect(Error::Unavailable));
                    }
                    _ if spare => {
                        state.connecting += 1;
                        true
                    }
                    // Every slot is still connecting.
                    _ => false,
                };
                drop(state);
                (reserved, live, held)
            };
            if reserved {
                let mut open = Box::pin(self.open(self.connecting()));
                let opened = if stream && live {
                    let patience = STREAM_CONNECT_WAIT.min(self.endpoint.timeout() / 2);
                    if let Ok(opened) = timeout(patience, &mut open).await {
                        opened
                    } else {
                        // Joins the pool idle if it ever connects.
                        tokio::spawn(open);
                        impatient = true;
                        continue;
                    }
                } else {
                    open.await
                };
                if let Some((tx, load, id)) = opened? {
                    return Ok((tx, load, 0, id));
                }
                continue;
            }
            match held {
                Some(until) => {
                    let _ = tokio::time::timeout_at(until.into(), changed).await;
                }
                None => changed.await,
            }
        }
    }

    fn limit(&self, state: &State) -> usize {
        state.limit(self.config.max_connections)
    }

    /// `None` when the connect was refused but other connections can take the request.
    fn open(
        &self,
        connecting: Connecting,
    ) -> impl Future<Output = Result<Option<(mpsc::Sender<Request>, Load, u32)>, Failure>> + Send + 'static
    {
        let (endpoint, config) = (self.endpoint.clone(), self.config);
        let usage = endpoint.usage().cloned();
        let live = Arc::clone(&self.live);
        let stats = Arc::clone(&self.stats);
        async move {
            let started = Instant::now();
            let conn = match Connection::connect_staged(&endpoint).await {
                Ok(conn) => conn,
                Err((stage, error)) => {
                    stats.connect_failed();
                    let mut state = connecting.lock();
                    let live = state.workers.iter().filter(|w| !w.tx.is_closed()).count();
                    // Excludes this attempt, whose slot `connecting` still holds.
                    let others = live + state.connecting - 1;
                    let now = Instant::now();
                    let (host, port) = (endpoint.host(), endpoint.port());
                    let reason = error.connect_problem();
                    let stage_label = stage.label();
                    let result = if others == 0 {
                        // Warn once per outage; retries while it lasts are debug.
                        if state.failed_at.replace(now).is_none() {
                            tracing::warn!(
                                host,
                                port,
                                stage = stage_label,
                                reason,
                                "cannot connect to provider"
                            );
                        } else {
                            tracing::debug!(
                                host,
                                port,
                                stage = stage_label,
                                reason,
                                "provider connect retry failed"
                            );
                        }
                        state.connect_error = Some(reason.into());
                        Err(Failure::Connect(error))
                    } else {
                        if stage.reached() {
                            // Turned away after accepting: likely the provider's connection
                            // limit, which the connections we hold do not exceed.
                            let limit = live.max(1);
                            if state
                                .throttle
                                .replace((limit, now))
                                .is_none_or(|(old, _)| old != limit)
                            {
                                tracing::warn!(
                                    host,
                                    port,
                                    connections = limit,
                                    stage = stage_label,
                                    reason,
                                    "provider refused more connections; limiting pool"
                                );
                            } else {
                                tracing::debug!(
                                    host,
                                    port,
                                    connections = limit,
                                    stage = stage_label,
                                    reason,
                                    "provider still refuses more connections"
                                );
                            }
                        } else if state.back_off(started, now) {
                            tracing::warn!(
                                host,
                                port,
                                connections = live,
                                stage = stage_label,
                                reason,
                                "cannot open more connections to provider; retrying later"
                            );
                        } else {
                            tracing::debug!(
                                host,
                                port,
                                connections = live,
                                stage = stage_label,
                                reason,
                                "provider connect retry failed"
                            );
                        }
                        // A hung handshake is a provider fault the breaker must hear of.
                        match error {
                            Error::Timeout if stage.reached() => Err(Failure::Request(error)),
                            _ => Ok(None),
                        }
                    };
                    drop(state);
                    drop(connecting);
                    return result;
                }
            };
            let (tx, rx) = mpsc::channel(config.pipeline_depth);
            let load = Arc::new(AtomicUsize::new(0));
            let acquired = Load::watched(&load, &connecting.changed);
            let link = Arc::clone(conn.link());
            let id = link.id;
            let task = tokio::spawn(run(
                conn,
                rx,
                config,
                Live::acquire(&live),
                usage,
                endpoint.host().to_owned(),
            ));
            let mut state = connecting.lock();
            if let Some(backoff) = state.backoff.take() {
                tracing::info!(
                    host = endpoint.host(),
                    port = endpoint.port(),
                    failures = backoff.failures,
                    "provider connects work again"
                );
            }
            state.failed_at = None;
            state.connect_error = None;
            state.connected = true;
            state.workers.push(Worker {
                tx: tx.clone(),
                load,
                task: task.abort_handle(),
                link,
            });
            // A throttle set while these connects were pending must not hold below them.
            let open = state.workers.iter().filter(|w| !w.tx.is_closed()).count();
            if state.throttle.is_some() && open > state.limit(config.max_connections) {
                state.throttle = Some((open, Instant::now()));
            }
            drop(state);
            drop(connecting);
            Ok(Some((tx, acquired, id)))
        }
    }

    /// Reserves nothing itself: the caller already counted the slot in `connecting`.
    fn connecting(&self) -> Connecting {
        Connecting {
            state: Arc::clone(&self.state),
            changed: Arc::clone(&self.changed),
        }
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Drives every future to completion concurrently, discarding outputs.
pub(crate) async fn join_all<F: Future>(futures: impl IntoIterator<Item = F>) {
    let mut pending: Vec<Pin<Box<F>>> = futures.into_iter().map(Box::pin).collect();
    poll_fn(|cx| {
        pending.retain_mut(|future| future.as_mut().poll(cx).is_pending());
        if pending.is_empty() {
            Poll::Ready(())
        } else {
            Poll::Pending
        }
    })
    .await;
}

/// `None` when the connection went away before this request was answered.
async fn dispatch(tx: &mpsc::Sender<Request>, mut request: Request) -> Option<Reply> {
    let (reply, response) = oneshot::channel();
    request.reply = Some(reply);
    tx.send(request).await.ok()?;
    response.await.ok()
}

/// Releases a reserved connection slot, also when `connect` fails or is cancelled.
struct Connecting {
    state: Arc<Mutex<State>>,
    changed: Arc<Notify>,
}

impl Connecting {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl Drop for Connecting {
    fn drop(&mut self) {
        self.lock().connecting -= 1;
        self.changed.notify_waiters();
    }
}

/// One running connection task; counted until it exits or retires itself.
struct Live(Option<Arc<AtomicUsize>>);

impl Live {
    fn acquire(live: &Arc<AtomicUsize>) -> Self {
        live.fetch_add(1, Ordering::Relaxed);
        Self(Some(Arc::clone(live)))
    }

    /// Leaves the count when more than `floor` remain; `false` keeps this one alive.
    fn retire(&mut self, floor: usize) -> bool {
        let Some(live) = &self.0 else { return true };
        let retired = live
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |n| {
                (n > floor).then(|| n - 1)
            })
            .is_ok();
        if retired {
            self.0 = None;
        }
        retired
    }
}

impl Drop for Live {
    fn drop(&mut self) {
        if let Some(live) = &self.0 {
            live.fetch_sub(1, Ordering::Relaxed);
        }
    }
}

/// Owns one connection: keeps up to `pipeline_depth` requests in flight and answers
/// them in order. Exits on idle timeout above the warm floor, pool drop or a
/// connection-level error; any requests still queued are dropped, which callers see
/// as unanswered and retry.
async fn run(
    mut conn: Connection,
    mut rx: mpsc::Receiver<Request>,
    config: PoolConfig,
    mut live: Live,
    usage: Option<Arc<Usage>>,
    host: String,
) {
    let mut in_flight = VecDeque::with_capacity(config.pipeline_depth);
    loop {
        if in_flight.is_empty() {
            conn.link().set(Phase::Idle);
            match timeout(config.idle_timeout, rx.recv()).await {
                Ok(Some(request)) => enqueue(&mut conn, &mut in_flight, request),
                Ok(None) => return,
                Err(_)
                    if live.retire(if usage.as_ref().is_some_and(|u| u.exhausted()) {
                        0
                    } else {
                        config.min_connections
                    }) =>
                {
                    return;
                }
                Err(_) => {
                    if conn.keepalive().await.is_err() {
                        return;
                    }
                    continue;
                }
            }
        }
        while in_flight.len() < config.pipeline_depth {
            match rx.try_recv() {
                Ok(request) => enqueue(&mut conn, &mut in_flight, request),
                Err(_) => break,
            }
        }
        if let Err(error) = conn.flush().await {
            let head = in_flight.pop_front();
            dropped(&host, &error, in_flight.len() + rx.len());
            if let Some(request) = head {
                request.answer(Err(error), Duration::ZERO);
            }
            return;
        }
        let Some(request) = in_flight.pop_front() else {
            continue;
        };
        let provider = request.tag.provider;
        request.tag.step(Step::Reading { provider });
        let abandoned = request
            .reply
            .as_ref()
            .is_none_or(oneshot::Sender::is_closed);
        conn.link().set(if abandoned {
            Phase::Draining
        } else {
            Phase::Reply
        });
        let result = match request.command {
            Command::Body => conn
                .read_body_with(request.tag.progress.as_ref(), request.tag.begin, || {
                    request.tag.step(Step::Receiving { provider });
                })
                .await
                .map(Some),
            Command::Stat => conn.read_stat().await.map(|()| None),
        };
        let timing = match request.command {
            Command::Body => conn.timing(),
            Command::Stat => Timing::default(),
        };
        let reusable = result
            .as_ref()
            .map_or_else(Error::is_connection_reusable, |_| true);
        if let Err(error) = &result
            && !reusable
        {
            dropped(&host, error, in_flight.len() + rx.len());
        }
        // A dropped receiver means the caller was cancelled; the reply is drained anyway.
        request.answer(result, timing.decode);
        if !reusable {
            return;
        }
    }
}

/// Expected now and then (provider resets, stalls); the requests are re-dispatched.
fn dropped(host: &str, error: &Error, requeued: usize) {
    tracing::debug!(host, reason = %error, requeued, "provider connection dropped");
}

fn enqueue(conn: &mut Connection, in_flight: &mut VecDeque<Request>, request: Request) {
    // The caller left while it was queued: sending it would only delay the requests
    // behind it. Dropping records it cancelled.
    if request
        .reply
        .as_ref()
        .is_none_or(oneshot::Sender::is_closed)
    {
        return;
    }
    let sent = match request.command {
        Command::Body => conn.send_body(&request.message_id),
        Command::Stat => conn.send_stat(&request.message_id),
    };
    match sent {
        Ok(()) => in_flight.push_back(request),
        Err(error) => request.answer(Err(error), Duration::ZERO),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slow_failing_retries_keep_doubling_the_backoff() {
        let start = Instant::now();
        let mut state = State::default();
        assert!(state.back_off(start, start));
        // Each retry starts as the hold ends and takes 6 s to fail.
        let mut retry = start + CONNECT_BACKOFF;
        for _ in 0..6 {
            assert!(!state.back_off(retry, retry + Duration::from_secs(6)));
            retry = state.backoff.map_or(retry, |b| b.until);
        }
        let backoff = state.backoff.map(|b| (b.delay, b.failures));
        assert_eq!(backoff, Some((MAX_CONNECT_BACKOFF, 7)));
    }
}
