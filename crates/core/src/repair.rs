//! Versioned identity of repaired posted bytes, before archive extraction/decryption.

use serde::{Deserialize, Serialize};

use crate::{BlobId, ByteRange, Par2Proof, PatchId, Segment};

const MAGIC: &[u8; 4] = b"IDRP";
pub const VERSION: u16 = 1;
pub const PREFIX_LEN: usize = 10;
pub const MAX_HEADER_BYTES: usize = 8192;

/// An omitted article has no global message ID. Its exact posted range is scoped to
/// one unchanged original NZB and one PAR2 recovery set.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct Omitted {
    pub nzb: BlobId,
    pub generation: i64,
    pub set: [u8; 16],
    pub file: [u8; 16],
    pub range: ByteRange,
    pub file_length: u64,
}

impl Omitted {
    /// Only trusted posted offsets and a saved recovery set can identify an empty ID.
    pub fn for_segment(
        source: Option<(BlobId, i64)>,
        segment: &Segment,
        begin: Option<u64>,
        proof: Option<&Par2Proof>,
    ) -> Result<Option<Self>, Error> {
        if !segment.id.is_empty() {
            return Ok(None);
        }
        let Some(((nzb, generation), (start, proof))) = source.zip(begin.zip(proof)) else {
            return Ok(None);
        };
        let Some(set) = proof.set_id else {
            return Ok(None);
        };
        let end = start
            .checked_add(u64::from(segment.size))
            .ok_or(Error::Invalid("source byte range"))?;
        if generation < 0 || end <= start || end > proof.file_length {
            return Err(Error::Invalid("omitted article identity"));
        }
        Ok(Some(Self {
            nzb,
            generation,
            set,
            file: proof.file_id,
            range: ByteRange { start, end },
            file_length: proof.file_length,
        }))
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Source {
    pub set: [u8; 16],
    pub file: [u8; 16],
    pub range: ByteRange,
    pub file_length: u64,
    /// None identifies an omitted range by set/file/range, never by an empty message ID.
    pub message_id: Option<String>,
    pub part: Option<u32>,
    pub total: Option<u32>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Patch {
    pub id: PatchId,
    pub source: Source,
    pub sha256: [u8; 32],
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("invalid repair file header")]
    Header,
    #[error("unsupported repair file version {0}")]
    Version(u16),
    #[error("invalid repair descriptor: {0}")]
    Invalid(&'static str),
    #[error("malformed repair descriptor")]
    Payload(#[from] postcard::Error),
}

impl Patch {
    pub fn validate(&self) -> Result<(), Error> {
        let s = &self.source;
        if s.range.is_empty() || s.range.end > s.file_length || s.range.len() > u64::from(u32::MAX)
        {
            return Err(Error::Invalid("source byte range"));
        }
        if s.message_id
            .as_ref()
            .is_some_and(|id| id.is_empty() || id.len() > 4096)
        {
            return Err(Error::Invalid("message identity"));
        }
        if s.part == Some(0)
            || s.total == Some(0)
            || (s.total.is_some() && s.part.is_none())
            || s.part
                .zip(s.total)
                .is_some_and(|(part, total)| part > total)
            || (s.part == Some(1) && s.range.start != 0)
            || s.part
                .zip(s.total)
                .is_some_and(|(part, total)| part == total && s.range.end != s.file_length)
            || (s.part.is_none()
                && s.message_id.is_some()
                && (s.range.start != 0 || s.range.end != s.file_length))
        {
            return Err(Error::Invalid("posted part metadata"));
        }
        Ok(())
    }

    /// Fixed versioned prefix plus a bounded postcard descriptor; payload bytes follow.
    pub fn encode(&self) -> Result<Vec<u8>, Error> {
        self.validate()?;
        let payload = postcard::to_stdvec(self)?;
        if payload.len() > MAX_HEADER_BYTES - PREFIX_LEN {
            return Err(Error::Header);
        }
        let mut header = Vec::with_capacity(PREFIX_LEN + payload.len());
        header.extend_from_slice(MAGIC);
        header.extend_from_slice(&VERSION.to_le_bytes());
        header.extend_from_slice(
            &u32::try_from(payload.len())
                .expect("bounded header")
                .to_le_bytes(),
        );
        header.extend_from_slice(&payload);
        Ok(header)
    }

    pub fn decode(bytes: &[u8]) -> Result<Self, Error> {
        let prefix = bytes.get(..PREFIX_LEN).ok_or(Error::Header)?;
        if header_length(prefix)? != bytes.len() {
            return Err(Error::Header);
        }
        let (patch, tail): (Self, _) = postcard::take_from_bytes(&bytes[PREFIX_LEN..])?;
        if !tail.is_empty() {
            return Err(Error::Header);
        }
        patch.validate()?;
        Ok(patch)
    }
}

/// Validate the complete prefix before allocating or reading the descriptor.
pub fn header_length(prefix: &[u8]) -> Result<usize, Error> {
    if prefix.len() != PREFIX_LEN || &prefix[..4] != MAGIC {
        return Err(Error::Header);
    }
    let version = u16::from_le_bytes(prefix[4..6].try_into().expect("checked prefix"));
    if version != VERSION {
        return Err(Error::Version(version));
    }
    let payload = u32::from_le_bytes(prefix[6..10].try_into().expect("checked prefix"));
    let length = usize::try_from(payload)
        .ok()
        .and_then(|length| length.checked_add(PREFIX_LEN))
        .filter(|&length| length <= MAX_HEADER_BYTES)
        .ok_or(Error::Header)?;
    Ok(length)
}
