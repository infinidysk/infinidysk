use infinidysk_core::{FileMeta, Par2Proof, Segment, Segments};
use infinidysk_par2::RepairSet;

use super::{Error, Layout};

/// Restore only ranges observed in yEnc headers. PAR2 identity must match fresh packets.
pub(super) fn restore(
    meta: Option<&FileMeta>,
    slots: &mut super::layout::Slots,
    sources: &[infinidysk_par2::File],
    set: infinidysk_par2::Id,
    slice_size: usize,
    budget: &mut super::Budget,
) -> Result<Option<usize>, Error> {
    let plain = match meta {
        Some(FileMeta::Nzb(file)) => Some((&file.segments, file.proof.as_ref(), 0)),
        _ => None,
    };
    let parts = match meta {
        Some(FileMeta::Multipart(file)) => file.parts.as_slice(),
        _ => &[],
    };
    let mut matched = None;
    for (segments, proof, offset) in plain.into_iter().chain(
        parts
            .iter()
            .map(|part| (&part.segments, part.proof.as_ref(), part.offset)),
    ) {
        if !segments.sizes_trusted {
            continue;
        }
        let Some((first, segment)) = segments
            .items
            .iter()
            .enumerate()
            .find(|(_, s)| !s.id.is_empty())
        else {
            continue;
        };
        let mut at = None;
        for i in 0..slots.len() {
            budget.comparison()?;
            if slots.id(i) == Some(segment.id.as_str()) {
                at = i.checked_sub(first);
                break;
            }
        }
        let Some(start) = at else {
            continue;
        };
        let Some(end) = start.checked_add(segments.items.len()) else {
            continue;
        };
        let mut identities_match = true;
        for (i, known) in (start..end).zip(&segments.items) {
            budget.comparison()?;
            identities_match &= slots.id(i).unwrap_or_default() == known.id;
        }
        if !identities_match {
            continue;
        }
        let mut candidate = None;
        if let Some(proof) = proof {
            for (i, source) in sources.iter().enumerate() {
                budget.comparison()?;
                if same_file(proof, source, set, slice_size) && candidate.replace(i).is_some() {
                    return Err(Error::Ambiguous("stored source identity is not unique"));
                }
            }
            if candidate.is_none() {
                continue;
            }
        }
        // Only validated identities may extend the NZB's trailing omitted slots.
        slots.grow(end, budget)?;
        let mut position = offset;
        for (i, known) in (start..end).zip(&segments.items) {
            if known.size == 0 || known.size as usize > infinidysk_nntp::MAX_DECODED_LEN {
                return Err(Error::Unavailable("stored article size is invalid"));
            }
            let next = position
                .checked_add(u64::from(known.size))
                .ok_or(Error::Unavailable("stored article range overflow"))?;
            if candidate.is_some_and(|source| next > sources[source].length) {
                return Err(Error::Unavailable(
                    "stored article range exceeds its protected source",
                ));
            }
            slots.remember(i, position..next)?;
            position = next;
        }
        if let Some(candidate) = candidate {
            if matched.is_some_and(|old| old != candidate) {
                return Err(Error::Ambiguous("stored source identities conflict"));
            }
            matched = Some(candidate);
        }
    }
    Ok(matched)
}

fn same_file(
    proof: &Par2Proof,
    source: &infinidysk_par2::File,
    set: infinidysk_par2::Id,
    size: usize,
) -> bool {
    proof.set_id.is_none_or(|old| old == set)
        && proof.file_id == source.id
        && proof.file_length == source.length
        && proof.file_md5 == source.hash
        && proof.md5_16k == source.hash_16k
        && usize::try_from(proof.slice_size).ok() == Some(size)
}

/// Complete posted-file metadata, emitted only after full slice and file verification.
pub struct VerifiedSource {
    pub proof: Par2Proof,
    pub segments: Segments,
}

impl VerifiedSource {
    /// Heap bytes `new` allocates for this layout: segment records, message IDs and the
    /// per-slice checksum vectors.
    pub(super) fn heap_bytes(set: &RepairSet<'_>, layout: &Layout) -> Option<usize> {
        let slices = set.sources()[layout.source()]
            .file
            .slices
            .len()
            .checked_mul(size_of::<[u8; 16]>() + size_of::<u32>())?;
        layout.articles().iter().try_fold(slices, |total, article| {
            total
                .checked_add(size_of::<Segment>())?
                .checked_add(article.id.as_ref().map_or(0, String::len))
        })
    }

    pub(super) fn new(set: &RepairSet<'_>, layout: &Layout) -> Result<Self, Error> {
        let file = &set.sources()[layout.source()].file;
        let proof = Par2Proof {
            set_id: Some(set.id()),
            file_length: file.length,
            slice_size: u32::try_from(set.slice_size())
                .map_err(|_| Error::Limit("stored slice size"))?,
            slice_md5: file.slices.iter().map(|slice| slice.md5).collect(),
            slice_crc32: file.slices.iter().map(|slice| slice.crc32).collect(),
            file_id: file.id,
            file_md5: file.hash,
            md5_16k: file.hash_16k,
        };
        let items = layout
            .articles()
            .iter()
            .map(|article| {
                Ok(Segment {
                    id: article.id.clone().unwrap_or_default(),
                    size: u32::try_from(article.range.end - article.range.start)
                        .map_err(|_| Error::Limit("stored article size"))?,
                    fallbacks: Vec::new(),
                })
            })
            .collect::<Result<_, Error>>()?;
        Ok(Self {
            proof,
            segments: Segments {
                items,
                sizes_trusted: true,
                missing: Vec::new(),
                corrupt: Vec::new(),
            },
        })
    }
}

/// Upgrade only exact posted identities. Preserve playable ranges, encryption and paths.
pub fn refresh_metadata(
    meta: &mut FileMeta,
    sources: &[VerifiedSource],
    covered: &mut Vec<bool>,
) -> bool {
    let old = meta.clone();
    let matched = match meta {
        FileMeta::Nzb(file) => {
            let end = file.segments.size();
            vec![refresh(
                &mut file.segments,
                &mut file.proof,
                0,
                end,
                sources,
                true,
            )]
        }
        FileMeta::Multipart(file) => file
            .parts
            .iter_mut()
            .map(|part| {
                let end = part.data.end;
                refresh(
                    &mut part.segments,
                    &mut part.proof,
                    part.offset,
                    end,
                    sources,
                    false,
                )
            })
            .collect(),
    };
    if meta.size() != old.size() {
        *meta = old;
        return false;
    }
    covered.resize(matched.len(), false);
    for (covered, matched) in covered.iter_mut().zip(matched) {
        *covered |= matched;
    }
    *meta != old
}

/// Takes the sizes of the one source that posted `segments` at `offset`, if they still
/// reach `end`. A `whole` file must be the entire source; a multipart part may be a
/// window of the volume its proof describes.
fn refresh(
    segments: &mut Segments,
    proof: &mut Option<Par2Proof>,
    offset: u64,
    end: u64,
    sources: &[VerifiedSource],
    whole: bool,
) -> bool {
    let matches = sources
        .iter()
        .filter_map(|source| {
            if whole && source.segments.items.len() != segments.items.len() {
                return None;
            }
            let mut at = 0;
            let start = source.segments.items.iter().position(|segment| {
                let found = at == offset;
                at += u64::from(segment.size);
                found
            })?;
            let end = start.checked_add(segments.items.len())?;
            let items = source.segments.items.get(start..end)?;
            // Empty message IDs alone cannot identify a source.
            (items.iter().any(|s| !s.id.is_empty())
                && items.iter().zip(&segments.items).all(|(a, b)| a.id == b.id)
                && proof.as_ref().is_none_or(|p| {
                    p.file_id == source.proof.file_id
                        && p.file_length == source.proof.file_length
                        // Committed omitted patches are keyed to the stored set.
                        && p.set_id.is_none_or(|set| source.proof.set_id == Some(set))
                }))
            .then_some((source, items))
        })
        .collect::<Vec<_>>();
    if let [(source, items)] = matches.as_slice() {
        let reach = items.iter().map(|s| u64::from(s.size)).sum::<u64>();
        if offset.saturating_add(reach) < end {
            return false;
        }
        for (current, verified) in segments.items.iter_mut().zip(*items) {
            current.size = verified.size;
        }
        segments.sizes_trusted = true;
        segments.missing.clear();
        segments.corrupt.clear();
        *proof = Some(source.proof.clone());
        return true;
    }
    false
}
