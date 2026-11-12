//! Read-only matching of complete posted files to PAR2 recovery sources.

mod execute;
mod identity;
mod layout;
mod metadata;

pub use execute::{Execution, execute};
pub use metadata::{VerifiedSource, refresh_metadata};

/// Discover verified PAR2 packets without depending on displayed NZB filenames.
/// Caller runs this on its admitted blocking worker; buffers count against memory.
pub async fn discover(
    client: &Client,
    nzb: &nzb::Nzb,
    limits: Limits,
    cancel: &CancellationToken,
) -> Result<(infinidysk_par2::Reader, Limits, u64), Error> {
    discover_with_context(client, nzb, limits, Context::default(), cancel).await
}

pub async fn discover_with_context(
    client: &Client,
    nzb: &nzb::Nzb,
    limits: Limits,
    context: Context,
    cancel: &CancellationToken,
) -> Result<(infinidysk_par2::Reader, Limits, u64), Error> {
    let mut budget = Budget {
        limits,
        scratch: 0,
        cancel: cancel.clone(),
        network_bytes: 0,
        source: context.source,
        candidates: context.candidates,
        probe: context.probe,
        progress: context.progress,
        legacy_ids: std::collections::BTreeSet::new(),
    };
    let reader = read_parity(client, nzb, &mut budget, None).await?;
    if reader.sets().is_empty() {
        return Err(Error::Unavailable("no verified PAR2 packets are available"));
    }
    Ok((reader, budget.limits, budget.network_bytes))
}

fn volume_stem(name: &str) -> Option<&str> {
    let (stem, range) = name.strip_suffix(".par2")?.rsplit_once(".vol")?;
    let (start, count) = range.split_once('+').or_else(|| range.split_once('-'))?;
    (start.parse::<u32>().is_ok() && count.parse::<u32>().is_ok()).then_some(stem)
}

async fn read_parity(
    client: &Client,
    nzb: &nzb::Nzb,
    budget: &mut Budget,
    recovery: Option<(Id, usize, usize)>,
) -> Result<infinidysk_par2::Reader, Error> {
    let mut reader = infinidysk_par2::Reader::default();
    let mut order = Vec::new();
    for file in &nzb.files {
        budget.comparison()?;
        let name = file.name().to_ascii_lowercase();
        budget.metadata(name.len() + 64)?;
        order.push((file, name));
    }
    order.sort_by_key(|(file, name)| (volume_stem(name).is_some(), file.segments.len()));
    let mut indexes = HashSet::new();
    for (file, name) in order {
        if recovery.is_none() && volume_stem(&name).is_some_and(|stem| indexes.contains(stem)) {
            continue;
        }
        let mut parity = name.contains(".par2");
        for (i, segment) in file.segments.iter().enumerate() {
            if i > 0 && !parity {
                break;
            }
            let body = match budget.body(client, &segment.id).await {
                Ok(body) => body,
                Err(Error::Source(
                    infinidysk_nntp::Error::NotFound
                    | infinidysk_nntp::Error::Yenc(_)
                    | infinidysk_nntp::Error::CrcMismatch { .. },
                )) => {
                    reader.end_volume();
                    continue;
                }
                Err(error) => return Err(error),
            };
            parity |= body.data.starts_with(b"PAR2\0PKT");
            if parity {
                // Vec capacity growth and scanner packet records have separate headroom.
                budget.metadata(
                    body.data
                        .len()
                        .checked_mul(3)
                        .ok_or(Error::Limit("PAR2 metadata"))?,
                )?;
                reader.push(&body.data);
                if let Some((set, size, count)) = recovery {
                    if reader.recovery_slices(set, size)?.len() >= count + 2 {
                        return Ok(reader);
                    }
                } else if reader.reached_recovery() && complete_metadata(&reader) {
                    break;
                }
            }
        }
        if recovery.is_none()
            && parity
            && volume_stem(&name).is_none()
            && let Some(stem) = name.strip_suffix(".par2")
            && complete_metadata(&reader)
        {
            indexes.insert(stem.to_owned());
        }
        reader.end_volume();
    }
    Ok(reader)
}

fn complete_metadata(reader: &infinidysk_par2::Reader) -> bool {
    let sets = reader.sets();
    !sets.is_empty() && sets.into_iter().all(|set| reader.repair_set(set).is_ok())
}

use std::collections::HashSet;
use std::ops::Range;
use std::sync::{Arc, LazyLock, Mutex, PoisonError};

use futures_util::{StreamExt as _, stream};
use infinidysk_nntp::{Body, Client, MAX_DECODED_LEN, Workload};
use infinidysk_par2::{File, Id, RepairSet, Verifier};
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

use crate::nzb;
use identity::Identity;
use layout::Slots;

static RESOLUTION: LazyLock<Arc<Semaphore>> = LazyLock::new(|| Arc::new(Semaphore::new(1)));
const PREFIX: usize = 16_384;

#[derive(Clone, Default)]
pub struct Context {
    pub source: Option<(infinidysk_core::BlobId, i64)>,
    /// Importer-only unverified candidates. Never install this lookup on the delivery client.
    pub candidates: Option<Arc<infinidysk_nntp::RepairLookup>>,
    pub probe: Option<infinidysk_core::trace::Probe>,
    pub progress: Option<Arc<Progress>>,
    /// Previously observed yEnc ranges and PAR2 identity, captured with the current source.
    pub metadata: Option<Arc<infinidysk_core::FileMeta>>,
}

/// Completed provider read requests and definitive misses, including cached misses and repeated reads.
#[derive(Default)]
pub struct Progress(Mutex<(u64, u64)>);

impl Progress {
    pub fn requests(&self) -> (u64, u64) {
        *self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn finish(&self, missing: bool) {
        let mut counts = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        counts.0 += 1;
        counts.1 += u64::from(missing);
    }
}

/// Per-call work bounds. Caller-owned NZB/PAR2 buffers and NNTP transport buffers are separate.
#[derive(Clone, Copy)]
pub struct Limits {
    pub requests: usize,
    /// Accepted decoded payload; uncertain failures reserve a full article allowance.
    /// Clean missing replies carry no payload. NNTP retries and wire overhead are separate.
    pub decoded_bytes: u64,
    /// Requested owned metadata, body, prefix and fallback slice storage; allocator
    /// overhead is separate from this allowance.
    pub memory_bytes: usize,
    pub comparisons: u64,
    /// Persistent output allowance, including a conservative descriptor/header reserve.
    pub retained_bytes: u64,
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("source resolution cancelled")]
    Cancelled,
    #[error("source resolution exceeds its {0} limit")]
    Limit(&'static str),
    #[error(
        "source resolution needs another {required} working-memory bytes; {available} bytes available"
    )]
    Memory { required: usize, available: usize },
    #[error("source resolution unavailable: {0}")]
    Unavailable(&'static str),
    #[error("source changed during repair: {0}")]
    Changed(&'static str),
    #[error("ambiguous source identity: {0}")]
    Ambiguous(&'static str),
    #[error("source provider read failed")]
    Source(#[source] infinidysk_nntp::Error),
    #[error("source resolution worker failed")]
    Worker(#[from] tokio::task::JoinError),
    #[error("source resolution allocation failed")]
    Allocation,
    #[error("PAR2 content verification failed: {0}")]
    Content(#[source] infinidysk_par2::Error),
}

impl From<infinidysk_par2::Error> for Error {
    fn from(error: infinidysk_par2::Error) -> Self {
        match error {
            infinidysk_par2::Error::Memory { required, limit } => Self::Memory {
                required,
                available: limit,
            },
            error => Self::Content(error),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Damage {
    Missing,
    Corrupt,
    Omitted,
}

#[derive(Debug)]
pub struct Article {
    /// None for an omitted part; its identity is the set/file ID plus this source range.
    pub id: Option<String>,
    pub range: Range<u64>,
    /// NNTP/yEnc observation only; PAR2 verification may find additional damage.
    pub damage: Option<Damage>,
}

#[derive(Debug)]
pub struct Layout {
    source: usize,
    file: usize,
    set_id: Id,
    file_id: Id,
    articles: Vec<Article>,
    verified: bool,
    checked: Vec<bool>,
}

impl Layout {
    pub const fn set_id(&self) -> Id {
        self.set_id
    }
    pub const fn file_id(&self) -> Id {
        self.file_id
    }
    pub const fn source(&self) -> usize {
        self.source
    }
    pub const fn file(&self) -> usize {
        self.file
    }
    pub fn articles(&self) -> &[Article] {
        &self.articles
    }

    /// Map a global repair input to local ranges in this layout's articles.
    pub fn spans(
        &self,
        set: &RepairSet<'_>,
        index: usize,
    ) -> Result<Vec<(usize, Range<usize>)>, Error> {
        if set.id() != self.set_id
            || set
                .sources()
                .get(self.source)
                .is_none_or(|source| source.file.id != self.file_id)
            || set
                .source(index)
                .is_none_or(|(source, _)| source != self.source)
        {
            return Err(Error::Unavailable(
                "slice does not belong to this protected source",
            ));
        }
        let (_, range) = set
            .source(index)
            .ok_or(Error::Unavailable("source slice"))?;
        let first = self
            .articles
            .partition_point(|article| article.range.end <= range.start);
        let mut spans = Vec::new();
        let mut covered = range.start;
        for (position, article) in self.articles.iter().enumerate().skip(first) {
            if article.range.start >= range.end {
                break;
            }
            let start = article.range.start.max(range.start);
            let end = article.range.end.min(range.end);
            if start != covered {
                return Err(Error::Unavailable("inconsistent source layout"));
            }
            spans.push((
                position,
                usize::try_from(start - article.range.start)
                    .map_err(|_| Error::Limit("article range"))?
                    ..usize::try_from(end - article.range.start)
                        .map_err(|_| Error::Limit("article range"))?,
            ));
            covered = end;
        }
        if covered != range.end {
            return Err(Error::Unavailable("inconsistent source layout"));
        }
        Ok(spans)
    }
}

/// Resolve every nonempty protected source, independently of displayed names or members.
/// This can read complete posted files. It retains metadata and bounded working buffers,
/// never publishes repair data or updates health. Ambiguous missing boundaries need
/// trusted stored ranges before repair can proceed.
pub async fn resolve(
    client: &Arc<Client>,
    nzb: &nzb::Nzb,
    set: &RepairSet<'_>,
    limits: Limits,
    cancel: &CancellationToken,
) -> Result<Vec<Layout>, Error> {
    resolve_budget(client, nzb, set, limits, Context::default(), cancel)
        .await
        .map(|(layouts, _)| layouts)
}

async fn resolve_budget(
    client: &Arc<Client>,
    nzb: &nzb::Nzb,
    set: &RepairSet<'_>,
    limits: Limits,
    context: Context,
    cancel: &CancellationToken,
) -> Result<(Vec<Layout>, Budget), Error> {
    let cancel = cancel.child_token();
    let _stop = cancel.clone().drop_guard();
    let permit = tokio::select! {
        biased;
        () = cancel.cancelled() => return Err(Error::Cancelled),
        permit = Arc::clone(&RESOLUTION).acquire_owned() =>
            permit.expect("source resolution semaphore is never closed"),
    };
    let (files, sources, mut budget) = snapshot(nzb, set, limits, cancel.clone())?;
    budget.candidates = context.candidates;
    budget.probe = context.probe;
    budget.progress = context.progress;
    budget.source = context.source;
    let metadata = context.metadata;
    let client = Arc::clone(client);
    let size = set.slice_size();
    let set_id = set.id();
    let runtime = tokio::runtime::Handle::current();
    let result = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let layouts = runtime.block_on(run(
            &client,
            files,
            &sources,
            size,
            set_id,
            metadata.as_deref(),
            &mut budget,
        ))?;
        Ok((layouts, budget))
    })
    .await?;
    if cancel.is_cancelled() {
        return Err(Error::Cancelled);
    }
    result
}

struct Budget {
    limits: Limits,
    scratch: usize,
    cancel: CancellationToken,
    network_bytes: u64,
    source: Option<(infinidysk_core::BlobId, i64)>,
    candidates: Option<Arc<infinidysk_nntp::RepairLookup>>,
    probe: Option<infinidysk_core::trace::Probe>,
    progress: Option<Arc<Progress>>,
    legacy_ids: std::collections::BTreeSet<String>,
}

impl Budget {
    fn admit_reads(&mut self, client: &Client, scratch: usize) -> Result<usize, Error> {
        // Snapshot already admits one maximum-sized body. Additional responses stay bounded.
        let width = (self.limits.memory_bytes.saturating_sub(scratch) / MAX_DECODED_LEN + 1)
            .min(client.free_permits().unwrap_or(16).max(1))
            .min(self.limits.requests.max(1))
            .min(
                usize::try_from(self.limits.decoded_bytes / MAX_DECODED_LEN as u64)
                    .unwrap_or(usize::MAX)
                    .max(1),
            );
        self.metadata((width - 1) * MAX_DECODED_LEN)?;
        Ok(width)
    }

    async fn bodies(
        &mut self,
        client: &Client,
        ids: &[&str],
    ) -> Result<Vec<Result<Body, Error>>, Error> {
        self.check()?;
        let mut output = Vec::new();
        let mut at = 0;
        while at < ids.len() {
            let width = (ids.len() - at).min(self.limits.requests.max(1)).min(
                usize::try_from(self.limits.decoded_bytes / MAX_DECODED_LEN as u64)
                    .unwrap_or(usize::MAX)
                    .max(1),
            );
            let chunk = &ids[at..at + width];
            let results = stream::iter(chunk.iter().map(|id| {
                let mut read = Self {
                    limits: Limits {
                        requests: self.limits.requests.min(1),
                        decoded_bytes: self.limits.decoded_bytes.min(MAX_DECODED_LEN as u64),
                        ..self.limits
                    },
                    scratch: 0,
                    cancel: self.cancel.clone(),
                    network_bytes: 0,
                    source: self.source,
                    candidates: self.candidates.clone(),
                    probe: self.probe.clone(),
                    progress: self.progress.clone(),
                    legacy_ids: std::collections::BTreeSet::new(),
                };
                async move {
                    let limits = read.limits;
                    let result = read.body(client, id).await;
                    (result, read, limits)
                }
            }))
            .buffered(chunk.len())
            .collect::<Vec<_>>()
            .await;
            for (result, read, limits) in results {
                self.limits.requests = self
                    .limits
                    .requests
                    .checked_sub(limits.requests - read.limits.requests)
                    .ok_or(Error::Limit("request"))?;
                self.limits.decoded_bytes = self
                    .limits
                    .decoded_bytes
                    .checked_sub(limits.decoded_bytes - read.limits.decoded_bytes)
                    .ok_or(Error::Limit("decoded byte"))?;
                self.network_bytes += read.network_bytes;
                for id in read.legacy_ids {
                    if !self.legacy_ids.contains(&id) {
                        self.metadata(id.len() + 128)?;
                        self.legacy_ids.insert(id);
                    }
                }
                output.push(result);
            }
            at += width;
        }
        Ok(output)
    }

    fn check(&self) -> Result<(), Error> {
        if self.cancel.is_cancelled() {
            Err(Error::Cancelled)
        } else {
            Ok(())
        }
    }

    fn metadata(&mut self, bytes: usize) -> Result<(), Error> {
        let available = self.limits.memory_bytes.saturating_sub(self.scratch);
        if bytes > available {
            return Err(Error::Memory {
                required: bytes,
                available,
            });
        }
        self.limits.memory_bytes -= bytes;
        Ok(())
    }

    fn scratch(&mut self, bytes: usize) -> Result<(), Error> {
        if bytes > self.limits.memory_bytes {
            return Err(Error::Memory {
                required: bytes,
                available: self.limits.memory_bytes,
            });
        }
        self.scratch = bytes;
        Ok(())
    }

    fn comparison(&mut self) -> Result<(), Error> {
        self.check()?;
        self.limits.comparisons = self
            .limits
            .comparisons
            .checked_sub(1)
            .ok_or(Error::Limit("comparison"))?;
        Ok(())
    }

    async fn body(&mut self, client: &Client, id: &str) -> Result<Body, Error> {
        self.check()?;
        let local = tokio::select! {
            biased;
            () = self.cancel.cancelled() => return Err(Error::Cancelled),
            result = client.repaired(id, None) => result.map_err(Error::Source)?,
        };
        if let Some(body) = local {
            return Ok(body);
        }
        let candidate = self
            .candidates
            .as_ref()
            .and_then(|lookup| lookup(infinidysk_nntp::RepairKey::Article(id)));
        if let Some(read) = candidate {
            let body = tokio::select! {
                biased;
                () = self.cancel.cancelled() => return Err(Error::Cancelled),
                result = read => result.map_err(Error::Source)?,
            };
            if let Some(body) = body {
                if body.data.is_empty() || body.data.len() > MAX_DECODED_LEN {
                    return Err(Error::Limit("candidate article"));
                }
                if !self.legacy_ids.contains(id) {
                    self.metadata(
                        id.len()
                            .checked_add(128)
                            .ok_or(Error::Limit("candidate identity"))?,
                    )?;
                    self.legacy_ids.insert(id.to_owned());
                }
                return Ok(body);
            }
        }
        self.limits.requests = self
            .limits
            .requests
            .checked_sub(1)
            .ok_or(Error::Limit("request"))?;
        if self.limits.decoded_bytes < MAX_DECODED_LEN as u64 {
            return Err(Error::Limit("decoded byte"));
        }
        let result = tokio::select! {
            biased;
            () = self.cancel.cancelled() => return Err(Error::Cancelled),
            result = client.provider_body_with(id, Workload::Health, self.probe.as_ref(), None, None) => result,
        };
        if let Some(progress) = &self.progress {
            progress.finish(matches!(result, Err(infinidysk_nntp::Error::NotFound)));
        }
        let bytes = match &result {
            Ok(body) => body.data.len(),
            Err(infinidysk_nntp::Error::NotFound) => 0,
            Err(_) => MAX_DECODED_LEN,
        };
        if bytes > MAX_DECODED_LEN {
            return Err(Error::Limit("article size"));
        }
        self.limits.decoded_bytes -= bytes as u64;
        if result.is_ok() {
            self.network_bytes += bytes as u64;
        }
        result.map_err(Error::Source)
    }
}

fn snapshot(
    nzb: &nzb::Nzb,
    set: &RepairSet<'_>,
    limits: Limits,
    cancel: CancellationToken,
) -> Result<(Vec<nzb::File>, Vec<File>, Budget), Error> {
    let mut budget = Budget {
        limits,
        scratch: 0,
        cancel,
        network_bytes: 0,
        source: None,
        candidates: None,
        probe: None,
        progress: None,
        legacy_ids: std::collections::BTreeSet::new(),
    };
    budget.metadata(MAX_DECODED_LEN + PREFIX)?;
    // Conservatively covers Vec/HashSet capacity and per-source working metadata.
    budget.metadata(
        set.sources()
            .len()
            .checked_mul(256)
            .ok_or(Error::Limit("metadata"))?,
    )?;
    let mut ids = HashSet::new();
    for file in &nzb.files {
        budget.check()?;
        budget.metadata(size_of::<nzb::File>() + file.subject.len())?;
        for segment in &file.segments {
            budget.metadata(
                256usize
                    .checked_add(segment.id.len())
                    .ok_or(Error::Limit("metadata"))?,
            )?;
            ids.try_reserve(1).map_err(|_| Error::Allocation)?;
            if !ids.insert(segment.id.as_str()) {
                return Err(Error::Ambiguous(
                    "article IDs must belong uniquely to one posted part",
                ));
            }
        }
    }
    for source in set.sources() {
        budget.metadata(source.file.name.len())?;
        budget.metadata(
            source
                .file
                .slices
                .len()
                .checked_mul(size_of::<infinidysk_par2::Slice>())
                .ok_or(Error::Limit("metadata"))?,
        )?;
    }
    let sources = set
        .sources()
        .iter()
        .map(|source| source.file.clone())
        .collect();
    Ok((nzb.files.clone(), sources, budget))
}

async fn run(
    client: &Client,
    files: Vec<nzb::File>,
    sources: &[File],
    size: usize,
    set_id: Id,
    metadata: Option<&infinidysk_core::FileMeta>,
    budget: &mut Budget,
) -> Result<Vec<Layout>, Error> {
    let mut resolved: Vec<Option<Layout>> = (0..sources.len()).map(|_| None).collect();
    let mut reason = "a protected source could not be identified; surviving checksums or trusted stored metadata are needed";
    for (file, candidate) in files.into_iter().enumerate() {
        budget.check()?;
        budget.scratch(0)?;
        let memory = budget.limits.memory_bytes;
        let result = observe(client, candidate, sources, size, set_id, metadata, budget).await;
        // Candidate buffers are gone; only successful output metadata remains live.
        budget.scratch(0)?;
        budget.limits.memory_bytes = memory;
        match result {
            Ok(Some((source, articles, verified, checked))) => {
                budget.metadata(checked.len())?;
                budget.metadata(
                    articles
                        .len()
                        .checked_mul(size_of::<Article>())
                        .ok_or(Error::Limit("metadata"))?,
                )?;
                if resolved[source].is_some() {
                    return Err(Error::Ambiguous(
                        "multiple posted files match one protected source",
                    ));
                }
                resolved[source] = Some(Layout {
                    source,
                    file,
                    set_id,
                    file_id: sources[source].id,
                    articles,
                    verified,
                    checked,
                });
            }
            Ok(None) => {}
            Err(Error::Unavailable(message)) => reason = message,
            Err(error) => return Err(error),
        }
    }
    if sources
        .iter()
        .zip(&resolved)
        .any(|(source, layout)| source.length > 0 && layout.is_none())
    {
        return Err(Error::Unavailable(reason));
    }
    Ok(resolved.into_iter().flatten().collect())
}

async fn observe(
    client: &Client,
    file: nzb::File,
    sources: &[File],
    size: usize,
    set_id: Id,
    metadata: Option<&infinidysk_core::FileMeta>,
    budget: &mut Budget,
) -> Result<Option<(usize, Vec<Article>, bool, Vec<bool>)>, Error> {
    let mut slots = Slots::new(file, budget)?;
    let stored_source = metadata::restore(metadata, &mut slots, sources, set_id, size, budget)?;
    let mut identity = Identity::new(sources, size, budget)?;
    let width = budget.admit_reads(client, size)?;
    budget.metadata(PREFIX)?;
    let mut prefix = Vec::new();
    let mut verifier = None;
    let mut intact = true;
    let mut offset = 0;
    let mut at = 0;
    while at < slots.len() {
        let end = (at + width).min(slots.len());
        let ids = (at..end).filter_map(|i| slots.id(i)).collect::<Vec<_>>();
        let results = budget.bodies(client, &ids).await?;
        let mut results = results.into_iter();
        for i in at..end {
            if slots.id(i).is_none() {
                intact = false;
                verifier = None;
                prefix.clear();
                continue;
            }
            match results.next().expect("one result per requested article") {
                Ok(body) => {
                    slots.observe(i, &body, budget)?;
                    if !sources.iter().any(|source| source.length == body.file_size) {
                        return Ok(None);
                    }
                    identity.feed(&body, budget)?;
                    if body.offset != offset {
                        intact = false;
                        verifier = None;
                        prefix.clear();
                    }
                    if intact {
                        if verifier.is_none()
                            && let Some(source) = identity.proven()
                        {
                            let file = &sources[source];
                            let mut check =
                                Verifier::new(file.length, file.hash, size as u64, &[])?;
                            if check
                                .push(&prefix, || budget.cancel.is_cancelled())
                                .is_err()
                            {
                                intact = false;
                            }
                            prefix.clear();
                            verifier = Some(check);
                        }
                        if let Some(check) = &mut verifier {
                            if check
                                .push(&body.data, || budget.cancel.is_cancelled())
                                .is_err()
                            {
                                intact = false;
                                verifier = None;
                            }
                        } else if prefix.len() + body.data.len() < PREFIX {
                            prefix.extend_from_slice(&body.data);
                        } else {
                            intact = false;
                            prefix.clear();
                        }
                    }
                    offset = body.offset + body.data.len() as u64;
                }
                Err(Error::Source(infinidysk_nntp::Error::NotFound)) => {
                    slots.damage(i, Damage::Missing);
                    intact = false;
                    verifier = None;
                    prefix.clear();
                }
                Err(Error::Source(
                    infinidysk_nntp::Error::Yenc(_)
                    | infinidysk_nntp::Error::CrcMismatch { .. }
                    | infinidysk_nntp::Error::TooLarge,
                )) => {
                    slots.damage(i, Damage::Corrupt);
                    intact = false;
                    verifier = None;
                    prefix.clear();
                }
                Err(error) => return Err(error),
            }
        }
        at = end;
    }
    let observed = identity.finish()?;
    if let (Some(stored), Some((observed, _))) = (stored_source, &observed)
        && stored != *observed
    {
        return Err(Error::Changed(
            "stored source identity disagrees with PAR2 observations",
        ));
    }
    let Some((source, checked)) =
        observed.or_else(|| stored_source.map(|i| (i, vec![false; sources[i].slices.len()])))
    else {
        return Ok(None);
    };
    let verified = verifier
        .is_some_and(|verifier| intact && verifier.finish(|| budget.cancel.is_cancelled()).is_ok());
    budget.check()?;
    Ok(Some((
        source,
        slots.finish(sources[source].length)?,
        verified && checked.iter().all(|checked| *checked),
        checked,
    )))
}
