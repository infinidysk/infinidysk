//! C# metadata blobs (`DavNzbFile`, `DavRarFile`, `DavMultipartFile`): zstd-compressed
//! `MemoryPack`, decoded into types shaped like the C# ones. Members are read by their
//! `MemoryPackOrder`; members a blob predates read as their C# default.

use std::io::Read as _;

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
    compressed: &[u8],
    read: impl FnOnce(&Object<'_>) -> Result<T, Error>,
) -> Result<T, DecodeError> {
    decode_within(compressed, crate::MAX_METADATA_BYTES, read)
}

/// [`decode`], refusing more than `limit` decompressed bytes.
fn decode_within<T>(
    compressed: &[u8],
    limit: u64,
    read: impl FnOnce(&Object<'_>) -> Result<T, Error>,
) -> Result<T, DecodeError> {
    let mut raw = Vec::new();
    zstd::stream::read::Decoder::new(compressed)?
        .take(limit + 1)
        .read_to_end(&mut raw)?;
    if raw.len() as u64 > limit {
        return Err(DecodeError::Zstd(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            format!("larger than {limit} bytes once decompressed"),
        )));
    }
    let mut r = Reader::new(&raw);
    let root = r.object()?.ok_or(Error::Invalid("null root"))?;
    r.finish()?;
    Ok(read(&root)?)
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
        id: o.field(0, Reader::guid)?.unwrap_or_default(),
        segment_ids: o.field(1, Reader::strings)?.flatten().unwrap_or_default(),
        ranges: o.field(2, ranges)?.flatten(),
        fallbacks: o.field(3, fallbacks)?.flatten(),
        missing: o.field(4, Reader::i32s)?.flatten(),
        container_class: o.field(5, Reader::opt_u8)?.flatten(),
        critical_head_end: o.field(6, Reader::opt_i64)?.flatten(),
        corrupt: o.field(7, Reader::i32s)?.flatten(),
        ranges_trusted: o.field(8, Reader::opt_bool)?.flatten(),
        proof: o.field(9, proof)?.flatten(),
    })
}

pub fn rar_file(o: &Object<'_>) -> Result<RarFile, Error> {
    Ok(RarFile {
        id: o.field(0, Reader::guid)?.unwrap_or_default(),
        parts: o
            .field(1, |r| r.list(|r| object(r, rar_part)))?
            .flatten()
            .unwrap_or_default(),
    })
}

fn rar_part(o: &Object<'_>) -> Result<RarPart, Error> {
    Ok(RarPart {
        segment_ids: o.field(0, Reader::strings)?.flatten().unwrap_or_default(),
        part_size: o.field(1, Reader::i64)?.unwrap_or_default(),
        offset: o.field(2, Reader::i64)?.unwrap_or_default(),
        byte_count: o.field(3, Reader::i64)?.unwrap_or_default(),
        ranges: o.field(4, ranges)?.flatten(),
        fallbacks: o.field(5, fallbacks)?.flatten(),
        ranges_trusted: o.field(6, Reader::opt_bool)?.flatten(),
    })
}

pub fn multipart_file(o: &Object<'_>) -> Result<MultipartFile, Error> {
    let id = o.field(0, Reader::guid)?.unwrap_or_default();
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
                    is_lazy: m.field(2, Reader::bool)?.unwrap_or_default(),
                    path_in_archive: m.field(3, Reader::string)?.flatten(),
                    password: m.field(4, Reader::string)?.flatten(),
                    pending: m
                        .field(5, |r| r.list(|r| object(r, pending_part)))?
                        .flatten()
                        .unwrap_or_default(),
                    expected_size: m.field(6, Reader::opt_i64)?.flatten(),
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
        segment_ids: o.field(0, Reader::strings)?.flatten().unwrap_or_default(),
        segment_range: o.field(1, range)?.unwrap_or_default(),
        data_range: o.field(2, range)?.unwrap_or_default(),
        ranges: o.field(3, ranges)?.flatten(),
        fallbacks: o.field(4, fallbacks)?.flatten(),
        split_after: o.field(5, Reader::opt_bool)?.flatten(),
        ranges_trusted: o.field(6, Reader::opt_bool)?.flatten(),
        proof: o.field(7, proof)?.flatten(),
    })
}

fn pending_part(o: &Object<'_>) -> Result<PendingPart, Error> {
    Ok(PendingPart {
        segment_ids: o.field(0, Reader::strings)?.flatten().unwrap_or_default(),
        segment_range: o.field(1, range)?.unwrap_or_default(),
        estimated_size: o.field(2, Reader::i64)?.unwrap_or_default(),
        fallbacks: o.field(3, fallbacks)?.flatten(),
        proof: o.field(4, proof)?.flatten(),
    })
}

/// A non-null object read with `read`.
fn object<'a, T>(
    r: &mut Reader<'a>,
    read: impl FnOnce(&Object<'a>) -> Result<T, Error>,
) -> Result<T, Error> {
    read(&r.object()?.ok_or(Error::Invalid("null element"))?)
}

fn range(r: &mut Reader<'_>) -> Result<LongRange, Error> {
    object(r, |o| {
        Ok(LongRange {
            start: o.field(0, Reader::i64)?.unwrap_or_default(),
            end: o.field(1, Reader::i64)?.unwrap_or_default(),
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
    Ok(Some(AesParams {
        decoded_size: o.field(0, Reader::i64)?.unwrap_or_default(),
        iv: o.field(1, Reader::bytes)?.flatten().unwrap_or_default(),
        key: o.field(2, Reader::bytes)?.flatten().unwrap_or_default(),
    }))
}

fn proof(r: &mut Reader<'_>) -> Result<Option<Par2Proof>, Error> {
    let Some(o) = r.object()? else {
        return Ok(None);
    };
    Ok(Some(Par2Proof {
        file_length: o.field(0, Reader::i64)?.unwrap_or_default(),
        slice_size: o.field(1, Reader::i32)?.unwrap_or_default(),
        slice_md5: o.field(2, Reader::bytes)?.flatten().unwrap_or_default(),
        slice_crc32: o.field(3, Reader::u32s)?.flatten().unwrap_or_default(),
        file_id: o.field(4, Reader::bytes)?.flatten().unwrap_or_default(),
        file_hash: o.field(5, Reader::bytes)?.flatten().unwrap_or_default(),
        file_16k_hash: o.field(6, Reader::bytes)?.flatten().unwrap_or_default(),
    }))
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
    fn decode_refuses_output_past_its_limit() {
        let over = zstd::encode_all(&vec![0u8; 1_001][..], 3).unwrap();
        match decode_within(&over, 1_000, nzb_file) {
            Err(DecodeError::Zstd(e)) => assert!(e.to_string().contains("larger than"), "{e}"),
            other => panic!("expected a size refusal, got {other:?}"),
        }
        let at = zstd::encode_all(&vec![0u8; 1_000][..], 3).unwrap();
        assert!(matches!(
            decode_within(&at, 1_000, nzb_file),
            Err(DecodeError::Payload(_))
        ));
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
        let file = decode(&compressed, nzb_file).unwrap();
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
