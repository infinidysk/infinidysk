//! Bounded streaming passes over complete protected sources; never uses playback filler.

use std::collections::BTreeSet;
use std::sync::{Arc, LazyLock};

use infinidysk_core::{
    ByteRange, PatchId,
    repair::{Patch, Source},
};
use infinidysk_nntp::{Body, Client, Error as ReadError};
use infinidysk_par2::{Id, Reader, Repair, RepairSet, Verifier};
use sha2::{Digest as _, Sha256};
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

use super::{Budget, Context, Error, Layout, Limits, VerifiedSource, read_parity, resolve_budget};
use crate::nzb;

static WORKER: LazyLock<Arc<Semaphore>> = LazyLock::new(|| Arc::new(Semaphore::new(1)));

pub struct Execution {
    pub patches: Vec<(Patch, Vec<u8>)>,
    pub verified_bytes: u64,
    pub transferred_bytes: u64,
    pub remaining: Limits,
    pub sources: Vec<VerifiedSource>,
}

/// Caller admits its NZB/packet buffers separately. Output and solver workspace share
/// the memory allowance. Dropping the future stops its worker.
#[allow(clippy::too_many_arguments)] // Explicit operation, admission and source context are independent inputs.
pub async fn execute(
    client: &Arc<Client>,
    nzb: Arc<nzb::Nzb>,
    reader: Arc<Reader>,
    set: Id,
    limits: Limits,
    verify_only: bool,
    context: Context,
    cancel: &CancellationToken,
) -> Result<Execution, Error> {
    let cancel = cancel.child_token();
    let _stop = cancel.clone().drop_guard();
    let permit = tokio::select! {
        biased;
        () = cancel.cancelled() => return Err(Error::Cancelled),
        permit = Arc::clone(&WORKER).acquire_owned() => permit.expect("worker admission never closes"),
    };
    let client = Arc::clone(client);
    let runtime = tokio::runtime::Handle::current();
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        runtime.block_on(run(
            &client,
            &nzb,
            &reader,
            set,
            limits,
            verify_only,
            context,
            &cancel,
        ))
    })
    .await?
}

#[allow(clippy::too_many_arguments)] // Mirrors admitted execution inputs.
async fn run(
    client: &Arc<Client>,
    nzb: &nzb::Nzb,
    reader: &Reader,
    id: Id,
    limits: Limits,
    verify_only: bool,
    context: Context,
    cancel: &CancellationToken,
) -> Result<Execution, Error> {
    let set = if verify_only {
        reader.verification_set(id)?
    } else {
        reader.repair_set(id)?
    };
    let (layouts, mut budget) = resolve_budget(client, nzb, &set, limits, context, cancel).await?;
    budget.cancel = cancel.clone();
    let cached_article = layouts
        .iter()
        .flat_map(super::Layout::articles)
        .map(|article| article.range.end - article.range.start)
        .max()
        .unwrap_or(0);
    budget
        .metadata(usize::try_from(cached_article).map_err(|_| Error::Limit("cached article"))?)?;
    // Slice input, concatenation and the largest returned NNTP body can coexist.
    budget.metadata(
        set.slice_size()
            .checked_mul(3)
            .ok_or(Error::Limit("slice workspace"))?,
    )?;
    budget.metadata(
        set.inputs()
            .len()
            .checked_mul(32)
            .ok_or(Error::Limit("slice metadata"))?,
    )?;
    let mut missing = Vec::new();
    let mut damaged = BTreeSet::new();
    let mut cached = None;
    for index in 0..set.inputs().len() {
        match slice(client, &set, &layouts, index, &mut budget, &mut cached).await? {
            Some(bytes)
                if set.inputs()[index]
                    .checksum
                    .matches(set.slice_size() as u64, &bytes) => {}
            _ => {
                missing.push(index);
                let (source, _) = set
                    .source(index)
                    .ok_or(Error::Unavailable("source slice"))?;
                let layout = layouts
                    .iter()
                    .find(|l| l.source() == source)
                    .ok_or(Error::Unavailable("source layout"))?;
                let spans = layout.spans(&set, index)?;
                let known = spans
                    .iter()
                    .any(|(article, _)| layout.articles()[*article].damage.is_some());
                for (article, _) in spans {
                    if !known || layout.articles()[article].damage.is_some() {
                        damaged.insert((source, article));
                    }
                }
            }
        }
    }
    if verify_only && !missing.is_empty() {
        return Err(Error::Unavailable(
            "complete source verification found damaged slices",
        ));
    }
    // Legacy bytes become authoritative only after the final complete-file hash pass.
    for layout in &layouts {
        for (position, article) in layout.articles().iter().enumerate() {
            if article
                .id
                .as_ref()
                .is_some_and(|id| budget.legacy_ids.contains(id))
            {
                damaged.insert((layout.source(), position));
            }
        }
    }
    if damaged.len() > infinidysk_db::repair::MAX_PATCHES {
        return Err(Error::Limit("replacement article count"));
    }
    let mut patches = Vec::new();
    for &(source, article) in &damaged {
        let layout = layouts
            .iter()
            .find(|l| l.source() == source)
            .ok_or(Error::Unavailable("source layout"))?;
        let part = &layout.articles()[article];
        let length = usize::try_from(part.range.end - part.range.start)
            .map_err(|_| Error::Limit("article"))?;
        budget.limits.retained_bytes = budget
            .limits
            .retained_bytes
            .checked_sub(length as u64 + infinidysk_core::repair::MAX_HEADER_BYTES as u64)
            .ok_or(Error::Limit("retained byte"))?;
        budget.metadata(
            length
                .checked_add(1024)
                .ok_or(Error::Limit("output memory"))?,
        )?;
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(length)
            .map_err(|_| Error::Allocation)?;
        let total =
            u32::try_from(layout.articles().len()).map_err(|_| Error::Limit("part count"))?;
        patches.push((
            Patch {
                id: PatchId::new(),
                source: Source {
                    set: id,
                    file: layout.file_id(),
                    range: ByteRange {
                        start: part.range.start,
                        end: part.range.end,
                    },
                    file_length: set.sources()[source].file.length,
                    message_id: part.id.clone(),
                    part: Some(
                        u32::try_from(article + 1).map_err(|_| Error::Limit("part number"))?,
                    ),
                    total: Some(total),
                },
                sha256: [0; 32],
            },
            bytes,
        ));
    }
    let mut recovered = Vec::new();
    if !missing.is_empty() {
        let supplemental;
        let rows;
        let recovery = if set.recovery().len() < missing.len() {
            supplemental = read_parity(
                client,
                nzb,
                &mut budget,
                Some((id, set.slice_size(), missing.len())),
            )
            .await?;
            rows = supplemental.recovery_slices(id, set.slice_size())?;
            rows.as_slice()
        } else {
            set.recovery()
        };
        let mut solver = Repair::new(
            set.slice_size(),
            set.inputs(),
            &missing,
            recovery,
            budget.limits.memory_bytes,
            || cancel.is_cancelled(),
        )?;
        for index in 0..set.inputs().len() {
            if missing.binary_search(&index).is_ok() {
                continue;
            }
            let bytes = slice(client, &set, &layouts, index, &mut budget, &mut cached)
                .await?
                .ok_or(Error::Changed("another article became unavailable"))?;
            solver
                .feed(index, &bytes, || cancel.is_cancelled())
                .map_err(|error| {
                    if matches!(error, infinidysk_par2::Error::Slice { .. }) {
                        Error::Changed("a previously verified slice no longer matches")
                    } else {
                        error.into()
                    }
                })?;
        }
        recovered = solver.finish(|| cancel.is_cancelled())?;
    }
    let mut verified_bytes = 0;
    for source in set.sources() {
        let file = &source.file;
        let mut verifier = Verifier::new(
            file.length,
            file.hash,
            set.slice_size() as u64,
            &file.slices,
        )?;
        if source.slices.is_empty() && file.length > 0 {
            let layout = layouts
                .iter()
                .find(|layout| layout.file_id() == file.id)
                .ok_or(Error::Unavailable("source layout"))?;
            for position in 0..layout.articles().len() {
                let body = fetch_article(client, &set, layout, position, &mut budget)
                    .await?
                    .ok_or(Error::Unavailable(
                        "complete source verification could not read an article",
                    ))?;
                verifier.push(&body.data, || cancel.is_cancelled())?;
                for (patch, output) in &mut patches {
                    if patch.source.file == file.id && patch.source.range.start == body.offset {
                        output.extend_from_slice(&body.data);
                    }
                }
            }
        }
        for index in source.slices.clone() {
            budget.check()?;
            let fresh;
            let bytes = if let Some(repaired) = recovered.iter().find(|r| r.index == index) {
                &repaired.data
            } else {
                fresh = slice(client, &set, &layouts, index, &mut budget, &mut cached)
                    .await?
                    .ok_or(Error::Changed(
                        "an article disappeared during final verification",
                    ))?;
                &fresh
            };
            verifier.push(bytes, || cancel.is_cancelled())?;
            let (_, range) = set
                .source(index)
                .ok_or(Error::Unavailable("source slice"))?;
            for (patch, output) in &mut patches {
                if patch.source.file != file.id {
                    continue;
                }
                let start = range.start.max(patch.source.range.start);
                let end = range.end.min(patch.source.range.end);
                if start < end {
                    output.extend_from_slice(
                        &bytes[usize::try_from(start - range.start)
                            .map_err(|_| Error::Limit("slice offset"))?
                            ..usize::try_from(end - range.start)
                                .map_err(|_| Error::Limit("slice offset"))?],
                    );
                }
            }
        }
        verified_bytes += verifier.finish(|| cancel.is_cancelled())?.bytes;
    }
    for (patch, bytes) in &mut patches {
        if bytes.len() as u64 != patch.source.range.len() {
            return Err(Error::Unavailable("incomplete replacement article"));
        }
        patch.sha256 = Sha256::digest(bytes).into();
    }
    budget.check()?;
    budget.metadata(
        set.inputs()
            .len()
            .checked_mul(24)
            .ok_or(Error::Limit("proof output"))?,
    )?;
    let sources = layouts
        .iter()
        .map(|layout| VerifiedSource::new(&set, layout))
        .collect::<Result<Vec<_>, _>>()?;
    budget.limits.memory_bytes = limits.memory_bytes;
    Ok(Execution {
        patches,
        verified_bytes,
        transferred_bytes: budget.network_bytes,
        remaining: budget.limits,
        sources,
    })
}

async fn slice(
    client: &Client,
    set: &RepairSet<'_>,
    layouts: &[Layout],
    index: usize,
    budget: &mut Budget,
    cached: &mut Option<(usize, usize, Body)>,
) -> Result<Option<Vec<u8>>, Error> {
    let (source, _) = set
        .source(index)
        .ok_or(Error::Unavailable("source slice"))?;
    let layout = layouts
        .iter()
        .find(|l| l.source() == source)
        .ok_or(Error::Unavailable("source layout"))?;
    let mut data = Vec::new();
    data.try_reserve_exact(set.inputs()[index].length)
        .map_err(|_| Error::Allocation)?;
    for (position, range) in layout.spans(set, index)? {
        if cached
            .as_ref()
            .is_none_or(|(file, part, _)| *file != source || *part != position)
        {
            let Some(body) = fetch_article(client, set, layout, position, budget).await? else {
                return Ok(None);
            };
            *cached = Some((source, position, body));
        }
        let body = &cached.as_ref().expect("body loaded above").2;
        data.extend_from_slice(&body.data[range]);
    }
    Ok(Some(data))
}

async fn fetch_article(
    client: &Client,
    set: &RepairSet<'_>,
    layout: &Layout,
    position: usize,
    budget: &mut Budget,
) -> Result<Option<Body>, Error> {
    let article = &layout.articles()[position];
    let file_length = set.sources()[layout.source()].file.length;
    let body = if let Some(id) = &article.id {
        match budget.body(client, id).await {
            Ok(body) => Some(body),
            Err(Error::Source(
                ReadError::NotFound
                | ReadError::Yenc(_)
                | ReadError::CrcMismatch { .. }
                | ReadError::TooLarge,
            )) => None,
            Err(error) => return Err(error),
        }
    } else if let Some((nzb, generation)) = budget.source {
        let key = infinidysk_core::repair::Omitted {
            nzb,
            generation,
            set: set.id(),
            file: layout.file_id(),
            range: ByteRange {
                start: article.range.start,
                end: article.range.end,
            },
            file_length,
        };
        client.repaired_omitted(&key).await.map_err(Error::Source)?
    } else {
        None
    };
    if let Some(body) = &body
        && (body.offset != article.range.start
            || body.data.len() as u64 != article.range.end - article.range.start
            || body.file_size != file_length)
    {
        return Err(Error::Changed("article layout changed"));
    }
    Ok(body)
}
