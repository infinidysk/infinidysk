//! Admin API handlers under `/api/v1/backups`.

use std::io;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use axum::Json;
use axum::body::Body;
use axum::extract::{Path as UrlPath, Query, State};
use axum::http::{HeaderMap, HeaderValue, Method, StatusCode, header};
use axum::response::{IntoResponse as _, Response};
use futures_util::StreamExt as _;
use infinidysk_db::now_ms;
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _, AsyncWriteExt as _};
use tokio_util::io::ReaderStream;
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

use super::files::{self, Kind, Manifest, Part};
use super::{
    AttemptAction, AttemptState, BackupView, BackupsView, NOTE_MAX, POSTGRES, TaskAction,
    UNFINISHED, boot,
};
use crate::admin::{Admin, ApiError, Fail};

#[derive(Deserialize, ToSchema)]
pub struct BackupPatch {
    pinned: Option<bool>,
    /// Trimmed; at most 500 characters.
    note: Option<String>,
}

fn check_name(name: &str) -> Result<(), Fail> {
    if files::valid_name(name) {
        Ok(())
    } else {
        Err(Fail::Bad("This is not a backup name.".to_owned()))
    }
}

fn io_fail(error: &io::Error) -> Fail {
    if error.kind() == io::ErrorKind::NotFound {
        Fail::NotFound
    } else {
        tracing::warn!(reason = %error, "backup request failed");
        Fail::Internal
    }
}

/// The backups, the folder and what restore left behind.
#[utoipa::path(get, path = "/api/v1/backups", tag = "backups",
    responses((status = 200, body = BackupsView)))]
pub async fn get_backups(State(admin): State<Arc<Admin>>) -> Json<BackupsView> {
    Json(admin.backups.view().await)
}

/// Downloads one backup; supports `Range` and `If-Range`.
#[utoipa::path(get, path = "/api/v1/backups/{name}/file", tag = "backups",
    operation_id = "get_backup_file",
    params(("name" = String, Path)),
    responses((status = 200, content_type = "application/vnd.sqlite3", body = Vec<u8>),
        (status = 206, content_type = "application/vnd.sqlite3", body = Vec<u8>),
        (status = 404, body = ApiError), (status = 416)))]
pub async fn get_file(
    State(admin): State<Arc<Admin>>,
    UrlPath(name): UrlPath<String>,
    method: Method,
    mut headers: HeaderMap,
) -> Result<Response, Fail> {
    let dir = admin.backups.dir().await.map_err(Fail::Conflict)?;
    let entry = files::blocking(move || files::find(&dir, &name))
        .await
        .map_err(|e| io_fail(&e))?;
    let size = entry.size_bytes;
    let etag = entry
        .manifest
        .as_ref()
        .and_then(|m| m.sha256.as_deref())
        .map_or_else(
            || format!("\"{size}-{}\"", entry.mtime_ms),
            |sha| format!("\"sha256-{sha}\""),
        );
    if headers
        .get(header::IF_RANGE)
        .is_some_and(|v| v.as_bytes() != etag.as_bytes())
    {
        headers.remove(header::RANGE);
    }
    let mut out = HeaderMap::new();
    out.insert(header::ACCEPT_RANGES, HeaderValue::from_static("bytes"));
    out.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    if let Ok(value) = HeaderValue::try_from(etag) {
        out.insert(header::ETAG, value);
    }
    let (status, start, end) = match infinidysk_webdav::range(&headers, size) {
        Ok(Some((start, end))) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes {start}-{}/{size}", end - 1)) {
                out.insert(header::CONTENT_RANGE, value);
            }
            (StatusCode::PARTIAL_CONTENT, start, end)
        }
        Ok(None) if !headers.contains_key(header::RANGE) => (StatusCode::OK, 0, size),
        // A range this server does not serve one-to-one.
        Ok(None) => return Ok(StatusCode::BAD_REQUEST.into_response()),
        Err(()) => {
            if let Ok(value) = HeaderValue::try_from(format!("bytes */{size}")) {
                out.insert(header::CONTENT_RANGE, value);
            }
            return Ok((StatusCode::RANGE_NOT_SATISFIABLE, out).into_response());
        }
    };
    out.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/vnd.sqlite3"),
    );
    // The name rule keeps the name safe inside quotes.
    if let Ok(value) = HeaderValue::try_from(format!("attachment; filename=\"{}\"", entry.name)) {
        out.insert(header::CONTENT_DISPOSITION, value);
    }
    out.insert(header::CONTENT_LENGTH, HeaderValue::from(end - start));
    if method == Method::HEAD {
        return Ok((status, out).into_response());
    }
    let mut file = tokio::fs::File::open(&entry.path)
        .await
        .map_err(|e| io_fail(&e))?;
    file.seek(io::SeekFrom::Start(start))
        .await
        .map_err(|e| io_fail(&e))?;
    let body = Body::from_stream(ReaderStream::with_capacity(file.take(end - start), 1 << 20));
    Ok((status, out, body).into_response())
}

/// Pins a backup or sets its note.
#[utoipa::path(patch, path = "/api/v1/backups/{name}", tag = "backups",
    params(("name" = String, Path)), request_body = BackupPatch,
    responses((status = 200, body = BackupView), (status = 400, body = ApiError),
        (status = 404, body = ApiError)))]
pub async fn patch_backup(
    State(admin): State<Arc<Admin>>,
    UrlPath(name): UrlPath<String>,
    Json(body): Json<BackupPatch>,
) -> Result<Json<BackupView>, Fail> {
    check_name(&name)?;
    let dir = admin.backups.dir().await.map_err(Fail::Conflict)?;
    let note = body.note.map(|n| {
        n.trim()
            .chars()
            .filter(|c| !c.is_control())
            .take(NOTE_MAX)
            .collect::<String>()
    });
    let pinned = body.pinned;
    files::update_manifest(&admin.backups.manifests, &dir, &name, move |m| {
        if let Some(pinned) = pinned {
            m.pinned = pinned;
        }
        if let Some(note) = note {
            m.note = note;
        }
    })
    .await
    .map_err(|e| io_fail(&e))?;
    let entry = files::blocking(move || files::find(&dir, &name))
        .await
        .map_err(|e| io_fail(&e))?;
    Ok(Json(BackupView::of(&entry)))
}

/// Deletes one backup and its manifest.
#[utoipa::path(delete, path = "/api/v1/backups/{name}", tag = "backups",
    params(("name" = String, Path)),
    responses((status = 204), (status = 404, body = ApiError),
        (status = 409, body = ApiError, description = "backup work is running")))]
pub async fn delete_backup(
    State(admin): State<Arc<Admin>>,
    UrlPath(name): UrlPath<String>,
) -> Result<StatusCode, Fail> {
    check_name(&name)?;
    if admin.maintenance.backup_busy() {
        return Err(Fail::Conflict("A backup task is running.".to_owned()));
    }
    let dir = admin.backups.dir().await.map_err(Fail::Conflict)?;
    files::delete(&admin.backups.manifests, &dir, &name)
        .await
        .map_err(|e| io_fail(&e))?;
    admin.backups.refresh_newest(dir).await;
    Ok(StatusCode::NO_CONTENT)
}

/// Compares a backup with its checksum and checks it, in the maintenance slot.
#[utoipa::path(post, path = "/api/v1/backups/{name}/verify", tag = "backups",
    params(("name" = String, Path)),
    responses((status = 202), (status = 404, body = ApiError),
        (status = 409, body = ApiError, description = "a job is running")))]
pub async fn post_verify(
    State(admin): State<Arc<Admin>>,
    UrlPath(name): UrlPath<String>,
) -> Result<StatusCode, Fail> {
    check_name(&name)?;
    let dir = admin.backups.dir().await.map_err(Fail::Conflict)?;
    {
        let name = name.clone();
        files::blocking(move || files::find(&dir, &name))
            .await
            .map_err(|e| io_fail(&e))?;
    }
    let (slot, cancel) = admin.maintenance.claim("Verifying a backup")?;
    let backups = Arc::clone(&admin.backups);
    tokio::spawn(async move {
        let _slot = slot;
        if let Err(reason) = backups.verify(&name, &cancel).await {
            backups.fail_task(TaskAction::Verify, &name, &reason);
        }
    });
    Ok(StatusCode::ACCEPTED)
}

/// Deletes the 1.x backup folders.
#[utoipa::path(delete, path = "/api/v1/backups/legacy", tag = "backups",
    responses((status = 204)))]
pub async fn delete_legacy(State(admin): State<Arc<Admin>>) -> Result<StatusCode, Fail> {
    let config_dir = admin.backups.parts.config_dir.clone();
    let removed = files::blocking(move || files::delete_legacy(&config_dir))
        .await
        .map_err(|e| io_fail(&e))?;
    tracing::info!(folders = removed, "deleted 1.x backups");
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Serialize, ToSchema)]
pub struct AttemptId {
    attempt: Uuid,
}

/// Restarts the server; the restore or undo runs before the database opens.
fn restart(admin: &Admin) {
    admin.restart.store(true, Ordering::Relaxed);
    admin.cancel.cancel();
}

/// Copies and checks a backup, then restarts into it.
#[utoipa::path(post, path = "/api/v1/backups/{name}/restore", tag = "backups",
    params(("name" = String, Path)),
    responses((status = 202, body = AttemptId), (status = 404, body = ApiError),
        (status = 409, body = ApiError)))]
pub async fn post_restore(
    State(admin): State<Arc<Admin>>,
    UrlPath(name): UrlPath<String>,
) -> Result<(StatusCode, Json<AttemptId>), Fail> {
    check_name(&name)?;
    let backups = &admin.backups;
    let paths = backups
        .paths()
        .ok_or_else(|| Fail::Conflict(POSTGRES.to_owned()))?;
    if paths.intent().exists() {
        return Err(Fail::Conflict(UNFINISHED.to_owned()));
    }
    let dir = backups.dir().await.map_err(Fail::Conflict)?;
    let entry = {
        let (dir, name) = (dir.clone(), name.clone());
        files::blocking(move || files::find(&dir, &name))
            .await
            .map_err(|e| io_fail(&e))?
    };
    if entry.incomplete {
        return Err(Fail::Conflict("This backup is incomplete.".to_owned()));
    }
    if super::newer(entry.manifest.as_ref().and_then(|m| m.schema_version)) {
        return Err(Fail::Conflict(
            "This backup was made by a newer infinidysk; update infinidysk first.".to_owned(),
        ));
    }
    files::room(
        infinidysk_stream::free_space(paths.dir()),
        entry.size_bytes,
        "The database folder",
        "Delete the undo copy or free some space.",
    )
    .map_err(Fail::Conflict)?;
    let (slot, cancel) = admin.maintenance.claim("Restoring: copying the backup")?;
    let id = Uuid::new_v4();
    backups.set_attempt(id, AttemptAction::Restore, AttemptState::Staging, None);
    let admin = Arc::clone(&admin);
    tokio::spawn(async move {
        let backups = &admin.backups;
        let staged = backups.stage_restore(&dir, entry, id, &cancel).await;
        drop(slot);
        match staged {
            Ok(()) => {
                backups.set_attempt(id, AttemptAction::Restore, AttemptState::Restarting, None);
                tracing::info!(backup = %name, "restore ready; restarting");
                restart(&admin);
            }
            Err(reason) => {
                backups.set_attempt(
                    id,
                    AttemptAction::Restore,
                    AttemptState::Failed,
                    Some(reason.clone()),
                );
                backups.fail_task(TaskAction::Restore, &name, &reason);
            }
        }
    });
    Ok((StatusCode::ACCEPTED, Json(AttemptId { attempt: id })))
}

/// Puts back the database the last restore replaced, at the next start.
#[utoipa::path(post, path = "/api/v1/backups/undo", tag = "backups",
    responses((status = 202, body = AttemptId), (status = 409, body = ApiError),
        (status = 500, body = ApiError)))]
pub async fn post_undo(
    State(admin): State<Arc<Admin>>,
) -> Result<(StatusCode, Json<AttemptId>), Fail> {
    let backups = &admin.backups;
    let paths = backups
        .paths()
        .ok_or_else(|| Fail::Conflict(POSTGRES.to_owned()))?;
    let (slot, _cancel) = admin.maintenance.claim("Undoing the restore")?;
    if paths.intent().exists() {
        return Err(Fail::Conflict(UNFINISHED.to_owned()));
    }
    if !paths.undo().exists() {
        return Err(Fail::Conflict("There is no undo copy.".to_owned()));
    }
    let id = Uuid::new_v4();
    let staged = backups.stage_undo(id).await;
    drop(slot);
    match staged {
        Ok(()) => {
            backups.set_attempt(id, AttemptAction::Undo, AttemptState::Restarting, None);
            tracing::info!("undo ready; restarting");
            restart(&admin);
            Ok((StatusCode::ACCEPTED, Json(AttemptId { attempt: id })))
        }
        Err(reason) => {
            backups.set_attempt(
                id,
                AttemptAction::Undo,
                AttemptState::Failed,
                Some(reason.clone()),
            );
            backups.fail_task(TaskAction::Undo, "", &reason);
            Err(Fail::Failed(reason))
        }
    }
}

/// Deletes the undo copy.
#[utoipa::path(delete, path = "/api/v1/backups/undo", tag = "backups",
    responses((status = 204), (status = 409, body = ApiError)))]
pub async fn delete_undo(State(admin): State<Arc<Admin>>) -> Result<StatusCode, Fail> {
    let paths = admin
        .backups
        .paths()
        .ok_or_else(|| Fail::Conflict(POSTGRES.to_owned()))?;
    let (slot, _cancel) = admin.maintenance.claim("Deleting the undo copy")?;
    if paths.intent().exists() {
        return Err(Fail::Conflict(UNFINISHED.to_owned()));
    }
    files::blocking(move || {
        for suffix in ["-journal", "-shm", "-wal", ""] {
            files::remove(&files::with(&paths.undo(), suffix))?;
        }
        Ok(())
    })
    .await
    .map_err(|e| io_fail(&e))?;
    drop(slot);
    Ok(StatusCode::NO_CONTENT)
}

/// Dismisses the last restore notice.
#[utoipa::path(delete, path = "/api/v1/backups/last-restore", tag = "backups",
    responses((status = 204)))]
pub async fn delete_last_restore(State(admin): State<Arc<Admin>>) -> Result<StatusCode, Fail> {
    admin
        .backups
        .parts
        .db
        .write_config(&[], &[boot::LAST_RESTORE_KEY])
        .await?;
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
pub struct UploadQuery {
    /// The file's original name, kept as `source_name`.
    name: Option<String>,
}

const NOT_SQLITE: &str = "This is not a SQLite database file.";

/// Stores an uploaded database file as a backup. It is checked when it is restored or
/// verified.
#[utoipa::path(put, path = "/api/v1/backups/upload", tag = "backups",
    params(UploadQuery),
    request_body(content = Vec<u8>, content_type = "application/octet-stream"),
    responses((status = 201, body = BackupView), (status = 400, body = ApiError),
        (status = 409, body = ApiError)))]
pub async fn put_upload(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<UploadQuery>,
    headers: HeaderMap,
    body: Body,
) -> Result<(StatusCode, Json<BackupView>), Fail> {
    let backups = &admin.backups;
    if let Some(reason) = backups.unavailable() {
        return Err(Fail::Conflict(reason.to_owned()));
    }
    let _flag = backups
        .start_upload()
        .ok_or_else(|| Fail::Conflict("Another upload is running.".to_owned()))?;
    let len: u64 = headers
        .get(header::CONTENT_LENGTH)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.parse().ok())
        .ok_or_else(|| Fail::Bad("The upload needs a Content-Length.".to_owned()))?;
    let dir = backups.dir().await.map_err(Fail::Conflict)?;
    files::room(
        infinidysk_stream::free_space(&dir),
        len,
        "The backup folder",
        "Delete old backups or free some space.",
    )
    .map_err(Fail::Conflict)?;
    let name = files::new_name(&dir, Kind::Uploaded);
    let (part, file) = Part::create(dir.join(format!(".{name}.part"))).map_err(|e| io_fail(&e))?;
    let mut file = tokio::fs::File::from_std(file);
    let mut hasher = Sha256::new();
    let mut head: Vec<u8> = Vec::with_capacity(100);
    let mut written = 0u64;
    let mut stream = body.into_data_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| Fail::Bad(format!("The upload broke off: {e}")))?;
        written += chunk.len() as u64;
        if written > len {
            return Err(Fail::Bad(
                "The upload is longer than its Content-Length.".to_owned(),
            ));
        }
        if head.len() < 100 {
            let take = chunk.len().min(100 - head.len());
            head.extend_from_slice(&chunk[..take]);
            if head.len() >= 16 && !files::is_sqlite(&head) {
                return Err(Fail::Bad(NOT_SQLITE.to_owned()));
            }
        }
        hasher.update(&chunk);
        file.write_all(&chunk).await.map_err(|e| io_fail(&e))?;
    }
    if head.len() < 16 {
        return Err(Fail::Bad(NOT_SQLITE.to_owned()));
    }
    let vouched = <&[u8; 100]>::try_from(head.as_slice())
        .ok()
        .and_then(files::header)
        .filter(|&(_, pages)| pages > 0);
    if written != len || vouched.is_some_and(|(page, pages)| page * pages != len) {
        return Err(Fail::Bad("The file is incomplete.".to_owned()));
    }
    file.sync_all().await.map_err(|e| io_fail(&e))?;
    drop(file);
    let source_name = query
        .name
        .map(|n| {
            n.chars()
                .filter(|c| !c.is_control())
                .take(200)
                .collect::<String>()
        })
        .filter(|n| !n.trim().is_empty());
    let manifest = Manifest {
        format: files::MANIFEST_FORMAT,
        kind: Kind::Uploaded,
        created_ms: now_ms(),
        sha256: Some(files::hex(&hasher.finalize())),
        size_bytes: Some(len),
        source_name,
        ..Manifest::default()
    };
    files::publish(&backups.manifests, &dir, part, &name, &manifest)
        .await
        .map_err(|e| io_fail(&e))?;
    let owner = backups.parts.owner;
    files::own(&dir.join(&name), owner);
    files::own(&files::manifest_path(&dir, &name), owner);
    tracing::info!(backup = %name, size_bytes = len, "backup uploaded");
    let entry = files::blocking(move || files::find(&dir, &name))
        .await
        .map_err(|e| io_fail(&e))?;
    Ok((StatusCode::CREATED, Json(BackupView::of(&entry))))
}
