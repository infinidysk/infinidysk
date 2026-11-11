//! Writes a C# install's tree, history and queue into our database. C# ids are kept, so
//! existing symlinks and STRM links resolve. Rows already written are skipped, so an
//! interrupted import resumes where it stopped.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::fmt;
use std::path::Path;

use futures_util::{StreamExt as _, stream};
use infinidysk_archive::rar::{Scanner, Step};
use infinidysk_core::crypt::{Crypt, Key};
use infinidysk_core::{
    BlobId, ByteRange, FileMeta, FilePart, ItemId, ItemKind, JobId, MultipartFile, NzbFile,
    Par2Proof, Segment, Segments, blob,
};
use infinidysk_db::{
    Db, Handoff, HealthState, HealthStatus, HistoryEntry, Item, JobStatus, QueueEntry, now_ms,
};
use infinidysk_nntp::{Body, Client, Workload};
use jiff::Timestamp;
use jiff::tz::TimeZone;
use uuid::Uuid;

use crate::csharp::{self, LongRange, decode};
use crate::{Error, MAX_NZB_BLOB_BYTES, open, open_blob, read_blob};

/// C#'s fixed folders (`/`, `/nzbs`, `/content`, `/completed-symlinks`, `/.ids`) have
/// ids up to this one.
const CS_SYSTEM: u128 = 4;
const CS_CONTENT: Uuid = Uuid::from_u128(2);
/// Where C#'s empty-named category folder and empty job categories go.
const UNCATEGORIZED: &str = "uncategorized";
/// Directories converted at once; each may fetch a few articles.
const PARALLEL: usize = 16;
/// Rows per write transaction.
const BATCH: usize = 500;
/// NZB bytes per write transaction.
const NZB_BATCH: usize = 64 << 20;
/// Archive trailer bytes C# may omit from a volume's recorded length.
const MAX_TRAILER: u64 = 4096;
/// The longest interval between routine health checks (the health crate's cadence cap).
const MAX_CHECK_INTERVAL_MS: i64 = 90 * 86_400_000;
/// C# stores times as `DateTime` text; this is Unix milliseconds in SQLite, read as UTC.
/// C# wrote local wall-clock time, so pass the result through [`from_wall`].
macro_rules! ms {
    ($column:literal) => {
        concat!(
            "CAST(ROUND((julianday(",
            $column,
            ") - 2440587.5) * 86400000) AS INTEGER)"
        )
    };
}

/// What [`apply`] wrote and what it left behind.
#[derive(Debug, Default)]
pub struct Imported {
    pub directories: u64,
    pub files: u64,
    pub nzbs: u64,
    pub history: u64,
    pub queued: u64,
    /// Articles fetched to learn segment sizes C# never recorded.
    pub fetched: u64,
    /// Of those, fetches that failed at the provider rather than on the article; files
    /// they cost may carry over on a later try.
    pub provider_errors: u64,
    /// Files not carried over, as `(path, reason)`.
    pub skipped: Vec<(String, String)>,
    /// Files not carried over, by reason.
    pub skip_reasons: BTreeMap<String, u64>,
    pub skipped_count: u64,
}

/// What an import is doing, in order.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Stage {
    /// Copying the tables the import reads from a 1.x PostgreSQL database.
    Copying,
    /// Reading the C# database and every file's metadata blob.
    #[default]
    Checking,
    Directories,
    Nzbs,
    Files,
    /// History, queue and settings.
    Finishing,
}

/// One stage of this attempt; items an earlier attempt wrote are not counted.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Progress {
    pub stage: Stage,
    /// Items this stage has finished; in [`Stage::Files`], files written.
    pub done: u64,
    /// Items this stage has to do; `None` while still counting them.
    pub total: Option<u64>,
    /// Files not carried over in [`Stage::Files`].
    pub skipped: u64,
}

impl Progress {
    pub(crate) const fn counting(stage: Stage) -> Self {
        Self {
            stage,
            done: 0,
            total: None,
            skipped: 0,
        }
    }

    pub(crate) const fn at(stage: Stage, done: u64, total: u64) -> Self {
        Self {
            stage,
            done,
            total: Some(total),
            skipped: 0,
        }
    }
}

impl Imported {
    fn skip(&mut self, path: String, reason: String) {
        self.skipped_count += 1;
        *self.skip_reasons.entry(reason.clone()).or_default() += 1;
        self.skipped.push((path, reason));
    }
}

impl fmt::Display for Imported {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(
            f,
            "Imported {} directories, {} files, {} NZBs, {} history rows, {} queued jobs",
            self.directories, self.files, self.nzbs, self.history, self.queued
        )?;
        writeln!(
            f,
            "  fetched {} articles to learn segment sizes",
            self.fetched
        )?;
        if self.skipped_count > 0 {
            writeln!(f, "Files not carried over: {}", self.skipped_count)?;
            for (reason, n) in &self.skip_reasons {
                writeln!(f, "  {n}: {reason}")?;
                for (path, _) in self.skipped.iter().filter(|(_, r)| r == reason) {
                    writeln!(f, "    {path}")?;
                }
            }
        }
        Ok(())
    }
}

/// `(id, parent, name, path, created ms)`.
type DirRow = (String, String, String, String, i64);

#[derive(sqlx::FromRow)]
struct FileRow {
    id: String,
    parent: String,
    name: String,
    path: String,
    sub_type: i64,
    size: Option<i64>,
    meta: Option<String>,
    nzb: Option<String>,
    created: i64,
    release_date: Option<i64>,
    last_check: Option<i64>,
    next_check: Option<i64>,
    repair_pending: bool,
}

#[derive(sqlx::FromRow)]
struct HistoryRow {
    id: String,
    created: i64,
    file_name: String,
    job_name: String,
    category: String,
    status: i64,
    bytes: i64,
    seconds: i64,
    fail_message: Option<String>,
    dir: Option<String>,
    dir_name: Option<String>,
    nzb: Option<String>,
}

#[derive(sqlx::FromRow)]
struct QueueRow {
    id: String,
    created: i64,
    file_name: String,
    job_name: String,
    category: String,
    priority: i64,
    post_processing: i64,
    bytes: i64,
    retry_at: Option<i64>,
    nzb: String,
}

#[derive(sqlx::FromRow)]
struct DownloadRow {
    id: String,
    path: String,
    dir: bool,
    arr: Option<String>,
    nzb: Option<String>,
}

/// A C# job folder, for [`stamp_download_ids`].
#[derive(Default)]
struct Job {
    id: Option<Uuid>,
    download: Option<Uuid>,
    /// The downloads its files name.
    files: HashSet<Uuid>,
}

/// Writes the C# data in `config` into `db`, fetching through `client` the article
/// sizes C# never recorded. Settings are not touched. C# reported a completed job at
/// `<storage_root>/<category>/<job folder>`.
pub async fn apply(
    config: &Path,
    db: &Db,
    client: &Client,
    storage_root: &str,
) -> Result<Imported, Error> {
    apply_with_progress(config, db, client, storage_root, |_| {}).await
}

/// Like [`apply`], reporting each stage after the survey and its counts.
pub async fn apply_with_progress(
    config: &Path,
    db: &Db,
    client: &Client,
    storage_root: &str,
    progress: impl Fn(Progress) + Sync,
) -> Result<Imported, Error> {
    let mut cs = open(config).await?;
    crate::legacy_repairs::stage(config, db).await?;
    let blobs = config.join("blobs");
    // C# wrote `DateTime.Now`, so its times are wall clock in its container's zone (`TZ`).
    let tz = TimeZone::system();
    if tz.is_unknown() {
        tracing::warn!(
            reason = "no time zone detected",
            "C# import: reading C# times as UTC; if the C# container had a different TZ, \
             set the same TZ here before importing, or file dates shift and media servers \
             may rescan"
        );
    } else {
        tracing::info!(
            zone = tz.iana_name().unwrap_or("local"),
            "C# import: reading C# times in this time zone"
        );
    }
    let mut done = Imported::default();
    progress(Progress::counting(Stage::Directories));
    let tree = tree(&mut cs, db, &tz, &mut done).await?;
    let total = tree.dirs.len() as u64;
    progress(Progress::at(Stage::Directories, 0, total));
    for chunk in tree.dirs.chunks(BATCH) {
        db.import_items(chunk, &[]).await?;
        done.directories += chunk.len() as u64;
        progress(Progress::at(Stage::Directories, done.directories, total));
    }
    tracing::info!(
        directories = done.directories,
        "C# import: directories written"
    );
    let nzbs = import_nzbs(&mut cs, db, &blobs, None, &mut done, &progress).await?;
    convert_files(tree.files, &blobs, client, &nzbs, db, &mut done, &progress).await?;
    progress(Progress::counting(Stage::Finishing));
    let items: HashSet<Uuid> = db.item_ids().await?.into_iter().map(|i| i.0).collect();
    record_handoffs(&mut cs, db, &items).await?;

    let finished: HashSet<JobId> = db.history_ids(false).await?.into_iter().collect();
    let rows: Vec<HistoryRow> = sqlx::query_as(concat!(
        "SELECT h.Id AS id, ",
        ms!("h.CreatedAt"),
        " AS created, h.FileName AS file_name, h.JobName AS job_name, \
         h.Category AS category, h.DownloadStatus AS status, \
         h.TotalSegmentBytes AS bytes, h.DownloadTimeSeconds AS seconds, \
         h.FailMessage AS fail_message, h.DownloadDirId AS dir, d.Name AS dir_name, \
         h.NzbBlobId AS nzb FROM HistoryItems h LEFT JOIN DavItems d ON d.Id = h.DownloadDirId"
    ))
    .fetch_all(&mut cs)
    .await?;
    let mut history = Vec::new();
    for h in rows {
        let Some(id) = uuid(&h.id).map(JobId) else {
            continue;
        };
        let status = match h.status {
            1 => JobStatus::Completed,
            2 => JobStatus::Failed,
            _ => continue,
        };
        if finished.contains(&id) {
            continue;
        }
        let storage = h
            .dir_name
            .as_deref()
            .map(|dir| join(&join(storage_root, &h.category), dir));
        history.push(HistoryEntry {
            id,
            created_at: from_wall(h.created, &tz),
            file_name: h.file_name,
            job_name: h.job_name,
            category: category(h.category),
            status,
            total_bytes: u64::try_from(h.bytes).unwrap_or_default(),
            download_seconds: i32::try_from(h.seconds).unwrap_or(i32::MAX),
            fail_message: h.fail_message.filter(|m| !m.is_empty()),
            download_dir: h
                .dir
                .as_deref()
                .and_then(uuid)
                .filter(|d| items.contains(d))
                .map(ItemId),
            nzb_blob: h
                .nzb
                .as_deref()
                .and_then(uuid)
                .filter(|n| nzbs.contains(n))
                .map(BlobId),
            storage,
        });
    }
    for chunk in history.chunks(BATCH) {
        db.import_history(chunk).await?;
    }
    done.history = history.len() as u64;
    let stamped = stamp_download_ids(config, db).await?;
    tracing::info!(
        folders = stamped,
        "C# import: download ids found for folders without history"
    );

    let queued: HashSet<JobId> = db.queue().await?.into_iter().map(|q| q.id).collect();
    let rows: Vec<QueueRow> = sqlx::query_as(concat!(
        "SELECT q.Id AS id, ",
        ms!("q.CreatedAt"),
        " AS created, q.FileName AS file_name, q.JobName AS job_name, \
         q.Category AS category, q.Priority AS priority, q.PostProcessing AS post_processing, \
         q.TotalSegmentBytes AS bytes, ",
        ms!("q.PauseUntil"),
        " AS retry_at, c.NzbContents AS nzb \
         FROM QueueItems q JOIN QueueNzbContents c ON c.Id = q.Id ORDER BY q.SortOrder"
    ))
    .fetch_all(&mut cs)
    .await?;
    drop(cs);
    for q in rows {
        let Some(id) = uuid(&q.id).map(JobId) else {
            continue;
        };
        if queued.contains(&id) {
            continue;
        }
        let nzb = blob::encode_nzb(q.nzb.as_bytes())?;
        let job = QueueEntry {
            id,
            created_at: from_wall(q.created, &tz),
            file_name: q.file_name,
            job_name: q.job_name,
            category: category(q.category),
            priority: i16::try_from(q.priority).unwrap_or_default(),
            post_processing: i16::try_from(q.post_processing).unwrap_or_default(),
            nzb_blob: BlobId::new(),
            nzb_size: q.nzb.len() as u64,
            total_bytes: u64::try_from(q.bytes).unwrap_or_default(),
            paused: false,
            retry_at: q.retry_at.map(|t| from_wall(t, &tz)),
            attempts: 0,
            stuck_count: 0,
            last_error: None,
            content_hash: None,
        };
        db.enqueue(&job, &nzb).await?;
        done.queued += 1;
    }
    Ok(done)
}

/// Restores missing NZB downloads for surviving C# items and history entries.
pub async fn restore_nzb_downloads(config: &Path, db: &Db) -> Result<u64, Error> {
    let (items, history) = db.missing_nzb_links().await?;
    let items: HashSet<_> = items.into_iter().map(|id| id.0).collect();
    let history: HashSet<_> = history.into_iter().map(|id| id.0).collect();
    let mut source = open(config).await?;
    let item_rows: Vec<(String, String)> =
        sqlx::query_as("SELECT Id, NzbBlobId FROM DavItems WHERE NzbBlobId IS NOT NULL")
            .fetch_all(&mut source)
            .await?;
    let history_rows: Vec<(String, String)> =
        sqlx::query_as("SELECT Id, NzbBlobId FROM HistoryItems WHERE NzbBlobId IS NOT NULL")
            .fetch_all(&mut source)
            .await?;
    let links = |rows: Vec<(String, String)>, missing: &HashSet<Uuid>| {
        rows.into_iter()
            .filter_map(|(id, blob)| {
                let id = uuid(&id)?;
                missing.contains(&id).then_some((id, uuid(&blob)?))
            })
            .collect::<Vec<_>>()
    };
    let items = links(item_rows, &items);
    let history = links(history_rows, &history);
    let wanted = items
        .iter()
        .chain(&history)
        .map(|(_, blob)| *blob)
        .collect();
    let available = import_nzbs(
        &mut source,
        db,
        &config.join("blobs"),
        Some(&wanted),
        &mut Imported::default(),
        &|_| {},
    )
    .await?;
    let items: Vec<_> = items
        .into_iter()
        .filter(|(_, blob)| available.contains(blob))
        .map(|(id, blob)| (ItemId(id), BlobId(blob)))
        .collect();
    let history: Vec<_> = history
        .into_iter()
        .filter(|(_, blob)| available.contains(blob))
        .map(|(id, blob)| (JobId(id), BlobId(blob)))
        .collect();
    Ok(db.restore_nzb_links(&items, &history).await?)
}

/// Writes the C# files in `config` that `db` lacks and `keep` accepts by id and path,
/// with the folders above them, as [`apply`] would have. A file whose path or folder
/// path another item holds is skipped. Meant for files an earlier import skipped.
// ponytail: remove before beta 1 with the upgrade task that calls it.
pub async fn retry_files(
    config: &Path,
    db: &Db,
    client: &Client,
    keep: impl Fn(Uuid, &str) -> bool,
) -> Result<Imported, Error> {
    let mut cs = open(config).await?;
    let blobs = config.join("blobs");
    let mut done = Imported::default();
    // Unreadable ids were reported by the first import.
    let tree = tree(&mut cs, db, &TimeZone::system(), &mut Imported::default()).await?;
    let absent: HashMap<Uuid, &Item> = tree.dirs.iter().map(|(d, _)| (d.id.0, d)).collect();
    let mut dirs: HashSet<Uuid> = HashSet::new();
    let mut files = Vec::new();
    for row in tree.files {
        let Some(id) = uuid(&row.id).filter(|&id| keep(id, &row.path)) else {
            continue;
        };
        let mut above = Vec::new();
        let mut at = uuid(&row.parent).and_then(|p| absent.get(&p));
        while let Some(dir) = at {
            above.push(*dir);
            at = dir.parent.and_then(|p| absent.get(&p.0));
        }
        let mut in_use = false;
        for (held_by, path) in above
            .iter()
            .map(|d| (d.id.0, d.path.as_str()))
            .chain([(id, row.path.as_str())])
        {
            in_use |= db
                .item_by_path(path)
                .await?
                .is_some_and(|i| i.id.0 != held_by);
        }
        if in_use {
            done.skip(row.path, "its path is in use".to_owned());
            continue;
        }
        dirs.extend(above.iter().map(|d| d.id.0));
        files.push(row);
    }
    let dirs: Vec<(Item, HealthStatus)> = tree
        .dirs
        .into_iter()
        .filter(|(d, _)| dirs.contains(&d.id.0))
        .collect();
    for chunk in dirs.chunks(BATCH) {
        db.import_items(chunk, &[]).await?;
        done.directories += chunk.len() as u64;
    }
    let wanted: HashSet<Uuid> = files
        .iter()
        .filter_map(|f| f.nzb.as_deref().and_then(uuid))
        .collect();
    let nzbs = import_nzbs(&mut cs, db, &blobs, Some(&wanted), &mut done, &|_| {}).await?;
    let ids: HashSet<Uuid> = files.iter().filter_map(|f| uuid(&f.id)).collect();
    convert_files(files, &blobs, client, &nzbs, db, &mut done, &|_| {}).await?;
    let written: HashSet<Uuid> = db
        .item_ids()
        .await?
        .into_iter()
        .map(|i| i.0)
        .filter(|i| ids.contains(i))
        .collect();
    record_handoffs(&mut cs, db, &written).await?;
    drop(cs);
    stamp_download_ids(config, db).await?;
    Ok(done)
}

/// C#'s `/content` tree as ours: what `db` lacks, with the paths and parents the import
/// gives it.
struct Tree {
    /// Shallowest first.
    dirs: Vec<(Item, HealthStatus)>,
    /// By folder.
    files: Vec<FileRow>,
}

async fn tree(
    cs: &mut sqlx::SqliteConnection,
    db: &Db,
    tz: &TimeZone,
    done: &mut Imported,
) -> Result<Tree, Error> {
    let mut items: HashSet<Uuid> = db.item_ids().await?.into_iter().map(|i| i.0).collect();
    let rows: Vec<DirRow> = sqlx::query_as(concat!(
        "SELECT Id, ParentId, Name, Path, ",
        ms!("CreatedAt"),
        " FROM DavItems WHERE SubType = 101 AND (Path LIKE '/content/%' \
         OR (Path = '/content' AND Name = '')) \
         ORDER BY length(Path) - length(replace(Path, '/', ''))"
    ))
    .fetch_all(&mut *cs)
    .await?;
    let uncategorized = format!("/content/{UNCATEGORIZED}");
    let mut existing = match rows
        .iter()
        .find(|d| uuid(&d.1) == Some(CS_CONTENT) && d.2 == UNCATEGORIZED)
    {
        Some(d) => uuid(&d.0),
        None => db.item_by_path(&uncategorized).await?.map(|i| i.id.0),
    };
    // C#'s empty category folder: its id to the uncategorized folder it merges into,
    // and the new paths of the folders below it.
    let mut merged: HashMap<Uuid, Uuid> = HashMap::new();
    let mut moved: HashMap<Uuid, String> = HashMap::new();
    let mut dirs = Vec::new();
    for (id, parent_id, mut name, mut path, created_at) in rows {
        let (Some(id), Some(parent_id)) = (uuid(&id), uuid(&parent_id)) else {
            done.skip(path, "unreadable id".to_owned());
            continue;
        };
        if parent_id == CS_CONTENT && name.trim().is_empty() {
            moved.insert(id, uncategorized.clone());
            if let Some(target) = existing {
                merged.insert(id, target);
                continue;
            }
            existing = Some(id);
            UNCATEGORIZED.clone_into(&mut name);
            path.clone_from(&uncategorized);
        } else if let Some(p) = moved.get(&parent_id) {
            path = join(p, &name);
            moved.insert(id, path.clone());
        }
        if !items.insert(id) {
            continue;
        }
        dirs.push((
            Item {
                id: ItemId(id),
                parent: Some(parent(merged.get(&parent_id).copied().unwrap_or(parent_id))),
                name,
                path,
                kind: ItemKind::Directory,
                size: None,
                meta_blob: None,
                nzb_blob: None,
                created_at: from_wall(created_at, tz),
                release_date: None,
                health_state: HealthState::Unchecked,
                encrypted: false,
            },
            HealthStatus::default(),
        ));
    }

    let rows: Vec<FileRow> = sqlx::query_as(concat!(
        "SELECT Id AS id, ParentId AS parent, Name AS name, Path AS path, \
         SubType AS sub_type, FileSize AS size, FileBlobId AS meta, NzbBlobId AS nzb, ",
        ms!("CreatedAt"),
        " AS created, ReleaseDate AS release_date, LastHealthCheck AS last_check, \
         NextHealthCheck AS next_check, HealthRepairPending <> 0 AS repair_pending \
         FROM DavItems WHERE Type = 2 AND Path LIKE '/content/%' ORDER BY ParentId"
    ))
    .fetch_all(&mut *cs)
    .await?;
    let mut files = Vec::new();
    for mut row in rows {
        let Some(id) = uuid(&row.id) else {
            done.skip(row.path, "unreadable id".to_owned());
            continue;
        };
        if items.contains(&id) {
            continue;
        }
        row.created = from_wall(row.created, tz);
        if let Some(p) = uuid(&row.parent) {
            if let Some(dir) = moved.get(&p) {
                row.path = join(dir, &row.name);
            }
            if let Some(target) = merged.get(&p) {
                row.parent = target.to_string();
            }
        }
        files.push(row);
    }
    Ok(Tree { dirs, files })
}

/// Converts `files` a folder at a time and writes them.
async fn convert_files(
    files: Vec<FileRow>,
    blobs: &Path,
    client: &Client,
    nzbs: &HashSet<Uuid>,
    db: &Db,
    done: &mut Imported,
    progress: &(dyn Fn(Progress) + Sync),
) -> Result<(), Error> {
    let total = files.len() as u64;
    let skipped_before = done.skipped_count;
    let report = |done: &Imported| {
        progress(Progress {
            stage: Stage::Files,
            done: done.files,
            total: Some(total),
            skipped: done.skipped_count - skipped_before,
        });
    };
    report(done);
    let mut groups: Vec<Vec<FileRow>> = Vec::new();
    for row in files {
        match groups.last_mut() {
            Some(g) if g[0].parent == row.parent => g.push(row),
            _ => groups.push(vec![row]),
        }
    }
    let mut converted = stream::iter(groups)
        .map(|group| convert_dir(group, blobs, client, nzbs))
        .buffer_unordered(PARALLEL);
    let mut pending: Vec<(Item, HealthStatus)> = Vec::new();
    let mut pending_blobs: Vec<(BlobId, Vec<u8>)> = Vec::new();
    while let Some(dir) = converted.next().await {
        done.fetched += dir.fetched;
        done.provider_errors += dir.provider_errors;
        for (path, reason) in dir.skipped {
            done.skip(path, reason);
        }
        for (item, health, meta) in dir.files {
            if let Some(b) = item.meta_blob {
                pending_blobs.push((b, meta));
            }
            pending.push((item, health));
        }
        if pending.len() >= BATCH {
            write_files(db, &mut pending, &mut pending_blobs, done).await?;
            log_files(done, total);
        }
        report(done);
    }
    write_files(db, &mut pending, &mut pending_blobs, done).await?;
    log_files(done, total);
    report(done);
    Ok(())
}

fn log_files(done: &Imported, total: u64) {
    let percent = if total == 0 {
        1000
    } else {
        1000 * u128::from(done.files) / u128::from(total)
    };
    tracing::info!(
        files = done.files,
        total,
        percent = format_args!("{}.{}", percent / 10, percent % 10),
        skipped = done.skipped_count,
        "C# import: files written"
    );
}

/// Records the STRM files and symlinks C# made for `items`, so cleanup removes them
/// with their item.
async fn record_handoffs(
    cs: &mut sqlx::SqliteConnection,
    db: &Db,
    items: &HashSet<Uuid>,
) -> Result<(), Error> {
    let generated: Vec<(String, Option<String>, Option<String>)> = sqlx::query_as(
        "SELECT Id, GeneratedStrmPath, GeneratedSymlinkPath FROM DavItems \
         WHERE GeneratedStrmPath IS NOT NULL OR GeneratedSymlinkPath IS NOT NULL",
    )
    .fetch_all(&mut *cs)
    .await?;
    // ponytail: C# handoffs carry no job, so cleanup removes them only once their item is
    // gone; a job no Arr imported keeps its C# STRMs until then.
    let handoffs: Vec<Handoff> = generated
        .into_iter()
        .filter_map(|(id, strm, link)| {
            let id = uuid(&id).filter(|id| items.contains(id))?;
            Some([strm, link].into_iter().flatten().map(move |path| Handoff {
                path,
                item: ItemId(id),
                job: None,
                size: None,
            }))
        })
        .flatten()
        .collect();
    for chunk in handoffs.chunks(BATCH) {
        db.record_handoffs(chunk).await?;
    }
    Ok(())
}

/// Stores every NZB blob items or history point at, or those in `only`; returns those
/// stored.
async fn import_nzbs(
    cs: &mut sqlx::SqliteConnection,
    db: &Db,
    blobs: &Path,
    only: Option<&HashSet<Uuid>>,
    done: &mut Imported,
    progress: &(dyn Fn(Progress) + Sync),
) -> Result<HashSet<Uuid>, Error> {
    progress(Progress::counting(Stage::Nzbs));
    let stored: HashSet<Uuid> = db.blob_ids().await?.into_iter().map(|b| b.0).collect();
    let ids: Vec<String> = sqlx::query_scalar(
        "SELECT NzbBlobId FROM DavItems WHERE NzbBlobId IS NOT NULL \
         UNION SELECT NzbBlobId FROM HistoryItems WHERE NzbBlobId IS NOT NULL",
    )
    .fetch_all(&mut *cs)
    .await?;
    let wanted: HashSet<Uuid> = ids
        .iter()
        .filter_map(|i| uuid(i))
        .filter(|i| only.is_none_or(|o| o.contains(i)))
        .collect();
    let mut available: HashSet<Uuid> = wanted.intersection(&stored).copied().collect();
    let todo: Vec<Uuid> = wanted.difference(&stored).copied().collect();
    let total = todo.len() as u64;
    let mut handled = 0;
    progress(Progress::at(Stage::Nzbs, handled, total));
    let mut encoded = stream::iter(todo)
        .map(|id| {
            let blobs = blobs.to_owned();
            tokio::task::spawn_blocking(move || {
                match read_blob(&blobs, &id.to_string(), MAX_NZB_BLOB_BYTES) {
                    Ok(Some(xml)) => blob::encode_nzb(&xml).map(|b| (id, Some(b))),
                    Ok(None) => Ok((id, None)),
                    Err(error) => {
                        tracing::warn!(
                            blob = %id,
                            reason = %error,
                            "C# import: skipped an NZB blob it could not read safely"
                        );
                        Ok((id, None))
                    }
                }
            })
        })
        .buffer_unordered(PARALLEL);
    let mut batch: Vec<(BlobId, Vec<u8>)> = Vec::new();
    let mut bytes = 0;
    while let Some(result) = encoded.next().await {
        let (id, nzb) = result??;
        handled += 1;
        if let Some(nzb) = nzb {
            bytes += nzb.len();
            batch.push((BlobId(id), nzb));
            if bytes >= NZB_BATCH {
                write_blobs(db, &mut batch, &mut available, done).await?;
                bytes = 0;
            }
        }
        progress(Progress::at(
            Stage::Nzbs,
            handled - batch.len() as u64,
            total,
        ));
    }
    write_blobs(db, &mut batch, &mut available, done).await?;
    progress(Progress::at(Stage::Nzbs, handled, total));
    tracing::info!(nzbs = available.len(), "C# import: NZBs written");
    Ok(available)
}

async fn write_blobs(
    db: &Db,
    batch: &mut Vec<(BlobId, Vec<u8>)>,
    available: &mut HashSet<Uuid>,
    done: &mut Imported,
) -> Result<(), Error> {
    let refs: Vec<(BlobId, &[u8])> = batch.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.insert(&[], &refs).await?;
    available.extend(batch.iter().map(|(id, _)| id.0));
    done.nzbs += batch.len() as u64;
    batch.clear();
    Ok(())
}

async fn write_files(
    db: &Db,
    items: &mut Vec<(Item, HealthStatus)>,
    blobs: &mut Vec<(BlobId, Vec<u8>)>,
    done: &mut Imported,
) -> Result<(), Error> {
    let refs: Vec<(BlobId, &[u8])> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
    db.import_items(items, &refs).await?;
    done.files += items.len() as u64;
    items.clear();
    blobs.clear();
    Ok(())
}

/// One directory's files, converted.
#[derive(Default)]
struct Dir {
    files: Vec<(Item, HealthStatus, Vec<u8>)>,
    skipped: Vec<(String, String)>,
    fetched: u64,
    provider_errors: u64,
}

async fn convert_dir(
    rows: Vec<FileRow>,
    blobs: &Path,
    client: &Client,
    nzbs: &HashSet<Uuid>,
) -> Dir {
    let mut dir = Dir::default();
    let mut sizer = Sizer {
        client,
        part: None,
        volumes: Vec::new(),
        fetched: 0,
        provider_errors: 0,
    };
    for row in rows {
        match convert(&row, blobs, &mut sizer).await {
            Ok((meta, damaged)) => match blob::encode_meta(&meta) {
                Ok(bytes) => dir.files.push(file_item(row, &meta, damaged, nzbs, bytes)),
                Err(error) => dir.skipped.push((row.path, error.to_string())),
            },
            Err(reason) => dir.skipped.push((row.path, reason)),
        }
    }
    dir.fetched = sizer.fetched;
    dir.provider_errors = sizer.provider_errors;
    dir
}

fn file_item(
    row: FileRow,
    meta: &FileMeta,
    damaged: bool,
    nzbs: &HashSet<Uuid>,
    bytes: Vec<u8>,
) -> (Item, HealthStatus, Vec<u8>) {
    let seconds = |s: Option<i64>| s.map(|s| s.saturating_mul(1000));
    let last_check = seconds(row.last_check);
    let state = if last_check.is_some() && !damaged && !row.repair_pending {
        HealthState::Healthy
    } else {
        HealthState::Unchecked
    };
    let item = Item {
        id: ItemId(uuid(&row.id).unwrap_or_default()),
        parent: uuid(&row.parent).map(parent),
        name: row.name,
        path: row.path,
        kind: match meta {
            FileMeta::Nzb(_) => ItemKind::NzbFile,
            FileMeta::Multipart(_) => ItemKind::Multipart,
        },
        size: Some(meta.size()),
        meta_blob: Some(BlobId::new()),
        nzb_blob: row
            .nzb
            .as_deref()
            .and_then(uuid)
            .filter(|n| nzbs.contains(n))
            .map(BlobId),
        created_at: row.created,
        release_date: seconds(row.release_date),
        health_state: state,
        encrypted: meta.encrypted(),
    };
    let health = HealthStatus {
        state,
        last_check,
        // C# kept routine schedules for files with recorded misses or a pending repair, and
        // its checks never saw the articles its list lacked; we check those first.
        next_check: if state == HealthState::Unchecked {
            Some(now_ms())
        } else {
            seconds(row.next_check).map(|next| cap_next_check(next, item.id, now_ms()))
        },
        ..HealthStatus::default()
    };
    (item, health, bytes)
}

/// `ms`, a wall-clock time read as if it were UTC, as the real instant in `tz`.
fn from_wall(ms: i64, tz: &TimeZone) -> i64 {
    let Ok(utc) = Timestamp::from_millisecond(ms) else {
        return ms;
    };
    tz.to_ambiguous_timestamp(TimeZone::UTC.to_datetime(utc))
        .compatible()
        .map_or(ms, Timestamp::as_millisecond)
}

/// C# scheduled some checks years out; ours wait at most [`MAX_CHECK_INTERVAL_MS`]. Those
/// later than that are spread over the window by id, so they do not all come due at once.
fn cap_next_check(next: i64, id: ItemId, now: i64) -> i64 {
    if next <= now + MAX_CHECK_INTERVAL_MS {
        return next;
    }
    let spread = id.0.as_u128() % u128::from(MAX_CHECK_INTERVAL_MS.unsigned_abs());
    now + i64::try_from(spread).unwrap_or_default()
}

/// The C# file as our metadata, and whether segments were recorded missing or corrupt.
async fn convert(
    row: &FileRow,
    blobs: &Path,
    sizer: &mut Sizer<'_>,
) -> Result<(FileMeta, bool), String> {
    let id = row.meta.clone().ok_or("no metadata blob")?;
    let blobs = blobs.to_owned();
    let sub_type = row.sub_type;
    let file = tokio::task::spawn_blocking(move || {
        let input = open_blob(&blobs, &id)
            .map_err(|e| format!("metadata blob: {e}"))?
            .ok_or("metadata blob: not found")?;
        let file = match sub_type {
            201 => decode(input, csharp::nzb_file).map(CsFile::Nzb),
            202 => decode(input, csharp::rar_file).map(CsFile::Rar),
            203 => decode(input, csharp::multipart_file).map(CsFile::Multipart),
            other => return Err(format!("unknown file type {other}")),
        };
        file.map_err(|e| format!("metadata blob: {e}"))
    })
    .await
    .map_err(|e| e.to_string())??;
    let size = match row.size {
        // C#'s marker for a size it did not know.
        Some(i64::MAX) => {
            tracing::debug!(path = row.path, "C# size unknown; using the computed size");
            None
        }
        size => size.and_then(|s| u64::try_from(s).ok()),
    };
    let (meta, damaged) = match file {
        CsFile::Nzb(f) => {
            let len = size
                .or_else(|| {
                    f.ranges
                        .as_deref()?
                        .last()
                        .and_then(|r| u64::try_from(r.end).ok())
                })
                .ok_or("no file size")?;
            let sized = sizer
                .run(
                    &f.segment_ids,
                    f.ranges.as_deref(),
                    (0, len),
                    f.ranges_trusted,
                    None,
                )
                .await?;
            let damaged = f.missing.as_ref().is_some_and(|m| !m.is_empty())
                || f.corrupt.as_ref().is_some_and(|c| !c.is_empty());
            let segments = segments(
                &f.segment_ids,
                &sized,
                f.fallbacks.as_deref(),
                (f.missing.as_deref(), f.corrupt.as_deref()),
            );
            let proof = f.proof.as_ref().and_then(proof);
            (FileMeta::Nzb(NzbFile { segments, proof }), damaged)
        }
        CsFile::Rar(f) => {
            let mut parts = Vec::new();
            let last = f.parts.len().saturating_sub(1);
            for (i, p) in f.parts.iter().enumerate() {
                let len = u64::try_from(p.part_size).map_err(|_| "negative volume size")?;
                let start = u64::try_from(p.offset).map_err(|_| "negative member offset")?;
                let count = u64::try_from(p.byte_count).map_err(|_| "negative member size")?;
                let sized = sizer
                    .run(
                        &p.segment_ids,
                        p.ranges.as_deref(),
                        (0, len),
                        p.ranges_trusted,
                        Some(start + count),
                    )
                    .await?;
                parts.push(volume(FilePart {
                    segments: segments(
                        &p.segment_ids,
                        &sized,
                        p.fallbacks.as_deref(),
                        (None, None),
                    ),
                    offset: 0,
                    data: ByteRange {
                        start,
                        end: start + count,
                    },
                    split_after: i < last,
                    proof: None,
                }));
            }
            (
                FileMeta::Multipart(MultipartFile { parts, crypt: None }),
                false,
            )
        }
        CsFile::Multipart(f) => {
            let crypt = f.aes.as_ref().map(crypt).transpose()?;
            let mut parts = Vec::new();
            for p in &f.parts {
                let start = u64::try_from(p.segment_range.start).map_err(|_| "negative offset")?;
                let len = u64::try_from(p.segment_range.len()).map_err(|_| "negative length")?;
                let data = range(p.data_range).ok_or("negative member range")?;
                // RAR volumes carry headers before the member's bytes; split files do not.
                let sized = sizer
                    .run(
                        &p.segment_ids,
                        p.ranges.as_deref(),
                        (start, len),
                        p.ranges_trusted,
                        (data.start > start).then(|| data.end.saturating_sub(start)),
                    )
                    .await?;
                parts.push(volume(FilePart {
                    segments: segments(
                        &p.segment_ids,
                        &sized,
                        p.fallbacks.as_deref(),
                        (None, None),
                    ),
                    offset: start,
                    data,
                    split_after: p.split_after.unwrap_or(false),
                    proof: p.proof.as_ref().and_then(proof),
                }));
            }
            for p in &f.pending {
                parts.push(
                    sizer
                        .resolve(
                            p,
                            f.path_in_archive.as_deref(),
                            f.password.as_deref(),
                            crypt.is_some(),
                        )
                        .await?,
                );
            }
            (FileMeta::Multipart(MultipartFile { parts, crypt }), false)
        }
    };
    if let FileMeta::Multipart(m) = &meta
        && m.parts
            .iter()
            .any(|p| p.data.start < p.offset || p.data.end > p.offset + p.segments.size())
    {
        return Err("a part's data lies outside its segments".to_owned());
    }
    if let Some(size) = size
        && meta.size() != size
    {
        return Err(format!("size {} differs from C#'s {size}", meta.size()));
    }
    let damaged = damaged || meta.has_placeholders();
    Ok((meta, damaged))
}

enum CsFile {
    Nzb(csharp::NzbFile),
    Rar(csharp::RarFile),
    Multipart(csharp::MultipartFile),
}

/// C#'s AES parameters: the key and IV it derived, and the plaintext size.
fn crypt(aes: &csharp::AesParams) -> Result<Crypt, String> {
    let key = match aes.key.len() {
        16 => Key::Aes128(aes.key.as_slice().try_into().expect("length checked")),
        32 => Key::Aes256(aes.key.as_slice().try_into().expect("length checked")),
        n => return Err(format!("AES key of {n} bytes")),
    };
    let iv = aes
        .iv
        .as_slice()
        .try_into()
        .map_err(|_| format!("AES IV of {} bytes", aes.iv.len()))?;
    let size = u64::try_from(aes.decoded_size)
        .ok()
        .filter(|&s| s > 0)
        .ok_or("encrypted with no decoded size")?;
    Ok(Crypt { key, iv, size })
}

/// Learns segment sizes C# never recorded. Posters split a file into equal articles
/// apart from the last, and use one article size per release, so one fetched article
/// usually sizes a whole directory.
struct Sizer<'a> {
    client: &'a Client,
    /// The directory's article size, once known.
    part: Option<u64>,
    /// Posted sizes of the directory's archive volumes measured so far.
    volumes: Vec<u64>,
    fetched: u64,
    provider_errors: u64,
}

/// A run's segment sizes, and its ids when they differ from C#'s list.
struct Sized {
    sizes: Vec<u32>,
    /// Sizes are validated.
    trusted: bool,
    /// C#'s ids with an empty placeholder for each article its list lacks.
    filled: Option<Vec<String>>,
}

/// The first readable article of a run, and the article size when it sits where the
/// run says.
struct Probe {
    index: usize,
    body: Body,
    part: Option<u64>,
}

impl Sizer<'_> {
    /// An article, counted in `fetched` and, when the provider failed, `provider_errors`.
    async fn body(&mut self, id: &str) -> Result<Body, infinidysk_nntp::Error> {
        use infinidysk_nntp::Error as E;
        self.fetched += 1;
        let body = self.client.body(id, Workload::Queue, None).await;
        if let Err(error) = &body
            && !matches!(
                error,
                E::NotFound | E::Yenc(_) | E::CrcMismatch { .. } | E::TooLarge
            )
        {
            self.provider_errors += 1;
        }
        body
    }
    /// Sizes of a run of segments covering `len` bytes from `start` of the posted file:
    /// recorded `ranges` when they fit, else derived. `member`: the run is an archive
    /// volume whose member ends this many bytes in.
    async fn run(
        &mut self,
        ids: &[String],
        ranges: Option<&[LongRange]>,
        (start, len): (u64, u64),
        trusted: Option<bool>,
        member: Option<u64>,
    ) -> Result<Sized, String> {
        if let Some(sizes) = ranges.and_then(|r| recorded(r, ids.len(), start, len)) {
            return Ok(Sized {
                sizes,
                trusted: trusted == Some(true),
                filled: None,
            });
        }
        if start != 0 {
            return Err("segment sizes unknown for a run that starts mid-file".to_owned());
        }
        let (sizes, filled) = self.sizes(ids, len, member).await?;
        Ok(Sized {
            sizes,
            trusted: false,
            filled,
        })
    }

    /// Sizes derived from the article size and the posted size. C# recorded an archive
    /// volume's length up to the member's last byte, short of the headers that end the
    /// volume, or estimated it from the NZB, past them. Matching directory geometry is
    /// reused; other volumes are measured themselves and must cover the member's bytes.
    async fn sizes(
        &mut self,
        ids: &[String],
        len: u64,
        member: Option<u64>,
    ) -> Result<(Vec<u32>, Option<Vec<String>>), String> {
        let n = ids.len();
        if n == 0 {
            return Err("no segments".to_owned());
        }
        let volume = member.is_some();
        let need = member.unwrap_or(len);
        let known = if volume {
            self.volumes
                .iter()
                .copied()
                .find(|&size| len <= size && size - len <= MAX_TRAILER && need <= size)
        } else {
            Some(len)
        };
        // ponytail: matching geometry can hide omitted ids in larger volumes or
        // smaller articles. Measure every run if migration policy requires proof.
        if let Some(sizes) = known.and_then(|total| uniform(n, total, self.part.unwrap_or(total))) {
            return Ok((sizes, None));
        }
        // The last article of a volume is usually small and confirms where it ends.
        let order = if volume { [n - 1, 0, 1] } else { [0, 1, 2] };
        let probe = self.measure(ids, order).await?;
        let file_size = probe.body.file_size;
        let reach = probe.body.offset + probe.body.data.len() as u64;
        let tail = probe.index == n - 1;
        // Some posters write a wrong `=ybegin size=` or `total=` but right offsets.
        if reach > file_size
            || (volume && need > file_size)
            || (volume && tail && reach >= need && reach < file_size)
            || (tail && n > 1 && probe.part.is_none() && reach == file_size)
            || (volume
                && !tail
                && probe
                    .part
                    .is_none_or(|part| uniform(n, file_size, part).is_none()))
        {
            return self.measured(ids, need, volume, probe).await;
        }
        if volume
            && probe.index == n - 1
            && reach == file_size
            && !self.volumes.contains(&file_size)
        {
            self.volumes.push(file_size);
        }
        if let Some(part) = probe.part {
            if n > 1 {
                self.part = Some(part);
            }
            // Some C# runs record the length from the NZB; the poster's yEnc size wins.
            if let Some(sizes) = uniform(n, file_size, part)
                .or_else(|| if volume { None } else { uniform(n, len, part) })
            {
                return Ok((sizes, None));
            }
            if file_size.div_ceil(part) <= n as u64 {
                tracing::debug!(part, len, file_size, segments = n, "uneven split");
                return Err("segments are not split evenly".to_owned());
            }
        }
        let hint = probe.part.or(if volume { None } else { self.part });
        let (part, filled) = self
            .fill(ids, vec![(probe.index, probe.body)], file_size, hint, true)
            .await?;
        self.part = Some(part);
        let sizes = uniform(filled.len(), file_size, part)
            .ok_or_else(|| "segments are not split evenly".to_owned())?;
        Ok((sizes, (filled.len() > n).then_some(filled)))
    }

    /// Sizes of a run whose headers its articles disprove or do not confirm: the first
    /// readable article before the last gives the article size and the last readable
    /// one the coverage, which must be `need` (a volume: at least). A volume may omit
    /// unavailable trailing articles beyond the member. Header claims are not read.
    async fn measured(
        &mut self,
        ids: &[String],
        need: u64,
        volume: bool,
        probe: Probe,
    ) -> Result<(Vec<u32>, Option<Vec<String>>), String> {
        let elsewhere = || "an article belongs elsewhere in its file".to_owned();
        let unknown = |e: infinidysk_nntp::Error| format!("segment sizes unknown: {e}");
        let mut last = ids.len() - 1;
        let (at, head, tail) = if probe.index < last {
            let tail = if volume {
                loop {
                    last -= 1;
                    if last == probe.index {
                        break probe.body.clone();
                    }
                    if let Ok(body) = self.body(&ids[last]).await {
                        break body;
                    }
                }
            } else {
                self.body(&ids[last]).await.map_err(unknown)?
            };
            (probe.index, probe.body, tail)
        } else if last == 0 {
            (0, probe.body.clone(), probe.body)
        } else {
            let mut found = Err(String::new());
            for i in [0, 1].into_iter().filter(|&i| i < last) {
                match self.body(&ids[i]).await {
                    Ok(b) => {
                        found = Ok((i, b));
                        break;
                    }
                    Err(e) => found = Err(unknown(e)),
                }
            }
            let (at, head) = found?;
            (at, head, probe.body)
        };
        let truncated = last + 1 < ids.len();
        let ids = &ids[..=last];
        let n = ids.len();
        let part = head.data.len() as u64;
        let tail_len = tail.data.len() as u64;
        let end = tail.offset + tail_len;
        let on_grid = |b: &Body| part > 0 && b.offset.is_multiple_of(part);
        let fits = on_grid(&head)
            && on_grid(&tail)
            && (head.offset < tail.offset
                || (n == 1 && head.offset == 0)
                || (truncated && at == last))
            && tail_len > 0
            && tail_len <= part
            && if volume { end >= need } else { end == need };
        if !fits {
            tracing::debug!(
                part,
                need,
                end,
                head = head.offset,
                tail = tail.offset,
                segments = n,
                "articles do not fit the run"
            );
            return Err(elsewhere());
        }
        let count = tail.offset / part + 1;
        let (sizes, filled) = if head.offset == at as u64 * part && count == n as u64 {
            (uniform(n, end, part).ok_or_else(elsewhere)?, None)
        } else {
            let (part, filled) = self
                .fill(ids, vec![(at, head), (last, tail)], end, Some(part), false)
                .await?;
            let sizes = uniform(filled.len(), end, part).ok_or_else(elsewhere)?;
            (sizes, Some(filled))
        };
        tracing::debug!(part, end, segments = n, "sized from measured articles");
        if n > 1 {
            self.part = Some(part);
        }
        if volume && !truncated && !self.volumes.contains(&end) {
            self.volumes.push(end);
        }
        Ok((sizes, filled.or_else(|| truncated.then(|| ids.to_vec()))))
    }

    /// The first article in `order` a provider still has.
    async fn measure(&mut self, ids: &[String], order: [usize; 3]) -> Result<Probe, String> {
        let last = ids.len() - 1;
        let mut reason = String::new();
        for (k, i) in order.into_iter().enumerate() {
            let Some(id) = ids.get(i).filter(|_| !order[..k].contains(&i)) else {
                continue;
            };
            let b = match self.body(id).await {
                Ok(b) => b,
                Err(error) => {
                    reason = error.to_string();
                    continue;
                }
            };
            let len = b.data.len() as u64;
            let part = if i < last || last == 0 {
                Some(len)
            } else {
                // Articles C# never listed would skew the division; the posted part
                // count must agree.
                let last = last as u64;
                (b.offset % last == 0)
                    .then(|| b.offset / last)
                    .filter(|_| b.total.is_some_and(|t| u64::from(t) == last + 1))
            };
            let part = part.filter(|&part| {
                len > 0
                    && len <= part
                    && b.offset == i as u64 * part
                    && (i < last || b.offset + len == b.file_size)
            });
            if part.is_none() {
                tracing::debug!(
                    index = i,
                    len,
                    offset = b.offset,
                    file_size = b.file_size,
                    segments = ids.len(),
                    "article is not where its run says"
                );
            }
            return Ok(Probe {
                index: i,
                body: b,
                part,
            });
        }
        Err(format!("segment sizes unknown: {reason}"))
    }

    /// The article size and C#'s ids with a placeholder for each article of the
    /// `file_size`-byte file its list lacks, given articles fetched by list index. Where
    /// they go is bisected: an article's number in the file less its index in the list
    /// counts the gaps before it. `claims`: articles' yEnc size must match; the part
    /// count is measured, never taken from `total=`.
    async fn fill(
        &mut self,
        ids: &[String],
        mut probes: Vec<(usize, Body)>,
        file_size: u64,
        hint: Option<u64>,
        claims: bool,
    ) -> Result<(u64, Vec<String>), String> {
        let elsewhere = || "an article belongs elsewhere in its file".to_owned();
        let n = ids.len();
        let ends = |b: &Body| b.offset + b.data.len() as u64 >= file_size;
        let part = if let Some((_, b)) = probes.iter().find(|(_, b)| !ends(b)) {
            b.data.len() as u64
        } else if let Some(part) = hint {
            part
        } else {
            // The file's last article does not tell the article size; an earlier one does.
            let mut found = None;
            let tried: Vec<usize> = probes.iter().map(|&(i, _)| i).collect();
            for i in [0, 1].into_iter().filter(|&i| i < n && !tried.contains(&i)) {
                if let Ok(b) = self.body(&ids[i]).await
                    && !ends(&b)
                {
                    found = Some(b.data.len() as u64);
                    probes.push((i, b));
                    break;
                }
            }
            found.ok_or_else(elsewhere)?
        };
        if part == 0 {
            return Err(elsewhere());
        }
        let total = usize::try_from(file_size.div_ceil(part)).map_err(|_| elsewhere())?;
        let gaps = total.checked_sub(n).ok_or_else(elsewhere)?;
        // Gaps before list index `i`, when the article sits on the file's grid.
        let shift = |i: usize, b: &Body| {
            let len = b.data.len() as u64;
            let fits = (!claims || b.file_size == file_size)
                && b.offset.is_multiple_of(part)
                && len > 0
                && (len == part || b.offset + len == file_size);
            if !fits {
                return None;
            }
            usize::try_from(b.offset / part)
                .ok()?
                .checked_sub(i)
                .filter(|&s| s <= gaps)
        };
        // Keyed by list index + 1, with the virtual ends before and after the list.
        let mut known = BTreeMap::from([(0, 0), (n + 1, gaps)]);
        for (i, b) in &probes {
            known.insert(i + 1, shift(*i, b).ok_or_else(elsewhere)?);
        }
        loop {
            let mut next = None;
            for ((&a, &sa), (&b, &sb)) in known.iter().zip(known.iter().skip(1)) {
                if sa > sb {
                    return Err("articles are out of order".to_owned());
                }
                if sa < sb && b - a > 1 {
                    next = Some(usize::midpoint(a, b)..b);
                    break;
                }
            }
            let Some(candidates) = next else {
                break;
            };
            // ponytail: a missing article mid-bisect falls back to its next two
            // neighbours, then fails the file; widen the search if that turns up.
            let mut reason = String::new();
            let mut placed = None;
            for key in candidates.take(3) {
                let i = key - 1;
                match self.body(&ids[i]).await {
                    Ok(b) => {
                        placed = Some((key, shift(i, &b).ok_or_else(elsewhere)?));
                        break;
                    }
                    Err(error) => reason = error.to_string(),
                }
            }
            let (key, s) = placed.ok_or_else(|| format!("segment sizes unknown: {reason}"))?;
            known.insert(key, s);
        }
        let mut filled = Vec::with_capacity(total);
        let mut before = 0;
        for (i, id) in ids.iter().enumerate() {
            let (_, &s) = known
                .range(i + 1..)
                .next()
                .expect("the key after the list is always present");
            filled.extend(std::iter::repeat_n(String::new(), s - before));
            filled.push(id.clone());
            before = s;
        }
        filled.extend(std::iter::repeat_n(String::new(), gaps - before));
        tracing::debug!(
            part,
            file_size,
            segments = n,
            gaps,
            "filled articles C# never listed"
        );
        Ok((part, filled))
    }

    /// A RAR volume C# had not read yet: where the member continues in it. Encrypted
    /// headers open with `password`; `encrypted`: the member's data is.
    async fn resolve(
        &mut self,
        p: &csharp::PendingPart,
        member: Option<&str>,
        password: Option<&str>,
        encrypted: bool,
    ) -> Result<FilePart, String> {
        let first = p.segment_ids.first().ok_or("no segments")?;
        let body = self
            .body(first)
            .await
            .map_err(|e| format!("RAR volume unreadable: {e}"))?;
        if body.offset != 0 {
            return Err("RAR volume's first article is not its start".to_owned());
        }
        let part = body.data.len() as u64;
        let claim = body.file_size;
        let member = member.map(|m| m.replace('\\', "/"));
        let mut scanner = Scanner::new();
        let entry = loop {
            match scanner
                .next(0, &body.data)
                .map_err(|e| format!("RAR volume: {e}"))?
            {
                Step::Entry(e)
                    if e.split_before && member.as_ref().is_none_or(|m| *m == e.name) =>
                {
                    break e;
                }
                Step::Entry(_) => {}
                Step::Key(kdf) => {
                    let password = password.ok_or("RAR headers are encrypted")?.to_owned();
                    let k = kdf.clone();
                    let derived = tokio::task::spawn_blocking(move || k.derive(&password))
                        .await
                        .map_err(|e| format!("key derivation failed: {e}"))?
                        .map_err(|e| format!("RAR volume: {e}"))?;
                    scanner.add_key(kdf, derived);
                }
                Step::Need { .. } | Step::End { .. } => {
                    return Err("member not found at the start of a RAR volume".to_owned());
                }
            }
        };
        // Encrypted continuations carry on the first volume's CBC chain.
        if !entry.stored || entry.encrypted != encrypted || (encrypted && entry.crypt.is_none()) {
            return Err("RAR member is compressed or encrypted".to_owned());
        }
        let sizes = match p.segment_ids.len() {
            1 => u32::try_from(part).ok().map(|s| vec![s]),
            // Another volume's size that this one's articles cannot hold is not its size.
            n if self.volumes.contains(&claim) && uniform(n, claim, part).is_some() => {
                uniform(n, claim, part)
            }
            // Every article before the last is `part` bytes, so a wrong claim only
            // misplaces the end of a volume the member does not reach.
            n if entry.data.end <= part * (n as u64 - 1) && uniform(n, claim, part).is_some() => {
                uniform(n, claim, part)
            }
            // The posted size may be wrong; the last article says where the volume ends.
            n => {
                let tail = self
                    .body(&p.segment_ids[n - 1])
                    .await
                    .map_err(|e| format!("RAR volume unreadable: {e}"))?;
                let len = tail.data.len() as u64;
                if tail.offset != part * (n as u64 - 1) || len == 0 || len > part {
                    return Err("an article belongs elsewhere in its file".to_owned());
                }
                let end = tail.offset + len;
                if !self.volumes.contains(&end) {
                    self.volumes.push(end);
                }
                uniform(n, end, part)
            }
        }
        .ok_or("segments are not split evenly")?;
        Ok(FilePart {
            segments: segments(
                &p.segment_ids,
                &Sized {
                    sizes,
                    trusted: false,
                    filled: None,
                },
                p.fallbacks.as_deref(),
                (None, None),
            ),
            offset: 0,
            data: ByteRange {
                start: entry.data.start,
                end: entry.data.end,
            },
            split_after: entry.split_after,
            proof: p.proof.as_ref().and_then(proof),
        })
    }
}

/// Recorded per-segment ranges as sizes, when they tile `start..start + len` in order.
fn recorded(ranges: &[LongRange], count: usize, start: u64, len: u64) -> Option<Vec<u32>> {
    if ranges.len() != count || count == 0 {
        return None;
    }
    let mut at = start;
    let mut sizes = Vec::with_capacity(count);
    for r in ranges {
        let r = range(*r)?;
        if r.start != at || r.is_empty() {
            return None;
        }
        sizes.push(u32::try_from(r.len()).ok()?);
        at = r.end;
    }
    (at == start + len).then_some(sizes)
}

/// `n` articles of `part` bytes covering `len`, the last one shorter or equal.
fn uniform(n: usize, len: u64, part: u64) -> Option<Vec<u32>> {
    let full = u64::try_from(n.checked_sub(1)?).ok()?;
    let last = len.checked_sub(part.checked_mul(full)?)?;
    if last == 0 || last > part {
        return None;
    }
    let mut sizes = vec![u32::try_from(part).ok()?; n];
    *sizes.last_mut()? = u32::try_from(last).ok()?;
    Some(sizes)
}

/// Our segments for C#'s `ids`; C#'s per-segment lists follow their ids past any
/// placeholders, which count as missing.
fn segments(
    ids: &[String],
    sized: &Sized,
    fallbacks: Option<&[Vec<String>]>,
    (missing, corrupt): (Option<&[i32]>, Option<&[i32]>),
) -> Segments {
    // Each segment's index in C#'s list; `None` for a placeholder.
    let origin: Vec<Option<usize>> = match &sized.filled {
        None => (0..ids.len()).map(Some).collect(),
        Some(filled) => {
            let mut next = 0;
            filled
                .iter()
                .map(|id| {
                    (!id.is_empty()).then(|| {
                        next += 1;
                        next - 1
                    })
                })
                .collect()
        }
    };
    let position: Vec<u32> = (0u32..)
        .zip(&origin)
        .filter_map(|(j, o)| o.map(|_| j))
        .collect();
    let indices = |list: Option<&[i32]>| -> Vec<u32> {
        list.unwrap_or_default()
            .iter()
            .filter_map(|&i| position.get(usize::try_from(i).ok()?).copied())
            .collect()
    };
    let mut missing = indices(missing);
    missing.extend(
        (0u32..)
            .zip(&origin)
            .filter_map(|(j, o)| o.is_none().then_some(j)),
    );
    let mut corrupt = indices(corrupt);
    for list in [&mut missing, &mut corrupt] {
        list.sort_unstable();
        list.dedup();
    }
    Segments {
        items: origin
            .iter()
            .zip(&sized.sizes)
            .map(|(&o, &size)| Segment {
                id: o.map_or_else(String::new, |i| ids[i].clone()),
                size,
                fallbacks: o
                    .and_then(|i| fallbacks?.get(i))
                    .cloned()
                    .unwrap_or_default(),
            })
            .collect(),
        sizes_trusted: sized.trusted,
        missing,
        corrupt,
    }
}

/// Drops a volume's placeholders, and the segments around them, that lie wholly outside
/// the member's bytes: no read reaches them, so they are no damage.
fn volume(mut p: FilePart) -> FilePart {
    let s = &mut p.segments;
    if !s.items.iter().any(|s| s.id.is_empty()) || p.data.is_empty() {
        return p;
    }
    let mut lead = 0;
    while lead < s.items.len() && p.offset + u64::from(s.items[lead].size) <= p.data.start {
        p.offset += u64::from(s.items[lead].size);
        lead += 1;
    }
    let mut end = p.offset
        + s.items[lead..]
            .iter()
            .map(|s| u64::from(s.size))
            .sum::<u64>();
    let mut keep = s.items.len();
    while keep > lead + 1 && end - u64::from(s.items[keep - 1].size) >= p.data.end {
        keep -= 1;
        end -= u64::from(s.items[keep].size);
    }
    s.items.truncate(keep);
    s.items.drain(..lead);
    let (lo, hi) = (
        u32::try_from(lead).unwrap_or(u32::MAX),
        u32::try_from(keep).unwrap_or(u32::MAX),
    );
    for list in [&mut s.missing, &mut s.corrupt] {
        list.retain(|i| (lo..hi).contains(i));
        for i in list.iter_mut() {
            *i -= lo;
        }
    }
    p
}

fn proof(p: &csharp::Par2Proof) -> Option<Par2Proof> {
    let (slice_md5, []) = p.slice_md5.as_chunks::<16>() else {
        return None;
    };
    Some(Par2Proof {
        set_id: None,
        file_length: u64::try_from(p.file_length).ok()?,
        slice_size: u32::try_from(p.slice_size).ok()?,
        slice_md5: slice_md5.to_vec(),
        slice_crc32: p.slice_crc32.clone(),
        file_id: p.file_id.as_slice().try_into().ok()?,
        file_md5: p.file_hash.as_slice().try_into().ok()?,
        md5_16k: p.file_16k_hash.as_slice().try_into().ok()?,
    })
}

fn range(r: LongRange) -> Option<ByteRange> {
    Some(ByteRange {
        start: u64::try_from(r.start).ok()?,
        end: u64::try_from(r.end).ok()?,
    })
}

/// Stamps the download each carried-over job folder in `db` came from, as the C# install
/// in `config` recorded it: the folder's Arr download id, else the one every file in it
/// agrees on (a file's Arr download id, else its NZB's id). C# kept no history for many
/// folders, and without a download no Arr can be asked to replace a broken file. Returns
/// how many folders were stamped.
pub async fn stamp_download_ids(config: &Path, db: &Db) -> Result<u64, Error> {
    let mut cs = open(config).await?;
    let columns: Vec<String> = sqlx::query_scalar("SELECT name FROM pragma_table_info('DavItems')")
        .fetch_all(&mut cs)
        .await?;
    let sql = if columns.iter().any(|c| c == "ArrDownloadId") {
        "SELECT Id AS id, Path AS path, SubType = 101 AS dir, ArrDownloadId AS arr, \
         NzbBlobId AS nzb FROM DavItems WHERE Path LIKE '/content/%'"
    } else {
        "SELECT Id AS id, Path AS path, SubType = 101 AS dir, NULL AS arr, \
         NzbBlobId AS nzb FROM DavItems WHERE Path LIKE '/content/%'"
    };
    let rows: Vec<DownloadRow> = sqlx::query_as(sql).fetch_all(&mut cs).await?;
    drop(cs);
    let job_dir = |path: &str| -> String {
        let dir: String = path.split_inclusive('/').take(4).collect();
        dir.trim_end_matches('/').to_owned()
    };
    // By C# job folder path.
    let mut jobs: HashMap<String, Job> = HashMap::new();
    for row in rows {
        let at = job_dir(&row.path);
        let arr = row.arr.as_deref().and_then(uuid);
        if row.dir && at == row.path {
            let job = jobs.entry(at).or_default();
            job.id = uuid(&row.id);
            job.download = arr;
        } else if !row.dir
            && let Some(download) = arr.or_else(|| row.nzb.as_deref().and_then(uuid))
        {
            jobs.entry(at).or_default().files.insert(download);
        }
    }
    let mut stamps = Vec::new();
    for job in jobs.into_values() {
        let download = job.download.or_else(|| match job.files.len() {
            1 => job.files.into_iter().next(),
            _ => None,
        });
        let (Some(id), Some(download)) = (job.id, download) else {
            continue;
        };
        // The importer keeps C# ids but may have moved the folder.
        if let Some(item) = db.item(ItemId(id)).await? {
            stamps.push((item.path, JobId(download)));
        }
    }
    let mut stamped = 0;
    for chunk in stamps.chunks(BATCH) {
        stamped += db.stamp_jobs(chunk).await?;
    }
    Ok(stamped)
}

/// C#'s `Path.Join` of two parts: one separator between them.
fn join(a: &str, b: &str) -> String {
    match (a.ends_with('/'), b.starts_with('/')) {
        _ if a.is_empty() => b.to_owned(),
        _ if b.is_empty() => a.to_owned(),
        (false, false) => format!("{a}/{b}"),
        _ => format!("{a}{b}"),
    }
}

fn category(c: String) -> String {
    if c.trim().is_empty() {
        UNCATEGORIZED.to_owned()
    } else {
        c
    }
}

fn uuid(s: &str) -> Option<Uuid> {
    Uuid::parse_str(s)
        .ok()
        .filter(|u| u.as_u128() > CS_SYSTEM || *u == CS_CONTENT)
}

/// C#'s `/content` is ours under another id.
fn parent(id: Uuid) -> ItemId {
    if id == CS_CONTENT {
        ItemId::CONTENT
    } else {
        ItemId(id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blob_path;

    #[test]
    fn derives_even_splits_and_refuses_others() {
        assert_eq!(
            uniform(3, 2_000_000, 768_000),
            Some(vec![768_000, 768_000, 464_000])
        );
        assert_eq!(uniform(2, 1_536_000, 768_000), Some(vec![768_000, 768_000]));
        assert_eq!(
            uniform(3, 1_536_000, 768_000),
            None,
            "last article would be empty"
        );
        assert_eq!(
            uniform(2, 1_600_000, 768_000),
            None,
            "last article longer than the rest"
        );
        let r = |start, end| LongRange { start, end };
        assert_eq!(recorded(&[r(0, 5), r(5, 7)], 2, 0, 7), Some(vec![5, 2]));
        assert_eq!(recorded(&[r(0, 5), r(6, 7)], 2, 0, 7), None, "gap");
        assert_eq!(
            recorded(&[r(0, 5), r(5, 7)], 2, 0, 8),
            None,
            "short of the length"
        );
    }

    #[test]
    fn caps_far_checks_within_our_longest_interval() {
        let now = 1_000_000;
        let id = ItemId(Uuid::from_u128(12_345));
        assert_eq!(cap_next_check(now + 5, id, now), now + 5);
        let years = now + 20 * MAX_CHECK_INTERVAL_MS;
        let capped = cap_next_check(years, id, now);
        assert!((now..=now + MAX_CHECK_INTERVAL_MS).contains(&capped));
        assert_ne!(
            capped,
            cap_next_check(years, ItemId(Uuid::from_u128(54_321)), now),
            "spread by id"
        );
    }

    fn rt() -> tokio::runtime::Runtime {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
    }

    fn tmp() -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("infinidysk-import-{}", Uuid::now_v7()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    /// A C# config dir whose empty-named category folder at `cat_path` holds a job
    /// folder at `job_path`, finished with category "".
    async fn seed(cat_path: &str, job_path: &str) -> (std::path::PathBuf, Uuid) {
        use sqlx::ConnectOptions as _;
        let dir = tmp();
        let mut c = sqlx::sqlite::SqliteConnectOptions::new()
            .filename(dir.join("db.sqlite"))
            .create_if_missing(true)
            .connect()
            .await
            .unwrap();
        sqlx::raw_sql(
            "CREATE TABLE DavItems (Id TEXT, ParentId TEXT, Name TEXT, Path TEXT, CreatedAt TEXT,
               Type INTEGER, SubType INTEGER, FileSize INTEGER, FileBlobId TEXT, NzbBlobId TEXT,
               ReleaseDate INTEGER, LastHealthCheck INTEGER, NextHealthCheck INTEGER,
               HealthRepairPending INTEGER DEFAULT 0, GeneratedStrmPath TEXT,
               GeneratedSymlinkPath TEXT, ArrDownloadId TEXT);
             CREATE TABLE HistoryItems (Id TEXT, CreatedAt TEXT, FileName TEXT, JobName TEXT,
               Category TEXT, DownloadStatus INTEGER, TotalSegmentBytes INTEGER,
               DownloadTimeSeconds INTEGER, FailMessage TEXT, DownloadDirId TEXT,
               NzbBlobId TEXT);
             CREATE TABLE QueueItems (Id TEXT, CreatedAt TEXT, FileName TEXT, JobName TEXT,
               Category TEXT, Priority INTEGER, PostProcessing INTEGER,
               TotalSegmentBytes INTEGER, PauseUntil TEXT, SortOrder INTEGER);
             CREATE TABLE QueueNzbContents (Id TEXT, NzbContents TEXT);",
        )
        .execute(&mut c)
        .await
        .unwrap();
        let cat = Uuid::now_v7();
        let job = Uuid::now_v7();
        for (id, parent, name, path) in
            [(cat, CS_CONTENT, "", cat_path), (job, cat, "job", job_path)]
        {
            sqlx::query(
                "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType) \
                 VALUES ($1, $2, $3, $4, '2025-01-01 00:00:00', 1, 101)",
            )
            .bind(id.to_string())
            .bind(parent.to_string())
            .bind(name)
            .bind(path)
            .execute(&mut c)
            .await
            .unwrap();
        }
        sqlx::query(
            "INSERT INTO HistoryItems VALUES ($1, '2025-01-01 00:00:00', 'job.nzb', 'job', '', \
             1, 100, 1, NULL, $2, NULL)",
        )
        .bind(Uuid::now_v7().to_string())
        .bind(job.to_string())
        .execute(&mut c)
        .await
        .unwrap();
        (dir, job)
    }

    #[test]
    fn csharp_wall_clock_times_become_utc() {
        let london = TimeZone::get("Europe/London").unwrap();
        let wall = |s: &str| s.parse::<Timestamp>().unwrap().as_millisecond();
        // BST is UTC+1; winter is UTC.
        assert_eq!(
            from_wall(wall("2026-10-02T09:50:39Z"), &london),
            wall("2026-10-02T08:50:39Z")
        );
        assert_eq!(
            from_wall(wall("2026-01-02T09:50:39Z"), &london),
            wall("2026-01-02T09:50:39Z")
        );
    }

    #[test]
    fn empty_category_folders_land_in_uncategorized() {
        // .NET's `Path.Join("/content", "")` shape, and the trailing-slash one.
        for (cat_path, job_path) in [("/content", "/content/job"), ("/content/", "/content//job")] {
            rt().block_on(async {
                let (cs, job) = seed(cat_path, job_path).await;
                let db = Db::connect(&format!("sqlite:{}", tmp().join("db.sqlite").display()))
                    .await
                    .unwrap();
                let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
                apply(&cs, &db, &client, "/mnt").await.unwrap();
                let item = db.item_by_path("/content/uncategorized/job").await.unwrap();
                assert_eq!(item.map(|i| i.id), Some(ItemId(job)), "{cat_path:?}");
                let history = db.history().await.unwrap();
                assert_eq!(history[0].0.category, "uncategorized");
            });
        }
    }

    #[test]
    fn folders_without_history_take_the_download_their_files_agree_on() {
        rt().block_on(async {
            use sqlx::ConnectOptions as _;
            let (cs, _) = seed("/content", "/content/job").await;
            let mut c = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(cs.join("db.sqlite"))
                .connect()
                .await
                .unwrap();
            let cat = Uuid::now_v7();
            let (agreed, split, download) = (Uuid::now_v7(), Uuid::now_v7(), Uuid::now_v7());
            for (id, parent, name, path) in [
                (cat, CS_CONTENT, "tv", "/content/tv"),
                (agreed, cat, "agreed", "/content/tv/agreed"),
                (split, cat, "split", "/content/tv/split"),
            ] {
                sqlx::query(
                    "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType) \
                     VALUES ($1, $2, $3, $4, '2025-01-01 00:00:00', 1, 101)",
                )
                .bind(id.to_string())
                .bind(parent.to_string())
                .bind(name)
                .bind(path)
                .execute(&mut c)
                .await
                .unwrap();
            }
            let db = Db::connect(&format!("sqlite:{}", tmp().join("db.sqlite").display()))
                .await
                .unwrap();
            let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
            apply(&cs, &db, &client, "/mnt").await.unwrap();
            // Files C# recorded no history for, added after the import's own stamping.
            let other = Uuid::now_v7();
            for (dir, arr, nzb) in [
                (agreed, Some(download), None),
                (agreed, None, Some(download)),
                (split, Some(download), None),
                (split, Some(other), None),
            ] {
                let name = if dir == agreed { "agreed" } else { "split" };
                sqlx::query(
                    "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType, \
                     ArrDownloadId, NzbBlobId) \
                     VALUES ($1, $2, 'f.mkv', $3, '2025-01-01 00:00:00', 2, 203, $4, $5)",
                )
                .bind(Uuid::now_v7().to_string())
                .bind(dir.to_string())
                .bind(format!("/content/tv/{name}/f.mkv"))
                .bind(arr.map(|u| u.to_string().to_uppercase()))
                .bind(nzb.map(|u| u.to_string()))
                .execute(&mut c)
                .await
                .unwrap();
            }
            assert_eq!(stamp_download_ids(&cs, &db).await.unwrap(), 1);
            assert_eq!(
                db.job_at("/content/tv/agreed").await.unwrap(),
                Some((JobId(download), "agreed".to_owned()))
            );
            assert_eq!(db.job_at("/content/tv/split").await.unwrap(), None);
        });
    }

    /// A C# 203 multipart blob in `blobs`: one split part of 100 bytes with recorded
    /// ranges, so it converts without fetching. Returns its id.
    fn multipart_blob(blobs: &Path) -> String {
        // MemoryPack: a version-tolerant object with short members.
        fn obj(members: &[Vec<u8>]) -> Vec<u8> {
            let mut out = vec![u8::try_from(members.len()).unwrap()];
            out.extend(members.iter().map(|m| u8::try_from(m.len()).unwrap()));
            out.extend(members.iter().flatten());
            out
        }
        fn list(items: &[Vec<u8>]) -> Vec<u8> {
            let mut out = i32::try_from(items.len()).unwrap().to_le_bytes().to_vec();
            out.extend(items.iter().flatten());
            out
        }
        let lr = |s: i64, e: i64| obj(&[s.to_le_bytes().to_vec(), e.to_le_bytes().to_vec()]);
        let segment: Vec<u8> = [(!3i32).to_le_bytes(), 3i32.to_le_bytes()]
            .concat()
            .into_iter()
            .chain(*b"a@b")
            .collect();
        let part = obj(&[
            list(&[segment]),
            lr(0, 100),
            lr(0, 100),
            list(&[lr(0, 100)]),
        ]);
        let root = obj(&[
            Uuid::now_v7().to_bytes_le().to_vec(),
            obj(&[vec![255], list(&[part])]),
        ]);
        let id = Uuid::now_v7().to_string();
        let path = blob_path(blobs, &id).unwrap();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, zstd::encode_all(root.as_slice(), 1).unwrap()).unwrap();
        id
    }

    #[test]
    fn progress_counts_written_skipped_and_remaining_files_on_resume() {
        rt().block_on(async {
            use sqlx::ConnectOptions as _;
            let (config, job) = seed("/content", "/content/job").await;
            let destination = tmp().join("db.sqlite");
            let db = Db::connect(&format!("sqlite:{}", destination.display()))
                .await
                .unwrap();
            let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
            let mut source = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(config.join("db.sqlite"))
                .connect()
                .await
                .unwrap();
            let nzb = Uuid::now_v7();
            let missing_nzb = Uuid::now_v7();
            let nzb_path = blob_path(&config.join("blobs"), &nzb.to_string()).unwrap();
            std::fs::create_dir_all(nzb_path.parent().unwrap()).unwrap();
            std::fs::write(nzb_path, b"<nzb/>").unwrap();
            for index in 0..3 {
                let meta = (index < 2).then(|| multipart_blob(&config.join("blobs")));
                sqlx::query(
                    "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType, \
                     FileSize, FileBlobId, NzbBlobId) \
                     VALUES ($1, $2, $3, $4, '2025-01-01', 2, 203, 100, $5, $6)",
                )
                .bind(Uuid::now_v7().to_string())
                .bind(job.to_string())
                .bind(format!("file-{index}.mkv"))
                .bind(format!("/content/job/file-{index}.mkv"))
                .bind(meta)
                .bind(if index == 1 { missing_nzb } else { nzb }.to_string())
                .execute(&mut source)
                .await
                .unwrap();
            }
            sqlx::raw_sql(
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT, ProductVersion TEXT);
                 INSERT INTO __EFMigrationsHistory VALUES ('20260824160000_Test', '10.0.0');",
            )
            .execute(&mut source)
            .await
            .unwrap();
            let checks = std::sync::Mutex::new(Vec::new());
            crate::survey_with_progress(&config, |progress| {
                checks.lock().unwrap().push(progress);
            })
            .await
            .unwrap();
            let checks = checks.into_inner().unwrap();
            let last = *checks.last().unwrap();
            assert_eq!(checks[0], Progress::counting(Stage::Checking));
            assert_eq!(last.total, Some(last.done));
            assert!(last.done >= 3);
            assert!(checks[1..].windows(2).all(|w| w[0].done <= w[1].done));
            let mut destination = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(destination)
                .connect()
                .await
                .unwrap();
            sqlx::raw_sql(
                "CREATE TRIGGER reject_blob BEFORE INSERT ON blobs \
                 BEGIN SELECT RAISE(FAIL, 'test write failure'); END;",
            )
            .execute(&mut destination)
            .await
            .unwrap();
            let failed_updates = std::sync::Mutex::new(Vec::new());
            let result = import_nzbs(
                &mut source,
                &db,
                &config.join("blobs"),
                None,
                &mut Imported::default(),
                &|progress| failed_updates.lock().unwrap().push(progress),
            )
            .await;
            assert!(result.is_err());
            assert!(
                failed_updates
                    .into_inner()
                    .unwrap()
                    .iter()
                    .all(|progress| { progress.total.is_none_or(|total| progress.done < total) })
            );
            assert_eq!(db.blob_ids().await.unwrap(), [] as [BlobId; 0]);
            sqlx::query("DROP TRIGGER reject_blob")
                .execute(&mut destination)
                .await
                .unwrap();
            for (total, written, directories, nzbs) in [(3, 2, 2, 2), (1, 0, 0, 1)] {
                let updates = std::sync::Mutex::new(Vec::new());
                let imported = apply_with_progress(&config, &db, &client, "/mnt", |progress| {
                    updates.lock().unwrap().push(progress);
                })
                .await
                .unwrap();
                let updates = updates.into_inner().unwrap();
                let mut stages: Vec<Stage> = updates.iter().map(|p| p.stage).collect();
                stages.dedup();
                assert_eq!(
                    stages,
                    [
                        Stage::Directories,
                        Stage::Nzbs,
                        Stage::Files,
                        Stage::Finishing
                    ]
                );
                for (stage, total) in [(Stage::Directories, directories), (Stage::Nzbs, nzbs)] {
                    let stage_updates: Vec<_> = updates
                        .iter()
                        .filter(|update| update.stage == stage)
                        .collect();
                    assert_eq!(*stage_updates[0], Progress::counting(stage));
                    assert_eq!(*stage_updates[1], Progress::at(stage, 0, total));
                    assert_eq!(
                        **stage_updates.last().unwrap(),
                        Progress::at(stage, total, total)
                    );
                    assert!(stage_updates.iter().all(|update| update.done <= total));
                }
                assert_eq!(imported.nzbs, nzbs - 1);
                assert!(db.blob_ids().await.unwrap().contains(&BlobId(nzb)));
                let updates: Vec<Progress> = updates
                    .into_iter()
                    .filter(|p| p.stage == Stage::Files)
                    .collect();
                assert_eq!(updates[0], Progress::at(Stage::Files, 0, total));
                assert_eq!(
                    updates.last(),
                    Some(&Progress {
                        stage: Stage::Files,
                        done: written,
                        total: Some(total),
                        skipped: 1
                    })
                );
                assert_eq!(imported.files, written);
                assert_eq!(imported.skipped_count, 1);
                assert!(
                    updates
                        .iter()
                        .all(|progress| progress.done + progress.skipped <= total)
                );
            }
        });
    }

    #[test]
    fn retry_writes_only_kept_files_and_the_folders_above_them() {
        rt().block_on(async {
            use sqlx::ConnectOptions as _;
            let (cs, _) = seed("/content", "/content/job").await;
            let db = Db::connect(&format!("sqlite:{}", tmp().join("db.sqlite").display()))
                .await
                .unwrap();
            let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
            apply(&cs, &db, &client, "/mnt").await.unwrap();
            // Files the first import skipped, and their folders.
            let mut c = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(cs.join("db.sqlite"))
                .connect()
                .await
                .unwrap();
            let (tv, download) = (Uuid::now_v7(), Uuid::now_v7());
            let insert = |id: Uuid, parent: Uuid, name: &str, path: &str, file: bool| {
                sqlx::query(
                    "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType, \
                     FileSize, FileBlobId, NzbBlobId) \
                     VALUES ($1, $2, $3, $4, '2025-01-01 00:00:00', $5, $6, 100, $7, $8)",
                )
                .bind(id.to_string())
                .bind(parent.to_string())
                .bind(name.to_owned())
                .bind(path.to_owned())
                .bind(if file { 2 } else { 1 })
                .bind(if file { 203 } else { 101 })
                .bind(file.then(|| multipart_blob(&cs.join("blobs"))))
                .bind(file.then(|| download.to_string()))
            };
            insert(tv, CS_CONTENT, "tv", "/content/tv", false)
                .execute(&mut c)
                .await
                .unwrap();
            for name in ["a", "b"] {
                let dir = Uuid::now_v7();
                let path = format!("/content/tv/{name}");
                insert(dir, tv, name, &path, false)
                    .execute(&mut c)
                    .await
                    .unwrap();
                insert(Uuid::now_v7(), dir, "f.mkv", &format!("{path}/f.mkv"), true)
                    .execute(&mut c)
                    .await
                    .unwrap();
            }

            let done = retry_files(&cs, &db, &client, |_, path| {
                path.starts_with("/content/tv/a/")
            })
            .await
            .unwrap();
            assert_eq!((done.directories, done.files), (2, 1), "{done}");
            assert!(
                db.item_by_path("/content/tv/a/f.mkv")
                    .await
                    .unwrap()
                    .is_some()
            );
            assert!(db.item_by_path("/content/tv/b").await.unwrap().is_none());
            assert_eq!(
                db.job_at("/content/tv/a").await.unwrap(),
                Some((JobId(download), "a".to_owned()))
            );
        });
    }

    #[test]
    fn restores_missing_downloads_without_replacing_links_or_recreating_records() {
        rt().block_on(async {
            use sqlx::ConnectOptions as _;
            let (config, job) = seed("/content", "/content/job").await;
            let mut source = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(config.join("db.sqlite")).connect().await.unwrap();
            let download = Uuid::now_v7();
            let history = Uuid::now_v7();
            let file = Uuid::now_v7();
            sqlx::query(
                "INSERT INTO DavItems (Id, ParentId, Name, Path, CreatedAt, Type, SubType, \
                 FileSize, FileBlobId, NzbBlobId) \
                 VALUES ($1, $2, 'file.mkv', '/content/job/file.mkv', '2025-01-01', 2, 203, 100, $3, $4)",
            ).bind(file.to_string()).bind(job.to_string())
                .bind(multipart_blob(&config.join("blobs"))).bind(download.to_string())
                .execute(&mut source).await.unwrap();
            sqlx::query(
                "INSERT INTO HistoryItems VALUES ($1, '2025-01-01', 'job.nzb', \
                 'job', '', 1, 100, 1, NULL, $2, $3)",
            ).bind(history.to_string()).bind(job.to_string()).bind(download.to_string())
                .execute(&mut source).await.unwrap();
            let db = Db::connect("sqlite::memory:").await.unwrap();
            let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
            apply(&config, &db, &client, "/mnt").await.unwrap();
            assert_eq!(db.item(ItemId(file)).await.unwrap().unwrap().nzb_blob, None);
            assert_eq!(db.history_entry(JobId(history)).await.unwrap().unwrap().nzb_blob, None);

            let path = blob_path(&config.join("blobs"), &download.to_string()).unwrap();
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(&path, b"<nzb/>").unwrap();
            let deleted = Uuid::now_v7();
            sqlx::query(
                "INSERT INTO HistoryItems VALUES ($1, '2025-01-01', 'job.nzb', \
                 'job', '', 1, 100, 1, NULL, $2, $3)",
            ).bind(Uuid::now_v7().to_string()).bind(job.to_string()).bind(deleted.to_string())
                .execute(&mut source).await.unwrap();
            let deleted_path = blob_path(&config.join("blobs"), &deleted.to_string()).unwrap();
            std::fs::create_dir_all(deleted_path.parent().unwrap()).unwrap();
            std::fs::write(deleted_path, b"<nzb/>").unwrap();

            assert_eq!(restore_nzb_downloads(&config, &db).await.unwrap(), 2);
            assert_eq!(db.item(ItemId(file)).await.unwrap().unwrap().nzb_blob, Some(BlobId(download)));
            assert_eq!(db.history_entry(JobId(history)).await.unwrap().unwrap().nzb_blob, Some(BlobId(download)));
            assert!(db.blob(BlobId(deleted)).await.unwrap().is_none());
            assert_eq!(restore_nzb_downloads(&config, &db).await.unwrap(), 0);
            assert_eq!(db.restore_nzb_links(
                &[(ItemId(file), BlobId(deleted))], &[(JobId(history), BlobId(deleted))],
            ).await.unwrap(), 0);
            db.close().await;
        });
    }

    #[test]
    fn a_symlinked_nzb_blob_is_not_imported() {
        rt().block_on(async {
            use sqlx::ConnectOptions as _;
            let (cs, job) = seed("/content", "/content/job").await;
            let nzb =
                b"<?xml version=\"1.0\"?><nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"></nzb>";
            let outside = tmp().join("outside.nzb");
            std::fs::write(&outside, nzb).unwrap();
            let (kept, linked) = (Uuid::now_v7(), Uuid::now_v7());
            for id in [kept, linked] {
                let path = blob_path(&cs.join("blobs"), &id.to_string()).unwrap();
                std::fs::create_dir_all(path.parent().unwrap()).unwrap();
                if id == kept {
                    std::fs::write(&path, nzb).unwrap();
                } else {
                    std::os::unix::fs::symlink(&outside, &path).unwrap();
                }
            }
            let mut c = sqlx::sqlite::SqliteConnectOptions::new()
                .filename(cs.join("db.sqlite"))
                .connect()
                .await
                .unwrap();
            for id in [kept, linked] {
                sqlx::query(
                    "INSERT INTO HistoryItems VALUES ($1, '2025-01-01 00:00:00', 'job.nzb', \
                     'job', '', 1, 100, 1, NULL, $2, $3)",
                )
                .bind(Uuid::now_v7().to_string())
                .bind(job.to_string())
                .bind(id.to_string())
                .execute(&mut c)
                .await
                .unwrap();
            }
            let db = Db::connect(&format!("sqlite:{}", tmp().join("db.sqlite").display()))
                .await
                .unwrap();
            let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
            apply(&cs, &db, &client, "/mnt").await.unwrap();
            assert!(db.blob(BlobId(kept)).await.unwrap().is_some());
            assert!(db.blob(BlobId(linked)).await.unwrap().is_none());
        });
    }

    #[test]
    fn unknown_csharp_size_takes_the_computed_size() {
        let blobs = tmp();
        let id = multipart_blob(&blobs);
        let row = FileRow {
            id: Uuid::now_v7().to_string(),
            parent: Uuid::now_v7().to_string(),
            name: "f.mkv".into(),
            path: "/content/c/j/f.mkv".into(),
            sub_type: 203,
            size: Some(i64::MAX),
            meta: Some(id),
            nzb: None,
            created: 0,
            release_date: None,
            last_check: None,
            next_check: None,
            repair_pending: false,
        };
        let client = Client::new(Vec::<infinidysk_nntp::Provider>::new());
        let mut sizer = Sizer {
            client: &client,
            part: None,
            volumes: Vec::new(),
            fetched: 0,
            provider_errors: 0,
        };
        let (meta, _) = rt().block_on(convert(&row, &blobs, &mut sizer)).unwrap();
        assert_eq!(meta.size(), 100);
    }

    async fn loopback(
        articles: Vec<infinidysk_testkit::Article>,
    ) -> (infinidysk_testkit::LoopbackServer, Client) {
        use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
        let server = infinidysk_testkit::LoopbackServer::start(articles)
            .await
            .unwrap();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            std::time::Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections: 2,
            pipeline_depth: 1,
            idle_timeout: std::time::Duration::from_secs(5),
            min_connections: 0,
        };
        let client = Client::new([Provider {
            pool: Pool::new(endpoint, config, Downloads::new(4)).unwrap(),
            key: String::new(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]);
        (server, client)
    }

    fn sizer(client: &Client) -> Sizer<'_> {
        Sizer {
            client,
            part: None,
            volumes: Vec::new(),
            fetched: 0,
            provider_errors: 0,
        }
    }

    #[test]
    fn matching_volume_geometry_avoids_additional_fetches() {
        use infinidysk_testkit::{Corpus, post};

        let corpus = Corpus::generate(1025, 4, 10_000);
        let cases = [
            ("first", 25_000, 24_990, 24_990, 1),
            ("matching", 25_000, 24_990, 24_990, 0),
            ("exact", 25_000, 25_000, 24_990, 0),
            ("boundary", 25_000, 20_904, 20_904, 0),
            ("outside", 25_000, 20_903, 20_903, 1),
            ("oversized", 25_000, 26_000, 24_990, 1),
            ("more-segments", 35_000, 24_990, 24_990, 1),
            ("member-beyond", 26_000, 24_990, 25_990, 1),
        ];
        let mut articles = Vec::new();
        let mut volumes = Vec::new();
        for (prefix, size, saved, member, fetches) in cases {
            let posted = post(prefix, "volume.bin", &corpus.data[..size], 10_000);
            let ids: Vec<_> = posted
                .iter()
                .map(|article| article.message_id.clone())
                .collect();
            articles.extend(posted);
            volumes.push((ids, size as u64, saved, member, fetches));
        }
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let mut sizing = sizer(&client);
            for (ids, size, saved, member, fetches) in volumes {
                let before = sizing.fetched;
                let sized = sizing
                    .run(&ids, None, (0, saved), None, Some(member))
                    .await
                    .unwrap();
                assert_eq!(sized.sizes, uniform(ids.len(), size, 10_000).unwrap());
                assert!(!sized.trusted);
                assert!(sized.filled.is_none());
                assert_eq!(sizing.fetched - before, fetches);
            }
        });
    }

    #[test]
    fn articles_csharp_never_listed_become_missing_placeholders() {
        use infinidysk_testkit::post;

        // Ten articles of 100 bytes, the last one 50.
        let articles = post("f", "f.bin", &[7; 950], 100);
        let ids = |skip: &[usize]| -> Vec<String> {
            (0..10)
                .filter(|i| !skip.contains(i))
                .map(|i| format!("f-{i}@loopback"))
                .collect()
        };
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let sizer = || sizer(&client);

            // A plain file missing two articles; C#'s missing index 2 (f-3) follows its id.
            let listed = ids(&[2, 7]);
            let sized = sizer()
                .run(&listed, None, (0, 950), None, None)
                .await
                .unwrap();
            let s = segments(&listed, &sized, None, (Some(&[2][..]), None));
            let got: Vec<&str> = s.items.iter().map(|s| s.id.as_str()).collect();
            let mut want = ids(&[]);
            want[2].clear();
            want[7].clear();
            assert_eq!(got, want);
            assert_eq!(s.size(), 950);
            assert_eq!(s.items[9].size, 50);
            assert_eq!(s.missing, [2, 3, 7]);

            // A volume found from its last article; the gap past the member is dropped.
            let listed = ids(&[4]);
            let sized = sizer()
                .run(&listed, None, (0, 950), None, Some(950))
                .await
                .unwrap();
            let part = volume(FilePart {
                segments: segments(&listed, &sized, None, (None, None)),
                offset: 0,
                data: ByteRange {
                    start: 10,
                    end: 350,
                },
                split_after: true,
                proof: None,
            });
            let got: Vec<&str> = part.segments.items.iter().map(|s| s.id.as_str()).collect();
            assert_eq!(got, ids(&[4, 5, 6, 7, 8, 9]));
            assert_eq!(part.segments.size(), 400);
            assert_eq!(part.segments.missing, [] as [u32; 0]);

            // Ending past a gap, the last article's offset over its list index is no
            // article size (900 / 5 = 180).
            let listed = ids(&[5, 6, 7, 8]);
            let sized = sizer()
                .run(&listed, None, (0, 950), None, Some(950))
                .await
                .unwrap();
            let s = segments(&listed, &sized, None, (None, None));
            assert_eq!(s.items[0].size, 100);
            assert_eq!(s.size(), 950);
            assert_eq!(s.missing, [5, 6, 7, 8]);
        });
    }

    #[test]
    fn wrong_posted_sizes_give_way_to_article_offsets() {
        use infinidysk_testkit::post;

        // Ten articles of 100 bytes, the last one 50, each claiming a different, smaller
        // file of more parts, as some posters write them.
        let mut articles = post("f", "f.bin", &[7; 950], 100);
        for (i, a) in articles.iter_mut().enumerate() {
            let line = a.wire.iter().position(|&b| b == b'\n').unwrap();
            let claim = format!(
                "=ybegin part={} total={} line=128 size={} name=f.bin\r\n",
                i + 1,
                40 + i,
                300 + 7 * i
            );
            a.wire.splice(..=line, claim.into_bytes());
        }
        let ids = |skip: &[usize]| -> Vec<String> {
            (0..10)
                .filter(|i| !skip.contains(i))
                .map(|i| format!("f-{i}@loopback"))
                .collect()
        };
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;

            // C# recorded the volume up to the member's last byte.
            let mut s = sizer(&client);
            let sized = s
                .run(&ids(&[]), None, (0, 940), None, Some(940))
                .await
                .unwrap();
            assert_eq!(
                sized.sizes,
                [100, 100, 100, 100, 100, 100, 100, 100, 100, 50]
            );
            assert!(sized.filled.is_none());
            assert_eq!((s.part, s.volumes.as_slice()), (Some(100), &[950][..]));

            // Articles C# never listed still find their places.
            let listed = ids(&[4, 5, 6, 7, 8]);
            let sized = sizer(&client)
                .run(&listed, None, (0, 940), None, Some(940))
                .await
                .unwrap();
            let s = segments(&listed, &sized, None, (None, None));
            let mut want = ids(&[]);
            for id in &mut want[4..9] {
                id.clear();
            }
            let got: Vec<&str> = s.items.iter().map(|s| s.id.as_str()).collect();
            assert_eq!(got, want);
            assert_eq!(s.size(), 950);

            // A list short of the member's end is not taken for the whole volume.
            let error = sizer(&client)
                .run(&ids(&[9]), None, (0, 940), None, Some(940))
                .await
                .err();
            assert_eq!(
                error.as_deref(),
                Some("an article belongs elsewhere in its file")
            );
        });
    }

    #[test]
    fn oversized_volume_claims_use_measured_member_coverage() {
        use infinidysk_testkit::post;

        for length in [50, 950, 1000] {
            let mut articles = post("f", "f.bin", &vec![7; length], 100);
            for (index, article) in articles.iter_mut().enumerate() {
                let line = article.wire.iter().position(|&byte| byte == b'\n').unwrap();
                let claim = format!(
                    "=ybegin part={} total=40 line=128 size={} name=f.bin\r\n",
                    index + 1,
                    1300 + 7 * index
                );
                article.wire.splice(..=line, claim.into_bytes());
            }
            let ids: Vec<_> = articles
                .iter()
                .map(|article| article.message_id.clone())
                .collect();
            rt().block_on(async {
                let (_server, client) = loopback(articles).await;
                let mut sizing = sizer(&client);
                let sized = sizing
                    .run(
                        &ids,
                        None,
                        (0, length as u64 - 10),
                        None,
                        Some(length as u64 - 10),
                    )
                    .await
                    .unwrap();
                assert_eq!(sized.sizes, uniform(ids.len(), length as u64, 100).unwrap());
                assert!(sized.filled.is_none());
                assert_eq!(sizing.volumes, [length as u64]);

                if ids.len() > 1 {
                    let mut listed = ids.clone();
                    listed.remove(4);
                    let sized = sizer(&client)
                        .run(
                            &listed,
                            None,
                            (0, length as u64 - 10),
                            None,
                            Some(length as u64 - 10),
                        )
                        .await
                        .unwrap();
                    let sized_segments = segments(&listed, &sized, None, (None, None));
                    assert_eq!(sized_segments.size(), length as u64);
                    assert_eq!(sized_segments.missing, [4]);

                    let truncated = &ids[..ids.len() - 1];
                    let damaged = sizer(&client)
                        .run(
                            truncated,
                            None,
                            (0, length as u64 - 10),
                            None,
                            Some(length as u64 - 10),
                        )
                        .await
                        .unwrap();
                    let damaged_segments = segments(truncated, &damaged, None, (None, None));
                    assert!(
                        damaged_segments
                            .missing
                            .contains(&u32::try_from(ids.len() - 1).unwrap())
                    );
                }
            });
        }
    }

    #[test]
    fn complete_volumes_without_a_right_part_count_take_the_posted_size() {
        use infinidysk_testkit::post;

        // No `total=`, then a wrong one; with and without the first article.
        for (total, head) in [("", true), ("total=40 ", true), ("", false)] {
            let mut articles = post("f", "f.bin", &[7; 950], 100);
            for (i, a) in articles.iter_mut().enumerate() {
                let line = a.wire.iter().position(|&b| b == b'\n').unwrap();
                let claim = format!(
                    "=ybegin part={} {total}line=128 size=950 name=f.bin\r\n",
                    i + 1
                );
                a.wire.splice(..=line, claim.into_bytes());
            }
            if !head {
                articles.remove(0);
            }
            let ids: Vec<String> = (0..10).map(|i| format!("f-{i}@loopback")).collect();
            rt().block_on(async {
                let (_server, client) = loopback(articles).await;
                let sized = sizer(&client)
                    .run(&ids, None, (0, 940), None, Some(940))
                    .await
                    .unwrap();
                assert_eq!(
                    sized.sizes,
                    [100, 100, 100, 100, 100, 100, 100, 100, 100, 50],
                    "{total}"
                );
                assert!(sized.filled.is_none());
            });
        }
    }

    #[test]
    fn conflicting_headers_give_way_to_a_tail_that_ends_the_volume() {
        use infinidysk_testkit::post;

        // Only the last article claims the volume's size, none the right part count, and
        // the first claims a one-part file.
        for length in [950, 1000] {
            let mut articles = post("f", "f.bin", &vec![7; length], 100);
            let last = articles.len() - 1;
            for (i, a) in articles.iter_mut().enumerate() {
                let line = a.wire.iter().position(|&b| b == b'\n').unwrap();
                let size = if i == last { length } else { 300 + 7 * i };
                let total = if i == 0 { 1 } else { 40 + i };
                let claim = format!(
                    "=ybegin part={} total={total} line=128 size={size} name=f.bin\r\n",
                    i + 1
                );
                a.wire.splice(..=line, claim.into_bytes());
            }
            let ids: Vec<String> = articles.iter().map(|a| a.message_id.clone()).collect();
            rt().block_on(async {
                let (_server, client) = loopback(articles).await;
                let mut s = sizer(&client);
                let sized = s
                    .run(
                        &ids,
                        None,
                        (0, length as u64),
                        None,
                        Some(length as u64 - 20),
                    )
                    .await
                    .unwrap();
                assert_eq!(sized.sizes, uniform(10, length as u64, 100).unwrap());
                assert!(sized.filled.is_none());
                assert_eq!(
                    (s.part, s.volumes.as_slice()),
                    (Some(100), &[length as u64][..])
                );
            });
        }
    }

    #[test]
    fn volume_estimates_past_the_volume_need_only_cover_the_member() {
        use infinidysk_testkit::post;

        // C# estimated these volumes from the NZB's encoded sizes, past their real end.
        let mut articles = post("f", "f.bin", &[7; 950], 100);
        let ids: Vec<String> = articles.iter().map(|a| a.message_id.clone()).collect();
        articles.extend(post("g", "g.bin", &[7; 1950], 100));
        let want = uniform(10, 950, 100).unwrap();
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let mut s = sizer(&client);
            let sized = s.run(&ids, None, (0, 990), None, Some(940)).await.unwrap();
            assert_eq!(sized.sizes, want);
            assert!(sized.filled.is_none());
            assert_eq!(s.volumes, [950]);

            for (len, additional_fetches) in [(1003, 1), (940, 0)] {
                let fetched = s.fetched;
                let sized = s.run(&ids, None, (0, len), None, Some(940)).await.unwrap();
                assert_eq!(
                    (sized.sizes, s.fetched),
                    (want.clone(), fetched + additional_fetches)
                );
            }

            // A bigger volume whose member ends there, its list short of ten articles,
            // is measured: its estimate runs far past that size.
            let listed: Vec<String> = (0..20)
                .filter(|i| !(4..14).contains(i))
                .map(|i| format!("g-{i}@loopback"))
                .collect();
            let sized = s
                .run(&listed, None, (0, 2010), None, Some(940))
                .await
                .unwrap();
            let g = segments(&listed, &sized, None, (None, None));
            assert_eq!(g.size(), 1950);
            assert_eq!(g.missing, (4..14).collect::<Vec<u32>>());

            // Member bytes C#'s list lacks stay missing.
            let mut listed = ids.clone();
            listed.remove(4);
            let sized = sizer(&client)
                .run(&listed, None, (0, 990), None, Some(940))
                .await
                .unwrap();
            let s = segments(&listed, &sized, None, (None, None));
            assert_eq!((s.size(), s.missing.as_slice()), (950, &[4][..]));
            let truncated = &ids[..9];
            let sized = sizer(&client)
                .run(truncated, None, (0, 990), None, Some(940))
                .await
                .unwrap();
            let s = segments(truncated, &sized, None, (None, None));
            assert_eq!((s.size(), s.missing.as_slice()), (950, &[9][..]));
        });

        // An article past the member's end need not be on the provider, with or without
        // a volume of the same size measured before.
        let mut articles = post("f", "f.bin", &[7; 950], 100);
        articles.pop();
        let same: Vec<String> = (0..10).map(|i| format!("g-{i}@loopback")).collect();
        articles.extend(post("g", "g.bin", &[7; 950], 100));
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let sized = sizer(&client)
                .run(&ids, None, (0, 990), None, Some(890))
                .await
                .unwrap();
            assert_eq!(sized.sizes, want);
            let mut s = sizer(&client);
            s.run(&same, None, (0, 940), None, Some(940)).await.unwrap();
            let sized = s.run(&ids, None, (0, 940), None, Some(890)).await.unwrap();
            assert_eq!(sized.sizes, want);
        });
    }

    #[test]
    fn an_article_listed_past_a_volumes_end_is_left_out() {
        use infinidysk_testkit::post;

        // C#'s list holds one article more than the volume was posted in; no provider
        // has it. The volume ends on a full article, then on a short one.
        for length in [1000, 950] {
            let articles = post("f", "f.bin", &vec![7; length], 100);
            let posted: Vec<String> = articles.iter().map(|a| a.message_id.clone()).collect();
            let mut ids = posted.clone();
            ids.push("extra@loopback".to_owned());
            let length = length as u64;
            rt().block_on(async {
                let (_server, client) = loopback(articles).await;
                let sized = sizer(&client)
                    .run(&ids, None, (0, length), None, Some(length - 20))
                    .await
                    .unwrap();
                assert_eq!(sized.sizes, uniform(10, length, 100).unwrap(), "{length}");
                assert_eq!(sized.filled.as_deref(), Some(posted.as_slice()));
                let s = segments(&ids, &sized, None, (None, None));
                assert_eq!((s.size(), s.missing.as_slice()), (length, &[][..]));
            });
        }
    }

    #[test]
    fn wrong_headers_do_not_require_a_trailer_past_the_member() {
        use infinidysk_testkit::post;

        for claim in [300, 1300] {
            for available in [2, 8, 9] {
                for omitted in [false, true] {
                    for missing_head in [false, true] {
                        let mut articles = post("prior", "prior.bin", &[7; 950], 100);
                        let prior: Vec<_> = articles
                            .iter()
                            .map(|article| article.message_id.clone())
                            .collect();
                        let mut current = post("current", "current.bin", &[7; 950], 100);
                        let listed: Vec<_> = current
                            .iter()
                            .enumerate()
                            .filter(|(index, _)| !omitted || *index != 4)
                            .map(|(_, article)| article.message_id.clone())
                            .collect();
                        for (index, article) in current.iter_mut().enumerate() {
                            let line = article.wire.iter().position(|&byte| byte == b'\n').unwrap();
                            let header = format!(
                                "=ybegin part={} total=40 line=128 size={claim} name=current.bin\r\n",
                                index + 1
                            );
                            article.wire.splice(..=line, header.into_bytes());
                        }
                        current.truncate(available);
                        if missing_head {
                            current.remove(0);
                        }
                        articles.extend(current);
                        rt().block_on(async {
                            let (_server, client) = loopback(articles).await;
                            for warm in [false, true] {
                                let mut sizing = sizer(&client);
                                if warm {
                                    sizing
                                        .run(&prior, None, (0, 940), None, Some(940))
                                        .await
                                        .unwrap();
                                }
                                let before = sizing.fetched;
                                let coverage = available as u64 * 100;
                                let sized = sizing
                                    .run(
                                        &listed,
                                        None,
                                        (0, coverage - 10),
                                        None,
                                        Some(coverage - 10),
                                    )
                                    .await
                                    .unwrap();
                                let result = segments(&listed, &sized, None, (None, None));
                                let reused = warm && !omitted;
                                assert!(!sized.trusted);
                                assert_eq!(result.size(), if reused { 950 } else { coverage });
                                assert_eq!(result.items.len(), if reused { 10 } else { available });
                                assert_eq!(
                                    result.missing,
                                    if omitted && available > 4 {
                                        vec![4]
                                    } else {
                                        vec![]
                                    }
                                );
                                assert!(!sizing.volumes.contains(&coverage));
                                if reused {
                                    assert_eq!(sizing.fetched, before);
                                } else if !omitted && available == 9 {
                                    assert_eq!(
                                        sizing.fetched - before,
                                        if missing_head { 4 } else { 3 }
                                    );
                                }
                                let beyond_available = sizing
                                    .run(
                                        &listed,
                                        None,
                                        (0, coverage + 10),
                                        None,
                                        Some(coverage + 10),
                                    )
                                    .await;
                                if reused {
                                    let inferred = beyond_available.unwrap();
                                    assert_eq!(inferred.sizes, uniform(10, 950, 100).unwrap());
                                    assert!(!inferred.trusted);
                                    assert_eq!(sizing.fetched, before);
                                } else {
                                    assert!(beyond_available.is_err());
                                }
                            }
                        });
                    }
                }
            }
        }
    }

    #[test]
    fn cached_volume_geometry_can_hide_unlisted_articles() {
        use infinidysk_testkit::post;

        let mut articles = post("small", "small.bin", &[7; 1950], 100);
        let small: Vec<String> = articles
            .iter()
            .map(|article| article.message_id.clone())
            .collect();
        let larger = post("large", "large.bin", &[7; 2050], 100);
        // As big as the small volume, but in 95-byte articles.
        let finer = post("finer", "finer.bin", &[7; 1950], 95);
        let omit = |posted: &[infinidysk_testkit::Article]| -> Vec<String> {
            posted
                .iter()
                .enumerate()
                .filter(|(index, _)| *index != 4)
                .map(|(_, article)| article.message_id.clone())
                .collect()
        };
        // Each list is as long as the small volume's. C#'s length is the volume's
        // estimate or the member's end.
        let cases = [
            (omit(&larger), 2050, 2050),
            (omit(&larger), 1940, 2050),
            (omit(&finer), 1940, 1950),
        ];
        articles.extend(larger);
        articles.extend(finer);
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            for (listed, len, size) in &cases {
                let cold = sizer(&client)
                    .run(listed, None, (0, *len), None, Some(1940))
                    .await
                    .unwrap();
                let cold = segments(listed, &cold, None, (None, None));
                assert_eq!((cold.size(), cold.missing.as_slice()), (*size, &[4][..]));
                let mut sizing = sizer(&client);
                sizing
                    .run(&small, None, (0, 1950), None, Some(1940))
                    .await
                    .unwrap();
                let before = sizing.fetched;
                let warm = sizing
                    .run(listed, None, (0, *len), None, Some(1940))
                    .await
                    .unwrap();
                assert!(!warm.trusted);
                if *len <= 1950 {
                    assert_eq!(warm.sizes, uniform(20, 1950, 100).unwrap());
                    assert!(warm.filled.is_none());
                }
                let warm = segments(listed, &warm, None, (None, None));
                if *len > 1950 {
                    assert_eq!(warm.items, cold.items, "{len} {size}");
                    assert_eq!(warm.missing, cold.missing, "{len} {size}");
                    assert!(sizing.fetched > before);
                } else {
                    assert_eq!(warm.size(), 1950);
                    assert_eq!(warm.missing, [] as [u32; 0]);
                    assert_eq!(sizing.fetched, before);
                }
            }
        });
    }

    #[test]
    fn a_pending_volume_resolves_without_its_trailer_article() {
        use infinidysk_testkit::post;

        // The member's bytes all sit in the first article; the second, holding
        // only the volume's end-of-archive block, is gone from the provider.
        let volume = std::fs::read(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../archive/tests/fixtures/Rar5.multi.none.part02.rar"
        ))
        .unwrap();
        let mut articles = post("v", "v.part02.rar", &volume, 100_048);
        assert_eq!(articles.len(), 2);
        articles.pop();
        let pending = csharp::PendingPart {
            segment_ids: vec!["v-0@loopback".into(), "v-1@loopback".into()],
            segment_range: csharp::LongRange::default(),
            estimated_size: 0,
            fallbacks: None,
            proof: None,
        };
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let part = sizer(&client)
                .resolve(&pending, None, None, false)
                .await
                .unwrap();
            assert_eq!((part.data.start, part.data.end), (45, 100_045));
            assert_eq!(part.segments.size(), 100_053);
        });
    }

    #[test]
    fn a_pending_volume_measures_a_known_size_its_articles_cannot_hold() {
        use infinidysk_testkit::post;

        // Every article claims a size another volume of the folder had, too small
        // for this volume's three articles.
        let volume = std::fs::read(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../archive/tests/fixtures/Rar5.multi.none.part02.rar"
        ))
        .unwrap();
        let mut articles = post("v", "v.part02.rar", &volume, 50_000);
        assert_eq!(articles.len(), 3);
        for article in &mut articles {
            let line = article.wire.iter().position(|&b| b == b'\n').unwrap();
            let header = String::from_utf8(article.wire[..=line].to_vec()).unwrap();
            let claim = header.replace(&format!("size={}", volume.len()), "size=90000");
            article.wire.splice(..=line, claim.into_bytes());
        }
        let pending = csharp::PendingPart {
            segment_ids: articles.iter().map(|a| a.message_id.clone()).collect(),
            segment_range: csharp::LongRange::default(),
            estimated_size: 0,
            fallbacks: None,
            proof: None,
        };
        rt().block_on(async {
            let (_server, client) = loopback(articles).await;
            let mut sizing = sizer(&client);
            sizing.volumes.push(90_000);
            let part = sizing.resolve(&pending, None, None, false).await.unwrap();
            assert_eq!((part.data.start, part.data.end), (45, 100_045));
            assert_eq!(part.segments.size(), volume.len() as u64);
        });
    }
}
