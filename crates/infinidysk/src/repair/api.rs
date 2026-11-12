//! Manual PAR2 actions stay distinct from the existing Arr replacement action.

use std::sync::Arc;

use axum::{
    Json,
    extract::{Path, Query, State},
    http::StatusCode,
};
use infinidysk_core::ItemId;
use infinidysk_db::{repair::Owner, repair_jobs::Job};
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;
use uuid::Uuid;

use crate::admin::{Admin, ApiError, Fail};

#[derive(Serialize, ToSchema)]
pub struct Par2Job {
    id: Uuid,
    state: &'static str,
    reason: String,
    attempts: i32,
    verify_only: bool,
    transferred_bytes: i64,
    stored_bytes: i64,
    updated_at: i64,
    duration_ms: i64,
    verified_bytes: i64,
    item_id: Option<Uuid>,
    file_path: Option<String>,
    next_at: i64,
    checked_articles: i64,
    total_articles: i64,
    completed_requests: i64,
    missing_requests: i64,
}

impl From<Job> for Par2Job {
    fn from(job: Job) -> Self {
        Self {
            id: job.id,
            state: match job.state {
                0 => "queued",
                1 => "running",
                2 if job.verify_only => "available",
                2 => "verified",
                3 => "repaired",
                4 => "deferred",
                5 if job.reason.starts_with("Retry limit reached:") => "failed",
                5 => "unavailable",
                6 => "cancelled",
                _ => "failed",
            },
            reason: job.reason,
            attempts: job.attempts,
            verify_only: job.verify_only,
            transferred_bytes: job.transferred_bytes,
            stored_bytes: job.stored_bytes,
            updated_at: job.updated_at,
            duration_ms: job.duration_ms.saturating_add(
                job.started_at
                    .filter(|_| job.state == 1)
                    .map_or(0, |started| {
                        infinidysk_db::now_ms().saturating_sub(started).max(0)
                    }),
            ),
            verified_bytes: job.verified_bytes,
            item_id: job.item_id,
            file_path: job.file_path,
            next_at: job.next_at,
            checked_articles: job.checked_articles,
            total_articles: job.total_articles,
            completed_requests: job.completed_requests,
            missing_requests: job.missing_requests,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct Par2Status {
    enabled: bool,
    repair: Option<Par2Job>,
    verification: Option<Par2Job>,
}

async fn owner(admin: &Admin, id: Uuid) -> Result<Owner, Fail> {
    let item = admin
        .queue
        .db()
        .item(ItemId(id))
        .await?
        .ok_or(Fail::NotFound)?;
    if item.meta_blob.is_none() {
        return Err(Fail::Bad("Only files support PAR2 work.".into()));
    }
    Ok(Owner::Item(item.id))
}

#[utoipa::path(get, path = "/api/v1/files/{id}/par2", tag = "files",
    params(("id" = Uuid, Path)), responses((status = 200, body = Par2Status), (status = 404, body = ApiError)))]
pub async fn get_par2_status(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<Par2Status>, Fail> {
    let owner = owner(&admin, id).await?;
    let db = admin.queue.db();
    Ok(Json(Par2Status {
        enabled: infinidysk_core::experiment("par2-repair"),
        repair: db.par2_result(owner, false).await?.map(Into::into),
        verification: db.par2_result(owner, true).await?.map(Into::into),
    }))
}

async fn request(admin: &Admin, id: Uuid, verify: bool) -> Result<StatusCode, Fail> {
    let owner = owner(admin, id).await?;
    if !infinidysk_core::experiment("par2-repair") {
        return Err(Fail::Conflict(
            "Enable PAR2 repair in Settings → Experiments and restart first.".into(),
        ));
    }
    let now = infinidysk_db::now_ms();
    let Some(job) = admin.queue.db().request_par2(owner, verify, now).await? else {
        return Err(Fail::Conflict(
            "The file has no original NZB needed for reconstruction.".into(),
        ));
    };
    admin.queue.db().restart_par2(job, now).await?;
    Ok(StatusCode::ACCEPTED)
}

#[utoipa::path(post, path = "/api/v1/files/{id}/par2", tag = "files",
    params(("id" = Uuid, Path)), responses((status = 202), (status = 404, body = ApiError), (status = 409, body = ApiError)))]
pub async fn post_par2_repair(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    request(&admin, id, false).await
}

#[utoipa::path(post, path = "/api/v1/files/{id}/verify", tag = "files",
    params(("id" = Uuid, Path)), responses((status = 202), (status = 404, body = ApiError), (status = 409, body = ApiError)))]
/// Check every article's availability with bounded body spot checks. Never downloads a full file for hashing.
pub async fn post_par2_verify(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    request(&admin, id, true).await
}

async fn cancel(admin: &Admin, id: Uuid, verify: bool) -> Result<StatusCode, Fail> {
    let owner = owner(admin, id).await?;
    if let Some(job) = admin.queue.db().par2_result(owner, verify).await? {
        admin
            .queue
            .db()
            .cancel_par2(job.id, infinidysk_db::now_ms())
            .await?;
    }
    Ok(StatusCode::NO_CONTENT)
}

#[utoipa::path(delete, path = "/api/v1/files/{id}/par2", tag = "files",
    params(("id" = Uuid, Path)), responses((status = 204), (status = 404, body = ApiError)))]
pub async fn delete_par2_repair(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    cancel(&admin, id, false).await
}

#[utoipa::path(delete, path = "/api/v1/files/{id}/verify", tag = "files",
    params(("id" = Uuid, Path)), responses((status = 204), (status = 404, body = ApiError)))]
pub async fn delete_par2_verify(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    cancel(&admin, id, true).await
}

#[derive(Serialize, ToSchema)]
pub struct Par2ScanStatus {
    enabled: bool,
    pending_jobs: i64,
    scan: Option<super::scan::Scan>,
    jobs: Vec<Par2Job>,
    counts: Vec<Par2Count>,
    storage_used_bytes: u64,
    limits: crate::config::Repair,
}

#[derive(Serialize, ToSchema)]
pub struct Par2Count {
    state: i16,
    count: i64,
}

#[utoipa::path(get, path = "/api/v1/health/par2-scan", tag = "health",
    responses((status = 200, body = Par2ScanStatus), (status = 500, body = ApiError)))]
pub async fn get_par2_scan(State(admin): State<Arc<Admin>>) -> Result<Json<Par2ScanStatus>, Fail> {
    Ok(Json(Par2ScanStatus {
        enabled: infinidysk_core::experiment("par2-repair"),
        pending_jobs: admin.queue.db().par2_pending().await?,
        scan: super::scan::load(admin.queue.db())
            .await
            .map_err(|e| Fail::Failed(e.to_string()))?,
        jobs: admin
            .queue
            .db()
            .par2_jobs()
            .await?
            .into_iter()
            .map(Into::into)
            .collect(),
        counts: admin
            .queue
            .db()
            .par2_counts()
            .await?
            .into_iter()
            .map(|(state, count)| Par2Count { state, count })
            .collect(),
        storage_used_bytes: admin
            .repairs
            .used_bytes()
            .await
            .map_err(|e| Fail::Failed(e.to_string()))?,
        limits: admin.config.read().await.settings().repair.clone(),
    }))
}

#[derive(Default, Deserialize)]
pub struct ScanOptions {
    #[serde(default)]
    repair: bool,
}

#[utoipa::path(post, path = "/api/v1/health/par2-scan", tag = "health",
    params(("repair" = Option<bool>, Query, description = "Check and repair source sets instead of read-only verification")),
    responses((status = 202), (status = 409, body = ApiError)))]
pub async fn post_par2_scan(
    State(admin): State<Arc<Admin>>,
    Query(options): Query<ScanOptions>,
) -> Result<StatusCode, Fail> {
    if !infinidysk_core::experiment("par2-repair") {
        return Err(Fail::Conflict(
            "Enable PAR2 repair in Settings → Experiments and restart first.".into(),
        ));
    }
    if super::scan::load(admin.queue.db())
        .await
        .map_err(|e| Fail::Failed(e.to_string()))?
        .is_some_and(|scan| !scan.complete)
    {
        return Ok(StatusCode::ACCEPTED);
    }
    let requested_at = infinidysk_db::now_ms();
    let boundary = admin.queue.db().par2_scan_boundary().await?;
    let total_files = if let Some(boundary) = boundary {
        Some(
            u64::try_from(
                admin
                    .queue
                    .db()
                    .par2_scan_total(boundary, requested_at)
                    .await?,
            )
            .unwrap_or(0),
        )
    } else {
        Some(0)
    };
    let scan = super::scan::Scan {
        boundary,
        cursor: None,
        requested_at,
        scanned_files: 0,
        complete: false,
        cancelled: false,
        repair: options.repair,
        total_files,
        skipped_files: 0,
    };
    super::scan::save(admin.queue.db(), &scan)
        .await
        .map_err(|e| Fail::Failed(e.to_string()))?;
    Ok(StatusCode::ACCEPTED)
}

#[utoipa::path(delete, path = "/api/v1/health/par2-jobs/{id}", tag = "health",
    params(("id" = Uuid, Path)), responses((status = 204), (status = 500, body = ApiError)))]
pub async fn delete_par2_job(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    admin
        .queue
        .db()
        .cancel_par2(id, infinidysk_db::now_ms())
        .await?;
    Ok(StatusCode::NO_CONTENT)
}

#[utoipa::path(delete, path = "/api/v1/health/par2-scan", tag = "health",
    responses((status = 204), (status = 500, body = ApiError)))]
pub async fn delete_par2_scan(State(admin): State<Arc<Admin>>) -> Result<StatusCode, Fail> {
    if let Some(mut scan) = super::scan::load(admin.queue.db())
        .await
        .map_err(|e| Fail::Failed(e.to_string()))?
    {
        scan.complete = true;
        scan.cancelled = true;
        super::scan::save(admin.queue.db(), &scan)
            .await
            .map_err(|e| Fail::Failed(e.to_string()))?;
    }
    Ok(StatusCode::NO_CONTENT)
}

#[cfg(test)]
mod tests {
    #[test]
    fn exhausted_retries_are_failed_and_terminal_duration_stops_growing() {
        for (reason, expected) in [
            ("Retry limit reached: provider timed out", "failed"),
            ("repair unavailable: no recovery data", "unavailable"),
        ] {
            let view = super::Par2Job::from(infinidysk_db::repair_jobs::Job {
                id: uuid::Uuid::nil(),
                nzb_blob: None,
                item_id: None,
                meta_blob: None,
                meta_generation: None,
                generation: 0,
                verify_only: false,
                state: 5,
                attempts: 3,
                reason: reason.into(),
                transferred_bytes: 0,
                stored_bytes: 0,
                updated_at: 1,
                duration_ms: 100,
                verified_bytes: 0,
                started_at: Some(0),
                next_at: 1,
                scan_at: None,
                repair_on_failure: false,
                checked_articles: 0,
                total_articles: 0,
                completed_requests: 0,
                missing_requests: 0,
                file_path: None,
            });
            assert_eq!(view.state, expected);
            assert_eq!(view.duration_ms, 100);
        }
    }

    #[test]
    fn repair_api_keeps_operation_names_unique_and_preserves_server_status() {
        let spec: serde_json::Value =
            serde_json::from_str(&crate::admin::openapi_json().unwrap()).unwrap();
        let mut names = std::collections::HashSet::new();
        for path in spec["paths"].as_object().unwrap().values() {
            for operation in path.as_object().unwrap().values() {
                if let Some(name) = operation["operationId"].as_str() {
                    assert!(names.insert(name), "duplicate operation: {name}");
                }
            }
        }
        let status = &spec["paths"]["/api/v1/status"]["get"]["parameters"];
        assert!(status.as_array().is_none_or(Vec::is_empty));
    }
}
