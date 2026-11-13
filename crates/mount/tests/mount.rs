//! Mounts the tree and reads it through the kernel. Needs Linux, `/dev/fuse` and root (the
//! test switches filesystem UID for cross-UID checks): `cargo test -p infinidysk-mount -- --ignored`.
#![cfg(target_os = "linux")]
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::io::{Read, Seek, SeekFrom};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, JobId, NzbFile, Segment, Segments, blob,
};
use infinidysk_db::{Db, HistoryEntry, Item, JobStatus};
use infinidysk_mount::{Config, run};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_stream::Budget;
use infinidysk_testkit::{LoopbackServer, post};
use infinidysk_vfs::Vfs;
use tokio_util::sync::CancellationToken;

const ARTICLE: usize = 4096;

#[tokio::test(flavor = "multi_thread")]
#[ignore = "needs /dev/fuse and root for cross-UID checks"]
#[allow(clippy::significant_drop_tightening)] // Capture the session before changing bytes; apply consumes it afterward.
async fn reads_the_tree_through_the_kernel() {
    assert!(
        nix::unistd::geteuid().is_root(),
        "cross-UID checks require root"
    );
    let data: Vec<u8> = (0..5 * ARTICLE + 77)
        .map(|i| u8::try_from(i * 13 % 251).unwrap())
        .collect();
    let articles = post("a", "a.mkv", &data, ARTICLE);
    let server = LoopbackServer::start(articles.clone()).await.unwrap();
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .unwrap();
    let config = PoolConfig {
        max_connections: 4,
        pipeline_depth: 4,
        idle_timeout: Duration::from_secs(5),
        min_connections: 0,
    };
    let pool = Pool::new(endpoint, config, Downloads::new(32)).unwrap();
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));
    let repaired = Arc::new(AtomicBool::new(false));
    client.set_repairs(Box::new({
        let repaired = Arc::clone(&repaired);
        let file_size = data.len() as u64;
        let bodies: std::collections::HashMap<_, _> = articles
            .iter()
            .enumerate()
            .map(|(index, article)| {
                let start = index * ARTICLE;
                let data: Vec<u8> = data[start..(start + ARTICLE).min(data.len())]
                    .iter()
                    .map(|byte| byte ^ 0x80)
                    .collect();
                (
                    article.message_id.clone(),
                    infinidysk_nntp::Body {
                        data: data.into(),
                        offset: start as u64,
                        file_size,
                        part: None,
                        total: None,
                    },
                )
            })
            .collect();
        move |key| {
            if !repaired.load(Ordering::SeqCst) {
                return None;
            }
            let infinidysk_nntp::RepairKey::Article(id) = key else {
                return None;
            };
            let body = bodies.get(id)?.clone();
            Some(Box::pin(async move { Ok(Some(body)) }))
        }
    }));

    let tmp = std::env::temp_dir().join(format!("infinidysk-fuse-{}", ItemId::new()));
    std::fs::create_dir_all(&tmp).unwrap();
    let db = Db::connect(&format!("sqlite:{}", tmp.join("db.sqlite").display()))
        .await
        .unwrap();
    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = Item::child(&content, "tv", ItemKind::Directory).unwrap();
    let job = Item::child(&tv, "my job", ItemKind::Directory).unwrap();
    let meta = FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: articles
                .iter()
                .enumerate()
                .map(|(i, a)| Segment {
                    id: a.message_id.clone(),
                    size: u32::try_from(ARTICLE.min(data.len() - i * ARTICLE)).unwrap(),
                    fallbacks: Vec::new(),
                })
                .collect(),
            ..Segments::default()
        },
        proof: None,
    });
    let mut file = Item::child(&job, "a.mkv", ItemKind::NzbFile).unwrap();
    let blob_id = BlobId::new();
    file.size = Some(meta.size());
    file.meta_blob = Some(blob_id);
    let encoded = blob::encode_meta(&meta).unwrap();
    db.insert(std::slice::from_ref(&tv), &[]).await.unwrap();
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "job.nzb".into(),
        job_name: "my job".into(),
        category: "tv".into(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: Some(job.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(&history, &[job, file.clone()], &[(blob_id, &encoded)])
        .await
        .unwrap();

    let mount = tmp.join("mnt");
    let mount_str = mount.to_str().unwrap().to_owned();
    let cancel = CancellationToken::new();
    let (state, mut kernel) = tokio::sync::watch::channel(Err(String::new()));
    let vfs = Arc::new(Vfs::new(
        db.clone(),
        client,
        Budget::new(1 << 20),
        1 << 20,
        &mount_str,
    ));
    let invalidator = Arc::new(infinidysk_mount::Invalidator::default());
    let task = tokio::spawn(run(
        Arc::clone(&vfs),
        Arc::clone(&invalidator),
        Config {
            mount_dir: mount.clone(),
            uid: 1234,
            gid: 5678,
            max_read: 1 << 20,
            max_readahead: 5_000_000,
            max_background: 64,
            epoch_times: false,
        },
        state,
        cancel.clone(),
    ));

    let rt = tokio::runtime::Handle::current();
    let checks = tokio::task::spawn_blocking(move || {
        let readme = mount.join("README");
        for _ in 0..100 {
            if readme.exists() {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let mut root: Vec<_> = std::fs::read_dir(&mount)
            .unwrap()
            .map(|e| e.unwrap().file_name().into_string().unwrap())
            .collect();
        root.sort();
        assert_eq!(
            root,
            [".ids", "README", "completed-symlinks", "content", "nzbs"]
        );
        infinidysk_mount::answers(&mount).unwrap();
        let plain = mount.parent().unwrap().join("plain");
        std::fs::create_dir_all(&plain).unwrap();
        assert!(
            infinidysk_mount::answers(&plain).is_err(),
            "not a FUSE mount"
        );

        let path = mount.join("content/tv/my job/a.mkv");
        let meta = std::fs::metadata(&path).unwrap();
        assert_eq!(meta.len(), data.len() as u64);
        assert_eq!(std::os::unix::fs::MetadataExt::uid(&meta), 1234);
        assert_eq!(std::fs::read(&path).unwrap(), data, "whole file");
        let mut f = std::fs::File::open(&path).unwrap();
        f.seek(SeekFrom::Start(4090)).unwrap();
        let mut buf = [0; 20];
        f.read_exact(&mut buf).unwrap();
        assert_eq!(buf, data[4090..4110]);

        // Concurrent reads on one handle reach the mount out of order.
        let f = Arc::new(f);
        let readers: Vec<_> = (0..8)
            .map(|i| {
                let f = Arc::clone(&f);
                std::thread::spawn(move || {
                    let offset = (i * 2731) % (5 * ARTICLE);
                    let mut buf = [0; 1000];
                    std::os::unix::fs::FileExt::read_exact_at(&*f, &mut buf, offset as u64)
                        .unwrap();
                    (offset, buf)
                })
            })
            .collect();
        for reader in readers {
            let (offset, buf) = reader.join().unwrap();
            assert_eq!(buf, data[offset..offset + 1000], "read at {offset}");
        }

        // Needs `/sys/devices/virtual/bdi` writable; the kernel keeps whole pages, so
        // 5,000,000 bytes rounds up.
        let dev = std::os::linux::fs::MetadataExt::st_dev(&std::fs::metadata(&mount).unwrap());
        let major = ((dev >> 8) & 0xfff) | ((dev >> 32) & !0xfff);
        let minor = (dev & 0xff) | ((dev >> 12) & !0xff);
        let kb: u64 =
            std::fs::read_to_string(format!("/sys/class/bdi/{major}:{minor}/read_ahead_kb"))
                .unwrap()
                .trim()
                .parse()
                .unwrap();
        assert!(
            (5_000_000..5_000_000 + 65_536).contains(&(kb * 1024)),
            "read_ahead_kb {kb}"
        );

        let link = mount.join("completed-symlinks/tv/my job/a.mkv");
        let target = std::fs::read_link(&link).unwrap();
        assert!(target.starts_with(&mount_str), "{}", target.display());
        assert_eq!(
            std::fs::read(&link).unwrap(),
            data,
            "link resolves through /.ids"
        );
        assert!(
            std::fs::write(mount.join("README"), b"x").is_err(),
            "read-only"
        );
        assert!(std::fs::remove_dir(mount.join("completed-symlinks/tv")).is_err());
        assert!(std::fs::remove_file(&path).is_err());
        assert!(std::fs::rename(&path, mount.join("content/tv/b.mkv")).is_err());
        // .NET clears ReadOnly with chmod before a move; accepted, nothing changes.
        std::fs::set_permissions(&link, std::os::unix::fs::PermissionsExt::from_mode(0o644))
            .unwrap();

        // Arr apps move a link by linking the new path, then unlinking the old one; then
        // they delete the emptied job folder. A PGID group member may.
        let library = mount.parent().unwrap().join("library.mkv");
        std::os::unix::fs::symlink(&target, &library).unwrap();
        let job_dir = mount.join("completed-symlinks/tv/my job");
        let category = mount.join("completed-symlinks/tv");
        // Filesystem credentials stay confined to this disposable thread.
        std::thread::spawn(move || {
            let group = nix::unistd::Gid::from_raw(5678);
            let user = nix::unistd::Uid::from_raw(4321);
            nix::unistd::setfsgid(group);
            nix::unistd::setfsuid(user);
            assert_eq!(nix::unistd::setfsgid(group), group, "fsgid must change");
            assert_eq!(nix::unistd::setfsuid(user), user, "fsuid must change");
            std::fs::remove_file(&link)?;
            assert!(std::fs::symlink_metadata(&link).is_err(), "link is gone");
            assert!(std::fs::remove_file(&link).is_err(), "already gone");
            assert_eq!(std::fs::read_dir(&job_dir)?.count(), 0);
            std::fs::remove_dir(&job_dir)?;
            assert!(!job_dir.exists(), "job folder is gone");
            assert_eq!(std::fs::read_dir(&category)?.count(), 0);
            Ok::<(), std::io::Error>(())
        })
        .join()
        .unwrap()
        .expect("a PGID group member can delete links and job folders");
        assert_eq!(
            std::fs::read(&library).unwrap(),
            data,
            "the moved link still plays"
        );
        assert!(std::fs::remove_dir(mount.join("completed-symlinks/tv")).is_err());

        // Both aliases have cached pages. Repair must update even already-open files.
        let alias = std::fs::File::open(&target).unwrap();
        let mut bytes = vec![0; data.len()];
        std::os::unix::fs::FileExt::read_exact_at(&alias, &mut bytes, 0).unwrap();
        assert_eq!(bytes, data);
        let fence = vfs.read_fence(file.id);
        for enabled in [true, false] {
            let mut writers = [rt.block_on(fence.write())];
            let publication = invalidator.prepare(&mut writers).unwrap();
            repaired.store(enabled, Ordering::SeqCst);
            rt.block_on(publication.apply()).unwrap();
            drop(writers);
            let expected: Vec<_> = data
                .iter()
                .map(|byte| if enabled { byte ^ 0x80 } else { *byte })
                .collect();
            for opened in [&*f, &alias] {
                std::os::unix::fs::FileExt::read_exact_at(opened, &mut bytes, 0).unwrap();
                assert_eq!(bytes, expected);
            }
        }

        // Reads on an open file must not need the database: once its attributes expire,
        // the next buffered read refreshes them with a GETATTR on its handle before serving
        // even cached bytes. Nothing touches the file while they expire.
        rt.block_on(db.close());
        std::thread::sleep(Duration::from_secs(32)); // past the mount's 30 s ATTR_TTL
        let mut buf = [0; 20];
        std::os::unix::fs::FileExt::read_exact_at(&*f, &mut buf, 4090).unwrap();
        assert_eq!(buf, data[4090..4110], "read after the database closed");
    });
    let result = checks.await;
    // What the mount reports is what the kernel took, and the queue drains once the last
    // close is answered.
    let reported = tokio::time::timeout(Duration::from_secs(5), kernel.wait_for(Result::is_ok))
        .await
        .ok()
        .and_then(|k| (*k.ok()?).clone().ok());
    let mut waiting = None;
    for _ in 0..100 {
        waiting = reported.as_ref().and_then(infinidysk_vfs::Kernel::waiting);
        if waiting == Some(0) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    cancel.cancel();
    task.await.unwrap();
    result.unwrap();
    let reported = reported.expect("the mount reports its kernel limits");
    assert_eq!(reported.max_background, Some(64));
    assert_eq!(reported.congestion_threshold, Some(48));
    assert_eq!(waiting, Some(0));
}
