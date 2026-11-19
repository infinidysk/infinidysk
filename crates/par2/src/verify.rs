//! Full-file verification without retaining input bytes or requiring recovery data.

use md5::{Digest, Md5};

use crate::{Error, Id, Slice};

#[derive(Debug, PartialEq, Eq)]
pub struct Verified {
    pub bytes: u64,
    /// Zero when only the expected whole-file hash was available.
    pub slices: usize,
}

/// Feed original source bytes in order. No file, NZB, or recovery packet is needed.
/// Hashing uses fixed-size state; expected checksums remain borrowed from the caller.
pub struct Verifier<'a> {
    length: u64,
    expected_hash: Id,
    slice_size: u64,
    slices: &'a [Slice],
    file_hash: Md5,
    slice_hash: Md5,
    crc: u32,
    bytes: u64,
    in_slice: u64,
    checked: usize,
    failed: bool,
}

impl<'a> Verifier<'a> {
    /// With no slice checksums, verify only length and whole-file MD5.
    pub fn new(length: u64, hash: Id, slice_size: u64, slices: &'a [Slice]) -> Result<Self, Error> {
        if !slices.is_empty()
            && (slice_size == 0
                || !slice_size.is_multiple_of(4)
                || slices.len() > 32_768
                || u64::try_from(slices.len()).ok() != Some(length.div_ceil(slice_size)))
        {
            return Err(Error::Invalid("slice checksum count or size"));
        }
        Ok(Self {
            length,
            expected_hash: hash,
            slice_size,
            slices,
            file_hash: Md5::new(),
            slice_hash: Md5::new(),
            crc: 0,
            bytes: 0,
            in_slice: 0,
            checked: 0,
            failed: false,
        })
    }

    /// Verify a chunk. An error or panicking callback makes this verifier unusable.
    pub fn push(&mut self, mut data: &[u8], cancelled: impl Fn() -> bool) -> Result<(), Error> {
        if self.failed {
            return Err(Error::Invalid("verification is no longer active"));
        }
        self.failed = true;
        if cancelled() {
            return Err(Error::Cancelled);
        }
        let actual = self
            .bytes
            .checked_add(data.len() as u64)
            .ok_or(Error::Invalid("file length overflow"))?;
        if actual > self.length {
            return Err(Error::Length {
                expected: self.length,
                actual,
            });
        }
        while !data.is_empty() {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            let limit = if self.slices.is_empty() {
                4096
            } else {
                usize::try_from((self.slice_size - self.in_slice).min(4096))
                    .expect("chunk is bounded by 4096")
            };
            let n = data.len().min(limit);
            let chunk = &data[..n];
            self.file_hash.update(chunk);
            self.bytes += n as u64;
            if !self.slices.is_empty() {
                self.slice_hash.update(chunk);
                self.crc = infinidysk_yenc::crc32(self.crc, chunk);
                self.in_slice += n as u64;
                if self.in_slice == self.slice_size || self.bytes == self.length {
                    self.finish_slice(&cancelled)?;
                }
            }
            data = &data[n..];
        }
        self.failed = false;
        Ok(())
    }

    fn finish_slice(&mut self, cancelled: &impl Fn() -> bool) -> Result<(), Error> {
        static ZEROS: [u8; 4096] = [0; 4096];
        let mut padding = self.slice_size - self.in_slice;
        while padding != 0 {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            let n = usize::try_from(padding.min(4096)).expect("padding chunk is bounded by 4096");
            self.slice_hash.update(&ZEROS[..n]);
            self.crc = infinidysk_yenc::crc32(self.crc, &ZEROS[..n]);
            padding -= n as u64;
        }
        let expected = self.slices[self.checked];
        let hash: Id = std::mem::take(&mut self.slice_hash).finalize().into();
        if self.crc != expected.crc32 || hash != expected.md5 {
            return Err(Error::Slice {
                index: self.checked,
                reason: "verification checksum mismatch",
            });
        }
        self.checked += 1;
        self.in_slice = 0;
        self.crc = 0;
        Ok(())
    }

    pub fn finish(self, cancelled: impl Fn() -> bool) -> Result<Verified, Error> {
        if self.failed {
            return Err(Error::Invalid("verification is no longer active"));
        }
        if cancelled() {
            return Err(Error::Cancelled);
        }
        if self.bytes != self.length {
            return Err(Error::Length {
                expected: self.length,
                actual: self.bytes,
            });
        }
        if <Id>::from(self.file_hash.finalize()) != self.expected_hash {
            return Err(Error::FileChecksum);
        }
        Ok(Verified {
            bytes: self.bytes,
            slices: self.checked,
        })
    }
}
