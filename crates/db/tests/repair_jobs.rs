#![allow(clippy::unwrap_used)] // Assertions in shared database test helpers.

use infinidysk_core::{BlobId, ItemId, ItemKind};
use infinidysk_db::{Db, Item, repair::Owner};
use sha2::{Digest as _, Sha256};

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
    assert!(db.claim_par2(12).await.unwrap().is_none());
    db.resume_par2(13).await.unwrap();
    assert_eq!(db.claim_par2(13).await.unwrap().unwrap().id, first);
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
    assert_eq!(db.claim_par2(16).await.unwrap().unwrap().id, first);
    db.cancel_par2(first, 17).await.unwrap();
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
