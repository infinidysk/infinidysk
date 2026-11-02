//! Explorer API: browse the tree, inspect a file, act on it, and sign player links.

use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use axum::Json;
use axum::extract::{Path, Query, State};
use axum::http::{StatusCode, header};
use axum::response::{IntoResponse, Response};
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, blob, media};
use infinidysk_db::{
    FileQuery, FileRow, FileSort, HealthState, HistoryEntry, Item, JobStatus, ProbeOutcome,
};
use infinidysk_queue::names;
use infinidysk_vfs::{Kind, Node};
use serde::{Deserialize, Serialize};
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

use crate::admin::{Admin, ApiError, CheckRepair, CheckResult, Fail, HealthCheckView, ItemHealth};
use crate::probe::MediaInfo;

const DEFAULT_LIMIT: usize = 100;
const MAX_LIMIT: usize = 1000;
/// How long a player link works.
const LINK_SECS: u64 = 6 * 60 * 60;
/// Health checks shown per file.
const CHECKS: i64 = 20;

#[derive(Deserialize, ToSchema, Clone, Copy)]
#[serde(rename_all = "snake_case")]
pub enum StorageFilter {
    Directory,
    NzbFile,
    Multipart,
}

#[derive(Deserialize, ToSchema, Clone, Copy, Default)]
#[serde(rename_all = "snake_case")]
pub enum SortBy {
    #[default]
    Name,
    Size,
    Added,
}

#[derive(Deserialize, ToSchema, Clone, Copy, Default, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum SortOrder {
    #[default]
    Asc,
    Desc,
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
pub struct FilesQuery {
    /// Directory to list. Without it: every file under `/content`, flat.
    path: Option<String>,
    /// Case-insensitive part of the path (name only outside `/content`).
    search: Option<String>,
    #[param(inline)]
    kind: Option<StorageFilter>,
    category: Option<String>,
    /// Health state; hidden files are listed only when asked for.
    #[param(inline)]
    state: Option<ItemHealth>,
    /// Latest health check result.
    #[param(inline)]
    result: Option<CheckResult>,
    /// Latest health check repair outcome.
    #[param(inline)]
    repair: Option<CheckRepair>,
    /// Encrypted archive members only, or none of them.
    encrypted: Option<bool>,
    min_size: Option<u64>,
    max_size: Option<u64>,
    /// Unix milliseconds, inclusive.
    added_after: Option<i64>,
    /// Unix milliseconds, exclusive.
    added_before: Option<i64>,
    #[param(inline)]
    sort: Option<SortBy>,
    #[param(inline)]
    order: Option<SortOrder>,
    /// Default 100, at most 1000.
    limit: Option<usize>,
    offset: Option<usize>,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum EntryKind {
    Directory,
    File,
    Symlink,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum Storage {
    NzbFile,
    Multipart,
}

#[derive(Serialize, ToSchema)]
pub struct FileEntry {
    name: String,
    path: String,
    kind: EntryKind,
    /// The `/content` item behind this entry.
    item_id: Option<Uuid>,
    storage: Option<Storage>,
    /// Files only.
    size: Option<u64>,
    /// Unix milliseconds.
    added: Option<i64>,
    category: Option<String>,
    /// Files only.
    state: Option<ItemHealth>,
    /// Latest health check.
    health: Option<HealthCheckView>,
    /// Unix milliseconds.
    last_check: Option<i64>,
    next_check: Option<i64>,
    /// Link target.
    target: Option<String>,
    /// An encrypted archive member, decrypted on read.
    encrypted: bool,
}

#[derive(Serialize, ToSchema)]
pub struct FilePage {
    entries: Vec<FileEntry>,
    /// Matching entries across all pages.
    total: u64,
}

/// `/content/<category>/…` → `<category>`.
pub fn category(path: &str) -> Option<String> {
    let rest = path.strip_prefix("/content/")?;
    Some(rest.split('/').next()?.to_owned()).filter(|c| !c.is_empty())
}

fn storage(kind: ItemKind) -> Option<Storage> {
    match kind {
        ItemKind::Directory => None,
        ItemKind::NzbFile => Some(Storage::NzbFile),
        ItemKind::Multipart => Some(Storage::Multipart),
    }
}

fn item_entry(item: &Item, path: String, kind: EntryKind) -> FileEntry {
    let dir = item.kind == ItemKind::Directory;
    FileEntry {
        name: item.name.clone(),
        category: category(&item.path),
        path,
        kind,
        item_id: Some(item.id.0),
        storage: storage(item.kind),
        size: (!dir).then(|| item.size.unwrap_or(0)),
        added: Some(item.created_at),
        state: (!dir).then(|| item.health_state.into()),
        health: None,
        last_check: None,
        next_check: None,
        target: None,
        encrypted: item.encrypted,
    }
}

fn row_entry(row: FileRow) -> FileEntry {
    let kind = if row.item.kind == ItemKind::Directory {
        EntryKind::Directory
    } else {
        EntryKind::File
    };
    FileEntry {
        health: row.health.map(Into::into),
        last_check: row.last_health_check,
        next_check: row.next_health_check,
        ..item_entry(&row.item, row.item.path.clone(), kind)
    }
}

fn node_entry(node: &Node, parent: &str) -> FileEntry {
    let path = format!("{}/{}", parent.trim_end_matches('/'), node.name());
    let kind = match node.kind() {
        Kind::Dir => EntryKind::Directory,
        Kind::File => EntryKind::File,
        Kind::Symlink => EntryKind::Symlink,
    };
    match node {
        Node::Item(item) | Node::IdFile(item) | Node::Links(Some(item)) => {
            item_entry(item, path, kind)
        }
        Node::Link { item, target } => FileEntry {
            target: Some(target.clone()),
            ..item_entry(item, path, kind)
        },
        _ => FileEntry {
            name: node.name(),
            path,
            size: (node.kind() == Kind::File).then(|| node.size()),
            added: matches!(node, Node::Nzb(_)).then(|| node.modified()),
            kind,
            item_id: None,
            storage: None,
            category: None,
            state: None,
            health: None,
            last_check: None,
            next_check: None,
            target: None,
            encrypted: false,
        },
    }
}

/// `/a/b` without empty components; `/` for the root.
fn normalize(path: &str) -> String {
    let parts: Vec<_> = path.split('/').filter(|p| !p.is_empty()).collect();
    format!("/{}", parts.join("/"))
}

fn vfs_failed(error: &infinidysk_vfs::Error) -> Fail {
    tracing::warn!(reason = %error, "explorer request failed");
    Fail::Internal
}

/// One page of a directory, or of every file under `/content` when `path` is absent.
#[utoipa::path(get, path = "/api/v1/files", tag = "files", params(FilesQuery),
    responses((status = 200, body = FilePage), (status = 404, body = ApiError)))]
pub async fn get_files(
    State(admin): State<Arc<Admin>>,
    Query(q): Query<FilesQuery>,
) -> Result<Json<FilePage>, Fail> {
    let limit = q.limit.unwrap_or(DEFAULT_LIMIT).min(MAX_LIMIT);
    let offset = q.offset.unwrap_or(0);
    let path = q.path.as_deref().map(normalize);
    let parent = match path.as_deref() {
        None => None,
        Some(p) if p == "/content" || p.starts_with("/content/") => {
            let item = admin.vfs.db().item_by_path(p).await?;
            match item {
                Some(item) if item.kind == ItemKind::Directory => Some(item.id),
                _ => return Err(Fail::NotFound),
            }
        }
        Some(p) => return list_vfs(&admin, p, &q, limit, offset).await,
    };
    let search = q.search.as_deref().map(str::trim).filter(|s| !s.is_empty());
    let query = FileQuery {
        parent,
        files_only: parent.is_none(),
        kind: q.kind.map(|k| match k {
            StorageFilter::Directory => ItemKind::Directory,
            StorageFilter::NzbFile => ItemKind::NzbFile,
            StorageFilter::Multipart => ItemKind::Multipart,
        }),
        search,
        category: q.category.as_deref().filter(|c| !c.is_empty()),
        min_size: q.min_size,
        max_size: q.max_size,
        added_after: q.added_after,
        added_before: q.added_before,
        state: q.state.map(Into::into),
        result: q.result.map(Into::into),
        repair: q.repair.map(Into::into),
        encrypted: q.encrypted,
        sort: match q.sort.unwrap_or_default() {
            SortBy::Name => FileSort::Name,
            SortBy::Size => FileSort::Size,
            SortBy::Added => FileSort::Added,
        },
        descending: q.order == Some(SortOrder::Desc),
        limit: i64::try_from(limit).unwrap_or(i64::MAX),
        offset: i64::try_from(offset).unwrap_or(i64::MAX),
    };
    let (rows, total) = admin.vfs.db().files(&query).await?;
    Ok(Json(FilePage {
        entries: rows.into_iter().map(row_entry).collect(),
        total,
    }))
}

/// Directories outside `/content` are small or synthetic: list them whole, filter by
/// name, and page in memory.
async fn list_vfs(
    admin: &Admin,
    path: &str,
    q: &FilesQuery,
    limit: usize,
    offset: usize,
) -> Result<Json<FilePage>, Fail> {
    let node = admin.vfs.lookup(path).await.map_err(|e| vfs_failed(&e))?;
    let Some(node) = node.filter(|n| n.kind() == Kind::Dir) else {
        return Err(Fail::NotFound);
    };
    let search = q.search.as_deref().map(|s| s.trim().to_lowercase());
    let mut nodes: Vec<Node> = admin
        .vfs
        .list(&node)
        .await
        .map_err(|e| vfs_failed(&e))?
        .into_iter()
        .filter(|n| {
            search
                .as_deref()
                .is_none_or(|s| n.name().to_lowercase().contains(s))
        })
        .collect();
    nodes.sort_by_key(|n| (n.kind() != Kind::Dir, n.name().to_lowercase()));
    if q.order == Some(SortOrder::Desc) {
        nodes.reverse();
    }
    Ok(Json(FilePage {
        total: nodes.len() as u64,
        entries: nodes
            .iter()
            .skip(offset)
            .take(limit)
            .map(|n| node_entry(n, path))
            .collect(),
    }))
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum JobState {
    Completed,
    Failed,
}

#[derive(Serialize, ToSchema)]
pub struct JobView {
    id: Uuid,
    name: String,
    file_name: String,
    category: String,
    status: JobState,
    /// Unix milliseconds.
    created_at: i64,
}

impl From<HistoryEntry> for JobView {
    fn from(h: HistoryEntry) -> Self {
        Self {
            id: h.id.0,
            name: h.job_name,
            file_name: h.file_name,
            category: h.category,
            status: match h.status {
                JobStatus::Completed => JobState::Completed,
                JobStatus::Failed => JobState::Failed,
            },
            created_at: h.created_at,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct SegmentStats {
    segments: u64,
    /// Known missing when imported.
    missing: u64,
    corrupt: u64,
    /// Archive volumes the file spans; 1 for plain files.
    parts: u64,
}

#[derive(Serialize, ToSchema)]
pub struct Contents {
    files: u64,
    directories: u64,
    bytes: u64,
}

#[derive(Serialize, ToSchema)]
pub struct FileDetails {
    entry: FileEntry,
    /// Newest first.
    checks: Vec<HealthCheckView>,
    /// The download this belongs to.
    job: Option<JobView>,
    /// Paths under the mount directory that reach this file.
    links: Vec<String>,
    segments: Option<SegmentStats>,
    /// Directories only.
    contents: Option<Contents>,
    has_nzb: bool,
    /// Media files only.
    media: Option<MediaView>,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum AnalysisOutcome {
    Discovered,
    /// The container holds no audio or video stream.
    NoStreams,
    /// The analyzer does not read this container.
    Unsupported,
    /// The attempt proved nothing; `reason` says why.
    Inconclusive,
}

impl From<ProbeOutcome> for AnalysisOutcome {
    fn from(outcome: ProbeOutcome) -> Self {
        match outcome {
            ProbeOutcome::Discovered => Self::Discovered,
            ProbeOutcome::NoStreams => Self::NoStreams,
            ProbeOutcome::Unsupported => Self::Unsupported,
            ProbeOutcome::Inconclusive => Self::Inconclusive,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct MediaView {
    /// ffprobe is installed, so analysis can run.
    available: bool,
    /// An analysis is queued; it runs while nothing streams or imports.
    pending: bool,
    /// Unix milliseconds of the latest attempt.
    attempted_at: Option<i64>,
    outcome: Option<AnalysisOutcome>,
    reason: Option<String>,
    /// From the latest successful attempt.
    info: Option<MediaInfo>,
    /// Unix milliseconds.
    info_at: Option<i64>,
    /// Container damage health checks saw.
    defect: Option<DefectView>,
}

#[derive(Serialize, ToSchema)]
pub struct DefectView {
    /// Where the broken box header starts.
    offset_bytes: u64,
    /// The size it claims.
    claimed_bytes: u64,
    /// Unix milliseconds of the first sighting.
    seen_at: i64,
}

/// The download whose directory holds `item`, if it is still in history.
async fn job_of(admin: &Admin, item: &Item) -> Result<Option<HistoryEntry>, Fail> {
    let job_dir: String = item.path.split_inclusive('/').take(4).collect();
    let db = admin.vfs.db();
    let Some((id, _)) = db.job_at(job_dir.trim_end_matches('/')).await? else {
        return Ok(None);
    };
    Ok(db.history_entry(id).await?)
}

/// Mount paths that reach `item`: its `/completed-symlinks` mirror while its download is
/// completed, and its `/.ids` entry.
fn links(admin: &Admin, item: &Item, job: Option<&HistoryEntry>) -> Vec<String> {
    let mut out = Vec::new();
    let mount = admin.live.mount_dir().trim_end_matches('/');
    if job.is_some_and(|j| j.status == JobStatus::Completed)
        && let Some(rest) = item.path.strip_prefix("/content")
    {
        out.push(format!("{mount}/completed-symlinks{rest}"));
    }
    if item.kind != ItemKind::Directory {
        out.push(admin.vfs.id_link(item.id));
    }
    out
}

async fn item(admin: &Admin, id: Uuid) -> Result<Item, Fail> {
    admin.vfs.db().item(ItemId(id)).await?.ok_or(Fail::NotFound)
}

/// Everything known about one `/content` item.
#[utoipa::path(get, path = "/api/v1/files/{id}", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 200, body = FileDetails), (status = 404, body = ApiError)))]
pub async fn get_file(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<FileDetails>, Fail> {
    let item = item(&admin, id).await?;
    let db = admin.vfs.db();
    let status = db
        .health(item.id)
        .await?
        .map(|(_, s)| s)
        .unwrap_or_default();
    let (last_check, next_check) = (status.last_check, status.next_check);
    let checks = db.item_checks(item.id, CHECKS).await?;
    let job = job_of(&admin, &item).await?;
    let segments = match item.meta_blob {
        Some(meta) => match db.blob(meta).await? {
            Some(bytes) => Some(segment_stats(&blob::decode_meta(&bytes).map_err(|e| {
                tracing::warn!(reason = %e, path = %item.path, "file metadata is unreadable");
                Fail::Internal
            })?)),
            None => None,
        },
        None => None,
    };
    let contents = if item.kind == ItemKind::Directory {
        let s = db.subtree_stats(item.id).await?;
        Some(Contents {
            files: s.files,
            directories: s.directories.saturating_sub(1),
            bytes: s.bytes,
        })
    } else {
        None
    };
    let kind = if item.kind == ItemKind::Directory {
        EntryKind::Directory
    } else {
        EntryKind::File
    };
    let entry = FileEntry {
        health: checks.first().cloned().map(Into::into),
        last_check,
        next_check,
        ..item_entry(&item, item.path.clone(), kind)
    };
    let media = if item.meta_blob.is_some() && media::is_analyzable(&item.name) {
        let probe = db.media_probe(item.id).await?;
        Some(MediaView {
            available: admin.health.analyzes(),
            pending: probe.requested_at.is_some(),
            attempted_at: probe.attempted_at,
            outcome: probe.outcome.map(Into::into),
            reason: probe.reason,
            info: probe.info.as_deref().and_then(MediaInfo::parse),
            info_at: probe.info_at,
            defect: probe.defect.map(|d| DefectView {
                offset_bytes: d.offset,
                claimed_bytes: d.size,
                seen_at: d.seen_at,
            }),
        })
    } else {
        None
    };
    Ok(Json(FileDetails {
        entry,
        checks: checks.into_iter().map(Into::into).collect(),
        links: links(&admin, &item, job.as_ref()),
        job: job.map(Into::into),
        segments,
        contents,
        has_nzb: item.nzb_blob.is_some(),
        media,
    }))
}

fn segment_stats(meta: &FileMeta) -> SegmentStats {
    let lists: Vec<_> = match meta {
        FileMeta::Nzb(f) => vec![&f.segments],
        FileMeta::Multipart(f) => f.parts.iter().map(|p| &p.segments).collect(),
    };
    SegmentStats {
        segments: lists.iter().map(|s| s.items.len() as u64).sum(),
        missing: lists.iter().map(|s| s.missing.len() as u64).sum(),
        corrupt: lists.iter().map(|s| s.corrupt.len() as u64).sum(),
        parts: lists.len() as u64,
    }
}

/// Queues a health check ahead of schedule; a file needing attention starts over as
/// unchecked. The result lands in `checks`.
#[utoipa::path(post, path = "/api/v1/files/{id}/check", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 202), (status = 400, body = ApiError), (status = 404, body = ApiError)))]
pub async fn post_check(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    let item = item(&admin, id).await?;
    if item.meta_blob.is_none() {
        return Err(Fail::Bad("only files can be checked".to_owned()));
    }
    request(admin.health.request_check(item.id).await, &item)
}

/// Starts a repair now, whatever the checks found: the Arr that imported the file is
/// asked for a replacement and the file is hidden meanwhile. The repair budget applies.
#[utoipa::path(post, path = "/api/v1/files/{id}/repair", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 202), (status = 400, body = ApiError), (status = 404, body = ApiError)))]
pub async fn post_repair(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    let item = item(&admin, id).await?;
    if item.meta_blob.is_none() {
        return Err(Fail::Bad("only files can be repaired".to_owned()));
    }
    request(admin.health.request_repair(item.id).await, &item)
}

/// Queues a media analysis with ffprobe; it runs while nothing streams or imports, and
/// the result lands in `media`.
#[utoipa::path(post, path = "/api/v1/files/{id}/analyze", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 202), (status = 400, body = ApiError), (status = 404, body = ApiError),
        (status = 409, body = ApiError), (status = 503, body = ApiError)))]
pub async fn post_analyze(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    let item = item(&admin, id).await?;
    if item.meta_blob.is_none() || !media::is_analyzable(&item.name) {
        return Err(Fail::Bad(
            "only audio and video files can be analyzed".to_owned(),
        ));
    }
    if matches!(
        item.health_state,
        HealthState::Repairing | HealthState::Hidden
    ) {
        return Err(Fail::Conflict("the file is being replaced".to_owned()));
    }
    if !admin.health.analyzes() {
        return Err(Fail::Unavailable("ffprobe is not installed".to_owned()));
    }
    request(admin.health.request_analysis(item.id).await, &item)
}

fn request(done: Result<bool, infinidysk_health::Error>, item: &Item) -> Result<StatusCode, Fail> {
    match done {
        Ok(true) => Ok(StatusCode::ACCEPTED),
        Ok(false) => Err(Fail::NotFound),
        Err(error) => {
            tracing::warn!(path = %item.path, reason = %error, "health request failed");
            Err(Fail::Internal)
        }
    }
}

/// The NZB the file was imported from.
#[utoipa::path(get, path = "/api/v1/files/{id}/nzb", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 200, content_type = "application/x-nzb", body = String),
        (status = 404, body = ApiError)))]
pub async fn get_nzb(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Response, Fail> {
    let item = item(&admin, id).await?;
    let Some(blob_id) = item.nzb_blob else {
        return Err(Fail::NotFound);
    };
    let name = match job_of(&admin, &item).await? {
        Some(job) => job.file_name,
        None => format!("{}.nzb", item.name),
    };
    nzb_download(admin.vfs.db(), blob_id, &name).await
}

/// A stored NZB as an attachment named `name`.
pub async fn nzb_download(
    db: &infinidysk_db::Db,
    blob_id: BlobId,
    name: &str,
) -> Result<Response, Fail> {
    let Some(bytes) = db.blob(blob_id).await? else {
        return Err(Fail::NotFound);
    };
    let nzb = blob::decode_nzb(&bytes).map_err(|e| {
        tracing::warn!(reason = %e, %name, "stored NZB is unreadable");
        Fail::Internal
    })?;
    let disposition = format!(
        "attachment; filename*=UTF-8''{}",
        percent_encoding::utf8_percent_encode(name, percent_encoding::NON_ALPHANUMERIC)
    );
    Ok((
        [
            (header::CONTENT_TYPE, "application/x-nzb".to_owned()),
            (header::CONTENT_DISPOSITION, disposition),
        ],
        nzb,
    )
        .into_response())
}

#[derive(Serialize, ToSchema)]
pub struct RemovalPreview {
    path: String,
    files: u64,
    directories: u64,
    bytes: u64,
    /// Mount paths that stop working; library links pointing at them break.
    links: Vec<String>,
    job: Option<JobView>,
    /// Why it may not be removed; `null` when it may.
    kept: Option<Kept>,
}

/// Why a folder stays.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum Kept {
    /// `/content` and the other folders at the top of the tree.
    Root,
    /// A category the download client offers; it would come back.
    Configured,
    /// A category folder with files below it.
    NotEmpty,
}

impl Kept {
    fn reason(self) -> &'static str {
        match self {
            Self::Root => "the top of the tree cannot be removed",
            Self::Configured => "the download client still offers this category",
            Self::NotEmpty => "category folders can only be removed once no files are left in them",
        }
    }
}

/// What removing an item takes with it.
#[utoipa::path(get, path = "/api/v1/files/{id}/removal", tag = "files",
    params(("id" = Uuid, Path)),
    responses((status = 200, body = RemovalPreview), (status = 404, body = ApiError)))]
pub async fn get_removal(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<RemovalPreview>, Fail> {
    let item = item(&admin, id).await?;
    let stats = admin.vfs.db().subtree_stats(item.id).await?;
    let job = job_of(&admin, &item).await?;
    let kept = kept(&admin, &item)
        .await
        .or_else(|| (is_category(&item) && stats.files > 0).then_some(Kept::NotEmpty));
    Ok(Json(RemovalPreview {
        files: stats.files,
        directories: stats.directories,
        bytes: stats.bytes,
        links: links(&admin, &item, job.as_ref()),
        job: job.map(Into::into),
        kept,
        path: item.path,
    }))
}

/// `/content/<category>`.
fn is_category(item: &Item) -> bool {
    item.parent == Some(ItemId::CONTENT)
}

/// Why `item` stays whatever is below it: the top two levels of the tree, except
/// categories the download client no longer offers (the queue recreates offered ones).
async fn kept(admin: &Admin, item: &Item) -> Option<Kept> {
    if item.path.split('/').filter(|p| !p.is_empty()).count() >= 3 {
        return None;
    }
    if !is_category(item) {
        return Some(Kept::Root);
    }
    let configured = admin
        .config
        .read()
        .await
        .settings()
        .sab
        .categories()
        .filter_map(names::sanitize)
        .any(|c| c == item.name);
    configured.then_some(Kept::Configured)
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
pub struct RemoveQuery {
    /// Also forget the download in history.
    #[serde(default)]
    forget_history: bool,
}

/// Removes an item and everything under it.
#[utoipa::path(delete, path = "/api/v1/files/{id}", tag = "files",
    params(("id" = Uuid, Path), RemoveQuery),
    responses((status = 204), (status = 400, body = ApiError), (status = 404, body = ApiError)))]
pub async fn delete_file(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Query(q): Query<RemoveQuery>,
) -> Result<StatusCode, Fail> {
    let item = item(&admin, id).await?;
    if let Some(kept) = kept(&admin, &item).await {
        return Err(Fail::Bad(kept.reason().to_owned()));
    }
    let job = if q.forget_history {
        job_of(&admin, &item).await?
    } else {
        None
    };
    let db = admin.vfs.db();
    if is_category(&item) {
        if !db.delete_without_files(item.id).await? {
            return Err(Fail::Bad(Kept::NotEmpty.reason().to_owned()));
        }
    } else {
        db.delete_item(item.id).await?;
    }
    if let Some(job) = job {
        db.delete_history(&[job.id]).await?;
    }
    tracing::info!(path = %item.path, "removed from the explorer");
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize, ToSchema)]
pub struct LinkRequest {
    /// Tree path, e.g. `/content/<category>/<job>/<file>`.
    path: String,
}

#[derive(Serialize, ToSchema)]
pub struct LinkView {
    /// `/view/…?token=…`, relative to the server.
    url: String,
    /// Unix milliseconds.
    expires_at: i64,
}

/// A `/view` link for one path that works without credentials for six hours.
#[utoipa::path(post, path = "/api/v1/links", tag = "files", request_body = LinkRequest,
    responses((status = 200, body = LinkView), (status = 400, body = ApiError)))]
pub async fn post_link(
    State(admin): State<Arc<Admin>>,
    Json(req): Json<LinkRequest>,
) -> Result<Json<LinkView>, Fail> {
    let path = normalize(&req.path);
    if path == "/" {
        return Err(Fail::Bad("path is required".to_owned()));
    }
    let key = admin
        .config
        .read()
        .await
        .settings()
        .auth
        .session_key
        .0
        .clone();
    if key.is_empty() {
        return Err(Fail::Internal);
    }
    let expires = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
        + LINK_SECS;
    let token = infinidysk_webdav::sign_view(&key, &path, expires);
    let encoded: String = path
        .split('/')
        .filter(|p| !p.is_empty())
        .flat_map(|p| {
            [
                "/".into(),
                percent_encoding::utf8_percent_encode(p, PATH).to_string(),
            ]
        })
        .collect();
    Ok(Json(LinkView {
        url: format!("/view{encoded}?token={token}"),
        expires_at: i64::try_from(expires * 1000).unwrap_or(i64::MAX),
    }))
}

/// Unreserved characters (RFC 3986) stay literal.
const PATH: &percent_encoding::AsciiSet = &percent_encoding::NON_ALPHANUMERIC
    .remove(b'-')
    .remove(b'.')
    .remove(b'_')
    .remove(b'~');
