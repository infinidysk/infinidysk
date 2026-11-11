//! SQLite and PostgreSQL storage behind one API.
//!
//! Queries are runtime-checked strings written in the SQL both backends accept (`$N`
//! placeholders, `ON CONFLICT`, recursive CTEs), so each lives once and runs on the
//! native driver of either backend. The exception is PostgreSQL row locking (`FOR UPDATE`),
//! which SQLite lacks and does not need with its single writer; it runs behind a backend check.

use std::path::PathBuf;
use std::str::FromStr;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use infinidysk_core::{BlobId, ItemId, ItemKind, JobId};
use sqlx::postgres::{PgPool, PgPoolOptions};
use sqlx::sqlite::{
    SqliteConnectOptions, SqliteJournalMode, SqlitePool, SqlitePoolOptions, SqliteSynchronous,
};
use tokio::sync::{Mutex, OwnedRwLockReadGuard, OwnedRwLockWriteGuard, RwLock, broadcast};
use uuid::Uuid;

mod backup;
pub mod legacy_repairs;
pub mod metrics;
pub mod repair;
pub mod repair_jobs;
pub mod transfer;

pub use backup::{FileCheck, check_file, latest_sqlite_schema};

/// The SQLite migrations; [`check_file`] compares a backup's history with them.
static SQLITE_MIGRATOR: sqlx::migrate::Migrator = sqlx::migrate!("./migrations/sqlite");

/// Buffered change notices per subscriber before it lags.
const CHANGES: usize = 256;
/// Files per health state; `items_health_summary` covers it.
const HEALTH_SUMMARY: &str = "SELECT health_state, CAST(COUNT(*) AS BIGINT) FROM items \
                              WHERE kind <> $1 AND meta_blob IS NOT NULL GROUP BY health_state";
/// Searches we may ask an Arr to run for one piece of media per [`SEARCH_WINDOW_MS`],
/// shared by health repair and queue management.
pub const SEARCH_LIMIT: i64 = 3;
pub const SEARCH_WINDOW_MS: i64 = 30 * 60 * 1000;
/// How long a SQLite write waits for another connection's lock before it fails.
pub const BUSY_TIMEOUT: Duration = Duration::from_secs(10);

/// A SQLite file's size and how much of it is unused pages.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SqliteStats {
    pub path: PathBuf,
    pub size_bytes: u64,
    pub free_bytes: u64,
}

/// Refreshes the query planner statistics of tables that changed enough to need it.
async fn sqlite_optimize(pool: &SqlitePool) -> Result<(), Error> {
    sqlx::query("PRAGMA optimize=0x10002").execute(pool).await?;
    Ok(())
}

async fn sqlite_stats(pool: &SqlitePool) -> Result<SqliteStats, Error> {
    let (path, size, free): (String, i64, i64) = sqlx::query_as(
        "SELECT (SELECT file FROM pragma_database_list WHERE name = 'main'), \
         c.page_count * s.page_size, f.freelist_count * s.page_size \
         FROM pragma_page_count c, pragma_page_size s, pragma_freelist_count f",
    )
    .fetch_one(pool)
    .await?;
    Ok(SqliteStats {
        path: path.into(),
        size_bytes: u64::try_from(size).unwrap_or(0),
        free_bytes: u64::try_from(free).unwrap_or(0),
    })
}

/// Rewrites the file without its unused pages, then shrinks the WAL back. Blocks writers
/// on other connections until it ends.
async fn sqlite_compact(pool: &SqlitePool) -> Result<(), Error> {
    sqlx::query("VACUUM").execute(pool).await?;
    sqlite_optimize(pool).await?;
    sqlx::query("PRAGMA wal_checkpoint(TRUNCATE)")
        .execute(pool)
        .await?;
    Ok(())
}

/// Which of an Arr instance's library files.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum OwnedFile {
    Id(i64),
    Path(String),
    /// Every file under this folder.
    Under(String),
}

/// An Arr library file that is one of our items.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ArrOwner {
    pub item: ItemId,
    /// Absent where the Arr sends none.
    pub file_id: Option<i64>,
    pub path: String,
}

/// `$head <$2 matches the file> $tail`, as one static SQL string.
macro_rules! by_file {
    ($which:expr, $head:literal, $tail:literal) => {
        match $which {
            OwnedFile::Id(_) => concat!($head, "file_id = $2", $tail),
            OwnedFile::Path(_) => concat!($head, "path = $2", $tail),
            OwnedFile::Under(_) => concat!($head, "substr(path, 1, length($2)) = $2", $tail),
        }
    };
}

fn under(folder: &str) -> String {
    format!("{}/", folder.trim_end_matches('/'))
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// Shows what the driver reported (e.g. `(code: 517) database is locked`,
    /// `pool timed out …`); a fixed text hid the cause from logs and run reports.
    #[error(transparent)]
    Sqlx(#[from] sqlx::Error),
    #[error("database migration failed")]
    Migrate(#[from] sqlx::migrate::MigrateError),
    #[error(
        "the database was created by another build: one from an earlier pre-release must be \
         recreated (back it up first), one from a newer version needs that version"
    )]
    OtherSchema(#[source] sqlx::migrate::MigrateError),
    #[error("database url must start with sqlite: or postgres:")]
    UnsupportedUrl,
    #[error("invalid item name {0:?}")]
    InvalidName(String),
    #[error("stored row is invalid: {0}")]
    Corrupt(&'static str),
    #[error("this needs a SQLite database")]
    NotSqlite,
    #[error("stopped on request")]
    Interrupted,
    #[error("the disk is full")]
    DiskFull,
    #[error("this needs a PostgreSQL database")]
    NotPostgres,
    #[error("change capture is not running")]
    NotCapturing,
    #[error("{0} differs between SQLite and PostgreSQL")]
    Mismatch(&'static str),
    #[error("column {0} has a type the copy does not handle")]
    UnsupportedColumn(String),
    #[error("repair descriptor failed validation")]
    RepairDescriptor(#[from] infinidysk_core::repair::Error),
    #[error("repair publication exceeds its {0} bound")]
    RepairBounds(&'static str),
    #[error("repair publication conflicts with an immutable reference")]
    RepairConflict,
}

impl Error {
    /// The target may differ from SQLite in a way only a full copy repairs.
    #[must_use]
    pub fn needs_full_copy(&self) -> bool {
        match self {
            Self::Mismatch(_) | Self::NotCapturing => true,
            Self::Sqlx(sqlx::Error::Database(e)) => {
                e.code().is_some_and(|code| code.starts_with("23"))
            }
            _ => false,
        }
    }
}

/// A migration history this build does not know is another build's schema.
fn migrated(result: Result<(), sqlx::migrate::MigrateError>) -> Result<(), Error> {
    match result {
        Err(
            e @ (sqlx::migrate::MigrateError::VersionMissing(_)
            | sqlx::migrate::MigrateError::VersionMismatch(_)),
        ) => Err(Error::OtherSchema(e)),
        result => Ok(result?),
    }
}

#[derive(Clone, Debug)]
pub struct Db {
    pool: Pool,
    changes: broadcast::Sender<Change>,
    // ponytail: serializes reservations within this process; one process owns the
    // database. Multiple writers would need a row lock per media.
    searches: Arc<Mutex<()>>,
    /// See [`Db::set_nzb_retention_days`].
    nzb_retention_ms: Arc<AtomicI64>,
    /// See [`Db::write_permit`].
    writes: Arc<RwLock<()>>,
    /// See [`Db::blob_epoch`].
    blob_epoch: Arc<AtomicU64>,
    repair_present: Arc<AtomicBool>,
}

#[derive(Clone, Debug)]
enum Pool {
    Sqlite(SqlitePool),
    Postgres(PgPool),
}

/// What a write touched, for live UI updates.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Change {
    Queue,
    History,
    Health,
}

/// Runs `$body` with `$pool` bound to the native pool of whichever backend is active.
macro_rules! on {
    ($db:expr, $pool:ident => $body:expr) => {
        match &$db.pool {
            Pool::Sqlite($pool) => $body,
            Pool::Postgres($pool) => $body,
        }
    };
}

pub(crate) use on;

/// A file or directory in the virtual tree.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Item {
    pub id: ItemId,
    /// `None` only for the root.
    pub parent: Option<ItemId>,
    pub name: String,
    pub path: String,
    pub kind: ItemKind,
    pub size: Option<u64>,
    pub meta_blob: Option<BlobId>,
    pub nzb_blob: Option<BlobId>,
    /// Unix milliseconds.
    pub created_at: i64,
    /// When the release was posted, Unix milliseconds.
    pub release_date: Option<i64>,
    pub health_state: HealthState,
    /// An encrypted archive member, read through AES.
    pub encrypted: bool,
}

impl Item {
    /// A new child of `parent`; `name` must be one non-empty path component.
    pub fn child(parent: &Self, name: &str, kind: ItemKind) -> Result<Self, Error> {
        if name.is_empty() || name == "." || name == ".." || name.contains(['/', '\0']) {
            return Err(Error::InvalidName(name.to_owned()));
        }
        let sep = if parent.path.ends_with('/') { "" } else { "/" };
        Ok(Self {
            id: ItemId::new(),
            parent: Some(parent.id),
            name: name.to_owned(),
            path: format!("{}{sep}{name}", parent.path),
            kind,
            size: None,
            meta_blob: None,
            nzb_blob: None,
            created_at: now_ms(),
            release_date: None,
            health_state: HealthState::Unchecked,
            encrypted: false,
        })
    }
}

#[derive(sqlx::FromRow)]
struct ItemRow {
    id: Uuid,
    parent_id: Option<Uuid>,
    name: String,
    path: String,
    kind: i16,
    size: Option<i64>,
    meta_blob: Option<Uuid>,
    nzb_blob: Option<Uuid>,
    created_at: i64,
    release_date: Option<i64>,
    health_state: i16,
    encrypted: i16,
}

impl TryFrom<ItemRow> for Item {
    type Error = Error;

    fn try_from(r: ItemRow) -> Result<Self, Error> {
        Ok(Self {
            id: ItemId(r.id),
            parent: r.parent_id.map(ItemId),
            name: r.name,
            path: r.path,
            kind: ItemKind::try_from(r.kind).map_err(|_| Error::Corrupt("unknown item kind"))?,
            size: r
                .size
                .map(|s| u64::try_from(s).map_err(|_| Error::Corrupt("negative item size")))
                .transpose()?,
            meta_blob: r.meta_blob.map(BlobId),
            nzb_blob: r.nzb_blob.map(BlobId),
            created_at: r.created_at,
            release_date: r.release_date,
            health_state: HealthState::try_from(r.health_state)?,
            encrypted: r.encrypted != 0,
        })
    }
}

/// Where an item is in the health state machine. Stored as `SMALLINT`; values must never
/// change.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(i16)]
pub enum HealthState {
    #[default]
    Unchecked = 0,
    Healthy = 1,
    /// Confirmed damage small enough to play through.
    Degraded = 2,
    /// One confirmed miss, awaiting the confirming re-check.
    Suspect = 3,
    Repairing = 4,
    /// An Arr is replacing it; not served.
    Hidden = 5,
    ActionNeeded = 6,
    /// Not media (subtitles, NFOs, images…); never checked.
    Ignored = 7,
}

impl HealthState {
    pub const ALL: [Self; 8] = [
        Self::Unchecked,
        Self::Healthy,
        Self::Degraded,
        Self::Suspect,
        Self::Repairing,
        Self::Hidden,
        Self::ActionNeeded,
        Self::Ignored,
    ];
}

impl TryFrom<i16> for HealthState {
    type Error = Error;

    fn try_from(v: i16) -> Result<Self, Error> {
        Self::ALL
            .into_iter()
            .find(|s| *s as i16 == v)
            .ok_or(Error::Corrupt("unknown health state"))
    }
}

/// Why an item is due before its schedule. Stored as `SMALLINT`; ordered by urgency.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord)]
#[repr(i16)]
pub enum Recheck {
    #[default]
    None = 0,
    User = 1,
    Playback = 2,
}

/// An item's health bookkeeping besides its state.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct HealthStatus {
    pub state: HealthState,
    /// Unix milliseconds.
    pub last_check: Option<i64>,
    /// Unix milliseconds; also when a repair or reconciliation step is next due.
    pub next_check: Option<i64>,
    pub repair_attempts: i16,
    /// Unix milliseconds.
    pub hidden_until: Option<i64>,
    pub recheck: Recheck,
    /// Unix milliseconds of the first confirmed article miss; stored only while `suspect`.
    pub suspect_since: Option<i64>,
}

#[derive(sqlx::FromRow)]
struct HealthWorkRow {
    #[sqlx(flatten)]
    item: ItemRow,
    last_health_check: Option<i64>,
    next_health_check: Option<i64>,
    repair_attempts: i16,
    hidden_until: Option<i64>,
    recheck_requested: i16,
    suspect_since: Option<i64>,
}

impl TryFrom<HealthWorkRow> for (Item, HealthStatus) {
    type Error = Error;

    fn try_from(r: HealthWorkRow) -> Result<Self, Error> {
        let item = Item::try_from(r.item)?;
        let status = HealthStatus {
            state: item.health_state,
            last_check: r.last_health_check,
            next_check: r.next_health_check,
            repair_attempts: r.repair_attempts,
            hidden_until: r.hidden_until,
            recheck: match r.recheck_requested {
                0 => Recheck::None,
                1 => Recheck::User,
                2 => Recheck::Playback,
                _ => return Err(Error::Corrupt("unknown recheck reason")),
            },
            suspect_since: r.suspect_since,
        };
        Ok((item, status))
    }
}

/// A download waiting to be imported.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct QueueEntry {
    pub id: JobId,
    pub created_at: i64,
    /// The NZB's name as submitted.
    pub file_name: String,
    pub job_name: String,
    pub category: String,
    pub priority: i16,
    pub post_processing: i16,
    pub nzb_blob: BlobId,
    pub nzb_size: u64,
    pub total_bytes: u64,
    /// Held until resumed; separate from `retry_at`.
    pub paused: bool,
    /// Unix milliseconds before which the job is not retried.
    pub retry_at: Option<i64>,
    /// Attempts that ended in a transient error.
    pub attempts: u32,
    /// Attempts cancelled for making no progress.
    pub stuck_count: u32,
    /// Why the last attempt was retried.
    pub last_error: Option<String>,
    /// Hex SHA-256 of the NZB's message-ids; a submission matching it returns this job.
    pub content_hash: Option<String>,
}

#[derive(sqlx::FromRow)]
struct QueueRow {
    id: Uuid,
    created_at: i64,
    file_name: String,
    job_name: String,
    category: String,
    priority: i16,
    post_processing: i16,
    nzb_blob: Uuid,
    nzb_size: i64,
    total_bytes: i64,
    paused: bool,
    retry_at: Option<i64>,
    attempts: i32,
    stuck_count: i32,
    last_error: Option<String>,
    content_hash: Option<String>,
}

impl From<QueueRow> for QueueEntry {
    fn from(r: QueueRow) -> Self {
        Self {
            id: JobId(r.id),
            created_at: r.created_at,
            file_name: r.file_name,
            job_name: r.job_name,
            category: r.category,
            priority: r.priority,
            post_processing: r.post_processing,
            nzb_blob: BlobId(r.nzb_blob),
            nzb_size: r.nzb_size.cast_unsigned(),
            total_bytes: r.total_bytes.cast_unsigned(),
            paused: r.paused,
            retry_at: r.retry_at,
            attempts: r.attempts.try_into().unwrap_or(0),
            stuck_count: r.stuck_count.try_into().unwrap_or(0),
            last_error: r.last_error,
            content_hash: r.content_hash,
        }
    }
}

/// Stored as `SMALLINT`; values must never change.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i16)]
pub enum JobStatus {
    Completed = 1,
    Failed = 2,
}

/// A finished download.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HistoryEntry {
    pub id: JobId,
    pub created_at: i64,
    pub file_name: String,
    pub job_name: String,
    pub category: String,
    pub status: JobStatus,
    pub total_bytes: u64,
    pub download_seconds: i32,
    pub fail_message: Option<String>,
    /// The job's directory under `/content`, while it exists.
    pub download_dir: Option<ItemId>,
    pub nzb_blob: Option<BlobId>,
    /// The SAB `storage` path fixed when the job was handed off; `None` derives it from
    /// the mount.
    pub storage: Option<String>,
}

#[derive(sqlx::FromRow)]
struct HistoryRow {
    id: Uuid,
    created_at: i64,
    file_name: String,
    job_name: String,
    category: String,
    status: i16,
    total_bytes: i64,
    download_seconds: i32,
    fail_message: Option<String>,
    download_dir: Option<Uuid>,
    nzb_blob: Option<Uuid>,
    storage: Option<String>,
}

impl TryFrom<HistoryRow> for HistoryEntry {
    type Error = Error;

    fn try_from(r: HistoryRow) -> Result<Self, Error> {
        Ok(Self {
            id: JobId(r.id),
            created_at: r.created_at,
            file_name: r.file_name,
            job_name: r.job_name,
            category: r.category,
            status: match r.status {
                1 => JobStatus::Completed,
                2 => JobStatus::Failed,
                _ => return Err(Error::Corrupt("unknown job status")),
            },
            total_bytes: r.total_bytes.cast_unsigned(),
            download_seconds: r.download_seconds,
            fail_message: r.fail_message,
            download_dir: r.download_dir.map(ItemId),
            nzb_blob: r.nzb_blob.map(BlobId),
            storage: r.storage,
        })
    }
}

#[derive(sqlx::FromRow)]
struct HistoryListRow {
    #[sqlx(flatten)]
    entry: HistoryRow,
    dir_path: Option<String>,
}

/// Filters and page for [`Db::history_page`]; `None` matches anything.
#[derive(Clone, Copy, Debug, Default)]
pub struct HistoryQuery<'a> {
    /// Only these jobs.
    pub ids: Option<&'a [JobId]>,
    pub category: Option<&'a str>,
    /// Case-insensitive substring of the job or file name.
    pub search: Option<&'a str>,
    /// As reported: a completed job whose folder is gone counts as failed.
    pub status: Option<JobStatus>,
    pub offset: usize,
    /// `None` means all.
    pub limit: Option<usize>,
}

/// Filters for [`Db::health_checks`]; `None` matches anything.
#[derive(Clone, Copy, Debug, Default)]
pub struct HealthFilter<'a> {
    /// Case-insensitive substring of the checked path.
    pub search: Option<&'a str>,
    pub result: Option<HealthResult>,
}

/// `FROM … WHERE …` of [`Db::history_page`], then `$extra`; binds `$1`-`$3`.
macro_rules! history_filter {
    ($extra:literal) => {
        concat!(
            "FROM history h LEFT JOIN items i ON i.id = h.download_dir \
             WHERE ($1 IS NULL OR h.category = $1) \
             AND ($2 IS NULL OR LOWER(h.job_name) LIKE $2 ESCAPE '\\' \
                  OR LOWER(h.file_name) LIKE $2 ESCAPE '\\') \
             AND ($3 IS NULL \
                  OR (CASE WHEN h.status = 1 AND i.id IS NOT NULL THEN 1 ELSE 2 END) = $3)",
            $extra
        )
    };
}

macro_rules! select_queue {
    ($filter:literal) => {
        concat!(
            "SELECT id, created_at, file_name, job_name, category, priority, post_processing, \
             nzb_blob, nzb_size, total_bytes, paused, retry_at, attempts, stuck_count, \
             last_error, content_hash FROM queue ",
            $filter
        )
    };
}

/// A file written outside the tree for an Arr to import (a STRM or a copied subtitle).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Handoff {
    pub path: String,
    /// The item it points at or copies.
    pub item: ItemId,
    /// The job it was handed off with; absent for files carried over from C#.
    pub job: Option<JobId>,
    /// Set for copies, which hold the item's bytes.
    pub size: Option<u64>,
}

#[derive(sqlx::FromRow)]
struct HandoffRow {
    path: String,
    item_id: Uuid,
    job_id: Option<Uuid>,
    size: Option<i64>,
}

impl From<HandoffRow> for Handoff {
    fn from(r: HandoffRow) -> Self {
        Self {
            path: r.path,
            item: ItemId(r.item_id),
            job: r.job_id.map(JobId),
            size: r.size.map(i64::cast_unsigned),
        }
    }
}

/// A library pointer being converted to another name, as journalled.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Conversion {
    pub source: String,
    pub target: String,
    pub item: ItemId,
    /// Step reached, as the converter numbers them.
    pub state: i16,
    pub from_link: bool,
    pub to_link: bool,
    pub original: String,
    pub written: String,
    /// The converter's JSON.
    pub owners: String,
    pub reason: Option<String>,
}

#[derive(sqlx::FromRow)]
struct ConversionRow {
    source: String,
    target: String,
    item_id: Uuid,
    state: i16,
    from_link: bool,
    to_link: bool,
    original: String,
    written: String,
    owners: String,
    reason: Option<String>,
}

impl From<ConversionRow> for Conversion {
    fn from(r: ConversionRow) -> Self {
        Self {
            source: r.source,
            target: r.target,
            item: ItemId(r.item_id),
            state: r.state,
            from_link: r.from_link,
            to_link: r.to_link,
            original: r.original,
            written: r.written,
            owners: r.owners,
            reason: r.reason,
        }
    }
}

/// Records handoff row `$f` on executor `$tx`; `$conflict` follows `ON CONFLICT (path)`.
macro_rules! insert_handoff {
    ($tx:expr, $f:expr, $conflict:literal) => {
        sqlx::query(concat!(
            "INSERT INTO handoff_files (path, item_id, job_id, size) VALUES ($1, $2, $3, $4) \
             ON CONFLICT (path) ",
            $conflict
        ))
        .bind(&$f.path)
        .bind($f.item.0)
        .bind($f.job.map(|j| j.0))
        .bind($f.size.map(u64::cast_signed))
        .execute(&mut *$tx)
        .await?;
    };
}

/// Inserts `$blobs` then `$items` (parents before children) on executor `$tx`.
macro_rules! insert_rows {
    ($tx:expr, $items:expr, $blobs:expr) => {
        for (id, data) in $blobs {
            sqlx::query("INSERT INTO blobs (id, data) VALUES ($1, $2)")
                .bind(id.0)
                .bind(*data)
                .execute(&mut *$tx)
                .await?;
        }
        for item in $items {
            sqlx::query(
                "INSERT INTO items (id, parent_id, name, path, kind, size, meta_blob, nzb_blob, \
                 created_at, release_date, health_state, encrypted) \
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
            )
            .bind(item.id.0)
            .bind(item.parent.map(|p| p.0))
            .bind(&item.name)
            .bind(&item.path)
            .bind(item.kind as i16)
            .bind(item.size.map(u64::cast_signed))
            .bind(item.meta_blob.map(|b| b.0))
            .bind(item.nzb_blob.map(|b| b.0))
            .bind(item.created_at)
            .bind(item.release_date)
            .bind(item.health_state as i16)
            .bind(i16::from(item.encrypted))
            .execute(&mut *$tx)
            .await?;
        }
    };
}

/// Inserts the history row `$h` with its output `$items` and `$blobs` on executor `$tx`,
/// stamping the job onto its download directory, and marks its Arrs for a refresh.
macro_rules! insert_history {
    ($tx:expr, $h:expr, $items:expr, $blobs:expr) => {
        insert_rows!($tx, $items, $blobs);
        insert_history_row!($tx, $h);
        sqlx::query(
            "INSERT INTO arr_refresh (category, generation) VALUES ($1, 1) \
             ON CONFLICT (category) DO UPDATE SET generation = arr_refresh.generation + 1",
        )
        .bind($h.category.to_lowercase())
        .execute(&mut *$tx)
        .await?;
    };
}

/// Inserts the history row `$h` on executor `$tx`, stamping the job onto its download
/// directory.
macro_rules! insert_history_row {
    ($tx:expr, $h:expr) => {
        sqlx::query(
            "INSERT INTO history (id, created_at, file_name, job_name, category, status, \
             total_bytes, download_seconds, fail_message, download_dir, nzb_blob, storage) \
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
        )
        .bind($h.id.0)
        .bind($h.created_at)
        .bind(&$h.file_name)
        .bind(&$h.job_name)
        .bind(&$h.category)
        .bind($h.status as i16)
        .bind($h.total_bytes.cast_signed())
        .bind($h.download_seconds)
        .bind(&$h.fail_message)
        .bind($h.download_dir.map(|d| d.0))
        .bind($h.nzb_blob.map(|b| b.0))
        .bind(&$h.storage)
        .execute(&mut *$tx)
        .await?;
        if let Some(dir) = $h.download_dir {
            sqlx::query("UPDATE items SET job_id = $2, job_name = $3 WHERE id = $1")
                .bind(dir.0)
                .bind($h.id.0)
                .bind(&$h.job_name)
                .execute(&mut *$tx)
                .await?;
        }
    };
}

macro_rules! item_columns {
    () => {
        "id, parent_id, name, path, kind, size, meta_blob, nzb_blob, created_at, release_date, \
         health_state, encrypted"
    };
}

macro_rules! select_items {
    ($filter:literal) => {
        concat!("SELECT ", item_columns!(), " FROM items ", $filter)
    };
}

macro_rules! select_health {
    ($filter:literal) => {
        concat!(
            "SELECT ",
            item_columns!(),
            ", last_health_check, next_health_check, repair_attempts, hidden_until, \
             recheck_requested, suspect_since FROM items ",
            $filter
        )
    };
}

/// The newest health check of item `$id`.
macro_rules! latest_check {
    ($id:literal) => {
        concat!(
            "(SELECT x.id FROM health_checks x WHERE x.item_id = ",
            $id,
            " ORDER BY x.created_at DESC, x.id DESC LIMIT 1)"
        )
    };
}

/// `FROM … WHERE …` of [`Db::files`]; `bind_file_filter!` binds `$1`-`$13`.
// `kind = 1` is `ItemKind::Directory`. The latest check is only looked up when `$11` or
// `$12` filters on it, and then from the matching checks: most files are healthy, so the
// filter is usually far more selective than the rest.
macro_rules! file_filter {
    () => {
        concat!(
            "FROM items i WHERE i.path LIKE '/content/%' \
             AND ($1 IS NULL OR i.parent_id = $1) \
             AND (NOT $2 OR i.kind <> 1) \
             AND ($3 IS NULL OR i.kind = $3) \
             AND ($4 IS NULL OR LOWER(i.path) LIKE $4 ESCAPE '\\') \
             AND ($5 IS NULL OR i.path LIKE $5 ESCAPE '\\') \
             AND ($6 IS NULL OR i.size >= $6) AND ($7 IS NULL OR i.size <= $7) \
             AND ($8 IS NULL OR i.created_at >= $8) AND ($9 IS NULL OR i.created_at < $9) \
             AND (($10 IS NULL AND i.health_state <> 5) OR i.health_state = $10) \
             AND ($13 IS NULL OR i.encrypted = $13) \
             AND (($11 IS NULL AND $12 IS NULL) OR i.id IN (SELECT h.item_id \
             FROM health_checks h WHERE ($11 IS NULL OR h.result = $11) \
             AND ($12 IS NULL OR h.repair = $12) AND h.id = ",
            latest_check!("h.item_id"),
            "))"
        )
    };
}

macro_rules! bind_file_filter {
    ($query:expr, $q:expr, $search:expr, $category:expr) => {
        $query
            .bind($q.parent.map(|p| p.0))
            .bind($q.files_only)
            .bind($q.kind.map(|k| k as i16))
            .bind($search.as_deref())
            .bind($category.as_deref())
            .bind($q.min_size.map(u64::cast_signed))
            .bind($q.max_size.map(u64::cast_signed))
            .bind($q.added_after)
            .bind($q.added_before)
            .bind($q.state.map(|s| s as i16))
            .bind($q.result.map(|r| r as i16))
            .bind($q.repair.map(|r| r as i16))
            .bind($q.encrypted.map(i16::from))
    };
}

/// Blob ids in the subtree rooted at `$1`.
const SUBTREE_BLOBS: &str = "WITH RECURSIVE sub (id) AS ( \
        SELECT id FROM items WHERE id = $1 \
        UNION ALL SELECT i.id FROM items i JOIN sub ON i.parent_id = sub.id) \
    SELECT meta_blob, nzb_blob FROM items WHERE id IN (SELECT id FROM sub)";

/// `(parent, nzb)` of folder `$1` when nothing but `$2` is left in it.
const EMPTY_DIR: &str = "SELECT parent_id, nzb_blob FROM items d WHERE id = $1 AND kind = 1 \
    AND NOT EXISTS (SELECT 1 FROM items c WHERE c.parent_id = d.id AND c.id <> $2)";

/// Ids of every item in a download that history still lists.
macro_rules! in_history {
    () => {
        "(WITH RECURSIVE kept (id) AS ( \
            SELECT download_dir FROM history WHERE download_dir IS NOT NULL \
            UNION ALL SELECT i.id FROM items i JOIN kept ON i.parent_id = kept.id) \
         SELECT id FROM kept)"
    };
}

/// `1` when file `$1` is under `/content`, created before `$2`, in no download history
/// lists.
const STILL_OUTSIDE_HISTORY: &str = "WITH RECURSIVE up (id, parent_id) AS ( \
        SELECT id, parent_id FROM items WHERE id = $1 \
        UNION ALL SELECT p.id, p.parent_id FROM items p JOIN up ON p.id = up.parent_id) \
    SELECT 1 FROM items WHERE id = $1 AND kind <> 1 AND path LIKE '/content/%' \
    AND created_at < $2 \
    AND NOT EXISTS (SELECT 1 FROM history h JOIN up ON h.download_dir = up.id)";

/// `1` when a file is anywhere below item `$1`.
const HOLDS_FILES: &str = "WITH RECURSIVE sub (id) AS ( \
        SELECT id FROM items WHERE parent_id = $1 \
        UNION ALL SELECT i.id FROM items i JOIN sub ON i.parent_id = sub.id) \
    SELECT 1 FROM items WHERE id IN (SELECT id FROM sub) AND kind <> 1 LIMIT 1";

/// Empty folders below `/content/<category>` created before `$1` in no download history
/// lists.
const EMPTY_DIRS_OUTSIDE_HISTORY: &str = concat!(
    "SELECT id FROM items d WHERE kind = 1 AND path LIKE '/content/%/%' AND created_at < $1 \
     AND NOT EXISTS (SELECT 1 FROM items c WHERE c.parent_id = d.id) AND id NOT IN ",
    in_history!(),
    " LIMIT 500"
);

/// Nothing references the blob `$id`.
macro_rules! unreferenced {
    ($id:literal) => {
        concat!(
            "NOT EXISTS (SELECT 1 FROM items WHERE meta_blob = ",
            $id,
            " OR nzb_blob = ",
            $id,
            ") \
             AND NOT EXISTS (SELECT 1 FROM queue WHERE nzb_blob = ",
            $id,
            ") \
             AND NOT EXISTS (SELECT 1 FROM history WHERE nzb_blob = ",
            $id,
            ") AND NOT EXISTS (SELECT 1 FROM repair_jobs WHERE nzb_blob = ",
            $id,
            " AND state IN (0, 1, 4))"
        )
    };
}

const DELETE_UNUSED_BLOB: &str =
    concat!("DELETE FROM blobs WHERE id = $1 AND ", unreferenced!("$1"));

/// Starts the retention clock of an NZB blob that just lost its last reference.
const RELEASE_NZB: &str = concat!(
    "UPDATE blobs SET unreferenced_at = $2 WHERE id = $1 AND unreferenced_at IS NULL AND ",
    unreferenced!("$1")
);

/// Up to `$2` NZB blobs unreferenced since `$1` or earlier, rechecked as they go.
const DELETE_EXPIRED_NZBS: &str = concat!(
    "DELETE FROM blobs WHERE id IN (SELECT id FROM blobs WHERE unreferenced_at <= $1 \
     ORDER BY unreferenced_at LIMIT $2) AND unreferenced_at <= $1 AND ",
    unreferenced!("blobs.id")
);

const DAY_MS: i64 = 24 * 60 * 60 * 1000;

/// Releases an NZB blob after a reference to it went in `$tx`: deleted at once with no
/// retention, otherwise kept until the daily cleanup finds it expired.
macro_rules! release_nzb {
    ($db:expr, $tx:ident, $blob:expr) => {
        if $db.nzb_retention_ms.load(Ordering::Relaxed) <= 0 {
            sqlx::query(DELETE_UNUSED_BLOB)
                .bind($blob)
                .execute(&mut *$tx)
                .await?;
        } else {
            sqlx::query(RELEASE_NZB)
                .bind($blob)
                .bind(now_ms())
                .execute(&mut *$tx)
                .await?;
        }
    };
}
pub(crate) use release_nzb;

/// Deletes item `$id` (a `Uuid`), its subtree, the job folders this leaves empty, and the
/// blobs nothing references anymore, in `$tx`.
macro_rules! delete_subtree {
    ($db:expr, $tx:ident, $id:expr) => {{
        let id: Uuid = $id;
        let refs: Vec<(Option<Uuid>, Option<Uuid>)> = sqlx::query_as(SUBTREE_BLOBS)
            .bind(id)
            .fetch_all(&mut *$tx)
            .await?;
        let mut parent: Option<Uuid> =
            sqlx::query_scalar("SELECT parent_id FROM items WHERE id = $1")
                .bind(id)
                .fetch_optional(&mut *$tx)
                .await?
                .flatten();
        sqlx::query("DELETE FROM items WHERE id = $1")
            .bind(id)
            .execute(&mut *$tx)
            .await?;
        let (mut meta, mut nzbs): (Vec<Uuid>, Vec<Uuid>) = (Vec::new(), Vec::new());
        for (m, n) in refs {
            meta.extend(m);
            nzbs.extend(n);
        }
        // Climb empty folders to the job folder (the one carrying the NZB) and delete it
        // with them; an empty chain that ends anywhere else (a category) stays.
        let mut below = id;
        while let Some(dir) = parent {
            let empty: Option<(Option<Uuid>, Option<Uuid>)> = sqlx::query_as(EMPTY_DIR)
                .bind(dir)
                .bind(below)
                .fetch_optional(&mut *$tx)
                .await?;
            let Some((up, nzb)) = empty else { break };
            if let Some(nzb) = nzb {
                sqlx::query("DELETE FROM items WHERE id = $1")
                    .bind(dir)
                    .execute(&mut *$tx)
                    .await?;
                nzbs.push(nzb);
                break;
            }
            (below, parent) = (dir, up);
        }
        for list in [&mut meta, &mut nzbs] {
            list.sort_unstable();
            list.dedup();
        }
        for blob in meta {
            sqlx::query(DELETE_UNUSED_BLOB)
                .bind(blob)
                .execute(&mut *$tx)
                .await?;
        }
        for blob in nzbs {
            release_nzb!($db, $tx, blob);
        }
    }};
}

impl Db {
    /// Opens `sqlite:<path>` (created if missing) or `postgres://…` and applies migrations.
    pub async fn connect(url: &str) -> Result<Self, Error> {
        let pool = if url.starts_with("sqlite:") {
            let options = SqliteConnectOptions::from_str(url)?
                .create_if_missing(true)
                .journal_mode(SqliteJournalMode::Wal)
                .synchronous(SqliteSynchronous::Normal)
                .busy_timeout(BUSY_TIMEOUT)
                .foreign_keys(true);
            // No server can drop an idle in-process connection, so skip the checkout
            // ping: it nearly doubled single-lookup latency in measurements.
            Pool::Sqlite(
                SqlitePoolOptions::new()
                    .test_before_acquire(false)
                    .connect_with(options)
                    .await?,
            )
        } else if url.starts_with("postgres:") || url.starts_with("postgresql:") {
            Pool::Postgres(PgPoolOptions::new().connect(url).await?)
        } else {
            return Err(Error::UnsupportedUrl);
        };
        migrated(match &pool {
            Pool::Sqlite(pool) => SQLITE_MIGRATOR.run(pool).await,
            Pool::Postgres(pool) => sqlx::migrate!("./migrations/postgres").run(pool).await,
        })?;
        let repair_query = "SELECT EXISTS (SELECT 1 FROM repair_patches)";
        let repair_present: bool = match &pool {
            Pool::Sqlite(pool) => sqlx::query_scalar(repair_query).fetch_one(pool).await?,
            Pool::Postgres(pool) => sqlx::query_scalar(repair_query).fetch_one(pool).await?,
        };
        Ok(Self {
            pool,
            changes: broadcast::channel(CHANGES).0,
            searches: Arc::default(),
            nzb_retention_ms: Arc::new(AtomicI64::new(30 * DAY_MS)),
            writes: Arc::default(),
            blob_epoch: Arc::default(),
            repair_present: Arc::new(AtomicBool::new(repair_present)),
        })
    }

    /// Held by background work around one unit of writes, so [`Db::pause_writes`] can
    /// wait for it to finish. Never acquire one while holding a permit or a pause: the
    /// lock is write-preferring, so a nested permit deadlocks behind a waiting pause.
    pub async fn write_permit(&self) -> OwnedRwLockReadGuard<()> {
        Arc::clone(&self.writes).read_owned().await
    }

    /// Clones share publication/maintenance admission; independent owners do not.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.writes, &other.writes)
    }

    /// Waits for every [`Db::write_permit`] to drop and holds new ones back until the
    /// guard drops.
    pub async fn pause_writes(&self) -> OwnedRwLockWriteGuard<()> {
        Arc::clone(&self.writes).write_owned().await
    }

    /// Refreshes stale query planner statistics; PostgreSQL's autovacuum does this itself.
    pub async fn optimize(&self) -> Result<(), Error> {
        match &self.pool {
            Pool::Sqlite(pool) => sqlite_optimize(pool).await,
            Pool::Postgres(_) => Ok(()),
        }
    }

    /// The SQLite file's size and unused space; `None` on PostgreSQL.
    pub async fn sqlite_stats(&self) -> Result<Option<SqliteStats>, Error> {
        match &self.pool {
            Pool::Sqlite(pool) => sqlite_stats(pool).await.map(Some),
            Pool::Postgres(_) => Ok(None),
        }
    }

    /// Rewrites the SQLite file without its unused pages (`VACUUM`); writes wait until it
    /// ends. Does nothing on PostgreSQL.
    pub async fn compact(&self) -> Result<(), Error> {
        match &self.pool {
            Pool::Sqlite(pool) => sqlite_compact(pool).await,
            Pool::Postgres(_) => Ok(()),
        }
    }

    /// How long an NZB nothing references anymore is kept (default 30 days). `0` deletes
    /// it with its last reference.
    pub fn set_nzb_retention_days(&self, days: u32) {
        self.nzb_retention_ms
            .store(i64::from(days) * DAY_MS, Ordering::Relaxed);
    }

    /// Deletes NZBs unreferenced longer than the retention window, in bounded batches.
    /// Returns how many went.
    pub async fn delete_expired_nzbs(&self) -> Result<u64, Error> {
        const BATCH: u16 = 500;
        let cutoff = now_ms() - self.nzb_retention_ms.load(Ordering::Relaxed);
        let mut deleted = 0;
        loop {
            let n = on!(self, pool => sqlx::query(DELETE_EXPIRED_NZBS)
                .bind(cutoff)
                .bind(i64::from(BATCH))
                .execute(pool)
                .await?
                .rows_affected());
            deleted += n;
            if n < u64::from(BATCH) {
                return Ok(deleted);
            }
        }
    }

    /// Forgets finished jobs older than `before` (Unix milliseconds) like
    /// [`Db::delete_history`], in bounded batches; their downloads stay. Returns how many.
    pub async fn prune_history(&self, before: i64) -> Result<u64, Error> {
        const BATCH: u16 = 500;
        let mut pruned = 0;
        loop {
            let ids: Vec<Uuid> = on!(self, pool => sqlx::query_scalar(
                "SELECT id FROM history WHERE created_at < $1 ORDER BY created_at LIMIT $2",
            )
            .bind(before)
            .bind(i64::from(BATCH))
            .fetch_all(pool)
            .await?);
            let ids: Vec<JobId> = ids.into_iter().map(JobId).collect();
            pruned += self.delete_history(&ids).await?;
            if ids.len() < usize::from(BATCH) {
                return Ok(pruned);
            }
        }
    }

    /// Stored NZBs with this content hash, to reuse for an identical submission.
    pub async fn nzbs_with_content(&self, hash: &str) -> Result<Vec<BlobId>, Error> {
        let ids: Vec<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT id FROM blobs WHERE content_hash = $1",
        )
        .bind(hash)
        .fetch_all(pool)
        .await?);
        Ok(ids.into_iter().map(BlobId).collect())
    }

    /// Changes from now on. A lagging receiver gets `Lagged` and should assume everything
    /// changed.
    #[must_use]
    pub fn subscribe(&self) -> broadcast::Receiver<Change> {
        self.changes.subscribe()
    }

    /// Announces a change that is not a write here, e.g. a job starting to import.
    pub fn notify(&self, change: Change) {
        // No receivers is fine.
        let _ = self.changes.send(change);
    }

    pub async fn close(&self) {
        on!(self, pool => pool.close().await);
    }

    /// Starts a transaction that reads, then writes. A deferred SQLite one fails with
    /// `SQLITE_BUSY_SNAPSHOT` (517) when another writer commits between the two.
    const fn begin_write(&self) -> &'static str {
        match self.pool {
            Pool::Sqlite(_) => "BEGIN IMMEDIATE",
            Pool::Postgres(_) => "BEGIN",
        }
    }

    pub async fn item(&self, id: ItemId) -> Result<Option<Item>, Error> {
        let sql = select_items!("WHERE id = $1");
        let row: Option<ItemRow> =
            on!(self, pool => sqlx::query_as(sql).bind(id.0).fetch_optional(pool).await?);
        row.map(Item::try_from).transpose()
    }

    pub async fn item_by_path(&self, path: &str) -> Result<Option<Item>, Error> {
        let sql = select_items!("WHERE path = $1");
        let row: Option<ItemRow> =
            on!(self, pool => sqlx::query_as(sql).bind(path).fetch_optional(pool).await?);
        row.map(Item::try_from).transpose()
    }

    /// Children of `parent`, ordered by name.
    pub async fn children(&self, parent: ItemId) -> Result<Vec<Item>, Error> {
        let sql = select_items!("WHERE parent_id = $1 ORDER BY name");
        let rows: Vec<ItemRow> =
            on!(self, pool => sqlx::query_as(sql).bind(parent.0).fetch_all(pool).await?);
        rows.into_iter().map(Item::try_from).collect()
    }

    /// Files (not directories) with ids in `start..end`, ordered by id.
    pub async fn files_in_id_range(
        &self,
        start: ItemId,
        end: Option<ItemId>,
    ) -> Result<Vec<Item>, Error> {
        let sql =
            select_items!("WHERE id >= $1 AND ($2 IS NULL OR id < $2) AND kind <> $3 ORDER BY id");
        let rows: Vec<ItemRow> = on!(self, pool => sqlx::query_as(sql)
            .bind(start.0)
            .bind(end.map(|e| e.0))
            .bind(ItemKind::Directory as i16)
            .fetch_all(pool)
            .await?);
        rows.into_iter().map(Item::try_from).collect()
    }

    /// Children of `parent` that are the directory of a completed download, by name.
    pub async fn completed_dirs(&self, parent: ItemId) -> Result<Vec<Item>, Error> {
        let sql = select_items!(
            "WHERE parent_id = $1 AND id IN (SELECT download_dir FROM history WHERE status = $2) ORDER BY name"
        );
        let rows: Vec<ItemRow> = on!(self, pool => sqlx::query_as(sql)
            .bind(parent.0)
            .bind(JobStatus::Completed as i16)
            .fetch_all(pool)
            .await?);
        rows.into_iter().map(Item::try_from).collect()
    }

    /// Whether `id` is the directory of a completed download.
    pub async fn is_completed_dir(&self, id: ItemId) -> Result<bool, Error> {
        let found: Option<i32> = on!(self, pool => sqlx::query_scalar(
            "SELECT 1 FROM history WHERE download_dir = $1 AND status = $2 LIMIT 1",
        )
        .bind(id.0)
        .bind(JobStatus::Completed as i16)
        .fetch_optional(pool)
        .await?);
        Ok(found.is_some())
    }

    /// Inserts `blobs` then `items` (parents before children) in one transaction.
    pub async fn insert(&self, items: &[Item], blobs: &[(BlobId, &[u8])]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            insert_rows!(tx, items, blobs);
            tx.commit().await?;
        });
        Ok(())
    }

    /// Reads the root folder's row: the database answers and holds our schema.
    pub async fn ping(&self) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("SELECT 1 FROM items WHERE id = $1")
                .bind(ItemId::ROOT.0)
                .fetch_optional(pool)
                .await?;
        });
        Ok(())
    }

    /// Whether nothing was written yet besides the seeded folders: no items, jobs,
    /// settings or admin.
    pub async fn is_fresh(&self) -> Result<bool, Error> {
        let used: Option<i32> = on!(self, pool => sqlx::query_scalar(
            "SELECT 1 WHERE EXISTS (SELECT 1 FROM items WHERE id NOT IN ($1, $2)) \
             OR EXISTS (SELECT 1 FROM queue) OR EXISTS (SELECT 1 FROM history) \
             OR EXISTS (SELECT 1 FROM config) OR EXISTS (SELECT 1 FROM accounts)",
        )
        .bind(ItemId::ROOT.0)
        .bind(ItemId::CONTENT.0)
        .fetch_optional(pool)
        .await?);
        Ok(used.is_none())
    }

    pub async fn item_ids(&self) -> Result<Vec<ItemId>, Error> {
        let ids: Vec<Uuid> =
            on!(self, pool => sqlx::query_scalar("SELECT id FROM items").fetch_all(pool).await?);
        Ok(ids.into_iter().map(ItemId).collect())
    }

    pub async fn blob_ids(&self) -> Result<Vec<BlobId>, Error> {
        let ids: Vec<Uuid> =
            on!(self, pool => sqlx::query_scalar("SELECT id FROM blobs").fetch_all(pool).await?);
        Ok(ids.into_iter().map(BlobId).collect())
    }

    pub async fn missing_nzb_links(&self) -> Result<(Vec<ItemId>, Vec<JobId>), Error> {
        let items: Vec<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT id FROM items WHERE nzb_blob IS NULL"
        ).fetch_all(pool).await?);
        let history: Vec<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT id FROM history WHERE nzb_blob IS NULL"
        ).fetch_all(pool).await?);
        Ok((
            items.into_iter().map(ItemId).collect(),
            history.into_iter().map(JobId).collect(),
        ))
    }

    /// Restores missing legacy NZB links without replacing current links or recreating rows.
    pub async fn restore_nzb_links(
        &self,
        items: &[(ItemId, BlobId)],
        history: &[(JobId, BlobId)],
    ) -> Result<u64, Error> {
        let mut restored = 0;
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for &(id, blob) in items {
                restored += sqlx::query(
                    "UPDATE items SET nzb_blob = $2 WHERE id = $1 AND nzb_blob IS NULL \
                     AND EXISTS (SELECT 1 FROM blobs WHERE id = $2)",
                ).bind(id.0).bind(blob.0).execute(&mut *tx).await?.rows_affected();
            }
            for &(id, blob) in history {
                restored += sqlx::query(
                    "UPDATE history SET nzb_blob = $2 WHERE id = $1 AND nzb_blob IS NULL \
                     AND EXISTS (SELECT 1 FROM blobs WHERE id = $2)",
                ).bind(id.0).bind(blob.0).execute(&mut *tx).await?.rows_affected();
            }
            tx.commit().await?;
        });
        if restored != 0 {
            self.notify(Change::History);
        }
        Ok(restored)
    }

    /// Writes `blobs`, then items carried over from another install with their health
    /// bookkeeping (parents before children), in one transaction.
    pub async fn import_items(
        &self,
        items: &[(Item, HealthStatus)],
        blobs: &[(BlobId, &[u8])],
    ) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            insert_rows!(tx, &[] as &[Item], blobs);
            for (item, h) in items {
                sqlx::query(
                    "INSERT INTO items (id, parent_id, name, path, kind, size, meta_blob, \
                     nzb_blob, created_at, release_date, health_state, last_health_check, \
                     next_health_check, encrypted) \
                     VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)",
                )
                .bind(item.id.0)
                .bind(item.parent.map(|p| p.0))
                .bind(&item.name)
                .bind(&item.path)
                .bind(item.kind as i16)
                .bind(item.size.map(u64::cast_signed))
                .bind(item.meta_blob.map(|b| b.0))
                .bind(item.nzb_blob.map(|b| b.0))
                .bind(item.created_at)
                .bind(item.release_date)
                .bind(h.state as i16)
                .bind(h.last_check)
                .bind(h.next_check)
                .bind(i16::from(item.encrypted))
                .execute(&mut *tx)
                .await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }

    /// Records finished jobs carried over from another install, in one transaction.
    /// Unlike [`Db::add_history`], no Arr hears about them: they finished long ago.
    pub async fn import_history(&self, entries: &[HistoryEntry]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for h in entries {
                insert_history_row!(tx, h);
            }
            tx.commit().await?;
        });
        self.notify(Change::History);
        Ok(())
    }

    /// Stamps download directories carried over without a job, as `(path, job)`, with
    /// that job and their own name, in one transaction. Directories that already have a
    /// job, and jobs another directory has, are left alone. Returns how many changed.
    pub async fn stamp_jobs(&self, dirs: &[(String, JobId)]) -> Result<u64, Error> {
        let sql = "UPDATE items SET job_id = $2, job_name = name WHERE path = $1 \
                   AND job_id IS NULL AND NOT EXISTS (SELECT 1 FROM items WHERE job_id = $2)";
        let mut stamped = 0;
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for (path, job) in dirs {
                let done = sqlx::query(sql).bind(path).bind(job.0).execute(&mut *tx).await?;
                stamped += done.rows_affected();
            }
            tx.commit().await?;
        });
        Ok(stamped)
    }

    /// Records files written outside the tree for an Arr to import; an existing path is
    /// left alone.
    pub async fn record_handoffs(&self, files: &[Handoff]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for f in files {
                insert_handoff!(tx, f, "DO NOTHING");
            }
            tx.commit().await?;
        });
        Ok(())
    }

    /// The item a handoff file at `path` points at.
    pub async fn handoff_item(&self, path: &str) -> Result<Option<ItemId>, Error> {
        let id: Option<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT item_id FROM handoff_files WHERE path = $1",
        )
        .bind(path)
        .fetch_optional(pool)
        .await?);
        Ok(id.map(ItemId))
    }

    /// Handoff files whose item, or whose job's history row, is gone.
    pub async fn stale_handoffs(&self) -> Result<Vec<Handoff>, Error> {
        let rows: Vec<HandoffRow> = on!(self, pool => sqlx::query_as(
            "SELECT path, item_id, job_id, size FROM handoff_files f \
             WHERE NOT EXISTS (SELECT 1 FROM items WHERE id = f.item_id) \
             OR (job_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM history WHERE id = f.job_id))",
        )
        .fetch_all(pool)
        .await?);
        Ok(rows.into_iter().map(Handoff::from).collect())
    }

    pub async fn forget_handoffs(&self, paths: &[String]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for path in paths {
                sqlx::query("DELETE FROM handoff_files WHERE path = $1")
                    .bind(path)
                    .execute(&mut *tx)
                    .await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }

    /// Queues the job with its NZB blob. A stored blob with the job's `nzb_blob` id is
    /// reused (and kept from expiring), so pass an existing id only for identical content.
    pub async fn enqueue(&self, job: &QueueEntry, nzb: &[u8]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            let reused = sqlx::query("UPDATE blobs SET unreferenced_at = NULL WHERE id = $1")
                .bind(job.nzb_blob.0)
                .execute(&mut *tx)
                .await?
                .rows_affected();
            if reused == 0 {
                sqlx::query("INSERT INTO blobs (id, data, content_hash) VALUES ($1, $2, $3)")
                    .bind(job.nzb_blob.0)
                    .bind(nzb)
                    .bind(&job.content_hash)
                    .execute(&mut *tx)
                    .await?;
            }
            sqlx::query(
                "INSERT INTO queue (id, created_at, sort_order, file_name, job_name, category, \
                 priority, post_processing, nzb_blob, nzb_size, total_bytes, paused, retry_at, \
                 attempts, stuck_count, last_error, content_hash) \
                 VALUES ($1, $2, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)",
            )
            .bind(job.id.0)
            .bind(job.created_at)
            .bind(&job.file_name)
            .bind(&job.job_name)
            .bind(&job.category)
            .bind(job.priority)
            .bind(job.post_processing)
            .bind(job.nzb_blob.0)
            .bind(job.nzb_size.cast_signed())
            .bind(job.total_bytes.cast_signed())
            .bind(job.paused)
            .bind(job.retry_at)
            .bind(i32::try_from(job.attempts).unwrap_or(i32::MAX))
            .bind(i32::try_from(job.stuck_count).unwrap_or(i32::MAX))
            .bind(&job.last_error)
            .bind(&job.content_hash)
            .execute(&mut *tx)
            .await?;
            tx.commit().await?;
        });
        self.notify(Change::Queue);
        Ok(())
    }

    /// The oldest queued job with this content hash.
    pub async fn job_with_content(&self, hash: &str) -> Result<Option<JobId>, Error> {
        let id: Option<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT id FROM queue WHERE content_hash = $1 ORDER BY sort_order, id LIMIT 1",
        )
        .bind(hash)
        .fetch_optional(pool)
        .await?);
        Ok(id.map(JobId))
    }

    /// Queued jobs without a content hash, with their NZB blob.
    pub async fn unhashed_jobs(&self) -> Result<Vec<(JobId, BlobId)>, Error> {
        let rows: Vec<(Uuid, Uuid)> = on!(self, pool => sqlx::query_as(
            "SELECT id, nzb_blob FROM queue WHERE content_hash IS NULL",
        )
        .fetch_all(pool)
        .await?);
        Ok(rows
            .into_iter()
            .map(|(id, blob)| (JobId(id), BlobId(blob)))
            .collect())
    }

    pub async fn set_content_hash(&self, id: JobId, hash: &str) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("UPDATE queue SET content_hash = $2 WHERE id = $1")
                .bind(id.0)
                .bind(hash)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Up to `limit` jobs neither paused nor waiting to retry at `now` (Unix milliseconds),
    /// in processing order.
    pub async fn next_jobs(&self, now: i64, limit: usize) -> Result<Vec<QueueEntry>, Error> {
        let sql = select_queue!(
            "WHERE NOT paused AND (retry_at IS NULL OR retry_at <= $1) \
             ORDER BY priority DESC, sort_order, id LIMIT $2"
        );
        let limit = i64::try_from(limit).unwrap_or(i64::MAX);
        let rows: Vec<QueueRow> = on!(self, pool => sqlx::query_as(sql)
            .bind(now)
            .bind(limit)
            .fetch_all(pool)
            .await?);
        Ok(rows.into_iter().map(QueueEntry::from).collect())
    }

    /// Queued jobs in processing order.
    pub async fn queue(&self) -> Result<Vec<QueueEntry>, Error> {
        let sql = select_queue!("ORDER BY priority DESC, sort_order");
        let rows: Vec<QueueRow> = on!(self, pool => sqlx::query_as(sql).fetch_all(pool).await?);
        Ok(rows.into_iter().map(QueueEntry::from).collect())
    }

    /// Drops a queued job and its NZB; `false` when it was not queued.
    pub async fn remove_job(&self, id: JobId) -> Result<bool, Error> {
        let removed = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let blob: Option<Uuid> = sqlx::query_scalar("DELETE FROM queue WHERE id = $1 RETURNING nzb_blob")
                .bind(id.0)
                .fetch_optional(&mut *tx)
                .await?;
            if let Some(blob) = blob {
                release_nzb!(self, tx, blob);
            }
            tx.commit().await?;
            blob.is_some()
        });
        if removed {
            self.notify(Change::Queue);
        }
        Ok(removed)
    }

    /// Counts a failed attempt and holds the job until `at` (Unix milliseconds).
    pub async fn retry_job(&self, id: JobId, at: i64, error: &str) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query(
                "UPDATE queue SET retry_at = $2, last_error = $3, \
                 attempts = CASE WHEN attempts < 2147483647 THEN attempts + 1 ELSE attempts END \
                 WHERE id = $1",
            )
            .bind(id.0)
            .bind(at)
            .bind(error)
            .execute(pool)
            .await?;
        });
        self.notify(Change::Queue);
        Ok(())
    }

    /// Counts a stalled attempt and moves the job behind every other job of its priority.
    pub async fn requeue_stuck(&self, id: JobId, error: &str) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query(
                "UPDATE queue SET last_error = $2, retry_at = NULL, \
                 stuck_count = CASE WHEN stuck_count < 2147483647 THEN stuck_count + 1 ELSE stuck_count END, \
                 sort_order = (SELECT MAX(q.sort_order) + 1 FROM queue q) \
                 WHERE id = $1",
            )
            .bind(id.0)
            .bind(error)
            .execute(pool)
            .await?;
        });
        self.notify(Change::Queue);
        Ok(())
    }

    /// Holds or releases queued jobs; returns how many exist.
    pub async fn pause_jobs(&self, ids: &[JobId], paused: bool) -> Result<u64, Error> {
        let changed = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let mut changed = 0;
            for id in ids {
                changed += sqlx::query("UPDATE queue SET paused = $2 WHERE id = $1")
                    .bind(id.0)
                    .bind(paused)
                    .execute(&mut *tx)
                    .await?
                    .rows_affected();
            }
            tx.commit().await?;
            changed
        });
        self.notify(Change::Queue);
        Ok(changed)
    }

    /// Moves a queued job to history, inserting its output, the first health `checks` of
    /// its files and the files it was handed off with in the same transaction. `false`,
    /// writing nothing, when the job is no longer queued (it was removed meanwhile).
    pub async fn finish_job(
        &self,
        history: &HistoryEntry,
        items: &[Item],
        blobs: &[(BlobId, &[u8])],
        checks: &[(ItemId, FirstCheck)],
        handoffs: &[Handoff],
    ) -> Result<bool, Error> {
        let finished = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let queued: Option<Uuid> = sqlx::query_scalar("DELETE FROM queue WHERE id = $1 RETURNING nzb_blob")
                .bind(history.id.0)
                .fetch_optional(&mut *tx)
                .await?;
            if let Some(blob) = queued {
                insert_history!(tx, history, items, blobs);
                for (id, check) in checks {
                    let at = check.status.last_check.unwrap_or(history.created_at);
                    sqlx::query(
                        "UPDATE items SET health_state = $2, last_health_check = $3, \
                         next_health_check = $4 WHERE id = $1",
                    )
                    .bind(id.0)
                    .bind(check.status.state as i16)
                    .bind(at)
                    .bind(check.status.next_check)
                    .execute(&mut *tx)
                    .await?;
                    sqlx::query(
                        "INSERT INTO health_checks (id, created_at, item_id, path, result, \
                         repair, message) SELECT $1, $2, id, path, $3, $4, $5 FROM items \
                         WHERE id = $6",
                    )
                    .bind(Uuid::now_v7())
                    .bind(at)
                    .bind(check.result as i16)
                    .bind(Repair::None as i16)
                    .bind(check.message.as_deref())
                    .bind(id.0)
                    .execute(&mut *tx)
                    .await?;
                    if check.defect.is_some() || check.analyze {
                        let defect = check.defect.as_ref();
                        sqlx::query(
                            "INSERT INTO media_probes (item_id, requested_at, defect_offset, \
                             defect_size, defect_seen_at, defect_source, structural_hold) \
                             VALUES ($1, $2, $3, $4, $5, $6, $7)",
                        )
                        .bind(id.0)
                        .bind(check.analyze.then_some(at))
                        .bind(defect.map(|d| d.offset.cast_signed()))
                        .bind(defect.map(|d| d.size.cast_signed()))
                        .bind(defect.map(|d| d.seen_at))
                        .bind(defect.map(|d| d.source))
                        .bind(i16::from(defect.is_some()))
                        .execute(&mut *tx)
                        .await?;
                    }
                }
                // A path left by an earlier job holds this job's file now.
                for f in handoffs {
                    insert_handoff!(tx, f, "DO UPDATE SET item_id = excluded.item_id, \
                        job_id = excluded.job_id, size = excluded.size");
                }
                // Usually history keeps it; a no-op then.
                release_nzb!(self, tx, blob);
                tx.commit().await?;
            }
            queued.is_some()
        });
        if finished {
            self.notify(Change::Queue);
            self.notify(Change::History);
            if !checks.is_empty() {
                self.notify(Change::Health);
            }
        }
        Ok(finished)
    }

    /// Records a finished job that was never queued here (imported or seeded), with its
    /// output.
    pub async fn add_history(
        &self,
        history: &HistoryEntry,
        items: &[Item],
        blobs: &[(BlobId, &[u8])],
    ) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            insert_history!(tx, history, items, blobs);
            tx.commit().await?;
        });
        self.notify(Change::History);
        Ok(())
    }

    pub async fn history_entry(&self, id: JobId) -> Result<Option<HistoryEntry>, Error> {
        let row: Option<HistoryRow> = on!(self, pool => sqlx::query_as(
            "SELECT id, created_at, file_name, job_name, category, status, total_bytes, \
             download_seconds, fail_message, download_dir, nzb_blob, storage FROM history \
             WHERE id = $1",
        )
        .bind(id.0)
        .fetch_optional(pool)
        .await?);
        row.map(HistoryEntry::try_from).transpose()
    }

    /// Every finished job, newest first, with the path of its download directory while it
    /// exists.
    pub async fn history(&self) -> Result<Vec<(HistoryEntry, Option<String>)>, Error> {
        Ok(self.history_page(&HistoryQuery::default()).await?.0)
    }

    /// One page of finished jobs matching `q`, newest first, with the path of each
    /// download directory while it exists, and how many match in total.
    pub async fn history_page(
        &self,
        q: &HistoryQuery<'_>,
    ) -> Result<(Vec<(HistoryEntry, Option<String>)>, u64), Error> {
        macro_rules! select {
            ($extra:literal) => {
                concat!(
                    "SELECT h.id, h.created_at, h.file_name, h.job_name, h.category, h.status, \
                     h.total_bytes, h.download_seconds, h.fail_message, h.download_dir, \
                     h.nzb_blob, h.storage, i.path AS dir_path ",
                    history_filter!($extra)
                )
            };
        }
        // ponytail: `LOWER` folds ASCII only on SQLite; release names are ASCII.
        let search = q
            .search
            .map(|s| format!("%{}%", like_escape(&s.to_lowercase())));
        let status = q.status.map(|s| s as i16);
        let limit = q.limit.unwrap_or(usize::MAX);
        let (rows, total) = if let Some(ids) = q.ids {
            // ponytail: one lookup per id; clients ask for the few jobs they track.
            let mut rows: Vec<HistoryListRow> = Vec::with_capacity(ids.len());
            on!(self, pool => for id in ids {
                rows.extend(
                    sqlx::query_as(select!(" AND h.id = $4"))
                        .bind(q.category)
                        .bind(search.as_deref())
                        .bind(status)
                        .bind(id.0)
                        .fetch_optional(pool)
                        .await?,
                );
            });
            rows.sort_by(|a, b| {
                (b.entry.created_at, a.entry.id).cmp(&(a.entry.created_at, b.entry.id))
            });
            let total = rows.len() as u64;
            let rows = rows.into_iter().skip(q.offset).take(limit).collect();
            (rows, total)
        } else {
            let page = select!(" ORDER BY h.created_at DESC, h.id LIMIT $4 OFFSET $5");
            let count = concat!("SELECT COUNT(*) ", history_filter!(""));
            let (rows, total): (Vec<HistoryListRow>, i64) = on!(self, pool => {
                let rows = sqlx::query_as(page)
                    .bind(q.category)
                    .bind(search.as_deref())
                    .bind(status)
                    .bind(i64::try_from(limit).unwrap_or(i64::MAX))
                    .bind(i64::try_from(q.offset).unwrap_or(i64::MAX))
                    .fetch_all(pool)
                    .await?;
                let total = sqlx::query_scalar(count)
                    .bind(q.category)
                    .bind(search.as_deref())
                    .bind(status)
                    .fetch_one(pool)
                    .await?;
                (rows, total)
            });
            (rows, total.cast_unsigned())
        };
        let rows = rows
            .into_iter()
            .map(|r| Ok((HistoryEntry::try_from(r.entry)?, r.dir_path)))
            .collect::<Result<_, Error>>()?;
        Ok((rows, total))
    }

    /// Ids of every finished job, or only of those that failed.
    pub async fn history_ids(&self, failed_only: bool) -> Result<Vec<JobId>, Error> {
        let ids: Vec<Uuid> = on!(self, pool => sqlx::query_scalar(
            "SELECT id FROM history WHERE status = 2 OR NOT $1",
        )
        .bind(failed_only)
        .fetch_all(pool)
        .await?);
        Ok(ids.into_iter().map(JobId).collect())
    }

    /// Forgets finished jobs; their downloads stay. Returns how many were removed.
    pub async fn delete_history(&self, ids: &[JobId]) -> Result<u64, Error> {
        let removed = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let mut removed = 0;
            for id in ids {
                let blob: Option<Option<Uuid>> =
                    sqlx::query_scalar("DELETE FROM history WHERE id = $1 RETURNING nzb_blob")
                        .bind(id.0)
                        .fetch_optional(&mut *tx)
                        .await?;
                if let Some(blob) = blob {
                    removed += 1;
                    if let Some(blob) = blob {
                        release_nzb!(self, tx, blob);
                    }
                }
            }
            tx.commit().await?;
            removed
        });
        if removed > 0 {
            self.notify(Change::History);
        }
        Ok(removed)
    }

    /// Deletes `id`, its subtree, the job folders this leaves empty, and the blobs nothing
    /// references anymore.
    pub async fn delete_item(&self, id: ItemId) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            delete_subtree!(self, tx, id.0);
            tx.commit().await?;
        });
        // A job whose download directory is gone reports as failed.
        self.notify(Change::History);
        Ok(())
    }

    /// Files under `/content` created before `before` that no download in history holds,
    /// in path order: what the unlinked-file cleanup may remove.
    pub async fn files_outside_history(&self, before: i64) -> Result<Vec<Item>, Error> {
        const SQL: &str = concat!(
            select_items!(
                "WHERE kind <> 1 AND path LIKE '/content/%' AND created_at < $1 AND id NOT IN "
            ),
            in_history!(),
            " ORDER BY path"
        );
        let rows: Vec<ItemRow> =
            on!(self, pool => sqlx::query_as(SQL).bind(before).fetch_all(pool).await?);
        rows.into_iter().map(Item::try_from).collect()
    }

    /// Deletes file `id` like [`Db::delete_item`] if it still is one of
    /// [`Db::files_outside_history`] for `before`, checked in the same transaction.
    /// Returns whether it went.
    pub async fn delete_unlinked(&self, id: ItemId, before: i64) -> Result<bool, Error> {
        let deleted = on!(self, pool => {
            let still: Option<i32> = sqlx::query_scalar(STILL_OUTSIDE_HISTORY)
                .bind(id.0)
                .bind(before)
                .fetch_optional(pool)
                .await?;
            if still.is_none() {
                return Ok(false);
            }
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let still: Option<i32> = sqlx::query_scalar(STILL_OUTSIDE_HISTORY)
                .bind(id.0)
                .bind(before)
                .fetch_optional(&mut *tx)
                .await?;
            if still.is_some() {
                delete_subtree!(self, tx, id.0);
                tx.commit().await?;
            }
            still.is_some()
        });
        if deleted {
            self.notify(Change::History);
        }
        Ok(deleted)
    }

    /// Deletes folder `id` like [`Db::delete_item`] if no file is below it, checked in the
    /// same transaction. Returns whether it went.
    pub async fn delete_without_files(&self, id: ItemId) -> Result<bool, Error> {
        let deleted = on!(self, pool => {
            let holds: Option<i32> = sqlx::query_scalar(HOLDS_FILES)
                .bind(id.0)
                .fetch_optional(pool)
                .await?;
            if holds.is_some() {
                return Ok(false);
            }
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let holds: Option<i32> = sqlx::query_scalar(HOLDS_FILES)
                .bind(id.0)
                .fetch_optional(&mut *tx)
                .await?;
            if holds.is_none() {
                delete_subtree!(self, tx, id.0);
                tx.commit().await?;
            }
            holds.is_none()
        });
        if deleted {
            self.notify(Change::History);
        }
        Ok(deleted)
    }

    /// Deletes `id` like [`Db::delete_item`] if it exists and no Arr owns it, checked in the
    /// same transaction. Returns whether it went.
    pub async fn delete_unowned(&self, id: ItemId) -> Result<bool, Error> {
        // An owner insert's foreign-key check share-locks the item row; SQLite has one writer.
        // READ COMMITTED lets the check below see an owner committed while the lock waited.
        const ISOLATION: &str = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";
        const LOCK: &str = "SELECT 1 FROM items WHERE id = $1 FOR UPDATE";
        const UNOWNED: &str = "SELECT 1 FROM items WHERE id = $1 \
                               AND NOT EXISTS (SELECT 1 FROM arr_owners WHERE item_id = $1)";
        let postgres = matches!(self.pool, Pool::Postgres(_));
        let deleted = on!(self, pool => {
            let unowned: Option<i32> = sqlx::query_scalar(UNOWNED)
                .bind(id.0)
                .fetch_optional(pool)
                .await?;
            if unowned.is_none() {
                return Ok(false);
            }
            let mut tx = pool.begin_with(self.begin_write()).await?;
            if postgres {
                sqlx::query(ISOLATION).execute(&mut *tx).await?;
                sqlx::query(LOCK).bind(id.0).execute(&mut *tx).await?;
            }
            let unowned: Option<i32> = sqlx::query_scalar(UNOWNED)
                .bind(id.0)
                .fetch_optional(&mut *tx)
                .await?;
            if unowned.is_some() {
                delete_subtree!(self, tx, id.0);
                tx.commit().await?;
            }
            unowned.is_some()
        });
        if deleted {
            self.notify(Change::History);
        }
        Ok(deleted)
    }

    /// Deletes the empty folders below `/content/<category>` created before `before`
    /// that no download in history holds, and the folders that leaves empty up to their
    /// job folder. Returns how many folders it found empty.
    pub async fn delete_empty_dirs(&self, before: i64) -> Result<u64, Error> {
        let mut removed = 0;
        loop {
            let found = on!(self, pool => {
                let any: Vec<Uuid> = sqlx::query_scalar(EMPTY_DIRS_OUTSIDE_HISTORY)
                    .bind(before)
                    .fetch_all(pool)
                    .await?;
                if any.is_empty() {
                    0
                } else {
                    let mut tx = pool.begin_with(self.begin_write()).await?;
                    let dirs: Vec<Uuid> = sqlx::query_scalar(EMPTY_DIRS_OUTSIDE_HISTORY)
                        .bind(before)
                        .fetch_all(&mut *tx)
                        .await?;
                    for &dir in &dirs {
                        delete_subtree!(self, tx, dir);
                    }
                    tx.commit().await?;
                    dirs.len() as u64
                }
            });
            if found == 0 {
                break;
            }
            removed += found;
        }
        if removed > 0 {
            self.notify(Change::History);
        }
        Ok(removed)
    }

    pub async fn blob(&self, id: BlobId) -> Result<Option<Vec<u8>>, Error> {
        Ok(
            on!(self, pool => sqlx::query_scalar("SELECT data FROM blobs WHERE id = $1")
            .bind(id.0)
            .fetch_optional(pool)
            .await?),
        )
    }

    /// Replaces a blob's bytes, e.g. after health checks record missing segments.
    pub async fn update_blob(&self, id: BlobId, data: &[u8]) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("UPDATE blobs SET data = $2, generation = generation + 1 WHERE id = $1")
                .bind(id.0)
                .bind(data)
                .execute(pool)
                .await?;
        });
        self.blob_epoch.fetch_add(1, Ordering::Release);
        Ok(())
    }

    /// Changes after every [`Db::update_blob`] in this process, so a blob read under one
    /// epoch may be cached under it. Blobs are otherwise immutable.
    pub fn blob_epoch(&self) -> u64 {
        self.blob_epoch.load(Ordering::Acquire)
    }

    /// Every stored config entry as `(key, value)`.
    pub async fn config(&self) -> Result<Vec<(String, String)>, Error> {
        Ok(
            on!(self, pool => sqlx::query_as("SELECT key, value FROM config ORDER BY key")
            .fetch_all(pool)
            .await?),
        )
    }

    /// Upserts `set` and deletes `remove` in one transaction.
    pub async fn write_config(&self, set: &[(&str, &str)], remove: &[&str]) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            for (key, value) in set {
                sqlx::query(
                    "INSERT INTO config (key, value) VALUES ($1, $2) \
                     ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                )
                .bind(*key)
                .bind(*value)
                .execute(&mut *tx)
                .await?;
            }
            for key in remove {
                sqlx::query("DELETE FROM config WHERE key = $1").bind(*key).execute(&mut *tx).await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }

    /// The admin login as `(username, password_hash)`.
    pub async fn admin(&self) -> Result<Option<(String, String)>, Error> {
        let sql = "SELECT username, password_hash FROM accounts WHERE kind = $1";
        Ok(on!(self, pool => sqlx::query_as(sql).bind(ADMIN).fetch_optional(pool).await?))
    }

    /// Creates the admin login unless one exists; `false` if one did.
    pub async fn create_admin(&self, username: &str, password_hash: &str) -> Result<bool, Error> {
        let sql = "INSERT INTO accounts (kind, username, password_hash) VALUES ($1, $2, $3) \
                   ON CONFLICT DO NOTHING";
        let done = on!(self, pool => sqlx::query(sql)
            .bind(ADMIN)
            .bind(username)
            .bind(password_hash)
            .execute(pool)
            .await?
            .rows_affected());
        Ok(done == 1)
    }

    pub async fn set_admin(&self, username: &str, password_hash: &str) -> Result<(), Error> {
        let sql = "UPDATE accounts SET username = $2, password_hash = $3 WHERE kind = $1";
        on!(self, pool => sqlx::query(sql)
            .bind(ADMIN)
            .bind(username)
            .bind(password_hash)
            .execute(pool)
            .await?
            .rows_affected());
        Ok(())
    }

    /// Up to `limit` items with health work due at `now`, most urgent first: playback
    /// reports, never checked, user rechecks, then by schedule (newest release on ties).
    /// Without `routine` only rechecks and pending repairs are returned.
    // ponytail: never-checked rows are not in the `next_health_check` index; a scan is
    // fine at library scale and polls are rare.
    pub async fn due_health_work(
        &self,
        now: i64,
        routine: bool,
        limit: i64,
    ) -> Result<Vec<(Item, HealthStatus)>, Error> {
        // 4 = repairing, 5 = hidden, 7 = ignored.
        let sql = select_health!(
            "WHERE kind <> $1 AND meta_blob IS NOT NULL AND ( \
                (recheck_requested <> 0 AND health_state NOT IN (4, 5)) \
                OR (COALESCE(next_health_check, 0) <= $2 \
                    AND (health_state IN (4, 5) OR ($3 AND health_state <> 7)))) \
             ORDER BY CASE WHEN recheck_requested = 2 THEN 0 WHEN health_state = 0 THEN 1 \
                WHEN recheck_requested = 1 THEN 2 ELSE 3 END, \
                COALESCE(next_health_check, 0), COALESCE(release_date, created_at) DESC \
             LIMIT $4"
        );
        let rows: Vec<HealthWorkRow> = on!(self, pool => sqlx::query_as(sql)
            .bind(ItemKind::Directory as i16)
            .bind(now)
            .bind(routine)
            .bind(limit)
            .fetch_all(pool)
            .await?);
        rows.into_iter().map(TryInto::try_into).collect()
    }

    /// `id` with its health bookkeeping.
    pub async fn health(&self, id: ItemId) -> Result<Option<(Item, HealthStatus)>, Error> {
        let sql = select_health!("WHERE id = $1");
        let row: Option<HealthWorkRow> =
            on!(self, pool => sqlx::query_as(sql).bind(id.0).fetch_optional(pool).await?);
        row.map(TryInto::try_into).transpose()
    }

    /// Stores `status`. The recheck request is cleared only while it is still `handled`, so
    /// one that arrived meanwhile survives.
    pub async fn set_health(
        &self,
        id: ItemId,
        status: &HealthStatus,
        handled: Recheck,
    ) -> Result<(), Error> {
        let sql = "UPDATE items SET health_state = $2, last_health_check = $3, \
                   next_health_check = $4, repair_attempts = $5, hidden_until = $6, \
                   recheck_requested = CASE WHEN recheck_requested = $7 THEN 0 \
                   ELSE recheck_requested END, suspect_since = $8, \
                   arr_replacement_requested = CASE WHEN $2 IN (1, 2) THEN FALSE ELSE arr_replacement_requested END WHERE id = $1";
        let suspect_since = status
            .suspect_since
            .filter(|_| status.state == HealthState::Suspect);
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(status.state as i16)
            .bind(status.last_check)
            .bind(status.next_check)
            .bind(status.repair_attempts)
            .bind(status.hidden_until)
            .bind(handled as i16)
            .bind(suspect_since)
            .execute(pool)
            .await?
            .rows_affected());
        self.notify(Change::Health);
        Ok(())
    }

    /// Moves `id`'s next health work to `next_check` and drops its recheck request,
    /// leaving its state alone.
    pub async fn postpone_health(&self, id: ItemId, next_check: i64) -> Result<(), Error> {
        let sql = "UPDATE items SET next_health_check = $2, recheck_requested = 0 WHERE id = $1";
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(next_check)
            .execute(pool)
            .await?
            .rows_affected());
        self.notify(Change::Health);
        Ok(())
    }

    /// Asks for a check of `id` ahead of its schedule; a more urgent pending reason wins.
    pub async fn request_recheck(&self, id: ItemId, reason: Recheck) -> Result<(), Error> {
        let sql = "UPDATE items SET recheck_requested = $2 \
                   WHERE id = $1 AND recheck_requested < $2";
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(reason as i16)
            .execute(pool)
            .await?
            .rows_affected());
        Ok(())
    }

    /// Files per health state.
    pub async fn health_summary(&self) -> Result<Vec<(HealthState, u64)>, Error> {
        let rows: Vec<(i16, i64)> = on!(self, pool => sqlx::query_as(HEALTH_SUMMARY)
            .bind(ItemKind::Directory as i16)
            .fetch_all(pool)
            .await?);
        rows.into_iter()
            .map(|(s, n)| Ok((HealthState::try_from(s)?, n.cast_unsigned())))
            .collect()
    }

    /// Remembers a release we asked an Arr to replace until `expires_at`, by name and
    /// article ids.
    pub async fn reject_release(
        &self,
        name: &str,
        message_ids: &[&str],
        expires_at: i64,
    ) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            let id = Uuid::now_v7();
            sqlx::query("INSERT INTO rejected_releases (id, name, expires_at) VALUES ($1, $2, $3)")
                .bind(id)
                .bind(name)
                .bind(expires_at)
                .execute(&mut *tx)
                .await?;
            for message_id in message_ids {
                sqlx::query(
                    "INSERT INTO rejected_articles (message_id, release_id) VALUES ($1, $2) \
                     ON CONFLICT DO NOTHING",
                )
                .bind(*message_id)
                .bind(id)
                .execute(&mut *tx)
                .await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }

    /// Whether a release named `name` or carrying any of `message_ids` was rejected and
    /// has not expired at `now`.
    pub async fn is_rejected(
        &self,
        name: &str,
        message_ids: &[&str],
        now: i64,
    ) -> Result<bool, Error> {
        let by_name: Option<i32> = on!(self, pool => sqlx::query_scalar(
            "SELECT 1 FROM rejected_releases WHERE name = $1 AND expires_at > $2 LIMIT 1",
        )
        .bind(name)
        .bind(now)
        .fetch_optional(pool)
        .await?);
        if by_name.is_some() {
            return Ok(true);
        }
        for message_id in message_ids {
            let hit: Option<i32> = on!(self, pool => sqlx::query_scalar(
                "SELECT 1 FROM rejected_articles a JOIN rejected_releases r ON r.id = a.release_id \
                 WHERE a.message_id = $1 AND r.expires_at > $2 LIMIT 1",
            )
            .bind(*message_id)
            .bind(now)
            .fetch_optional(pool)
            .await?);
            if hit.is_some() {
                return Ok(true);
            }
        }
        Ok(false)
    }

    /// Rejected releases not expired at `now`, newest first.
    pub async fn rejected_releases(&self, now: i64) -> Result<Vec<RejectedRelease>, Error> {
        let rows: Vec<(Uuid, String, i64)> = on!(self, pool => sqlx::query_as(
            "SELECT id, name, expires_at FROM rejected_releases WHERE expires_at > $1 \
             ORDER BY id DESC",
        )
        .bind(now)
        .fetch_all(pool)
        .await?);
        Ok(rows
            .into_iter()
            .map(|(id, name, expires_at)| RejectedRelease {
                name,
                // The id is a v7 UUID, stamped when the release was rejected.
                rejected_at: id.get_timestamp().map_or(0, |t| {
                    let (secs, nanos) = t.to_unix();
                    i64::try_from(secs).unwrap_or(i64::MAX).saturating_mul(1000)
                        + i64::from(nanos / 1_000_000)
                }),
                expires_at,
            })
            .collect())
    }

    /// Forgets rejected releases expired at `now`.
    pub async fn prune_rejected_releases(&self, now: i64) -> Result<(), Error> {
        on!(self, pool => sqlx::query("DELETE FROM rejected_releases WHERE expires_at <= $1")
            .bind(now)
            .execute(pool)
            .await?
            .rows_affected());
        Ok(())
    }

    pub async fn record_health_check(&self, check: &HealthCheck) -> Result<(), Error> {
        let sql = "INSERT INTO health_checks (id, created_at, item_id, path, result, repair, message) \
                   VALUES ($1, $2, $3, $4, $5, $6, $7)";
        on!(self, pool => sqlx::query(sql)
            .bind(Uuid::now_v7())
            .bind(check.created_at)
            .bind(check.item_id.0)
            .bind(&check.path)
            .bind(check.result as i16)
            .bind(check.repair as i16)
            .bind(check.message.as_deref())
            .execute(pool)
            .await?
            .rows_affected());
        self.notify(Change::Health);
        Ok(())
    }

    /// The newest `limit` health check results matching `filter`, newest first, each with
    /// whether its item is still in the library.
    pub async fn health_checks(
        &self,
        filter: HealthFilter<'_>,
        limit: i64,
    ) -> Result<Vec<(HealthCheck, bool)>, Error> {
        type Row = (i64, Uuid, String, i16, i16, Option<String>, bool);
        let sql = "SELECT c.created_at, c.item_id, c.path, c.result, c.repair, c.message, \
                   i.id IS NOT NULL FROM health_checks c LEFT JOIN items i ON i.id = c.item_id \
                   WHERE ($1 IS NULL OR LOWER(c.path) LIKE $1 ESCAPE '\\') \
                   AND ($2 IS NULL OR c.result = $2) \
                   ORDER BY c.created_at DESC, c.id DESC LIMIT $3";
        // ponytail: `LOWER` folds ASCII only on SQLite; release names are ASCII.
        let search = filter
            .search
            .map(|s| format!("%{}%", like_escape(&s.to_lowercase())));
        let result = filter.result.map(|r| r as i16);
        let rows: Vec<Row> = on!(self, pool => sqlx::query_as(sql)
            .bind(search.as_deref())
            .bind(result)
            .bind(limit)
            .fetch_all(pool)
            .await?);
        rows.into_iter()
            .map(
                |(created_at, item, path, result, repair, message, exists)| {
                    let check = HealthCheck {
                        created_at,
                        item_id: ItemId(item),
                        path,
                        result: health_result(result)?,
                        repair: repair_action(repair)?,
                        message,
                    };
                    Ok((check, exists))
                },
            )
            .collect()
    }

    /// Forgets health check results older than `before`.
    pub async fn prune_health_checks(&self, before: i64) -> Result<(), Error> {
        on!(self, pool => sqlx::query("DELETE FROM health_checks WHERE created_at < $1")
            .bind(before)
            .execute(pool)
            .await?
            .rows_affected());
        Ok(())
    }

    /// The job, as `(id, name)`, that created the download directory at `dir_path`; it
    /// outlives the job's history row.
    pub async fn job_at(&self, dir_path: &str) -> Result<Option<(JobId, String)>, Error> {
        let sql = "SELECT job_id, job_name FROM items \
                   WHERE path = $1 AND job_id IS NOT NULL AND job_name IS NOT NULL";
        let row: Option<(Uuid, String)> =
            on!(self, pool => sqlx::query_as(sql).bind(dir_path).fetch_optional(pool).await?);
        Ok(row.map(|(id, name)| (JobId(id), name)))
    }

    /// Lowercased categories with finished jobs their Arrs have not been told about, with
    /// the generation to pass to [`Self::refreshed`].
    pub async fn pending_refreshes(&self) -> Result<Vec<(String, i64)>, Error> {
        let sql = "SELECT category, generation FROM arr_refresh ORDER BY category";
        Ok(on!(self, pool => sqlx::query_as(sql).fetch_all(pool).await?))
    }

    /// Marks `category` told, unless a job finished in it since `generation` was read.
    pub async fn refreshed(&self, category: &str, generation: i64) -> Result<(), Error> {
        let sql = "DELETE FROM arr_refresh WHERE category = $1 AND generation = $2";
        on!(self, pool => {
            sqlx::query(sql).bind(category).bind(generation).execute(pool).await?;
        });
        Ok(())
    }

    /// Reserves one search on Arr `instance` for every `kind` media id in `ids` at `now`:
    /// all of them, or none when any already had [`SEARCH_LIMIT`] searches within
    /// [`SEARCH_WINDOW_MS`]. A reservation is never given back.
    pub async fn reserve_searches(
        &self,
        instance: Uuid,
        kind: &str,
        ids: &[i64],
        now: i64,
    ) -> Result<bool, Error> {
        let ids: std::collections::BTreeSet<i64> = ids.iter().copied().collect();
        let since = now.saturating_sub(SEARCH_WINDOW_MS);
        let _serial = self.searches.lock().await;
        on!(self, pool => {
            let mut tx = pool.begin().await?;
            sqlx::query("DELETE FROM search_reservations WHERE at_ms <= $1")
                .bind(since)
                .execute(&mut *tx)
                .await?;
            for &id in &ids {
                let used: i64 = sqlx::query_scalar(
                    "SELECT COUNT(*) FROM search_reservations \
                     WHERE instance_id = $1 AND kind = $2 AND media_id = $3",
                )
                .bind(instance)
                .bind(kind)
                .bind(id)
                .fetch_one(&mut *tx)
                .await?;
                if used >= SEARCH_LIMIT {
                    tx.commit().await?;
                    return Ok(false);
                }
            }
            for &id in &ids {
                sqlx::query(
                    "INSERT INTO search_reservations (instance_id, kind, media_id, at_ms) \
                     VALUES ($1, $2, $3, $4)",
                )
                .bind(instance)
                .bind(kind)
                .bind(id)
                .bind(now)
                .execute(&mut *tx)
                .await?;
            }
            tx.commit().await?;
        });
        Ok(true)
    }

    /// The path of the download directory job `job` created, while it exists.
    pub async fn job_dir(&self, job: JobId) -> Result<Option<String>, Error> {
        let sql = "SELECT path FROM items WHERE job_id = $1";
        Ok(on!(self, pool => sqlx::query_scalar(sql).bind(job.0).fetch_optional(pool).await?))
    }

    /// Records that Arr `instance` imported `item` as its library file `file_id` at `path`.
    pub async fn record_owner(
        &self,
        instance: Uuid,
        item: ItemId,
        file_id: Option<i64>,
        path: &str,
        job: Option<JobId>,
    ) -> Result<(), Error> {
        let sql = "INSERT INTO arr_owners (instance_id, item_id, file_id, path, job_id) \
                   VALUES ($1, $2, $3, $4, $5) ON CONFLICT (instance_id, item_id) DO UPDATE \
                   SET file_id = excluded.file_id, path = excluded.path, job_id = excluded.job_id";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(instance)
                .bind(item.0)
                .bind(file_id)
                .bind(path)
                .bind(job.map(|j| j.0))
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Forgets the files of Arr `instance` matching `which`; returns their items no Arr
    /// owns anymore.
    pub async fn forget_owners(
        &self,
        instance: Uuid,
        which: &OwnedFile,
    ) -> Result<Vec<ItemId>, Error> {
        let sql = by_file!(
            which,
            "DELETE FROM arr_owners WHERE instance_id = $1 AND ",
            " RETURNING item_id"
        );
        let unowned = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let query = sqlx::query_scalar(sql).bind(instance);
            let items: Vec<Uuid> = match which {
                OwnedFile::Id(id) => query.bind(*id),
                OwnedFile::Path(p) => query.bind(p.as_str()),
                OwnedFile::Under(p) => query.bind(under(p)),
            }
            .fetch_all(&mut *tx)
            .await?;
            let mut unowned = Vec::new();
            for item in items {
                let left: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM arr_owners WHERE item_id = $1")
                    .bind(item)
                    .fetch_one(&mut *tx)
                    .await?;
                if left == 0 {
                    unowned.push(ItemId(item));
                }
            }
            tx.commit().await?;
            unowned
        });
        Ok(unowned)
    }

    /// Moves the files of Arr `instance` matching `which` to `path`.
    pub async fn rename_owner(
        &self,
        instance: Uuid,
        which: &OwnedFile,
        path: &str,
    ) -> Result<(), Error> {
        let sql = by_file!(
            which,
            "UPDATE arr_owners SET path = $3 WHERE instance_id = $1 AND ",
            ""
        );
        on!(self, pool => {
            let query = sqlx::query(sql).bind(instance);
            match which {
                OwnedFile::Id(id) => query.bind(*id),
                OwnedFile::Path(p) => query.bind(p.as_str()),
                OwnedFile::Under(p) => query.bind(under(p)),
            }
            .bind(path)
            .execute(pool)
            .await?;
        });
        Ok(())
    }

    /// The Arr instances that own `item`.
    pub async fn owner_instances(&self, item: ItemId) -> Result<Vec<Uuid>, Error> {
        let sql = "SELECT instance_id FROM arr_owners WHERE item_id = $1";
        Ok(on!(self, pool => sqlx::query_scalar(sql).bind(item.0).fetch_all(pool).await?))
    }

    /// The download each Arr instance recorded for `item`, where it sent one.
    pub async fn owner_jobs(&self, item: ItemId) -> Result<Vec<(Uuid, JobId)>, Error> {
        let sql = "SELECT instance_id, job_id FROM arr_owners \
                   WHERE item_id = $1 AND job_id IS NOT NULL";
        let rows: Vec<(Uuid, Uuid)> =
            on!(self, pool => sqlx::query_as(sql).bind(item.0).fetch_all(pool).await?);
        Ok(rows.into_iter().map(|(i, j)| (i, JobId(j))).collect())
    }

    /// The id of Arr `instance`'s file that is `item`, where it sent one.
    pub async fn owner_file(&self, instance: Uuid, item: ItemId) -> Result<Option<i64>, Error> {
        let sql = "SELECT file_id FROM arr_owners WHERE instance_id = $1 AND item_id = $2";
        let id: Option<Option<i64>> = on!(self, pool => sqlx::query_scalar(sql)
            .bind(instance)
            .bind(item.0)
            .fetch_optional(pool)
            .await?);
        Ok(id.flatten())
    }

    /// Every file Arr `instance` owns.
    pub async fn owners(&self, instance: Uuid) -> Result<Vec<ArrOwner>, Error> {
        let sql = "SELECT item_id, file_id, path FROM arr_owners WHERE instance_id = $1";
        let rows: Vec<(Uuid, Option<i64>, String)> =
            on!(self, pool => sqlx::query_as(sql).bind(instance).fetch_all(pool).await?);
        Ok(rows
            .into_iter()
            .map(|(item, file_id, path)| ArrOwner {
                item: ItemId(item),
                file_id,
                path,
            })
            .collect())
    }

    /// Forgets exactly `owner` of Arr `instance`, unless it changed since it was read.
    /// Returns its item when no Arr owns it anymore.
    pub async fn forget_owner(
        &self,
        instance: Uuid,
        owner: &ArrOwner,
    ) -> Result<Option<ItemId>, Error> {
        let sql = "DELETE FROM arr_owners WHERE instance_id = $1 AND item_id = $2 \
                   AND file_id IS NOT DISTINCT FROM $3 AND path = $4";
        let unowned = on!(self, pool => {
            let mut tx = pool.begin().await?;
            let gone = sqlx::query(sql)
                .bind(instance)
                .bind(owner.item.0)
                .bind(owner.file_id)
                .bind(&owner.path)
                .execute(&mut *tx)
                .await?
                .rows_affected();
            let left: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM arr_owners WHERE item_id = $1")
                .bind(owner.item.0)
                .fetch_one(&mut *tx)
                .await?;
            tx.commit().await?;
            gone > 0 && left == 0
        });
        Ok(unowned.then_some(owner.item))
    }

    /// Gives `owner` of Arr `instance` library file id `file_id`, unless it changed since
    /// it was read.
    pub async fn refile_owner(
        &self,
        instance: Uuid,
        owner: &ArrOwner,
        file_id: i64,
    ) -> Result<(), Error> {
        let sql = "UPDATE arr_owners SET file_id = $5 WHERE instance_id = $1 AND item_id = $2 \
                   AND file_id IS NOT DISTINCT FROM $3 AND path = $4";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(instance)
                .bind(owner.item.0)
                .bind(owner.file_id)
                .bind(&owner.path)
                .bind(file_id)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Every Arr file that is `item`, with its instance.
    pub async fn item_owners(&self, item: ItemId) -> Result<Vec<(Uuid, ArrOwner)>, Error> {
        let sql = "SELECT instance_id, file_id, path FROM arr_owners WHERE item_id = $1";
        let rows: Vec<(Uuid, Option<i64>, String)> =
            on!(self, pool => sqlx::query_as(sql).bind(item.0).fetch_all(pool).await?);
        Ok(rows
            .into_iter()
            .map(|(instance, file_id, path)| {
                (
                    instance,
                    ArrOwner {
                        item,
                        file_id,
                        path,
                    },
                )
            })
            .collect())
    }

    /// Moves Arr `instance`'s file that is `item` from `from` to `to`, waiting for a new
    /// file id when `pending`. Does nothing when it is no longer at `from`.
    pub async fn move_owner(
        &self,
        instance: Uuid,
        item: ItemId,
        from: &str,
        to: &str,
        pending: bool,
    ) -> Result<(), Error> {
        let sql = "UPDATE arr_owners SET path = $4, remap_pending = $5 \
                   WHERE instance_id = $1 AND item_id = $2 AND path = $3";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(instance)
                .bind(item.0)
                .bind(from)
                .bind(to)
                .bind(pending)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Gives Arr `instance`'s file that is `item` the id its rescan gave it.
    pub async fn settle_owner(
        &self,
        instance: Uuid,
        item: ItemId,
        file_id: i64,
    ) -> Result<(), Error> {
        let sql = "UPDATE arr_owners SET file_id = $3, remap_pending = FALSE \
                   WHERE instance_id = $1 AND item_id = $2";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(instance)
                .bind(item.0)
                .bind(file_id)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// How many Arr files wait for a new file id, of `instance` or of any.
    pub async fn remap_pending(&self, instance: Option<Uuid>) -> Result<u64, Error> {
        let sql = "SELECT COUNT(*) FROM arr_owners WHERE remap_pending \
                   AND ($1 IS NULL OR instance_id = $1)";
        let n: i64 =
            on!(self, pool => sqlx::query_scalar(sql).bind(instance).fetch_one(pool).await?);
        Ok(n.cast_unsigned())
    }

    /// Whether an Arr file that is `item` waits for a new file id.
    pub async fn item_remap_pending(&self, item: ItemId) -> Result<bool, Error> {
        let sql = "SELECT 1 FROM arr_owners WHERE item_id = $1 AND remap_pending LIMIT 1";
        let found: Option<i32> =
            on!(self, pool => sqlx::query_scalar(sql).bind(item.0).fetch_optional(pool).await?);
        Ok(found.is_some())
    }

    /// Records or updates a library conversion journal row.
    pub async fn put_conversion(&self, c: &Conversion) -> Result<(), Error> {
        let sql = "INSERT INTO library_files (source, target, item_id, state, from_link, \
                   to_link, original, written, owners, reason, updated_at) \
                   VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) \
                   ON CONFLICT (source) DO UPDATE SET target = excluded.target, \
                   item_id = excluded.item_id, state = excluded.state, \
                   from_link = excluded.from_link, to_link = excluded.to_link, \
                   original = excluded.original, written = excluded.written, \
                   owners = excluded.owners, reason = excluded.reason, \
                   updated_at = excluded.updated_at";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(&c.source)
                .bind(&c.target)
                .bind(c.item.0)
                .bind(c.state)
                .bind(c.from_link)
                .bind(c.to_link)
                .bind(&c.original)
                .bind(&c.written)
                .bind(&c.owners)
                .bind(&c.reason)
                .bind(now_ms())
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Every library conversion journal row, oldest change first.
    pub async fn conversions(&self) -> Result<Vec<Conversion>, Error> {
        let sql = "SELECT source, target, item_id, state, from_link, to_link, original, \
                   written, owners, reason FROM library_files ORDER BY updated_at, source";
        let rows: Vec<ConversionRow> =
            on!(self, pool => sqlx::query_as(sql).fetch_all(pool).await?);
        Ok(rows.into_iter().map(Conversion::from).collect())
    }

    pub async fn forget_conversion(&self, source: &str) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("DELETE FROM library_files WHERE source = $1")
                .bind(source)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Forgets the journal rows in `state`.
    pub async fn forget_conversions(&self, state: i16) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("DELETE FROM library_files WHERE state = $1")
                .bind(state)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// Stores a webhook delivery from Arr `instance` until it is applied.
    pub async fn push_arr_event(&self, instance: Uuid, body: &[u8]) -> Result<(), Error> {
        let sql = "INSERT INTO arr_events (id, instance_id, body) VALUES ($1, $2, $3)";
        on!(self, pool => {
            sqlx::query(sql)
                .bind(Uuid::now_v7())
                .bind(instance)
                .bind(body)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// The oldest stored delivery as `(id, instance, body)`.
    pub async fn next_arr_event(&self) -> Result<Option<(Uuid, Uuid, Vec<u8>)>, Error> {
        let sql = "SELECT id, instance_id, body FROM arr_events ORDER BY id LIMIT 1";
        Ok(on!(self, pool => sqlx::query_as(sql).fetch_optional(pool).await?))
    }

    pub async fn drop_arr_event(&self, id: Uuid) -> Result<(), Error> {
        on!(self, pool => {
            sqlx::query("DELETE FROM arr_events WHERE id = $1")
                .bind(id)
                .execute(pool)
                .await?;
        });
        Ok(())
    }

    /// One page of items under `/content` matching `q`, directories first, and how many
    /// match in total.
    pub async fn files(&self, q: &FileQuery<'_>) -> Result<(Vec<FileRow>, u64), Error> {
        // The page is chosen first, so only its rows look up their latest check.
        macro_rules! sql {
            ($order:literal) => {
                concat!(
                    "SELECT i.*, h.created_at AS h_created_at, h.result AS h_result, \
                     h.repair AS h_repair, h.message AS h_message FROM (\
                     SELECT i.id, i.parent_id, i.name, i.path, i.kind, i.size, i.meta_blob, \
                     i.nzb_blob, i.created_at, i.release_date, i.health_state, i.encrypted, \
                     i.last_health_check, i.next_health_check ",
                    file_filter!(),
                    " ORDER BY CASE WHEN i.kind = 1 THEN 0 ELSE 1 END, ",
                    $order,
                    " LIMIT $14 OFFSET $15) i LEFT JOIN health_checks h ON h.id = ",
                    latest_check!("i.id"),
                    " ORDER BY CASE WHEN i.kind = 1 THEN 0 ELSE 1 END, ",
                    $order
                )
            };
        }
        let sql = match (q.sort, q.descending) {
            (FileSort::Name, false) => sql!("LOWER(i.name), i.id"),
            (FileSort::Name, true) => sql!("LOWER(i.name) DESC, i.id DESC"),
            (FileSort::Size, false) => sql!("COALESCE(i.size, 0), i.id"),
            (FileSort::Size, true) => sql!("COALESCE(i.size, 0) DESC, i.id DESC"),
            (FileSort::Added, false) => sql!("i.created_at, i.id"),
            (FileSort::Added, true) => sql!("i.created_at DESC, i.id DESC"),
        };
        let search = q
            .search
            .map(|s| format!("%{}%", like_escape(&s.to_lowercase())));
        let category = q.category.map(|c| format!("/content/{}/%", like_escape(c)));
        let count_sql = concat!("SELECT COUNT(*) ", file_filter!());
        let (rows, total): (Vec<FileRowRaw>, i64) = on!(self, pool => {
            let rows = bind_file_filter!(sqlx::query_as(sql), q, search, category)
                .bind(q.limit)
                .bind(q.offset)
                .fetch_all(pool)
                .await?;
            let total = bind_file_filter!(sqlx::query_scalar(count_sql), q, search, category)
                .fetch_one(pool)
                .await?;
            (rows, total)
        });
        let rows = rows
            .into_iter()
            .map(FileRow::try_from)
            .collect::<Result<_, _>>()?;
        Ok((rows, total.cast_unsigned()))
    }

    /// The newest `limit` health checks of `item`, newest first.
    pub async fn item_checks(&self, item: ItemId, limit: i64) -> Result<Vec<HealthCheck>, Error> {
        type Row = (i64, String, i16, i16, Option<String>);
        let sql = "SELECT created_at, path, result, repair, message FROM health_checks \
                   WHERE item_id = $1 ORDER BY created_at DESC, id DESC LIMIT $2";
        let rows: Vec<Row> =
            on!(self, pool => sqlx::query_as(sql).bind(item.0).bind(limit).fetch_all(pool).await?);
        rows.into_iter()
            .map(|(created_at, path, result, repair, message)| {
                Ok(HealthCheck {
                    created_at,
                    item_id: item,
                    path,
                    result: health_result(result)?,
                    repair: repair_action(repair)?,
                    message,
                })
            })
            .collect()
    }

    /// What deleting `id` removes: its subtree's files, directories and file bytes.
    pub async fn subtree_stats(&self, id: ItemId) -> Result<SubtreeStats, Error> {
        let sql = "WITH RECURSIVE sub (id) AS ( \
                SELECT id FROM items WHERE id = $1 \
                UNION ALL SELECT i.id FROM items i JOIN sub ON i.parent_id = sub.id) \
            SELECT CAST(COALESCE(SUM(CASE WHEN kind = $2 THEN 0 ELSE 1 END), 0) AS BIGINT), \
                CAST(COALESCE(SUM(CASE WHEN kind = $2 THEN 1 ELSE 0 END), 0) AS BIGINT), \
                CAST(COALESCE(SUM(size), 0) AS BIGINT) \
            FROM items WHERE id IN (SELECT id FROM sub)";
        let (files, directories, bytes): (i64, i64, i64) = on!(self, pool => sqlx::query_as(sql)
            .bind(id.0)
            .bind(ItemKind::Directory as i16)
            .fetch_one(pool)
            .await?);
        Ok(SubtreeStats {
            files: files.cast_unsigned(),
            directories: directories.cast_unsigned(),
            bytes: bytes.cast_unsigned(),
        })
    }

    /// Files in the library: count, total and largest size, and how many were added since
    /// `added_since` (Unix milliseconds).
    pub async fn catalogue(&self, added_since: i64) -> Result<Catalogue, Error> {
        let sql = "SELECT CAST(COUNT(*) AS BIGINT), CAST(COALESCE(SUM(size), 0) AS BIGINT), \
                CAST(COALESCE(MAX(size), 0) AS BIGINT), \
                CAST(COALESCE(SUM(CASE WHEN created_at >= $2 THEN 1 ELSE 0 END), 0) AS BIGINT) \
            FROM items WHERE kind <> $1";
        let (files, bytes, largest, added): (i64, i64, i64, i64) = on!(self, pool => sqlx::query_as(sql)
            .bind(ItemKind::Directory as i16)
            .bind(added_since)
            .fetch_one(pool)
            .await?);
        Ok(Catalogue {
            files: files.cast_unsigned(),
            bytes: bytes.cast_unsigned(),
            largest: largest.cast_unsigned(),
            added: added.cast_unsigned(),
        })
    }
}

/// `s` matching itself literally inside a `LIKE … ESCAPE '\'` pattern.
fn like_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for c in s.chars() {
        if matches!(c, '\\' | '%' | '_') {
            out.push('\\');
        }
        out.push(c);
    }
    out
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FileSort {
    #[default]
    Name,
    Size,
    Added,
}

/// Filters, order and page for [`Db::files`]; `None` matches anything.
#[derive(Clone, Debug, Default)]
pub struct FileQuery<'a> {
    pub parent: Option<ItemId>,
    pub files_only: bool,
    pub kind: Option<ItemKind>,
    /// Case-insensitive substring of the path.
    pub search: Option<&'a str>,
    pub category: Option<&'a str>,
    pub min_size: Option<u64>,
    pub max_size: Option<u64>,
    /// Unix milliseconds, inclusive.
    pub added_after: Option<i64>,
    /// Unix milliseconds, exclusive.
    pub added_before: Option<i64>,
    /// Hidden items only match when asked for by state.
    pub state: Option<HealthState>,
    /// Matches the latest check.
    pub result: Option<HealthResult>,
    /// Matches the latest check.
    pub repair: Option<Repair>,
    pub encrypted: Option<bool>,
    pub sort: FileSort,
    pub descending: bool,
    pub limit: i64,
    pub offset: i64,
}

/// An item with its health schedule and latest check.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FileRow {
    pub item: Item,
    pub last_health_check: Option<i64>,
    pub next_health_check: Option<i64>,
    pub health: Option<HealthCheck>,
}

#[derive(sqlx::FromRow)]
struct FileRowRaw {
    #[sqlx(flatten)]
    item: ItemRow,
    last_health_check: Option<i64>,
    next_health_check: Option<i64>,
    h_created_at: Option<i64>,
    h_result: Option<i16>,
    h_repair: Option<i16>,
    h_message: Option<String>,
}

impl TryFrom<FileRowRaw> for FileRow {
    type Error = Error;

    fn try_from(r: FileRowRaw) -> Result<Self, Error> {
        let item = Item::try_from(r.item)?;
        let health = match (r.h_created_at, r.h_result, r.h_repair) {
            (Some(created_at), Some(result), Some(repair)) => Some(HealthCheck {
                created_at,
                item_id: item.id,
                path: item.path.clone(),
                result: health_result(result)?,
                repair: repair_action(repair)?,
                message: r.h_message,
            }),
            _ => None,
        };
        Ok(Self {
            item,
            last_health_check: r.last_health_check,
            next_health_check: r.next_health_check,
            health,
        })
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SubtreeStats {
    pub files: u64,
    pub directories: u64,
    pub bytes: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Catalogue {
    pub files: u64,
    pub bytes: u64,
    pub largest: u64,
    pub added: u64,
}

fn health_result(v: i16) -> Result<HealthResult, Error> {
    match v {
        0 => Ok(HealthResult::Healthy),
        1 => Ok(HealthResult::Missing),
        2 => Ok(HealthResult::Degraded),
        3 => Ok(HealthResult::Inconclusive),
        4 => Ok(HealthResult::Damaged),
        _ => Err(Error::Corrupt("unknown health result")),
    }
}

fn repair_action(v: i16) -> Result<Repair, Error> {
    match v {
        0 => Ok(Repair::None),
        1 => Ok(Repair::ReplacementRequested),
        2 => Ok(Repair::Replaced),
        3 => Ok(Repair::ActionNeeded),
        4 => Ok(Repair::Deleted),
        _ => Err(Error::Corrupt("unknown repair action")),
    }
}

/// A check's outcome. Stored as `SMALLINT`; values must never change.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i16)]
pub enum HealthResult {
    Healthy = 0,
    /// Confirmed missing or corrupt beyond what playback tolerates.
    Missing = 1,
    Degraded = 2,
    /// A provider failed, so the check proved nothing.
    Inconclusive = 3,
    /// The articles are there but the container structure is broken.
    Damaged = 4,
}

/// What repair did. Stored as `SMALLINT`; values must never change.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i16)]
pub enum Repair {
    None = 0,
    /// An Arr accepted a replacement request; the item is hidden meanwhile.
    ReplacementRequested = 1,
    /// The replacement arrived and the item was deleted.
    Replaced = 2,
    /// Nothing could repair it automatically.
    ActionNeeded = 3,
    /// Deleted without a replacement (no owner).
    Deleted = 4,
}

/// One health check outcome. Outlives the item it describes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HealthCheck {
    pub created_at: i64,
    pub item_id: ItemId,
    pub path: String,
    pub result: HealthResult,
    pub repair: Repair,
    pub message: Option<String>,
}

/// A file's first health check, done by its import and stored with its output.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FirstCheck {
    /// Its state, `last_check` (when the check ran) and `next_check`.
    pub status: HealthStatus,
    pub result: HealthResult,
    pub message: Option<String>,
    /// Container evidence that alone makes the file `suspect`.
    pub defect: Option<Defect>,
    /// Asks for a media analysis.
    pub analyze: bool,
}

/// A release we asked an Arr to replace; matching NZBs fail until `expires_at`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RejectedRelease {
    pub name: String,
    /// Unix milliseconds.
    pub rejected_at: i64,
    /// Unix milliseconds.
    pub expires_at: i64,
}

/// `accounts.kind` of the single admin login.
const ADMIN: i16 = 0;

/// What the latest media analysis of a file found. Stored as `SMALLINT`; values must
/// never change.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i16)]
pub enum ProbeOutcome {
    /// Metadata was read.
    Discovered = 0,
    /// The container holds no audio or video stream.
    NoStreams = 1,
    /// The analyzer does not read this container.
    Unsupported = 2,
    /// The attempt proved nothing (provider failure, limit, timeout, unreadable output).
    Inconclusive = 3,
}

impl TryFrom<i16> for ProbeOutcome {
    type Error = Error;

    fn try_from(v: i16) -> Result<Self, Error> {
        [
            Self::Discovered,
            Self::NoStreams,
            Self::Unsupported,
            Self::Inconclusive,
        ]
        .into_iter()
        .find(|o| *o as i16 == v)
        .ok_or(Error::Corrupt("unknown probe outcome"))
    }
}

/// A container defect seen on verified bytes, awaiting or past its confirmation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Defect {
    /// Where the broken box header starts.
    pub offset: u64,
    /// The size the header claims.
    pub size: u64,
    /// Unix milliseconds of the first sighting.
    pub seen_at: i64,
    /// Identity of the article layout and classifier that saw it.
    pub source: i64,
}

/// A file's media analysis state.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct MediaProbe {
    /// Unix milliseconds of the pending request.
    pub requested_at: Option<i64>,
    pub attempted_at: Option<i64>,
    pub outcome: Option<ProbeOutcome>,
    pub reason: Option<String>,
    /// Metadata JSON of the last successful attempt; carries its own schema version.
    pub info: Option<String>,
    pub info_at: Option<i64>,
    pub defect: Option<Defect>,
    /// The file is `suspect` only because of `defect`.
    pub structural_hold: bool,
}

/// One finished analysis attempt.
#[derive(Clone, Copy, Debug)]
pub struct ProbeAttempt<'a> {
    pub at: i64,
    pub outcome: ProbeOutcome,
    pub reason: Option<&'a str>,
    /// Replaces the stored metadata when set.
    pub info: Option<&'a str>,
}

#[derive(sqlx::FromRow)]
struct MediaProbeRow {
    requested_at: Option<i64>,
    attempted_at: Option<i64>,
    outcome: Option<i16>,
    reason: Option<String>,
    info: Option<String>,
    info_at: Option<i64>,
    defect_offset: Option<i64>,
    defect_size: Option<i64>,
    defect_seen_at: Option<i64>,
    defect_source: Option<i64>,
    structural_hold: i16,
}

impl TryFrom<MediaProbeRow> for MediaProbe {
    type Error = Error;

    fn try_from(r: MediaProbeRow) -> Result<Self, Error> {
        let defect = match (
            r.defect_offset,
            r.defect_size,
            r.defect_seen_at,
            r.defect_source,
        ) {
            (Some(offset), Some(size), Some(seen_at), Some(source)) => Some(Defect {
                offset: offset.cast_unsigned(),
                size: size.cast_unsigned(),
                seen_at,
                source,
            }),
            _ => None,
        };
        Ok(Self {
            requested_at: r.requested_at,
            attempted_at: r.attempted_at,
            outcome: r.outcome.map(ProbeOutcome::try_from).transpose()?,
            reason: r.reason,
            info: r.info,
            info_at: r.info_at,
            defect,
            structural_hold: r.structural_hold != 0,
        })
    }
}

impl Db {
    /// `id`'s media analysis state; default when never analyzed.
    pub async fn media_probe(&self, id: ItemId) -> Result<MediaProbe, Error> {
        let sql = "SELECT requested_at, attempted_at, outcome, reason, info, info_at, \
                   defect_offset, defect_size, defect_seen_at, defect_source, structural_hold \
                   FROM media_probes WHERE item_id = $1";
        let row: Option<MediaProbeRow> =
            on!(self, pool => sqlx::query_as(sql).bind(id.0).fetch_optional(pool).await?);
        Ok(row
            .map(MediaProbe::try_from)
            .transpose()?
            .unwrap_or_default())
    }

    /// Asks for an analysis of `id`; a pending request keeps its place.
    pub async fn request_media_probe(&self, id: ItemId, now: i64) -> Result<(), Error> {
        let sql = "INSERT INTO media_probes (item_id, requested_at) VALUES ($1, $2) \
                   ON CONFLICT (item_id) DO UPDATE \
                   SET requested_at = COALESCE(media_probes.requested_at, excluded.requested_at)";
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(now)
            .execute(pool)
            .await?
            .rows_affected());
        self.notify(Change::Health);
        Ok(())
    }

    /// The oldest pending analysis of a file that is not being repaired or replaced, with
    /// its request time.
    pub async fn next_media_probe(&self) -> Result<Option<(Item, i64)>, Error> {
        #[derive(sqlx::FromRow)]
        struct Row {
            #[sqlx(flatten)]
            item: ItemRow,
            requested_at: i64,
        }
        // 4 = repairing, 5 = hidden.
        let sql = concat!(
            "SELECT ",
            item_columns!(),
            ", requested_at FROM items JOIN media_probes ON item_id = id \
             WHERE requested_at IS NOT NULL AND health_state NOT IN (4, 5) \
             ORDER BY requested_at LIMIT 1"
        );
        let row: Option<Row> = on!(self, pool => sqlx::query_as(sql).fetch_optional(pool).await?);
        row.map(|r| Ok((Item::try_from(r.item)?, r.requested_at)))
            .transpose()
    }

    /// Stores an attempt on `id`. The pending request is cleared only while it is still
    /// the `consumed` one, so a newer request survives.
    pub async fn finish_media_probe(
        &self,
        id: ItemId,
        consumed: i64,
        attempt: &ProbeAttempt<'_>,
    ) -> Result<(), Error> {
        let sql = "UPDATE media_probes SET attempted_at = $2, outcome = $3, reason = $4, \
                   info = COALESCE($5, info), \
                   info_at = CASE WHEN $5 IS NULL THEN info_at ELSE $2 END, \
                   requested_at = CASE WHEN requested_at = $6 THEN NULL ELSE requested_at END \
                   WHERE item_id = $1";
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(attempt.at)
            .bind(attempt.outcome as i16)
            .bind(attempt.reason)
            .bind(attempt.info)
            .bind(consumed)
            .execute(pool)
            .await?
            .rows_affected());
        self.notify(Change::Health);
        Ok(())
    }

    /// Stores `id`'s container evidence and whether it alone makes the file `suspect`.
    pub async fn set_structural(
        &self,
        id: ItemId,
        defect: Option<&Defect>,
        hold: bool,
    ) -> Result<(), Error> {
        // Skipped once the item is gone.
        let sql = "INSERT INTO media_probes (item_id, defect_offset, defect_size, defect_seen_at, \
                   defect_source, structural_hold) \
                   SELECT $1, $2, $3, $4, $5, $6 WHERE EXISTS (SELECT 1 FROM items WHERE id = $1) \
                   ON CONFLICT (item_id) DO UPDATE SET defect_offset = excluded.defect_offset, \
                   defect_size = excluded.defect_size, defect_seen_at = excluded.defect_seen_at, \
                   defect_source = excluded.defect_source, \
                   structural_hold = excluded.structural_hold";
        on!(self, pool => sqlx::query(sql)
            .bind(id.0)
            .bind(defect.map(|d| d.offset.cast_signed()))
            .bind(defect.map(|d| d.size.cast_signed()))
            .bind(defect.map(|d| d.seen_at))
            .bind(defect.map(|d| d.source))
            .bind(i16::from(hold))
            .execute(pool)
            .await?
            .rows_affected());
        Ok(())
    }
}

/// Unix milliseconds now.
#[must_use]
pub fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;

    /// Reads in a transaction while another connection writes, then writes.
    async fn read_then_write(
        pool: &sqlx::SqlitePool,
        begin: &'static str,
    ) -> Result<(), sqlx::Error> {
        let mut tx = pool.begin_with(begin).await?;
        sqlx::query("SELECT COUNT(*) FROM t")
            .execute(&mut *tx)
            .await?;
        let other = pool.clone();
        let mut writer = Some(tokio::spawn(async move {
            sqlx::query("INSERT INTO t VALUES (1)")
                .execute(&other)
                .await
        }));
        if begin == "BEGIN" {
            // A deferred read holds no write lock, so the other writer commits first.
            writer
                .take()
                .expect("spawned")
                .await
                .expect("writer task")?;
        } else {
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
        let wrote = sqlx::query("INSERT INTO t VALUES (2)")
            .execute(&mut *tx)
            .await;
        if wrote.is_ok() {
            tx.commit().await?;
        }
        if let Some(writer) = writer {
            writer.await.expect("writer task")?;
        }
        wrote.map(drop)
    }

    #[tokio::test]
    async fn the_health_summary_reads_its_index() {
        let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .expect("connect");
        let Pool::Sqlite(pool) = &db.pool else {
            unreachable!("the test opens SQLite")
        };
        let plan: Vec<(i64, i64, i64, String)> = sqlx::query_as(sqlx::AssertSqlSafe(format!(
            "EXPLAIN QUERY PLAN {HEALTH_SUMMARY}"
        )))
        .bind(ItemKind::Directory as i16)
        .fetch_all(pool)
        .await
        .expect("plan");
        let plan: Vec<_> = plan.into_iter().map(|row| row.3).collect();
        assert_eq!(plan, ["SCAN items USING INDEX items_health_summary"]);

        db.close().await;
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
        }
    }

    #[tokio::test]
    async fn a_read_then_write_survives_a_concurrent_sqlite_writer() {
        let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .expect("connect");
        let Pool::Sqlite(pool) = &db.pool else {
            unreachable!("the test opens SQLite")
        };
        sqlx::query("CREATE TABLE t (x INTEGER)")
            .execute(pool)
            .await
            .expect("create");

        let deferred = read_then_write(pool, "BEGIN")
            .await
            .expect_err("snapshot is stale");
        let code = deferred
            .as_database_error()
            .and_then(sqlx::error::DatabaseError::code);
        assert_eq!(code.as_deref(), Some("517"), "{deferred}");
        read_then_write(pool, db.begin_write())
            .await
            .expect("an immediate transaction waits for the other writer");

        db.close().await;
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
        }
    }
}
