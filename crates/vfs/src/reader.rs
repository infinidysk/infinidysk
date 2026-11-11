//! Seekable readers over a file's bytes: in memory, one segment run, or parts of several.

use std::io::{self, Cursor, SeekFrom};
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll, ready};

use infinidysk_core::crypt::{BLOCK, Cipher};
use infinidysk_core::trace::Trace;
use infinidysk_core::wait::Watch;
use infinidysk_core::{BlobId, FileMeta, Par2Proof, Segments, repair::Omitted};
use infinidysk_nntp::Client;
use infinidysk_stream::{
    Access, Budget, Cache, Filler, Gaps, Layout, Report, Segment, SegmentReader,
};
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};

use crate::Error;

/// [`AsyncRead`] + [`AsyncSeek`] over one file of the tree.
pub enum Reader {
    Memory(Cursor<Vec<u8>>),
    Segments(SegmentReader),
    Parts(PartsReader),
    Crypt(Box<CryptReader>),
    Live(Box<crate::live::LiveReader>),
}

impl Reader {
    #[allow(clippy::too_many_arguments)] // Source identity accompanies the existing reader options.
    pub(crate) fn new(
        meta: &FileMeta,
        source: Option<(BlobId, i64)>,
        client: &Arc<Client>,
        budget: &Budget,
        cache: Option<&Arc<Cache>>,
        read_ahead: u64,
        trace: Option<&Arc<Trace>>,
        report: Option<&Report>,
    ) -> Result<Self, Error> {
        let floor = infinidysk_core::experiment("win-floor").then(|| client.max_connections());
        let reader = |segments: Vec<Segment>, omitted: Vec<(usize, Omitted)>| {
            let size = segments.first().map_or(1, |s| s.size.max(1));
            let prefetch = prefetch(read_ahead, size, floor);
            let layout = Layout::new(segments)
                .with_omitted(omitted)
                .map_err(|_| Error::Corrupt("omitted segment identity differs from layout"))?;
            let mut reader = SegmentReader::new(Arc::clone(client), Arc::new(layout), prefetch)
                .with_budget(budget.clone());
            if let Some(cache) = cache {
                reader = reader.with_cache(Arc::clone(cache));
            }
            if let Some(report) = report {
                reader = reader.with_report(Arc::clone(report));
            }
            Ok::<_, Error>(match trace {
                Some(trace) => reader.with_trace(Arc::clone(trace)),
                None => reader,
            })
        };
        let (mut segments, mut omitted) = (Vec::new(), Vec::new());
        Ok(match meta {
            FileMeta::Nzb(file) => {
                append_layout(
                    &mut segments,
                    &mut omitted,
                    &file.segments,
                    0,
                    file.proof.as_ref(),
                    source,
                )?;
                Self::Segments(reader(segments, omitted)?)
            }
            FileMeta::Multipart(file) => {
                let (mut starts, mut ats) = (Vec::new(), Vec::new());
                let (mut len, mut at) = (0, 0);
                for part in &file.parts {
                    let span = part.segments.size();
                    if part.data.start < part.offset
                        || part
                            .offset
                            .checked_add(span)
                            .is_none_or(|end| part.data.end > end)
                    {
                        return Err(Error::Corrupt("part data lies outside its segments"));
                    }
                    if !part.data.is_empty() {
                        starts.push(len);
                        ats.push(at + part.data.start - part.offset);
                        len += part.data.len();
                    }
                    at += span;
                    append_layout(
                        &mut segments,
                        &mut omitted,
                        &part.segments,
                        part.offset,
                        part.proof.as_ref(),
                        source,
                    )?;
                }
                let parts = PartsReader {
                    inner: reader(segments, omitted)?,
                    starts,
                    ats,
                    len,
                    pos: 0,
                    inner_pos: 0,
                };
                match &file.crypt {
                    None => Self::Parts(parts),
                    Some(crypt) => {
                        if len < crypt.size.next_multiple_of(BLOCK as u64) {
                            return Err(Error::Corrupt("ciphertext is shorter than the file"));
                        }
                        Self::Crypt(Box::new(CryptReader {
                            inner: parts,
                            cipher: Cipher::new(&crypt.key),
                            first_iv: crypt.iv,
                            size: crypt.size,
                            pos: 0,
                            plain: Vec::new(),
                            plain_at: 0,
                            chain_at: None,
                            seed: false,
                            iv: crypt.iv,
                            pending: Vec::new(),
                        }))
                    }
                }
            }
        })
    }
}

/// Read-ahead ceiling in segments: `read_ahead` bytes' worth, at least 2. The
/// `win-floor` experiment passes the first level's connections as `floor`, so articles
/// of 2,097,152 bytes or more can grow to twice that many in flight.
// ponytail: A/B switch; deleted or made the default once its arm is decided.
fn prefetch(read_ahead: u64, size: u64, floor: Option<usize>) -> usize {
    let prefetch = usize::try_from(read_ahead / size)
        .unwrap_or(usize::MAX)
        .max(2);
    match floor {
        Some(connections) if read_ahead > 0 && size >= 2_097_152 => prefetch.max(2 * connections),
        _ => prefetch,
    }
}

/// Stream segments; `offset` is where the first starts in its posted file, so trusted
/// sizes give each segment's expected `=ypart` offset.
fn append_layout(
    layout: &mut Vec<Segment>,
    omitted: &mut Vec<(usize, Omitted)>,
    segments: &Segments,
    mut at: u64,
    proof: Option<&Par2Proof>,
    source: Option<(BlobId, i64)>,
) -> Result<(), Error> {
    for s in &segments.items {
        let end = at
            .checked_add(u64::from(s.size))
            .ok_or(Error::Corrupt("segment range overflow"))?;
        let begin = segments.sizes_trusted.then_some(at);
        if let Some(key) = Omitted::for_segment(source, s, begin, proof)
            .map_err(|_| Error::Corrupt("invalid omitted article layout"))?
        {
            omitted.push((layout.len(), key));
        }
        layout.push(Segment {
            message_id: s.id.clone(),
            size: u64::from(s.size),
            begin,
        });
        at = end;
    }
    Ok(())
}

impl Reader {
    /// Fills unavailable segments of the file `name` with its container's filler.
    #[must_use]
    pub(crate) fn with_filler(self, meta: &FileMeta, name: &str) -> Self {
        match (self, meta) {
            // Untrusted sizes may put layout offsets off the file's packet grid.
            (Self::Segments(r), FileMeta::Nzb(file)) if file.segments.sizes_trusted => {
                Self::Segments(r.with_filler(Filler::for_name(name)))
            }
            // ponytail: archive members get zeros, since their layout offsets are volume
            // offsets; map gaps to member offsets to fill a stored `.ts` too.
            (reader, _) => reader,
        }
    }

    pub(crate) fn with_access(self, access: Access) -> Self {
        match self {
            Self::Memory(_) | Self::Live(_) => self,
            Self::Segments(r) => Self::Segments(r.with_access(access)),
            Self::Parts(mut r) => {
                r.inner = r.inner.with_access(access);
                Self::Parts(r)
            }
            Self::Crypt(mut r) => {
                r.inner.inner = r.inner.inner.with_access(access);
                Self::Crypt(r)
            }
        }
    }

    /// Shares `gaps` with the file's other readers; analysis readers stay without.
    #[must_use]
    pub(crate) fn with_gaps(self, gaps: Arc<Gaps>) -> Self {
        match self {
            Self::Memory(_) | Self::Live(_) => self,
            Self::Segments(r) => Self::Segments(r.with_gaps(gaps)),
            Self::Parts(mut r) => {
                r.inner = r.inner.with_gaps(gaps);
                Self::Parts(r)
            }
            Self::Crypt(mut r) => {
                r.inner.inner = r.inner.inner.with_gaps(gaps);
                Self::Crypt(r)
            }
        }
    }

    /// Keeps `watch` up to date with what the reader is blocked on.
    #[must_use]
    pub(crate) fn with_watch(self, watch: Arc<Watch>) -> Self {
        match self {
            Self::Memory(_) | Self::Live(_) => self,
            Self::Segments(r) => Self::Segments(r.with_watch(watch)),
            Self::Parts(mut r) => {
                r.inner = r.inner.with_watch(watch);
                Self::Parts(r)
            }
            Self::Crypt(mut r) => {
                r.inner.inner = r.inner.inner.with_watch(watch);
                Self::Crypt(r)
            }
        }
    }

    /// Fails reads instead of serving bytes that are not exactly as posted.
    #[must_use]
    pub(crate) fn into_strict(self) -> Self {
        match self {
            Self::Memory(_) | Self::Live(_) => self,
            Self::Segments(r) => Self::Segments(r.with_strict()),
            Self::Parts(mut r) => {
                r.inner = r.inner.with_strict();
                Self::Parts(r)
            }
            Self::Crypt(mut r) => {
                r.inner.inner = r.inner.inner.with_strict();
                Self::Crypt(r)
            }
        }
    }

    /// The caller reads no further than `end`, so read-ahead stops there.
    pub fn set_end(&mut self, end: u64) {
        match self {
            Self::Memory(_) => {}
            Self::Live(r) => r.set_end(end),
            Self::Segments(r) => r.set_end(end),
            Self::Parts(r) => r.set_end(end),
            Self::Crypt(r) => r
                .inner
                .set_end(end.min(r.size).next_multiple_of(BLOCK as u64)),
        }
    }
}

impl AsyncRead for Reader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        match self.get_mut() {
            Self::Memory(r) => Pin::new(r).poll_read(cx, buf),
            Self::Segments(r) => Pin::new(r).poll_read(cx, buf),
            Self::Parts(r) => Pin::new(r).poll_read(cx, buf),
            Self::Crypt(r) => Pin::new(r).poll_read(cx, buf),
            Self::Live(r) => Pin::new(r).poll_read(cx, buf),
        }
    }
}

impl AsyncSeek for Reader {
    fn start_seek(self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        match self.get_mut() {
            Self::Memory(r) => Pin::new(r).start_seek(position),
            Self::Segments(r) => Pin::new(r).start_seek(position),
            Self::Parts(r) => Pin::new(r).start_seek(position),
            Self::Crypt(r) => Pin::new(r).start_seek(position),
            Self::Live(r) => Pin::new(r).start_seek(position),
        }
    }

    fn poll_complete(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        match self.get_mut() {
            Self::Memory(r) => Pin::new(r).poll_complete(cx),
            Self::Segments(r) => Pin::new(r).poll_complete(cx),
            Self::Parts(r) => Pin::new(r).poll_complete(cx),
            Self::Crypt(r) => Pin::new(r).poll_complete(cx),
            Self::Live(r) => Pin::new(r).poll_complete(cx),
        }
    }
}

/// Byte ranges of one segment run read as a single file. All parts share one
/// [`SegmentReader`], so prefetch runs on across part boundaries.
pub struct PartsReader {
    inner: SegmentReader,
    /// File offset where each span starts.
    starts: Vec<u64>,
    /// Offset of each span in `inner`.
    ats: Vec<u64>,
    len: u64,
    pos: u64,
    inner_pos: u64,
}

impl AsyncRead for PartsReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        self.get_mut().read_into(cx, buf)
    }
}

impl PartsReader {
    fn set_end(&mut self, end: u64) {
        let end = end.min(self.len);
        let inner_end = match end.checked_sub(1) {
            Some(last) => {
                let span = self.starts.partition_point(|&s| s <= last) - 1;
                self.ats[span] + (last - self.starts[span]) + 1
            }
            None => 0,
        };
        self.inner.set_end(inner_end);
    }

    fn read_into(&mut self, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<io::Result<()>> {
        if self.pos >= self.len || buf.remaining() == 0 {
            return Poll::Ready(Ok(()));
        }
        let span = self.starts.partition_point(|&s| s <= self.pos) - 1;
        let at = self.ats[span] + (self.pos - self.starts[span]);
        let end = self.starts.get(span + 1).copied().unwrap_or(self.len);
        if self.inner_pos != at {
            Pin::new(&mut self.inner).start_seek(SeekFrom::Start(at))?;
            self.inner_pos = ready!(Pin::new(&mut self.inner).poll_complete(cx))?;
        }
        let remaining = buf.remaining();
        let want = usize::try_from(end - self.pos).map_or(remaining, |left| left.min(remaining));
        let mut part = ReadBuf::new(buf.initialize_unfilled_to(want));
        ready!(Pin::new(&mut self.inner).poll_read(cx, &mut part))?;
        let n = part.filled().len();
        if n == 0 {
            return Poll::Ready(Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "segments end before the part they hold",
            )));
        }
        buf.advance(n);
        self.pos += n as u64;
        self.inner_pos += n as u64;
        Poll::Ready(Ok(()))
    }
}

impl AsyncSeek for PartsReader {
    fn start_seek(mut self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let pos = match position {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::End(delta) => self.len.checked_add_signed(delta),
            SeekFrom::Current(delta) => self.pos.checked_add_signed(delta),
        };
        self.pos = pos.ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "seek before start of file")
        })?;
        Ok(())
    }

    fn poll_complete(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        Poll::Ready(Ok(self.pos))
    }
}

/// AES-CBC plaintext over a [`PartsReader`] of ciphertext. A seek costs one extra
/// block: the ciphertext block before the target is its IV.
pub struct CryptReader {
    inner: PartsReader,
    cipher: Cipher,
    first_iv: [u8; BLOCK],
    /// Plaintext length; the ciphertext pads it to whole blocks.
    size: u64,
    pos: u64,
    /// Decrypted bytes starting at file offset `plain_at`.
    plain: Vec<u8>,
    plain_at: u64,
    /// Ciphertext offset `pending` starts at. `None` until the first read positions it.
    chain_at: Option<u64>,
    /// `pending` starts with the block that seeds `iv`, not with data.
    seed: bool,
    /// The ciphertext block before the next one to decrypt.
    iv: [u8; BLOCK],
    /// Ciphertext read but not yet a whole block.
    pending: Vec<u8>,
}

impl CryptReader {
    /// Reads further than the caller asks so small reads still decrypt in batches.
    const MIN_READ: usize = 64 * 1024;

    fn read_into(&mut self, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<io::Result<()>> {
        loop {
            if self.pos >= self.size || buf.remaining() == 0 {
                return Poll::Ready(Ok(()));
            }
            if let Some(within) = self.pos.checked_sub(self.plain_at)
                && let Ok(within) = usize::try_from(within)
                && within < self.plain.len()
            {
                let left = usize::try_from(self.size - self.pos).unwrap_or(usize::MAX);
                let n = (self.plain.len() - within).min(buf.remaining()).min(left);
                buf.put_slice(&self.plain[within..within + n]);
                self.pos += n as u64;
                return Poll::Ready(Ok(()));
            }
            let block = self.pos - self.pos % BLOCK as u64;
            let next = self
                .chain_at
                .map(|at| at + if self.seed { BLOCK as u64 } else { 0 });
            if next != Some(block) {
                // Restart the chain: at 0 from the first IV, else one block early to seed it.
                self.pending.clear();
                self.seed = block > 0;
                let from = block.saturating_sub(BLOCK as u64);
                if !self.seed {
                    self.iv = self.first_iv;
                }
                self.chain_at = Some(from);
                Pin::new(&mut self.inner).start_seek(SeekFrom::Start(from))?;
            }
            let have = self.pending.len();
            let want = buf.remaining().max(Self::MIN_READ).next_multiple_of(BLOCK);
            self.pending.resize(have + want, 0);
            let mut read = ReadBuf::new(&mut self.pending[have..]);
            let polled = self.inner.read_into(cx, &mut read);
            let n = read.filled().len();
            self.pending.truncate(have + n);
            ready!(polled)?;
            if n == 0 {
                return Poll::Ready(Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "ciphertext ends before the file",
                )));
            }
            let mut at = self.chain_at.unwrap_or(0);
            if self.seed && self.pending.len() >= BLOCK {
                self.iv.copy_from_slice(&self.pending[..BLOCK]);
                self.pending.drain(..BLOCK);
                self.seed = false;
                at += BLOCK as u64;
            }
            let whole = if self.seed {
                0
            } else {
                self.pending.len() - self.pending.len() % BLOCK
            };
            if whole > 0 {
                self.plain.clear();
                self.plain.extend_from_slice(&self.pending[..whole]);
                self.pending.drain(..whole);
                self.cipher.decrypt(&mut self.iv, &mut self.plain);
                self.plain_at = at;
                at += whole as u64;
            }
            self.chain_at = Some(at);
        }
    }
}

impl AsyncRead for CryptReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        self.get_mut().read_into(cx, buf)
    }
}

impl AsyncSeek for CryptReader {
    fn start_seek(mut self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let pos = match position {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::End(delta) => self.size.checked_add_signed(delta),
            SeekFrom::Current(delta) => self.pos.checked_add_signed(delta),
        };
        self.pos = pos.ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "seek before start of file")
        })?;
        Ok(())
    }

    fn poll_complete(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        Poll::Ready(Ok(self.pos))
    }
}

#[cfg(test)]
mod tests {
    use std::process::Command;
    use std::time::Duration;

    use infinidysk_core::trace::{Event, Step};
    use infinidysk_core::{NzbFile, Segment as MetaSegment};
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::{Corpus, LoopbackServer};
    use tokio::io::{AsyncReadExt, AsyncSeekExt};

    use super::*;

    #[test]
    fn win_floor_widens_only_large_article_windows() {
        let mb = 1_000_000;
        assert_eq!(prefetch(0, 3_840_000, Some(20)), 2);
        assert_eq!(prefetch(60 * mb, 3_840_000, None), 15);
        assert_eq!(prefetch(60 * mb, 3_840_000, Some(20)), 40);
        assert_eq!(prefetch(60 * mb, 768_000, Some(20)), 78);
        assert_eq!(prefetch(60 * mb, 2_000_000, Some(20)), 30);
    }

    fn queued(trace: &Trace) -> Vec<u32> {
        trace
            .records()
            .0
            .into_iter()
            .filter_map(|record| match record.event {
                Event::Segment {
                    segment,
                    step: Step::Queued,
                    hedge: false,
                } => Some(segment),
                _ => None,
            })
            .collect()
    }

    #[tokio::test]
    async fn win_floor_preserves_large_reader_lifecycle() {
        const CHILD: &str = "INFINIDYSK_WF_TEST_CHILD";
        const ARTICLE: usize = 2_097_152;
        const PERMITS: usize = 32;
        if std::env::var_os(CHILD).is_none() {
            tokio::task::spawn_blocking(|| {
                for flags in ["", "win-floor"] {
                    let output = Command::new(std::env::current_exe().unwrap())
                        .args([
                            "--exact",
                            "reader::tests::win_floor_preserves_large_reader_lifecycle",
                            "--nocapture",
                        ])
                        .env(CHILD, flags)
                        .env("INFINIDYSK_EXPERIMENTS", flags)
                        .output()
                        .unwrap();
                    assert!(
                        output.status.success(),
                        "flags={flags:?}\n{}\n{}",
                        String::from_utf8_lossy(&output.stdout),
                        String::from_utf8_lossy(&output.stderr),
                    );
                }
            })
            .await
            .unwrap();
            return;
        }
        let enabled = std::env::var(CHILD).unwrap() == "win-floor";
        assert_eq!(infinidysk_core::experiment("win-floor"), enabled);

        let corpus = Corpus::generate(1025, 24, ARTICLE);
        let server = LoopbackServer::start(corpus.articles.clone())
            .await
            .unwrap();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        let pool = Pool::new(
            endpoint,
            PoolConfig {
                max_connections: 8,
                pipeline_depth: 4,
                idle_timeout: Duration::from_secs(5),
                min_connections: 0,
            },
            Downloads::new(PERMITS),
        )
        .unwrap();
        let client = Arc::new(Client::new([Provider {
            pool,
            key: String::new(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]));
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: corpus
                    .articles
                    .iter()
                    .map(|article| MetaSegment {
                        id: article.message_id.clone(),
                        size: u32::try_from(ARTICLE).unwrap(),
                        fallbacks: Vec::new(),
                    })
                    .collect(),
                ..Segments::default()
            },
            proof: None,
        });
        let capacity = 4 * ARTICLE;
        let budget = Budget::new(capacity);
        let open = |read_ahead, trace: &Arc<Trace>| {
            Reader::new(
                &meta,
                None,
                &client,
                &budget,
                None,
                read_ahead,
                Some(trace),
                None,
            )
            .unwrap()
        };
        let released = || async {
            tokio::time::timeout(Duration::from_secs(10), async {
                while budget.available() != capacity || client.free_permits() != Some(PERMITS) {
                    tokio::task::yield_now().await;
                }
            })
            .await
            .expect("dropped readers release their byte budget and download permits");
        };
        let trace = Arc::new(Trace::new(10_000));
        let read_ahead = u64::try_from(8 * ARTICLE).unwrap();
        let mut reader = open(read_ahead, &trace);
        std::future::poll_fn(|context| {
            let mut first = [0; 1];
            let mut buffer = ReadBuf::new(&mut first);
            assert!(
                Pin::new(&mut reader)
                    .poll_read(context, &mut buffer)
                    .is_pending()
            );
            Poll::Ready(())
        })
        .await;
        assert_eq!(queued(&trace), (0..8).collect::<Vec<_>>(), "opening window");

        let mut head = vec![0; ARTICLE + 1];
        reader.read_exact(&mut head).await.unwrap();
        assert_eq!(head, corpus.data[..head.len()]);
        let window = if enabled { 16 } else { 8 };
        let count = queued(&trace).len();
        assert!(
            (window + 1..=window + 2).contains(&count),
            "ramped window: {count}"
        );

        for offset in [20 * ARTICLE + 13, 3 * ARTICLE + 11] {
            let before = queued(&trace).len();
            reader.seek(SeekFrom::Start(offset as u64)).await.unwrap();
            let mut bytes = [0; 64];
            reader.read_exact(&mut bytes).await.unwrap();
            assert_eq!(bytes, corpus.data[offset..offset + bytes.len()]);
            assert_eq!(
                queued(&trace)[before],
                u32::try_from(offset / ARTICLE).unwrap()
            );
        }

        let bounded_trace = Arc::new(Trace::new(10_000));
        let mut bounded = open(read_ahead, &bounded_trace);
        let start = 8 * ARTICLE + 7;
        let mut bytes = [0; 9];
        bounded.set_end((start + bytes.len()) as u64);
        bounded.seek(SeekFrom::Start(start as u64)).await.unwrap();
        tokio::time::timeout(Duration::from_secs(10), bounded.read_exact(&mut bytes))
            .await
            .expect("a reader holding the shared budget does not freeze another")
            .unwrap();
        assert_eq!(bytes, corpus.data[start..start + bytes.len()]);
        assert_eq!(
            queued(&bounded_trace),
            [8],
            "a bounded read queues only its requested segment"
        );
        drop(bounded);
        drop(reader);
        released().await;

        let analysis_trace = Arc::new(Trace::new(10_000));
        let mut analysis = open(0, &analysis_trace).into_strict();
        analysis.set_end((10 * ARTICLE) as u64);
        let mut first = [0; 1];
        analysis.read_exact(&mut first).await.unwrap();
        assert_eq!(first, corpus.data[..1]);
        assert_eq!(
            queued(&analysis_trace),
            [0, 1, 2],
            "analysis keeps a small window"
        );
        drop(analysis);
        released().await;
    }
}
