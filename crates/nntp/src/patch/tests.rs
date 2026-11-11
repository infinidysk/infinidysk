use std::fs;
use std::path::{Path, PathBuf};

use infinidysk_core::{
    ByteRange, PatchId,
    repair::{Patch, Source},
};
use sha2::{Digest, Sha256};

use super::{
    CHUNK, Error, Store,
    steps::{Fault, Steps},
};

fn root() -> PathBuf {
    let root = std::env::temp_dir().join(format!("infinidysk-patch-crash-{}", PatchId::new()));
    fs::create_dir(&root).expect("test parent");
    root
}

fn bytes() -> Vec<u8> {
    (0..2 * CHUNK + 3)
        .map(|i| ((i + 1025) % 251).to_le_bytes()[0])
        .collect()
}

fn patch(id: &str, bytes: &[u8]) -> Patch {
    Patch {
        id: id.parse().expect("fixed valid UUID"),
        source: Source {
            set: [1; 16],
            file: [2; 16],
            range: ByteRange {
                start: 7000,
                end: 7000 + bytes.len() as u64,
            },
            file_length: 1_000_000,
            message_id: Some("repair@loopback".to_owned()),
            part: Some(2),
            total: Some(100),
        },
        sha256: Sha256::digest(bytes).into(),
    }
}

const OLD: &str = "00000000-0000-0000-0000-000000000001";
const NEW: &str = "00000000-0000-0000-0000-000000000002";

#[test]
fn dropping_the_last_store_releases_its_lock_despite_an_inherited_descriptor() {
    let root = root();
    let path = root.join("repairs");
    let store = Store::open(&path, 1_000_000).unwrap();
    let inherited = store.0.lease.try_clone().unwrap();
    drop(store);
    let reopened = Store::open(&path, 1_000_000).expect("store owner released the lease");
    drop(inherited);
    drop(reopened);
    fs::remove_dir_all(root).unwrap();
}

fn boundaries() -> usize {
    let root = root();
    let store = Store::open(&root.join("repairs"), 10_000_000).expect("store");
    let data = bytes();
    let mut steps = Steps::default();
    store
        .write_with(&patch(NEW, &data), &data, &|| false, &mut steps)
        .expect("write");
    drop(store);
    fs::remove_dir_all(root).expect("cleanup");
    steps.n
}

fn validate_after_failure(path: &Path, data: &[u8]) {
    let store = Store::open(path, 10_000_000).expect("recover store");
    assert_eq!(
        store
            .read(&patch(OLD, b"old bytes"))
            .expect("old data preserved")
            .data
            .as_ref(),
        b"old bytes"
    );
    let new = patch(NEW, data);
    if path.join(format!("{}.patch", new.id)).exists() {
        assert_eq!(
            store
                .read(&new)
                .expect("any durable file is complete")
                .data
                .as_ref(),
            data
        );
    }
    store.write(&new, data, || false).expect("retry succeeds");
    let used = store.used_bytes().expect("accounted");
    store
        .write(&new, data, || false)
        .expect("retry stays idempotent");
    assert_eq!(store.used_bytes().expect("accounted"), used);
    assert!(fs::read_dir(path).expect("listing").all(|entry| {
        !entry
            .expect("entry")
            .file_name()
            .to_string_lossy()
            .ends_with(".part")
    }));
}

#[test]
fn filesystem_errors_at_every_boundary_never_lose_existing_repairs() {
    let data = bytes();
    for at in 0..boundaries() {
        let root = root();
        let path = root.join("repairs");
        let store = Store::open(&path, 10_000_000).unwrap();
        store
            .write(&patch(OLD, b"old bytes"), b"old bytes", || false)
            .unwrap();
        let mut steps = Steps {
            n: 0,
            fault: Some((at, Fault::Error)),
        };
        assert!(
            store
                .write_with(&patch(NEW, &data), &data, &|| false, &mut steps)
                .is_err(),
            "step {at}"
        );
        assert_eq!(
            store.read(&patch(OLD, b"old bytes")).unwrap().data.as_ref(),
            b"old bytes"
        );
        drop(store);
        validate_after_failure(&path, &data);
        fs::remove_dir_all(root).unwrap();
    }
}

#[test]
fn crash_child() {
    let Ok(path) = std::env::var("INFINIDYSK_PATCH_CRASH_DIR") else {
        return;
    };
    let at = std::env::var("INFINIDYSK_PATCH_CRASH_STEP")
        .unwrap()
        .parse()
        .unwrap();
    let store = Store::open(Path::new(&path), 10_000_000).unwrap();
    let data = bytes();
    let mut steps = Steps {
        n: 0,
        fault: Some((at, Fault::Exit)),
    };
    store
        .write_with(&patch(NEW, &data), &data, &|| false, &mut steps)
        .unwrap();
    panic!("did not reach crash step");
}

#[test]
fn process_exit_at_every_boundary_recovers_without_destructors() {
    let data = bytes();
    for at in 0..boundaries() {
        let root = root();
        let path = root.join("repairs");
        let store = Store::open(&path, 10_000_000).unwrap();
        store
            .write(&patch(OLD, b"old bytes"), b"old bytes", || false)
            .unwrap();
        drop(store);
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "patch::tests::crash_child"])
            .env("INFINIDYSK_PATCH_CRASH_DIR", &path)
            .env("INFINIDYSK_PATCH_CRASH_STEP", at.to_string())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .unwrap();
        assert_eq!(status.code(), Some(79), "step {at}");
        validate_after_failure(&path, &data);
        fs::remove_dir_all(root).unwrap();
    }
}

#[test]
fn callback_panic_requires_reopening_but_does_not_lose_old_data() {
    let root = root();
    let path = root.join("repairs");
    let store = Store::open(&path, 10_000_000).unwrap();
    store
        .write(&patch(OLD, b"old bytes"), b"old bytes", || false)
        .unwrap();
    let data = bytes();
    let calls = std::cell::Cell::new(0);
    assert!(
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            store.write(&patch(NEW, &data), &data, || {
                calls.set(calls.get() + 1);
                assert!(calls.get() != 7);
                false
            })
        }))
        .is_err()
    );
    assert!(matches!(
        store.write(&patch(NEW, &data), &data, || false),
        Err(Error::Recovery)
    ));
    assert_eq!(
        store.read(&patch(OLD, b"old bytes")).unwrap().data.as_ref(),
        b"old bytes"
    );
    drop(store);
    validate_after_failure(&path, &data);
    fs::remove_dir_all(root).unwrap();
}
