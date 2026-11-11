use infinidysk_core::{
    BlobId, ByteRange, FileMeta, FilePart, ItemId, ItemKind, MultipartFile, Par2Proof, PatchId,
    Segment, Segments, blob,
    repair::{Patch, Source as Posted},
};
use infinidysk_db::{
    Item,
    repair::{Owner, Source},
};
use infinidysk_nntp::Workload;
use sha2::{Digest, Sha256};

use super::*;

const DATA: &[u8] = b"repaired article";

#[tokio::test]
async fn durable_worker_recovers_a_missing_article_and_upgrades_existing_proofs() {
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::{LoopbackServer, post};
    use std::fmt::Write as _;
    use std::time::Duration;
    use tokio::sync::watch;
    let f = Fixture::new().await;
    let data = |length: usize| {
        let mut data = (0..length)
            .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
            .collect::<Vec<_>>();
        if length == 100_000 {
            data[..4].copy_from_slice(&[0x1a, 0x45, 0xdf, 0xa3]);
        }
        data
    };
    let a = post("worker-a", "a.bin", &data(100_000), 7000);
    let b = post("worker-b", "b.bin", &data(8192), 3000);
    // Independent par2cmdline packets protect these deterministic source bytes.
    let index = post(
        "worker-p",
        "proof.par2",
        include_bytes!("../../tests/fixtures/repair.par2"),
        7000,
    );
    let volume = post(
        "worker-v",
        "proof.vol0+3.par2",
        include_bytes!("../../tests/fixtures/repair.vol0+3.par2"),
        7000,
    );
    let mut xml = "<nzb>".to_owned();
    for (name, articles) in [
        ("a.mkv", &a),
        ("b.bin", &b),
        ("proof.par2", &index),
        ("proof.vol0+3.par2", &volume),
    ] {
        write!(xml, "<file subject=\"{name}\"><segments>").unwrap();
        for (i, article) in articles.iter().enumerate() {
            write!(
                xml,
                "<segment bytes=\"1\" number=\"{}\">{}</segment>",
                i + 1,
                article.message_id
            )
            .unwrap();
        }
        xml.push_str("</segments></file>");
    }
    xml.push_str("</nzb>");
    let parent = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&parent, "existing.bin", ItemKind::NzbFile).unwrap();
    let (nzb, meta) = (BlobId::new(), BlobId::new());
    item.nzb_blob = Some(nzb);
    item.meta_blob = Some(meta);
    item.size = Some(100_000);
    let metadata = blob::encode_meta(&FileMeta::Nzb(infinidysk_core::NzbFile {
        segments: Segments {
            items: a
                .iter()
                .enumerate()
                .map(|(i, article)| Segment {
                    id: article.message_id.clone(),
                    size: u32::try_from((100_000 - i * 7000).min(7000)).unwrap(),
                    fallbacks: vec![],
                })
                .collect(),
            sizes_trusted: true,
            missing: vec![3],
            corrupt: vec![],
        },
        proof: None,
    }))
    .unwrap();
    f.db.insert(
        &[item.clone()],
        &[
            (nzb, &blob::encode_nzb(xml.as_bytes()).unwrap()),
            (meta, &metadata),
        ],
    )
    .await
    .unwrap();
    let server = LoopbackServer::start(
        a.into_iter()
            .enumerate()
            .filter_map(|(i, article)| (i != 3).then_some(article))
            .chain(b)
            .chain(index)
            .chain(volume),
    )
    .await
    .unwrap();
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(3),
    )
    .unwrap();
    let pool = Pool::new(
        endpoint,
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(3),
            min_connections: 0,
        },
        Downloads::new(32),
    )
    .unwrap();
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let vfs = Arc::new(infinidysk_vfs::Vfs::new(
        f.db.clone(),
        Arc::clone(&client),
        infinidysk_stream::Budget::new(1_000_000),
        0,
        "/mnt",
    ));
    let publisher = Publisher::new(Arc::clone(&repairs), vfs).unwrap();
    let (_arr_tx, arr) = watch::channel(vec![]);
    let (_settings_tx, settings) = watch::channel(infinidysk_health::Settings::default());
    let health =
        infinidysk_health::Health::new(f.db.clone(), Arc::clone(&client), arr, settings).unwrap();
    let queue = infinidysk_queue::Queue::new(f.db.clone(), Arc::clone(&client));
    queue.enable_par2();
    queue.update_rules(|rules| rules.check_percent = 100);
    let importing = queue
        .enqueue(
            &infinidysk_queue::Submission {
                file_name: "input.nzb",
                category: "test",
                priority: 0,
                post_processing: 0,
            },
            xml.as_bytes(),
        )
        .await
        .unwrap();
    assert!(queue.process_next().await);
    assert!(f.db.history_entry(importing).await.unwrap().is_none());
    assert_eq!(
        f.db.par2_result(Owner::Queue(importing), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        0
    );
    let repair_job = f.db.claim_par2(i64::MAX).await.unwrap().unwrap();
    let (state, reason) = repairs
        .perform(
            &client,
            &publisher,
            &health,
            &repair_job,
            &tokio_util::sync::CancellationToken::new(),
            &worker::Progress::default(),
        )
        .await
        .unwrap();
    assert_eq!(state, 3);
    f.db.finish_par2(
        repair_job.id,
        state,
        reason,
        0,
        0,
        infinidysk_db::now_ms(),
        None,
    )
    .await
    .unwrap();
    assert!(queue.process_next().await);
    let imported = f.db.history_entry(importing).await.unwrap().unwrap();
    assert_eq!(
        imported.status,
        infinidysk_db::JobStatus::Completed,
        "{:?}",
        imported.fail_message
    );
    f.db.request_par2(Owner::Item(item.id), false, 0)
        .await
        .unwrap()
        .unwrap();
    let job = f.db.claim_par2(1).await.unwrap().unwrap();
    let (state, _) = repairs
        .perform(
            &client,
            &publisher,
            &health,
            &job,
            &tokio_util::sync::CancellationToken::new(),
            &worker::Progress::default(),
        )
        .await
        .unwrap();
    assert_eq!(state, 2);
    let FileMeta::Nzb(file) = blob::decode_meta(&f.db.blob(meta).await.unwrap().unwrap()).unwrap()
    else {
        panic!("plain file")
    };
    assert!(file.proof.unwrap().set_id.is_some());
    assert_eq!(file.segments.missing, [] as [u32; 0]);
    let patch =
        f.db.article_repairs(&file.segments.items[3].id)
            .await
            .unwrap()
            .pop()
            .unwrap();
    assert_eq!(
        repairs
            .store(1_000_000)
            .unwrap()
            .read(&patch)
            .unwrap()
            .data
            .as_ref(),
        &data(100_000)[21_000..28_000]
    );
    drop(publisher);
    drop(repairs);
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn metadata_publication_retires_readers_and_rejects_a_stale_source() {
    let f = Fixture::new().await;
    let (repairs, vfs, _reader) = delivery(&f).await;
    let publisher = Publisher::new(repairs, vfs).unwrap();
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let meta = item.meta_blob.unwrap();
    let raw = f.db.blob(meta).await.unwrap().unwrap();
    assert!(
        publisher
            .metadata(f.source.clone(), raw.clone())
            .await
            .unwrap()
    );
    f.db.update_blob(meta, &raw).await.unwrap();
    assert!(!publisher.metadata(f.source.clone(), raw).await.unwrap());
    f.close().await;
}

#[tokio::test]
async fn cleanup_preserves_owned_repairs_then_removes_unowned_bytes_without_breaking_buffers() {
    use tokio::io::AsyncReadExt as _;
    let f = Fixture::new().await;
    f.publish().await;
    let (repairs, vfs, mut reader) = delivery(&f).await;
    let stopped = tokio_util::sync::CancellationToken::new();
    assert_eq!(repairs.cleanup(&vfs, &stopped).await.unwrap(), 0);
    let mut bytes = vec![0; DATA.len()];
    reader.read_exact(&mut bytes).await.unwrap();
    assert_eq!(bytes, DATA);
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    f.db.delete_item(item.id).await.unwrap();
    assert_eq!(repairs.cleanup(&vfs, &stopped).await.unwrap(), 0);
    drop(reader);
    let rollback = f.root.join(crate::database::PRE_POSTGRES);
    std::fs::write(&rollback, b"retained SQLite copy").unwrap();
    assert_eq!(repairs.cleanup(&vfs, &stopped).await.unwrap(), 0);
    std::fs::remove_file(rollback).unwrap();
    assert_eq!(repairs.cleanup(&vfs, &stopped).await.unwrap(), 1);
    assert_eq!(bytes, DATA);
    assert_eq!(repairs.store(u64::MAX).unwrap().used_bytes().unwrap(), 0);
    drop(vfs);
    drop(repairs);
    f.close().await;
}

#[tokio::test]
async fn database_backup_embeds_verified_patches_and_restores_without_the_original_store() {
    use sqlx::Connection as _;
    let f = Fixture::new().await;
    f.publish().await;
    let client = Arc::new(Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let backup = f.root.join("backup.sqlite");
    repairs
        .backup_into(&backup, &tokio_util::sync::CancellationToken::new())
        .await
        .unwrap();
    assert!(
        infinidysk_db::check_file(&backup, || false)
            .await
            .unwrap()
            .failure()
            .is_none()
    );
    let corrupt = f.root.join("corrupt.sqlite");
    std::fs::copy(&backup, &corrupt).unwrap();
    let mut conn = sqlx::SqliteConnection::connect(&format!("sqlite:{}", corrupt.display()))
        .await
        .unwrap();
    sqlx::query("UPDATE repair_patches SET backup_payload = zeroblob(length(backup_payload))")
        .execute(&mut conn)
        .await
        .unwrap();
    conn.close().await.unwrap();
    assert!(
        infinidysk_db::check_file(&corrupt, || false)
            .await
            .unwrap()
            .failure()
            .is_some()
    );
    drop(repairs);
    drop(client);
    std::fs::remove_dir_all(f.root.join("repairs")).unwrap();
    let restored = Db::connect(&format!("sqlite:{}", backup.display()))
        .await
        .unwrap();
    let client = Arc::new(Client::new([]));
    let repairs = install(&client, restored.clone(), f.root.join("repairs"));
    repairs.restore_embedded().await.unwrap();
    assert_eq!(
        client
            .repaired("repair@loopback", None)
            .await
            .unwrap()
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    drop(repairs);
    drop(client);
    restored.close().await;
    f.close().await;
}

#[tokio::test]
async fn moving_the_database_without_its_repair_directory_requests_a_recheck() {
    use infinidysk_db::{HealthState, Recheck};

    let f = Fixture::new().await;
    f.publish().await;
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let (_, mut health) = f.db.health(item.id).await.unwrap().unwrap();
    health.state = HealthState::Healthy;
    f.db.set_health(item.id, &health, Recheck::None)
        .await
        .unwrap();
    let moved = f.root.join("moved");
    std::fs::create_dir(&moved).unwrap();
    let database = moved.join("data.sqlite");
    f.db.backup_into(&database, || false).await.unwrap();
    let db = Db::connect(&format!("sqlite:{}", database.display()))
        .await
        .unwrap();
    let client = Client::new([]);
    let repairs = install(&client, db.clone(), moved.join("repairs"));
    repairs.restore_embedded().await.unwrap();
    let (_, health) = db.health(item.id).await.unwrap().unwrap();
    assert_eq!(health.state, HealthState::Unchecked);
    assert_eq!(health.next_check, Some(0));
    assert!(client.repaired("repair@loopback", None).await.is_err());
    drop(repairs);
    drop(client);
    let original = f.client();
    assert_eq!(
        original
            .repaired("repair@loopback", None)
            .await
            .unwrap()
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    drop(original);
    db.close().await;
    f.close().await;
}

struct Fixture {
    root: PathBuf,
    db: Db,
    source: Source,
    patch: Patch,
}

impl Fixture {
    async fn new() -> Self {
        Self::seed(false).await
    }

    async fn seed(omitted: bool) -> Self {
        let root = std::env::temp_dir().join(format!("infinidysk-repair-read-{}", ItemId::new()));
        std::fs::create_dir(&root).expect("parent");
        let db = Db::connect(&format!("sqlite:{}", root.join("data.sqlite").display()))
            .await
            .expect("database");
        let parent = db
            .item(ItemId::CONTENT)
            .await
            .expect("query")
            .expect("content");
        let (nzb, meta) = (BlobId::new(), BlobId::new());
        let mut file = Item::child(&parent, "source.bin", ItemKind::Multipart).expect("file");
        file.nzb_blob = Some(nzb);
        file.meta_blob = Some(meta);
        file.size = Some(DATA.len() as u64);
        let metadata = blob::encode_meta(&FileMeta::Multipart(MultipartFile {
            parts: vec![FilePart {
                segments: Segments {
                    items: vec![Segment {
                        id: if omitted {
                            String::new()
                        } else {
                            "repair@loopback".into()
                        },
                        size: u32::try_from(DATA.len()).expect("small article"),
                        fallbacks: vec![],
                    }],
                    sizes_trusted: true,
                    missing: vec![0],
                    corrupt: vec![],
                },
                offset: 7000,
                data: ByteRange {
                    start: 7000,
                    end: 7000 + DATA.len() as u64,
                },
                split_after: false,
                proof: omitted.then_some(Par2Proof {
                    set_id: Some([1; 16]),
                    file_id: [2; 16],
                    file_length: 20_000_000_000,
                    slice_size: 4,
                    slice_md5: vec![],
                    slice_crc32: vec![],
                    file_md5: [3; 16],
                    md5_16k: [4; 16],
                }),
            }],
            crypt: None,
        }))
        .expect("metadata");
        db.insert(&[file.clone()], &[(nzb, b"nzb"), (meta, &metadata)])
            .await
            .expect("seed");
        let source = db
            .repair_source(Owner::Item(file.id))
            .await
            .expect("query")
            .expect("source");
        let patch = Patch {
            id: PatchId::new(),
            source: Posted {
                set: [1; 16],
                file: [2; 16],
                range: ByteRange {
                    start: 7000,
                    end: 7000 + DATA.len() as u64,
                },
                file_length: 20_000_000_000,
                message_id: (!omitted).then(|| "repair@loopback".into()),
                part: Some(2),
                total: Some(1000),
            },
            sha256: Sha256::digest(DATA).into(),
        };
        let store = Store::open(&root.join("repairs"), 1_000_000).expect("store");
        store
            .write(&patch, DATA, || false)
            .expect("file durability");
        drop(store);
        Self {
            root,
            db,
            source,
            patch,
        }
    }

    fn client(&self) -> Client {
        let client = Client::new([]);
        install(&client, self.db.clone(), self.root.join("repairs"));
        client
    }

    async fn publish(&self) {
        assert!(
            self.db
                .publish_patches(&self.source, std::slice::from_ref(&self.patch))
                .await
                .expect("publish")
        );
    }

    async fn close(self) {
        self.db.close().await;
        std::fs::remove_dir_all(self.root).expect("cleanup");
    }
}

async fn delivery(
    f: &Fixture,
) -> (
    Arc<Repairs>,
    Arc<infinidysk_vfs::Vfs>,
    infinidysk_vfs::Handle,
) {
    let client = Arc::new(Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let vfs = Arc::new(infinidysk_vfs::Vfs::new(
        f.db.clone(),
        client,
        infinidysk_stream::Budget::new(1_000_000),
        0,
        "/mnt",
    ));
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let reader = vfs
        .open(
            &infinidysk_vfs::Node::Item(item),
            infinidysk_vfs::Origin {
                delivery: infinidysk_vfs::Delivery::WebDav,
                client: "test".into(),
                player: None,
            },
        )
        .await
        .unwrap()
        .unwrap();
    (repairs, vfs, reader)
}

#[tokio::test]
async fn publication_refreshes_an_open_reader_after_draining_observations() {
    use tokio::io::{AsyncReadExt, AsyncSeekExt};
    let f = Fixture::new().await;
    let (repairs, vfs, mut reader) = delivery(&f).await;
    let mut data = vec![0; DATA.len()];
    reader.read_exact(&mut data).await.unwrap();
    assert_eq!(data, vec![0; DATA.len()]);
    let receipt = repairs
        .store(1_000_000)
        .unwrap()
        .write(&f.patch, DATA, || false)
        .unwrap();
    let publisher = Publisher::new(repairs, vfs).unwrap();
    let observing = f.db.write_permit().await;
    let mut publishing = Box::pin(publisher.publish(f.source.clone(), vec![receipt]));
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(20), &mut publishing)
            .await
            .is_err()
    );
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        Vec::<Patch>::new()
    );
    // Dropping the request cannot abandon an accepted transaction or its fences.
    drop(publishing);
    drop(observing);
    tokio::time::timeout(std::time::Duration::from_secs(3), async {
        loop {
            if !f
                .db
                .article_repairs("repair@loopback")
                .await
                .unwrap()
                .is_empty()
            {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    let finalized = f.db.pause_writes().await;
    reader.seek(std::io::SeekFrom::Start(0)).await.unwrap();
    reader.read_exact(&mut data).await.unwrap();
    assert_eq!(data, DATA);
    drop(finalized);
    f.close().await;
}

#[tokio::test]
async fn publication_rejects_changed_sources_without_losing_durable_files() {
    let f = Fixture::new().await;
    let (repairs, vfs, _reader) = delivery(&f).await;
    let receipt = repairs
        .store(1_000_000)
        .unwrap()
        .write(&f.patch, DATA, || false)
        .unwrap();
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let id = item.meta_blob.unwrap();
    let same = f.db.blob(id).await.unwrap().unwrap();
    f.db.update_blob(id, &same).await.unwrap();
    let publisher = Publisher::new(Arc::clone(&repairs), vfs).unwrap();
    assert!(
        !publisher
            .publish(f.source.clone(), vec![receipt])
            .await
            .unwrap()
    );
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        Vec::<Patch>::new()
    );
    assert_eq!(
        repairs
            .store(1_000_000)
            .unwrap()
            .read(&f.patch)
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    f.close().await;
}

#[tokio::test]
async fn publication_requires_valid_bytes_and_the_same_database_owner() {
    let f = Fixture::new().await;
    let (repairs, vfs, _reader) = delivery(&f).await;
    let receipt = repairs
        .store(1_000_000)
        .unwrap()
        .write(&f.patch, DATA, || false)
        .unwrap();
    let other = Db::connect("sqlite::memory:").await.unwrap();
    let different = Arc::new(infinidysk_vfs::Vfs::new(
        other,
        Arc::new(Client::new([])),
        infinidysk_stream::Budget::new(1),
        0,
        "/mnt",
    ));
    assert!(Publisher::new(Arc::clone(&repairs), different).is_err());
    std::fs::remove_file(f.root.join("repairs").join(format!("{}.patch", f.patch.id))).unwrap();
    assert!(
        Publisher::new(repairs, vfs)
            .unwrap()
            .publish(f.source.clone(), vec![receipt])
            .await
            .is_err()
    );
    assert!(!f.db.may_have_repairs());
    f.close().await;
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn unavailable_native_invalidation_rejects_before_sql_and_releases_admission() {
    use tokio::io::AsyncReadExt;
    let f = Fixture::new().await;
    let (repairs, vfs, mut reader) = delivery(&f).await;
    let receipt = repairs
        .store(1_000_000)
        .unwrap()
        .write(&f.patch, DATA, || false)
        .unwrap();
    let publisher = Publisher::new(repairs, vfs)
        .unwrap()
        .with_mount(Arc::default());
    assert!(
        publisher
            .publish(f.source.clone(), vec![receipt])
            .await
            .is_err()
    );
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        Vec::<Patch>::new()
    );
    let mut bytes = [0; 1];
    assert_eq!(reader.read(&mut bytes).await.unwrap(), 1);
    assert_eq!(bytes, [0]);
    f.close().await;
}

#[tokio::test]
async fn durable_files_become_effective_only_after_commit_and_survive_restart() {
    let mut f = Fixture::new().await;
    let client = f.client();
    assert!(!f.db.may_have_repairs());
    assert!(matches!(
        client.body("repair@loopback", Workload::Stream, None).await,
        Err(Error::Unavailable)
    ));
    f.publish().await;
    assert!(f.db.may_have_repairs());
    assert_eq!(
        client
            .body("repair@loopback", Workload::Stream, None)
            .await
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    client
        .stat("repair@loopback", Workload::Health, None)
        .await
        .unwrap();
    drop(client);
    f.db.close().await;
    f.db = Db::connect(&format!("sqlite:{}", f.root.join("data.sqlite").display()))
        .await
        .unwrap();
    assert!(f.db.may_have_repairs());
    let client = f.client();
    assert_eq!(
        client
            .hedge("repair@loopback", None, Some(7000))
            .await
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn missing_corrupt_and_stale_repairs_cannot_count_as_effective_availability() {
    let f = Fixture::new().await;
    f.publish().await;
    let client = f.client();
    let path = f.root.join("repairs").join(format!("{}.patch", f.patch.id));
    let original = std::fs::read(&path).unwrap();
    let mut corrupt = original.clone();
    *corrupt.last_mut().unwrap() ^= 1;
    std::fs::write(&path, corrupt).unwrap();
    assert!(matches!(
        client.body("repair@loopback", Workload::Stream, None).await,
        Err(Error::Repair(_))
    ));
    assert!(matches!(
        client.stat("repair@loopback", Workload::Health, None).await,
        Err(Error::Repair(_))
    ));
    std::fs::remove_file(&path).unwrap();
    assert!(matches!(
        client.stat("repair@loopback", Workload::Health, None).await,
        Err(Error::Repair(_))
    ));
    std::fs::write(&path, original).unwrap();
    assert!(
        client
            .stat("repair@loopback", Workload::Health, None)
            .await
            .is_ok()
    );
    f.db.update_blob(f.source.nzb(), b"changed source")
        .await
        .unwrap();
    assert!(matches!(
        client.body("repair@loopback", Workload::Stream, None).await,
        Err(Error::Unavailable)
    ));
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn conflicting_current_owners_are_rejected_before_reading_any_bytes() {
    let f = Fixture::new().await;
    f.publish().await;
    let parent = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut file = Item::child(&parent, "other.bin", ItemKind::NzbFile).unwrap();
    let (nzb, meta) = (BlobId::new(), BlobId::new());
    file.nzb_blob = Some(nzb);
    file.meta_blob = Some(meta);
    f.db.insert(&[file.clone()], &[(nzb, b"other NZB"), (meta, b"metadata")])
        .await
        .unwrap();
    let source =
        f.db.repair_source(Owner::Item(file.id))
            .await
            .unwrap()
            .unwrap();
    let mut patch = f.patch.clone();
    patch.id = PatchId::new();
    patch.sha256[0] ^= 1;
    assert!(f.db.publish_patches(&source, &[patch]).await.unwrap());
    let client = f.client();
    assert!(matches!(
        client.body("repair@loopback", Workload::Stream, None).await,
        Err(Error::Repair("committed article identities conflict"))
    ));
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn an_empty_catalog_does_not_need_a_directory_or_a_database_lookup_per_read() {
    let f = Fixture::new().await;
    std::fs::remove_dir_all(f.root.join("repairs")).unwrap();
    let client = f.client();
    f.db.close().await;
    assert!(matches!(
        client.body("repair@loopback", Workload::Stream, None).await,
        Err(Error::Unavailable)
    ));
    assert!(!f.root.join("repairs").exists());
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn equivalent_owners_can_supply_another_verified_copy() {
    let f = Fixture::new().await;
    f.publish().await;
    let parent = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut file = Item::child(&parent, "copy.bin", ItemKind::NzbFile).unwrap();
    let (nzb, meta) = (BlobId::new(), BlobId::new());
    file.nzb_blob = Some(nzb);
    file.meta_blob = Some(meta);
    f.db.insert(&[file.clone()], &[(nzb, b"other NZB"), (meta, b"metadata")])
        .await
        .unwrap();
    let source =
        f.db.repair_source(Owner::Item(file.id))
            .await
            .unwrap()
            .unwrap();
    let mut patch = f.patch.clone();
    patch.id = PatchId::new();
    patch.source.set = [4; 16];
    patch.source.file = [5; 16];
    let store = Store::open(&f.root.join("repairs"), 1_000_000).unwrap();
    let durable = store.write(&patch, DATA, || false).unwrap();
    assert!(
        f.db.publish_patches(&source, std::slice::from_ref(durable.patch()))
            .await
            .unwrap()
    );
    drop(durable);
    drop(store);
    std::fs::remove_file(f.root.join("repairs").join(format!("{}.patch", f.patch.id))).unwrap();
    let client = f.client();
    assert_eq!(
        client
            .body("repair@loopback", Workload::Stream, None)
            .await
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn committed_repairs_serve_webdav_ranges_and_strict_archive_member_reads() {
    check_delivery(false).await;
}

#[tokio::test]
async fn committed_omitted_ranges_serve_webdav_and_strict_archive_member_reads() {
    check_delivery(true).await;
}

#[tokio::test]
async fn omitted_reads_never_guess_unknown_or_mismatching_source_layouts() {
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    use tokio::io::AsyncReadExt as _;
    for case in 0..8 {
        let f = Fixture::seed(true).await;
        f.publish().await;
        let file =
            f.db.item_by_path("/content/source.bin")
                .await
                .unwrap()
                .unwrap();
        let metadata = file.meta_blob.unwrap();
        let raw = f.db.blob(metadata).await.unwrap().unwrap();
        let mut meta = blob::decode_meta(&raw).unwrap();
        let FileMeta::Multipart(parts) = &mut meta else {
            unreachable!()
        };
        let part = &mut parts.parts[0];
        match case {
            0 => part.proof.as_mut().unwrap().set_id = None,
            1 => part.proof = None,
            2 => part.segments.sizes_trusted = false,
            3 => part.proof.as_mut().unwrap().set_id = Some([9; 16]),
            4 => part.proof.as_mut().unwrap().file_id = [9; 16],
            5 => part.proof.as_mut().unwrap().file_length += 1,
            6 => {
                part.offset += 1;
                part.data.start += 1;
                part.data.end += 1;
            }
            7 => {
                part.offset = u64::MAX - 4;
                part.data.start = part.offset;
                part.data.end = u64::MAX;
            }
            _ => unreachable!(),
        }
        f.db.update_blob(metadata, &blob::encode_meta(&meta).unwrap())
            .await
            .unwrap();
        let client = Arc::new(f.client());
        let vfs = Vfs::new(
            f.db.clone(),
            Arc::clone(&client),
            Budget::new(1_000_000),
            0,
            "/mnt",
        );
        if case == 7 {
            assert!(matches!(
                vfs.open_analysis(&file).await,
                Err(infinidysk_vfs::Error::Corrupt(
                    "part data lies outside its segments"
                ))
            ));
        } else {
            let mut reader = vfs.open_analysis(&file).await.unwrap();
            assert!(
                reader.read_to_end(&mut Vec::new()).await.is_err(),
                "case {case}"
            );
        }
        // Unpersisted metadata does not carry trusted NZB ownership.
        let original = blob::decode_meta(&raw).unwrap();
        let mut reader = vfs.open_meta(&original).unwrap();
        assert!(reader.read_to_end(&mut Vec::new()).await.is_err());
        drop(reader);
        drop(vfs);
        drop(client);
        f.close().await;
    }
}

#[tokio::test]
async fn omitted_plain_file_reads_survive_restart_and_refuse_a_changed_nzb() {
    use infinidysk_core::NzbFile;
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    use tokio::io::AsyncReadExt as _;
    let mut f = Fixture::seed(true).await;
    let old =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let raw = f.db.blob(old.meta_blob.unwrap()).await.unwrap().unwrap();
    let FileMeta::Multipart(mut parts) = blob::decode_meta(&raw).unwrap() else {
        unreachable!()
    };
    let mut part = parts.parts.remove(0);
    part.proof.as_mut().unwrap().file_length = DATA.len() as u64;
    let meta = FileMeta::Nzb(NzbFile {
        segments: part.segments,
        proof: part.proof,
    });
    let root = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut file = Item::child(&root, "plain.bin", ItemKind::NzbFile).unwrap();
    let metadata = BlobId::new();
    file.meta_blob = Some(metadata);
    file.nzb_blob = Some(f.source.nzb());
    file.size = Some(DATA.len() as u64);
    f.db.insert(
        &[file.clone()],
        &[(metadata, &blob::encode_meta(&meta).unwrap())],
    )
    .await
    .unwrap();
    f.source =
        f.db.repair_source(Owner::Item(file.id))
            .await
            .unwrap()
            .unwrap();
    f.patch.id = PatchId::new();
    f.patch.source.range = ByteRange {
        start: 0,
        end: DATA.len() as u64,
    };
    f.patch.source.file_length = DATA.len() as u64;
    f.patch.source.part = None;
    f.patch.source.total = None;
    let store = Store::open(&f.root.join("repairs"), 1_000_000).unwrap();
    store.write(&f.patch, DATA, || false).unwrap();
    drop(store);
    f.publish().await;
    f.db.close().await;
    f.db = Db::connect(&format!("sqlite:{}", f.root.join("data.sqlite").display()))
        .await
        .unwrap();
    let client = Arc::new(f.client());
    let vfs = Vfs::new(
        f.db.clone(),
        Arc::clone(&client),
        Budget::new(1_000_000),
        0,
        "/mnt",
    );
    let mut reader = vfs.open_analysis(&file).await.unwrap();
    let mut bytes = Vec::new();
    reader.read_to_end(&mut bytes).await.unwrap();
    assert_eq!(bytes, DATA);
    drop(reader);
    let mut reader = vfs.open_analysis(&file).await.unwrap();
    f.db.update_blob(f.source.nzb(), b"changed NZB")
        .await
        .unwrap();
    assert!(reader.read_to_end(&mut Vec::new()).await.is_err());
    drop(reader);
    drop(vfs);
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn cached_layouts_cannot_be_bound_to_different_persisted_metadata() {
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    let f = Fixture::seed(true).await;
    f.publish().await;
    let client = Arc::new(f.client());
    let vfs = Vfs::new(
        f.db.clone(),
        Arc::clone(&client),
        Budget::new(1_000_000),
        0,
        "/mnt",
    );
    let file =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    drop(vfs.open_analysis(&file).await.unwrap());
    let metadata = file.meta_blob.unwrap();
    let raw = f.db.blob(metadata).await.unwrap().unwrap();
    let mut meta = blob::decode_meta(&raw).unwrap();
    let FileMeta::Multipart(parts) = &mut meta else {
        unreachable!()
    };
    parts.parts[0].proof.as_mut().unwrap().file_id = [9; 16];
    // Another DB owner does not advance this VFS owner's metadata-cache epoch.
    let other = Db::connect(&format!("sqlite:{}", f.root.join("data.sqlite").display()))
        .await
        .unwrap();
    other
        .update_blob(metadata, &blob::encode_meta(&meta).unwrap())
        .await
        .unwrap();
    assert!(matches!(
        vfs.open_analysis(&file).await,
        Err(infinidysk_vfs::Error::Corrupt(
            "repair metadata changed while opening file"
        ))
    ));
    other.close().await;
    drop(vfs);
    drop(client);
    f.close().await;
}

async fn check_delivery(omitted: bool) {
    use axum::body::Body as HttpBody;
    use axum::http::{Request, StatusCode};
    use http_body_util::BodyExt as _;
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    use infinidysk_webdav::{Access, Auth, WebDav, router};
    use tokio::io::AsyncReadExt as _;
    use tower::ServiceExt as _;

    let f = Fixture::seed(omitted).await;
    f.publish().await;
    let client = Arc::new(f.client());
    let vfs = Arc::new(Vfs::new(
        f.db.clone(),
        Arc::clone(&client),
        Budget::new(1_000_000),
        1_000_000,
        "/mnt",
    ));
    let app = router(Arc::new(WebDav {
        vfs: Arc::clone(&vfs),
        queue: Arc::new(infinidysk_queue::Queue::new(
            f.db.clone(),
            Arc::clone(&client),
        )),
        access: std::sync::RwLock::new(Access {
            auth: Auth::Disabled,
            download_key: None,
            link_key: None,
            strm_key: None,
            previous_strm: vec![],
            legacy_strm: None,
            trusted_proxies: vec![],
        }),
    }));
    let response = app
        .oneshot(
            Request::builder()
                .uri("/content/source.bin")
                .header("Range", "bytes=2-8")
                .body(HttpBody::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::PARTIAL_CONTENT);
    assert_eq!(
        response.headers()["content-range"],
        format!("bytes 2-8/{}", DATA.len())
    );
    assert_eq!(
        response
            .into_body()
            .collect()
            .await
            .unwrap()
            .to_bytes()
            .as_ref(),
        &DATA[2..9]
    );
    let file =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let mut analysis = vfs.open_analysis(&file).await.unwrap();
    let mut data = Vec::new();
    analysis.read_to_end(&mut data).await.unwrap();
    assert_eq!(data, DATA);
    drop(analysis);
    drop(vfs);
    drop(client);
    f.close().await;
}
