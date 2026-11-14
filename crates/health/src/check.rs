//! One check of one file: STAT a sample, BODY a few segments, and on a confirmed miss
//! census every segment and classify the damage.

use std::collections::{BTreeMap, BTreeSet};
use std::hash::BuildHasher as _;

use bytes::Bytes;
use futures_util::{StreamExt as _, stream};
use infinidysk_core::media::{self, Exceeded};
use infinidysk_core::{FileMeta, Segment, Segments};
use infinidysk_nntp::{ArticleFuture, Articles, Client, Error as NntpError, Workload};

const MP4: &[&str] = &["mp4", "m4v", "mov"];
/// Random sampled segments decoded besides the first.
const RANDOM_BODIES: usize = 2;

pub enum Outcome {
    Healthy,
    /// Damage within the caps; the message describes it.
    Degraded(String),
    Missing(String),
    Inconclusive(NntpError),
}

pub struct Options {
    /// Share of segments probed, 1 to 100.
    pub percent: u8,
    pub tolerant: bool,
    pub concurrency: usize,
    /// Every segment answered STAT moments ago: only the BODY reads remain, and damage
    /// needs no census.
    pub proven: bool,
}

/// A routine check's own requests, outside any import's budget.
pub struct Direct<'a>(pub &'a Client);

impl Articles for Direct<'_> {
    fn stat<'a>(&'a self, id: &'a str) -> ArticleFuture<'a, ()> {
        Box::pin(self.0.stat(id, Workload::Health, None))
    }

    fn body<'a>(&'a self, id: &'a str, begin: Option<u64>) -> ArticleFuture<'a, Bytes> {
        Box::pin(async move {
            let body = self
                .0
                .body_with(id, Workload::Health, None, None, begin)
                .await?;
            Ok(body.data)
        })
    }
}

#[derive(Debug)]
enum Probe {
    Present,
    Missing,
    Corrupt,
    Inconclusive(NntpError),
}

impl Probe {
    const fn damaged(&self) -> bool {
        matches!(self, Self::Missing | Self::Corrupt)
    }
}

/// Checks `meta` (of file `name`) and updates its missing/corrupt sets; `true` with the
/// outcome when the sets changed and the blob needs rewriting, and the decoded first
/// segment when it was read.
pub async fn run(
    articles: &dyn Articles,
    meta: &mut FileMeta,
    name: &str,
    opts: &Options,
) -> (Outcome, bool, Option<Vec<u8>>) {
    let (segments, old_missing, old_corrupt) = flatten(meta);
    let n = segments.len();
    let sample = media::sample(n, opts.percent);
    // Known holes are re-probed with BODY so a recovery is noticed.
    let mut bodies: BTreeSet<usize> = old_missing.union(&old_corrupt).copied().collect();
    if n > 0 {
        bodies.insert(0);
        for _ in 0..RANDOM_BODIES {
            let pick = random_below(sample.len() as u64);
            bodies.insert(sample[usize::try_from(pick).unwrap_or(0)]);
        }
    }
    let stats: Vec<usize> = sample
        .into_iter()
        .filter(|i| !opts.proven && !bodies.contains(i))
        .collect();
    let jobs: Vec<_> = bodies
        .iter()
        .map(|&i| (i, true))
        .chain(stats.into_iter().map(|i| (i, false)))
        .collect();
    let (mut results, head) = probe(articles, &segments, jobs, opts.concurrency).await;

    if !opts.proven && results.values().any(Probe::damaged) {
        let rest: Vec<_> = (0..n)
            .filter(|i| !results.contains_key(i))
            .map(|i| (i, false))
            .collect();
        results.extend(probe(articles, &segments, rest, opts.concurrency).await.0);
    }
    // Holes whose re-probe proved nothing stay recorded.
    let keep = |old: &BTreeSet<usize>, hit: fn(&Probe) -> bool| -> BTreeSet<usize> {
        results
            .iter()
            .filter(|(i, p)| hit(p) || (old.contains(i) && matches!(p, Probe::Inconclusive(_))))
            .map(|(&i, _)| i)
            .collect()
    };
    let missing = keep(&old_missing, |p| matches!(p, Probe::Missing));
    let corrupt = keep(&old_corrupt, |p| matches!(p, Probe::Corrupt));
    let changed = missing != old_missing || corrupt != old_corrupt;
    let damaged: Vec<usize> = results
        .iter()
        .filter(|(_, p)| p.damaged())
        .map(|(&i, _)| i)
        .collect();
    let inconclusive = results.into_values().find_map(|p| match p {
        Probe::Inconclusive(e) => Some(e),
        _ => None,
    });
    if changed {
        store(meta, &missing, &corrupt);
    }
    let outcome = if !damaged.is_empty() {
        match classify(meta, name, opts.tolerant, &damaged, head.as_deref()) {
            Ok(message) => Outcome::Degraded(message),
            Err(message) => Outcome::Missing(message),
        }
    } else if let Some(error) = inconclusive {
        Outcome::Inconclusive(error)
    } else {
        Outcome::Healthy
    };
    (outcome, changed, head)
}

/// Adds flat `missing`/`corrupt` indices to `meta`'s sets; `true` when they changed.
pub fn mark(meta: &mut FileMeta, missing: &[usize], corrupt: &[usize]) -> bool {
    let (_, mut all_missing, mut all_corrupt) = flatten(meta);
    let before = all_missing.len() + all_corrupt.len();
    all_missing.extend(missing);
    all_corrupt.extend(corrupt);
    let changed = all_missing.len() + all_corrupt.len() != before;
    if changed {
        store(meta, &all_missing, &all_corrupt);
    }
    changed
}

/// A segment with its expected `=ypart` offset when sizes are trusted.
type Placed<'a> = (&'a Segment, Option<u64>);

/// Every segment in file order, with the recorded missing and corrupt flat indices.
fn flatten(meta: &FileMeta) -> (Vec<Placed<'_>>, BTreeSet<usize>, BTreeSet<usize>) {
    let starts: Vec<u64> = match meta {
        FileMeta::Nzb(_) => vec![0],
        FileMeta::Multipart(f) => f.parts.iter().map(|p| p.offset).collect(),
    };
    let (mut all, mut missing, mut corrupt) = (Vec::new(), BTreeSet::new(), BTreeSet::new());
    for (part, start) in parts(meta).into_iter().zip(starts) {
        let base = all.len();
        let flat = |i: &u32| base + usize::try_from(*i).unwrap_or(usize::MAX);
        missing.extend(part.missing.iter().map(flat));
        corrupt.extend(part.corrupt.iter().map(flat));
        all.extend(part.items.iter().scan(start, |at, s| {
            let begin = part.sizes_trusted.then_some(*at);
            *at += u64::from(s.size);
            Some((s, begin))
        }));
    }
    (all, missing, corrupt)
}

fn parts(meta: &FileMeta) -> Vec<&Segments> {
    match meta {
        FileMeta::Nzb(f) => vec![&f.segments],
        FileMeta::Multipart(f) => f.parts.iter().map(|p| &p.segments).collect(),
    }
}

/// Writes flat `missing`/`corrupt` indices back into each part's own sets.
fn store(meta: &mut FileMeta, missing: &BTreeSet<usize>, corrupt: &BTreeSet<usize>) {
    let parts: Vec<&mut Segments> = match meta {
        FileMeta::Nzb(f) => vec![&mut f.segments],
        FileMeta::Multipart(f) => f.parts.iter_mut().map(|p| &mut p.segments).collect(),
    };
    let mut base = 0;
    for part in parts {
        let end = base + part.items.len();
        let local = |set: &BTreeSet<usize>| -> Vec<u32> {
            set.range(base..end)
                .filter_map(|i| u32::try_from(i - base).ok())
                .collect()
        };
        part.missing = local(missing);
        part.corrupt = local(corrupt);
        base = end;
    }
}

/// Runs `(index, body?)` probes; returns each result and the decoded first segment.
async fn probe(
    articles: &dyn Articles,
    segments: &[Placed<'_>],
    jobs: Vec<(usize, bool)>,
    concurrency: usize,
) -> (BTreeMap<usize, Probe>, Option<Vec<u8>>) {
    let mut runs = stream::iter(jobs)
        .map(|(i, body)| async move {
            let (segment, begin) = segments[i];
            if body {
                let (probe, data) = fetch(articles, segment, begin).await;
                (i, probe, data.filter(|_| i == 0))
            } else {
                (i, stat(articles, segment).await, None)
            }
        })
        .buffer_unordered(concurrency.max(1));
    let (mut results, mut head) = (BTreeMap::new(), None);
    while let Some((i, probe, data)) = runs.next().await {
        head = head.or(data);
        results.insert(i, probe);
    }
    (results, head)
}

async fn stat(articles: &dyn Articles, segment: &Segment) -> Probe {
    match articles.stat(&segment.id).await {
        Ok(()) => Probe::Present,
        Err(NntpError::NotFound) => Probe::Missing,
        Err(e) => Probe::Inconclusive(e),
    }
}

/// BODY-reads `segment`; with `begin` (trusted sizes) it must be the part posted there,
/// as playback requires.
async fn fetch(
    articles: &dyn Articles,
    segment: &Segment,
    begin: Option<u64>,
) -> (Probe, Option<Vec<u8>>) {
    let probe = match articles.body(&segment.id, begin).await {
        Ok(data) if begin.is_some() && data.len() != segment.size as usize => Probe::Corrupt,
        Ok(data) => return (Probe::Present, Some(data.into())),
        Err(NntpError::NotFound) => Probe::Missing,
        Err(NntpError::CrcMismatch { .. } | NntpError::Yenc(_) | NntpError::TooLarge) => {
            Probe::Corrupt
        }
        Err(e) => Probe::Inconclusive(e),
    };
    (probe, None)
}

/// Whether confirmed damage at the sorted flat indices `damaged` is playable: `Ok` with a
/// description if so (`degraded`), `Err` with the reason it is not (`missing`).
fn classify(
    meta: &FileMeta,
    name: &str,
    tolerant: bool,
    damaged: &[usize],
    head: Option<&[u8]>,
) -> Result<String, String> {
    let total: usize = parts(meta).iter().map(|p| p.items.len()).sum();
    let summary = format!("{} of {total} segments missing or corrupt", damaged.len());
    let FileMeta::Nzb(file) = meta else {
        return Err(format!("{summary} in a file stored inside archive volumes"));
    };
    if !tolerant {
        return Err(summary);
    }
    if !media::is_gap_tolerant(name) {
        return Err(format!("{summary}; this container cannot play past gaps"));
    }
    let items = &file.segments.items;
    let sizes: Vec<u64> = items.iter().map(|s| u64::from(s.size)).collect();
    match media::within_caps(damaged, &sizes) {
        Ok(()) => {}
        Err(Exceeded::First) => return Err(format!("{summary}, including the first")),
        Err(Exceeded::Run(run)) => return Err(format!("{summary}, {run} in a row")),
        Err(Exceeded::Count) => return Err(summary),
        Err(Exceeded::Bytes) => return Err(format!("{summary}, more than 2 % of the file")),
    }
    let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
    let mut starts = Vec::with_capacity(items.len());
    let mut size = 0u64;
    for s in &sizes {
        starts.push(size);
        size += s;
    }
    let span = |i: usize| (starts[i], starts[i] + sizes[i]);
    if MP4.iter().any(|e| ext.eq_ignore_ascii_case(e)) {
        let Some((start, end)) = head.and_then(|h| mp4_index(h, size)) else {
            return Err(format!("{summary}; the MP4 index could not be located"));
        };
        if damaged
            .iter()
            .any(|&i| span(i).0 < end && span(i).1 > start)
        {
            return Err(format!("{summary}, including the MP4 index"));
        }
    }
    Ok(summary)
}

/// The byte range of an MP4's index (`moov`), from the file's first bytes `head`: the
/// `moov` box itself, or everything after `mdat` when the index trails the media.
fn mp4_index(head: &[u8], len: u64) -> Option<(u64, u64)> {
    let mut pos = 0u64;
    loop {
        let at = usize::try_from(pos).ok()?;
        let header = head.get(at..at + 8)?;
        let kind = &header[4..8];
        let size = match u32::from_be_bytes(header[..4].try_into().ok()?) {
            0 => len.checked_sub(pos)?,
            1 => u64::from_be_bytes(head.get(at + 8..at + 16)?.try_into().ok()?),
            s => u64::from(s),
        };
        if size < 8 {
            return None;
        }
        match kind {
            b"moov" => return Some((pos, pos + size)),
            b"mdat" => return Some(((pos + size).min(len), len)),
            _ => pos += size,
        }
    }
}

/// A random value in `0..bound`, for sampling and jitter.
pub fn random_below(bound: u64) -> u64 {
    std::hash::RandomState::new().hash_one(()) % bound.max(1)
}

#[cfg(test)]
mod tests {
    use infinidysk_core::NzbFile;

    use super::*;

    fn file(n: usize) -> FileMeta {
        let items = (0..n)
            .map(|i| Segment {
                id: i.to_string(),
                size: 1000,
                fallbacks: Vec::new(),
            })
            .collect();
        FileMeta::Nzb(NzbFile {
            segments: Segments {
                items,
                ..Segments::default()
            },
            proof: None,
        })
    }

    #[test]
    fn classifies_damage_against_the_caps() {
        let f = file(1000);
        assert!(classify(&f, "a.mkv", true, &[5, 6, 7, 8], None).is_ok());
        assert!(
            classify(&f, "a.mkv", true, &[5, 6, 7, 8, 9], None).is_err(),
            "run of 5"
        );
        assert!(
            classify(&f, "a.mkv", true, &[0], None).is_err(),
            "first segment"
        );
        assert!(
            classify(&f, "a.avi", true, &[5], None).is_err(),
            "intolerant container"
        );
        assert!(
            classify(&f, "a.mkv", false, &[5], None).is_err(),
            "tolerance off"
        );
        let spread: Vec<usize> = (1..=21).map(|i| i * 40).collect();
        assert!(
            classify(&f, "a.mkv", true, &spread, None).is_err(),
            "over 2 %"
        );
        // moov box at bytes 32..2032 covers segments 0..=2.
        let mut head = vec![0, 0, 0, 32, b'f', b't', b'y', b'p'];
        head.resize(32, 0);
        head.extend_from_slice(&[0, 0, 0x07, 0xd0, b'm', b'o', b'o', b'v']);
        assert!(
            classify(&f, "a.mp4", true, &[2], Some(&head)).is_err(),
            "moov hit"
        );
        assert!(classify(&f, "a.mp4", true, &[3], Some(&head)).is_ok());
        assert!(
            classify(&f, "a.mp4", true, &[3], None).is_err(),
            "layout unknown"
        );
    }

    #[test]
    fn stores_flat_holes_per_part() {
        let mut meta = file(10);
        store(&mut meta, &BTreeSet::from([2, 4]), &BTreeSet::from([7]));
        let (_, missing, corrupt) = flatten(&meta);
        assert_eq!(
            (missing, corrupt),
            (BTreeSet::from([2, 4]), BTreeSet::from([7]))
        );
    }
}
