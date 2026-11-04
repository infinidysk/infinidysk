//! Admin API handlers under `/api/v1/backups`.

use std::io;
use std::sync::Arc;

use axum::Json;
use axum::body::Body;
use axum::extract::{Path as UrlPath, State};
use axum::http::{HeaderMap, HeaderValue, Method, StatusCode, header};
use axum::response::{IntoResponse as _, Response};
use serde::Deserialize;
use tokio::io::{AsyncReadExt as _, AsyncSeekExt as _};
use tokio_util::io::ReaderStream;
use utoipa::ToSchema;

use super::files;
use super::{BackupView, BackupsView, NOTE_MAX, TaskAction};
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
