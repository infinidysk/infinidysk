//! Explicit library verification enumerates stable IDs; startup never starts a scan.

use anyhow::Result;
use infinidysk_core::ItemId;
use infinidysk_db::{Db, repair::Owner};
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;

const KEY: &str = "par2.scan";

#[derive(Default, Serialize, Deserialize, ToSchema)]
pub struct Scan {
    #[schema(value_type = Option<uuid::Uuid>)]
    pub boundary: Option<ItemId>,
    #[schema(value_type = Option<uuid::Uuid>)]
    pub cursor: Option<ItemId>,
    pub requested_at: i64,
    pub scanned_files: u64,
    pub complete: bool,
    #[serde(default)]
    pub cancelled: bool,
    #[serde(default)]
    pub repair: bool,
    #[serde(default)]
    pub total_files: Option<u64>,
    #[serde(default)]
    pub skipped_files: u64,
}

pub async fn load(db: &Db) -> Result<Option<Scan>> {
    db.config()
        .await?
        .into_iter()
        .find(|(key, _)| key == KEY)
        .map(|(_, value)| serde_json::from_str(&value))
        .transpose()
        .map_err(Into::into)
}

pub async fn save(db: &Db, scan: &Scan) -> Result<()> {
    db.write_config(&[(KEY, &serde_json::to_string(scan)?)], &[])
        .await?;
    Ok(())
}

pub async fn step(db: &Db) -> Result<()> {
    let Some((_, mut expected)) = db.config().await?.into_iter().find(|(key, _)| key == KEY) else {
        return Ok(());
    };
    let mut scan: Scan = serde_json::from_str(&expected)?;
    if scan.complete || db.par2_pending().await? >= 96 {
        return Ok(());
    }
    let Some(boundary) = scan.boundary else {
        scan.complete = true;
        db.advance_par2_scan(&expected, &serde_json::to_string(&scan)?)
            .await?;
        return Ok(());
    };
    let files = db
        .par2_scan_page(scan.cursor, boundary, scan.requested_at)
        .await?;
    if files.is_empty() {
        scan.complete = true;
    }
    for id in files {
        let now = infinidysk_db::now_ms();
        let verify = !scan.repair;
        let request = db.request_par2(Owner::Item(id), verify, now).await?;
        if request.is_none() {
            scan.skipped_files += 1;
        }
        if let Some(request) = request
            && db
                .par2_result(Owner::Item(id), verify)
                .await?
                .is_some_and(|job| {
                    job.updated_at < scan.requested_at && matches!(job.state, 2 | 3 | 5 | 6)
                })
        {
            db.restart_par2(request, now).await?;
        }
        scan.cursor = Some(id);
        scan.scanned_files += 1;
        // The request commits before its scan cursor advances. Repeating it is idempotent.
        let next = serde_json::to_string(&scan)?;
        if !db.advance_par2_scan(&expected, &next).await? {
            return Ok(());
        }
        expected = next;
    }
    db.advance_par2_scan(&expected, &serde_json::to_string(&scan)?)
        .await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn scans_start_explicitly_resume_in_batches_and_preserve_cancellation() {
        check_scan("sqlite::memory:").await;
    }

    #[tokio::test]
    async fn postgres_scan_resumes_after_reconnecting() {
        let Ok(url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            return;
        };
        let admin = sqlx::PgPool::connect(&url).await.unwrap();
        let name = format!("infinidysk_scan_{}", uuid::Uuid::new_v4().simple());
        sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&admin)
            .await
            .unwrap();
        let (base, _) = url.rsplit_once('/').unwrap();
        check_scan(&format!("{base}/{name}")).await;
        sqlx::query(sqlx::AssertSqlSafe(format!(
            "DROP DATABASE {name} WITH (FORCE)"
        )))
        .execute(&admin)
        .await
        .unwrap();
    }

    async fn check_scan(url: &str) {
        let mut db = Db::connect(url).await.unwrap();
        let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
        for n in 0..20 {
            let mut item = infinidysk_db::Item::child(
                &parent,
                &format!("file-{n}.bin"),
                infinidysk_core::ItemKind::NzbFile,
            )
            .unwrap();
            let meta = infinidysk_core::BlobId::new();
            item.meta_blob = Some(meta);
            db.insert(&[item], &[(meta, b"metadata")]).await.unwrap();
        }
        step(&db).await.unwrap();
        assert_eq!(db.par2_pending().await.unwrap(), 0);
        let scan = Scan {
            boundary: db.par2_scan_boundary().await.unwrap(),
            requested_at: infinidysk_db::now_ms() + 1,
            ..Scan::default()
        };
        assert_eq!(
            db.par2_scan_total(scan.boundary.unwrap(), scan.requested_at)
                .await
                .unwrap(),
            20
        );
        save(&db, &scan).await.unwrap();
        step(&db).await.unwrap();
        assert_eq!(load(&db).await.unwrap().unwrap().scanned_files, 16);
        let finished = db
            .claim_par2(infinidysk_db::now_ms())
            .await
            .unwrap()
            .unwrap();
        db.finish_par2(finished.id, 2, "", 0, 0, infinidysk_db::now_ms(), None)
            .await
            .unwrap();
        if db.is_postgres() {
            db.close().await;
            db = Db::connect(url).await.unwrap();
        }
        step(&db).await.unwrap();
        assert_eq!(db.par2_pending().await.unwrap(), 19);
        step(&db).await.unwrap();
        assert!(load(&db).await.unwrap().unwrap().complete);
        let mut stopped = load(&db).await.unwrap().unwrap();
        stopped.cancelled = true;
        save(&db, &stopped).await.unwrap();
        step(&db).await.unwrap();
        assert!(load(&db).await.unwrap().unwrap().cancelled);
        assert_eq!(db.par2_pending().await.unwrap(), 19);
        db.close().await;
    }

    #[tokio::test]
    async fn repair_backlog_deduplicates_source_sets_and_resumes_older_scan_documents() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        let parent = db.item(ItemId::CONTENT).await.unwrap().unwrap();
        let nzb = infinidysk_core::BlobId::new();
        let mut items = Vec::new();
        let mut blobs = vec![(nzb, b"nzb".as_slice())];
        for n in 0..3 {
            let mut item = infinidysk_db::Item::child(
                &parent,
                &format!("source-{n}.bin"),
                infinidysk_core::ItemKind::NzbFile,
            )
            .unwrap();
            let meta = infinidysk_core::BlobId::new();
            item.meta_blob = Some(meta);
            blobs.push((meta, b"metadata".as_slice()));
            item.nzb_blob = (n < 2).then_some(nzb);
            items.push(item);
        }
        db.insert(&items, &blobs).await.unwrap();
        let scan = Scan {
            boundary: db.par2_scan_boundary().await.unwrap(),
            requested_at: infinidysk_db::now_ms() + 1,
            repair: true,
            ..Scan::default()
        };
        save(&db, &scan).await.unwrap();
        step(&db).await.unwrap();
        step(&db).await.unwrap();
        assert_eq!(load(&db).await.unwrap().unwrap().scanned_files, 3);
        assert_eq!(load(&db).await.unwrap().unwrap().skipped_files, 1);
        let jobs = db.par2_jobs().await.unwrap();
        assert_eq!(jobs.len(), 1);
        assert!(!jobs[0].verify_only);
        assert_eq!(jobs[0].nzb_blob, Some(nzb.0));
        let old: Scan = serde_json::from_str(r#"{"boundary":null,"cursor":null,"requested_at":0,"scanned_files":0,"complete":false}"#).unwrap();
        assert!(!old.repair);
        assert_eq!(old.total_files, None);
    }
}
