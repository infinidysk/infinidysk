//! `aes-*` fixtures hold one stored 9001-byte member, `data.bin`, made from
//! [`plain`]. RAR5 with `rar` 7.23, RAR4 with `rar` 6.24 (`-m0 -ptest`, `-hp` for
//! encrypted headers, `-v4k` for volumes; `aes-4long` uses [`LONG`]); 7z with
//! `7zz a -mx0 -ptest` (`-mhe=on`, `-v4k`).

use infinidysk_archive::crypt::{Cipher, Encryption};
use infinidysk_archive::{Error, rar, sevenzip};

const LONG: &str = "correct-horse-battery-staple-0123456789";

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture exists")
}

fn plain() -> Vec<u8> {
    (0..9001u32)
        .map(|i| ((i * 7 + i / 251) & 0xff) as u8)
        .collect()
}

fn scan(vol: &[u8], password: &str) -> Result<Vec<rar::Entry>, Error> {
    let mut scanner = rar::Scanner::new();
    let mut entries = Vec::new();
    loop {
        match scanner.next(0, vol)? {
            rar::Step::Need { offset, .. } if offset >= vol.len() as u64 => return Ok(entries),
            rar::Step::Need { offset, len } => panic!("whole volume given: {offset}+{len}"),
            rar::Step::Entry(e) => entries.push(e),
            rar::Step::Key(kdf) => {
                let derived = kdf.derive(password)?;
                scanner.add_key(kdf, derived);
            }
            rar::Step::End { .. } => return Ok(entries),
        }
    }
}

fn decrypt(crypt: &Encryption, password: &str, cipher_text: &[u8]) -> Result<Vec<u8>, Error> {
    let derived = crypt.kdf.derive(password)?;
    let mut out = cipher_text.to_vec();
    Cipher::new(&derived.key).decrypt(&mut crypt.iv(&derived), &mut out);
    Ok(out)
}

fn slice(vol: &[u8], range: std::ops::Range<u64>) -> &[u8] {
    &vol[usize::try_from(range.start).expect("fits")..usize::try_from(range.end).expect("fits")]
}

/// Decrypts the member across `volumes`: its packed data split across volumes at any
/// byte is one CBC chain, keyed by the first volume's header.
fn rar_member(volumes: &[&str], password: &str) -> Result<Vec<u8>, Error> {
    let mut cipher_text = Vec::new();
    let (mut first, mut size) = (None, 0);
    for name in volumes {
        let vol = fixture(name);
        let [e] = scan(&vol, password)?
            .try_into()
            .expect("one entry per volume");
        assert!(e.stored && e.encrypted, "{name}");
        first.get_or_insert_with(|| e.crypt.expect("AES"));
        cipher_text.extend_from_slice(slice(&vol, e.data.clone()));
        size = e.size.expect("size");
    }
    assert_eq!(cipher_text.len() % 16, 0);
    let mut out = decrypt(&first.expect("a volume"), password, &cipher_text)?;
    assert!(out.len() as u64 - size < 16);
    out.truncate(usize::try_from(size).expect("fits"));
    Ok(out)
}

#[test]
fn rar_members_decrypt_per_volume() {
    for set in [
        &["aes-5.rar"][..],
        &["aes-5hp.rar"],
        &["aes-5v.part1.rar", "aes-5v.part2.rar", "aes-5v.part3.rar"],
        &[
            "aes-5hpv.part1.rar",
            "aes-5hpv.part2.rar",
            "aes-5hpv.part3.rar",
        ],
        &["aes-4.rar"],
        &["aes-4hp.rar"],
        &["aes-4v.part1.rar", "aes-4v.part2.rar", "aes-4v.part3.rar"],
        &[
            "aes-4hpv.part1.rar",
            "aes-4hpv.part2.rar",
            "aes-4hpv.part3.rar",
        ],
    ] {
        assert_eq!(rar_member(set, "test").unwrap(), plain(), "{set:?}");
    }
    assert_eq!(rar_member(&["aes-4long.rar"], LONG).unwrap(), plain());
}

#[test]
fn wrong_passwords_are_caught_where_the_archive_allows() {
    for name in ["aes-5.rar", "aes-5hp.rar", "aes-4hp.rar"] {
        assert_eq!(
            rar_member(&[name], "nope").err(),
            Some(Error::WrongPassword),
            "{name}"
        );
    }
    // RAR4 file data carries no check: the caller has to look at the plaintext.
    assert_ne!(rar_member(&["aes-4.rar"], "nope").unwrap(), plain());
}

fn read7(archive: &[u8], password: &str) -> Result<Vec<sevenzip::Entry>, Error> {
    let mut reader = sevenzip::Reader::new();
    loop {
        match reader.next(0, archive)? {
            sevenzip::Step::Need { offset, len } => panic!("whole archive given: {offset}+{len}"),
            sevenzip::Step::Key(kdf) => {
                let derived = kdf.derive(password)?;
                reader.add_key(kdf, derived);
            }
            sevenzip::Step::Done(entries) => return Ok(entries),
        }
    }
}

#[test]
fn sevenzip_members_decrypt_in_place() {
    let split: Vec<u8> = (1..=3)
        .flat_map(|i| fixture(&format!("aes-7v.7z.00{i}")))
        .collect();
    for archive in [fixture("aes-7.7z"), fixture("aes-7he.7z"), split] {
        let [e] = read7(&archive, "test")
            .unwrap()
            .try_into()
            .expect("one entry");
        let data = slice(&archive, e.data.expect("stored"));
        let mut out = decrypt(&e.crypt.expect("AES"), "test", data).unwrap();
        out.truncate(usize::try_from(e.size).expect("fits"));
        assert_eq!(out, plain());
    }
    assert_eq!(
        read7(&fixture("aes-7he.7z"), "nope").err(),
        Some(Error::WrongPassword)
    );
}
