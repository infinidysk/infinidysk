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
                } else if reader.reached_recovery() && !reader.index().files.is_empty() {
                    break;
                }
            }
        }
        if recovery.is_none()
            && parity
            && volume_stem(&name).is_none()
            && let Some(stem) = name.strip_suffix(".par2")
            && reader
                .sets()
                .into_iter()
                .any(|set| reader.verification_set(set).is_ok())
        {
            indexes.insert(stem.to_owned());
        }
        reader.end_volume();
    }
    Ok(reader)
}

use std::collections::HashSet;
use std::ops::Range;
use std::sync::{Arc, LazyLock};

use infinidysk_nntp::{Body, Client, MAX_DECODED_LEN, Workload};
use infinidysk_par2::{File, Id, RepairSet};
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
}

/// Per-call work bounds. Caller-owned NZB/PAR2 buffers and NNTP transport buffers are separate.
#[derive(Clone, Copy)]
pub struct Limits {
    pub requests: usize,
    /// Accepted decoded payload; failures reserve a full article allowance. NNTP retries
    /// and encoded wire overhead are accounted by the shared client separately.
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
    Content(#[from] infinidysk_par2::Error),
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
        let mut lengths = Vec::new();
        lengths
            .try_reserve_exact(self.articles.len())
            .map_err(|_| Error::Allocation)?;
        for article in &self.articles {
            lengths.push(
                u32::try_from(article.range.end - article.range.start)
                    .expect("validated articles fit MAX_DECODED_LEN"),
            );
        }
        set.articles(index, &lengths)
            .map_err(|_| Error::Unavailable("inconsistent source layout"))
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
    budget.source = context.source;
    let client = Arc::clone(client);
    let size = set.slice_size();
    let set_id = set.id();
    let runtime = tokio::runtime::Handle::current();
    let result = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let layouts = runtime.block_on(run(&client, files, &sources, size, set_id, &mut budget))?;
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
    legacy_ids: std::collections::BTreeSet<String>,
}

impl Budget {
    fn check(&self) -> Result<(), Error> {
        if self.cancel.is_cancelled() {
            Err(Error::Cancelled)
        } else {
            Ok(())
        }
    }

    fn metadata(&mut self, bytes: usize) -> Result<(), Error> {
        if bytes > self.limits.memory_bytes.saturating_sub(self.scratch) {
            return Err(Error::Limit("memory"));
        }
        self.limits.memory_bytes -= bytes;
        Ok(())
    }

    fn scratch(&mut self, bytes: usize) -> Result<(), Error> {
        if bytes > self.limits.memory_bytes {
            return Err(Error::Limit("memory"));
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
        let bytes = result
            .as_ref()
            .map_or(MAX_DECODED_LEN, |body| body.data.len());
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
    budget: &mut Budget,
) -> Result<Vec<Layout>, Error> {
    let mut resolved: Vec<Option<Layout>> = (0..sources.len()).map(|_| None).collect();
    let mut reason = "no matching complete source was found";
    for (file, candidate) in files.into_iter().enumerate() {
        budget.check()?;
        budget.scratch(0)?;
        let memory = budget.limits.memory_bytes;
        let result = observe(client, candidate, sources, size, budget).await;
        // Candidate buffers are gone; only successful output metadata remains live.
        budget.scratch(0)?;
        budget.limits.memory_bytes = memory;
        match result {
            Ok(Some((source, articles))) => {
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
    budget: &mut Budget,
) -> Result<Option<(usize, Vec<Article>)>, Error> {
    let mut slots = Slots::new(file, budget)?;
    let mut identity = Identity::new(sources, size);
    for i in 0..slots.len() {
        let Some(id) = slots.id(i) else {
            continue;
        };
        match budget.body(client, id).await {
            Ok(body) => {
                slots.observe(i, &body, budget)?;
                if !sources.iter().any(|source| source.length == body.file_size) {
                    return Ok(None);
                }
                identity.feed(&body, budget)?;
            }
            Err(Error::Source(infinidysk_nntp::Error::NotFound)) => {
                slots.damage(i, Damage::Missing);
            }
            Err(Error::Source(
                infinidysk_nntp::Error::Yenc(_)
                | infinidysk_nntp::Error::CrcMismatch { .. }
                | infinidysk_nntp::Error::TooLarge,
            )) => slots.damage(i, Damage::Corrupt),
            Err(error) => return Err(error),
        }
    }
    let Some(source) = identity.finish()? else {
        return Ok(None);
    };
    Ok(Some((source, slots.finish(sources[source].length)?)))
}
