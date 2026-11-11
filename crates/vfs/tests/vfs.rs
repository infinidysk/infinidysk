//! The tree and its readers over a loopback NNTP server.
#![allow(clippy::unwrap_used)] // Test helpers outside `#[test]` fns; panics are the assertions.

use std::io::SeekFrom;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use infinidysk_core::crypt::{Cipher, Crypt, Key};
use infinidysk_core::{
    BlobId, ByteRange, FileMeta, FilePart, ItemId, ItemKind, JobId, MultipartFile, NzbFile,
    Segment, Segments, blob,
};
use infinidysk_db::{
    Db, HealthState, HealthStatus, HistoryEntry, Item, JobStatus, QueueEntry, Recheck,
};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_stream::{Budget, Fault};
use infinidysk_testkit::{Article, Corpus, LoopbackServer, post};
use infinidysk_vfs::{Delivery, Kind, Node, Origin, Tracing, Vfs};
use tokio::io::{AsyncReadExt, AsyncSeekExt};

const ARTICLE: usize = 4096;

fn segments(articles: &[Article], len: usize) -> Segments {
    Segments {
        items: articles
            .iter()
            .enumerate()
            .map(|(i, a)| Segment {
                id: a.message_id.clone(),
                size: u32::try_from(ARTICLE.min(len - i * ARTICLE)).unwrap(),
                fallbacks: Vec::new(),
            })
            .collect(),
        sizes_trusted: true,
        ..Segments::default()
    }
}

fn file(parent: &Item, name: &str, meta: &FileMeta, blobs: &mut Vec<(BlobId, Vec<u8>)>) -> Item {
    let kind = match meta {
        FileMeta::Nzb(_) => ItemKind::NzbFile,
        FileMeta::Multipart(_) => ItemKind::Multipart,
    };
    let mut item = Item::child(parent, name, kind).unwrap();
    let id = BlobId::new();
    item.size = Some(meta.size());
    item.meta_blob = Some(id);
    blobs.push((id, blob::encode_meta(meta).unwrap()));
    item
}

fn origin() -> Origin {
    Origin {
        delivery: Delivery::WebDav,
        client: "test".into(),
        player: None,
    }
}

async fn read_all(vfs: &Vfs, node: &Node) -> Vec<u8> {
    let mut out = Vec::new();
    vfs.open(node, origin())
        .await
        .unwrap()
        .unwrap()
        .read_to_end(&mut out)
        .await
        .unwrap();
    out
}

/// Reads from `start` to the end of a fresh handle with single `read` calls of odd
/// sizes, so short reads land across segment, part and cipher block boundaries.
async fn read_odd(vfs: &Vfs, node: &Node, start: usize, expected: &[u8]) {
    let mut reader = vfs.open(node, origin()).await.unwrap().unwrap();
    reader.seek(SeekFrom::Start(start as u64)).await.unwrap();
    let mut got = Vec::new();
    for size in [1, 7, 4095, 16, 4097, 33, 15].into_iter().cycle() {
        let mut buf = vec![0; size];
        let n = reader.read(&mut buf).await.unwrap();
        if n == 0 {
            break;
        }
        got.extend_from_slice(&buf[..n]);
    }
    assert!(got == expected[start..], "odd reads from {start}");
}

async fn names(vfs: &Vfs, path: &str) -> Vec<String> {
    let node = vfs.lookup(path).await.unwrap().unwrap();
    vfs.list(&node)
        .await
        .unwrap()
        .iter()
        .map(Node::name)
        .collect()
}

#[tokio::test]
async fn serves_content_links_ids_and_queued_nzbs() {
    let plain = Corpus::generate(1025, 5, ARTICLE).data[..5 * ARTICLE - 100].to_vec();
    let vol1 = Corpus::generate(7, 3, ARTICLE).data[..3 * ARTICLE - 10].to_vec();
    let vol2 = Corpus::generate(8, 4, ARTICLE).data[..4 * ARTICLE - 20].to_vec();
    let (a_plain, a_vol1, a_vol2) = (
        post("plain", "a.mkv", &plain, ARTICLE),
        post("vol1", "b.part1.rar", &vol1, ARTICLE),
        post("vol2", "b.part2.rar", &vol2, ARTICLE),
    );
    let server = LoopbackServer::start([a_plain.clone(), a_vol1.clone(), a_vol2.clone()].concat())
        .await
        .unwrap();

    // The member is vol1[37..len-5] then vol2[ARTICLE+5..len-3]; the second part's
    // segments start at the volume's second article.
    let member = [
        &vol1[37..vol1.len() - 5],
        &vol2[ARTICLE + 5..vol2.len() - 3],
    ]
    .concat();
    let plain_meta = FileMeta::Nzb(NzbFile {
        segments: segments(&a_plain, plain.len()),
        proof: None,
    });
    let mut tail = segments(&a_vol2, vol2.len());
    tail.items.remove(0);
    let member_meta = FileMeta::Multipart(MultipartFile {
        parts: vec![
            FilePart {
                segments: segments(&a_vol1, vol1.len()),
                offset: 0,
                data: ByteRange {
                    start: 37,
                    end: vol1.len() as u64 - 5,
                },
                split_after: true,
                proof: None,
            },
            FilePart {
                segments: tail,
                offset: ARTICLE as u64,
                data: ByteRange {
                    start: ARTICLE as u64 + 5,
                    end: vol2.len() as u64 - 3,
                },
                split_after: false,
                proof: None,
            },
        ],
        crypt: None,
    });
    // The same bytes read as AES ciphertext: whole blocks, trimmed to `size`.
    assert_eq!(member.len() % 16, 0);
    let crypt = Crypt {
        key: Key::Aes256([9; 32]),
        iv: [3; 16],
        size: member.len() as u64 - 7,
    };
    let mut decrypted = member.clone();
    Cipher::new(&crypt.key).decrypt(&mut crypt.iv.clone(), &mut decrypted);
    decrypted.truncate(member.len() - 7);
    let FileMeta::Multipart(member_file) = &member_meta else {
        unreachable!()
    };
    let encrypted_meta = FileMeta::Multipart(MultipartFile {
        parts: member_file.parts.clone(),
        crypt: Some(crypt),
    });

    let path = std::env::temp_dir().join(format!("infinidysk-vfs-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = Item::child(&content, "tv", ItemKind::Directory).unwrap();
    let job = Item::child(&tv, "job", ItemKind::Directory).unwrap();
    let sub = Item::child(&job, "sub", ItemKind::Directory).unwrap();
    let pending = Item::child(&tv, "pending", ItemKind::Directory).unwrap();
    let mut blobs = Vec::new();
    let a = file(&job, "a.mkv", &plain_meta, &mut blobs);
    let b = file(&sub, "b.mkv", &member_meta, &mut blobs);
    let e = file(&job, "e.mkv", &encrypted_meta, &mut blobs);
    let mut holed = segments(&a_plain, plain.len());
    holed.items[1].id = "gone@test".into();
    let c = file(
        &job,
        "c.mkv",
        &FileMeta::Nzb(NzbFile {
            segments: holed,
            proof: None,
        }),
        &mut blobs,
    );
    let blob_refs: Vec<(BlobId, &[u8])> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(&[tv.clone(), pending], &[]).await.unwrap();
    let history = HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: "job.nzb".into(),
        job_name: "job".into(),
        category: "tv".into(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: Some(job.id),
        nzb_blob: None,
        storage: None,
    };
    db.add_history(
        &history,
        &[job, sub, a.clone(), b.clone(), c.clone(), e],
        &blob_refs,
    )
    .await
    .unwrap();
    let xml = b"<nzb></nzb>".to_vec();
    let queued = QueueEntry {
        id: JobId::new(),
        created_at: 2,
        file_name: "next.nzb".into(),
        job_name: "next".into(),
        category: "tv".into(),
        priority: 0,
        post_processing: 3,
        nzb_blob: BlobId::new(),
        nzb_size: xml.len() as u64,
        total_bytes: 0,
        paused: false,
        retry_at: None,
        attempts: 0,
        stuck_count: 0,
        last_error: None,
        content_hash: None,
    };
    db.enqueue(&queued, &blob::encode_nzb(&xml).unwrap())
        .await
        .unwrap();

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
    let client = Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]);
    let faults = Arc::new(Mutex::new(Vec::new()));
    let vfs = Vfs::new(
        db.clone(),
        Arc::new(client),
        Budget::new(1 << 20),
        1 << 20,
        "/mnt/dysk/",
    )
    .with_report({
        let faults = Arc::clone(&faults);
        Arc::new(move |id, _version, index, fault| faults.lock().unwrap().push((id, index, fault)))
    });

    assert_eq!(
        names(&vfs, "/").await,
        [".ids", "README", "completed-symlinks", "content", "nzbs"]
    );
    assert_eq!(names(&vfs, "/content/tv").await, ["job", "pending"]);

    let node = vfs.lookup("/content/tv/job/a.mkv").await.unwrap().unwrap();
    assert_eq!((node.kind(), node.size()), (Kind::File, plain.len() as u64));
    assert!(read_all(&vfs, &node).await == plain, "plain sequential");

    let node = vfs
        .lookup("content/tv/job/sub/b.mkv")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(node.size(), member.len() as u64);
    assert!(
        read_all(&vfs, &node).await == member,
        "multipart sequential"
    );
    vfs.sessions().set_tracing(Some(Tracing {
        until: std::time::Instant::now() + Duration::from_secs(60),
        capacity: 10_000,
    }));
    let mut reader = vfs.open(&node, origin()).await.unwrap().unwrap();
    let split = vol1.len() - 5 - 37;
    for (pos, len) in [
        (split - 3, 10),
        (5, 100),
        (split + 100, 50),
        (member.len() - 7, 7),
    ] {
        reader.seek(SeekFrom::Start(pos as u64)).await.unwrap();
        let mut buf = vec![0; len];
        reader.read_exact(&mut buf).await.unwrap();
        assert!(buf == member[pos..pos + len], "multipart read at {pos}");
    }
    assert_eq!(reader.read(&mut [0; 4]).await.unwrap(), 0, "end of file");
    // The open read is listed until closed; its trace outlives it.
    let open = vfs.sessions().open();
    assert_eq!(open.len(), 1);
    assert_eq!(
        (open[0].served(), open[0].pos()),
        (167, member.len() as u64)
    );
    drop(reader);
    assert!(vfs.sessions().open().is_empty());
    let traced = vfs.sessions().traced();
    let summary = traced[0].trace.as_ref().unwrap().summary();
    assert_eq!((traced.len(), summary.seeks), (1, 4));
    assert!(summary.served >= 4, "{summary:?}");

    let node = vfs.lookup("/content/tv/job/e.mkv").await.unwrap().unwrap();
    assert_eq!(node.size(), decrypted.len() as u64);
    assert!(
        read_all(&vfs, &node).await == decrypted,
        "encrypted sequential"
    );
    let mut reader = vfs.open(&node, origin()).await.unwrap().unwrap();
    for (pos, len) in [
        (split - 3, 10),
        (5, 100),
        (16, 16),
        (17, 40),
        (split + 100, 50),
        (decrypted.len() - 9, 9),
    ] {
        reader.seek(SeekFrom::Start(pos as u64)).await.unwrap();
        let mut buf = vec![0; len];
        reader.read_exact(&mut buf).await.unwrap();
        assert!(buf == decrypted[pos..pos + len], "encrypted read at {pos}");
    }
    assert_eq!(reader.read(&mut [0; 4]).await.unwrap(), 0, "end of file");
    drop(reader);
    let member_node = vfs
        .lookup("/content/tv/job/sub/b.mkv")
        .await
        .unwrap()
        .unwrap();
    for start in [0, 5, split - 3] {
        read_odd(&vfs, &member_node, start, &member).await;
        read_odd(&vfs, &node, start, &decrypted).await;
    }

    assert_eq!(
        names(&vfs, "/completed-symlinks/tv").await,
        ["job"],
        "only completed"
    );
    assert_eq!(
        vfs.lookup("/completed-symlinks/tv/pending").await.unwrap(),
        None
    );
    assert_eq!(
        names(&vfs, "/completed-symlinks/tv/job").await,
        ["a.mkv", "c.mkv", "e.mkv", "sub"]
    );
    let link = vfs
        .lookup("/completed-symlinks/tv/job/sub/b.mkv")
        .await
        .unwrap()
        .unwrap();
    let id = b.id.to_string();
    let shards: String = id.chars().take(5).flat_map(|c| [c, '/']).collect();
    let target = format!("/mnt/dysk/.ids/{shards}{id}");
    assert_eq!(link.kind(), Kind::Symlink);
    assert_eq!(
        String::from_utf8(read_all(&vfs, &link).await).unwrap(),
        target
    );

    let by_id = vfs
        .lookup(&target["/mnt/dysk".len()..])
        .await
        .unwrap()
        .unwrap();
    assert_eq!(by_id, Node::IdFile(b.clone()));
    assert!(names(&vfs, &format!("/.ids/{shards}")).await.contains(&id));
    assert_eq!(names(&vfs, "/.ids/0").await.len(), 16);
    assert_eq!(
        vfs.lookup("/.ids/0/1").await.unwrap(),
        Some(Node::Shard("01".into()))
    );
    assert_eq!(
        vfs.lookup(&format!("/.ids/0/0/0/0/0/{id}")).await.unwrap(),
        None,
        "wrong shard"
    );

    assert_eq!(names(&vfs, "/nzbs").await, ["tv"]);
    let nzb = vfs.lookup("/nzbs/tv/next.nzb").await.unwrap().unwrap();
    assert_eq!(read_all(&vfs, &nzb).await, xml);
    assert_eq!(vfs.lookup("/content/../content").await.unwrap(), None);

    // A missing article plays as zeros and is reported once.
    vfs.set_gap_tolerance(false);
    let node = vfs.lookup("/content/tv/job/c.mkv").await.unwrap().unwrap();
    let mut expected = plain.clone();
    expected[ARTICLE..2 * ARTICLE].fill(0);
    assert!(read_all(&vfs, &node).await == expected, "zero-filled hole");
    assert_eq!(*faults.lock().unwrap(), [(c.id, 1, Fault::Missing)]);
    // A second open fills the hole it found without asking providers again.
    assert!(read_all(&vfs, &node).await == expected, "known hole");
    assert_eq!(faults.lock().unwrap().len(), 1);
    // Updated metadata replaces what earlier opens of the file cached.
    let a_node = vfs.lookup("/content/tv/job/a.mkv").await.unwrap().unwrap();
    let mut moved = segments(&a_plain, plain.len());
    moved.items[2].id = "moved@test".into();
    let moved = FileMeta::Nzb(NzbFile {
        segments: moved,
        proof: None,
    });
    db.update_blob(a.meta_blob.unwrap(), &blob::encode_meta(&moved).unwrap())
        .await
        .unwrap();
    let mut updated = plain.clone();
    updated[2 * ARTICLE..3 * ARTICLE].fill(0);
    assert!(read_all(&vfs, &a_node).await == updated, "updated metadata");
    // Under the health caps, one of a few segments is too much of the file.
    vfs.set_gap_tolerance(true);
    let mut handle = vfs.open(&node, origin()).await.unwrap().unwrap();
    assert!(handle.read_to_end(&mut Vec::new()).await.is_err());

    // Hidden files vanish from every view of the tree.
    let status = HealthStatus {
        state: HealthState::Hidden,
        ..HealthStatus::default()
    };
    db.set_health(b.id, &status, Recheck::None).await.unwrap();
    assert_eq!(names(&vfs, "/content/tv/job/sub").await, [""; 0]);
    assert_eq!(vfs.lookup("/content/tv/job/sub/b.mkv").await.unwrap(), None);
    assert_eq!(
        vfs.lookup("/completed-symlinks/tv/job/sub/b.mkv")
            .await
            .unwrap(),
        None
    );
    assert_eq!(
        vfs.lookup(&target["/mnt/dysk".len()..]).await.unwrap(),
        None
    );
    assert!(!names(&vfs, &format!("/.ids/{shards}")).await.contains(&id));
}

fn completed(job: &Item) -> HistoryEntry {
    HistoryEntry {
        id: JobId::new(),
        created_at: 1,
        file_name: format!("{}.nzb", job.name),
        job_name: job.name.clone(),
        category: "tv".into(),
        status: JobStatus::Completed,
        total_bytes: 0,
        download_seconds: 0,
        fail_message: None,
        download_dir: Some(job.id),
        nzb_blob: None,
        storage: None,
    }
}

#[tokio::test]
async fn deleted_completed_links_hide_without_changing_what_is_stored() {
    let path = std::env::temp_dir().join(format!("infinidysk-vfs-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .unwrap();
    let content = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let tv = Item::child(&content, "tv", ItemKind::Directory).unwrap();
    let job = Item::child(&tv, "job", ItemKind::Directory).unwrap();
    let sub = Item::child(&job, "sub", ItemKind::Directory).unwrap();
    let a = Item::child(&job, "a.mkv", ItemKind::NzbFile).unwrap();
    let b = Item::child(&job, "b.mkv", ItemKind::NzbFile).unwrap();
    let c = Item::child(&sub, "c.mkv", ItemKind::NzbFile).unwrap();
    let other = Item::child(&tv, "other", ItemKind::Directory).unwrap();
    db.insert(std::slice::from_ref(&tv), &[]).await.unwrap();
    let history = completed(&job);
    db.add_history(&history, &[job, sub, a.clone(), b, c], &[])
        .await
        .unwrap();
    db.add_history(&completed(&other), std::slice::from_ref(&other), &[])
        .await
        .unwrap();
    let start = || {
        Vfs::new(
            db.clone(),
            Arc::new(Client::new([])),
            Budget::new(1),
            0,
            "/mnt",
        )
    };
    let vfs = start();
    let node = |path: &'static str| {
        let vfs = &vfs;
        async move { vfs.lookup(path).await.unwrap() }
    };

    // Moving a link out: the app links the new path, then deletes the old one.
    let link = node("/completed-symlinks/tv/job/a.mkv").await.unwrap();
    assert!(vfs.delete_link(&link));
    assert_eq!(node("/completed-symlinks/tv/job/a.mkv").await, None);
    assert_eq!(
        names(&vfs, "/completed-symlinks/tv/job").await,
        ["b.mkv", "sub"]
    );
    assert!(node("/content/tv/job/a.mkv").await.is_some());
    let target = vfs.id_link(a.id);
    assert!(vfs.lookup(&target["/mnt".len()..]).await.unwrap().is_some());

    // Then the job folder; everything below it goes with it.
    let folder = node("/completed-symlinks/tv/job").await.unwrap();
    assert!(vfs.delete_link(&folder));
    assert_eq!(node("/completed-symlinks/tv/job").await, None);
    assert_eq!(node("/completed-symlinks/tv/job/b.mkv").await, None);
    assert_eq!(node("/completed-symlinks//tv/job/sub/c.mkv").await, None);
    assert_eq!(names(&vfs, "/completed-symlinks/tv").await, ["other"]);
    assert!(node("/content/tv/job/sub/c.mkv").await.is_some());
    assert!(db.history_entry(history.id).await.unwrap().is_some());

    // Only a download's links and folders can go.
    for path in [
        "/completed-symlinks/tv",
        "/completed-symlinks",
        "/content/tv/other",
    ] {
        assert!(!vfs.delete_link(&node(path).await.unwrap()), "{path}");
    }
    assert!(node("/completed-symlinks/tv").await.is_some());

    // A restart shows them again, until the history row goes.
    let restarted = start();
    assert_eq!(
        names(&restarted, "/completed-symlinks/tv").await,
        ["job", "other"]
    );
    assert!(
        restarted
            .lookup("/completed-symlinks/tv/job/a.mkv")
            .await
            .unwrap()
            .is_some()
    );
    db.delete_history(&[history.id]).await.unwrap();
    assert_eq!(names(&restarted, "/completed-symlinks/tv").await, ["other"]);
    assert_eq!(
        restarted
            .lookup("/completed-symlinks/tv/job")
            .await
            .unwrap(),
        None
    );
}
