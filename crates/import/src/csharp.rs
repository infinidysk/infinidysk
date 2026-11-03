//! C# metadata blobs (`DavNzbFile`, `DavRarFile`, `DavMultipartFile`): zstd-compressed
//! `MemoryPack`, decoded into types shaped like the C# ones. Members are read by their
//! `MemoryPackOrder`; members a blob predates read as their C# default.

#![allow(
    clippy::redundant_closure_for_method_calls,
    reason = "Method pointers do not satisfy the streaming field callback's higher-ranked lifetimes."
)]

use std::io::{BufReader, Read};

use uuid::Uuid;

use crate::memorypack::{Error, Object, Reader};

/// `[start, end)` byte offsets.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct LongRange {
    pub start: i64,
    pub end: i64,
}

impl LongRange {
    pub const fn len(self) -> i64 {
        self.end - self.start
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Par2Proof {
    pub file_length: i64,
    pub slice_size: i32,
    pub slice_md5: Vec<u8>,
    pub slice_crc32: Vec<u32>,
    pub file_id: Vec<u8>,
    pub file_hash: Vec<u8>,
    pub file_16k_hash: Vec<u8>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct NzbFile {
    pub id: Uuid,
    pub segment_ids: Vec<String>,
    /// Per segment, absolute in the file; absent on early blobs.
    pub ranges: Option<Vec<LongRange>>,
    pub fallbacks: Option<Vec<Vec<String>>>,
    pub missing: Option<Vec<i32>>,
    pub container_class: Option<u8>,
    pub critical_head_end: Option<i64>,
    pub corrupt: Option<Vec<i32>>,
    pub ranges_trusted: Option<bool>,
    pub proof: Option<Par2Proof>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RarFile {
    pub id: Uuid,
    pub parts: Vec<RarPart>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RarPart {
    pub segment_ids: Vec<String>,
    pub part_size: i64,
    pub offset: i64,
    pub byte_count: i64,
    pub ranges: Option<Vec<LongRange>>,
    pub fallbacks: Option<Vec<Vec<String>>>,
    pub ranges_trusted: Option<bool>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct AesParams {
    pub decoded_size: i64,
    pub iv: Vec<u8>,
    pub key: Vec<u8>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct MultipartFile {
    pub id: Uuid,
    pub aes: Option<AesParams>,
    pub parts: Vec<FilePart>,
    pub is_lazy: bool,
    pub path_in_archive: Option<String>,
    pub password: Option<String>,
    /// Trailing RAR volumes C# resolves on first read.
    pub pending: Vec<PendingPart>,
    pub expected_size: Option<i64>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct FilePart {
    pub segment_ids: Vec<String>,
    /// The bytes the segments cover, as offsets in the volume.
    pub segment_range: LongRange,
    /// The member's bytes, as offsets in the volume.
    pub data_range: LongRange,
    pub ranges: Option<Vec<LongRange>>,
    pub fallbacks: Option<Vec<Vec<String>>>,
    pub split_after: Option<bool>,
    pub ranges_trusted: Option<bool>,
    pub proof: Option<Par2Proof>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PendingPart {
    pub segment_ids: Vec<String>,
    pub segment_range: LongRange,
    pub estimated_size: i64,
    pub fallbacks: Option<Vec<Vec<String>>>,
    pub proof: Option<Par2Proof>,
}

/// Decompresses blob file contents and reads the root object with `read`.
pub fn decode<T>(
    compressed: impl Read,
    read: impl FnOnce(&Object<'_>) -> Result<T, Error>,
) -> Result<T, DecodeError> {
    let decoder = zstd::stream::read::Decoder::new(compressed)?;
    let mut input = BufReader::new(decoder);
    let mut reader = Reader::new(&mut input);
    let value = object(&mut reader, read)?;
    reader.finish()?;
    Ok(value)
}

#[derive(Debug, thiserror::Error)]
pub enum DecodeError {
    #[error("zstd: {0}")]
    Zstd(#[from] std::io::Error),
    #[error("memorypack: {0}")]
    Payload(#[from] Error),
}

pub fn nzb_file(o: &Object<'_>) -> Result<NzbFile, Error> {
    Ok(NzbFile {
        id: o.field(0, |reader| reader.guid())?.unwrap_or_default(),
        segment_ids: o
            .field(1, |reader| reader.strings())?
            .flatten()
            .unwrap_or_default(),
        ranges: o.field(2, ranges)?.flatten(),
        fallbacks: o.field(3, fallbacks)?.flatten(),
        missing: o.field(4, |reader| reader.i32s())?.flatten(),
        container_class: o.field(5, |reader| reader.opt_u8())?.flatten(),
        critical_head_end: o.field(6, |reader| reader.opt_i64())?.flatten(),
        corrupt: o.field(7, |reader| reader.i32s())?.flatten(),
        ranges_trusted: o.field(8, |reader| reader.opt_bool())?.flatten(),
        proof: o.field(9, proof)?.flatten(),
    })
}

pub fn rar_file(o: &Object<'_>) -> Result<RarFile, Error> {
    Ok(RarFile {
        id: o.field(0, |reader| reader.guid())?.unwrap_or_default(),
        parts: o
            .field(1, |r| r.list(|r| object(r, rar_part)))?
            .flatten()
            .unwrap_or_default(),
    })
}

fn rar_part(o: &Object<'_>) -> Result<RarPart, Error> {
    Ok(RarPart {
        segment_ids: o
            .field(0, |reader| reader.strings())?
            .flatten()
            .unwrap_or_default(),
        part_size: o.field(1, |reader| reader.i64())?.unwrap_or_default(),
        offset: o.field(2, |reader| reader.i64())?.unwrap_or_default(),
        byte_count: o.field(3, |reader| reader.i64())?.unwrap_or_default(),
        ranges: o.field(4, ranges)?.flatten(),
        fallbacks: o.field(5, fallbacks)?.flatten(),
        ranges_trusted: o.field(6, |reader| reader.opt_bool())?.flatten(),
    })
}

pub fn multipart_file(o: &Object<'_>) -> Result<MultipartFile, Error> {
    let id = o.field(0, |reader| reader.guid())?.unwrap_or_default();
    let file = o
        .field(1, |r| {
            object(r, |m| {
                Ok(MultipartFile {
                    id,
                    aes: m.field(0, aes)?.flatten(),
                    parts: m
                        .field(1, |r| r.list(|r| object(r, file_part)))?
                        .flatten()
                        .unwrap_or_default(),
                    is_lazy: m.field(2, |reader| reader.bool())?.unwrap_or_default(),
                    path_in_archive: m.field(3, |reader| reader.string())?.flatten(),
                    password: m.field(4, |reader| reader.string())?.flatten(),
                    pending: m
                        .field(5, |r| r.list(|r| object(r, pending_part)))?
                        .flatten()
                        .unwrap_or_default(),
                    expected_size: m.field(6, |reader| reader.opt_i64())?.flatten(),
                })
            })
        })?
        .unwrap_or_else(|| MultipartFile {
            id,
            ..MultipartFile::default()
        });
    Ok(file)
}

fn file_part(o: &Object<'_>) -> Result<FilePart, Error> {
    Ok(FilePart {
        segment_ids: o
            .field(0, |reader| reader.strings())?
            .flatten()
            .unwrap_or_default(),
        segment_range: o.field(1, range)?.unwrap_or_default(),
        data_range: o.field(2, range)?.unwrap_or_default(),
        ranges: o.field(3, ranges)?.flatten(),
        fallbacks: o.field(4, fallbacks)?.flatten(),
        split_after: o.field(5, |reader| reader.opt_bool())?.flatten(),
        ranges_trusted: o.field(6, |reader| reader.opt_bool())?.flatten(),
        proof: o.field(7, proof)?.flatten(),
    })
}

fn pending_part(o: &Object<'_>) -> Result<PendingPart, Error> {
    Ok(PendingPart {
        segment_ids: o
            .field(0, |reader| reader.strings())?
            .flatten()
            .unwrap_or_default(),
        segment_range: o.field(1, range)?.unwrap_or_default(),
        estimated_size: o.field(2, |reader| reader.i64())?.unwrap_or_default(),
        fallbacks: o.field(3, fallbacks)?.flatten(),
        proof: o.field(4, proof)?.flatten(),
    })
}

/// A non-null object read with `read`.
fn object<T>(
    r: &mut Reader<'_>,
    read: impl FnOnce(&Object<'_>) -> Result<T, Error>,
) -> Result<T, Error> {
    let object = r.object()?.ok_or(Error::Invalid("null element"))?;
    let value = read(&object)?;
    object.finish()?;
    Ok(value)
}

fn range(r: &mut Reader<'_>) -> Result<LongRange, Error> {
    object(r, |o| {
        Ok(LongRange {
            start: o.field(0, |reader| reader.i64())?.unwrap_or_default(),
            end: o.field(1, |reader| reader.i64())?.unwrap_or_default(),
        })
    })
}

fn ranges(r: &mut Reader<'_>) -> Result<Option<Vec<LongRange>>, Error> {
    r.list(range)
}

/// `string[][]`; a null inner array means no fallbacks for that segment.
fn fallbacks(r: &mut Reader<'_>) -> Result<Option<Vec<Vec<String>>>, Error> {
    r.list(|r| Ok(r.strings()?.unwrap_or_default()))
}

fn aes(r: &mut Reader<'_>) -> Result<Option<AesParams>, Error> {
    let Some(o) = r.object()? else {
        return Ok(None);
    };
    let value = AesParams {
        decoded_size: o.field(0, |reader| reader.i64())?.unwrap_or_default(),
        iv: o
            .field(1, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
        key: o
            .field(2, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
    };
    o.finish()?;
    Ok(Some(value))
}

fn proof(r: &mut Reader<'_>) -> Result<Option<Par2Proof>, Error> {
    let Some(o) = r.object()? else {
        return Ok(None);
    };
    let value = Par2Proof {
        file_length: o.field(0, |reader| reader.i64())?.unwrap_or_default(),
        slice_size: o.field(1, |reader| reader.i32())?.unwrap_or_default(),
        slice_md5: o
            .field(2, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
        slice_crc32: o
            .field(3, |reader| reader.u32s())?
            .flatten()
            .unwrap_or_default(),
        file_id: o
            .field(4, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
        file_hash: o
            .field(5, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
        file_16k_hash: o
            .field(6, |reader| reader.bytes())?
            .flatten()
            .unwrap_or_default(),
    };
    o.finish()?;
    Ok(Some(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A version-tolerant object from its encoded members (all shorter than 120 bytes).
    fn obj(members: &[Vec<u8>]) -> Vec<u8> {
        let mut out = vec![u8::try_from(members.len()).unwrap()];
        out.extend(members.iter().map(|m| u8::try_from(m.len()).unwrap()));
        out.extend(members.iter().flatten());
        out
    }

    fn utf8(s: &str) -> Vec<u8> {
        let n = i32::try_from(s.len()).unwrap();
        [(!n).to_le_bytes(), n.to_le_bytes()]
            .concat()
            .into_iter()
            .chain(s.bytes())
            .collect()
    }

    fn list(items: &[Vec<u8>]) -> Vec<u8> {
        let mut out = i32::try_from(items.len()).unwrap().to_le_bytes().to_vec();
        out.extend(items.iter().flatten());
        out
    }

    #[test]
    fn decode_rejects_truncated_fields_and_trailing_bytes() {
        for bytes in [&[1, 16, 0][..], &[0, 0][..]] {
            let compressed = zstd::encode_all(bytes, 1).unwrap();
            assert!(matches!(
                decode(compressed.as_slice(), nzb_file),
                Err(DecodeError::Payload(_))
            ));
        }

        let mut remainder = [0; 16].as_slice();
        let error = Reader::new(&mut remainder).finish().unwrap_err();
        assert!(matches!(error, Error::Trailing));
        assert_eq!(error.to_string(), "trailing data");
        assert_eq!(remainder.len(), 15);
    }

    #[test]
    fn skips_large_unknown_fields_without_a_metadata_size_limit() {
        let length = 536_870_913_u32;
        let header = [vec![2, 0, 131], length.to_le_bytes().to_vec()].concat();
        let input = header
            .as_slice()
            .chain(std::io::repeat(0).take(u64::from(length)));
        let compressed = zstd::encode_all(input, 1).unwrap();
        let value = decode(compressed.as_slice(), |root| {
            Ok(root.field(0, |_| Ok(42))?.unwrap())
        })
        .unwrap();
        assert_eq!(value, 42);
        let truncated = zstd::encode_all(header.as_slice(), 1).unwrap();
        assert!(matches!(
            decode(truncated.as_slice(), |_| Ok(())),
            Err(DecodeError::Payload(Error::Truncated))
        ));
    }

    #[test]
    fn collection_lengths_do_not_allocate_before_reading_elements() {
        let payload = obj(&[vec![], i32::MAX.to_le_bytes().to_vec()]);
        let compressed = zstd::encode_all(payload.as_slice(), 1).unwrap();
        assert!(matches!(
            decode(compressed.as_slice(), |root| root
                .field(1, |reader| reader.strings())),
            Err(DecodeError::Payload(Error::Truncated))
        ));
    }

    #[test]
    fn nested_optional_objects_skip_new_fields() {
        let payload = obj(&[
            obj(&[0_i64.to_le_bytes().to_vec(), list(&[]), list(&[]), vec![7]]),
            obj(&[
                0_i64.to_le_bytes().to_vec(),
                0_i32.to_le_bytes().to_vec(),
                list(&[]),
                list(&[]),
                list(&[]),
                list(&[]),
                list(&[]),
                vec![8],
            ]),
            vec![1],
        ]);
        let compressed = zstd::encode_all(payload.as_slice(), 1).unwrap();
        decode(compressed.as_slice(), |root| {
            assert!(root.field(0, aes)?.flatten().is_some());
            assert!(root.field(1, proof)?.flatten().is_some());
            assert_eq!(root.field(2, |reader| reader.bool())?, Some(true));
            Ok(())
        })
        .unwrap();
    }

    #[test]
    fn reads_a_version_tolerant_nzb_file() {
        let id = Uuid::now_v7();
        let range = |s: i64, e: i64| obj(&[s.to_le_bytes().to_vec(), e.to_le_bytes().to_vec()]);
        let mut head_end = vec![1, 0, 0, 0, 0, 0, 0, 0];
        head_end.extend(4096_i64.to_le_bytes());
        let blob = obj(&[
            id.to_bytes_le().to_vec(),
            list(&[utf8("a@b"), utf8("c@d")]),
            list(&[range(0, 700), range(700, 900)]),
            list(&[list(&[utf8("e@f")]), (-1_i32).to_le_bytes().to_vec()]),
            (-1_i32).to_le_bytes().to_vec(),
            vec![0, 0],
            head_end,
        ]);
        let compressed = zstd::encode_all(blob.as_slice(), 1).unwrap();
        let file = decode(compressed.as_slice(), nzb_file).unwrap();
        assert_eq!(
            file,
            NzbFile {
                id,
                segment_ids: vec!["a@b".into(), "c@d".into()],
                ranges: Some(vec![
                    LongRange { start: 0, end: 700 },
                    LongRange {
                        start: 700,
                        end: 900
                    }
                ]),
                fallbacks: Some(vec![vec!["e@f".into()], vec![]]),
                critical_head_end: Some(4096),
                ..NzbFile::default()
            },
            "members after the seventh default"
        );
    }
}
