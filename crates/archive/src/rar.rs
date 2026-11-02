//! RAR4 and RAR5 block scanner.
//!
//! [`Scanner`] walks the block headers of one volume and reports each file
//! entry with the absolute byte range of its packed data. Packed data is never
//! read: the scanner jumps straight to the next header, so the caller only has
//! to fetch the few bytes around each header.

use std::ops::Range;

use infinidysk_yenc::crc32;
use sha2::{Digest, Sha256};

use crate::crypt::{BLOCK, Cipher, Derived, Encryption, Kdf, MAX_LG2};
use crate::{Error, Format};

pub const RAR4_SIGNATURE: &[u8] = b"Rar!\x1a\x07\x00";
pub const RAR5_SIGNATURE: &[u8] = b"Rar!\x1a\x07\x01\x00";

/// RAR5 caps a header at 2 MiB; anything larger is corruption.
const MAX_HEADER: u64 = 2 << 20;
/// Enough for a RAR5 header CRC plus the longest header-size vint.
const MIN_BLOCK: usize = 14;

/// What the main and end-of-archive headers say about this volume.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Volume {
    /// Part of a multi-volume set.
    pub multi: bool,
    /// First volume of the set (always true for single-volume archives).
    pub first: bool,
    /// Zero-based volume number: RAR5 main header, or RAR4 end header once seen.
    pub number: Option<u32>,
    pub solid: bool,
}

/// One file header and where its packed data lives in this volume.
#[allow(clippy::struct_excessive_bools)] // reason: independent header flags
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    /// Path inside the archive, `/`-separated.
    pub name: String,
    /// Absolute byte range of the packed data in this volume.
    pub data: Range<u64>,
    /// Unpacked size of the whole file, when the archive records it.
    pub size: Option<u64>,
    /// RAR5: CRC of the whole file (last part only). RAR4: CRC of this part's
    /// packed data, or of the whole file for the last part.
    pub crc32: Option<u32>,
    pub split_before: bool,
    pub split_after: bool,
    /// Packed data is the file content verbatim.
    pub stored: bool,
    pub solid: bool,
    pub encrypted: bool,
    /// How to decrypt the packed data; `None` when not encrypted, or encrypted with a
    /// scheme older than AES.
    pub crypt: Option<Encryption>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Step {
    /// Call [`Scanner::next`] again with bytes covering `offset..offset + len`,
    /// or as many as exist before the end of the volume.
    Need {
        offset: u64,
        len: u64,
    },
    Entry(Entry),
    /// Headers are encrypted: derive this key and pass it to [`Scanner::add_key`], then
    /// call [`Scanner::next`] again.
    Key(Kdf),
    /// End-of-archive header reached; repeats on further calls.
    End {
        next_volume: bool,
    },
}

#[derive(Debug, Default)]
pub struct Scanner {
    format: Option<Format>,
    pos: u64,
    volume: Volume,
    end: Option<bool>,
    headers: Headers,
    keys: Vec<(Kdf, Derived)>,
    /// An encrypted header decoded, so later failures are corruption, not a wrong key.
    decrypted: bool,
}

/// How the remaining headers are stored.
#[derive(Debug, Default)]
enum Headers {
    #[default]
    Plain,
    /// RAR5: each header follows its IV, encrypted with this key.
    Rar5(Kdf),
    /// RAR4: each header follows its own salt.
    Rar4,
}

enum Parsed {
    Need(u64),
    Key(Kdf),
    Advance(u64, Option<Entry>),
}

impl Scanner {
    pub fn new() -> Self {
        Self::default()
    }

    pub const fn format(&self) -> Option<Format> {
        self.format
    }

    pub const fn volume(&self) -> &Volume {
        &self.volume
    }

    /// Supplies the key a [`Step::Key`] asked for.
    pub fn add_key(&mut self, kdf: Kdf, derived: Derived) {
        self.keys.push((kdf, derived));
    }

    fn key(&self, kdf: &Kdf) -> Option<Derived> {
        self.keys
            .iter()
            .find(|(k, _)| k == kdf)
            .map(|(_, d)| d.clone())
    }

    /// Continues the scan using `buf`, which holds volume bytes starting at `at`.
    pub fn next(&mut self, at: u64, buf: &[u8]) -> Result<Step, Error> {
        loop {
            if let Some(next_volume) = self.end {
                return Ok(Step::End { next_volume });
            }
            let avail = self
                .pos
                .checked_sub(at)
                .and_then(|rel| usize::try_from(rel).ok())
                .and_then(|rel| buf.get(rel..))
                .unwrap_or_default();
            let parsed = match self.format {
                None => self.signature(avail)?,
                Some(Format::Rar4) => self.rar4(avail)?,
                Some(_) => self.rar5(avail)?,
            };
            match parsed {
                Parsed::Need(len) => {
                    return Ok(Step::Need {
                        offset: self.pos,
                        len,
                    });
                }
                Parsed::Key(kdf) => return Ok(Step::Key(kdf)),
                Parsed::Advance(len, entry) => {
                    self.pos = self
                        .pos
                        .checked_add(len)
                        .ok_or_else(|| self.corrupt("data size"))?;
                    if let Some(entry) = entry {
                        return Ok(Step::Entry(entry));
                    }
                }
            }
        }
    }

    fn signature(&mut self, b: &[u8]) -> Result<Parsed, Error> {
        if b.len() < RAR5_SIGNATURE.len() {
            return Ok(Parsed::Need(RAR5_SIGNATURE.len() as u64));
        }
        let Some(format @ (Format::Rar4 | Format::Rar5)) = crate::detect(b) else {
            return Err(Error::Signature);
        };
        self.format = Some(format);
        let len = if format == Format::Rar5 {
            RAR5_SIGNATURE
        } else {
            RAR4_SIGNATURE
        }
        .len();
        Ok(Parsed::Advance(len as u64, None))
    }

    fn corrupt(&self, reason: &'static str) -> Error {
        Error::Corrupt {
            offset: self.pos,
            reason,
        }
    }

    /// A RAR5 file encryption record (`iv`), or the archive encryption header, and
    /// whether checksums are MAC-tweaked.
    fn rar5_crypt(&self, body: &[u8], iv: bool) -> Result<(Encryption, bool), Error> {
        let mut c = Cur::new(body, self);
        c.vint()?; // version
        let flags = c.vint()?;
        let lg2 = c.take(1)?[0];
        if lg2 > MAX_LG2 {
            return Err(self.corrupt("key derivation cost"));
        }
        let salt = c.take(16)?.try_into().expect("16 bytes");
        let iv = if iv {
            Some(c.take(BLOCK)?.try_into().expect("one block"))
        } else {
            None
        };
        let check = if flags & 0x1 != 0 {
            let check: [u8; 8] = c.take(8)?.try_into().expect("8 bytes");
            let sum = c.take(4)?;
            // A damaged check value is ignored rather than failing every password.
            (Sha256::digest(check)[..4] == *sum).then_some(check)
        } else {
            None
        };
        Ok((
            Encryption {
                kdf: Kdf::Rar5 { salt, lg2, check },
                iv,
            },
            flags & 0x2 != 0,
        ))
    }

    /// A wrong key garbles the first encrypted header; later failures are corruption.
    fn bad_key(&self, reason: &'static str) -> Error {
        if self.decrypted {
            self.corrupt(reason)
        } else {
            Error::WrongPassword
        }
    }

    /// Checks the outcome of parsing a decrypted header.
    fn decrypted(&mut self, parsed: Result<Parsed, Error>) -> Result<Parsed, Error> {
        match parsed {
            Ok(parsed) => {
                self.decrypted = true;
                Ok(parsed)
            }
            Err(Error::HeaderCrc(_) | Error::Corrupt { .. }) if !self.decrypted => {
                Err(Error::WrongPassword)
            }
            Err(error) => Err(error),
        }
    }

    /// Decrypts the encrypted header at the start of `b`, which follows `skip` bytes
    /// (IV or salt). `len` reads the header length from its first block.
    fn decrypt_header(
        &mut self,
        b: &[u8],
        kdf: Kdf,
        skip: usize,
        len: fn(&[u8; BLOCK]) -> Option<u64>,
    ) -> Result<Result<(Vec<u8>, u64), Parsed>, Error> {
        let Some(derived) = self.key(&kdf) else {
            return Ok(Err(Parsed::Key(kdf)));
        };
        if b.len() < skip + BLOCK {
            return Ok(Err(Parsed::Need((skip + BLOCK) as u64)));
        }
        let cipher = Cipher::new(&derived.key);
        // RAR4 derives the IV with the key; RAR5 stores it before the header.
        let iv = derived
            .iv
            .unwrap_or_else(|| b[..BLOCK].try_into().expect("one block"));
        let mut first: [u8; BLOCK] = b[skip..skip + BLOCK].try_into().expect("one block");
        cipher.decrypt(&mut iv.clone(), &mut first);
        let total = len(&first)
            .filter(|&n| n <= MAX_HEADER)
            .ok_or_else(|| self.bad_key("header size"))?;
        let disk = skip as u64 + total.next_multiple_of(BLOCK as u64);
        // `disk` is at most a few MiB here, so it fits in usize.
        let Some(enc) = b.get(skip..usize::try_from(disk).unwrap_or(usize::MAX)) else {
            return Ok(Err(Parsed::Need(disk)));
        };
        let mut plain = enc.to_vec();
        cipher.decrypt(&mut iv.clone(), &mut plain);
        plain.truncate(usize::try_from(total).unwrap_or(usize::MAX));
        Ok(Ok((plain, disk)))
    }

    fn rar5(&mut self, b: &[u8]) -> Result<Parsed, Error> {
        let Headers::Rar5(kdf) = &self.headers else {
            let Some((size, size_len)) = b.get(4..).and_then(peek_vint) else {
                return if b.len() < MIN_BLOCK {
                    Ok(Parsed::Need(MIN_BLOCK as u64))
                } else {
                    Err(self.corrupt("header size"))
                };
            };
            if size > MAX_HEADER {
                return Err(self.corrupt("header size"));
            }
            let total = 4 + size_len as u64 + size;
            // `total` is at most a few MiB here, so it fits in usize.
            let Some(block) = b.get(..usize::try_from(total).unwrap_or(usize::MAX)) else {
                return Ok(Parsed::Need(total));
            };
            return self.rar5_block(block, total);
        };
        // Every header type is 1..=5; anything else is a wrong key.
        let len = |first: &[u8; BLOCK]| {
            let (size, size_len) = peek_vint(&first[4..])?;
            let (kind, _) = peek_vint(first.get(4 + size_len..)?)?;
            (1..=5)
                .contains(&kind)
                .then_some(4 + size_len as u64 + size)
        };
        match self.decrypt_header(b, kdf.clone(), BLOCK, len)? {
            Ok((plain, disk)) => {
                let parsed = self.rar5_block(&plain, disk);
                self.decrypted(parsed)
            }
            Err(parsed) => Ok(parsed),
        }
    }

    /// Parses one plaintext header block (CRC, size, header) that takes `disk` bytes
    /// in the volume.
    fn rar5_block(&mut self, block: &[u8], disk: u64) -> Result<Parsed, Error> {
        let (_, size_len) = peek_vint(&block[4..]).ok_or_else(|| self.corrupt("header size"))?;
        let header = &block[4..];
        if crc32(0, header) != u32::from_le_bytes([block[0], block[1], block[2], block[3]]) {
            return Err(Error::HeaderCrc(self.pos));
        }
        let mut c = Cur::new(&header[size_len..], self);
        let kind = c.vint()?;
        let flags = c.vint()?;
        let extra_len = if flags & 0x1 != 0 { c.vint()? } else { 0 };
        let data_len = if flags & 0x2 != 0 { c.vint()? } else { 0 };
        let extra = usize::try_from(extra_len)
            .ok()
            .and_then(|n| c.b.len().checked_sub(n))
            .map(|start| &c.b[start..])
            .ok_or_else(|| self.corrupt("extra area"))?;
        let advance = disk
            .checked_add(data_len)
            .ok_or_else(|| self.corrupt("data size"))?;
        let end = self
            .pos
            .checked_add(advance)
            .ok_or_else(|| self.corrupt("data size"))?;
        let entry = match kind {
            1 => {
                let archive = c.vint()?;
                let numbered = archive & 0x2 != 0;
                self.volume.multi = archive & 0x1 != 0;
                self.volume.solid = archive & 0x4 != 0;
                self.volume.first = !numbered;
                self.volume.number = if numbered {
                    Some(u32::try_from(c.vint()?).map_err(|_| self.corrupt("volume number"))?)
                } else {
                    self.volume.multi.then_some(0)
                };
                None
            }
            2 => {
                let file = c.vint()?;
                let size = c.vint()?;
                c.vint()?; // attributes
                if file & 0x2 != 0 {
                    c.take(4)?; // mtime
                }
                let crc = if file & 0x4 != 0 {
                    Some(c.u32()?)
                } else {
                    None
                };
                let compression = c.vint()?;
                c.vint()?; // host OS
                let name_len = c.vint()?;
                let name = c.take(usize::try_from(name_len).unwrap_or(usize::MAX))?;
                let crypt = rar5_record(extra, 0x1)
                    .map(|r| self.rar5_crypt(r, true))
                    .transpose()?;
                // A MAC-tweaked checksum is not the plain CRC of the content.
                let tweaked = crypt.as_ref().is_some_and(|(_, tweaked)| *tweaked);
                (file & 0x1 == 0).then(|| Entry {
                    name: String::from_utf8_lossy(name).into_owned(),
                    data: self.pos + disk..end,
                    size: (file & 0x8 == 0).then_some(size),
                    crc32: crc.filter(|_| !tweaked),
                    split_before: flags & 0x8 != 0,
                    split_after: flags & 0x10 != 0,
                    stored: compression & 0x380 == 0,
                    solid: compression & 0x40 != 0,
                    encrypted: crypt.is_some(),
                    crypt: crypt.map(|(e, _)| e),
                })
            }
            4 => {
                let (crypt, _) = self.rar5_crypt(c.b, false)?;
                self.headers = Headers::Rar5(crypt.kdf);
                None
            }
            5 => {
                self.end = Some(c.vint()? & 0x1 != 0);
                None
            }
            // Service headers (quick open, comments, recovery) and unknown blocks.
            _ => None,
        };
        Ok(Parsed::Advance(advance, entry))
    }

    fn rar4(&mut self, b: &[u8]) -> Result<Parsed, Error> {
        if matches!(self.headers, Headers::Rar4) {
            if b.len() < 8 {
                return Ok(Parsed::Need(8 + BLOCK as u64));
            }
            let salt = b[..8].try_into().expect("8 bytes");
            let len = |first: &[u8; BLOCK]| {
                let size = u64::from(u16::from_le_bytes([first[5], first[6]]));
                ((0x72..=0x7b).contains(&first[2]) && size >= 7).then_some(size)
            };
            return match self.decrypt_header(b, Kdf::Rar4 { salt: Some(salt) }, 8, len)? {
                Ok((plain, disk)) => {
                    let parsed = if plain.len() < 7 {
                        Err(self.corrupt("header size"))
                    } else {
                        self.rar4_block(&plain, disk)
                    };
                    self.decrypted(parsed)
                }
                Err(parsed) => Ok(parsed),
            };
        }
        if b.len() < 7 {
            return Ok(Parsed::Need(7));
        }
        let header_len = usize::from(u16::from_le_bytes([b[5], b[6]]));
        if header_len < 7 {
            return Err(self.corrupt("header size"));
        }
        let Some(header) = b.get(..header_len) else {
            return Ok(Parsed::Need(header_len as u64));
        };
        self.rar4_block(header, header_len as u64)
    }

    /// Parses one plaintext header that takes `size` bytes in the volume.
    fn rar4_block(&mut self, header: &[u8], size: u64) -> Result<Parsed, Error> {
        let kind = header[2];
        let flags = u16::from_le_bytes([header[3], header[4]]);
        if crc32(0, &header[2..]) & 0xffff != u32::from(u16::from_le_bytes([header[0], header[1]]))
        {
            return Err(Error::HeaderCrc(self.pos));
        }
        let mut c = Cur::new(&header[7..], self);
        match kind {
            0x73 => {
                if flags & 0x0080 != 0 {
                    self.headers = Headers::Rar4;
                }
                self.volume.multi = flags & 0x0001 != 0;
                self.volume.solid = flags & 0x0008 != 0;
                self.volume.first = !self.volume.multi || flags & 0x0100 != 0;
                Ok(Parsed::Advance(size, None))
            }
            0x74 => {
                let packed = c.u32()?;
                let unpacked = c.u32()?;
                c.take(1)?; // host OS
                let crc = c.u32()?;
                c.take(4)?; // mtime
                let version = c.take(1)?[0];
                let method = c.take(1)?[0];
                let name_len = c.u16()?;
                c.take(4)?; // attributes
                let (packed_high, unpacked_high) = if flags & 0x0100 != 0 {
                    (c.u32()?, c.u32()?)
                } else {
                    (0, 0)
                };
                let name = c.take(usize::from(name_len))?;
                let salt = if flags & 0x0400 != 0 {
                    Some(c.take(8)?.try_into().expect("8 bytes"))
                } else {
                    None
                };
                let encrypted = flags & 0x0004 != 0;
                // Versions before 2.9 used ciphers older than AES.
                let crypt = (encrypted && version >= 29).then_some(Encryption {
                    kdf: Kdf::Rar4 { salt },
                    iv: None,
                });
                let packed = u64::from(packed_high) << 32 | u64::from(packed);
                let unpacked = u64::from(unpacked_high) << 32 | u64::from(unpacked);
                let start = self.pos + size;
                let end = start
                    .checked_add(packed)
                    .ok_or_else(|| self.corrupt("data size"))?;
                let entry = (flags & 0x00e0 != 0x00e0).then(|| Entry {
                    name: rar4_name(name, flags & 0x0200 != 0),
                    data: start..end,
                    size: (unpacked != u64::MAX && unpacked != u64::from(u32::MAX))
                        .then_some(unpacked),
                    crc32: Some(crc),
                    split_before: flags & 0x0001 != 0,
                    split_after: flags & 0x0002 != 0,
                    stored: method == 0x30,
                    solid: flags & 0x0010 != 0,
                    encrypted,
                    crypt,
                });
                Ok(Parsed::Advance(size + packed, entry))
            }
            0x7b => {
                if flags & 0x0002 != 0 {
                    c.u32()?; // archive data CRC
                }
                if flags & 0x0008 != 0 {
                    self.volume.number = Some(u32::from(c.u16()?));
                }
                self.end = Some(flags & 0x0001 != 0);
                Ok(Parsed::Advance(size, None))
            }
            _ => {
                let data = if flags & 0x8000 != 0 {
                    u64::from(c.u32()?)
                } else {
                    0
                };
                Ok(Parsed::Advance(size + data, None))
            }
        }
    }
}

struct Cur<'a> {
    b: &'a [u8],
    at: u64,
}

impl<'a> Cur<'a> {
    const fn new(b: &'a [u8], scanner: &Scanner) -> Self {
        Self { b, at: scanner.pos }
    }

    const fn short(&self) -> Error {
        Error::Corrupt {
            offset: self.at,
            reason: "header too short",
        }
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8], Error> {
        let (head, tail) = self.b.split_at_checked(n).ok_or_else(|| self.short())?;
        self.b = tail;
        Ok(head)
    }

    fn u16(&mut self) -> Result<u16, Error> {
        let b = self.take(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }

    fn u32(&mut self) -> Result<u32, Error> {
        let b = self.take(4)?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    fn vint(&mut self) -> Result<u64, Error> {
        let (value, len) = peek_vint(self.b).ok_or_else(|| self.short())?;
        self.b = &self.b[len..];
        Ok(value)
    }
}

/// RAR5 variable-length integer: 7 bits per byte, low bits first.
fn peek_vint(b: &[u8]) -> Option<(u64, usize)> {
    let mut value = 0u64;
    for (i, &byte) in b.iter().take(10).enumerate() {
        value |= u64::from(byte & 0x7f) << (7 * i);
        if byte & 0x80 == 0 {
            return Some((value, i + 1));
        }
    }
    None
}

fn rar5_record(mut extra: &[u8], kind: u64) -> Option<&[u8]> {
    while let Some((len, len_len)) = peek_vint(extra) {
        let record = usize::try_from(len)
            .ok()
            .and_then(|len| extra.get(len_len..len_len.checked_add(len)?))?;
        if let Some((k, k_len)) = peek_vint(record)
            && k == kind
        {
            return Some(&record[k_len..]);
        }
        extra = &extra[len_len + record.len()..];
    }
    None
}

/// RAR4 names: plain bytes, UTF-8, or an ASCII name followed by a NUL and a
/// compact UTF-16 encoding relative to it.
fn rar4_name(raw: &[u8], unicode: bool) -> String {
    let name = match raw.iter().position(|&b| b == 0) {
        Some(nul) if unicode => decode_rar4_unicode(&raw[..nul], &raw[nul + 1..]),
        _ => String::from_utf8_lossy(raw).into_owned(),
    };
    name.replace('\\', "/")
}

fn decode_rar4_unicode(ascii: &[u8], enc: &[u8]) -> String {
    let byte = |i: usize| enc.get(i).copied().map(u16::from);
    let mut out = Vec::with_capacity(ascii.len());
    let Some(high) = byte(0) else {
        return String::from_utf8_lossy(ascii).into_owned();
    };
    let high = high << 8;
    let (mut pos, mut flags, mut bits) = (1, 0u16, 0);
    while pos < enc.len() {
        if bits == 0 {
            flags = u16::from(enc[pos]);
            pos += 1;
            bits = 8;
        }
        match (flags >> 6) & 0x3 {
            0 => {
                out.extend(byte(pos));
                pos += 1;
            }
            1 => {
                out.extend(byte(pos).map(|b| b + high));
                pos += 1;
            }
            2 => {
                out.extend(byte(pos).zip(byte(pos + 1)).map(|(lo, hi)| lo | hi << 8));
                pos += 2;
            }
            _ => {
                let Some(len) = byte(pos) else { break };
                pos += 1;
                let correction = if len & 0x80 != 0 {
                    pos += 1;
                    byte(pos - 1)
                } else {
                    None
                };
                for _ in 0..(len & 0x7f) + 2 {
                    let Some(&a) = ascii.get(out.len()) else {
                        break;
                    };
                    out.push(match correction {
                        Some(c) => (u16::from(a) + c) & 0xff | high,
                        None => u16::from(a),
                    });
                }
            }
        }
        flags <<= 2;
        bits -= 2;
    }
    String::from_utf16_lossy(&out)
}
