//! `stored.7z.00*` and `headers-encrypted.7z` were made with `7zz a -mx0`
//! (`-v40000`, and `-mhe=on -p…`) from the `SharpCompress` MIT test files; the
//! other 7z fixtures are `SharpCompress` test archives.

use infinidysk_archive::Error;
use infinidysk_archive::sevenzip::{Entry, Reader, Step};
use infinidysk_yenc::crc32;

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture exists")
}

/// Hands the reader exactly the windows it asks for.
fn read(archive: &[u8]) -> Result<Vec<Entry>, Error> {
    let mut reader = Reader::new();
    let (mut at, mut buf) = (0, &archive[..0]);
    loop {
        match reader.next(at, buf)? {
            Step::Need { offset, len } => {
                let start = usize::try_from(offset).expect("fits");
                let end = start + usize::try_from(len).expect("fits");
                assert!(end <= archive.len(), "asked past the end: {offset}+{len}");
                at = offset;
                buf = &archive[start..end];
            }
            Step::Key(_) => return Err(Error::WrongPassword),
            Step::Done(entries) => return Ok(entries),
        }
    }
}

#[test]
fn stored_split_set_maps_members_across_volumes() {
    let archive: Vec<u8> = (1..=3)
        .flat_map(|i| fixture(&format!("stored.7z.00{i}")))
        .collect();
    let entries = read(&archive).unwrap();
    let mut names: Vec<_> = entries.iter().map(|e| e.name.as_str()).collect();
    names.sort_unstable();
    assert_eq!(names, ["exe/test.exe", "jpg/test.jpg", "тест.txt"]);
    let mut spans_volumes = false;
    for e in &entries {
        let data = e.data.clone().expect("stored");
        assert!(!e.encrypted);
        assert_eq!(data.end - data.start, e.size, "{e:?}");
        let bytes =
            &archive[usize::try_from(data.start).unwrap()..usize::try_from(data.end).unwrap()];
        assert_eq!(Some(crc32(0, bytes)), e.crc32, "{e:?}");
        spans_volumes |= data.start / 40_000 != (data.end - 1) / 40_000;
    }
    assert!(spans_volumes);
}

#[test]
fn compressed_members_have_no_range_and_encrypted_ones_a_key() {
    let lzma = read(&fixture("7Zip.LZMA.7z")).unwrap();
    assert!(!lzma.is_empty() && lzma.iter().all(|e| e.data.is_none() && !e.encrypted));
    let [aes] = read(&fixture("7Zip.Copy.Aes.7z"))
        .unwrap()
        .try_into()
        .expect("one entry");
    assert_eq!(
        (aes.name.as_str(), aes.size, aes.encrypted),
        ("payload.txt", 31, true)
    );
    assert!(aes.data.is_some() && aes.crypt.is_some());
    // `read` answers a key request with this error.
    assert_eq!(
        read(&fixture("headers-encrypted.7z")),
        Err(Error::WrongPassword)
    );
}

#[test]
fn empty_files_are_kept_and_directories_dropped() {
    let entries = read(&fixture("7Zip.EmptyStream.7z")).unwrap();
    let mut names: Vec<_> = entries.iter().map(|e| (e.name.as_str(), e.size)).collect();
    names.sort_unstable();
    assert_eq!(
        names,
        [
            ("000", 0),
            ("001", 0),
            ("NewFolder/A", 0),
            ("NewFolder/B", 0)
        ]
    );
}

#[test]
fn rejects_other_formats() {
    assert_eq!(read(&fixture("Rar5.none.rar")), Err(Error::Signature));
}

#[test]
fn rejects_nested_encoded_header() {
    // An encoded header whose one COPY-coded stream is this same header.
    let header = [
        0x17, 0x06, 0x00, 0x01, 0x09, 0x12, 0x00, 0x07, 0x0b, 0x01, 0x00, 0x01, 0x01, 0x00, 0x0c,
        0x12, 0x00, 0x00,
    ];
    let mut tail = Vec::new();
    tail.extend_from_slice(&0u64.to_le_bytes());
    tail.extend_from_slice(&(header.len() as u64).to_le_bytes());
    tail.extend_from_slice(&crc32(0, &header).to_le_bytes());
    let mut archive = b"7z\xbc\xaf\x27\x1c\x00\x04".to_vec();
    archive.extend_from_slice(&crc32(0, &tail).to_le_bytes());
    archive.extend_from_slice(&tail);
    archive.extend_from_slice(&header);
    assert!(matches!(
        read(&archive),
        Err(Error::Corrupt {
            reason: "nested encoded header",
            ..
        })
    ));
}
