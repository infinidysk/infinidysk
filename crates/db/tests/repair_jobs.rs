#![allow(clippy::unwrap_used)] // Assertions in shared database test helpers.

use infinidysk_core::{BlobId, ItemId, ItemKind};
use infinidysk_db::{Db, Item, repair::Owner};
use sha2::{Digest as _, Sha256};

#[tokio::test]
async fn manual_checks_overtake_a_scan_without_starving_it_or_waiting_for_repairs() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    lane_contract(&db).await;
}

#[tokio::test]
async fn damage_handoff_releases_a_slot_before_queuing_repair_at_capacity() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    handoff_at_capacity(&db).await;
}

async fn handoff_at_capacity(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let nzb = BlobId::new();
    let mut items = Vec::new();
    let mut blobs = vec![(nzb, b"nzb".as_slice())];
    for n in 0..128 {
        let mut item = Item::child(&root, &format!("handoff-{n}.bin"), ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        item.meta_blob = Some(meta);
        item.nzb_blob = Some(nzb);
        blobs.push((meta, b"meta"));
        items.push(item);
    }
    db.insert(&items, &blobs).await.unwrap();
    for item in &items {
        db.request_par2_scan(item.id, 10, 1, true).await.unwrap();
    }
    assert_eq!(db.par2_pending().await.unwrap(), 128);
    let check = db.claim_par2_lane(11, true, true).await.unwrap().unwrap();
    assert!(db.escalate_par2(check.id, 12).await.unwrap());
    assert_eq!(db.par2_pending().await.unwrap(), 128);
    let repair = db.claim_par2_lane(13, false, false).await.unwrap().unwrap();
    assert_eq!(repair.nzb_blob, Some(nzb.0));
    assert_eq!(repair.scan_at, Some(1));
    assert!(!db.escalate_par2(check.id, 14).await.unwrap());
    let next = db.claim_par2_lane(14, true, true).await.unwrap().unwrap();
    db.cancel_par2(next.id, 15).await.unwrap();
    assert!(!db.escalate_par2(next.id, 16).await.unwrap());
}

async fn lane_contract(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut ids = Vec::new();
    for n in 0..6 {
        let mut item = Item::child(&root, &format!("lane-{n}.bin"), ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        let nzb = BlobId::new();
        item.meta_blob = Some(meta);
        item.nzb_blob = Some(nzb);
        db.insert(&[item.clone()], &[(meta, b"meta"), (nzb, b"nzb")])
            .await
            .unwrap();
        let id = if n < 2 {
            db.request_par2_scan(item.id, 10 + n, 1, true)
                .await
                .unwrap()
                .unwrap()
        } else {
            db.request_par2(Owner::Item(item.id), n != 5, 100 + n)
                .await
                .unwrap()
                .unwrap()
        };
        ids.push(id);
    }
    let repair = db
        .claim_par2_lane(1000, false, false)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(repair.id, ids[5]);
    for expected in &ids[2..5] {
        let claimed = db
            .claim_par2_lane(1000, true, false)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            claimed.id, *expected,
            "manual work must overtake older scan requests"
        );
    }
    let scan = db.claim_par2_lane(1000, true, true).await.unwrap().unwrap();
    assert_eq!(scan.id, ids[0]);
    assert_eq!(scan.scan_at, Some(1));
    assert_eq!(
        scan.next_at, 10,
        "enqueue time stays separate from the scan receipt scope"
    );
    assert!(scan.repair_on_failure);
    db.update_availability_progress(scan.id, 2, 10)
        .await
        .unwrap();
    assert!(
        db.update_availability_progress(scan.id, 11, 10)
            .await
            .is_err()
    );
    db.finish_par2(scan.id, 2, "available", 0, 0, 1001, None)
        .await
        .unwrap();
    db.restart_par2(scan.id, 1002).await.unwrap();
    let manual = db
        .claim_par2_lane(1003, true, false)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(manual.id, scan.id);
    assert_eq!(manual.scan_at, None);
    assert_eq!(manual.checked_articles, 0);
    db.yield_par2_attempt(manual.id, manual.attempts)
        .await
        .unwrap();
    db.yield_par2_attempt(manual.id, manual.attempts)
        .await
        .unwrap();
    let yielded = db
        .par2_jobs()
        .await
        .unwrap()
        .into_iter()
        .find(|job| job.id == manual.id)
        .unwrap();
    assert_eq!(
        yielded.attempts, 0,
        "playback must not consume a repair retry"
    );
    let before = db
        .par2_counts()
        .await
        .unwrap()
        .into_iter()
        .find(|row| row.0 == 5)
        .unwrap_or((5, 0, 0, 0));
    db.finish_par2(
        manual.id,
        5,
        "Missing or corrupt articles found.",
        0,
        0,
        1004,
        None,
    )
    .await
    .unwrap();
    db.finish_par2(
        ids[2],
        5,
        "repair unavailable: memory allowance",
        0,
        0,
        1004,
        None,
    )
    .await
    .unwrap();
    db.finish_par2(
        repair.id,
        5,
        "repair unavailable: source matching",
        0,
        0,
        1004,
        None,
    )
    .await
    .unwrap();
    let after = db
        .par2_counts()
        .await
        .unwrap()
        .into_iter()
        .find(|row| row.0 == 5)
        .unwrap();
    assert_eq!(
        after,
        (5, before.1 + 3, before.2 + 2, before.3 + 1),
        "keep total job counts compatible while separating check errors, damage and repair failures"
    );
}

#[tokio::test]
async fn repair_requests_are_durable_deduplicated_and_claimed_once() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    durable_requests(&db).await;
}

async fn durable_requests(db: &Db) {
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "source.bin", ItemKind::NzbFile).unwrap();
    let nzb = BlobId::new();
    let meta = BlobId::new();
    item.nzb_blob = Some(nzb);
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(nzb, b"nzb"), (meta, b"meta")])
        .await
        .unwrap();
    let first = db
        .request_par2(Owner::Item(item.id), false, 10)
        .await
        .unwrap()
        .unwrap();
    let duplicate = db
        .request_par2(Owner::Item(item.id), false, 11)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(first, duplicate);
    let job = db.claim_par2(12).await.unwrap().unwrap();
    assert_eq!(job.id, first);
    db.update_par2_progress(first, "Discovering PAR2 files", 40, 0, 13)
        .await
        .unwrap();
    db.update_par2_requests(first, 12, 10).await.unwrap();
    assert!(db.update_par2_requests(first, 9, 10).await.is_err());
    assert!(db.update_par2_requests(first, 12, -1).await.is_err());
    let listed = db.par2_jobs().await.unwrap();
    assert_eq!(listed.len(), 1);
    assert_eq!(
        (listed[0].completed_requests, listed[0].missing_requests),
        (12, 10)
    );
    assert_eq!(
        (listed[0].transferred_bytes, listed[0].started_at),
        (40, Some(12))
    );
    assert_eq!(db.par2_counts().await.unwrap(), vec![(1, 1, 0, 0)]);
    assert!(db.claim_par2(12).await.unwrap().is_none());
    db.resume_par2(13).await.unwrap();
    let resumed = db.claim_par2(13).await.unwrap().unwrap();
    assert_eq!(resumed.id, first);
    assert_eq!(
        (resumed.completed_requests, resumed.missing_requests),
        (12, 10)
    );
    db.finish_par2(first, 2, "verified", 100, 0, 14, None)
        .await
        .unwrap();
    assert!(db.claim_par2(15).await.unwrap().is_none());
    assert_eq!(
        db.par2_result(Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        2
    );
    db.restart_par2(first, 16).await.unwrap();
    let restarted = db.claim_par2(16).await.unwrap().unwrap();
    assert_eq!(restarted.id, first);
    assert_eq!(
        (restarted.completed_requests, restarted.missing_requests),
        (0, 0)
    );
    db.cancel_par2(first, 17).await.unwrap();
    db.update_par2_progress(first, "Late progress", 999, 0, 18)
        .await
        .unwrap();
    assert_eq!(db.par2_jobs().await.unwrap()[0].reason, "cancelled");
    db.finish_par2(first, 3, "late completion", 200, 50, 18, None)
        .await
        .unwrap();
    let duration = db
        .par2_result(Owner::Item(item.id), false)
        .await
        .unwrap()
        .unwrap()
        .duration_ms;
    db.finish_par2(first, 3, "late completion", 200, 50, 19, None)
        .await
        .unwrap();
    assert_eq!(
        db.par2_result(Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .duration_ms,
        duration
    );
    assert_eq!(
        db.par2_result(Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        6
    );
    db.restart_par2(first, 20).await.unwrap();
    db.set_nzb_retention_days(0);
    db.delete_item(item.id).await.unwrap();
    assert_eq!(
        db.blob(nzb).await.unwrap().as_deref(),
        Some(b"nzb".as_slice())
    );
    db.cancel_par2(first, 21).await.unwrap();
    assert!(db.blob(nzb).await.unwrap().is_none());
    let items = (0..25)
        .map(|n| {
            let mut item =
                Item::child(&root, &format!("status-{n}.bin"), ItemKind::NzbFile).unwrap();
            item.meta_blob = Some(BlobId::new());
            item
        })
        .collect::<Vec<_>>();
    let blobs = items
        .iter()
        .map(|item| (item.meta_blob.unwrap(), b"meta".as_slice()))
        .collect::<Vec<_>>();
    db.insert(&items, &blobs).await.unwrap();
    for (n, item) in items.iter().enumerate() {
        db.request_par2(Owner::Item(item.id), true, 30)
            .await
            .unwrap();
        if n < 22 {
            let job = db.claim_par2(31).await.unwrap().unwrap();
            db.finish_par2(job.id, 2, "", 0, 0, 32, None).await.unwrap();
        }
    }
    let jobs = db.par2_jobs().await.unwrap();
    assert_eq!(
        jobs.len(),
        23,
        "all three pending jobs and the latest twenty results"
    );
    assert_eq!(jobs.iter().filter(|job| job.state == 0).count(), 3);
    assert!(jobs.iter().all(|job| job.file_path.is_some()));
}

#[tokio::test]
async fn postgres_requests_obey_the_same_claim_resume_and_cancellation_contract() {
    let Ok(url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
        return;
    };
    let admin = sqlx::PgPool::connect(&url).await.unwrap();
    let name = format!("infinidysk_jobs_{}", uuid::Uuid::new_v4().simple());
    sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
        .execute(&admin)
        .await
        .unwrap();
    let (base, _) = url.rsplit_once('/').unwrap();
    let db = Db::connect(&format!("{base}/{name}")).await.unwrap();
    durable_requests(&db).await;
    lane_contract(&db).await;
    handoff_at_capacity(&db).await;
    db.close().await;
    sqlx::query(sqlx::AssertSqlSafe(format!(
        "DROP DATABASE {name} WITH (FORCE)"
    )))
    .execute(&admin)
    .await
    .unwrap();
    admin.close().await;
}

#[tokio::test]
async fn existing_files_can_queue_verification_without_an_original_nzb() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "existing.bin", ItemKind::NzbFile).unwrap();
    let meta = BlobId::new();
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(meta, b"meta")])
        .await
        .unwrap();
    assert!(
        db.request_par2(Owner::Item(item.id), false, 10)
            .await
            .unwrap()
            .is_none()
    );
    let id = db
        .request_par2(Owner::Item(item.id), true, 10)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(db.claim_par2(10).await.unwrap().unwrap().id, id);
    db.update_blob(meta, b"new metadata").await.unwrap();
    assert!(
        db.par2_result(Owner::Item(item.id), true)
            .await
            .unwrap()
            .is_none()
    );
}

#[tokio::test]
async fn library_scan_pages_surviving_files_by_id_with_a_fixed_boundary() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut items = Vec::new();
    for i in 0..40 {
        let mut item = Item::child(&parent, &format!("file-{i}.bin"), ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        item.meta_blob = Some(meta);
        db.insert(&[item.clone()], &[(meta, b"metadata")])
            .await
            .unwrap();
        items.push(item.id);
    }
    items.sort();
    let boundary = db.par2_scan_boundary().await.unwrap().unwrap();
    let first = db.par2_scan_page(None, boundary, i64::MAX).await.unwrap();
    assert_eq!(first, items[..16]);
    db.delete_item(items[2]).await.unwrap();
    let next = db
        .par2_scan_page(first.last().copied(), boundary, i64::MAX)
        .await
        .unwrap();
    assert_eq!(next, items[16..32]);
}

#[tokio::test]
async fn legacy_candidates_are_durable_and_not_visible_as_committed_repairs() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    db.stage_legacy_repair(
        &"A".repeat(64),
        b"header",
        b"candidate",
        &Sha256::digest(b"candidate"),
    )
    .await
    .unwrap();
    let saved = db.legacy_repair(&"A".repeat(64)).await.unwrap().unwrap();
    assert_eq!(saved.1, b"candidate");
    assert_eq!(db.article_repairs("candidate@loopback").await.unwrap(), []);
}
