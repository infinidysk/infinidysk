//! Blob format for file metadata and original NZBs.
//!
//! ```text
//! offset  size  field
//! 0       3     magic "IDB"
//! 3       1     kind: 1 = file metadata, 2 = NZB
//! 4       2     schema version, little-endian u16
//! 6       ..    one zstd frame
//! ```
//!
//! Metadata uses the postcard wire format (<https://postcard.jamesmunns.com/wire-format>)
//! with the versioned layouts below: field order is the struct field order,
//! integers are varints, enums are a varint variant index. An NZB blob payload is the NZB
//! XML exactly as received.
//!
//! Postcard has no field tags, so any layout change needs a new version. Readers keep
//! decoding every older version (converting to the current types) instead of rewriting
//! old blobs, and may feature-gate on [`Blob::version`].
//!
//! Metadata versions: 1 = no encryption; 2 = adds [`MultipartFile::crypt`]; 3 = v2 layout
//! whose segments may have an empty id (an article the NZB never listed), so older readers
//! refuse it instead of asking providers for ""; 4 = (v2 metadata, recovery-set IDs), with
//! one optional ID per source file/volume. Writers emit the lowest version that holds
//! the data, so files without newer features stay readable by older readers.

use serde::{Deserialize, Serialize};
use std::io::Read as _;

use crate::{FileMeta, FilePart, MultipartFile, NzbFile};

const MAGIC: &[u8; 3] = b"IDB";
const HEADER_LEN: usize = 6;
/// Current metadata layout.
pub const META_VERSION: u16 = 4;
/// Current NZB layout.
pub const NZB_VERSION: u16 = 1;
const ZSTD_LEVEL: i32 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Kind {
    Meta = 1,
    Nzb = 2,
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("not a blob (bad magic or truncated header)")]
    Header,
    #[error("unknown blob kind {0}")]
    UnknownKind(u8),
    #[error("expected a {expected:?} blob, found {found:?}")]
    WrongKind { expected: Kind, found: Kind },
    #[error("unsupported {kind:?} blob version {version}")]
    UnsupportedVersion { kind: Kind, version: u16 },
    #[error("zstd failed")]
    Zstd(#[from] std::io::Error),
    #[error("metadata payload is malformed")]
    Payload(#[from] postcard::Error),
    #[error("metadata recovery-set IDs do not match its proofs")]
    RecoverySets,
    #[error("decompressed blob exceeds its {0}-byte allowance")]
    Limit(usize),
}

/// A decoded header plus its still-compressed payload.
#[derive(Debug)]
pub struct Blob<'a> {
    pub kind: Kind,
    pub version: u16,
    payload: &'a [u8],
}

impl<'a> Blob<'a> {
    pub fn parse(bytes: &'a [u8]) -> Result<Self, Error> {
        let (header, payload) = bytes.split_at_checked(HEADER_LEN).ok_or(Error::Header)?;
        if &header[..3] != MAGIC {
            return Err(Error::Header);
        }
        let kind = match header[3] {
            1 => Kind::Meta,
            2 => Kind::Nzb,
            other => return Err(Error::UnknownKind(other)),
        };
        Ok(Self {
            kind,
            version: u16::from_le_bytes([header[4], header[5]]),
            payload,
        })
    }

    fn expect(&self, kind: Kind, limit: usize) -> Result<Vec<u8>, Error> {
        if self.kind != kind {
            return Err(Error::WrongKind {
                expected: kind,
                found: self.kind,
            });
        }
        let mut payload = Vec::new();
        zstd::stream::read::Decoder::new(self.payload)?
            .take(u64::try_from(limit).unwrap_or(u64::MAX).saturating_add(1))
            .read_to_end(&mut payload)?;
        if payload.len() > limit {
            return Err(Error::Limit(limit));
        }
        Ok(payload)
    }
}

/// Version 1 layout: [`FileMeta`] before multipart encryption.
#[derive(Serialize, Deserialize)]
enum MetaV1 {
    Nzb(NzbFile),
    Multipart { parts: Vec<FilePart> },
}

pub fn encode_meta(meta: &FileMeta) -> Result<Vec<u8>, Error> {
    let sets: Vec<_> = match meta {
        FileMeta::Nzb(file) => vec![file.proof.as_ref().and_then(|p| p.set_id)],
        FileMeta::Multipart(file) => file
            .parts
            .iter()
            .map(|p| p.proof.as_ref().and_then(|p| p.set_id))
            .collect(),
    };
    if sets.iter().any(Option::is_some) {
        return encode(Kind::Meta, 4, &postcard::to_stdvec(&(meta, sets))?);
    }
    if meta.has_placeholders() {
        return encode(Kind::Meta, 3, &postcard::to_stdvec(meta)?);
    }
    let v1 = match meta {
        FileMeta::Nzb(f) => Some(MetaV1::Nzb(f.clone())),
        FileMeta::Multipart(MultipartFile { parts, crypt: None }) => Some(MetaV1::Multipart {
            parts: parts.clone(),
        }),
        FileMeta::Multipart(_) => None,
    };
    match v1 {
        Some(v1) => encode(Kind::Meta, 1, &postcard::to_stdvec(&v1)?),
        None => encode(Kind::Meta, 2, &postcard::to_stdvec(meta)?),
    }
}

pub fn decode_meta(bytes: &[u8]) -> Result<FileMeta, Error> {
    decode_meta_limited(bytes, usize::MAX)
}

pub fn decode_meta_limited(bytes: &[u8], limit: usize) -> Result<FileMeta, Error> {
    let blob = Blob::parse(bytes)?;
    let payload = blob.expect(Kind::Meta, limit)?;
    match blob.version {
        1 => Ok(match postcard::from_bytes(&payload)? {
            MetaV1::Nzb(f) => FileMeta::Nzb(f),
            MetaV1::Multipart { parts } => {
                FileMeta::Multipart(MultipartFile { parts, crypt: None })
            }
        }),
        2 | 3 => Ok(postcard::from_bytes(&payload)?),
        4 => {
            let (mut meta, sets): (FileMeta, Vec<Option<[u8; 16]>>) =
                postcard::from_bytes(&payload)?;
            let proofs: Vec<_> = match &mut meta {
                FileMeta::Nzb(file) => vec![&mut file.proof],
                FileMeta::Multipart(file) => file.parts.iter_mut().map(|p| &mut p.proof).collect(),
            };
            if proofs.len() != sets.len() {
                return Err(Error::RecoverySets);
            }
            for (proof, set) in proofs.into_iter().zip(sets) {
                match proof {
                    Some(proof) => proof.set_id = set,
                    None if set.is_some() => return Err(Error::RecoverySets),
                    None => {}
                }
            }
            Ok(meta)
        }
        version => Err(Error::UnsupportedVersion {
            kind: Kind::Meta,
            version,
        }),
    }
}

pub fn encode_nzb(xml: &[u8]) -> Result<Vec<u8>, Error> {
    encode(Kind::Nzb, NZB_VERSION, xml)
}

pub fn decode_nzb(bytes: &[u8]) -> Result<Vec<u8>, Error> {
    decode_nzb_limited(bytes, usize::MAX)
}

pub fn decode_nzb_limited(bytes: &[u8], limit: usize) -> Result<Vec<u8>, Error> {
    let blob = Blob::parse(bytes)?;
    let payload = blob.expect(Kind::Nzb, limit)?;
    match blob.version {
        1 => Ok(payload),
        version => Err(Error::UnsupportedVersion {
            kind: Kind::Nzb,
            version,
        }),
    }
}

fn encode(kind: Kind, version: u16, payload: &[u8]) -> Result<Vec<u8>, Error> {
    let mut out = Vec::with_capacity(HEADER_LEN + payload.len() / 4);
    out.extend_from_slice(MAGIC);
    out.push(kind as u8);
    out.extend_from_slice(&version.to_le_bytes());
    zstd::stream::copy_encode(payload, &mut out, ZSTD_LEVEL)?;
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::crypt::{Crypt, Key};
    use crate::{ByteRange, Par2Proof, Segment, Segments};

    #[test]
    fn repair_decoding_stops_at_its_decompressed_allowance() {
        let encoded = encode_nzb(&vec![b' '; 100_000]).unwrap();
        assert!(decode_nzb_limited(&encoded, 1024).is_err());
        assert_eq!(
            decode_nzb_limited(&encoded, 100_000).unwrap().len(),
            100_000
        );
    }

    fn segments(n: u32) -> Segments {
        Segments {
            items: (0..n)
                .map(|i| Segment {
                    id: format!("part{i}of{n}.abc@example"),
                    size: 768_000,
                    fallbacks: if i == 1 {
                        vec!["alt@example".into()]
                    } else {
                        vec![]
                    },
                })
                .collect(),
            sizes_trusted: true,
            missing: vec![2],
            corrupt: vec![],
        }
    }

    #[test]
    fn recovery_set_identity_survives_restart() {
        let mut meta = FileMeta::Nzb(NzbFile {
            segments: segments(4),
            proof: Some(Par2Proof {
                set_id: Some([9; 16]),
                file_length: 3_072_000,
                slice_size: 768_000,
                slice_md5: vec![[7; 16]; 4],
                slice_crc32: vec![1, 2, 3, 4],
                file_id: [1; 16],
                file_md5: [2; 16],
                md5_16k: [3; 16],
            }),
        });
        let bytes = encode_meta(&meta).expect("encode recovery set");
        assert_eq!(Blob::parse(&bytes).expect("header").version, 4);
        assert_eq!(decode_meta(&bytes).expect("decode recovery set"), meta);
        let FileMeta::Nzb(file) = &meta else {
            unreachable!()
        };
        let part = FilePart {
            segments: file.segments.clone(),
            offset: 0,
            data: ByteRange {
                start: 0,
                end: file.segments.size(),
            },
            split_after: true,
            proof: file.proof.clone(),
        };
        let mut unknown = part.clone();
        unknown.proof.as_mut().unwrap().set_id = None;
        let mut no_proof = part.clone();
        no_proof.proof = None;
        let multipart = FileMeta::Multipart(MultipartFile {
            parts: vec![part, unknown, no_proof],
            crypt: Some(Crypt {
                key: Key::Aes256([5; 32]),
                iv: [6; 16],
                size: 37,
            }),
        });
        let bytes = encode_meta(&multipart).unwrap();
        assert_eq!(Blob::parse(&bytes).unwrap().version, 4);
        assert_eq!(decode_meta(&bytes).unwrap(), multipart);
        let FileMeta::Nzb(file) = &mut meta else {
            unreachable!()
        };
        file.proof.as_mut().expect("proof").set_id = None;
        let bytes = encode_meta(&meta).expect("encode legacy proof");
        assert_eq!(Blob::parse(&bytes).expect("header").version, 1);
        assert_eq!(decode_meta(&bytes).expect("decode legacy proof"), meta);
    }

    #[test]
    fn recovery_set_ids_must_have_one_slot_per_source_and_a_proof() {
        let meta = FileMeta::Nzb(NzbFile {
            segments: segments(1),
            proof: None,
        });
        for sets in [vec![], vec![Some([1; 16])], vec![None, None]] {
            let bytes =
                encode(Kind::Meta, 4, &postcard::to_stdvec(&(&meta, sets)).unwrap()).unwrap();
            assert!(matches!(decode_meta(&bytes), Err(Error::RecoverySets)));
        }
    }

    #[test]
    fn legacy_proof_wire_bytes_have_no_recovery_set_field() {
        let mut payload = vec![0, 1, 1, b'a', 4, 0, 1, 0, 0, 1, 4, 4, 1];
        payload.extend([1; 16]);
        payload.extend([1, 1]);
        payload.extend([2; 16]);
        payload.extend([3; 16]);
        payload.extend([4; 16]);
        for version in [1, 2, 3] {
            let bytes = encode(Kind::Meta, version, &payload).unwrap();
            let meta = decode_meta(&bytes).expect("legacy proof layout");
            let FileMeta::Nzb(file) = &meta else {
                unreachable!()
            };
            assert_eq!(file.proof.as_ref().unwrap().set_id, None);
            assert_eq!(file.proof.as_ref().unwrap().file_id, [2; 16]);
            let current = encode_meta(&meta).unwrap();
            assert_eq!(
                Blob::parse(&current)
                    .unwrap()
                    .expect(Kind::Meta, usize::MAX)
                    .unwrap(),
                payload
            );
        }
    }

    #[test]
    fn metadata_round_trips_and_pins_its_header() {
        let metas = [
            FileMeta::Nzb(NzbFile {
                segments: segments(4),
                proof: Some(Par2Proof {
                    set_id: None,
                    file_length: 3_072_000,
                    slice_size: 768_000,
                    slice_md5: vec![[7; 16]; 4],
                    slice_crc32: vec![1, 2, 3, 4],
                    file_id: [1; 16],
                    file_md5: [2; 16],
                    md5_16k: [3; 16],
                }),
            }),
            FileMeta::Multipart(MultipartFile {
                parts: vec![FilePart {
                    segments: segments(3),
                    offset: 768_000,
                    data: ByteRange {
                        start: 768_100,
                        end: 3_072_000,
                    },
                    split_after: true,
                    proof: None,
                }],
                crypt: None,
            }),
        ];
        for meta in metas {
            let bytes = encode_meta(&meta).expect("encodes");
            assert_eq!(
                &bytes[..6],
                b"IDB\x01\x01\x00",
                "header layout is part of the format"
            );
            assert_eq!(decode_meta(&bytes).expect("decodes"), meta);
        }
        let encrypted = FileMeta::Multipart(MultipartFile {
            parts: vec![],
            crypt: Some(Crypt {
                key: Key::Aes256([5; 32]),
                iv: [6; 16],
                size: 37,
            }),
        });
        let bytes = encode_meta(&encrypted).expect("encodes");
        assert_eq!(&bytes[..6], b"IDB\x01\x02\x00", "encryption needs v2");
        assert_eq!(decode_meta(&bytes).expect("decodes"), encrypted);
        assert_eq!(encrypted.size(), 37);

        let mut gap = segments(4);
        gap.items[2].id.clear();
        let gap = FileMeta::Nzb(NzbFile {
            segments: gap,
            proof: None,
        });
        let bytes = encode_meta(&gap).expect("encodes");
        assert_eq!(&bytes[..6], b"IDB\x01\x03\x00", "placeholders need v3");
        assert_eq!(decode_meta(&bytes).expect("decodes"), gap);
    }

    #[test]
    fn readers_reject_foreign_kinds_and_future_versions() {
        let nzb = encode_nzb(b"<nzb/>").expect("encodes");
        assert_eq!(decode_nzb(&nzb).expect("decodes"), b"<nzb/>");
        assert!(matches!(decode_meta(&nzb), Err(Error::WrongKind { .. })));

        let mut future = nzb;
        future[4] = 9;
        assert!(matches!(
            decode_nzb(&future),
            Err(Error::UnsupportedVersion { version: 9, .. })
        ));
        assert!(matches!(decode_meta(b"ID"), Err(Error::Header)));
    }
}
