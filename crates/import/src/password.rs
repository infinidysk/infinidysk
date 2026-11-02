//! C# password hashes (ASP.NET Identity v3: PBKDF2-HMAC), accepted at the first sign-in
//! after an upgrade and then replaced by our own.

use base64::Engine as _;
use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
use hmac::{Hmac, KeyInit, Mac};
use sha2::{Sha256, Sha512};

/// Prefix of a stored C# hash: `$csharp$<salt, base64url>$<C# hash>`.
const PREFIX: &str = "$csharp$";
/// Far above C#'s 100 000, low enough that one check stays well under a second.
const MAX_ROUNDS: u32 = 1_000_000;

/// The C# `hash` of a password with `salt` appended, in our stored form.
pub fn legacy_hash(hash: &str, salt: &str) -> String {
    format!("{PREFIX}{}${hash}", URL_SAFE_NO_PAD.encode(salt))
}

/// Whether `stored` is a C# hash, not one of ours.
pub fn is_legacy(stored: &str) -> bool {
    stored.starts_with(PREFIX)
}

/// Whether `password` matches `stored`; `None` when `stored` is not a C# hash.
pub fn verify_legacy(stored: &str, password: &str) -> Option<bool> {
    let (salt, hash) = stored.strip_prefix(PREFIX)?.split_once('$')?;
    let ok = (|| {
        let salt = String::from_utf8(URL_SAFE_NO_PAD.decode(salt).ok()?).ok()?;
        let raw = STANDARD.decode(hash).ok()?;
        let input = format!("{password}{salt}");
        Some(verify_v3(&raw, input.as_bytes()))
    })();
    Some(ok.unwrap_or(false))
}

/// `0x01`, PRF, rounds and salt length (big-endian `u32`s), salt, derived key.
fn verify_v3(raw: &[u8], password: &[u8]) -> bool {
    let field = |at: usize| {
        raw.get(at..at + 4)
            .and_then(|b| b.try_into().ok())
            .map(u32::from_be_bytes)
    };
    let (Some(1), Some(prf), Some(rounds), Some(salt_len)) = (
        raw.first().map(|b| u32::from(*b)),
        field(1),
        field(5),
        field(9),
    ) else {
        return false;
    };
    let Some((salt, key)) = usize::try_from(salt_len)
        .ok()
        .and_then(|n| raw.get(13..)?.split_at_checked(n))
    else {
        return false;
    };
    if rounds == 0 || rounds > MAX_ROUNDS || key.is_empty() {
        return false;
    }
    match prf {
        1 => pbkdf2::<Hmac<Sha256>>(password, salt, rounds, key),
        2 => pbkdf2::<Hmac<Sha512>>(password, salt, rounds, key),
        _ => false,
    }
}

/// PBKDF2's first block compared with `want`; C# keys are never longer than one block.
fn pbkdf2<M: Mac + KeyInit + Clone>(
    password: &[u8],
    salt: &[u8],
    rounds: u32,
    want: &[u8],
) -> bool {
    let Ok(prf) = <M as KeyInit>::new_from_slice(password) else {
        return false;
    };
    let mut mac = prf.clone();
    mac.update(salt);
    mac.update(&1_u32.to_be_bytes());
    let mut u = mac.finalize().into_bytes();
    if want.len() > u.len() {
        return false;
    }
    let mut t = u.clone();
    for _ in 1..rounds {
        let mut mac = prf.clone();
        mac.update(&u);
        u = mac.finalize().into_bytes();
        for (a, b) in t.iter_mut().zip(&u) {
            *a ^= b;
        }
    }
    t.iter().zip(want).fold(0, |acc, (a, b)| acc | (a ^ b)) == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_the_csharp_password_with_its_salt() {
        // PBKDF2 of "hunter2!" + "pepper", 1000 rounds: HMAC-SHA256, then HMAC-SHA512.
        for hash in [
            "AQAAAAEAAAPoAAAAEAABAgMEBQYHCAkKCwwNDg9jYQWw1WgkjgmoogcR/ruPfjli4Hi9Uv0XM22X8Y5naw==",
            "AQAAAAIAAAPoAAAAEAABAgMEBQYHCAkKCwwNDg9mdsgilgY92IByHRHBdX750su/OPfFf2VqMk+b2THxhQ==",
        ] {
            let stored = legacy_hash(hash, "pepper");
            assert_eq!(verify_legacy(&stored, "hunter2!"), Some(true));
            assert_eq!(verify_legacy(&stored, "hunter2?"), Some(false));
        }
        assert_eq!(verify_legacy("$argon2id$v=19$...", "hunter2!"), None);
    }
}
