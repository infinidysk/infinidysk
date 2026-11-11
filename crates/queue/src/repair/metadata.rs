use infinidysk_core::{FileMeta, Par2Proof, Segment, Segments};
use infinidysk_par2::RepairSet;

use super::{Error, Layout};

/// Complete posted-file metadata, emitted only after full slice and file verification.
pub struct VerifiedSource {
    pub proof: Par2Proof,
    pub segments: Segments,
}

impl VerifiedSource {
    pub fn memory_bytes(&self) -> usize {
        size_of::<Self>()
            + self.proof.slice_md5.capacity() * 16
            + self.proof.slice_crc32.capacity() * 4
            + self.segments.items.capacity() * size_of::<Segment>()
            + self
                .segments
                .items
                .iter()
                .map(|segment| {
                    segment.id.capacity()
                        + segment.fallbacks.capacity() * size_of::<String>()
                        + segment
                            .fallbacks
                            .iter()
                            .map(String::capacity)
                            .sum::<usize>()
                })
                .sum::<usize>()
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
            vec![refresh(&mut file.segments, &mut file.proof, 0, sources)]
        }
        FileMeta::Multipart(file) => file
            .parts
            .iter_mut()
            .map(|part| refresh(&mut part.segments, &mut part.proof, part.offset, sources))
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

fn refresh(
    segments: &mut Segments,
    proof: &mut Option<Par2Proof>,
    offset: u64,
    sources: &[VerifiedSource],
) -> bool {
    let matches = sources
        .iter()
        .filter_map(|source| {
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
                    p.file_id == source.proof.file_id && p.file_length == source.proof.file_length
                }))
            .then_some((source, items))
        })
        .collect::<Vec<_>>();
    if let [(source, items)] = matches.as_slice() {
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
