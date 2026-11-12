use std::io;
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU32, AtomicU64, Ordering};
use std::task::{Context, Poll};
use std::time::{Duration, Instant};

use infinidysk_core::wait::{age, now_ns};
use infinidysk_yenc::{DecodeEnd, DecoderState, crc32, decode_incremental};
use rustls::pki_types::ServerName;
use socket2::{SockRef, TcpKeepalive};
use tokio::io::{
    AsyncBufReadExt, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, BufReader, ReadBuf,
};
use tokio::net::TcpStream;
use tokio::time::timeout;
use tokio_rustls::TlsConnector;
use tokio_rustls::client::TlsStream;
use tokio_util::either::Either;

use crate::article::{Body, MAX_DECODED_LEN, crc_field, field};
use crate::{Bandwidth, Credentials, Error, Usage, resolve, tls};

/// Socket read buffer; large enough that one TLS record batch fits per fill.
const READ_BUFFER_LEN: usize = 128 * 1024;
/// Longest header/status line accepted.
const MAX_LINE_LEN: u64 = 64 * 1024;
/// RFC 3977 caps message-ids at 250 octets.
const MAX_MESSAGE_ID_LEN: usize = 250;
/// Reply text kept on [`Error::Status`] and [`Error::AuthRejected`].
const MAX_REPLY_LEN: usize = 120;
/// Extra decode capacity so `\r\n=yend` is reached without growing the body buffer.
const DECODE_SLACK: usize = 64;
const OTHER_PART: Error = Error::Yenc("article is another part of the file");
/// Longest TCP connect to an address with another one after it; also at most half the
/// time left, so a silent address cannot use up the connect deadline.
const ADDRESS_WAIT: Duration = Duration::from_secs(5);

type Stream = Either<TcpStream, TlsStream<TcpStream>>;

/// Tries `addrs` in order, one at a time, until one connects by `deadline`. The last
/// address gets all the time left.
async fn connect_addresses<C, A>(
    addrs: &[std::net::SocketAddr],
    deadline: tokio::time::Instant,
    mut connect: C,
) -> Result<TcpStream, Error>
where
    C: FnMut(std::net::SocketAddr) -> A,
    A: Future<Output = io::Result<TcpStream>>,
{
    let mut last = None;
    for (i, &addr) in addrs.iter().enumerate() {
        let now = tokio::time::Instant::now();
        let Some(left) = deadline
            .checked_duration_since(now)
            .filter(|d| !d.is_zero())
        else {
            return Err(Error::Timeout);
        };
        let until = if i + 1 < addrs.len() {
            now + ADDRESS_WAIT.min(left / 2)
        } else {
            deadline
        };
        match tokio::time::timeout_at(until, connect(addr)).await {
            Ok(Ok(tcp)) => return Ok(tcp),
            Ok(Err(error)) => last = Some(Error::Io(error)),
            Err(_) => last = Some(Error::Timeout),
        }
    }
    Err(last.unwrap_or_else(|| {
        Error::Io(io::Error::new(
            io::ErrorKind::InvalidInput,
            "could not resolve to any address",
        ))
    }))
}

/// Counts the NNTP bytes read (after TLS) into the provider's [`Usage`] and the
/// [`Bandwidth`] bill.
struct Metered {
    inner: Stream,
    usage: Option<Arc<Usage>>,
    unpaid: u64,
    link: Arc<Link>,
}

impl AsyncRead for Metered {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        let poll = Pin::new(&mut self.inner).poll_read(cx, buf);
        let read = (buf.filled().len() - before) as u64;
        if read > 0 {
            self.link.last_byte.store(now_ns(), Ordering::Relaxed);
        }
        if let Some(usage) = &self.usage {
            usage.add(read);
        }
        self.unpaid += read;
        poll
    }
}

/// What a connection is doing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    /// Nothing to do.
    Idle,
    /// Waiting for the provider to answer its oldest request.
    Reply,
    /// Receiving a body.
    Transfer,
    /// Held to the download limit.
    Bandwidth,
    /// Reading a reply nobody waits for any more.
    Draining,
}

impl Phase {
    const ALL: [Self; 5] = [
        Self::Idle,
        Self::Reply,
        Self::Transfer,
        Self::Bandwidth,
        Self::Draining,
    ];

    #[must_use]
    pub const fn label(self) -> &'static str {
        match self {
            Self::Idle => "idle",
            Self::Reply => "reply",
            Self::Transfer => "transfer",
            Self::Bandwidth => "bandwidth",
            Self::Draining => "draining",
        }
    }
}

/// Where a connect failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Stage {
    Resolve,
    Tcp,
    Tls,
    Greeting,
    Auth,
}

impl Stage {
    /// Whether the server had accepted the TCP connection.
    pub(crate) const fn reached(self) -> bool {
        matches!(self, Self::Tls | Self::Greeting | Self::Auth)
    }

    pub(crate) const fn label(self) -> &'static str {
        match self {
            Self::Resolve => "resolve",
            Self::Tcp => "tcp",
            Self::Tls => "tls",
            Self::Greeting => "greeting",
            Self::Auth => "auth",
        }
    }
}

/// Live state of one connection, shared with its pool.
pub(crate) struct Link {
    pub(crate) id: u32,
    phase: AtomicU8,
    /// [`now_ns`] stamps: when `phase` began, and when the last byte arrived.
    since: AtomicU64,
    last_byte: AtomicU64,
}

impl Link {
    fn new() -> Self {
        static NEXT: AtomicU32 = AtomicU32::new(1);
        Self {
            id: NEXT.fetch_add(1, Ordering::Relaxed),
            phase: AtomicU8::new(Phase::Idle as u8),
            since: AtomicU64::new(now_ns()),
            last_byte: AtomicU64::new(0),
        }
    }

    pub(crate) fn set(&self, phase: Phase) {
        self.since.store(now_ns(), Ordering::Relaxed);
        self.phase.store(phase as u8, Ordering::Relaxed);
    }

    fn phase(&self) -> Phase {
        Phase::ALL
            .get(usize::from(self.phase.load(Ordering::Relaxed)))
            .copied()
            .unwrap_or(Phase::Idle)
    }

    pub(crate) fn view(&self, requests: usize) -> ConnectionStats {
        ConnectionStats {
            id: self.id,
            phase: self.phase(),
            elapsed: age(self.since.load(Ordering::Relaxed)).unwrap_or_default(),
            since_byte: age(self.last_byte.load(Ordering::Relaxed)),
            requests,
        }
    }
}

/// One open connection, as [`ProviderStats`](crate::ProviderStats) lists it.
#[derive(Clone, Copy, Debug)]
pub struct ConnectionStats {
    /// Unique for the process lifetime; matches trace `connection` fields.
    pub id: u32,
    pub phase: Phase,
    /// How long it has been in `phase`.
    pub elapsed: Duration,
    /// Time since it last received a byte; `None` before the first.
    pub since_byte: Option<Duration>,
    /// Requests on it, the one being answered included.
    pub requests: usize,
}

impl AsyncWrite for Metered {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.inner).poll_write(cx, buf)
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.inner).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.inner).poll_shutdown(cx)
    }
}

/// Receives a body's decoded bytes as they arrive, as `(offset, chunk)`, for a reader
/// that cannot wait for the whole article. Nothing is copied until `wanted` is set; the
/// first chunk after that starts at offset 0 and holds everything decoded so far. A
/// retry on another provider starts again at offset 0. Bytes are sent before the
/// article's CRC is checked.
#[derive(Clone, Debug)]
pub struct Progress {
    pub tx: tokio::sync::mpsc::UnboundedSender<(usize, Vec<u8>)>,
    /// Set by the reader once it may read this body before the whole article is in.
    /// Never cleared.
    pub wanted: Arc<AtomicBool>,
}

/// How to reach and log in to one provider. Build once and share: TLS sessions are
/// resumed across connections made from the same endpoint.
#[derive(Clone)]
pub struct Endpoint {
    host: String,
    port: u16,
    tls: Option<(TlsConnector, ServerName<'static>)>,
    credentials: Option<Credentials>,
    timeout: Duration,
    usage: Option<Arc<Usage>>,
    bandwidth: Option<Arc<Bandwidth>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TlsMode {
    Plain,
    /// Implicit TLS with certificate and hostname verification.
    Verified,
    /// Implicit TLS that accepts any certificate.
    Unverified,
}

impl Endpoint {
    pub(crate) fn host(&self) -> &str {
        &self.host
    }

    pub(crate) const fn port(&self) -> u16 {
        self.port
    }

    pub(crate) const fn usage(&self) -> Option<&Arc<Usage>> {
        self.usage.as_ref()
    }

    pub(crate) const fn timeout(&self) -> Duration {
        self.timeout
    }

    /// Counts every byte read on connections from this endpoint into `usage`.
    #[must_use]
    pub fn with_usage(mut self, usage: Arc<Usage>) -> Self {
        self.usage = Some(usage);
        self
    }

    /// Holds reads on connections from this endpoint to `bandwidth`'s limit.
    #[must_use]
    pub fn with_bandwidth(mut self, bandwidth: Arc<Bandwidth>) -> Self {
        self.bandwidth = Some(bandwidth);
        self
    }

    /// `timeout` bounds the whole connect and login, and every later single read or
    /// write.
    pub fn new(
        host: impl Into<String>,
        port: u16,
        tls: TlsMode,
        credentials: Option<Credentials>,
        timeout: Duration,
    ) -> Result<Self, Error> {
        let host = host.into();
        let tls = match tls {
            TlsMode::Plain => None,
            TlsMode::Verified | TlsMode::Unverified => {
                let name = ServerName::try_from(host.clone())
                    .map_err(|_| Error::InvalidArgument("host"))?;
                let config = tls::client_config(tls == TlsMode::Verified)?;
                Some((TlsConnector::from(Arc::new(config)), name))
            }
        };
        Ok(Self {
            host,
            port,
            tls,
            credentials,
            timeout,
            usage: None,
            bandwidth: None,
        })
    }
}

/// One NNTP connection. BODY and STAT requests may be pipelined: queue with
/// [`send_body`](Self::send_body) / [`send_stat`](Self::send_stat),
/// [`flush`](Self::flush), then read the replies in order with
/// [`read_body`](Self::read_body) / [`read_stat`](Self::read_stat).
pub struct Connection {
    stream: BufReader<Metered>,
    line: Vec<u8>,
    write_buf: Vec<u8>,
    pending: usize,
    timeout: Duration,
    timing: Timing,
    bandwidth: Option<Arc<Bandwidth>>,
}

/// Where the last body read spent its time.
#[derive(Clone, Copy, Debug, Default)]
pub struct Timing {
    /// Spent decoding yEnc and checking the CRC.
    pub decode: Duration,
}

impl Connection {
    /// Timing of the last [`read_body`](Self::read_body).
    #[must_use]
    pub const fn timing(&self) -> Timing {
        self.timing
    }

    pub(crate) fn link(&self) -> &Arc<Link> {
        &self.stream.get_ref().link
    }

    /// Connects, reads the greeting and authenticates.
    pub async fn connect(endpoint: &Endpoint) -> Result<Self, Error> {
        Self::connect_staged(endpoint)
            .await
            .map_err(|(_, error)| error)
    }

    /// [`connect`](Self::connect), with the stage a failure happened in.
    pub(crate) async fn connect_staged(endpoint: &Endpoint) -> Result<Self, (Stage, Error)> {
        Self::connect_staged_until(endpoint, tokio::time::Instant::now() + endpoint.timeout).await
    }

    /// [`connect_staged`](Self::connect_staged), with all of it ending by `deadline`.
    pub(crate) async fn connect_staged_until(
        endpoint: &Endpoint,
        deadline: tokio::time::Instant,
    ) -> Result<Self, (Stage, Error)> {
        let mut stage = Stage::Resolve;
        match tokio::time::timeout_at(deadline, Self::connect_at(endpoint, &mut stage, deadline))
            .await
        {
            Ok(result) => result.map_err(|error| (stage, error)),
            Err(_) => Err((stage, Error::Timeout)),
        }
    }

    async fn connect_at(
        endpoint: &Endpoint,
        stage: &mut Stage,
        deadline: tokio::time::Instant,
    ) -> Result<Self, Error> {
        let timeout_after = endpoint.timeout;
        let started = Instant::now();
        let addrs = resolve::PROVIDERS
            .resolve(&endpoint.host, endpoint.port, resolve::system)
            .await?;
        let resolved = Instant::now();
        *stage = Stage::Tcp;
        let tcp = connect_addresses(&addrs, deadline, TcpStream::connect).await?;
        let connected = Instant::now();
        let peer = tcp.peer_addr().ok();
        tcp.set_nodelay(true)?;
        SockRef::from(&tcp).set_tcp_keepalive(
            &TcpKeepalive::new()
                .with_time(Duration::from_secs(60))
                .with_interval(Duration::from_secs(5))
                .with_retries(3),
        )?;
        *stage = Stage::Tls;
        let stream = match &endpoint.tls {
            None => Either::Left(tcp),
            Some((connector, name)) => Either::Right(
                timeout(timeout_after, connector.connect(name.clone(), tcp))
                    .await
                    .map_err(|_| Error::Timeout)??,
            ),
        };
        let resumed = match &stream {
            Either::Left(_) => None,
            Either::Right(tls) => {
                Some(tls.get_ref().1.handshake_kind() == Some(rustls::HandshakeKind::Resumed))
            }
        };
        let secured = Instant::now();

        let mut conn = Self {
            stream: BufReader::with_capacity(
                READ_BUFFER_LEN,
                Metered {
                    inner: stream,
                    usage: endpoint.usage.clone(),
                    unpaid: 0,
                    link: Arc::new(Link::new()),
                },
            ),
            line: Vec::with_capacity(1024),
            write_buf: Vec::with_capacity(4096),
            pending: 0,
            timeout: timeout_after,
            timing: Timing::default(),
            // Set after login, so the connect deadline never covers a local limit wait.
            bandwidth: None,
        };
        *stage = Stage::Greeting;
        match conn.read_status().await? {
            200 | 201 => {}
            code => return Err(conn.unexpected(code)),
        }
        let greeted = Instant::now();
        *stage = Stage::Auth;
        if let Some(credentials) = &endpoint.credentials {
            conn.authenticate(credentials).await?;
        }
        // Login bytes stay unpaid until the first read after this.
        conn.bandwidth = endpoint.bandwidth.clone();
        let ms = |from: Instant, to: Instant| to.duration_since(from).as_millis();
        tracing::debug!(
            host = endpoint.host.as_str(),
            peer = ?peer,
            dns_ms = ms(started, resolved),
            tcp_ms = ms(resolved, connected),
            tls_ms = ms(connected, secured),
            tls_resumed = resumed,
            greeting_ms = ms(secured, greeted),
            auth_ms = ms(greeted, Instant::now()),
            total_ms = ms(started, Instant::now()),
            "connection opened"
        );
        Ok(conn)
    }

    /// Requests not yet answered by [`read_body`](Self::read_body).
    #[must_use]
    pub const fn pending(&self) -> usize {
        self.pending
    }

    /// Fetches and decodes one body (message-id without angle brackets).
    pub async fn body(&mut self, message_id: &str) -> Result<Body, Error> {
        self.send_body(message_id)?;
        self.flush().await?;
        self.read_body().await
    }

    /// Queues a BODY command; nothing is sent until [`flush`](Self::flush).
    pub fn send_body(&mut self, message_id: &str) -> Result<(), Error> {
        self.queue(b"BODY <", message_id)
    }

    /// Queues a STAT command; nothing is sent until [`flush`](Self::flush).
    pub fn send_stat(&mut self, message_id: &str) -> Result<(), Error> {
        self.queue(b"STAT <", message_id)
    }

    fn queue(&mut self, command: &[u8], message_id: &str) -> Result<(), Error> {
        validate_message_id(message_id)?;
        self.write_buf.extend_from_slice(command);
        self.write_buf.extend_from_slice(message_id.as_bytes());
        self.write_buf.extend_from_slice(b">\r\n");
        self.pending += 1;
        Ok(())
    }

    /// Reads the reply to the oldest pending STAT request: `Ok` when the article exists.
    pub async fn read_stat(&mut self) -> Result<(), Error> {
        self.take_pending()?;
        match self.read_status().await? {
            223 => Ok(()),
            423 | 430 | 451 => Err(Error::NotFound),
            code => Err(self.unexpected(code)),
        }
    }

    fn take_pending(&mut self) -> Result<(), Error> {
        self.pending = self
            .pending
            .checked_sub(1)
            .ok_or(Error::InvalidArgument("no pending request"))?;
        Ok(())
    }

    /// Cheap round trip that keeps an idle connection alive and proves it still works.
    pub async fn keepalive(&mut self) -> Result<(), Error> {
        if self.pending > 0 {
            return Err(Error::InvalidArgument("requests pending"));
        }
        self.write_buf.extend_from_slice(b"DATE\r\n");
        self.flush().await?;
        match self.read_status().await? {
            111 => Ok(()),
            code => Err(self.unexpected(code)),
        }
    }

    /// Sends all queued commands in one write.
    pub async fn flush(&mut self) -> Result<(), Error> {
        if self.write_buf.is_empty() {
            return Ok(());
        }
        let stream = self.stream.get_mut();
        timeout(self.timeout, async {
            stream.write_all(&self.write_buf).await?;
            stream.flush().await
        })
        .await
        .map_err(|_| Error::Timeout)??;
        self.write_buf.clear();
        Ok(())
    }

    /// Reads the reply to the oldest pending BODY request.
    pub async fn read_body(&mut self) -> Result<Body, Error> {
        self.read_body_with(None, None, || {}).await
    }

    /// [`read_body`](Self::read_body), sending decoded bytes to `progress` as they arrive
    /// once it is wanted.
    /// With `begin`, an article for another part of the posted file (its `=ypart` offset
    /// is not `begin`) fails as [`Error::Yenc`] before any byte reaches `progress`.
    /// `replied` runs once the status line is in.
    pub async fn read_body_with(
        &mut self,
        progress: Option<&Progress>,
        begin: Option<u64>,
        replied: impl FnOnce(),
    ) -> Result<Body, Error> {
        self.take_pending()?;
        self.timing = Timing::default();
        let code = self.read_status().await?;
        replied();
        let link = self.link();
        if link.phase() == Phase::Reply {
            link.set(Phase::Transfer);
        }
        match code {
            222 => {}
            // 451 is a provider-specific "article unavailable".
            423 | 430 | 451 => return Err(Error::NotFound),
            code => return Err(self.unexpected(code)),
        }

        let (file_size, part, total) = loop {
            self.read_line().await?;
            if is_terminator(&self.line) {
                return Err(Error::Yenc("missing =ybegin"));
            }
            if self.line.starts_with(b"=ybegin ") {
                break (
                    field::<u64>(&self.line, b"size"),
                    field::<u32>(&self.line, b"part"),
                    field::<u32>(&self.line, b"total"),
                );
            }
        };
        let Some(file_size) = file_size else {
            return self.fail_drained(Error::Yenc("missing =ybegin size")).await;
        };

        let other_part = |offset: u64| begin.is_some_and(|begin| begin != offset);
        let mut offset = 0;
        let mut expected = usize::try_from(file_size).unwrap_or(usize::MAX);
        // Whether the offset was checked against `begin`; a `=ypart` line split across
        // reads is only seen by the decoder, so the check waits for it or the first byte.
        let mut checked = false;
        if self.fill().await?.starts_with(b"=ypart ") {
            self.read_line().await?;
            match parse_ypart(&self.line) {
                Some((begin, len)) => (offset, expected) = (begin, len),
                None => return self.fail_drained(Error::Yenc("invalid =ypart")).await,
            }
            if other_part(offset) {
                return self.fail_drained(OTHER_PART).await;
            }
            checked = true;
        }

        let mut data = Vec::with_capacity(expected.min(MAX_DECODED_LEN) + DECODE_SLACK);
        let mut state = DecoderState::default();
        let mut crc = 0;
        // Decoded bytes already sent to `progress`.
        let mut sent = 0;
        // Another part found mid-data: decoded to its end and discarded, so the
        // connection stays in step without draining from inside an encoded line.
        let mut wrong = false;
        loop {
            if data.len() > MAX_DECODED_LEN {
                return Err(Error::TooLarge);
            }
            if data.capacity() == data.len() {
                data.reserve(READ_BUFFER_LEN);
            }
            let buf = self.fill().await?;
            let decoded = data.len();
            let started = Instant::now();
            let (used, end) = decode_incremental(buf, &mut data, &mut state);
            crc = crc32(crc, &data[decoded..]);
            self.timing.decode += started.elapsed();
            self.stream.consume(used);
            if !checked && data.len() > decoded {
                checked = true;
                wrong = other_part(offset);
            }
            if wrong {
                data.clear();
            } else if let Some(progress) = progress
                && data.len() > sent
                && progress.wanted.load(Ordering::Relaxed)
                && !progress.tx.is_closed()
            {
                let _ = progress.tx.send((sent, data[sent..].to_vec()));
                sent = data.len();
            }
            match end {
                DecodeEnd::None => {}
                DecodeEnd::Article => {
                    if wrong || (!checked && other_part(offset)) {
                        return Err(OTHER_PART);
                    }
                    // Without `=yend`, only the length shows the article is whole.
                    if data.len() < expected {
                        return Err(Error::Yenc("article ended before its announced size"));
                    }
                    break;
                }
                DecodeEnd::Control => {
                    // Rest of a `=y…` line; data resumes at a line start.
                    self.read_line().await?;
                    state = DecoderState::default();
                    if wrong {
                        if self.line.starts_with(b"end") {
                            self.drain().await?;
                            return Err(OTHER_PART);
                        }
                    } else if self.line.starts_with(b"part ") {
                        match parse_ypart(&self.line) {
                            Some((begin, _)) if other_part(begin) => {
                                return self.fail_drained(OTHER_PART).await;
                            }
                            Some((begin, len)) => {
                                (offset, expected) = (begin, len);
                                let fits = len.min(MAX_DECODED_LEN) + DECODE_SLACK;
                                if data.is_empty() && data.capacity() > fits {
                                    data = Vec::with_capacity(fits);
                                }
                            }
                            None if !checked => {
                                return self.fail_drained(Error::Yenc("invalid =ypart")).await;
                            }
                            None => {}
                        }
                        checked = true;
                    } else if self.line.starts_with(b"end") {
                        let key: &[u8] = if part.is_some() { b"pcrc32" } else { b"crc32" };
                        let posted = crc_field(&self.line, key);
                        self.drain().await?;
                        if !checked && other_part(offset) {
                            return Err(OTHER_PART);
                        }
                        if let Some(expected) = posted
                            && expected != crc
                        {
                            return Err(Error::CrcMismatch {
                                expected,
                                actual: crc,
                            });
                        }
                        break;
                    }
                }
            }
        }
        Ok(Body {
            data: data.into(),
            offset,
            file_size,
            part,
            total,
        })
    }

    async fn authenticate(&mut self, credentials: &Credentials) -> Result<(), Error> {
        let Credentials { username, password } = credentials;
        if username.is_empty()
            || !username.bytes().all(|b| b.is_ascii_graphic())
            || password.bytes().any(|b| b == b'\r' || b == b'\n')
        {
            return Err(Error::InvalidArgument("credentials"));
        }

        self.write_buf.extend_from_slice(b"AUTHINFO USER ");
        self.write_buf.extend_from_slice(username.as_bytes());
        self.write_buf.extend_from_slice(b"\r\n");
        self.flush().await?;
        match self.read_status().await? {
            281 => return Ok(()),
            381 => {}
            code => return Err(self.rejected(code)),
        }
        self.write_buf.extend_from_slice(b"AUTHINFO PASS ");
        self.write_buf.extend_from_slice(password.as_bytes());
        self.write_buf.extend_from_slice(b"\r\n");
        self.flush().await?;
        match self.read_status().await? {
            281 => Ok(()),
            code => Err(self.rejected(code)),
        }
    }

    /// Buffered bytes, reading more from the socket if none are buffered.
    async fn fill(&mut self) -> Result<&[u8], Error> {
        if self.stream.buffer().is_empty() {
            self.pay().await;
        }
        let buf = timeout(self.timeout, self.stream.fill_buf())
            .await
            .map_err(|_| Error::Timeout)??;
        if buf.is_empty() {
            return Err(Error::Closed);
        }
        Ok(buf)
    }

    /// Reads one full line, including its `\n`, into `self.line`.
    async fn read_line(&mut self) -> Result<(), Error> {
        self.pay().await;
        self.line.clear();
        let mut limited = (&mut self.stream).take(MAX_LINE_LEN);
        let n = timeout(self.timeout, limited.read_until(b'\n', &mut self.line))
            .await
            .map_err(|_| Error::Timeout)??;
        if n == 0 {
            return Err(Error::Closed);
        }
        if self.line.last() != Some(&b'\n') {
            // Over-long line, or EOF mid-line.
            return Err(Error::Protocol);
        }
        Ok(())
    }

    /// Waits out the download limit for bytes already read. Outside the read timeout,
    /// so a slow limit never fails a healthy connection.
    async fn pay(&mut self) {
        if let Some(bandwidth) = &self.bandwidth
            && self.stream.get_ref().unpaid > 0
        {
            let unpaid = std::mem::take(&mut self.stream.get_mut().unpaid);
            let link = &self.stream.get_ref().link;
            let (phase, since) = (link.phase(), link.since.load(Ordering::Relaxed));
            link.set(Phase::Bandwidth);
            bandwidth.take(unpaid).await;
            link.phase.store(phase as u8, Ordering::Relaxed);
            link.since.store(since, Ordering::Relaxed);
        }
    }

    async fn read_status(&mut self) -> Result<u16, Error> {
        self.read_line().await?;
        self.line
            .get(..3)
            .filter(|digits| digits.iter().all(u8::is_ascii_digit))
            .map(|digits| {
                digits
                    .iter()
                    .fold(0, |code, &d| code * 10 + u16::from(d - b'0'))
            })
            .ok_or(Error::Protocol)
    }

    /// [`Error::Status`] with the text of the status line just read.
    fn unexpected(&self, code: u16) -> Error {
        Error::Status {
            code,
            reply: self.reply(),
        }
    }

    fn rejected(&self, code: u16) -> Error {
        Error::AuthRejected {
            code,
            reply: self.reply(),
        }
    }

    /// Text of the status line just read, cleaned for logs.
    fn reply(&self) -> Box<str> {
        let text = String::from_utf8_lossy(self.line.get(3..).unwrap_or_default());
        let reply: String = text.chars().filter(|c| !c.is_control()).collect();
        let reply = reply.trim();
        let mut end = reply.len().min(MAX_REPLY_LEN);
        while !reply.is_char_boundary(end) {
            end -= 1;
        }
        reply[..end].into()
    }

    /// Skips lines through the article terminator.
    async fn drain(&mut self) -> Result<(), Error> {
        while !is_terminator(&self.line) {
            self.read_line().await?;
        }
        Ok(())
    }

    /// Drains the rest of the article so the connection stays usable, then fails.
    async fn fail_drained<T>(&mut self, error: Error) -> Result<T, Error> {
        self.line.clear();
        self.drain().await?;
        Err(error)
    }
}

pub(crate) fn validate_message_id(message_id: &str) -> Result<(), Error> {
    if message_id.is_empty()
        || message_id.len() > MAX_MESSAGE_ID_LEN
        || !message_id
            .bytes()
            .all(|b| b.is_ascii_graphic() && b != b'<' && b != b'>')
    {
        return Err(Error::InvalidArgument("message-id"));
    }
    Ok(())
}

fn is_terminator(line: &[u8]) -> bool {
    matches!(line, b".\r\n" | b".\n")
}

/// `(offset, len)` from `=ypart begin=… end=…` (one-based, inclusive).
fn parse_ypart(line: &[u8]) -> Option<(u64, usize)> {
    let begin = field::<u64>(line, b"begin")?;
    let end = field::<u64>(line, b"end")?;
    let len = end.checked_sub(begin)?.checked_add(1)?;
    Some((begin.checked_sub(1)?, usize::try_from(len).ok()?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_testkit::LoopbackServer;

    struct Dropped(Arc<AtomicBool>);

    impl Drop for Dropped {
        fn drop(&mut self) {
            self.0.store(true, Ordering::SeqCst);
        }
    }

    #[tokio::test]
    async fn connect_addresses_skips_a_stalled_address() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let good = listener.local_addr().unwrap();
        let stalled: std::net::SocketAddr = "192.0.2.1:119".parse().unwrap();
        let dropped = Arc::new(AtomicBool::new(false));
        let dropped_first = Arc::new(AtomicBool::new(false));
        let start = tokio::time::Instant::now();
        let deadline = start + Duration::from_secs(1);

        let tcp = connect_addresses(&[stalled, good], deadline, |addr| {
            let guard = (addr == stalled).then(|| Dropped(Arc::clone(&dropped)));
            if addr == good {
                dropped_first.store(dropped.load(Ordering::SeqCst), Ordering::SeqCst);
            }
            async move {
                if let Some(_guard) = guard {
                    std::future::pending::<()>().await;
                }
                TcpStream::connect(addr).await
            }
        })
        .await
        .unwrap();
        assert_eq!(tcp.peer_addr().unwrap(), good);
        assert!(
            dropped_first.load(Ordering::SeqCst),
            "the stalled attempt ended first"
        );
        // Half the budget went to the stalled address, not all of it.
        assert!(
            start.elapsed() < Duration::from_millis(900),
            "{:?}",
            start.elapsed()
        );

        // A single address keeps the whole budget.
        let tcp = connect_addresses(
            &[good],
            tokio::time::Instant::now() + Duration::from_secs(1),
            |addr| async move {
                tokio::time::sleep(Duration::from_millis(700)).await;
                TcpStream::connect(addr).await
            },
        )
        .await
        .unwrap();
        assert_eq!(tcp.peer_addr().unwrap(), good);
    }

    #[tokio::test]
    async fn login_preserves_bandwidth_debt_until_the_limiter_is_attached() {
        let server = LoopbackServer::start(Vec::new()).await.unwrap();
        let usage = Arc::new(Usage::default());
        let bandwidth = Arc::new(Bandwidth::new(Some(1)));
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            Some(Credentials {
                username: "user".into(),
                password: "pass".into(),
            }),
            Duration::from_secs(5),
        )
        .unwrap()
        .with_usage(Arc::clone(&usage))
        .with_bandwidth(Arc::clone(&bandwidth));
        let mut conn = Connection::connect(&endpoint).await.unwrap();
        let debt = usage.used();
        assert!(debt > 0);
        assert_eq!(conn.stream.get_ref().unpaid, debt);

        let mut payment = Box::pin(conn.pay());
        std::future::poll_fn(|context| {
            assert!(std::future::Future::poll(payment.as_mut(), context).is_pending());
            std::task::Poll::Ready(())
        })
        .await;
        bandwidth.set_limit(None);
        payment.await;
        assert_eq!(conn.stream.get_ref().unpaid, 0);
        assert_eq!(usage.used(), debt);
    }
}
