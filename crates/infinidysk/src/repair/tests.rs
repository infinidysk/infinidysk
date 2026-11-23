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
async fn ready_jobs_do_not_wait_for_the_idle_poll_between_attempts() {
    use tokio::sync::watch;
    let f = Fixture::new().await;
    let client = Arc::new(infinidysk_nntp::Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let vfs = Arc::new(
        infinidysk_vfs::Vfs::new(
            f.db.clone(),
            Arc::clone(&client),
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/mnt",
        )
        .with_repair_publisher(),
    );
    let publisher = Publisher::new(Arc::clone(&repairs), vfs).unwrap();
    let (_arr_tx, arr) = watch::channel(vec![]);
    let (_health_tx, settings) = watch::channel(infinidysk_health::Settings::default());
    let health = Arc::new(
        infinidysk_health::Health::new(f.db.clone(), client.clone(), arr, settings).unwrap(),
    );
    let parent = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    for n in 0..2 {
        let mut item =
            Item::child(&parent, &format!("pending-{n}.bin"), ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        item.meta_blob = Some(meta);
        let data = blob::encode_meta(&FileMeta::Nzb(infinidysk_core::NzbFile {
            segments: Segments {
                items: vec![],
                sizes_trusted: true,
                missing: vec![],
                corrupt: vec![],
            },
            proof: None,
        }))
        .unwrap();
        f.db.insert(&[item.clone()], &[(meta, &data)])
            .await
            .unwrap();
        assert!(
            f.db.request_par2(Owner::Item(item.id), true, infinidysk_db::now_ms())
                .await
                .unwrap()
                .is_some(),
            "each file queues its own check"
        );
    }
    assert_eq!(f.db.par2_pending().await.unwrap(), 2);
    let stop = tokio_util::sync::CancellationToken::new();
    let task = tokio::spawn(repairs.run(client, publisher, health, stop.clone()));
    let drained = tokio::time::timeout(std::time::Duration::from_secs(1), async {
        while f.db.par2_pending().await.unwrap() != 0 {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await;
    stop.cancel();
    task.await.unwrap();
    assert!(
        drained.is_ok(),
        "queued work should start immediately after the previous job finishes"
    );
    f.close().await;
}

#[tokio::test]
async fn shutdown_defers_a_claimed_check_without_consuming_a_retry() {
    use tokio::sync::watch;
    let f = Fixture::new().await;
    let client = Arc::new(infinidysk_nntp::Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let vfs = Arc::new(
        infinidysk_vfs::Vfs::new(
            f.db.clone(),
            client.clone(),
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/mnt",
        )
        .with_repair_publisher(),
    );
    let publisher = Publisher::new(repairs.clone(), vfs).unwrap();
    let (_arr_tx, arr) = watch::channel(vec![]);
    let (_health_tx, settings) = watch::channel(infinidysk_health::Settings::default());
    let health = Arc::new(
        infinidysk_health::Health::new(f.db.clone(), client.clone(), arr, settings).unwrap(),
    );
    let root = f.db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "interrupted.bin", ItemKind::NzbFile).unwrap();
    let meta = BlobId::new();
    item.meta_blob = Some(meta);
    let raw = blob::encode_meta(&FileMeta::Nzb(infinidysk_core::NzbFile {
        segments: Segments::default(),
        proof: None,
    }))
    .unwrap();
    f.db.insert(&[item.clone()], &[(meta, &raw)]).await.unwrap();
    let id =
        f.db.request_par2(Owner::Item(item.id), true, 0)
            .await
            .unwrap()
            .unwrap();
    let paused = f.db.pause_writes().await;
    let stop = tokio_util::sync::CancellationToken::new();
    let task = tokio::spawn(repairs.run(client, publisher, health, stop.clone()));
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while f
            .db
            .par2_result(Owner::Item(item.id), true)
            .await
            .unwrap()
            .unwrap()
            .state
            != 1
        {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    stop.cancel();
    drop(paused);
    task.await.unwrap();
    let saved =
        f.db.par2_jobs()
            .await
            .unwrap()
            .into_iter()
            .find(|job| job.id == id)
            .unwrap();
    assert_eq!(saved.state, 4);
    assert_eq!(
        saved.attempts, 0,
        "application shutdown must not consume a provider retry"
    );
    f.close().await;
}

#[tokio::test]
async fn raising_capacity_reuses_the_store_lease_without_retaining_the_old_limit() {
    let f = Fixture::new().await;
    let client = infinidysk_nntp::Client::new(vec![]);
    let repairs = install(&client, f.db.clone(), f.root.join("capacity"));
    let small = repairs.store(1).unwrap();
    assert!(small.write(&f.patch, DATA, || false).is_err());
    let raised = repairs.store(1_000_000).unwrap();
    let receipt = raised.write(&f.patch, DATA, || false).unwrap();
    assert_eq!(raised.read(&f.patch).unwrap().data.as_ref(), DATA);
    assert!(small.used_bytes().unwrap() > 1);
    drop(receipt);
    drop(raised);
    drop(small);
    drop(repairs);
    f.close().await;
}

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
    let missing_article = a[3].message_id.clone();
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
    let vfs = Arc::new(
        infinidysk_vfs::Vfs::new(
            f.db.clone(),
            Arc::clone(&client),
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/mnt",
        )
        .with_repair_publisher(),
    );
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
    let mut limited = worker::Progress::default();
    limited.memory = 128_000_000;
    let error = repairs
        .perform(
            &client,
            &publisher,
            &health,
            &repair_job,
            &tokio_util::sync::CancellationToken::new(),
            &limited,
        )
        .await
        .unwrap_err();
    assert!(error.to_string().contains("memory"), "{error}");
    assert_eq!(f.db.article_repairs(&missing_article).await.unwrap(), []);
    let (state, reason, retry, outcome) = settle(
        repairs
            .perform(
                &client,
                &publisher,
                &health,
                &repair_job,
                &tokio_util::sync::CancellationToken::new(),
                &worker::Progress::default(),
            )
            .await
            .unwrap(),
    );
    assert_eq!(state, 3);
    f.db.finish_par2(
        repair_job.claim(),
        state,
        &reason,
        0,
        0,
        infinidysk_db::now_ms(),
        retry,
        outcome,
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
    let mut sibling = Item::child(&parent, "same-source.bin", ItemKind::NzbFile).unwrap();
    let sibling_meta = BlobId::new();
    sibling.nzb_blob = Some(nzb);
    sibling.meta_blob = Some(sibling_meta);
    sibling.size = item.size;
    f.db.insert(&[sibling.clone()], &[(sibling_meta, &metadata)])
        .await
        .unwrap();
    f.db.request_par2(Owner::Item(sibling.id), true, infinidysk_db::now_ms() - 1)
        .await
        .unwrap();
    let (state, reason, retry, outcome) = settle(
        repairs
            .perform(
                &client,
                &publisher,
                &health,
                &job,
                &tokio_util::sync::CancellationToken::new(),
                &worker::Progress::default(),
            )
            .await
            .unwrap(),
    );
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
    f.db.finish_par2(
        job.claim(),
        state,
        &reason,
        0,
        0,
        infinidysk_db::now_ms(),
        retry,
        outcome,
    )
    .await
    .unwrap();
    let other_owner =
        f.db.par2_owner(BlobId(repair_job.nzb_blob.unwrap()))
            .await
            .unwrap()
            .unwrap();
    let other =
        f.db.request_par2(other_owner, false, 0)
            .await
            .unwrap()
            .unwrap();
    f.db.restart_par2(other, 0).await.unwrap();
    let interleaved =
        f.db.claim_par2(infinidysk_db::now_ms())
            .await
            .unwrap()
            .unwrap();
    assert_eq!(interleaved.id, other);
    let (state, reason, retry, outcome) = settle(
        repairs
            .perform(
                &client,
                &publisher,
                &health,
                &interleaved,
                &tokio_util::sync::CancellationToken::new(),
                &worker::Progress::default(),
            )
            .await
            .unwrap(),
    );
    f.db.finish_par2(
        interleaved.claim(),
        state,
        &reason,
        0,
        0,
        infinidysk_db::now_ms(),
        retry,
        outcome,
    )
    .await
    .unwrap();
    let alias_job =
        f.db.claim_par2(infinidysk_db::now_ms())
            .await
            .unwrap()
            .unwrap();
    let before = server.body_commands();
    let (state, reason, retry, outcome) = settle(
        repairs
            .perform(
                &client,
                &publisher,
                &health,
                &alias_job,
                &tokio_util::sync::CancellationToken::new(),
                &worker::Progress::default(),
            )
            .await
            .unwrap(),
    );
    assert_eq!(state, 2);
    assert!(reason.contains("Articles are available"), "{reason}");
    assert!(
        server.body_commands() <= before + 3,
        "a manual availability check reads only spot checks"
    );
    f.db.finish_par2(
        alias_job.claim(),
        state,
        &reason,
        0,
        0,
        infinidysk_db::now_ms(),
        retry,
        outcome,
    )
    .await
    .unwrap();
    let mut uncovered = Item::child(&parent, "uncovered.bin", ItemKind::NzbFile).unwrap();
    let uncovered_meta = BlobId::new();
    uncovered.nzb_blob = Some(nzb);
    uncovered.meta_blob = Some(uncovered_meta);
    uncovered.size = Some(7000);
    let raw = blob::encode_meta(&FileMeta::Nzb(infinidysk_core::NzbFile {
        segments: Segments {
            items: vec![Segment {
                id: "uncovered@loopback".into(),
                size: 7000,
                fallbacks: vec![],
            }],
            sizes_trusted: true,
            missing: vec![],
            corrupt: vec![],
        },
        proof: None,
    }))
    .unwrap();
    f.db.insert(&[uncovered.clone()], &[(uncovered_meta, &raw)])
        .await
        .unwrap();
    let whole =
        f.db.request_par2(Owner::Item(item.id), false, 0)
            .await
            .unwrap()
            .unwrap();
    f.db.restart_par2(whole, 0).await.unwrap();
    let whole_job =
        f.db.claim_par2(infinidysk_db::now_ms())
            .await
            .unwrap()
            .unwrap();
    assert_eq!(whole_job.id, whole);
    let error = repairs
        .perform(
            &client,
            &publisher,
            &health,
            &whole_job,
            &tokio_util::sync::CancellationToken::new(),
            &worker::Progress::default(),
        )
        .await
        .unwrap_err();
    assert!(
        error.to_string().contains("every file in this release"),
        "a sibling's proof cannot verify an uncovered file: {error:#}"
    );
    let (state, reason, retry, outcome) = settle(worker::Outcome::Unavailable(error.to_string()));
    f.db.finish_par2(
        whole_job.claim(),
        state,
        &reason,
        0,
        0,
        infinidysk_db::now_ms(),
        retry,
        outcome,
    )
    .await
    .unwrap();
    assert_ne!(
        f.db.par2_result(Owner::Item(uncovered.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        2,
        "the uncovered file is not reported verified"
    );
    // Each stalled BODY costs a 3 s request timeout, so an attempt that ignores
    // cancellation keeps the lane for well over the wait below.
    let stalled = &file.segments.items[0].id;
    server.stall_article(stalled, 8, Duration::from_secs(10));
    let verification =
        f.db.request_par2(Owner::Item(item.id), true, infinidysk_db::now_ms())
            .await
            .unwrap()
            .unwrap();
    let stop = tokio_util::sync::CancellationToken::new();
    let running = tokio::spawn(Arc::clone(&repairs).run(
        Arc::clone(&client),
        publisher,
        Arc::new(health),
        stop.clone(),
    ));
    tokio::time::timeout(Duration::from_secs(8), async {
        loop {
            let current =
                f.db.par2_result(Owner::Item(item.id), true)
                    .await
                    .unwrap()
                    .unwrap();
            if current.state == 1
                && current.reason == "Checking article availability"
                && current.checked_articles > 0
            {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("live phase and article progress are saved before completion");
    f.db.cancel_par2(verification, infinidysk_db::now_ms())
        .await
        .unwrap();
    let running_claim =
        f.db.par2_jobs()
            .await
            .unwrap()
            .into_iter()
            .find(|job| job.id == verification)
            .unwrap()
            .claim();
    // A restart before the worker polls requeues the job; this attempt still stops.
    f.db.restart_par2(verification, infinidysk_db::now_ms())
        .await
        .unwrap();
    assert!(f.db.par2_cancelled(running_claim).await.unwrap());
    let claim = async || {
        f.db.par2_jobs()
            .await
            .unwrap()
            .into_iter()
            .find(|job| job.id == verification)
            .unwrap()
            .claim()
    };
    let requeued = claim().await;
    // One verify lane: any later claim proves the old attempt returned.
    tokio::time::timeout(Duration::from_secs(6), async {
        while claim().await == requeued {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("the cancelled attempt stops at its next poll and frees its lane");
    server.stall_article(stalled, 0, Duration::ZERO);
    tokio::time::timeout(Duration::from_secs(10), async {
        loop {
            let current =
                f.db.par2_result(Owner::Item(item.id), true)
                    .await
                    .unwrap()
                    .unwrap();
            if current.state == 2 {
                assert!(current.checked_articles > 0);
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("a restarted check runs again");
    stop.cancel();
    running.await.unwrap();
    drop(repairs);
    drop(client);
    f.close().await;
}

#[tokio::test]
async fn metadata_publication_retires_readers_and_rejects_a_stale_source() {
    use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _};
    let f = Fixture::new().await;
    f.publish().await;
    let (repairs, vfs, mut reader) = delivery(&f).await;
    let mut data = vec![0; DATA.len()];
    reader.read_exact(&mut data).await.unwrap();
    assert_eq!(data, DATA);
    let publisher = Publisher::new(repairs, vfs).unwrap();
    let item =
        f.db.item_by_path("/content/source.bin")
            .await
            .unwrap()
            .unwrap();
    let meta = item.meta_blob.unwrap();
    let raw = f.db.blob(meta).await.unwrap().unwrap();
    // Name an article with no stored repair: an open reader that follows this metadata
    // reads the gap filler instead of the repaired bytes.
    let mut renamed = blob::decode_meta_limited(&raw, 16_000_000).unwrap();
    let FileMeta::Multipart(file) = &mut renamed else {
        panic!("fixture is multipart");
    };
    file.parts[0].segments.items[0].id = "renamed@loopback".into();
    let renamed = blob::encode_meta(&renamed).unwrap();
    assert!(
        publisher
            .publish(
                f.source.clone(),
                Vec::new(),
                Some(renamed.clone()),
                &tokio_util::sync::CancellationToken::new(),
            )
            .await
            .unwrap()
    );
    reader.seek(std::io::SeekFrom::Start(0)).await.unwrap();
    reader.read_exact(&mut data).await.unwrap();
    assert_eq!(
        data,
        vec![0; DATA.len()],
        "the open reader follows the published metadata"
    );
    f.db.update_blob(meta, &raw).await.unwrap();
    assert!(
        !publisher
            .publish(
                f.source.clone(),
                Vec::new(),
                Some(renamed),
                &tokio_util::sync::CancellationToken::new(),
            )
            .await
            .unwrap()
    );
    drop(reader);
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
    // URL metacharacters in the directory must not change which file is opened.
    let dir = f.root.join("backup?dir%41");
    std::fs::create_dir(&dir).unwrap();
    let backup = dir.join("backup.sqlite");
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
    let mut conn = sqlx::SqliteConnection::connect(&crate::app::sqlite_url(&corrupt))
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
    let restored = Db::connect(&crate::app::sqlite_url(&backup)).await.unwrap();
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
    let db = Db::connect(&crate::app::sqlite_url(&database))
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

/// Removes a test directory even when setup or an assertion fails.
struct Scratch(PathBuf);

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

struct Fixture {
    root: PathBuf,
    db: Db,
    source: Source,
    patch: Patch,
    _scratch: Scratch,
}

impl Fixture {
    async fn new() -> Self {
        Self::seed(false).await
    }

    async fn seed(omitted: bool) -> Self {
        let root = std::env::temp_dir().join(format!("infinidysk-repair-read-{}", ItemId::new()));
        std::fs::create_dir(&root).expect("parent");
        let scratch = Scratch(root.clone());
        let db = Db::connect(&crate::app::sqlite_url(&root.join("data.sqlite")))
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
            _scratch: scratch,
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
                .publish_patches((), &self.source, std::slice::from_ref(&self.patch))
                .await
                .expect("publish")
        );
    }

    /// Closes the database before `Scratch` removes its directory.
    async fn close(self) {
        self.db.close().await;
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
    let vfs = Arc::new(
        infinidysk_vfs::Vfs::new(
            f.db.clone(),
            client,
            infinidysk_stream::Budget::new(1_000_000),
            0,
            "/mnt",
        )
        .with_repair_publisher(),
    );
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
    let running = tokio_util::sync::CancellationToken::new();
    let mut publishing =
        Box::pin(publisher.publish(f.source.clone(), vec![receipt], None, &running));
    // The first poll starts the publication; it cannot finish while an observation runs.
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(20), &mut publishing)
            .await
            .is_err()
    );
    // The publication has queued for the write pause once a new observation must wait.
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            match tokio::time::timeout(std::time::Duration::from_millis(5), f.db.write_permit())
                .await
            {
                Err(_) => break,
                Ok(permit) => drop(permit),
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("publication reaches the write pause");
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
            .publish(
                f.source.clone(),
                vec![receipt],
                None,
                &tokio_util::sync::CancellationToken::new(),
            )
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
    let different = Arc::new(
        infinidysk_vfs::Vfs::new(
            other,
            Arc::new(Client::new([])),
            infinidysk_stream::Budget::new(1),
            0,
            "/mnt",
        )
        .with_repair_publisher(),
    );
    assert!(Publisher::new(Arc::clone(&repairs), different).is_err());
    std::fs::remove_file(f.root.join("repairs").join(format!("{}.patch", f.patch.id))).unwrap();
    assert!(
        Publisher::new(repairs, vfs)
            .unwrap()
            .publish(
                f.source.clone(),
                vec![receipt],
                None,
                &tokio_util::sync::CancellationToken::new(),
            )
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
            .publish(
                f.source.clone(),
                vec![receipt],
                None,
                &tokio_util::sync::CancellationToken::new(),
            )
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
    f.db = Db::connect(&crate::app::sqlite_url(&f.root.join("data.sqlite")))
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
    assert!(f.db.publish_patches((), &source, &[patch]).await.unwrap());
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
        f.db.publish_patches((), &source, std::slice::from_ref(durable.patch()))
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
    f.db = Db::connect(&crate::app::sqlite_url(&f.root.join("data.sqlite")))
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
    let other = Db::connect(&crate::app::sqlite_url(&f.root.join("data.sqlite")))
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
    let vfs = Arc::new(
        Vfs::new(
            f.db.clone(),
            Arc::clone(&client),
            Budget::new(1_000_000),
            1_000_000,
            "/mnt",
        )
        .with_repair_publisher(),
    );
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

/// Turns an attempt's outcome into the saved state, as the worker does.
fn settle(outcome: worker::Outcome) -> (i16, String, Option<i64>, i16) {
    let finish = outcome
        .finish(1, infinidysk_db::now_ms())
        .expect("tests settle attempts that finish");
    (finish.state, finish.reason, finish.retry, finish.outcome)
}

#[test]
fn failed_attempts_retry_after_the_cooldown_then_stop() {
    use infinidysk_db::repair_jobs::{DAMAGED, EXHAUSTED};
    use worker::Outcome;
    let now = 1_000;
    let cooldown = 6 * 3_600_000;
    for attempts in [1, 2] {
        let retry = Outcome::Failed("provider timed out".into())
            .finish(attempts, now)
            .unwrap();
        assert_eq!(
            (retry.state, retry.retry, retry.outcome, retry.yielded),
            (4, Some(now + cooldown), 0, false)
        );
    }
    let last = Outcome::Failed("provider timed out".into())
        .finish(3, now)
        .unwrap();
    assert_eq!((last.state, last.retry, last.outcome), (5, None, EXHAUSTED));
    assert_eq!(last.reason, "Retry limit reached: provider timed out");
    let damaged = Outcome::Damaged.finish(1, now).unwrap();
    assert_eq!((damaged.state, damaged.outcome), (5, DAMAGED));
    let unavailable = Outcome::Unavailable("repair unavailable: no recovery data".into())
        .finish(1, now)
        .unwrap();
    assert_eq!((unavailable.state, unavailable.outcome), (5, 0));
    for outcome in [Outcome::Yielded, Outcome::Interrupted, Outcome::Restarted] {
        let resumed = outcome.finish(1, now).unwrap();
        assert_eq!((resumed.state, resumed.retry), (4, Some(now)));
        assert!(
            resumed.yielded,
            "playback, shutdown and other repairs do not use a retry"
        );
    }
    assert!(Outcome::Escalated.finish(1, now).is_none());
}

#[tokio::test]
async fn unrelated_articles_skip_reader_admission_once_the_catalog_is_known() {
    let f = Fixture::new().await;
    f.publish().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    assert!(!repairs.stored("unrelated@loopback").await.unwrap());
    // The catalog set is rebuilt in the background, off the read path.
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while repairs.cached("repair@loopback").is_none() {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("the catalog set is rebuilt");
    assert_eq!(repairs.cached("repair@loopback"), Some(true));
    let busy = Arc::clone(&repairs.readers)
        .acquire_many_owned(u32::try_from(READERS).unwrap())
        .await
        .unwrap();
    let read = tokio::time::timeout(
        std::time::Duration::from_secs(1),
        repairs.read(infinidysk_nntp::RepairKey::Article("other@loopback")),
    )
    .await
    .expect("a known miss needs no reader permit");
    assert!(matches!(read, Ok(None)));
    drop(busy);
    f.close().await;
}

/// Stores the fixture's repair and a second one written after it, then reads the
/// second first, so read order and write order pick different victims.
/// Returns the second patch and the store's usage with both.
async fn two_read_repairs(f: &Fixture, repairs: &Repairs) -> (Patch, u64) {
    let mut stale = f.patch.clone();
    stale.id = PatchId::new();
    stale.source.message_id = Some("stale@loopback".into());
    stale.source.range = ByteRange {
        start: 9000,
        end: 9000 + DATA.len() as u64,
    };
    let store = repairs.store(1_000_000).unwrap();
    drop(store.write(&f.patch, DATA, || false).unwrap());
    drop(store.write(&stale, DATA, || false).unwrap());
    f.publish().await;
    assert!(
        f.db.publish_patches((), &f.source, std::slice::from_ref(&stale))
            .await
            .unwrap()
    );
    let dir = f.root.join("repairs");
    let file = |patch: &Patch| dir.join(format!("{}.patch", patch.id));
    let modified = |patch: &Patch| std::fs::metadata(file(patch)).unwrap().modified().unwrap();
    let now = std::time::SystemTime::now();
    for (patch, age) in [(&f.patch, 200), (&stale, 100)] {
        std::fs::File::options()
            .write(true)
            .open(file(patch))
            .unwrap()
            .set_modified(now - std::time::Duration::from_secs(age))
            .unwrap();
    }
    store.read(&f.patch).unwrap();
    assert!(modified(&f.patch) > modified(&stale), "a read makes a repair the newest");
    (stale, store.used_bytes().unwrap())
}

#[tokio::test]
async fn staging_evicts_the_least_recently_read_repair_to_make_room() {
    let f = Fixture::new().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let (stale, used) = two_read_repairs(&f, &repairs).await;
    let mut next = f.patch.clone();
    next.id = PatchId::new();
    // Room for the new article once either file goes, not before.
    // The repair being staged belongs to another NZB.
    let receipts = repairs
        .stage(
            vec![(next.clone(), DATA.to_vec())],
            BlobId::new(),
            Store::charge(&next).unwrap() + used - 1,
            &tokio_util::sync::CancellationToken::new(),
        )
        .await
        .unwrap();
    assert_eq!(receipts.len(), 1);
    assert_eq!(f.db.article_repairs("stale@loopback").await.unwrap(), []);
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        std::slice::from_ref(&f.patch)
    );
    let store = repairs.store(u64::MAX).unwrap();
    assert!(store.read(&stale).is_err(), "the evicted file is removed");
    assert_eq!(store.read(&f.patch).unwrap().data.as_ref(), DATA);
    assert_eq!(store.read(&next).unwrap().data.as_ref(), DATA);
    drop((receipts, store, repairs));
    f.close().await;
}

#[tokio::test]
async fn staging_never_evicts_repairs_the_same_nzb_still_needs() {
    let f = Fixture::new().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let (stale, used) = two_read_repairs(&f, &repairs).await;
    let mut next = f.patch.clone();
    next.id = PatchId::new();
    next.source.message_id = Some("next@loopback".into());
    let needed = Store::charge(&next).unwrap();
    let error = repairs
        .stage(
            vec![(next, DATA.to_vec())],
            f.source.nzb(),
            // Room only through eviction.
            needed + used - 1,
            &tokio_util::sync::CancellationToken::new(),
        )
        .await
        .unwrap_err();
    assert!(error.to_string().contains("exceed"), "{error:#}");
    let store = repairs.store(u64::MAX).unwrap();
    assert_eq!(store.read(&stale).unwrap().data.as_ref(), DATA);
    assert_eq!(store.read(&f.patch).unwrap().data.as_ref(), DATA);
    assert_eq!(
        f.db.article_repairs("stale@loopback").await.unwrap(),
        [stale]
    );
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        std::slice::from_ref(&f.patch)
    );
    drop((store, repairs));
    f.close().await;
}

#[tokio::test]
async fn a_published_repair_whose_file_is_missing_or_damaged_is_rebuilt_under_its_id() {
    let f = Fixture::new().await;
    f.publish().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let path = f.root.join("repairs").join(format!("{}.patch", f.patch.id));
    for damage in [
        Box::new(|path: &std::path::Path| std::fs::remove_file(path).unwrap())
            as Box<dyn Fn(&std::path::Path)>,
        Box::new(|path: &std::path::Path| {
            let mut corrupt = std::fs::read(path).unwrap();
            *corrupt.last_mut().unwrap() ^= 1;
            std::fs::write(path, corrupt).unwrap();
        }),
    ] {
        damage(&path);
        let mut rebuilt = f.patch.clone();
        rebuilt.id = PatchId::new();
        let kept = repairs
            .unpublished(&f.source, vec![(rebuilt, DATA.to_vec())])
            .await
            .unwrap();
        assert_eq!(
            kept.iter().map(|(patch, _)| patch).collect::<Vec<_>>(),
            [&f.patch],
            "the published descriptor is staged again"
        );
        let receipts = repairs
            .stage(
                kept,
                f.source.nzb(),
                1_000_000,
                &tokio_util::sync::CancellationToken::new(),
            )
            .await
            .unwrap();
        // Publishing the same descriptor again is idempotent.
        assert!(
            f.db.publish_patches((), &f.source, std::slice::from_ref(&f.patch))
                .await
                .unwrap()
        );
        drop(receipts);
        assert_eq!(
            repairs
                .store(u64::MAX)
                .unwrap()
                .read(&f.patch)
                .unwrap()
                .data
                .as_ref(),
            DATA
        );
    }
    drop(repairs);
    f.close().await;
}

#[tokio::test]
async fn replacing_a_damaged_repair_evicts_nothing_for_its_own_bytes() {
    let f = Fixture::new().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let (stale, used) = two_read_repairs(&f, &repairs).await;
    let path = f.root.join("repairs").join(format!("{}.patch", f.patch.id));
    let mut corrupt = std::fs::read(&path).unwrap();
    *corrupt.last_mut().unwrap() ^= 1;
    std::fs::write(&path, corrupt).unwrap();
    let kept = repairs
        .unpublished(&f.source, vec![(f.patch.clone(), DATA.to_vec())])
        .await
        .unwrap();
    // A full store: only eviction of the other NZB's repair would make new room.
    let receipts = repairs
        .stage(
            kept,
            BlobId::new(),
            used,
            &tokio_util::sync::CancellationToken::new(),
        )
        .await
        .unwrap();
    assert_eq!(receipts.len(), 1);
    assert_eq!(
        f.db.article_repairs("stale@loopback").await.unwrap(),
        [stale]
    );
    let store = repairs.store(u64::MAX).unwrap();
    assert_eq!(store.read(&f.patch).unwrap().data.as_ref(), DATA);
    drop((receipts, store, repairs));
    f.close().await;
}

#[tokio::test]
async fn staging_keeps_every_repair_while_an_older_database_is_retained() {
    let f = Fixture::new().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let (stale, used) = two_read_repairs(&f, &repairs).await;
    std::fs::write(f.root.join(crate::database::PRE_POSTGRES), b"").unwrap();
    let mut next = f.patch.clone();
    next.id = PatchId::new();
    let needed = Store::charge(&next).unwrap();
    let error = repairs
        .stage(
            vec![(next, DATA.to_vec())],
            BlobId::new(),
            // Room only through eviction.
            needed + used - 1,
            &tokio_util::sync::CancellationToken::new(),
        )
        .await
        .unwrap_err();
    assert!(error.to_string().contains("retained"), "{error:#}");
    let store = repairs.store(u64::MAX).unwrap();
    assert_eq!(store.read(&stale).unwrap().data.as_ref(), DATA);
    assert_eq!(store.read(&f.patch).unwrap().data.as_ref(), DATA);
    assert_eq!(
        f.db.article_repairs("stale@loopback").await.unwrap(),
        [stale]
    );
    drop((store, repairs));
    f.close().await;
}

#[tokio::test]
async fn a_damaged_local_repair_falls_back_to_a_provider_copy() {
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::{LoopbackServer, post};
    use std::time::Duration;
    let f = Fixture::new().await;
    f.publish().await;
    let mut articles = post("fallback", "fallback.bin", DATA, DATA.len());
    articles[0].message_id = "repair@loopback".into();
    let server = LoopbackServer::start(articles).await.unwrap();
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
            max_connections: 2,
            pipeline_depth: 1,
            idle_timeout: Duration::from_secs(3),
            min_connections: 0,
        },
        Downloads::new(4),
    )
    .unwrap();
    let client = Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]);
    install(&client, f.db.clone(), f.root.join("repairs"));
    let path = f.root.join("repairs").join(format!("{}.patch", f.patch.id));
    let mut corrupt = std::fs::read(&path).unwrap();
    *corrupt.last_mut().unwrap() ^= 1;
    std::fs::write(&path, corrupt).unwrap();
    assert!(matches!(
        client.repaired("repair@loopback", None).await,
        Err(Error::Repair(_))
    ));
    // The provider's copy wins; the local error surfaces only when no provider has one.
    assert_eq!(
        client
            .body("repair@loopback", Workload::Stream, None)
            .await
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    assert_eq!(
        client
            .hedge("repair@loopback", None, None)
            .await
            .unwrap()
            .data
            .as_ref(),
        DATA
    );
    drop(client);
    drop(server);
    f.close().await;
}

#[tokio::test]
async fn an_unreadable_repair_is_left_out_of_the_backup_and_checked_after_restore() {
    use infinidysk_db::HealthState;
    let f = Fixture::new().await;
    f.publish().await;
    let client = Arc::new(Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    std::fs::remove_file(f.root.join("repairs").join(format!("{}.patch", f.patch.id))).unwrap();
    let backup = f.root.join("missing.sqlite");
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
    // The live database keeps its reference; only the backup leaves it out.
    assert_eq!(
        f.db.article_repairs("repair@loopback").await.unwrap(),
        std::slice::from_ref(&f.patch)
    );
    let restored = Db::connect(&crate::app::sqlite_url(&backup)).await.unwrap();
    assert_eq!(
        restored.article_repairs("repair@loopback").await.unwrap(),
        Vec::<Patch>::new()
    );
    let item = restored
        .item_by_path("/content/source.bin")
        .await
        .unwrap()
        .unwrap();
    let (_, health) = restored.health(item.id).await.unwrap().unwrap();
    assert_eq!(health.state, HealthState::Unchecked);
    assert_eq!(health.next_check, Some(0));
    drop(repairs);
    drop(client);
    restored.close().await;
    f.close().await;
}

#[tokio::test]
async fn rebuilt_articles_with_an_identical_published_repair_are_not_staged_again() {
    let f = Fixture::new().await;
    f.publish().await;
    let client = Client::new([]);
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    // A slice spanning the repaired article rebuilds it under a new ID.
    let mut rebuilt = f.patch.clone();
    rebuilt.id = PatchId::new();
    assert!(matches!(
        f.db.publish_patches((), &f.source, std::slice::from_ref(&rebuilt))
            .await,
        Err(infinidysk_db::Error::RepairConflict)
    ));
    let mut fresh = f.patch.clone();
    fresh.id = PatchId::new();
    fresh.source.message_id = Some("fresh@loopback".into());
    fresh.source.range = ByteRange {
        start: 8000,
        end: 8000 + DATA.len() as u64,
    };
    let mut different = rebuilt.clone();
    different.id = PatchId::new();
    different.sha256 = [9; 32];
    let kept = repairs
        .unpublished(
            &f.source,
            vec![
                (rebuilt, DATA.to_vec()),
                (fresh.clone(), DATA.to_vec()),
                (different.clone(), DATA.to_vec()),
            ],
        )
        .await
        .unwrap();
    assert_eq!(
        kept.into_iter().map(|(patch, _)| patch).collect::<Vec<_>>(),
        vec![fresh, different],
        "only new or different articles are staged; a different one still conflicts"
    );
    drop(repairs);
    f.close().await;
}

#[tokio::test]
async fn a_backup_keeps_an_embedded_payload_whose_local_file_is_missing() {
    let f = Fixture::new().await;
    f.publish().await;
    // Startup could not move an embedded payload into the store, so SQLite still holds it.
    f.db.set_repair_payload(&f.patch, Some(DATA)).await.unwrap();
    std::fs::remove_file(f.root.join("repairs").join(format!("{}.patch", f.patch.id))).unwrap();
    let client = Arc::new(Client::new([]));
    let repairs = install(&client, f.db.clone(), f.root.join("repairs"));
    let backup = f.root.join("backup.sqlite");
    repairs
        .backup_into(&backup, &tokio_util::sync::CancellationToken::new())
        .await
        .unwrap();
    let snapshot = Db::connect(&crate::app::sqlite_url(&backup)).await.unwrap();
    assert_eq!(
        snapshot
            .repair_payload(f.patch.id)
            .await
            .unwrap()
            .as_deref(),
        Some(DATA),
        "the only recoverable bytes stay in the backup"
    );
    snapshot.close().await;
    drop(repairs);
    drop(client);
    f.close().await;
}
