use std::fs;

use infinidysk_core::{
    ByteRange, PatchId,
    repair::{Patch, Source},
};
use infinidysk_nntp::patch::{Error, Store};
use sha2::{Digest, Sha256};

fn descriptor(bytes: &[u8]) -> Patch {
    Patch {
        id: PatchId::new(),
        source: Source {
            set: [1; 16],
            file: [2; 16],
            range: ByteRange {
                start: 7000,
                end: 7000 + bytes.len() as u64,
            },
            file_length: 20_000_000_000,
            message_id: Some("part@loopback".to_owned()),
            part: Some(2),
            total: Some(100_000),
        },
        sha256: Sha256::digest(bytes).into(),
    }
}

#[test]
fn persists_only_the_repaired_article_and_verifies_it_after_reopening() {
    let root = std::env::temp_dir().join(format!("infinidysk-patch-{}", PatchId::new()));
    fs::create_dir(&root).unwrap();
    let path = root.join("repairs");
    let data = b"only this repaired article";
    let patch = descriptor(data);
    let store = Store::open(&path, 1_000_000).unwrap();
    let durable = store.write(&patch, data, || false).unwrap();
    assert_eq!(durable.patch(), &patch);
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), data);
    assert!(store.used_bytes().unwrap() < 10_000);
    drop(durable);
    drop(store);
    let store = Store::open(&path, 1_000_000).unwrap();
    let body = store.read(&patch).unwrap();
    assert_eq!(body.offset, 7000);
    assert_eq!(body.file_size, 20_000_000_000);
    assert_eq!(body.part, Some(2));
    assert_eq!(body.total, Some(100_000));
    assert_eq!(body.data.as_ref(), data);
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

fn directory() -> std::path::PathBuf {
    let root = std::env::temp_dir().join(format!("infinidysk-patch-{}", PatchId::new()));
    fs::create_dir(&root).expect("private test parent");
    root
}

#[test]
fn cleanup_keeps_pinned_patches_and_updates_capacity_without_invalidating_owned_bytes() {
    let root = directory();
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let a = descriptor(b"kept");
    let b = descriptor(b"removed");
    store.write(&a, b"kept", || false).unwrap();
    store.write(&b, b"removed", || false).unwrap();
    let owned = store.read(&b).unwrap();
    let before = store.used_bytes().unwrap();
    assert_eq!(
        store
            .prune(|id| Ok((id != a.id).then_some(())), || false)
            .unwrap(),
        1
    );
    assert!(store.used_bytes().unwrap() < before);
    assert_eq!(store.read(&a).unwrap().data.as_ref(), b"kept");
    assert!(store.read(&b).is_err());
    assert_eq!(owned.data.as_ref(), b"removed");
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn restore_heals_a_corrupt_payload_without_overwriting_a_different_valid_identity() {
    use std::io::{Seek as _, SeekFrom, Write as _};
    let root = directory();
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let patch = descriptor(b"bytes");
    store.write(&patch, b"bytes", || false).unwrap();
    let mut file = fs::OpenOptions::new()
        .write(true)
        .open(root.join("repairs").join(format!("{}.patch", patch.id)))
        .unwrap();
    file.seek(SeekFrom::End(-1)).unwrap();
    file.write_all(&[0xff]).unwrap();
    drop(file);
    store.restore(&patch, b"bytes", || false).unwrap();
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), b"bytes");
    let mut conflicting = patch.clone();
    conflicting.sha256 = Sha256::digest(b"other").into();
    assert!(store.restore(&conflicting, b"other", || false).is_err());
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), b"bytes");
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn retries_are_idempotent_and_capacity_never_evicts_existing_repairs() {
    let root = directory();
    let path = root.join("repairs");
    let data = b"verified bytes";
    let patch = descriptor(data);
    let length = patch.encode().unwrap().len() as u64 + data.len() as u64;
    let store = Store::open(&path, length).unwrap();
    store.write(&patch, data, || false).unwrap();
    store.write(&patch, data, || false).unwrap();
    assert_eq!(store.used_bytes().unwrap(), length);
    assert!(matches!(
        store.write(&descriptor(data), data, || false),
        Err(Error::Capacity)
    ));
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), data);
    drop(store);
    let store = Store::open(&path, 1).unwrap();
    assert_eq!(store.used_bytes().unwrap(), length);
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), data);
    store.write(&patch, data, || false).unwrap();
    assert!(matches!(
        store.write(&descriptor(data), data, || false),
        Err(Error::Capacity)
    ));
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn a_limited_writer_shares_the_reader_lease_and_global_usage() {
    let root = directory();
    let wide = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let bytes = b"verified bytes";
    let first = descriptor(bytes);
    let length = first.encode().unwrap().len() as u64 + bytes.len() as u64;
    let limited = wide.limited(length).unwrap();
    limited.write(&first, bytes, || false).unwrap();
    assert_eq!(wide.used_bytes().unwrap(), length);
    assert_eq!(wide.read(&first).unwrap().data.as_ref(), bytes);
    assert!(matches!(
        limited
            .limited(u64::MAX)
            .unwrap()
            .write(&descriptor(bytes), bytes, || false),
        Err(Error::Capacity)
    ));
    assert!(matches!(wide.limited(0), Err(Error::Capacity)));
    drop(limited);
    drop(wide);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn immutable_id_collisions_and_unverified_input_do_not_change_old_bytes() {
    let root = directory();
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let data = b"verified bytes";
    let patch = descriptor(data);
    store.write(&patch, data, || false).unwrap();
    let used = store.used_bytes().unwrap();
    let mut other = patch.clone();
    other.source.file[0] ^= 1;
    assert!(store.write(&other, data, || false).is_err());
    assert!(store.write(&patch, b"wrong bytes", || false).is_err());
    other = descriptor(data);
    other.sha256[0] ^= 1;
    assert!(store.write(&other, data, || false).is_err());
    assert_eq!(store.used_bytes().unwrap(), used);
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), data);
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn corrupt_metadata_payloads_truncation_and_extra_bytes_are_rejected() {
    let root = directory();
    let dir = root.join("repairs");
    let store = Store::open(&dir, 1_000_000).unwrap();
    let patch = descriptor(b"verified bytes");
    store.write(&patch, b"verified bytes", || false).unwrap();
    let path = dir.join(format!("{}.patch", patch.id));
    let original = fs::read(&path).unwrap();
    for at in [0, 4, 18, original.len() - 1] {
        let mut damaged = original.clone();
        damaged[at] ^= 1;
        fs::write(&path, damaged).unwrap();
        assert!(store.read(&patch).is_err());
    }
    fs::write(&path, &original[..original.len() - 1]).unwrap();
    assert!(store.read(&patch).is_err());
    let mut overlong = original.clone();
    overlong.push(0);
    fs::write(&path, overlong).unwrap();
    assert!(store.read(&patch).is_err());
    fs::write(&path, original).unwrap();
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), b"verified bytes");
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn temporary_recovery_preserves_durable_files_and_excludes_other_owners() {
    let root = directory();
    let path = root.join("repairs");
    let store = Store::open(&path, 1_000_000).unwrap();
    let patch = descriptor(b"verified bytes");
    store.write(&patch, b"verified bytes", || false).unwrap();
    let copy = store.clone();
    assert!(matches!(Store::open(&path, 1_000_000), Err(Error::Busy)));
    drop(store);
    assert!(matches!(Store::open(&path, 1_000_000), Err(Error::Busy)));
    drop(copy);
    let part = path.join(format!("{}.part", PatchId::new()));
    fs::write(&part, b"interrupted data").unwrap();
    let store = Store::open(&path, 1_000_000).unwrap();
    assert!(!part.exists());
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), b"verified bytes");
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn symlinks_cannot_redirect_store_or_payload_access() {
    use std::os::unix::fs::symlink;
    let root = directory();
    let path = root.join("repairs");
    let store = Store::open(&path, 1_000_000).unwrap();
    let alias = root.join("alias");
    symlink(&path, &alias).unwrap();
    assert!(Store::open(&alias, 1_000_000).is_err());
    let patch = descriptor(b"verified bytes");
    store.write(&patch, b"verified bytes", || false).unwrap();
    let outside = root.join("outside.bin");
    fs::write(&outside, b"outside data").unwrap();
    let payload = path.join(format!("{}.patch", patch.id));
    fs::remove_file(&payload).unwrap();
    symlink(&outside, &payload).unwrap();
    assert!(store.read(&patch).is_err());
    assert_eq!(fs::read(&outside).unwrap(), b"outside data");
    drop(store);
    assert!(Store::open(&path, 1_000_000).is_err());
    assert_eq!(fs::read(&outside).unwrap(), b"outside data");
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn permissions_omitted_identity_and_cancellation_preserve_the_store() {
    use std::cell::Cell;
    use std::os::unix::fs::PermissionsExt as _;
    let root = directory();
    let path = root.join("repairs");
    let store = Store::open(&path, 1_000_000).unwrap();
    let data = vec![7; 2 * 64 * 1024 + 3];
    let mut patch = descriptor(&data);
    patch.source.message_id = None;
    patch.source.part = None;
    patch.source.total = None;
    let calls = Cell::new(0);
    assert!(matches!(
        store.write(&patch, &data, || {
            calls.set(calls.get() + 1);
            calls.get() == 7
        }),
        Err(Error::Cancelled)
    ));
    assert_eq!(store.used_bytes().unwrap(), 0);
    store.write(&patch, &data, || false).unwrap();
    assert_eq!(
        fs::metadata(&path).unwrap().permissions().mode() & 0o777,
        0o700
    );
    assert_eq!(
        fs::metadata(path.join(format!("{}.patch", patch.id)))
            .unwrap()
            .permissions()
            .mode()
            & 0o777,
        0o600
    );
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), data);
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn concurrent_writes_share_one_capacity_reservation() {
    use std::sync::{Arc, Barrier};
    let root = directory();
    let path = root.join("repairs");
    let first = descriptor(b"verified bytes");
    let capacity = first.encode().unwrap().len() as u64 + 14;
    let store = Store::open(&path, capacity).unwrap();
    let barrier = Arc::new(Barrier::new(2));
    let threads: Vec<_> = [first, descriptor(b"verified bytes")]
        .into_iter()
        .map(|patch| {
            let store = store.clone();
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                barrier.wait();
                store.write(&patch, b"verified bytes", || false)
            })
        })
        .collect();
    let results: Vec<_> = threads
        .into_iter()
        .map(|thread| thread.join().unwrap())
        .collect();
    assert_eq!(results.iter().filter(|r| r.is_ok()).count(), 1);
    assert_eq!(
        results
            .iter()
            .filter(|r| matches!(r, Err(Error::Capacity)))
            .count(),
        1
    );
    assert_eq!(store.used_bytes().unwrap(), capacity);
    drop(store);
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn descriptor_versions_and_bounds_are_checked_before_storage() {
    use infinidysk_core::repair::{self, PREFIX_LEN};
    let patch = descriptor(b"verified bytes");
    let header = patch.encode().unwrap();
    assert_eq!(Patch::decode(&header).unwrap(), patch);
    for length in 0..header.len() {
        assert!(Patch::decode(&header[..length]).is_err());
    }
    let mut future = header.clone();
    future[4..6].copy_from_slice(&2u16.to_le_bytes());
    assert!(matches!(
        Patch::decode(&future),
        Err(repair::Error::Version(2))
    ));
    let mut huge = header[..PREFIX_LEN].to_vec();
    huge[6..10].copy_from_slice(&u32::MAX.to_le_bytes());
    assert!(repair::header_length(&huge).is_err());
    for invalid in 0..5 {
        let mut other = patch.clone();
        match invalid {
            0 => other.source.range.end = other.source.range.start,
            1 => other.source.range.end = other.source.file_length + 1,
            2 => other.source.part = Some(0),
            3 => other.source.message_id = Some(String::new()),
            _ => other.source.message_id = Some("x".repeat(4097)),
        }
        assert!(other.encode().is_err());
    }
}

#[test]
fn a_durable_receipt_pins_unpublished_bytes_until_publication_finishes() {
    let root = directory();
    let store = Store::open(&root.join("repairs"), 1_000_000).unwrap();
    let patch = descriptor(b"verified bytes");
    let receipt = store.write(&patch, b"verified bytes", || false).unwrap();
    assert_eq!(store.prune(|_| Ok(Some(())), || false).unwrap(), 0);
    assert_eq!(store.read(&patch).unwrap().data.as_ref(), b"verified bytes");
    drop(receipt);
    assert_eq!(store.prune(|_| Ok(Some(())), || false).unwrap(), 1);
    drop(store);
    fs::remove_dir_all(root).unwrap();
}
