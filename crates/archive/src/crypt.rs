//! Password key derivation for encrypted RAR4, RAR5 and 7z archives.
//!
//! Parsers report a [`Kdf`]; deriving its key is CPU-heavy (up to 2^24 hash rounds),
//! so callers run [`Kdf::derive`] off the async runtime and cache the result per
//! [`Kdf`]: every volume of a set usually shares one salt.

use hmac::{Hmac, KeyInit, Mac};
pub use infinidysk_core::crypt::{BLOCK, Cipher, Key};
use sha1::block_api::compress;
use sha2::{Digest, Sha256};

use std::sync::atomic::{AtomicBool, Ordering};

use crate::Error;

/// Highest KDF cost accepted: RAR5's own limit and 7-Zip's.
pub const MAX_LG2: u8 = 24;

/// How a key is derived from the password.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum Kdf {
    /// PBKDF2-HMAC-SHA256 over the UTF-8 password, 2^`lg2` rounds.
    Rar5 {
        salt: [u8; 16],
        lg2: u8,
        /// Password check value, when the archive stores one.
        check: Option<[u8; 8]>,
    },
    /// The RAR 2.9 SHA-1 schedule over the UTF-16LE password; also yields the IV.
    Rar4 { salt: Option<[u8; 8]> },
    /// SHA-256 over salt and UTF-16LE password, 2^`cycles` rounds.
    SevenZip { salt: Vec<u8>, cycles: u8 },
}

/// A derived key, plus the IV when the KDF produces it (RAR4).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Derived {
    pub key: Key,
    pub iv: Option<[u8; BLOCK]>,
}

/// An encrypted member's or header's parameters.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Encryption {
    pub kdf: Kdf,
    /// The IV the archive stores; RAR4 takes it from [`Derived::iv`] instead.
    pub iv: Option<[u8; BLOCK]>,
}

impl Encryption {
    /// The IV to decrypt with, given this encryption's derived key.
    pub fn iv(&self, derived: &Derived) -> [u8; BLOCK] {
        self.iv.or(derived.iv).unwrap_or_default()
    }
}

impl Kdf {
    /// Derives the key for `password`. Fails with [`Error::WrongPassword`] when the
    /// archive's password check disagrees.
    pub fn derive(&self, password: &str) -> Result<Derived, Error> {
        self.derive_until(password, &AtomicBool::new(false))
    }

    /// Like [`Kdf::derive`], but fails with [`Error::Cancelled`] soon after `stop` is set,
    /// so a caller that no longer wants the key does not wait out every round.
    pub fn derive_until(&self, password: &str, stop: &AtomicBool) -> Result<Derived, Error> {
        match self {
            Self::Rar5 { salt, lg2, check } => {
                rar5(rar_password(password), salt, *lg2, check.as_ref(), stop)
            }
            Self::Rar4 { salt } => rar4(rar_password(password), salt.as_ref(), stop),
            Self::SevenZip { salt, cycles } => Ok(Derived {
                key: Key::Aes256(sevenzip(password, salt, *cycles, stop)?),
                iv: None,
            }),
        }
    }
}

/// The first 127 characters, as unrar keys RAR archives (`MAXPASSWORD_RAR - 1` 32-bit `wchar_t`s).
fn rar_password(password: &str) -> &str {
    password
        .char_indices()
        .nth(127)
        .map_or(password, |(end, _)| &password[..end])
}

fn rar5(
    password: &str,
    salt: &[u8; 16],
    lg2: u8,
    check: Option<&[u8; 8]>,
    stop: &AtomicBool,
) -> Result<Derived, Error> {
    let mac = Hmac::<Sha256>::new_from_slice(password.as_bytes())
        .expect("HMAC accepts keys of any length");
    let prf = |data: &[u8]| -> [u8; 32] {
        let mut m = mac.clone();
        m.update(data);
        m.finalize().into_bytes().into()
    };
    let mut first = [0u8; 20];
    first[..16].copy_from_slice(salt);
    first[19] = 1;
    let mut u = prf(&first);
    let mut f = u;
    // The key after 2^lg2 rounds, the hash key 16 rounds later, the check 16 after that.
    let mut values = [[0u8; 32]; 3];
    for (value, rounds) in values.iter_mut().zip([(1u32 << lg2) - 1, 16, 16]) {
        for _ in 0..rounds {
            stopped(stop)?;
            u = prf(&u);
            for (x, y) in f.iter_mut().zip(&u) {
                *x ^= y;
            }
        }
        *value = f;
    }
    if let Some(check) = check {
        let mut folded = [0u8; 8];
        for (i, b) in values[2].iter().enumerate() {
            folded[i % 8] ^= b;
        }
        if &folded != check {
            return Err(Error::WrongPassword);
        }
    }
    Ok(Derived {
        key: Key::Aes256(values[0]),
        iv: None,
    })
}

fn utf16le(password: &str) -> Vec<u8> {
    password.encode_utf16().flat_map(u16::to_le_bytes).collect()
}

fn rar4(password: &str, salt: Option<&[u8; 8]>, stop: &AtomicBool) -> Result<Derived, Error> {
    const ROUNDS: u32 = 0x40000;
    let mut raw = utf16le(password);
    raw.extend(salt.into_iter().flatten());
    let mut sha = Sha1Rar29::new();
    let mut iv = [0u8; BLOCK];
    for i in 0..ROUNDS {
        stopped(stop)?;
        sha.update(&mut raw);
        let [a, b, c, _] = i.to_le_bytes();
        sha.update(&mut [a, b, c]);
        if i % (ROUNDS / 16) == 0 {
            iv[(i / (ROUNDS / 16)) as usize] = sha.digest()[4].to_le_bytes()[0];
        }
    }
    let digest = sha.digest();
    let mut key = [0u8; 16];
    for (k, word) in key.as_chunks_mut::<4>().0.iter_mut().zip(&digest) {
        *k = word.to_le_bytes();
    }
    Ok(Derived {
        key: Key::Aes128(key),
        iv: Some(iv),
    })
}

/// SHA-1 as RAR 2.9 implemented it: each 64-byte block hashed straight from the input
/// is overwritten with the end of its message schedule, so long passwords (29+
/// characters) change between rounds.
#[derive(Clone)]
struct Sha1Rar29 {
    state: [u32; 5],
    buf: [u8; 64],
    count: u64,
}

impl Sha1Rar29 {
    const fn new() -> Self {
        Self {
            state: [
                0x6745_2301,
                0xEFCD_AB89,
                0x98BA_DCFE,
                0x1032_5476,
                0xC3D2_E1F0,
            ],
            buf: [0; 64],
            count: 0,
        }
    }

    fn update(&mut self, data: &mut [u8]) {
        let mut j = (self.count % 64) as usize;
        self.count += data.len() as u64;
        let mut i = 0;
        if j + data.len() > 63 {
            i = 64 - j;
            self.buf[j..].copy_from_slice(&data[..i]);
            compress(&mut self.state, &[self.buf]);
            while i + 64 <= data.len() {
                let block: [u8; 64] = data[i..i + 64].try_into().expect("64-byte slice");
                compress(&mut self.state, &[block]);
                data[i..i + 64].copy_from_slice(&schedule_tail(&block));
                i += 64;
            }
            j = 0;
        }
        self.buf[j..j + data.len() - i].copy_from_slice(&data[i..]);
    }

    fn digest(&self) -> [u32; 5] {
        let mut c = self.clone();
        let bits = c.count * 8;
        let used = (c.count % 64) as usize;
        let mut pad = vec![0x80];
        pad.resize(if used < 56 { 56 - used } else { 120 - used }, 0);
        pad.extend(bits.to_be_bytes());
        c.update(&mut pad);
        c.state
    }
}

/// Words 64..80 of a block's SHA-1 message schedule, little-endian.
fn schedule_tail(block: &[u8; 64]) -> [u8; 64] {
    let mut w = [0u32; 80];
    for (w, b) in w.iter_mut().zip(block.as_chunks::<4>().0) {
        *w = u32::from_be_bytes(*b);
    }
    for t in 16..80 {
        w[t] = (w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16]).rotate_left(1);
    }
    let mut out = [0u8; 64];
    for (o, w) in out.as_chunks_mut::<4>().0.iter_mut().zip(&w[64..]) {
        *o = w.to_le_bytes();
    }
    out
}

fn sevenzip(password: &str, salt: &[u8], cycles: u8, stop: &AtomicBool) -> Result<[u8; 32], Error> {
    let pw = utf16le(password);
    let mut key = [0u8; 32];
    if cycles == 0x3f {
        for (k, b) in key.iter_mut().zip(salt.iter().chain(&pw)) {
            *k = *b;
        }
        return Ok(key);
    }
    let mut sha = Sha256::new();
    for i in 0..1u64 << cycles {
        stopped(stop)?;
        sha.update(salt);
        sha.update(&pw);
        sha.update(i.to_le_bytes());
    }
    key.copy_from_slice(&sha.finalize());
    Ok(key)
}

/// One relaxed load per round: the flag guards no data, and a round's hashing dwarfs it.
fn stopped(stop: &AtomicBool) -> Result<(), Error> {
    if stop.load(Ordering::Relaxed) {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rar29_sha1_matches_sha1_for_short_inputs() {
        let mut sha = Sha1Rar29::new();
        sha.update(&mut b"abc".to_vec());
        let words = sha.digest();
        let bytes: Vec<u8> = words.iter().flat_map(|w| w.to_be_bytes()).collect();
        assert_eq!(bytes, sha1::Sha1::digest(b"abc").to_vec());
    }

    #[test]
    fn rar_passwords_keep_their_first_127_characters() {
        // 129 characters, multibyte in UTF-8 and UTF-16; the 126- and 127-character prefixes differ.
        let long = format!("{}abcdefghi", "\u{e9}".repeat(120));
        let prefix = |n: usize| -> String { long.chars().take(n).collect() };
        let salt = [7u8; 16];
        let rar5 = |password: &str| {
            Kdf::Rar5 {
                salt,
                lg2: 4,
                check: None,
            }
            .derive(password)
            .unwrap()
        };
        assert_eq!(rar5(&long), rar5(&prefix(127)));
        assert_ne!(rar5(&long), rar5(&prefix(126)));

        let rar4 = |password: &str| Kdf::Rar4 { salt: Some([3; 8]) }.derive(password).unwrap();
        assert_eq!(rar4(&long), rar4(&prefix(127)));
        assert_ne!(rar4(&long), rar4(&prefix(126)));

        // 127 characters but 128 UTF-16 units: kept whole.
        let emoji = format!("{}\u{1F600}", "a".repeat(126));
        assert_ne!(rar5(&emoji), rar5(&"a".repeat(126)));

        let sevenzip = |password: &str| {
            Kdf::SevenZip {
                salt: vec![],
                cycles: 4,
            }
            .derive(password)
            .unwrap()
        };
        assert_ne!(sevenzip(&long), sevenzip(&prefix(127)));
    }

    #[test]
    fn a_stopped_derivation_gives_up() {
        let stopped = AtomicBool::new(true);
        for kdf in [
            Kdf::Rar5 {
                salt: [0; 16],
                lg2: 4,
                check: None,
            },
            Kdf::Rar4 { salt: None },
            Kdf::SevenZip {
                salt: vec![],
                cycles: 4,
            },
        ] {
            assert_eq!(
                kdf.derive_until("secret", &stopped),
                Err(Error::Cancelled),
                "{kdf:?}"
            );
        }

        // 2^24 rounds over a long password take minutes; the flag ends it mid-way.
        let stop = std::sync::Arc::new(AtomicBool::new(false));
        let worker = std::thread::spawn({
            let stop = std::sync::Arc::clone(&stop);
            move || {
                Kdf::SevenZip {
                    salt: vec![],
                    cycles: 24,
                }
                .derive_until(&"p".repeat(4096), &stop)
            }
        });
        std::thread::sleep(std::time::Duration::from_millis(50));
        stop.store(true, Ordering::Relaxed);
        assert_eq!(worker.join().unwrap(), Err(Error::Cancelled));
    }
}
