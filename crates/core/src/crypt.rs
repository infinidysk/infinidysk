//! AES-CBC decryption of encrypted archive members (RAR4, RAR5, 7z). No padding:
//! callers trim to the plaintext length the archive records.

use std::fmt;

use aes::cipher::{Array, BlockCipherDecrypt, KeyInit};
use serde::{Deserialize, Serialize};

pub const BLOCK: usize = 16;

/// A derived archive key. `Debug` never prints the key bytes.
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum Key {
    /// RAR4.
    Aes128([u8; 16]),
    /// RAR5 and 7z.
    Aes256([u8; 32]),
}

impl fmt::Debug for Key {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Aes128(_) => "Aes128(..)",
            Self::Aes256(_) => "Aes256(..)",
        })
    }
}

/// An expanded [`Key`], ready to decrypt.
#[derive(Clone)]
#[allow(clippy::large_enum_variant)] // one per open reader; boxing would add a hop per block
pub enum Cipher {
    Aes128(aes::Aes128),
    Aes256(aes::Aes256),
}

impl Cipher {
    #[must_use]
    pub fn new(key: &Key) -> Self {
        match key {
            Key::Aes128(k) => Self::Aes128(aes::Aes128::new(&Array(*k))),
            Key::Aes256(k) => Self::Aes256(aes::Aes256::new(&Array(*k))),
        }
    }

    /// Decrypts whole blocks of `buf` in place, CBC chained from `iv`, and leaves `iv`
    /// at the last ciphertext block so the next call continues the chain. A trailing
    /// partial block is left untouched.
    pub fn decrypt(&self, iv: &mut [u8; BLOCK], buf: &mut [u8]) {
        const BATCH: usize = 32;
        for chunk in buf.chunks_mut(BATCH * BLOCK) {
            let (blocks, _) = Array::<u8, aes::cipher::consts::U16>::slice_as_chunks_mut(chunk);
            if blocks.is_empty() {
                return;
            }
            let mut cipher = [[0u8; BLOCK]; BATCH];
            for (c, b) in cipher.iter_mut().zip(blocks.iter()) {
                c.copy_from_slice(b);
            }
            match self {
                Self::Aes128(a) => a.decrypt_blocks(blocks),
                Self::Aes256(a) => a.decrypt_blocks(blocks),
            }
            for (i, b) in blocks.iter_mut().enumerate() {
                let prev = if i == 0 { &*iv } else { &cipher[i - 1] };
                for (x, p) in b.iter_mut().zip(prev) {
                    *x ^= p;
                }
            }
            *iv = cipher[blocks.len() - 1];
        }
    }
}

/// Encryption of a [`crate::MultipartFile`]: its parts' bytes laid end to end are one
/// CBC chain, even across RAR volumes.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Crypt {
    pub key: Key,
    pub iv: [u8; BLOCK],
    /// Plaintext bytes; the ciphertext is this rounded up to whole blocks, or more.
    pub size: u64,
}

#[cfg(test)]
mod tests {
    use aes::cipher::BlockCipherEncrypt;

    use super::*;

    #[test]
    fn cbc_decrypts_across_calls_and_batches() {
        let key = [7u8; 32];
        let iv0 = [9u8; BLOCK];
        let plain: Vec<u8> = (0..BLOCK * 70)
            .map(|i| u8::try_from(i * 31 % 251).unwrap())
            .collect();
        let enc = aes::Aes256::new(&Array(key));
        let mut data = plain.clone();
        let mut prev = iv0;
        for block in data.chunks_mut(BLOCK) {
            for (x, p) in block.iter_mut().zip(prev) {
                *x ^= p;
            }
            let b: &mut Array<u8, aes::cipher::consts::U16> =
                block.try_into().expect("whole block");
            enc.encrypt_block(b);
            prev.copy_from_slice(block);
        }
        let cipher = Cipher::new(&Key::Aes256(key));
        let mut iv = iv0;
        let (head, tail) = data.split_at_mut(BLOCK * 3);
        cipher.decrypt(&mut iv, head);
        cipher.decrypt(&mut iv, tail);
        assert_eq!(data, plain);
        assert_eq!(format!("{:?}", Key::Aes256(key)), "Aes256(..)");
    }
}
