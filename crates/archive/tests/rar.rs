use infinidysk_archive::Error;
use infinidysk_archive::rar::{Entry, Scanner, Step, Volume};
use infinidysk_yenc::crc32;

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture exists")
}

struct Scan {
    volume: Volume,
    entries: Vec<Entry>,
    next_volume: bool,
}

/// Drives the scanner. `whole` hands it the full volume once; otherwise it only
/// ever gets exactly the window it asked for, like a caller fetching segments.
fn scan(vol: &[u8], whole: bool) -> Result<Scan, Error> {
    let mut scanner = Scanner::new();
    let mut entries = Vec::new();
    let (mut at, mut buf) = (0, if whole { vol } else { &vol[..0] });
    let mut last = None;
    loop {
        match scanner.next(at, buf)? {
            Step::Need { offset, len } => {
                assert!(
                    !whole,
                    "whole volume given, yet scanner needs {offset}+{len}"
                );
                assert_ne!(last, Some((offset, len)), "scanner stuck at {offset}");
                last = Some((offset, len));
                let start = usize::try_from(offset).expect("fixture offsets fit usize");
                let end = (start + usize::try_from(len).expect("small len")).min(vol.len());
                at = offset;
                buf = &vol[start..end];
            }
            Step::Entry(entry) => {
                last = None;
                entries.push(entry);
            }
            Step::Key(_) => return Err(Error::WrongPassword),
            Step::End { next_volume } => {
                return Ok(Scan {
                    volume: scanner.volume().clone(),
                    entries,
                    next_volume,
                });
            }
        }
    }
}

fn bytes<'a>(vol: &'a [u8], entry: &Entry) -> &'a [u8] {
    let range = entry.data.clone();
    &vol[usize::try_from(range.start).expect("fits")..usize::try_from(range.end).expect("fits")]
}

#[test]
fn stored_members_map_to_their_exact_bytes() {
    for name in ["Rar.none.rar", "Rar5.none.rar"] {
        let vol = fixture(name);
        let scan = scan(&vol, false).unwrap();
        assert_eq!(
            scan.entries,
            self::scan(&vol, true).unwrap().entries,
            "{name}"
        );
        assert!(
            !scan.volume.multi && scan.volume.first && !scan.next_volume,
            "{name}"
        );
        let names: Vec<_> = scan.entries.iter().map(|e| e.name.as_str()).collect();
        assert_eq!(
            names,
            ["exe/test.exe", "jpg/test.jpg", "тест.txt"],
            "{name}"
        );
        for e in &scan.entries {
            assert!(
                e.stored && !e.encrypted && !e.split_before && !e.split_after,
                "{name} {e:?}"
            );
            assert_eq!(e.size, Some(e.data.end - e.data.start), "{name} {e:?}");
            assert_eq!(Some(crc32(0, bytes(&vol, e))), e.crc32, "{name} {e:?}");
        }
    }
}

#[test]
fn rar5_volumes_chain_one_stored_member() {
    let mut crc = 0;
    for (i, start) in [(0u32, 44u64), (1, 45), (2, 49)] {
        let vol = fixture(&format!("Rar5.multi.none.part0{}.rar", i + 1));
        let scan = scan(&vol, false).unwrap();
        assert_eq!(scan.volume.number, Some(i));
        assert_eq!(scan.volume.first, i == 0);
        assert_eq!(scan.next_volume, i < 2);
        let [e] = scan.entries.as_slice() else {
            panic!("one entry per volume")
        };
        assert_eq!(
            (e.name.as_str(), e.data.clone()),
            ("stored.bin", start..start + 100_000)
        );
        assert_eq!((e.split_before, e.split_after), (i > 0, i < 2));
        assert_eq!(e.size, Some(300_000));
        crc = crc32(crc, bytes(&vol, e));
        if i == 2 {
            assert_eq!(e.crc32, Some(crc));
        }
    }
}

#[test]
fn rar4_volumes_number_themselves_in_the_end_header() {
    for i in 0..7u32 {
        let vol = fixture(&format!("Rar4.multi.part0{}.rar", i + 1));
        let scan = scan(&vol, false).unwrap();
        assert_eq!((scan.volume.multi, scan.volume.first), (true, i == 0));
        assert_eq!((scan.volume.number, scan.next_volume), (Some(i), i < 6));
        assert!(scan.entries.iter().all(|e| !e.stored));
        if i < 2 {
            let e = &scan.entries[0];
            assert_eq!(e.name, "exe/test.exe");
            assert_eq!(e.data, if i == 0 { 69..69 + 9911 } else { 69..69 + 8126 });
            assert_eq!((e.split_before, e.split_after), (i == 1, i == 0));
            if i == 0 {
                assert_eq!(e.crc32, Some(crc32(0, bytes(&vol, e))));
            }
        }
    }
}

#[test]
fn compressed_members_are_flagged() {
    let scan = scan(&fixture("Rar.rar"), false).unwrap();
    assert!(scan.entries.iter().any(|e| e.name == "тест.txt"));
    assert!(scan.entries.iter().all(|e| !e.stored));
}

#[test]
fn encryption_is_reported() {
    // `scan` answers a key request with this error.
    for name in [
        "Rar.encrypted_filesAndHeader.rar",
        "Rar5.encrypted_filesAndHeader.rar",
    ] {
        assert_eq!(
            scan(&fixture(name), false).err(),
            Some(Error::WrongPassword),
            "{name}"
        );
    }
    for name in [
        "Rar.encrypted_filesOnly.rar",
        "Rar5.encrypted_filesOnly.rar",
    ] {
        let scan = scan(&fixture(name), false).unwrap();
        assert_eq!(scan.entries.len(), 3, "{name}");
        assert!(
            scan.entries
                .iter()
                .all(|e| e.encrypted && e.crypt.is_some()),
            "{name}"
        );
    }
}

#[test]
fn rejects_non_rar_and_corrupt_headers() {
    assert_eq!(
        scan(b"PK\x03\x04 not a rar", true).err(),
        Some(Error::Signature)
    );
    let mut vol = fixture("Rar5.none.rar");
    vol[20] ^= 0xff;
    assert!(matches!(scan(&vol, true), Err(Error::HeaderCrc(_))));
}
