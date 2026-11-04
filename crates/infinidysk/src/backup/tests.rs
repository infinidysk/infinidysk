use std::fs;
use std::os::unix::fs::PermissionsExt as _;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use infinidysk_core::ItemId;

use super::files::*;

fn scratch() -> PathBuf {
    let dir = std::env::temp_dir().join(format!("infinidysk-backup-{}", ItemId::new()));
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn mode(path: &Path) -> u32 {
    fs::metadata(path).unwrap().permissions().mode() & 0o777
}

/// A finished part file holding `bytes`, like a backup ready to publish.
fn finished(dir: &Path, name: &str, bytes: &[u8]) -> Part {
    use std::io::Write as _;
    let (part, mut file) = Part::create(dir.join(format!(".{name}.part"))).unwrap();
    file.write_all(bytes).unwrap();
    part
}

fn manifest(kind: Kind, created_ms: i64) -> Manifest {
    Manifest {
        format: MANIFEST_FORMAT,
        kind,
        created_ms,
        ..Manifest::default()
    }
}

fn entry(name: &str, kind: Kind, created_ms: i64) -> Entry {
    Entry {
        name: name.to_owned(),
        path: PathBuf::from(name),
        size_bytes: 10,
        incomplete: false,
        manifest: (kind != Kind::Added).then(|| manifest(kind, created_ms)),
        mtime_ms: created_ms,
    }
}

#[tokio::test]
async fn header_reads_page_size_and_count() {
    use sqlx::ConnectOptions as _;
    let dir = scratch();
    let path = dir.join("real.sqlite");
    let mut conn = sqlx::sqlite::SqliteConnectOptions::new()
        .filename(&path)
        .create_if_missing(true)
        .connect()
        .await
        .unwrap();
    sqlx::query("CREATE TABLE t (x BLOB)")
        .execute(&mut conn)
        .await
        .unwrap();
    sqlx::query("INSERT INTO t VALUES (zeroblob(20000))")
        .execute(&mut conn)
        .await
        .unwrap();
    sqlx::Connection::close(conn).await.unwrap();
    let bytes = fs::read(&path).unwrap();
    let mut head: [u8; 100] = bytes[..100].try_into().unwrap();
    assert_eq!(header(&head), Some((4096, bytes.len() as u64 / 4096)));
    assert_eq!(header(&[0; 100]), None);
    head[16..18].copy_from_slice(&1u16.to_be_bytes());
    assert_eq!(header(&head).map(|(page, _)| page), Some(65_536));
    head[92] ^= 0xff;
    assert_eq!(header(&head).map(|(_, pages)| pages), Some(0));
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn names_follow_the_rule() {
    assert!(valid_name("infinidysk-20261103T030012Z-manual.sqlite"));
    assert!(valid_name("my copy.sqlite"));
    for bad in [
        "../x.sqlite",
        ".x.sqlite",
        "a/b.sqlite",
        "x.db",
        "a\"b.sqlite",
        ".sqlite",
    ] {
        assert!(!valid_name(bad), "{bad}");
    }
    let long = format!("{}.sqlite", "a".repeat(201 - ".sqlite".len()));
    assert_eq!(long.len(), 201);
    assert!(!valid_name(&long));
    assert!(valid_name(&long[1..]));
}

#[test]
fn prune_keeps_the_newest_scheduled_and_manual() {
    let mut pinned = entry("p.sqlite", Kind::Manual, 1);
    if let Some(m) = pinned.manifest.as_mut() {
        m.pinned = true;
    }
    let mut incomplete = entry("i.sqlite", Kind::Scheduled, 0);
    incomplete.incomplete = true;
    let entries = vec![
        entry("s5.sqlite", Kind::Scheduled, 5),
        entry("m4.sqlite", Kind::Manual, 4),
        entry("s3.sqlite", Kind::Scheduled, 3),
        entry("m2.sqlite", Kind::Manual, 2),
        pinned,
        entry("u.sqlite", Kind::Uploaded, 0),
        entry("a.sqlite", Kind::Added, 0),
        incomplete,
    ];
    let mut names: Vec<&str> = prune_list(&entries, 2)
        .into_iter()
        .map(|e| e.name.as_str())
        .collect();
    names.sort_unstable();
    assert_eq!(names, ["m2.sqlite", "s3.sqlite"]);
}

#[test]
fn room_needs_the_reserve() {
    assert_eq!(reserve(5 * GB), GB);
    assert_eq!(reserve(50 * GB), 5 * GB);
    let error = room(
        Some(5 * GB + GB / 2),
        5 * GB,
        "The backup folder",
        "Advice.",
    )
    .unwrap_err();
    assert!(error.contains("5.5 GB free"), "{error}");
    assert!(error.contains("6.0 GB"), "{error}");
    assert!(room(Some(6 * GB), 5 * GB, "The backup folder", "").is_ok());
    assert!(room(None, 5 * GB, "The backup folder", "").is_err());
}

#[test]
fn legacy_scan_matches_only_1x_folders() {
    let config = scratch();
    let backups = config.join("backups");
    for (folder, file) in [("id1", "manifest.json"), ("id2", "db.sql"), (".tmp-x", "a")] {
        fs::create_dir_all(backups.join(folder)).unwrap();
        fs::write(backups.join(folder).join(file), b"12345").unwrap();
    }
    fs::create_dir_all(backups.join("id1/rollback")).unwrap();
    fs::write(backups.join("id1/rollback/db.sql"), b"123").unwrap();
    fs::write(backups.join("2x.sqlite"), b"keep").unwrap();
    fs::create_dir_all(backups.join("other")).unwrap();
    fs::write(backups.join("other/file"), b"keep").unwrap();

    let view = serde_json::to_value(legacy(&config).unwrap()).unwrap();
    assert_eq!(view["folders"], 3);
    assert_eq!(view["bytes"], 18);
    assert_eq!(delete_legacy(&config).unwrap(), 3);
    assert!(legacy(&config).is_none());
    assert!(backups.join("2x.sqlite").exists());
    assert!(backups.join("other/file").exists());
    fs::remove_dir_all(config).unwrap();
}

#[tokio::test]
async fn publish_adds_and_update_needs_an_existing_backup() {
    let dir = scratch();
    let lock = tokio::sync::Mutex::new(());
    let name = "b.sqlite";
    let missing = update_manifest(&lock, &dir, name, |m| m.pinned = true).await;
    assert_eq!(missing.unwrap_err().kind(), std::io::ErrorKind::NotFound);

    let part = finished(&dir, name, b"first");
    publish(&lock, &dir, part, name, &manifest(Kind::Manual, 1))
        .await
        .unwrap();
    let listed = entries(&dir).unwrap();
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].kind(), Kind::Manual);
    assert_eq!(mode(&dir.join(name)), 0o600);
    assert_eq!(mode(&manifest_path(&dir, name)), 0o600);

    let updated = update_manifest(&lock, &dir, name, |m| m.pinned = true)
        .await
        .unwrap();
    assert!(updated.pinned);

    let before = (
        fs::read(dir.join(name)).unwrap(),
        fs::read(manifest_path(&dir, name)).unwrap(),
    );
    let part = finished(&dir, name, b"second");
    let part_path = part.path().to_owned();
    let taken = publish(&lock, &dir, part, name, &manifest(Kind::Manual, 2)).await;
    assert_eq!(taken.unwrap_err().kind(), std::io::ErrorKind::AlreadyExists);
    assert_eq!(fs::read(dir.join(name)).unwrap(), before.0);
    assert_eq!(fs::read(manifest_path(&dir, name)).unwrap(), before.1);
    assert!(!part_path.exists());
    fs::remove_dir_all(dir).unwrap();
}

#[tokio::test]
async fn a_stale_manifest_tmp_does_not_block_updates() {
    let dir = scratch();
    let lock = tokio::sync::Mutex::new(());
    fs::write(dir.join("b.sqlite"), b"data").unwrap();
    let tmp = dir.join("b.sqlite.json.tmp");
    fs::write(&tmp, b"garbage").unwrap();
    update_manifest(&lock, &dir, "b.sqlite", |m| m.note = "n".to_owned())
        .await
        .unwrap();
    assert!(!tmp.exists());
    assert_eq!(
        entries(&dir).unwrap()[0].manifest.as_ref().unwrap().note,
        "n"
    );
    fs::remove_dir_all(dir).unwrap();
}

async fn three_manual(dir: &Path, lock: &tokio::sync::Mutex<()>) {
    for i in 1..=3 {
        let name = format!("m{i}.sqlite");
        let part = finished(dir, &name, b"data");
        publish(lock, dir, part, &name, &manifest(Kind::Manual, i))
            .await
            .unwrap();
    }
}

fn pinned_in_manifest(dir: &Path, name: &str) -> bool {
    let bytes = fs::read(manifest_path(dir, name)).unwrap();
    serde_json::from_slice::<Manifest>(&bytes).unwrap().pinned
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn a_pin_is_never_lost_to_pruning() {
    let lock = Arc::new(tokio::sync::Mutex::new(()));
    let dir = scratch();
    three_manual(&dir, &lock).await;
    update_manifest(&lock, &dir, "m1.sqlite", |m| m.pinned = true)
        .await
        .unwrap();
    prune(&lock, &dir, 1).await.unwrap();
    assert!(dir.join("m1.sqlite").exists());
    assert!(dir.join("m3.sqlite").exists());
    assert!(!dir.join("m2.sqlite").exists());
    fs::remove_dir_all(dir).unwrap();

    let dir = scratch();
    three_manual(&dir, &lock).await;
    prune(&lock, &dir, 1).await.unwrap();
    let late = update_manifest(&lock, &dir, "m1.sqlite", |m| m.pinned = true).await;
    assert_eq!(late.unwrap_err().kind(), std::io::ErrorKind::NotFound);
    fs::remove_dir_all(dir).unwrap();

    for _ in 0..200 {
        let dir = Arc::new(scratch());
        three_manual(&dir, &lock).await;
        let pin = {
            let (lock, dir) = (Arc::clone(&lock), Arc::clone(&dir));
            tokio::spawn(async move {
                update_manifest(&lock, &dir, "m1.sqlite", |m| m.pinned = true).await
            })
        };
        let pruned = {
            let (lock, dir) = (Arc::clone(&lock), Arc::clone(&dir));
            tokio::spawn(async move { prune(&lock, &dir, 1).await })
        };
        let (pin, pruned) = tokio::join!(pin, pruned);
        pruned.unwrap().unwrap();
        if pin.unwrap().is_ok() {
            assert!(dir.join("m1.sqlite").exists());
            assert!(pinned_in_manifest(&dir, "m1.sqlite"));
        }
        fs::remove_dir_all(&*dir).unwrap();
    }
}

#[tokio::test]
async fn verify_results_keep_a_pin_made_meanwhile() {
    let dir = scratch();
    let lock = tokio::sync::Mutex::new(());
    let part = finished(&dir, "b.sqlite", b"data");
    let mut first = manifest(Kind::Manual, 1);
    first.sha256 = Some("abc".to_owned());
    publish(&lock, &dir, part, "b.sqlite", &first)
        .await
        .unwrap();
    let read = entries(&dir).unwrap()[0].manifest.clone().unwrap();
    update_manifest(&lock, &dir, "b.sqlite", |m| m.pinned = true)
        .await
        .unwrap();
    let after = update_manifest(&lock, &dir, "b.sqlite", |m| {
        m.verified_ms = Some(5);
        m.verify_error = None;
    })
    .await
    .unwrap();
    assert!(!read.pinned);
    assert!(after.pinned);
    assert_eq!(after.verified_ms, Some(5));
    assert_eq!(after.sha256.as_deref(), Some("abc"));
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn restore_copy_is_private_while_it_is_written() {
    let dir = scratch();
    let src = dir.join("src.sqlite");
    fs::write(&src, vec![7u8; 9_000_000]).unwrap();
    let dst = dir.join("dst.part");
    let mut seen = None;
    let (part, sha) = copy_to_part(&src, dst.clone(), &mut |_| {
        seen.get_or_insert_with(|| mode(&dst));
        true
    })
    .unwrap();
    assert_eq!(seen, Some(0o600));
    assert_eq!(fs::read(part.path()).unwrap().len(), 9_000_000);
    assert_eq!(sha.len(), 32);
    drop(part);
    assert!(!dst.exists());
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn part_files_start_private() {
    let dir = scratch();
    let path = dir.join(".x.part");
    let (part, _file) = Part::create(path.clone()).unwrap();
    assert_eq!(mode(&path), 0o600);
    assert_eq!(fs::metadata(&path).unwrap().len(), 0);
    assert_eq!(
        Part::create(path.clone()).err().map(|e| e.kind()),
        Some(std::io::ErrorKind::AlreadyExists)
    );
    drop(part);
    assert!(!path.exists());
    fs::remove_dir_all(dir).unwrap();
}
