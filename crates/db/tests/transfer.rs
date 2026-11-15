//! The SQLite → PostgreSQL copy. Tests that need a server run when
//! `INFINIDYSK_TEST_POSTGRES_URL` points at one whose user may create databases.

use infinidysk_core::{
    BlobId, ByteRange, ItemId, ItemKind, JobId, PatchId,
    repair::{Patch, Source as Posted},
};
use infinidysk_db::transfer::{self, Capture, PgConnection};
use infinidysk_db::{
    Conversion, Db, Handoff, HealthCheck, HealthResult, HistoryEntry, Item, JobStatus, QueueEntry,
    Repair, repair::Owner,
};
use sqlx::{AssertSqlSafe, SqlitePool};
use std::path::PathBuf;
use std::time::Duration;

/// Every table, as the copy lists them.
const ALL: [&str; 19] = [
    "blobs",
    "repair_patches",
    "items",
    "queue",
    "repair_jobs",
    "legacy_repair_articles",
    "history",
    "health_checks",
    "media_probes",
    "rejected_releases",
    "rejected_articles",
    "arr_refresh",
    "search_reservations",
    "arr_owners",
    "arr_events",
    "config",
    "accounts",
    "handoff_files",
    "library_files",
];

struct Sqlite {
    db: Db,
    raw: SqlitePool,
    path: PathBuf,
}

impl Sqlite {
    async fn open() -> Self {
        let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
        let url = format!("sqlite:{}", path.display());
        let db = Db::connect(&url).await.expect("connect");
        let raw = SqlitePool::connect(&url).await.expect("open");
        Self { db, raw, path }
    }

    async fn close(self) {
        self.raw.close().await;
        self.db.close().await;
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", self.path.display()));
        }
    }

    async fn exec(&self, sql: &str) {
        sqlx::raw_sql(AssertSqlSafe(sql.to_owned()))
            .execute(&self.raw)
            .await
            .expect(sql);
    }

    /// Tables with a row in the change table.
    async fn logged(&self) -> Vec<String> {
        sqlx::query_scalar("SELECT DISTINCT tbl FROM pg_sync_changes ORDER BY tbl")
            .fetch_all(&self.raw)
            .await
            .expect("query")
    }
}

fn sorted(mut tables: Vec<&str>) -> Vec<String> {
    tables.sort_unstable();
    tables.into_iter().map(str::to_owned).collect()
}

/// A throwaway PostgreSQL database.
struct Pg {
    admin: sqlx::PgPool,
    name: String,
    url: String,
}

impl Pg {
    async fn create() -> Option<Self> {
        let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
            return None;
        };
        let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
        let admin = sqlx::PgPool::connect(&admin_url)
            .await
            .expect("connect admin");
        sqlx::query(AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&admin)
            .await
            .expect("create");
        let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
        let url = format!("{base}/{name}");
        Some(Self { admin, name, url })
    }

    /// A migrated transfer connection.
    async fn target(&self) -> PgConnection {
        let mut conn = transfer::connect(&self.url, Duration::from_secs(5))
            .await
            .expect("connect");
        transfer::migrate(&mut conn).await.expect("migrate");
        conn
    }

    async fn drop(self) {
        sqlx::query(AssertSqlSafe(format!(
            "DROP DATABASE {} WITH (FORCE)",
            self.name
        )))
        .execute(&self.admin)
        .await
        .expect("drop");
        self.admin.close().await;
    }
}

struct Seeded {
    dir: Item,
    sub: Item,
    inner: Item,
    file: Item,
    meta: BlobId,
    history: JobId,
}

/// One row or more in every table.
async fn seed(s: &Sqlite) -> Seeded {
    let db = &s.db;
    let content = db
        .item_by_path("/content")
        .await
        .expect("query")
        .expect("seeded");
    let nzb = BlobId::new();
    let meta = BlobId::new();
    let mut dir = Item::child(&content, "job", ItemKind::Directory).expect("valid");
    dir.nzb_blob = Some(nzb);
    let mut file = Item::child(&dir, "b.mkv", ItemKind::NzbFile).expect("valid");
    file.meta_blob = Some(meta);
    file.nzb_blob = Some(nzb);
    file.size = Some(5 << 32);
    let mut other = Item::child(&dir, "a.mkv", ItemKind::Multipart).expect("valid");
    other.encrypted = true;
    let sub = Item::child(&dir, "sub", ItemKind::Directory).expect("valid");
    let inner = Item::child(&sub, "c.mkv", ItemKind::NzbFile).expect("valid");
    db.insert(
        &[dir.clone(), file.clone(), other, sub.clone(), inner.clone()],
        &[(nzb, b"nzb"), (meta, b"meta")],
    )
    .await
    .expect("insert");
    let source = db
        .repair_source(Owner::Item(file.id))
        .await
        .expect("source")
        .expect("exists");
    let patch = Patch {
        id: PatchId::new(),
        source: Posted {
            set: [1; 16],
            file: [2; 16],
            range: ByteRange { start: 0, end: 3 },
            file_length: 3,
            message_id: Some("patch@loopback".into()),
            part: None,
            total: None,
        },
        sha256: [3; 32],
    };
    assert!(
        db.publish_patches(&source, &[patch])
            .await
            .expect("publish")
    );
    db.enqueue(
        &QueueEntry {
            id: JobId::new(),
            created_at: 1,
            file_name: "job.nzb".into(),
            job_name: "job".into(),
            category: "tv".into(),
            priority: 0,
            post_processing: 3,
            nzb_blob: BlobId::new(),
            nzb_size: 3,
            total_bytes: 5 << 32,
            paused: true,
            retry_at: Some(10),
            attempts: 2,
            stuck_count: 0,
            last_error: Some("busy".into()),
            content_hash: None,
        },
        b"queued nzb",
    )
    .await
    .expect("enqueue");
    let history = JobId::new();
    for (id, dir) in [(history, Some(dir.id)), (JobId::new(), None)] {
        db.add_history(
            &HistoryEntry {
                id,
                created_at: 2,
                file_name: "job.nzb".into(),
                job_name: "job".into(),
                category: "tv".into(),
                status: JobStatus::Completed,
                total_bytes: 10,
                download_seconds: 3,
                fail_message: None,
                download_dir: dir,
                nzb_blob: None,
                storage: Some("/mnt/job".into()),
            },
            &[],
            &[],
        )
        .await
        .expect("history");
    }
    db.record_health_check(&HealthCheck {
        created_at: 7,
        item_id: file.id,
        path: file.path.clone(),
        result: HealthResult::Missing,
        repair: Repair::ActionNeeded,
        message: Some("gone".into()),
    })
    .await
    .expect("health");
    db.request_media_probe(file.id, 5).await.expect("probe");
    db.reject_release("release", &["<a@b>", "<c@d>"], i64::MAX / 2)
        .await
        .expect("reject");
    s.exec("INSERT INTO arr_refresh (category, generation) VALUES ('movies', 1)")
        .await;
    let instance = uuid::Uuid::now_v7();
    assert!(
        db.reserve_searches(instance, "movie", &[1, 2], 10)
            .await
            .expect("reserve")
    );
    db.record_owner(instance, file.id, Some(7), "/tv/b.mkv", None)
        .await
        .expect("owner");
    db.push_arr_event(instance, b"{}").await.expect("event");
    db.write_config(&[("a", "1"), ("b", "2")], &[])
        .await
        .expect("config");
    assert!(db.create_admin("admin", "hash").await.expect("admin"));
    db.record_handoffs(&[Handoff {
        path: "/library/b.mkv".into(),
        item: file.id,
        job: None,
        size: None,
    }])
    .await
    .expect("handoff");
    db.put_conversion(&Conversion {
        source: "/library/x.mkv".into(),
        target: "/library/y.strm".into(),
        item: file.id,
        state: 1,
        from_link: true,
        to_link: false,
        original: "o".into(),
        written: "w".into(),
        owners: "[]".into(),
        reason: None,
    })
    .await
    .expect("conversion");
    db.request_par2(Owner::Item(file.id), false, 7)
        .await
        .expect("repair request");
    {
        use sha2::{Digest as _, Sha256};
        db.stage_legacy_repair(
            &"A".repeat(64),
            b"{}",
            b"candidate",
            &Sha256::digest(b"candidate"),
        )
        .await
        .expect("legacy candidate");
    }
    Seeded {
        dir,
        sub,
        inner,
        file,
        meta,
        history,
    }
}

#[tokio::test]
async fn capture_logs_every_table() {
    let s = Sqlite::open().await;
    s.db.start_capture().await.expect("capture");
    seed(&s).await;
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "inserts");
    assert!(s.db.pending_changes().await.expect("count") > 0);

    s.exec("DELETE FROM pg_sync_changes").await;
    for table in ALL {
        let first: String =
            sqlx::query_scalar("SELECT name FROM pragma_table_info($1) ORDER BY cid LIMIT 1")
                .bind(table)
                .fetch_one(&s.raw)
                .await
                .expect("column");
        s.exec(&format!("UPDATE {table} SET {first} = {first}"))
            .await;
    }
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "updates");

    s.exec("DELETE FROM pg_sync_changes").await;
    for table in ALL.iter().rev() {
        s.exec(&format!("DELETE FROM {table}")).await;
    }
    assert_eq!(s.logged().await, sorted(ALL.to_vec()), "deletes");
    s.close().await;
}

#[tokio::test]
async fn capture_state_follows_objects() {
    let s = Sqlite::open().await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    assert_eq!(s.db.pending_changes().await.expect("count"), 0);
    s.db.start_capture().await.expect("start");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.exec(
        "CREATE TABLE config_new (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL); \
         INSERT INTO config_new SELECT key, value FROM config; \
         DROP TABLE config; ALTER TABLE config_new RENAME TO config;",
    )
    .await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Broken);
    s.db.start_capture().await.expect("restart");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.db.stop_capture().await.expect("stop");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    s.db.stop_capture().await.expect("stop is idempotent");
    s.close().await;
}

#[tokio::test]
async fn capture_ignores_other_pg_sync_objects() {
    let s = Sqlite::open().await;
    s.exec("CREATE TABLE pg_sync_notes (note TEXT); INSERT INTO pg_sync_notes VALUES ('kept')")
        .await;
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Off);
    s.db.start_capture().await.expect("start");
    assert_eq!(s.db.capture().await.expect("capture"), Capture::Intact);
    s.db.stop_capture().await.expect("stop");
    let kept: String = sqlx::query_scalar("SELECT note FROM pg_sync_notes")
        .fetch_one(&s.raw)
        .await
        .expect("still there");
    assert_eq!(kept, "kept");
    s.close().await;
}

#[tokio::test]
async fn copy_needs_capture() {
    let s = Sqlite::open().await;
    let Some(pg) = Pg::create().await else {
        s.close().await;
        return;
    };
    let mut target = pg.target().await;
    assert!(matches!(
        s.db.copy_all(&mut target, |_| {}).await,
        Err(infinidysk_db::Error::NotCapturing)
    ));
    drop(target);
    pg.drop().await;
    s.close().await;
}

async fn sorted_ids(db: &Db) -> Vec<ItemId> {
    let mut ids = db.item_ids().await.expect("ids");
    ids.sort_unstable_by_key(|id| id.0);
    ids
}

#[tokio::test]
async fn copy_then_verify_matches() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    let reports = std::sync::Mutex::new(Vec::new());
    s.db.copy_all(&mut target, |p| reports.lock().expect("lock").push(p))
        .await
        .expect("copy");
    assert!(
        reports
            .lock()
            .expect("lock")
            .iter()
            .any(|p| p.table == "items" && p.done == p.total && p.total > 0)
    );
    assert_eq!(s.db.sync(&mut target, true, |_| {}).await.expect("sync"), 0);
    drop(target);
    let copy = Db::connect(&pg.url).await.expect("open target");
    assert_eq!(
        copy.config().await.expect("config"),
        s.db.config().await.expect("config")
    );
    assert_eq!(sorted_ids(&copy).await, sorted_ids(&s.db).await);
    assert_eq!(
        copy.admin().await.expect("admin"),
        s.db.admin().await.expect("admin")
    );
    copy.close().await;
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn changes_during_capture_reach_target() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    let seeded = seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    s.db.copy_all(&mut target, |_| {}).await.expect("copy");

    let added = Item::child(&seeded.dir, "d.mkv", ItemKind::NzbFile).expect("valid");
    s.db.insert(&[added], &[]).await.expect("insert");
    s.db.update_blob(seeded.meta, b"meta2").await.expect("blob");
    assert_eq!(
        s.db.delete_history(&[seeded.history])
            .await
            .expect("history"),
        1
    );
    // Rename a folder, then move its child out and delete the folder.
    let (sub, inner) = (seeded.sub.id.0, seeded.inner.id.0);
    let renamed = format!("{}2", seeded.sub.path);
    sqlx::query("UPDATE items SET name = 'sub2', path = $1 WHERE id = $2")
        .bind(&renamed)
        .bind(sub)
        .execute(&s.raw)
        .await
        .expect("rename");
    sqlx::query("UPDATE items SET path = $1 WHERE id = $2")
        .bind(format!("{renamed}/c.mkv"))
        .bind(inner)
        .execute(&s.raw)
        .await
        .expect("rename child");
    sqlx::query("UPDATE items SET parent_id = $1, path = $2 WHERE id = $3")
        .bind(seeded.dir.id.0)
        .bind(format!("{}/c.mkv", seeded.dir.path))
        .bind(inner)
        .execute(&s.raw)
        .await
        .expect("move");
    s.db.delete_item(seeded.sub.id)
        .await
        .expect("delete folder");
    // Swap two paths: only parking keeps the unique index happy.
    let mut tx = s.raw.begin().await.expect("begin");
    for (path, id) in [
        ("/swap".to_owned(), seeded.file.id.0),
        (seeded.file.path.clone(), inner),
        (format!("{}/c.mkv", seeded.dir.path), seeded.file.id.0),
    ] {
        sqlx::query("UPDATE items SET path = $1 WHERE id = $2")
            .bind(path)
            .bind(id)
            .execute(&mut *tx)
            .await
            .expect("swap");
    }
    tx.commit().await.expect("commit");
    s.db.set_admin("root", "hash2").await.expect("admin");
    s.db.write_config(&[("c", "3")], &["a"])
        .await
        .expect("config");

    let applied = s.db.sync(&mut target, true, |_| {}).await.expect("sync");
    assert!(applied > 0);
    assert_eq!(s.db.pending_changes().await.expect("count"), 0);
    assert_eq!(
        s.db.sync(&mut target, true, |_| {}).await.expect("again"),
        0
    );
    drop(target);
    let copy = Db::connect(&pg.url).await.expect("open target");
    assert_eq!(
        copy.admin().await.expect("admin"),
        s.db.admin().await.expect("admin")
    );
    assert_eq!(
        copy.item(seeded.inner.id)
            .await
            .expect("item")
            .map(|i| i.path),
        Some(seeded.file.path.clone())
    );
    assert!(copy.item(seeded.sub.id).await.expect("item").is_none());
    copy.close().await;
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn sync_detects_divergence() {
    let Some(pg) = Pg::create().await else { return };
    let s = Sqlite::open().await;
    seed(&s).await;
    s.db.start_capture().await.expect("capture");
    let mut target = pg.target().await;
    s.db.copy_all(&mut target, |_| {}).await.expect("copy");
    sqlx::query("UPDATE config SET value = 'changed' WHERE key = 'a'")
        .execute(&mut target)
        .await
        .expect("diverge");
    let error =
        s.db.sync(&mut target, true, |_| {})
            .await
            .expect_err("mismatch");
    assert!(matches!(error, infinidysk_db::Error::Mismatch("config")));
    assert!(error.needs_full_copy());
    drop(target);
    pg.drop().await;
    s.close().await;
}

#[tokio::test]
async fn check_target_reports_foreign_and_used_tables() {
    let Some(pg) = Pg::create().await else { return };
    let empty = transfer::check_target(&pg.url).await.expect("check");
    assert!(empty.foreign_tables.is_empty() && empty.used.is_empty());
    assert!(empty.can_create);
    assert!(!empty.version.is_empty() && !empty.collation.is_empty());

    let mut conn = pg.target().await;
    let migrated = transfer::check_target(&pg.url).await.expect("check");
    assert_eq!(migrated.used, Vec::<&str>::new());
    sqlx::query("INSERT INTO blobs (id, data) VALUES ($1, $2)")
        .bind(uuid::Uuid::now_v7())
        .bind(&b"x"[..])
        .execute(&mut conn)
        .await
        .expect("blob");
    assert_eq!(
        transfer::check_target(&pg.url).await.expect("check").used,
        ["blobs"]
    );
    sqlx::query("CREATE TABLE other (x int)")
        .execute(&mut conn)
        .await
        .expect("create");
    assert_eq!(
        transfer::check_target(&pg.url)
            .await
            .expect("check")
            .foreign_tables,
        ["other"]
    );
    drop(conn);
    pg.drop().await;
}
