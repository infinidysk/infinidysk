//! Complete recovery membership and source ranges, independent of archive member views.

use std::collections::HashMap;
use std::ops::Range;

use crate::repair::{filled, reserved};
use crate::{
    Error, FILE_DESC, File, HEADER, IFSC, Id, Index, MAIN, Packet, RECOVERY, Reader, Recovery,
    UNICODE_NAME,
};
use crate::{Input, build};

#[derive(Debug)]
pub struct Source {
    pub file: File,
    /// Global input slice indices, in Main-packet order.
    pub slices: Range<usize>,
}

/// Immutable, complete metadata for the recoverable files of one set.
/// Recovery packets remain borrowed; absent parity does not prevent source planning.
pub struct RepairSet<'a> {
    id: Id,
    slice_size: usize,
    sources: Vec<Source>,
    inputs: Vec<Input>,
    recovery: Vec<Recovery<'a>>,
}

impl RepairSet<'_> {
    pub const fn id(&self) -> Id {
        self.id
    }
    pub const fn slice_size(&self) -> usize {
        self.slice_size
    }

    pub fn sources(&self) -> &[Source] {
        &self.sources
    }

    pub fn inputs(&self) -> &[Input] {
        &self.inputs
    }

    pub fn recovery(&self) -> &[Recovery<'_>] {
        &self.recovery
    }

    /// The original source file and unpadded file range of a global slice.
    pub fn source(&self, index: usize) -> Option<(usize, Range<u64>)> {
        let position = self.sources.partition_point(|s| s.slices.end <= index);
        let source = self.sources.get(position)?;
        if !source.slices.contains(&index) {
            return None;
        }
        let start = (index - source.slices.start) as u64 * self.slice_size as u64;
        let length = self.inputs.get(index)?.length as u64;
        Some((position, start..start + length))
    }

    /// Intersections with decoded articles, addressed by position (including omitted IDs).
    /// `lengths` must describe the whole protected file, not an extracted member's view.
    /// The caller must match source identity and validate decoded article lengths first.
    pub fn articles(
        &self,
        index: usize,
        lengths: &[u32],
    ) -> Result<Vec<(usize, Range<usize>)>, Error> {
        let (source, range) = self.source(index).ok_or(Error::Invalid("slice index"))?;
        let mut parts = reserved(0)?;
        let mut offset = 0u64;
        for (article, &length) in lengths.iter().enumerate() {
            let end = offset
                .checked_add(u64::from(length))
                .ok_or(Error::Invalid("article layout overflow"))?;
            let start = range.start.max(offset);
            let stop = range.end.min(end);
            if start < stop {
                let begin = usize::try_from(start - offset)
                    .map_err(|_| Error::Invalid("article offset"))?;
                let finish =
                    usize::try_from(stop - offset).map_err(|_| Error::Invalid("article offset"))?;
                parts.try_reserve(1).map_err(|_| Error::Allocation)?;
                parts.push((article, begin..finish));
            }
            offset = end;
        }
        if offset != self.sources[source].file.length {
            return Err(Error::Invalid(
                "article layout must cover the whole source file",
            ));
        }
        Ok(parts)
    }
}

fn metadata(reader: &Reader, set: Id, verify: bool) -> Result<(usize, Index), Error> {
    let packets = || {
        reader
            .scan
            .found
            .iter()
            .filter(|p| p.set == set)
            .map(|p| Packet {
                set,
                kind: &p.kind,
                body: &reader.buf[p.body.clone()],
            })
    };
    let main = packets()
        .find(|p| p.kind == MAIN)
        .ok_or(Error::Invalid("Main packet is missing"))?;
    let (slice_size, positions) = membership(main.body)?;
    let count = positions.len();
    let mut descriptions = filled(count, None)?;
    let mut checksums = filled(count, None)?;
    let mut relevant = reserved(1)?;
    let main_body = main.body;
    relevant.push(main);
    for packet in packets() {
        if packet.kind == MAIN {
            if packet.body != main_body {
                return Err(Error::Invalid("conflicting Main packets"));
            }
            continue;
        }
        if packet.kind != FILE_DESC && packet.kind != IFSC && packet.kind != UNICODE_NAME {
            continue;
        }
        let Some(id) = packet.body.get(..16).and_then(|id| <Id>::try_from(id).ok()) else {
            continue;
        };
        let Some(&i) = positions.get(&id) else {
            continue;
        };
        let slot = if packet.kind == FILE_DESC {
            &mut descriptions[i]
        } else if packet.kind == IFSC {
            &mut checksums[i]
        } else {
            relevant.try_reserve(1).map_err(|_| Error::Allocation)?;
            relevant.push(packet);
            continue;
        };
        if let Some(previous) = slot {
            if *previous != packet.body {
                return Err(Error::Invalid(
                    "conflicting source descriptions or checksums",
                ));
            }
            continue;
        }
        *slot = Some(packet.body);
        relevant.try_reserve(1).map_err(|_| Error::Allocation)?;
        relevant.push(packet);
    }
    let mut total = 0;
    for (description, checksum) in descriptions.iter().zip(&checksums) {
        let Some(description) = description.filter(|b: &&[u8]| b.len() >= 56) else {
            return Err(Error::Invalid("source description is missing"));
        };
        let length =
            u64::from_le_bytes(description[48..56].try_into().expect("checked descriptor"));
        let count = usize::try_from(length.div_ceil(slice_size as u64))
            .map_err(|_| Error::Invalid("source slice count"))?;
        if count > 32_768 - total {
            return Err(Error::Invalid("total input count exceeds 32768"));
        }
        total += count;
        if checksum.map_or(count != 0 && !verify, |b: &[u8]| b.len() != 16 + count * 20) {
            return Err(Error::Invalid(
                "source slice checksums are missing or incomplete",
            ));
        }
    }
    let mut index = Index::default();
    build(&mut index, &relevant);
    Ok((slice_size, index))
}

fn membership(body: &[u8]) -> Result<(usize, HashMap<Id, usize>), Error> {
    if body.len() < 12 || !(body.len() - 12).is_multiple_of(16) {
        return Err(Error::Invalid("Main packet layout"));
    }
    let size = u64::from_le_bytes(body[..8].try_into().expect("checked Main header"));
    let slice_size = usize::try_from(size).map_err(|_| Error::Invalid("slice size"))?;
    let count = u32::from_le_bytes(body[8..12].try_into().expect("checked Main header"));
    let count = usize::try_from(count).map_err(|_| Error::Invalid("source count"))?;
    let ids = body[12..].as_chunks::<16>().0;
    if size == 0 || !size.is_multiple_of(4) || count > ids.len() || count > 32_768 {
        return Err(Error::Invalid("slice size or recovery membership"));
    }
    let mut positions = HashMap::new();
    positions
        .try_reserve(count)
        .map_err(|_| Error::Allocation)?;
    for (i, &id) in ids[..count].iter().enumerate() {
        if positions.insert(id, i).is_some() {
            return Err(Error::Invalid("duplicate recoverable file identity"));
        }
    }
    if ids[count..].iter().any(|id| positions.contains_key(id)) {
        return Err(Error::Invalid(
            "file belongs to both recovery and non-recovery sets",
        ));
    }
    Ok((slice_size, positions))
}

pub(super) fn read(reader: &Reader, set: Id, verify: bool) -> Result<RepairSet<'_>, Error> {
    let (slice_size, index) = metadata(reader, set, verify)?;
    let size = slice_size as u64;
    let mut sources = reserved(index.files.len())?;
    let mut inputs = reserved(0)?;
    for file in index.files {
        inputs
            .try_reserve(file.slices.len())
            .map_err(|_| Error::Allocation)?;
        let start = inputs.len();
        for (i, &checksum) in file.slices.iter().enumerate() {
            let length = usize::try_from((file.length - i as u64 * size).min(size))
                .expect("slice length is bounded by usize slice_size");
            inputs.push(Input { length, checksum });
        }
        sources.push(Source {
            file,
            slices: start..inputs.len(),
        });
    }
    Ok(RepairSet {
        id: set,
        slice_size,
        sources,
        inputs,
        recovery: if verify {
            Vec::new()
        } else {
            recovery(reader, set, slice_size)?
        },
    })
}

pub(super) fn recovery(
    reader: &Reader,
    set: Id,
    slice_size: usize,
) -> Result<Vec<Recovery<'_>>, Error> {
    let mut recovery: Vec<Recovery<'_>> = reserved(0)?;
    let mut exponents: HashMap<u32, usize> = HashMap::new();
    for packet in reader
        .scan
        .found
        .iter()
        .filter(|p| p.set == set && &p.kind == RECOVERY)
    {
        let row = Recovery::read(
            &reader.buf[packet.body.start - HEADER..packet.body.end],
            set,
            slice_size,
        )?;
        let exponent = row.exponent % 65_535;
        if let Some(&i) = exponents.get(&exponent) {
            if recovery[i].data != row.data {
                return Err(Error::Invalid("conflicting recovery slices"));
            }
            continue;
        }
        recovery.try_reserve(1).map_err(|_| Error::Allocation)?;
        exponents.try_reserve(1).map_err(|_| Error::Allocation)?;
        exponents.insert(exponent, recovery.len());
        recovery.push(row);
    }
    Ok(recovery)
}
