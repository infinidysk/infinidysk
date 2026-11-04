//! The copy of a 1.x install on PostgreSQL. Runs when `INFINIDYSK_TEST_POSTGRES_URL` points
//! at a server whose user may create databases.

use std::os::unix::fs::PermissionsExt as _;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use infinidysk_core::ItemId;
use infinidysk_db::Db;
use infinidysk_import::postgres::{STAGED, stage};
use infinidysk_import::{Config, Error, Progress, Stage, apply, legacy_hash, survey};
use infinidysk_nntp::Client;
use sqlx::{ConnectOptions as _, Row as _};
use uuid::Uuid;

const FIXTURE: &str = include_str!("fixtures/v1-postgres.sql");
const ROWS: u64 = 17;

/// A new database on the test server, without tables when `fixture` is false.
async fn throwaway(fixture: bool) -> Option<(String, String)> {
    let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
        eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
        return None;
    };
    let name = format!("infinidysk_test_{}", Uuid::now_v7().simple());
    let admin = sqlx::PgPool::connect(&admin_url)
        .await
        .expect("connect admin");
    sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
        .execute(&admin)
        .await
        .expect("create");
    admin.close().await;
    let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
    let url = format!("{base}/{name}");
    if fixture {
        let pool = sqlx::PgPool::connect(&url).await.expect("connect");
        sqlx::raw_sql(FIXTURE)
            .execute(&pool)
            .await
            .expect("fixture");
        pool.close().await;
    }
    Some((url, name))
}

async fn drop_database(name: &str) {
    let admin_url = std::env::var("INFINIDYSK_TEST_POSTGRES_URL").expect("set");
    let admin = sqlx::PgPool::connect(&admin_url)
        .await
        .expect("connect admin");
    sqlx::query(sqlx::AssertSqlSafe(format!(
        "DROP DATABASE {name} WITH (FORCE)"
    )))
    .execute(&admin)
    .await
    .expect("drop");
    admin.close().await;
}

/// `postgres://user:pass@host:port/db` as an Npgsql connection string.
fn npgsql(url: &str) -> String {
    let rest = url.strip_prefix("postgres://").expect("postgres url");
    let (credentials, rest) = rest.split_once('@').expect("credentials");
    let (user, password) = credentials.split_once(':').expect("password");
    let (address, database) = rest.split_once('/').expect("database");
    let (host, port) = address.split_once(':').unwrap_or((address, "5432"));
    format!(
        "Host={host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Disable"
    )
}

fn temp_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!("infinidysk-v1pg-{}", Uuid::now_v7()));
    std::fs::create_dir_all(&dir).expect("create temp dir");
    dir
}

fn no_env(_: &str) -> Option<String> {
    None
}

/// Runs `stage` and returns what it reported.
async fn copy(config: &Path, connection: &str) -> (Result<(), Error>, Vec<Progress>) {
    let reports = Mutex::new(Vec::new());
    let result = stage(config, connection, &no_env, &|p| {
        reports.lock().expect("reports lock").push(p);
    })
    .await;
    (result, reports.into_inner().expect("reports lock"))
}

fn ends_complete(reports: &[Progress]) {
    let first = reports.first().expect("a report");
    assert_eq!((first.stage, first.total), (Stage::Copying, None));
    let last = reports.last().expect("a report");
    assert_eq!(
        (last.stage, last.done, last.total),
        (Stage::Copying, ROWS, Some(ROWS))
    );
}

#[tokio::test]
async fn the_copy_has_the_ef_sqlite_encoding() {
    let Some((url, name)) = throwaway(true).await else {
        return;
    };
    let dir = temp_dir();
    let (result, reports) = copy(&dir, &npgsql(&url)).await;
    result.unwrap();
    ends_complete(&reports);

    let staged = dir.join(STAGED);
    let mode = std::fs::metadata(&staged).unwrap().permissions().mode();
    assert_eq!(mode & 0o777, 0o600);
    assert!(!dir.join(format!("{STAGED}.tmp")).exists());

    let mut c = sqlx::sqlite::SqliteConnectOptions::new()
        .filename(&staged)
        .read_only(true)
        .connect()
        .await
        .unwrap();
    let row = sqlx::query(
        "SELECT Id, ParentId, CreatedAt, typeof(HealthRepairPending), HealthRepairPending, \
         FileSize, ArrDownloadId FROM DavItems WHERE Name = 'f.mkv'",
    )
    .fetch_one(&mut c)
    .await
    .unwrap();
    assert_eq!(
        row.get::<String, _>(0),
        "0198A3C2-5A10-7000-8000-00000000000C"
    );
    assert_eq!(
        row.get::<String, _>(1),
        "0198A3C2-5A10-7000-8000-00000000000B"
    );
    assert_eq!(row.get::<String, _>(2), "2026-10-02 09:50:39.5");
    assert_eq!(row.get::<String, _>(3), "integer");
    assert_eq!(row.get::<i64, _>(4), 1);
    assert_eq!(row.get::<i64, _>(5), 5_000_000_000);
    assert_eq!(row.get::<Option<String>, _>(6), None);
    let created: String = sqlx::query_scalar("SELECT CreatedAt FROM DavItems WHERE Name = 'tv'")
        .fetch_one(&mut c)
        .await
        .unwrap();
    assert_eq!(created, "2026-01-02 03:04:05");
    let columns: Vec<String> =
        sqlx::query_scalar("SELECT name FROM pragma_table_info('DavItems') ORDER BY cid")
            .fetch_all(&mut c)
            .await
            .unwrap();
    assert_eq!(
        columns,
        [
            "Id",
            "IdPrefix",
            "CreatedAt",
            "ParentId",
            "Name",
            "FileSize",
            "Type",
            "SubType",
            "Path",
            "ReleaseDate",
            "LastHealthCheck",
            "NextHealthCheck",
            "HistoryItemId",
            "FileBlobId",
            "NzbBlobId",
            "GeneratedStrmOutputRoot",
            "GeneratedStrmPath",
            "GeneratedStrmTarget",
            "GeneratedSymlinkOutputRoot",
            "GeneratedSymlinkPath",
            "GeneratedSymlinkTarget",
            "HealthRepairPending",
            "ArrDownloadId",
            "UrgentRepairFailures",
        ]
    );
    let nzb: String = sqlx::query_scalar("SELECT NzbContents FROM QueueNzbContents")
        .fetch_one(&mut c)
        .await
        .unwrap();
    assert_eq!(nzb, "<nzb>it's ü</nzb>");
    let latest: String = sqlx::query_scalar("SELECT max(MigrationId) FROM __EFMigrationsHistory")
        .fetch_one(&mut c)
        .await
        .unwrap();
    assert_eq!(
        latest,
        "20260921210000_Add-Health-Alert-History-Lookup-Index"
    );
    drop(c);

    std::fs::remove_dir_all(&dir).unwrap();
    drop_database(&name).await;
}

#[tokio::test]
async fn a_postgres_install_imports_like_a_sqlite_one() {
    let Some((url, name)) = throwaway(true).await else {
        return;
    };
    let dir = temp_dir();
    let (result, _) = copy(&dir, &npgsql(&url)).await;
    result.unwrap();

    let s = survey(&dir).await.unwrap();
    assert!(s.supported());
    assert_eq!(s.directories, 2);
    assert_eq!(s.multipart_files, 0);
    assert_eq!(s.missing_file_blobs, 1);
    assert_eq!(s.queued, 1);
    assert_eq!(s.history_completed, 1);

    let target = dir.join("target.sqlite");
    let db = Db::connect(&format!("sqlite:{}", target.display()))
        .await
        .unwrap();
    let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
    let imported = apply(&dir, &db, &client, "/mnt").await.unwrap();
    assert_eq!(imported.directories, 2);
    assert_eq!(imported.history, 1);
    assert_eq!(imported.queued, 1);
    assert_eq!(imported.skipped_count, 1);
    assert_eq!(
        imported.skip_reasons.get("metadata blob: not found"),
        Some(&1)
    );
    let job = Uuid::parse_str("0198a3c2-5a10-7000-8000-00000000000b").unwrap();
    let item = db.item_by_path("/content/tv/job").await.unwrap();
    assert_eq!(item.map(|i| i.id), Some(ItemId(job)));
    let history = db.history().await.unwrap();
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].0.download_dir, Some(ItemId(job)));
    assert_eq!(history[0].0.storage.as_deref(), Some("/mnt/tv/job"));
    db.close().await;

    let config = Config::read(&dir, Vec::new()).await.unwrap();
    assert_eq!(
        config.admin,
        Some(("admin".to_owned(), legacy_hash("hash", "salt")))
    );
    assert_eq!(
        config.get("api.key"),
        Some("0123456789abcdef0123456789abcdef")
    );

    std::fs::remove_dir_all(&dir).unwrap();
    drop_database(&name).await;
}

#[tokio::test]
async fn a_database_without_1x_tables_is_refused_by_name() {
    let Some((url, name)) = throwaway(false).await else {
        return;
    };
    let dir = temp_dir();
    let (result, _) = copy(&dir, &npgsql(&url)).await;
    assert!(matches!(
        result,
        Err(Error::MissingTable("__EFMigrationsHistory"))
    ));
    assert!(!dir.join(STAGED).exists());
    std::fs::remove_dir_all(&dir).unwrap();
    drop_database(&name).await;
}

#[tokio::test]
async fn tables_resolve_through_the_search_path() {
    let Some((url, name)) = throwaway(true).await else {
        return;
    };
    let pool = sqlx::PgPool::connect(&url).await.unwrap();
    sqlx::raw_sql("CREATE SCHEMA app")
        .execute(&pool)
        .await
        .unwrap();
    pool.close().await;
    let dir = temp_dir();
    let (result, reports) = copy(&dir, &format!("{};Search Path=app,public", npgsql(&url))).await;
    result.unwrap();
    ends_complete(&reports);
    std::fs::remove_dir_all(&dir).unwrap();
    drop_database(&name).await;
}
