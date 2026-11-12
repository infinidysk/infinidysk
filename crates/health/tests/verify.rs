use std::sync::Arc;
use std::time::Duration;

use infinidysk_core::{
    BlobId, FileMeta, ItemId, ItemKind, NzbFile, Par2Proof, Segment, Segments, blob,
};
use infinidysk_db::{Db, Item};
use infinidysk_health::{
    Availability, AvailabilityOptions, AvailabilityProgress, Evidence, Health, Settings,
};
use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
use infinidysk_testkit::{LoopbackServer, post};
use tokio::sync::watch;
use tokio_util::sync::CancellationToken;

const ARTICLE: usize = 7000;

#[tokio::test]
async fn active_playback_keeps_new_availability_checks_off_the_provider() {
    let (data, _) = content();
    let server = LoopbackServer::start(post("verify", "a.bin", &data, ARTICLE))
        .await
        .unwrap();
    let (_, item, health) = stored(&server, &data, None).await;
    let health = Arc::try_unwrap(health)
        .ok()
        .unwrap()
        .with_gates(|| false, || true);
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Yielded
    ));
    assert_eq!(server.body_commands(), 0);
    assert_eq!(server.stat_commands(), 0);
}

#[tokio::test]
async fn playback_preempts_an_availability_check_and_releases_publication() {
    use std::sync::atomic::{AtomicBool, Ordering};
    let (data, _) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_secs(3),
    )
    .await
    .unwrap();
    let (db, item, health) = stored(&server, &data, None).await;
    let playing = Arc::new(AtomicBool::new(false));
    let gate = Arc::clone(&playing);
    let health = Arc::new(
        Arc::try_unwrap(health)
            .ok()
            .unwrap()
            .with_gates(|| false, move || gate.load(Ordering::Relaxed)),
    );
    let checking = tokio::spawn({
        let health = Arc::clone(&health);
        async move {
            health
                .check_availability(
                    item.id,
                    &CancellationToken::new(),
                    AvailabilityOptions::default(),
                )
                .await
                .unwrap()
        }
    });
    wait_for_body(&server).await;
    playing.store(true, Ordering::Relaxed);
    let result = tokio::time::timeout(Duration::from_secs(1), checking)
        .await
        .expect("availability must yield when playback begins")
        .unwrap();
    assert!(matches!(result, Availability::Yielded));
    let _publication = tokio::time::timeout(Duration::from_secs(1), db.pause_writes())
        .await
        .expect("yielding releases the publication barrier");
}

#[tokio::test]
async fn archive_members_check_their_stored_ranges_without_a_par2_proof() {
    use infinidysk_core::{ByteRange, FilePart, MultipartFile};
    let (data, _) = content();
    let server = LoopbackServer::start(post("verify", "a.bin", &data, ARTICLE))
        .await
        .unwrap();
    let (db, item, health) = stored(&server, &data, None).await;
    let meta = item.meta_blob.unwrap();
    let FileMeta::Nzb(plain) = blob::decode_meta(&db.blob(meta).await.unwrap().unwrap()).unwrap()
    else {
        unreachable!()
    };
    let archive = FileMeta::Multipart(MultipartFile {
        parts: vec![FilePart {
            segments: plain.segments,
            offset: 0,
            data: ByteRange {
                start: 150,
                end: data.len() as u64 - 150,
            },
            split_after: false,
            proof: None,
        }],
        crypt: None,
    });
    db.update_blob(meta, &blob::encode_meta(&archive).unwrap())
        .await
        .unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Available { articles: 15 }
    ));
    assert_eq!(server.body_commands(), 3);
    assert_eq!(server.stat_commands(), 12);
}

#[tokio::test]
async fn availability_checks_all_articles_without_a_proof_or_a_full_download() {
    let (data, _) = content();
    let posted = post("verify", "a.bin", &data, ARTICLE);
    let total = posted.len();
    let server = LoopbackServer::start(posted).await.unwrap();
    let (_, item, health) = stored(&server, &data, None).await;
    let result = health
        .check_availability(
            item.id,
            &CancellationToken::new(),
            AvailabilityOptions::default(),
        )
        .await
        .unwrap();
    assert!(
        matches!(result, Availability::Available { .. }),
        "{result:?}"
    );
    assert!(server.body_commands() <= 3);
    assert!(server.body_commands() < total);
}

#[tokio::test]
async fn availability_probes_continue_while_a_body_spot_check_waits() {
    let (data, _) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_secs(3),
    )
    .await
    .unwrap();
    let (_, item, health) = stored(&server, &data, None).await;
    let cancel = CancellationToken::new();
    let progress = Arc::new(AvailabilityProgress::default());
    let running = tokio::spawn({
        let stop = cancel.clone();
        let progress = Arc::clone(&progress);
        async move {
            health
                .check_availability(
                    item.id,
                    &stop,
                    AvailabilityOptions {
                        progress,
                        concurrency: 16,
                        ..AvailabilityOptions::default()
                    },
                )
                .await
        }
    });
    wait_for_body(&server).await;
    let advanced = tokio::time::timeout(Duration::from_secs(1), async {
        while progress.checked.load(std::sync::atomic::Ordering::Relaxed) < 5 {
            tokio::task::yield_now().await;
        }
    })
    .await;
    cancel.cancel();
    assert!(matches!(
        running.await.unwrap().unwrap(),
        Availability::Cancelled
    ));
    assert!(
        advanced.is_ok(),
        "STAT probes must progress without a stalled BODY"
    );
}

#[tokio::test]
async fn scan_evidence_deduplicates_articles_but_a_manual_check_is_fresh() {
    let (data, _) = content();
    let server = LoopbackServer::start(post("verify", "a.bin", &data, ARTICLE))
        .await
        .unwrap();
    let (_, item, health) = stored(&server, &data, None).await;
    let evidence = Arc::new(Evidence::default());
    for _ in 0..2 {
        assert!(matches!(
            health
                .check_availability(
                    item.id,
                    &CancellationToken::new(),
                    AvailabilityOptions {
                        evidence: Some(Arc::clone(&evidence)),
                        ..AvailabilityOptions::default()
                    }
                )
                .await
                .unwrap(),
            Availability::Available { articles: 15 }
        ));
    }
    assert_eq!(server.body_commands(), 3);
    assert_eq!(server.stat_commands(), 12);
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Available { .. }
    ));
    assert_eq!(server.body_commands(), 6);
    assert_eq!(server.stat_commands(), 24);
}

fn content() -> (Vec<u8>, Par2Proof) {
    let index = infinidysk_par2::read(include_bytes!("../../par2/tests/fixtures/set.par2"));
    let file = index
        .files
        .iter()
        .find(|file| file.name == "a.bin")
        .expect("fixture describes a.bin");
    let data = (0..usize::try_from(file.length).expect("fixture length fits usize"))
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect();
    (
        data,
        Par2Proof {
            set_id: None,
            file_length: file.length,
            slice_size: u32::try_from(index.slice_size).expect("small fixture slices"),
            slice_md5: file.slices.iter().map(|slice| slice.md5).collect(),
            slice_crc32: file.slices.iter().map(|slice| slice.crc32).collect(),
            file_id: file.id,
            file_md5: file.hash,
            md5_16k: file.hash_16k,
        },
    )
}

async fn stored(
    server: &LoopbackServer,
    data: &[u8],
    proof: Option<Par2Proof>,
) -> (Db, Item, Arc<Health>) {
    stored_with_repairs(server, data, proof, None).await
}

async fn stored_with_repairs(
    server: &LoopbackServer,
    data: &[u8],
    proof: Option<Par2Proof>,
    repairs: Option<(BlobId, infinidysk_nntp::RepairLookup)>,
) -> (Db, Item, Arc<Health>) {
    let path = std::env::temp_dir().join(format!("infinidysk-verify-{}.sqlite", ItemId::new()));
    let db = Db::connect(&format!("sqlite:{}", path.display()))
        .await
        .expect("test database opens");
    let parent = db
        .item_by_path("/")
        .await
        .expect("root lookup succeeds")
        .expect("root exists");
    let name = if repairs.is_some() { "a.mkv" } else { "a.bin" };
    let mut item = Item::child(&parent, name, ItemKind::NzbFile).expect("valid test path");
    let metadata_id = BlobId::new();
    item.meta_blob = Some(metadata_id);
    item.size = Some(data.len() as u64);
    item.nzb_blob = repairs.as_ref().map(|(nzb, _)| *nzb);
    let mut meta = FileMeta::Nzb(NzbFile {
        segments: Segments {
            items: post("verify", "a.bin", data, ARTICLE)
                .iter()
                .zip(data.chunks(ARTICLE))
                .map(|(article, bytes)| Segment {
                    id: article.message_id.clone(),
                    size: u32::try_from(bytes.len()).expect("small test article"),
                    fallbacks: Vec::new(),
                })
                .collect(),
            sizes_trusted: true,
            missing: Vec::new(),
            corrupt: Vec::new(),
        },
        proof,
    });
    if repairs.is_some() {
        let FileMeta::Nzb(file) = &mut meta else {
            unreachable!()
        };
        file.segments.items[1].id.clear();
        file.segments.missing = vec![1];
    }
    let encoded = blob::encode_meta(&meta).expect("metadata encodes");
    let original = blob::encode_nzb(b"<nzb/>").expect("original NZB encodes");
    let mut blobs = vec![(metadata_id, encoded.as_slice())];
    if let Some(nzb) = item.nzb_blob {
        blobs.push((nzb, original.as_slice()));
    }
    db.insert(&[item.clone()], &blobs)
        .await
        .expect("file is stored");
    let endpoint = Endpoint::new(
        "127.0.0.1",
        server.addr().port(),
        TlsMode::Plain,
        None,
        Duration::from_secs(5),
    )
    .expect("valid loopback endpoint");
    let pool = Pool::new(
        endpoint,
        PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        },
        Downloads::new(32),
    )
    .expect("valid test pool");
    let client = Arc::new(Client::new([Provider {
        pool,
        key: String::new(),
        name: String::new(),
        priority: 0,
        backbone: None,
    }]));
    if let Some((_, lookup)) = repairs {
        client.set_repairs(lookup);
    }
    let health = Health::new(
        db.clone(),
        client,
        watch::channel(Vec::new()).1,
        watch::channel(Settings::default()).1,
    )
    .expect("health starts");
    (db, item, Arc::new(health))
}

#[tokio::test]
async fn verifies_existing_content_with_a_scoped_omitted_article() {
    use infinidysk_core::{
        ByteRange, PatchId,
        repair::{Omitted, Patch, Source},
    };
    use infinidysk_db::repair::Owner;
    use infinidysk_nntp::{Body, RepairKey};
    let (data, mut proof) = content();
    proof.set_id = Some([7; 16]);
    let key = Omitted {
        nzb: BlobId::new(),
        generation: 0,
        set: [7; 16],
        file: proof.file_id,
        range: ByteRange {
            start: ARTICLE as u64,
            end: 2 * ARTICLE as u64,
        },
        file_length: data.len() as u64,
    };
    let body = Body {
        data: bytes::Bytes::copy_from_slice(&data[ARTICLE..2 * ARTICLE]),
        offset: key.range.start,
        file_size: key.file_length,
        part: Some(2),
        total: Some(15),
    };
    let corrupt = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let fault = Arc::clone(&corrupt);
    let lookup: infinidysk_nntp::RepairLookup = Box::new(move |requested| {
        if !matches!(requested, RepairKey::Omitted(requested) if *requested == key) {
            return None;
        }
        let body = body.clone();
        let corrupt = fault.load(std::sync::atomic::Ordering::Relaxed);
        Some(Box::pin(async move {
            if corrupt {
                Err(infinidysk_nntp::Error::Repair("stored article is corrupt"))
            } else {
                Ok(Some(body))
            }
        }))
    });
    let articles = post("verify", "a.bin", &data, ARTICLE)
        .into_iter()
        .enumerate()
        .filter_map(|(i, article)| (i != 1).then_some(article));
    let server = LoopbackServer::start_stalling(articles, 0, Duration::from_millis(100))
        .await
        .unwrap();
    let (db, item, health) =
        stored_with_repairs(&server, &data, Some(proof), Some((key.nzb, lookup))).await;
    let source = db
        .repair_source(Owner::Item(item.id))
        .await
        .unwrap()
        .unwrap();
    db.publish_patches(
        &source,
        &[Patch {
            id: PatchId::new(),
            source: Source {
                set: key.set,
                file: key.file,
                range: key.range,
                file_length: key.file_length,
                message_id: None,
                part: Some(2),
                total: Some(15),
            },
            sha256: [0; 32],
        }],
    )
    .await
    .unwrap();
    let before = db.health(item.id).await.unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Available { .. }
    ));
    assert_eq!(db.health(item.id).await.unwrap(), before);
    assert!(health.step().await.unwrap());
    assert_eq!(
        db.health(item.id).await.unwrap().unwrap().1.state,
        infinidysk_db::HealthState::Healthy
    );
    let raw = db.blob(item.meta_blob.unwrap()).await.unwrap().unwrap();
    let FileMeta::Nzb(meta) = blob::decode_meta(&raw).unwrap() else {
        unreachable!()
    };
    assert_eq!(meta.segments.missing, [] as [u32; 0]);
    assert_eq!(meta.segments.items[1].id, "");
    corrupt.store(true, std::sync::atomic::Ordering::Relaxed);
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Source(infinidysk_nntp::Error::Repair(_))
    ));
    health.request_check(item.id).await.unwrap();
    assert!(health.step().await.unwrap());
    assert_eq!(
        db.health(item.id).await.unwrap().unwrap().1.state,
        infinidysk_db::HealthState::Healthy
    );
    corrupt.store(false, std::sync::atomic::Ordering::Relaxed);
    let bodies = server.body_commands();
    let running = {
        let health = Arc::clone(&health);
        let id = item.id;
        tokio::spawn(async move {
            health
                .check_availability(
                    id,
                    &CancellationToken::new(),
                    AvailabilityOptions::default(),
                )
                .await
        })
    };
    tokio::time::timeout(Duration::from_secs(3), async {
        while server.body_commands() == bodies {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("verification has captured its source");
    db.update_blob(key.nzb, b"changed original").await.unwrap();
    assert!(matches!(
        running.await.unwrap().unwrap(),
        Availability::Changed
    ));
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Damaged { .. }
    ));
}

#[tokio::test]
async fn availability_obeys_the_source_read_allowance_without_changing_health() {
    let (data, _) = content();
    let server = LoopbackServer::start(post("verify", "a.bin", &data, ARTICLE))
        .await
        .unwrap();
    let (db, item, health) = stored(&server, &data, None).await;
    let before = db.health(item.id).await.unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions {
                    decoded_bytes: 0,
                    ..AvailabilityOptions::default()
                }
            )
            .await
            .unwrap(),
        Availability::Unavailable("source read allowance exceeded")
    ));
    assert_eq!(server.body_commands(), 0);
    assert_eq!(db.health(item.id).await.unwrap(), before);
}

#[tokio::test]
async fn availability_does_not_require_slice_checksums() {
    let (data, mut proof) = content();
    proof.slice_md5.clear();
    proof.slice_crc32.clear();
    proof.slice_size = 0;
    let server = LoopbackServer::start(post("verify", "a.bin", &data, ARTICLE))
        .await
        .unwrap();
    let (_, item, health) = stored(&server, &data, Some(proof)).await;
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Available { .. }
    ));
}

#[tokio::test]
async fn missing_articles_are_detected_without_a_proof_or_health_changes() {
    let (data, _) = content();
    let server = LoopbackServer::start([]).await.unwrap();
    let (db, item, health) = stored(&server, &data, None).await;
    let before = db.health(item.id).await.unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Damaged { .. }
    ));
    assert!(server.body_commands() <= 3);
    assert_eq!(db.health(item.id).await.unwrap(), before);
}

#[tokio::test]
async fn availability_does_not_claim_a_whole_file_checksum_check() {
    let (data, proof) = content();
    let mut wrong = data.clone();
    wrong[9000] ^= 1;
    let server = LoopbackServer::start(post("verify", "a.bin", &wrong, ARTICLE))
        .await
        .unwrap();
    let (db, item, health) = stored(&server, &data, Some(proof)).await;
    let before = db.health(item.id).await.unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Available { .. }
    ));
    assert_eq!(db.health(item.id).await.unwrap(), before);
}

async fn wait_for_body(server: &LoopbackServer) {
    tokio::time::timeout(Duration::from_secs(3), async {
        while server.body_commands() == 0 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("verification started its read");
}

#[tokio::test]
async fn metadata_changed_during_verification_invalidates_the_result() {
    let (data, proof) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_millis(200),
    )
    .await
    .unwrap();
    let (db, item, health) = stored(&server, &data, Some(proof)).await;
    let id = item.id;
    let running = tokio::spawn(async move {
        health
            .check_availability(
                id,
                &CancellationToken::new(),
                AvailabilityOptions::default(),
            )
            .await
    });
    wait_for_body(&server).await;
    let blob_id = item.meta_blob.unwrap();
    let mut meta = blob::decode_meta(&db.blob(blob_id).await.unwrap().unwrap()).unwrap();
    let FileMeta::Nzb(ref mut file) = meta else {
        panic!("plain fixture");
    };
    file.segments.items[0].id = "replacement@loopback".to_owned();
    db.update_blob(blob_id, &blob::encode_meta(&meta).unwrap())
        .await
        .unwrap();
    assert!(matches!(
        running.await.unwrap().unwrap(),
        Availability::Changed
    ));
}

#[tokio::test]
async fn publication_drains_existing_verification_and_holds_new_verification() {
    let (data, proof) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_millis(150),
    )
    .await
    .unwrap();
    let (db, item, health) = stored(&server, &data, Some(proof)).await;
    let checking = tokio::spawn({
        let health = Arc::clone(&health);
        async move {
            health
                .check_availability(
                    item.id,
                    &CancellationToken::new(),
                    AvailabilityOptions::default(),
                )
                .await
                .unwrap()
        }
    });
    wait_for_body(&server).await;
    assert!(
        tokio::time::timeout(Duration::from_millis(15), db.pause_writes())
            .await
            .is_err()
    );
    assert!(matches!(
        checking.await.unwrap(),
        Availability::Available { .. }
    ));
    let publication = db.pause_writes().await;
    let cancel = CancellationToken::new();
    let checking = health.check_availability(item.id, &cancel, AvailabilityOptions::default());
    tokio::pin!(checking);
    assert!(
        tokio::time::timeout(Duration::from_millis(15), &mut checking)
            .await
            .is_err()
    );
    drop(publication);
    assert!(matches!(
        checking.await.unwrap(),
        Availability::Available { .. }
    ));
    db.close().await;
}

#[tokio::test]
async fn cancelling_a_pending_body_returns_without_waiting_for_the_server() {
    let (data, proof) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_secs(30),
    )
    .await
    .unwrap();
    let (_, item, health) = stored(&server, &data, Some(proof)).await;
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    let running = tokio::spawn(async move {
        health
            .check_availability(item.id, &stop, AvailabilityOptions::default())
            .await
    });
    wait_for_body(&server).await;
    cancel.cancel();
    assert!(matches!(
        tokio::time::timeout(Duration::from_secs(2), running)
            .await
            .unwrap()
            .unwrap()
            .unwrap(),
        Availability::Cancelled
    ));
}

#[tokio::test]
async fn missing_articles_cannot_verify_even_when_expected_content_is_zero() {
    let (_, mut proof) = content();
    let data = vec![0; 4096];
    proof.file_length = data.len() as u64;
    proof.file_md5 = infinidysk_par2::hash_16k(&data);
    proof.slice_md5.clear();
    proof.slice_crc32.clear();
    proof.slice_size = 0;
    let server = LoopbackServer::start([]).await.unwrap();
    let (db, item, health) = stored(&server, &data, Some(proof)).await;
    let before = db.health(item.id).await.unwrap();
    assert!(matches!(
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default()
            )
            .await
            .unwrap(),
        Availability::Damaged { .. }
    ));
    assert!(server.body_commands() > 0);
    assert_eq!(db.health(item.id).await.unwrap(), before);
}

#[tokio::test]
async fn aborting_the_caller_releases_verification_admission() {
    let (data, proof) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_secs(30),
    )
    .await
    .unwrap();
    let (_, item, health) = stored(&server, &data, Some(proof)).await;
    let cancel = CancellationToken::new();
    let running = {
        let health = Arc::clone(&health);
        let cancel = cancel.clone();
        tokio::spawn(async move {
            health
                .check_availability(item.id, &cancel, AvailabilityOptions::default())
                .await
        })
    };
    wait_for_body(&server).await;
    running.abort();
    assert!(running.await.unwrap_err().is_cancelled());
    assert!(!cancel.is_cancelled());
    assert!(matches!(
        tokio::time::timeout(
            Duration::from_secs(3),
            // Admission must reopen even while the old NNTP connection drains.
            health.check_availability(ItemId::new(), &cancel, AvailabilityOptions::default())
        )
        .await
        .unwrap()
        .unwrap(),
        Availability::Unavailable(_)
    ));
    assert!(!cancel.is_cancelled());
}

#[tokio::test]
async fn unchanged_metadata_remains_valid_after_a_blob_epoch_change() {
    let (data, proof) = content();
    let server = LoopbackServer::start_stalling(
        post("verify", "a.bin", &data, ARTICLE),
        0,
        Duration::from_millis(200),
    )
    .await
    .unwrap();
    let (db, item, health) = stored(&server, &data, Some(proof)).await;
    let running = tokio::spawn(async move {
        health
            .check_availability(
                item.id,
                &CancellationToken::new(),
                AvailabilityOptions::default(),
            )
            .await
    });
    wait_for_body(&server).await;
    let blob_id = item.meta_blob.unwrap();
    let bytes = db.blob(blob_id).await.unwrap().unwrap();
    db.update_blob(blob_id, &bytes).await.unwrap();
    assert!(matches!(
        running.await.unwrap().unwrap(),
        Availability::Available { .. }
    ));
}
