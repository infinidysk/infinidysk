//! Replace the entire reader, including decrypted bytes, at a publication boundary.

use std::collections::BTreeSet;
use std::future::Future;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::Arc;
use std::sync::PoisonError;
use std::task::{Context, Poll, ready};

use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};

use infinidysk_core::{BlobId, FileMeta, blob, media, trace::Trace, wait::Watch};
use infinidysk_db::{Db, Item};
use infinidysk_nntp::Client;
use infinidysk_stream::{Access, Budget, Cache, Gaps, Report};

use crate::{Error, FaultReport, GapEntries, ReadVersion, Reader};

type Loading = Pin<Box<dyn Future<Output = io::Result<Reader>> + Send>>;
pub(crate) type Reload = Arc<dyn Fn(ReadVersion) -> Loading + Send + Sync>;
type Changed = Pin<Box<dyn Future<Output = ()> + Send>>;

/// The existing open options, reused when an item revision changes.
pub(crate) struct Opened {
    pub db: Db,
    pub item: Item,
    pub client: Arc<Client>,
    pub budget: Budget,
    pub cache: Option<Arc<Cache>>,
    pub read_ahead: u64,
    pub trace: Option<Arc<Trace>>,
    pub report: Option<FaultReport>,
    pub gaps: Option<(GapEntries, bool)>,
    pub watch: Option<Arc<Watch>>,
}

impl Opened {
    fn build(
        &self,
        meta: &FileMeta,
        source: Option<(BlobId, i64)>,
        version: &ReadVersion,
    ) -> Result<Reader, Error> {
        let report = self.report.as_ref().map(|report| {
            let (report, id) = (Arc::clone(report), self.item.id);
            let version = version.clone();
            Arc::new(move |index, fault| {
                if version.is_current() {
                    report(id, version.clone(), index, fault);
                }
            }) as Report
        });
        let access: Access = {
            let version = version.clone();
            Arc::new(move |id| {
                let version = version.clone();
                Box::pin(async move { version.access(id).await })
            })
        };
        let reader = Reader::new(
            meta,
            source,
            &self.client,
            &self.budget,
            self.cache.as_ref(),
            self.read_ahead,
            self.trace.as_ref(),
            report.as_ref(),
        )?
        .with_access(access);
        let reader = if let Some((entries, tolerance)) = &self.gaps {
            let gaps = gaps(entries, &self.item, meta, version, *tolerance);
            reader.with_filler(meta, &self.item.name).with_gaps(gaps)
        } else {
            reader.into_strict()
        };
        Ok(match &self.watch {
            Some(watch) => reader.with_watch(Arc::clone(watch)),
            None => reader,
        })
    }

    pub(crate) fn reader(
        self,
        meta: &FileMeta,
        source: Option<(BlobId, i64)>,
        version: ReadVersion,
    ) -> Result<Reader, Error> {
        let reader = self.build(meta, source, &version)?;
        let size = meta.size();
        let opened = Arc::new(self);
        let reload: Reload = Arc::new(move |version| {
            let opened = Arc::clone(&opened);
            Box::pin(async move {
                let result = async {
                    loop {
                        let item = opened
                            .db
                            .item(opened.item.id)
                            .await?
                            .ok_or(Error::Corrupt("open file was removed"))?;
                        if item.size != opened.item.size
                            || item.kind != opened.item.kind
                            || item.nzb_blob != opened.item.nzb_blob
                        {
                            return Err(Error::Corrupt("open file identity changed"));
                        }
                        let metadata = item
                            .meta_blob
                            .ok_or(Error::Corrupt("file has no metadata"))?;
                        let (fresh_source, raw) = if let Some(nzb) = item.nzb_blob {
                            let (generation, raw) = opened
                                .db
                                .repair_metadata(item.id, metadata, nzb)
                                .await?
                                .ok_or(Error::Corrupt(
                                    "repair source changed while refreshing file",
                                ))?;
                            if source.is_some_and(|expected| expected != (nzb, generation)) {
                                return Err(Error::Corrupt("open file source changed"));
                            }
                            (Some((nzb, generation)), raw)
                        } else {
                            (
                                None,
                                opened
                                    .db
                                    .blob(metadata)
                                    .await?
                                    .ok_or(Error::Corrupt("metadata blob is missing"))?,
                            )
                        };
                        let meta = Arc::new(blob::decode_meta(&raw)?);
                        if meta.size() != size {
                            return Err(Error::Corrupt("open file size changed"));
                        }
                        if !version.track(item.nzb_blob, &meta).await {
                            continue;
                        }
                        return opened.build(&meta, fresh_source, &version);
                    }
                }
                .await;
                result.map_err(io::Error::other)
            })
        });
        Ok(Reader::Live(Box::new(LiveReader::new(
            reader, version, size, reload,
        ))))
    }
}

fn gaps(
    entries: &GapEntries,
    item: &Item,
    meta: &FileMeta,
    version: &ReadVersion,
    tolerance: bool,
) -> Arc<Gaps> {
    let mut all = entries.lock().unwrap_or_else(PoisonError::into_inner);
    if !version.is_current() {
        return Arc::default();
    }
    all.retain(|_, (_, gaps)| Arc::strong_count(gaps) > 1 || !gaps.stale());
    let (saved, gaps) = all
        .entry(item.id)
        .or_insert_with(|| (version.clone(), Arc::default()));
    if !saved.same(version) {
        // Detached CRC checks may still write to the previous revision's holes.
        *saved = version.clone();
        *gaps = Arc::default();
    }
    let gaps = Arc::clone(gaps);
    drop(all);
    let parts = match meta {
        FileMeta::Nzb(file) => vec![&file.segments],
        FileMeta::Multipart(file) => file.parts.iter().map(|part| &part.segments).collect(),
    };
    let (mut known, mut base) = (BTreeSet::new(), 0);
    for part in parts {
        let flat = |index: &u32| base + usize::try_from(*index).unwrap_or(usize::MAX);
        known.extend(part.missing.iter().chain(&part.corrupt).map(flat));
        base += part.items.len();
    }
    let budget =
        tolerance && matches!(meta, FileMeta::Nzb(_)) && media::is_gap_tolerant(&item.name);
    gaps.refresh(known, budget);
    gaps
}

/// An open file keeps its cursor while replacing all buffered source data.
pub struct LiveReader {
    inner: Option<Reader>,
    version: ReadVersion,
    changed: Changed,
    reload: Reload,
    loading: Option<Loading>,
    pos: u64,
    size: u64,
    positioned: bool,
    end: Option<u64>,
}

impl LiveReader {
    pub(crate) fn new(reader: Reader, version: ReadVersion, size: u64, reload: Reload) -> Self {
        Self {
            inner: Some(reader),
            changed: Box::pin(version.clone().changed()),
            version,
            reload,
            loading: None,
            pos: 0,
            size,
            positioned: true,
            end: None,
        }
    }

    pub(crate) fn set_end(&mut self, end: u64) {
        self.end = Some(end);
        if let Some(reader) = &mut self.inner {
            reader.set_end(end);
        }
    }

    fn reader(&mut self) -> io::Result<Pin<&mut Reader>> {
        self.inner
            .as_mut()
            .map(Pin::new)
            .ok_or_else(|| io::Error::other("file reader is being refreshed"))
    }

    fn poll_ready(&mut self, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if self.version.blocked() {
            return Poll::Ready(Err(io::Error::other(
                "repair publication needs reconciliation",
            )));
        }
        // Register even when the underlying fetch is pending, so retirement wakes it.
        let _ = self.changed.as_mut().poll(cx);
        if !self.version.is_current() {
            self.inner = None;
            self.loading = None;
            self.version = self.version.latest();
            self.changed = Box::pin(self.version.clone().changed());
            let _ = self.changed.as_mut().poll(cx);
        }
        if self.inner.is_none() {
            let loading = self
                .loading
                .get_or_insert_with(|| (self.reload)(self.version.clone()));
            let result = ready!(loading.as_mut().poll(cx));
            self.loading = None;
            let mut reader = result?;
            if !self.version.is_current() {
                cx.waker().wake_by_ref();
                return Poll::Pending;
            }
            if let Some(end) = self.end {
                reader.set_end(end);
            }
            self.inner = Some(reader);
            self.positioned = false;
        }
        if !self.positioned {
            let pos = self.pos;
            self.reader()?.start_seek(SeekFrom::Start(pos))?;
            self.positioned = true;
        }
        ready!(self.reader()?.poll_complete(cx))?;
        Poll::Ready(Ok(()))
    }
}

impl AsyncRead for LiveReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let this = self.get_mut();
        ready!(this.poll_ready(cx))?;
        let before = buf.filled().len();
        let result = this.reader()?.poll_read(cx, buf);
        this.pos += (buf.filled().len() - before) as u64;
        result
    }
}

impl AsyncSeek for LiveReader {
    fn start_seek(self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let this = self.get_mut();
        let pos = match position {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::Current(by) => this.pos.checked_add_signed(by),
            SeekFrom::End(by) => this.size.checked_add_signed(by),
        }
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "invalid file seek"))?;
        this.pos = pos;
        this.positioned = false;
        Ok(())
    }

    fn poll_complete(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        let this = self.get_mut();
        ready!(this.poll_ready(cx))?;
        Poll::Ready(Ok(this.pos))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fence::Fences;
    use infinidysk_core::ItemId;
    use std::io::Cursor;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::time::Duration;
    use tokio::io::{AsyncReadExt, AsyncSeekExt};
    use tokio::sync::Notify;

    #[tokio::test]
    async fn another_retirement_cancels_a_pending_reload_without_losing_the_cursor() {
        let fence = Fences::default().get(ItemId::new());
        let calls = Arc::new(AtomicUsize::new(0));
        let started = Arc::new(Notify::new());
        let reload: Reload = Arc::new({
            let calls = Arc::clone(&calls);
            let started = Arc::clone(&started);
            move |_| {
                let first = calls.fetch_add(1, Ordering::Relaxed) == 0;
                let started = Arc::clone(&started);
                Box::pin(async move {
                    if first {
                        started.notify_one();
                        std::future::pending::<()>().await;
                    }
                    Ok(Reader::Memory(Cursor::new(vec![3; 20])))
                })
            }
        });
        let mut reader = LiveReader::new(
            Reader::Memory(Cursor::new(vec![1; 20])),
            fence.version(),
            20,
            reload,
        );
        reader.seek(SeekFrom::End(-9)).await.unwrap();
        let mut first = [0; 2];
        reader.read_exact(&mut first).await.unwrap();
        assert_eq!(first, [1; 2]);
        fence.write().await.retire().unwrap();
        let reading = tokio::spawn(async move {
            let mut bytes = [0; 3];
            reader.read_exact(&mut bytes).await.unwrap();
            (reader, bytes)
        });
        tokio::time::timeout(Duration::from_secs(1), started.notified())
            .await
            .unwrap();
        fence.write().await.retire().unwrap();
        let (mut reader, bytes) = tokio::time::timeout(Duration::from_secs(1), reading)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(bytes, [3; 3]);
        assert_eq!(reader.stream_position().await.unwrap(), 16);
        assert_eq!(calls.load(Ordering::Relaxed), 2);
        assert!(reader.seek(SeekFrom::Current(-17)).await.is_err());
    }
}
