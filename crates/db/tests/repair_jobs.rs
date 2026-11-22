#![allow(clippy::unwrap_used)] // Assertions in shared database test helpers.

use infinidysk_core::{BlobId, ItemId, ItemKind};
use infinidysk_db::{Db, Error, Item, repair::Owner, repair_jobs::QUEUE_CAPACITY};
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

#[tokio::test]
async fn the_queue_takes_its_last_job_and_refuses_the_next() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    capacity_bounds(&db).await;
}

async fn capacity_bounds(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut items = Vec::new();
    for n in 0..QUEUE_CAPACITY + 2 {
        let mut item = Item::child(&root, &format!("capacity-{n}.bin"), ItemKind::NzbFile).unwrap();
        let (meta, nzb) = (BlobId::new(), BlobId::new());
        item.meta_blob = Some(meta);
        item.nzb_blob = Some(nzb);
        db.insert(&[item.clone()], &[(meta, b"meta"), (nzb, b"nzb")])
            .await
            .unwrap();
        items.push(item.id);
    }
    let last = usize::try_from(QUEUE_CAPACITY - 1).unwrap();
    for &item in &items[..last] {
        db.request_par2_scan(item, 10, 1, true)
            .await
            .unwrap()
            .unwrap();
    }
    let repair = db
        .request_par2(Owner::Item(items[last]), false, 10)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(db.par2_pending().await.unwrap(), QUEUE_CAPACITY);
    let full = |r: Result<Option<uuid::Uuid>, Error>| matches!(r, Err(Error::RepairQueueFull));
    let next = Owner::Item(items[last + 1]);
    assert!(full(db.request_par2(next, false, 11).await));
    assert!(full(db.request_par2(next, true, 11).await));
    assert!(full(
        db.request_par2_scan(items[last + 1], 11, 1, true).await
    ));
    // A repeated request names the job it already has.
    assert_eq!(
        db.request_par2(Owner::Item(items[last]), false, 11)
            .await
            .unwrap(),
        Some(repair)
    );
    db.cancel_par2(repair, 12).await.unwrap();
    let check = db.request_par2(next, true, 13).await.unwrap().unwrap();
    assert!(matches!(
        db.restart_par2(repair, 14).await,
        Err(Error::RepairQueueFull)
    ));
    db.cancel_par2(check, 15).await.unwrap();
    db.restart_par2(repair, 16).await.unwrap();
    assert_eq!(db.par2_pending().await.unwrap(), QUEUE_CAPACITY);
}

#[tokio::test]
async fn repair_history_counts_only_final_repairs_inside_the_window() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    history_contract(&db).await;
}

async fn history_contract(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    for (n, (state, check)) in [
        (3, false),
        (2, false),
        (5, false),
        (6, false),
        (2, true),
        (5, true),
        (1, false),
    ]
    .into_iter()
    .enumerate()
    {
        let mut item = Item::child(&root, &format!("history-{n}.bin"), ItemKind::NzbFile).unwrap();
        let meta = BlobId::new();
        let nzb = BlobId::new();
        item.meta_blob = Some(meta);
        item.nzb_blob = Some(nzb);
        db.insert(&[item.clone()], &[(meta, b"meta"), (nzb, b"nzb")])
            .await
            .unwrap();
        let id = db
            .request_par2(Owner::Item(item.id), check, 100)
            .await
            .unwrap()
            .unwrap();
        let claim = db
            .claim_par2_lane(100, check, false)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(claim.id, id);
        if state != 1 {
            db.finish_par2(
                claim.claim(),
                state,
                "result",
                0,
                0,
                100 + i64::try_from(n).unwrap(),
                None,
                0,
            )
            .await
            .unwrap();
        }
    }
    assert_eq!(db.par2_history(100, 106).await.unwrap(), (1, 1, 1, 1));
    assert_eq!(db.par2_history(101, 103).await.unwrap(), (1, 0, 1, 1));
    assert_eq!(db.par2_history(100, 100).await.unwrap(), (0, 1, 0, 0));
    assert_eq!(db.par2_history(107, 108).await.unwrap(), (0, 0, 0, 0));
    assert_eq!(db.par2_history(103, 101).await.unwrap(), (0, 0, 0, 0));
}

async fn handoff_at_capacity(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let nzb = BlobId::new();
    let mut items = Vec::new();
    let mut blobs = vec![(nzb, b"nzb".as_slice())];
    for n in 0..QUEUE_CAPACITY {
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
    assert_eq!(db.par2_pending().await.unwrap(), QUEUE_CAPACITY);
    let check = db.claim_par2_lane(11, true, true).await.unwrap().unwrap();
    assert!(db.escalate_par2(check.claim(), 12).await.unwrap());
    assert_eq!(db.par2_pending().await.unwrap(), QUEUE_CAPACITY);
    let repair = db.claim_par2_lane(13, false, false).await.unwrap().unwrap();
    assert_eq!(repair.nzb_blob, Some(nzb.0));
    assert_eq!(repair.scan_at, Some(1));
    assert!(!db.escalate_par2(check.claim(), 14).await.unwrap());
    let next = db.claim_par2_lane(14, true, true).await.unwrap().unwrap();
    db.cancel_par2(next.id, 15).await.unwrap();
    assert!(!db.escalate_par2(next.claim(), 16).await.unwrap());
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
    let mut manual_claims = Vec::new();
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
        manual_claims.push(claimed.claim());
    }
    let scan = db.claim_par2_lane(1000, true, true).await.unwrap().unwrap();
    assert_eq!(scan.id, ids[0]);
    assert_eq!(scan.scan_at, Some(1));
    assert_eq!(
        scan.next_at, 10,
        "enqueue time stays separate from the scan receipt scope"
    );
    assert!(scan.repair_on_failure);
    db.update_availability_progress(scan.claim(), 2, 10)
        .await
        .unwrap();
    assert!(
        db.update_availability_progress(scan.claim(), 11, 10)
            .await
            .is_err()
    );
    db.finish_par2(scan.claim(), 2, "available", 0, 0, 1001, None, 0)
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
    for _ in 0..2 {
        db.yield_par2(manual.claim(), manual.attempts, "waiting", 0, 0, 1003)
            .await
            .unwrap();
    }
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
    assert_eq!(
        (yielded.state, yielded.next_at),
        (4, 1003),
        "the refund and the deferral are saved together"
    );
    let manual = db
        .claim_par2_lane(1003, true, false)
        .await
        .unwrap()
        .unwrap();
    assert_eq!((manual.id, manual.attempts), (yielded.id, 1));
    let before = db
        .par2_counts()
        .await
        .unwrap()
        .into_iter()
        .find(|row| row.0 == 5)
        .unwrap_or((5, 0, 0, 0));
    db.finish_par2(
        manual.claim(),
        5,
        "Missing or corrupt articles found.",
        0,
        0,
        1004,
        None,
        infinidysk_db::repair_jobs::DAMAGED,
    )
    .await
    .unwrap();
    db.finish_par2(
        manual_claims[0],
        5,
        "repair unavailable: memory allowance",
        0,
        0,
        1004,
        None,
        0,
    )
    .await
    .unwrap();
    db.finish_par2(
        repair.claim(),
        5,
        "repair unavailable: source matching",
        0,
        0,
        1004,
        None,
        0,
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
    let outcome = |jobs: Vec<infinidysk_db::repair_jobs::Job>| {
        jobs.into_iter()
            .find(|job| job.id == manual.id)
            .unwrap()
            .outcome
    };
    assert_eq!(
        outcome(db.par2_jobs().await.unwrap()),
        infinidysk_db::repair_jobs::DAMAGED
    );
    db.restart_par2(manual.id, 1005).await.unwrap();
    assert_eq!(outcome(db.par2_jobs().await.unwrap()), 0);
}

#[tokio::test]
async fn repair_requests_are_durable_deduplicated_and_claimed_once() {
    let path = std::env::temp_dir().join(format!("infinidysk-jobs-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let mut db = Db::connect(&url).await.unwrap();
    durable_requests(&mut db, &url).await;
    db.close().await;
    for suffix in ["", "-wal", "-shm"] {
        let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
    }
}

async fn durable_requests(db: &mut Db, url: &str) {
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
    db.update_par2_progress(job.claim(), "Discovering PAR2 files", 40, 0, 13)
        .await
        .unwrap();
    db.update_par2_requests(job.claim(), 12, 10).await.unwrap();
    assert!(db.update_par2_requests(job.claim(), 9, 10).await.is_err());
    assert!(db.update_par2_requests(job.claim(), 12, -1).await.is_err());
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
    // The running claim must survive a restart before it resumes.
    db.close().await;
    *db = Db::connect(url).await.unwrap();
    db.resume_par2(13).await.unwrap();
    let resumed = db.claim_par2(13).await.unwrap().unwrap();
    assert_eq!(resumed.id, first);
    assert_eq!(
        (resumed.completed_requests, resumed.missing_requests),
        (12, 10)
    );
    db.finish_par2(resumed.claim(), 2, "verified", 100, 0, 14, None, 0)
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
    db.update_par2_progress(restarted.claim(), "Late progress", 999, 0, 18)
        .await
        .unwrap();
    assert_eq!(db.par2_jobs().await.unwrap()[0].reason, "cancelled");
    db.finish_par2(
        restarted.claim(),
        3,
        "late completion",
        200,
        50,
        18,
        None,
        0,
    )
    .await
    .unwrap();
    let duration = db
        .par2_result(Owner::Item(item.id), false)
        .await
        .unwrap()
        .unwrap()
        .duration_ms;
    db.finish_par2(
        restarted.claim(),
        3,
        "late completion",
        999,
        99,
        19,
        None,
        0,
    )
    .await
    .unwrap();
    db.update_par2_requests(restarted.claim(), 9, 1)
        .await
        .unwrap();
    db.update_availability_progress(restarted.claim(), 2, 9)
        .await
        .unwrap();
    db.set_par2_verified(restarted.claim(), 900).await.unwrap();
    let final_job = db
        .par2_result(Owner::Item(item.id), false)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        (
            final_job.transferred_bytes,
            final_job.stored_bytes,
            final_job.updated_at
        ),
        (200, 50, 18)
    );
    assert_eq!(
        (
            final_job.completed_requests,
            final_job.checked_articles,
            final_job.verified_bytes
        ),
        (0, 0, 0)
    );
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
            db.finish_par2(job.claim(), 2, "", 0, 0, 32, None, 0)
                .await
                .unwrap();
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
async fn a_stale_success_cannot_revive_a_cancelled_or_move_a_deferred_job() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    stale_retries(&db).await;
}

async fn stale_retries(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "retry.bin", ItemKind::NzbFile).unwrap();
    let (nzb, meta) = (BlobId::new(), BlobId::new());
    item.nzb_blob = Some(nzb);
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(nzb, b"nzb"), (meta, b"meta")])
        .await
        .unwrap();
    let owner = Owner::Item(item.id);
    let id = db.request_par2(owner, false, 10).await.unwrap().unwrap();
    let job = |db: &Db| {
        let db = db.clone();
        async move { db.par2_result(owner, false).await.unwrap().unwrap() }
    };
    let claimed = db.claim_par2_lane(10, false, false).await.unwrap().unwrap();
    db.finish_par2(claimed.claim(), 3, "repaired", 0, 0, 11, None, 0)
        .await
        .unwrap();
    let observed = job(db).await;
    db.restart_par2(id, 12).await.unwrap();
    db.cancel_par2(id, 13).await.unwrap();
    assert!(!db.retry_par2(observed.claim(), 14).await.unwrap());
    assert_eq!(job(db).await.state, 6, "the cancellation stands");

    db.restart_par2(id, 15).await.unwrap();
    let claimed = db.claim_par2_lane(15, false, false).await.unwrap().unwrap();
    db.finish_par2(claimed.claim(), 3, "repaired", 0, 0, 16, None, 0)
        .await
        .unwrap();
    let observed = job(db).await;
    db.restart_par2(id, 17).await.unwrap();
    let claimed = db.claim_par2_lane(17, false, false).await.unwrap().unwrap();
    db.finish_par2(claimed.claim(), 4, "busy", 0, 0, 18, Some(1_000), 0)
        .await
        .unwrap();
    assert!(!db.retry_par2(observed.claim(), 19).await.unwrap());
    let deferred = job(db).await;
    assert_eq!(
        (deferred.state, deferred.next_at),
        (4, 1_000),
        "the cooldown stands"
    );

    db.cancel_par2(id, 20).await.unwrap();
    db.restart_par2(id, 21).await.unwrap();
    let claimed = db.claim_par2_lane(21, false, false).await.unwrap().unwrap();
    db.finish_par2(claimed.claim(), 2, "verified", 0, 0, 22, None, 0)
        .await
        .unwrap();
    assert!(db.retry_par2(job(db).await.claim(), 23).await.unwrap());
    assert_eq!(job(db).await.state, 0, "an unchanged result queues again");
}

#[tokio::test]
async fn cancelled_attempt_cannot_finish_or_refund_a_restarted_claim() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    stale_claims(&db).await;
}

#[tokio::test]
async fn verification_after_nzb_restoration_gets_a_current_source_job() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    verification_restoration(&db).await;
}

async fn verification_restoration(db: &Db) {
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "restored.bin", ItemKind::NzbFile).unwrap();
    let meta = BlobId::new();
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(meta, b"meta")])
        .await
        .unwrap();
    let old = db
        .request_par2(Owner::Item(item.id), true, 10)
        .await
        .unwrap()
        .unwrap();
    let claimed = db.claim_par2_lane(10, true, false).await.unwrap().unwrap();
    assert_eq!(claimed.id, old);
    let old_claim = claimed.claim();
    let nzb = BlobId::new();
    db.insert(&[], &[(nzb, b"nzb")]).await.unwrap();
    assert_eq!(
        db.restore_nzb_links(&[(item.id, nzb)], &[]).await.unwrap(),
        1
    );
    let current = db
        .request_par2(Owner::Item(item.id), true, 11)
        .await
        .unwrap()
        .unwrap();
    assert_ne!(old, current);
    let claimed = db.claim_par2_lane(11, true, false).await.unwrap().unwrap();
    assert_eq!(claimed.id, current);
    let source = db
        .repair_source(Owner::Item(item.id))
        .await
        .unwrap()
        .unwrap();
    let epoch = db.blob_epoch();
    assert!(
        !db.publish_repair_metadata(&source, b"stale", Some(old_claim))
            .await
            .unwrap()
    );
    assert_eq!(db.blob_epoch(), epoch);
    assert!(
        db.publish_repair_metadata(&source, b"new-meta", Some(claimed.claim()))
            .await
            .unwrap()
    );
    let jobs = db.par2_jobs().await.unwrap();
    let generation = |id| {
        jobs.iter()
            .find(|job| job.id == id)
            .unwrap()
            .meta_generation
    };
    assert_eq!(
        (generation(old), generation(current)),
        (Some(0), Some(1)),
        "a check made without the NZB must keep its metadata generation"
    );
    assert_eq!(
        db.request_par2(Owner::Item(item.id), true, 12)
            .await
            .unwrap(),
        Some(current)
    );
    assert_eq!(
        db.par2_result(Owner::Item(item.id), true)
            .await
            .unwrap()
            .unwrap()
            .id,
        current
    );
}

#[tokio::test]
async fn a_stale_check_cannot_escalate_a_restarted_claim() {
    let db = Db::connect("sqlite::memory:").await.unwrap();
    escalation_fencing(&db).await;
}

async fn escalation_fencing(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "escalate.bin", ItemKind::NzbFile).unwrap();
    let (nzb, meta) = (BlobId::new(), BlobId::new());
    item.nzb_blob = Some(nzb);
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(nzb, b"nzb"), (meta, b"meta")])
        .await
        .unwrap();
    let id = db
        .request_par2(Owner::Item(item.id), true, 10)
        .await
        .unwrap()
        .unwrap();
    let old = db.claim_par2_lane(10, true, false).await.unwrap().unwrap();
    assert_eq!(old.id, id);
    db.cancel_par2(id, 11).await.unwrap();
    db.restart_par2(id, 12).await.unwrap();
    let current = db.claim_par2_lane(12, true, false).await.unwrap().unwrap();
    assert_eq!(current.id, id);
    assert!(!db.escalate_par2(old.claim(), 13).await.unwrap());
    let source = db
        .repair_source(Owner::Item(item.id))
        .await
        .unwrap()
        .unwrap();
    assert!(
        db.publish_repair_metadata(&source, b"new-meta", None)
            .await
            .unwrap()
    );
    assert!(!db.escalate_par2(current.claim(), 14).await.unwrap());
}

async fn stale_claims(db: &Db) {
    for job in db.par2_jobs().await.unwrap() {
        db.cancel_par2(job.id, 0).await.unwrap();
    }
    let root = db.item(ItemId::CONTENT).await.unwrap().unwrap();
    let mut item = Item::child(&root, "claim.bin", ItemKind::NzbFile).unwrap();
    let nzb = BlobId::new();
    let meta = BlobId::new();
    item.nzb_blob = Some(nzb);
    item.meta_blob = Some(meta);
    db.insert(&[item.clone()], &[(nzb, b"nzb"), (meta, b"meta")])
        .await
        .unwrap();
    let id = db
        .request_par2(Owner::Item(item.id), false, 10)
        .await
        .unwrap()
        .unwrap();
    let old = db.claim_par2(10).await.unwrap().unwrap();
    db.cancel_par2(id, 11).await.unwrap();
    db.restart_par2(id, 12).await.unwrap();
    let current = db.claim_par2_lane(12, false, false).await.unwrap().unwrap();
    assert_eq!(old.attempts, current.attempts);
    assert!(current.claim_generation > old.claim_generation);
    db.yield_par2(old.claim(), old.attempts, "stale", 999, 99, 13)
        .await
        .unwrap();
    db.update_par2_progress(old.claim(), "stale", 999, 99, 13)
        .await
        .unwrap();
    db.update_par2_requests(old.claim(), 999, 99).await.unwrap();
    db.update_availability_progress(old.claim(), 99, 999)
        .await
        .unwrap();
    db.set_par2_verified(old.claim(), 999).await.unwrap();
    assert!(!db.escalate_par2(old.claim(), 13).await.unwrap());
    db.finish_par2(old.claim(), 3, "stale", 999, 99, 13, None, 0)
        .await
        .unwrap();
    let after = db
        .par2_jobs()
        .await
        .unwrap()
        .into_iter()
        .find(|job| job.id == id)
        .unwrap();
    assert_eq!(
        (after.state, after.attempts, after.transferred_bytes),
        (1, 1, 0)
    );
    assert_eq!(
        (
            after.completed_requests,
            after.checked_articles,
            after.verified_bytes
        ),
        (0, 0, 0)
    );
    db.update_par2_progress(current.claim(), "current", 42, 0, 14)
        .await
        .unwrap();
    db.finish_par2(current.claim(), 2, "verified", 42, 0, 15, None, 0)
        .await
        .unwrap();
    assert_eq!(
        db.par2_result(Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        2
    );
    db.restart_par2_scan(id, 16, Some(16), false).await.unwrap();
    let restarted = db
        .par2_jobs()
        .await
        .unwrap()
        .into_iter()
        .find(|job| job.id == id)
        .unwrap();
    assert!(restarted.claim_generation > current.claim_generation);
    let next = db.claim_par2(16).await.unwrap().unwrap();
    assert!(next.claim_generation > restarted.claim_generation);
    db.finish_par2(current.claim(), 5, "late", 999, 0, 17, None, 0)
        .await
        .unwrap();
    db.finish_par2(next.claim(), 2, "verified", 0, 0, 17, None, 0)
        .await
        .unwrap();
    assert_eq!(
        db.par2_result(Owner::Item(item.id), false)
            .await
            .unwrap()
            .unwrap()
            .state,
        2
    );
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
    let mut db = Db::connect(&format!("{base}/{name}")).await.unwrap();
    durable_requests(&mut db, &format!("{base}/{name}")).await;
    lane_contract(&db).await;
    history_contract(&db).await;
    handoff_at_capacity(&db).await;
    capacity_bounds(&db).await;
    stale_claims(&db).await;
    stale_retries(&db).await;
    escalation_fencing(&db).await;
    verification_restoration(&db).await;
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
    let path = std::env::temp_dir().join(format!("infinidysk-legacy-{}.sqlite", ItemId::new()));
    let url = format!("sqlite:{}", path.display());
    let db = Db::connect(&url).await.unwrap();
    db.stage_legacy_repair(
        &"A".repeat(64),
        b"header",
        b"candidate",
        &Sha256::digest(b"candidate"),
        |_, _| true,
    )
    .await
    .unwrap();
    db.close().await;
    let db = Db::connect(&url).await.unwrap();
    let saved = db.legacy_repair(&"A".repeat(64)).await.unwrap().unwrap();
    assert_eq!(saved.1, b"candidate");
    assert_eq!(db.repair_files(None).await.unwrap(), []);
    db.close().await;
    std::fs::remove_file(path).unwrap();
}
