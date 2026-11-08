//! Admin API for the UI under `/api/v1`, behind [`auth::require`]. `/api/v1/openapi.json`
//! describes it.

use std::convert::Infallible;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant, SystemTime};

use axum::extract::{DefaultBodyLimit, Multipart, Path, Query, State};
use axum::http::{StatusCode, header};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post, put};
use axum::{Json, Router};
use futures_util::{Stream, stream};
use infinidysk_core::JobId;
use infinidysk_core::trace::Summary;
use infinidysk_db::{
    Change, HealthCheck, HealthFilter, HealthResult, HealthState, HistoryQuery, JobStatus, Recheck,
    Repair,
};
use infinidysk_health::Health;
use infinidysk_nntp::{Client, catalog};
use infinidysk_queue::{Queue, Submission, service};
use infinidysk_vfs::{Delivery, Session, Tracing, Vfs};
use serde::{Deserialize, Serialize};
use serde_json::json;
use tokio::sync::broadcast::error::RecvError;
use tokio::sync::{RwLock, watch};
use tokio_util::sync::CancellationToken;
use utoipa::openapi::security::{ApiKey, ApiKeyValue, SecurityScheme};
use utoipa::{IntoParams, Modify, OpenApi, ToSchema};
use uuid::Uuid;

use crate::app::Live;
use crate::arr;
use crate::auth::{self, Auth};
use crate::backup;
use crate::config::{self, ConfigStore, ImportStrategy, Settings};
use crate::database;
use crate::files;
use crate::library;
use crate::logs::{self, Entry, Level};
use crate::maintenance;
use crate::metrics::{self, Metrics, unix_ms};
use crate::provider_status;
use crate::provider_test;
use crate::quota;
use crate::release_notes;
use crate::sab;
use crate::upgrade;

mod support;

const DEFAULT_PAGE: usize = 500;
const MAX_PAGE: usize = 10_000;

pub struct Admin {
    pub config: Arc<RwLock<ConfigStore>>,
    pub queue: Arc<Queue>,
    pub client: Arc<Client>,
    pub vfs: Arc<Vfs>,
    pub health: Arc<Health>,
    pub live: Live,
    pub metrics: Arc<Metrics>,
    pub arr: Arc<arr::Discovery>,
    pub tests: provider_test::Tests,
    /// `Ok` while FUSE is mounted, otherwise why not.
    pub mount: watch::Receiver<Result<infinidysk_vfs::Kernel, String>>,
    pub library: Arc<crate::library::Library>,
    pub maintenance: Arc<crate::maintenance::Maintenance>,
    pub backups: Arc<crate::backup::Backups>,
    pub updates: Arc<crate::update::Updates>,
    pub database: Arc<crate::database::Migrator>,
    /// Ends event streams at shutdown.
    pub cancel: CancellationToken,
    /// Set before `cancel` when the process should start again.
    pub restart: Arc<AtomicBool>,
}

#[derive(OpenApi)]
#[openapi(
    info(title = "infinidysk admin API"),
    paths(
        get_settings,
        put_settings,
        get_key,
        get_catalog,
        provider_status::post_provider_check,
        provider_status::get_provider_status,
        provider_test::post_speed_test,
        provider_test::post_tune_pipeline,
        provider_test::get_provider_tests,
        provider_test::delete_provider_tests,
        quota::get_provider_usage,
        quota::post_reset_usage,
        get_queue,
        post_queue,
        get_queue_paused,
        put_queue_paused,
        put_queue_job_paused,
        delete_queue_job,
        get_queue_nzb,
        get_history,
        delete_history_job,
        get_history_nzb,
        post_retry,
        get_health,
        get_health_summary,
        get_health_schedule,
        get_rejected_releases,
        files::get_files,
        files::get_file,
        files::post_check,
        files::post_repair,
        files::post_analyze,
        files::get_nzb,
        files::get_removal,
        files::delete_file,
        files::post_link,
        get_events,
        get_status,
        release_notes::get_release_notes,
        post_restart,
        get_arr_status,
        post_arr_discover,
        post_arr_test,
        get_reconcile,
        post_reconcile_confirm,
        delete_reconcile,
        get_performance,
        get_logs,
        support::post_support_pack,
        get_reads,
        get_tracing,
        put_tracing,
        get_traces,
        get_trace,
        export_trace,
        metrics::get_overview,
        metrics::get_file_reads,
        metrics::get_live,
        metrics::get_prometheus,
        auth::status,
        auth::setup,
        auth::login,
        auth::logout,
        auth::change_password,
        upgrade::get_upgrade_status,
        upgrade::get_upgrade_report,
        upgrade::post_upgrade_report_seen,
        upgrade::post_upgrade_retry,
        library::get_library,
        library::post_library_preview,
        library::delete_library_preview,
        library::post_library_confirm,
        library::post_library_cancel,
        library::post_library_check,
        library::post_library_revoke_keys,
        library::post_library_legacy_stop,
        maintenance::get_maintenance,
        maintenance::post_run,
        maintenance::post_cancel,
        maintenance::get_report,
        maintenance::post_confirm,
        maintenance::delete_report,
        database::get_database,
        database::post_database_check,
        database::post_migration,
        database::delete_migration,
        database::post_cutover,
        database::get_cutover_none,
        backup::api::get_backups,
        backup::api::get_file,
        backup::api::patch_backup,
        backup::api::delete_backup,
        backup::api::post_verify,
        backup::api::delete_legacy,
        backup::api::post_restore,
        backup::api::post_undo,
        backup::api::delete_undo,
        backup::api::delete_last_restore,
        backup::api::put_upload,
    ),
    modifiers(&Security),
    security(("session" = []), ("api_key" = []))
)]
struct ApiDoc;

struct Security;

impl Modify for Security {
    fn modify(&self, openapi: &mut utoipa::openapi::OpenApi) {
        let components = openapi.components.get_or_insert_with(Default::default);
        components.add_security_scheme(
            "session",
            SecurityScheme::ApiKey(ApiKey::Cookie(ApiKeyValue::new(auth::COOKIE))),
        );
        components.add_security_scheme(
            "api_key",
            SecurityScheme::ApiKey(ApiKey::Header(ApiKeyValue::new("X-Api-Key"))),
        );
    }
}

pub fn router(admin: Arc<Admin>, auth: Arc<Auth>) -> Router {
    Router::new()
        .route("/api/v1/settings", get(get_settings).put(put_settings))
        .route("/api/v1/settings/keys/{name}", get(get_key))
        .route("/api/v1/catalog", get(get_catalog))
        .route(
            "/api/v1/providers/check",
            post(provider_status::post_provider_check),
        )
        .route(
            "/api/v1/providers/status",
            get(provider_status::get_provider_status),
        )
        .route(
            "/api/v1/providers/{id}/speed-test",
            post(provider_test::post_speed_test),
        )
        .route(
            "/api/v1/providers/tune-pipeline",
            post(provider_test::post_tune_pipeline),
        )
        .route(
            "/api/v1/provider-tests",
            get(provider_test::get_provider_tests).delete(provider_test::delete_provider_tests),
        )
        .route("/api/v1/provider-usage", get(quota::get_provider_usage))
        .route(
            "/api/v1/providers/{id}/usage/reset",
            post(quota::post_reset_usage),
        )
        .route(
            "/api/v1/queue",
            get(get_queue)
                .post(post_queue)
                .layer(DefaultBodyLimit::max(sab::MAX_NZB)),
        )
        .route(
            "/api/v1/queue/paused",
            get(get_queue_paused).put(put_queue_paused),
        )
        .route("/api/v1/queue/{id}", delete(delete_queue_job))
        .route("/api/v1/queue/{id}/paused", put(put_queue_job_paused))
        .route("/api/v1/queue/{id}/nzb", get(get_queue_nzb))
        .route("/api/v1/history", get(get_history))
        .route("/api/v1/history/{id}", delete(delete_history_job))
        .route("/api/v1/history/{id}/nzb", get(get_history_nzb))
        .route("/api/v1/history/{id}/retry", post(post_retry))
        .route("/api/v1/health", get(get_health))
        .route("/api/v1/health/summary", get(get_health_summary))
        .route("/api/v1/health/schedule", get(get_health_schedule))
        .route("/api/v1/health/rejected", get(get_rejected_releases))
        .route("/api/v1/files", get(files::get_files))
        .route(
            "/api/v1/files/{id}",
            get(files::get_file).delete(files::delete_file),
        )
        .route("/api/v1/files/{id}/check", post(files::post_check))
        .route("/api/v1/files/{id}/repair", post(files::post_repair))
        .route("/api/v1/files/{id}/analyze", post(files::post_analyze))
        .route("/api/v1/files/{id}/nzb", get(files::get_nzb))
        .route("/api/v1/files/{id}/removal", get(files::get_removal))
        .route("/api/v1/files/{id}/reads", get(metrics::get_file_reads))
        .route("/api/v1/links", post(files::post_link))
        .route("/api/v1/events", get(get_events))
        .route("/api/v1/status", get(get_status))
        .route(
            "/api/v1/release-notes",
            get(release_notes::get_release_notes),
        )
        .route("/api/v1/restart", post(post_restart))
        .route("/api/v1/arr/status", get(get_arr_status))
        .route("/api/v1/arr/discover", post(post_arr_discover))
        .route("/api/v1/arr/test", post(post_arr_test))
        .route("/api/v1/arr/reconcile", get(get_reconcile))
        .route(
            "/api/v1/arr/reconcile/{instance}/{at}",
            delete(delete_reconcile),
        )
        .route(
            "/api/v1/arr/reconcile/{instance}/{at}/confirm",
            post(post_reconcile_confirm),
        )
        .route("/api/v1/performance", get(get_performance))
        .route("/api/v1/logs", get(get_logs))
        .route("/api/v1/support-pack", post(support::post_support_pack))
        .route("/api/v1/reads", get(get_reads))
        .route("/api/v1/tracing", get(get_tracing).put(put_tracing))
        .route("/api/v1/traces", get(get_traces))
        .route("/api/v1/traces/{id}", get(get_trace))
        .route("/api/v1/traces/{id}/export", get(export_trace))
        .route("/api/v1/overview", get(metrics::get_overview))
        .route("/api/v1/live", get(metrics::get_live))
        .route("/metrics", get(metrics::get_prometheus))
        .route("/api/v1/upgrade/report", get(upgrade::get_upgrade_report))
        .route(
            "/api/v1/upgrade/report/seen",
            post(upgrade::post_upgrade_report_seen),
        )
        .route("/api/v1/library", get(library::get_library))
        .route(
            "/api/v1/library/preview",
            post(library::post_library_preview).delete(library::delete_library_preview),
        )
        .route(
            "/api/v1/library/confirm",
            post(library::post_library_confirm),
        )
        .route("/api/v1/library/cancel", post(library::post_library_cancel))
        .route("/api/v1/library/check", post(library::post_library_check))
        .route(
            "/api/v1/library/keys/revoke",
            post(library::post_library_revoke_keys),
        )
        .route(
            "/api/v1/library/legacy/stop",
            post(library::post_library_legacy_stop),
        )
        .route("/api/v1/maintenance", get(maintenance::get_maintenance))
        .route("/api/v1/maintenance/cancel", post(maintenance::post_cancel))
        .route("/api/v1/maintenance/{job}/run", post(maintenance::post_run))
        .route(
            "/api/v1/maintenance/{job}/report",
            get(maintenance::get_report).delete(maintenance::delete_report),
        )
        .route(
            "/api/v1/maintenance/{job}/report/confirm",
            post(maintenance::post_confirm),
        )
        .route("/api/v1/database", get(database::get_database))
        .route(
            "/api/v1/database/check",
            post(database::post_database_check),
        )
        .route(
            "/api/v1/database/migration",
            post(database::post_migration).delete(database::delete_migration),
        )
        .route("/api/v1/database/cutover", post(database::post_cutover))
        .route("/api/v1/backups", get(backup::api::get_backups))
        .route("/api/v1/backups/legacy", delete(backup::api::delete_legacy))
        .route(
            "/api/v1/backups/undo",
            post(backup::api::post_undo).delete(backup::api::delete_undo),
        )
        .route(
            "/api/v1/backups/last-restore",
            delete(backup::api::delete_last_restore),
        )
        .route(
            "/api/v1/backups/upload",
            put(backup::api::put_upload).layer(DefaultBodyLimit::disable()),
        )
        .route(
            "/api/v1/backups/{name}",
            axum::routing::patch(backup::api::patch_backup).delete(backup::api::delete_backup),
        )
        .route("/api/v1/backups/{name}/file", get(backup::api::get_file))
        .route(
            "/api/v1/backups/{name}/verify",
            post(backup::api::post_verify),
        )
        .route(
            "/api/v1/backups/{name}/restore",
            post(backup::api::post_restore),
        )
        .route_layer(axum::middleware::from_fn_with_state(auth, auth::require))
        .route("/api/v1/upgrade/status", get(upgrade::get_upgrade_status))
        .route("/api/v1/database/cutover", get(database::get_cutover_none))
        .route("/api/v1/openapi.json", get(openapi))
        .with_state(admin)
}

async fn openapi() -> Json<utoipa::openapi::OpenApi> {
    Json(ApiDoc::openapi())
}

/// The admin API spec as pretty JSON, for generating the UI client.
///
/// # Errors
///
/// Fails only if the spec does not serialize.
pub fn openapi_json() -> Result<String, serde_json::Error> {
    ApiDoc::openapi().to_pretty_json()
}

pub enum Fail {
    Bad(String),
    /// A bad request about the setting at a dotted path.
    Setting(String, String),
    NotFound,
    Conflict(String),
    Unavailable(String),
    /// A server-side failure the caller should read.
    Failed(String),
    Internal,
}

impl From<config::Error> for Fail {
    fn from(error: config::Error) -> Self {
        if let Some(path) = error.path() {
            Self::Setting(path.to_owned(), error.to_string())
        } else {
            tracing::warn!(reason = %error, "could not save settings");
            Self::Internal
        }
    }
}

impl From<infinidysk_db::Error> for Fail {
    fn from(error: infinidysk_db::Error) -> Self {
        tracing::warn!(reason = %error, "admin API request failed");
        Self::Internal
    }
}

impl From<service::Error> for Fail {
    fn from(error: service::Error) -> Self {
        tracing::warn!(reason = %error, "admin API queue request failed");
        Self::Internal
    }
}

impl IntoResponse for Fail {
    fn into_response(self) -> Response {
        let (code, error) = match self {
            Self::Bad(msg) => (StatusCode::BAD_REQUEST, msg),
            Self::Setting(path, error) => {
                return (
                    StatusCode::BAD_REQUEST,
                    Json(json!({ "error": error, "path": path })),
                )
                    .into_response();
            }
            Self::NotFound => (StatusCode::NOT_FOUND, "not found".to_owned()),
            Self::Conflict(msg) => (StatusCode::CONFLICT, msg),
            Self::Unavailable(msg) => (StatusCode::SERVICE_UNAVAILABLE, msg),
            Self::Failed(msg) => (StatusCode::INTERNAL_SERVER_ERROR, msg),
            Self::Internal => (
                StatusCode::INTERNAL_SERVER_ERROR,
                "internal error".to_owned(),
            ),
        };
        (code, Json(json!({ "error": error }))).into_response()
    }
}

/// Every API error body.
#[derive(Serialize, ToSchema)]
pub struct ApiError {
    pub error: String,
    /// The dotted setting path the error is about, like `mount.dir`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

#[derive(Serialize, ToSchema)]
struct SettingsView {
    /// Secrets are masked; send the mask back to keep them.
    settings: Settings,
    /// Dotted paths fixed by environment variables.
    locked: Vec<String>,
    /// Saved changes that apply only after a restart (mount directory, built-in FUSE
    /// mount on or off, read-ahead memory, FUSE tuning, experiments).
    restart_required: bool,
    /// What `settings.experiments` may name.
    experiments: &'static [config::Experiment],
    /// Experiments `INFINIDYSK_EXPERIMENTS` switches on whatever the settings say.
    env_experiments: Vec<String>,
}

fn view(admin: &Admin, store: &ConfigStore) -> SettingsView {
    SettingsView {
        settings: store.settings().masked(),
        locked: store.locked().collect(),
        restart_required: admin.live.restart_required(store.settings()),
        experiments: &config::EXPERIMENTS,
        env_experiments: infinidysk_core::env_experiments(),
    }
}

#[utoipa::path(get, path = "/api/v1/settings", tag = "settings",
    responses((status = 200, body = SettingsView)))]
async fn get_settings(State(admin): State<Arc<Admin>>) -> Json<SettingsView> {
    let store = admin.config.read().await;
    Json(view(&admin, &store))
}

/// Replaces every setting; provider, `WebDAV` and Arr changes apply at once.
#[utoipa::path(put, path = "/api/v1/settings", tag = "settings", request_body = Settings,
    responses(
        (status = 200, body = SettingsView),
        (status = 400, body = ApiError, description = "Invalid or locked by the environment"),
    ))]
async fn put_settings(
    State(admin): State<Arc<Admin>>,
    Json(settings): Json<Settings>,
) -> Result<Json<SettingsView>, Fail> {
    // The probe can hang on a dead share, so it runs before the settings lock.
    let wanted = |current: &config::Imports| {
        let next = &settings.imports;
        let dir = next.strm_dir.trim();
        (next.strategy == ImportStrategy::Strm
            && dir.starts_with('/')
            && (current.strategy != next.strategy || current.strm_dir != dir))
            .then(|| dir.to_owned())
    };
    let probed = wanted(&admin.config.read().await.settings().imports);
    if let Some(dir) = &probed
        && let Err(reason) = admin.live.probe_strm_dir(dir).await
    {
        return Err(Fail::Setting(
            "imports.strm_dir".to_owned(),
            format!("Cannot write STRM files in {dir:?}: {reason}"),
        ));
    }
    let mut store = admin.config.write().await;
    if let Some(dir) = wanted(&store.settings().imports)
        && probed.as_ref() != Some(&dir)
    {
        return Err(Fail::Setting(
            "imports.strm_dir".to_owned(),
            "Settings changed while the STRM folder was checked; save again".to_owned(),
        ));
    }
    store.save(settings).await?;
    store.generate_missing_secrets().await?;
    if let Err(error) = admin.live.apply(store.settings()).await {
        return Err(Fail::Bad(format!("saved, but could not apply: {error:#}")));
    }
    let view = view(&admin, &store);
    drop(store);
    Ok(Json(view))
}

#[derive(Serialize, ToSchema)]
struct RevealedKey {
    value: String,
}

/// A stored key in plain text, so it can be copied. Passwords are never revealed.
#[utoipa::path(get, path = "/api/v1/settings/keys/{name}", tag = "settings",
    params(("name" = String, Path, description = "`sab.api_key`, `auth.api_key`, `webdav.download_key` or `imports.strm_key`")),
    responses((status = 200, body = RevealedKey), (status = 404, body = ApiError)))]
async fn get_key(
    State(admin): State<Arc<Admin>>,
    Path(name): Path<String>,
) -> Result<Json<RevealedKey>, Fail> {
    let settings = admin.config.read().await.settings().clone();
    let key = match name.as_str() {
        "sab.api_key" => settings.sab.api_key,
        "auth.api_key" => settings.auth.api_key,
        "webdav.download_key" => settings.webdav.download_key,
        "imports.strm_key" => settings.imports.strm_key,
        _ => return Err(Fail::NotFound),
    };
    Ok(Json(RevealedKey { value: key.0 }))
}

#[derive(Serialize, ToSchema)]
struct Catalog {
    backbones: Vec<CatalogBackbone>,
    profiles: Vec<CatalogProfile>,
}

#[derive(Serialize, ToSchema)]
struct CatalogBackbone {
    id: &'static str,
    name: &'static str,
}

#[derive(Serialize, ToSchema)]
struct CatalogProfile {
    id: &'static str,
    name: &'static str,
    website: &'static str,
    /// The first is the default.
    servers: Vec<CatalogServer>,
}

#[derive(Serialize, ToSchema)]
struct CatalogServer {
    hostname: &'static str,
    /// ISO 3166 alpha-2; absent for geo-balanced hostnames.
    region: Option<&'static str>,
    port: u16,
    backbone: &'static str,
}

/// Known providers to pick from.
#[utoipa::path(get, path = "/api/v1/catalog", tag = "settings",
    responses((status = 200, body = Catalog)))]
async fn get_catalog() -> Json<Catalog> {
    Json(Catalog {
        backbones: catalog::BACKBONES
            .iter()
            .map(|b| CatalogBackbone {
                id: b.id,
                name: b.name,
            })
            .collect(),
        profiles: catalog::PROFILES
            .iter()
            .map(|p| CatalogProfile {
                id: p.id,
                name: p.name,
                website: p.website,
                servers: p
                    .servers
                    .iter()
                    .map(|s| CatalogServer {
                        hostname: s.hostname,
                        region: s.region,
                        port: s.port,
                        backbone: s.backbone,
                    })
                    .collect(),
            })
            .collect(),
    })
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum QueueStatus {
    Importing,
    Queued,
    /// Held until resumed.
    Paused,
    /// Waiting to retry until `retry_at`, for `last_error`.
    Waiting,
}

#[derive(Serialize, ToSchema)]
struct QueueJob {
    id: Uuid,
    name: String,
    file_name: String,
    category: String,
    priority: i16,
    status: QueueStatus,
    total_bytes: u64,
    /// Unix milliseconds.
    created_at: i64,
    /// Unix milliseconds.
    retry_at: Option<i64>,
    /// Failed attempts so far.
    attempts: u32,
    /// Times the import stopped making progress and was put back.
    stuck_count: u32,
    last_error: Option<String>,
}

/// Jobs in import order; importing ones first.
#[utoipa::path(get, path = "/api/v1/queue", tag = "queue",
    responses((status = 200, body = [QueueJob])))]
async fn get_queue(State(admin): State<Arc<Admin>>) -> Result<Json<Vec<QueueJob>>, Fail> {
    let active = admin.queue.active();
    let now = infinidysk_db::now_ms();
    let mut jobs: Vec<QueueJob> = admin
        .queue
        .db()
        .queue()
        .await?
        .into_iter()
        .map(|j| QueueJob {
            status: if active.contains(&j.id) {
                QueueStatus::Importing
            } else if j.paused {
                QueueStatus::Paused
            } else if j.retry_at.is_some_and(|t| t > now) {
                QueueStatus::Waiting
            } else {
                QueueStatus::Queued
            },
            id: j.id.0,
            name: j.job_name,
            file_name: j.file_name,
            category: j.category,
            priority: j.priority,
            total_bytes: j.total_bytes,
            created_at: j.created_at,
            retry_at: j.retry_at,
            attempts: j.attempts,
            stuck_count: j.stuck_count,
            last_error: j.last_error,
        })
        .collect();
    jobs.sort_by_key(|j| !matches!(j.status, QueueStatus::Importing));
    Ok(Json(jobs))
}

/// One NZB to queue.
#[derive(ToSchema)]
#[allow(dead_code)] // OpenAPI shape only; `post_queue` reads the form fields itself.
struct Upload {
    #[schema(value_type = String, format = Binary)]
    nzb: Vec<u8>,
    /// One of the configured categories; the manual category when absent.
    category: Option<String>,
}

#[derive(Serialize, ToSchema)]
struct Queued {
    id: Uuid,
}

/// Queues an uploaded NZB, like a download client's `addfile`.
#[utoipa::path(post, path = "/api/v1/queue", tag = "queue",
    request_body(content = Upload, content_type = "multipart/form-data"),
    responses((status = 201, body = Queued), (status = 400, body = ApiError)))]
async fn post_queue(
    State(admin): State<Arc<Admin>>,
    mut form: Multipart,
) -> Result<(StatusCode, Json<Queued>), Fail> {
    let mut nzb = None;
    let mut category = None;
    while let Some(field) = form
        .next_field()
        .await
        .map_err(|e| Fail::Bad(e.body_text()))?
    {
        match field.name() {
            Some("nzb") => {
                let name = field.file_name().unwrap_or_default().to_owned();
                let data = field.bytes().await.map_err(|e| Fail::Bad(e.body_text()))?;
                nzb = Some((name, data));
            }
            Some("category") => {
                category = Some(field.text().await.map_err(|e| Fail::Bad(e.body_text()))?);
            }
            _ => {}
        }
    }
    let Some((name, data)) = nzb else {
        return Err(Fail::Bad("no NZB file was sent".to_owned()));
    };
    let file_name = sab::nzb_file_name(&name).map_err(|e| Fail::Bad(e.to_owned()))?;
    let sab = admin.config.read().await.settings().sab.clone();
    let category = match category.filter(|c| !c.is_empty()) {
        None => sab.manual_category,
        Some(c) if sab.categories().any(|known| known == c) => c,
        Some(c) => return Err(Fail::Bad(format!("unknown category {c:?}"))),
    };
    let job = Submission {
        file_name: &file_name,
        category: &category,
        priority: 0,
        post_processing: 0,
    };
    match admin.queue.enqueue(&job, &data).await {
        Ok(id) => Ok((StatusCode::CREATED, Json(Queued { id: id.0 }))),
        Err(error @ (service::Error::Nzb(_) | service::Error::Invalid { .. })) => {
            Err(Fail::Bad(error.to_string()))
        }
        Err(error) => {
            tracing::warn!(reason = %error, "could not queue an uploaded NZB");
            Err(Fail::Internal)
        }
    }
}

#[derive(Serialize, Deserialize, ToSchema)]
struct Paused {
    paused: bool,
}

/// Whether new imports are held.
#[utoipa::path(get, path = "/api/v1/queue/paused", tag = "queue",
    responses((status = 200, body = Paused)))]
async fn get_queue_paused(State(admin): State<Arc<Admin>>) -> Json<Paused> {
    Json(Paused {
        paused: admin.queue.is_paused(),
    })
}

/// Holds or releases the whole queue; imports already running finish.
#[utoipa::path(put, path = "/api/v1/queue/paused", tag = "queue",
    request_body = Paused, responses((status = 200, body = Paused)))]
async fn put_queue_paused(
    State(admin): State<Arc<Admin>>,
    Json(body): Json<Paused>,
) -> Result<Json<Paused>, Fail> {
    admin.queue.set_paused(body.paused).await?;
    Ok(Json(body))
}

/// Holds or releases one queued job; a job already importing finishes.
#[utoipa::path(put, path = "/api/v1/queue/{id}/paused", tag = "queue",
    params(("id" = Uuid, Path)), request_body = Paused,
    responses((status = 204), (status = 404, body = ApiError)))]
async fn put_queue_job_paused(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(body): Json<Paused>,
) -> Result<StatusCode, Fail> {
    if admin.queue.pause_jobs(&[JobId(id)], body.paused).await? == 0 {
        return Err(Fail::NotFound);
    }
    Ok(StatusCode::NO_CONTENT)
}

#[utoipa::path(delete, path = "/api/v1/queue/{id}", tag = "queue",
    params(("id" = Uuid, Path)),
    responses((status = 204), (status = 404, body = ApiError)))]
async fn delete_queue_job(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    if admin.queue.db().remove_job(JobId(id)).await? {
        Ok(StatusCode::NO_CONTENT)
    } else {
        Err(Fail::NotFound)
    }
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct Page {
    /// Default 500, at most 10000.
    limit: Option<usize>,
    offset: Option<usize>,
}

impl Page {
    fn apply<T>(&self, items: Vec<T>) -> Vec<T> {
        items
            .into_iter()
            .skip(self.offset.unwrap_or(0))
            .take(self.limit.unwrap_or(DEFAULT_PAGE).min(MAX_PAGE))
            .collect()
    }
}

#[derive(Serialize, Deserialize, ToSchema, Clone, Copy)]
#[serde(rename_all = "snake_case")]
enum HistoryStatus {
    Completed,
    Failed,
}

#[derive(Deserialize, IntoParams, Default)]
#[into_params(parameter_in = Query)]
struct HistoryFilter {
    /// Case-insensitive part of the job or file name.
    search: Option<String>,
    status: Option<HistoryStatus>,
    /// Leaves out failed jobs created before this (Unix milliseconds); they stay in history.
    failed_since: Option<i64>,
}

#[derive(Serialize, ToSchema)]
struct HistoryJob {
    id: Uuid,
    name: String,
    file_name: String,
    category: String,
    status: HistoryStatus,
    fail_message: Option<String>,
    total_bytes: u64,
    download_seconds: i32,
    /// Unix milliseconds.
    created_at: i64,
    /// The job's directory under `/content`, while it exists.
    path: Option<String>,
}

/// Finished jobs, newest first.
#[utoipa::path(get, path = "/api/v1/history", tag = "history", params(Page, HistoryFilter),
    responses((status = 200, body = [HistoryJob])))]
async fn get_history(
    State(admin): State<Arc<Admin>>,
    Query(page): Query<Page>,
    Query(filter): Query<HistoryFilter>,
) -> Result<Json<Vec<HistoryJob>>, Fail> {
    let query = HistoryQuery {
        search: filter.search.as_deref().filter(|s| !s.is_empty()),
        status: filter.status.map(|s| match s {
            HistoryStatus::Completed => JobStatus::Completed,
            HistoryStatus::Failed => JobStatus::Failed,
        }),
        failed_since: filter.failed_since,
        offset: page.offset.unwrap_or(0),
        limit: Some(page.limit.unwrap_or(DEFAULT_PAGE).min(MAX_PAGE)),
        ..HistoryQuery::default()
    };
    let (rows, _) = admin.queue.db().history_page(&query).await?;
    Ok(Json(
        rows.into_iter()
            .map(|(h, path)| HistoryJob {
                id: h.id.0,
                name: h.job_name,
                file_name: h.file_name,
                category: h.category,
                status: match h.status {
                    JobStatus::Completed => HistoryStatus::Completed,
                    JobStatus::Failed => HistoryStatus::Failed,
                },
                fail_message: h.fail_message,
                total_bytes: h.total_bytes,
                download_seconds: h.download_seconds,
                created_at: h.created_at,
                path,
            })
            .collect(),
    ))
}

/// Forgets a finished job; its files stay.
#[utoipa::path(delete, path = "/api/v1/history/{id}", tag = "history",
    params(("id" = Uuid, Path)),
    responses((status = 204), (status = 404, body = ApiError)))]
async fn delete_history_job(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    if admin.queue.db().delete_history(&[JobId(id)]).await? == 1 {
        Ok(StatusCode::NO_CONTENT)
    } else {
        Err(Fail::NotFound)
    }
}

/// The NZB a queued job was submitted with.
#[utoipa::path(get, path = "/api/v1/queue/{id}/nzb", tag = "queue",
    params(("id" = Uuid, Path)),
    responses((status = 200, content_type = "application/x-nzb", body = String),
        (status = 404, body = ApiError)))]
async fn get_queue_nzb(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Response, Fail> {
    let db = admin.queue.db();
    let job = db.queue().await?.into_iter().find(|j| j.id.0 == id);
    let job = job.ok_or(Fail::NotFound)?;
    files::nzb_download(db, job.nzb_blob, &job.file_name).await
}

/// The NZB a finished job was submitted with.
#[utoipa::path(get, path = "/api/v1/history/{id}/nzb", tag = "history",
    params(("id" = Uuid, Path)),
    responses((status = 200, content_type = "application/x-nzb", body = String),
        (status = 404, body = ApiError)))]
async fn get_history_nzb(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Response, Fail> {
    let db = admin.queue.db();
    let job = db.history_entry(JobId(id)).await?.ok_or(Fail::NotFound)?;
    let blob = job.nzb_blob.ok_or(Fail::NotFound)?;
    files::nzb_download(db, blob, &job.file_name).await
}

/// Queues a failed job again from its stored NZB, as a new job.
#[utoipa::path(post, path = "/api/v1/history/{id}/retry", tag = "history",
    params(("id" = Uuid, Path)),
    responses((status = 201, body = Queued), (status = 400, body = ApiError),
        (status = 404, body = ApiError)))]
async fn post_retry(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<(StatusCode, Json<Queued>), Fail> {
    match admin.queue.retry(JobId(id)).await {
        Ok(id) => Ok((StatusCode::CREATED, Json(Queued { id: id.0 }))),
        Err(service::Error::NotInHistory) => Err(Fail::NotFound),
        Err(
            error @ (service::Error::Nzb(_) | service::Error::NotFailed | service::Error::NoNzb),
        ) => Err(Fail::Bad(error.to_string())),
        Err(error) => {
            tracing::warn!(reason = %error, "could not retry a failed job");
            Err(Fail::Internal)
        }
    }
}

#[derive(Serialize, Deserialize, ToSchema, Clone, Copy)]
#[serde(rename_all = "snake_case")]
pub enum CheckResult {
    Healthy,
    /// Confirmed damage too large to play through.
    Missing,
    /// Confirmed damage small enough to play through.
    Degraded,
    /// A provider failed, so the check proved nothing.
    Inconclusive,
    /// The articles are there but the container structure is broken.
    Damaged,
}

impl From<HealthResult> for CheckResult {
    fn from(result: HealthResult) -> Self {
        match result {
            HealthResult::Healthy => Self::Healthy,
            HealthResult::Missing => Self::Missing,
            HealthResult::Degraded => Self::Degraded,
            HealthResult::Inconclusive => Self::Inconclusive,
            HealthResult::Damaged => Self::Damaged,
        }
    }
}

impl From<CheckResult> for HealthResult {
    fn from(result: CheckResult) -> Self {
        match result {
            CheckResult::Healthy => Self::Healthy,
            CheckResult::Missing => Self::Missing,
            CheckResult::Degraded => Self::Degraded,
            CheckResult::Inconclusive => Self::Inconclusive,
            CheckResult::Damaged => Self::Damaged,
        }
    }
}

#[derive(Serialize, Deserialize, ToSchema, Clone, Copy)]
#[serde(rename_all = "snake_case")]
pub enum CheckRepair {
    None,
    /// An Arr is searching for a replacement; the file is hidden meanwhile.
    ReplacementRequested,
    /// The replacement arrived and the broken file was deleted.
    Replaced,
    ActionNeeded,
    /// No Arr could replace it, so it was deleted.
    Deleted,
}

impl From<Repair> for CheckRepair {
    fn from(repair: Repair) -> Self {
        match repair {
            Repair::None => Self::None,
            Repair::ReplacementRequested => Self::ReplacementRequested,
            Repair::Replaced => Self::Replaced,
            Repair::ActionNeeded => Self::ActionNeeded,
            Repair::Deleted => Self::Deleted,
        }
    }
}

impl From<CheckRepair> for Repair {
    fn from(repair: CheckRepair) -> Self {
        match repair {
            CheckRepair::None => Self::None,
            CheckRepair::ReplacementRequested => Self::ReplacementRequested,
            CheckRepair::Replaced => Self::Replaced,
            CheckRepair::ActionNeeded => Self::ActionNeeded,
            CheckRepair::Deleted => Self::Deleted,
        }
    }
}

/// Where a file is in the health state machine.
#[derive(Serialize, Deserialize, ToSchema, Clone, Copy)]
#[serde(rename_all = "snake_case")]
pub enum ItemHealth {
    Unchecked,
    Healthy,
    Degraded,
    /// One confirmed miss, awaiting the confirming re-check.
    Suspect,
    Repairing,
    /// An Arr is replacing it; not served.
    Hidden,
    ActionNeeded,
    /// Not media; never checked.
    Ignored,
}

impl From<HealthState> for ItemHealth {
    fn from(state: HealthState) -> Self {
        match state {
            HealthState::Unchecked => Self::Unchecked,
            HealthState::Healthy => Self::Healthy,
            HealthState::Degraded => Self::Degraded,
            HealthState::Suspect => Self::Suspect,
            HealthState::Repairing => Self::Repairing,
            HealthState::Hidden => Self::Hidden,
            HealthState::ActionNeeded => Self::ActionNeeded,
            HealthState::Ignored => Self::Ignored,
        }
    }
}

impl From<ItemHealth> for HealthState {
    fn from(state: ItemHealth) -> Self {
        match state {
            ItemHealth::Unchecked => Self::Unchecked,
            ItemHealth::Healthy => Self::Healthy,
            ItemHealth::Degraded => Self::Degraded,
            ItemHealth::Suspect => Self::Suspect,
            ItemHealth::Repairing => Self::Repairing,
            ItemHealth::Hidden => Self::Hidden,
            ItemHealth::ActionNeeded => Self::ActionNeeded,
            ItemHealth::Ignored => Self::Ignored,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct StateCount {
    state: ItemHealth,
    files: u64,
}

/// Files per health state; states without files are left out.
#[utoipa::path(get, path = "/api/v1/health/summary", tag = "health",
    responses((status = 200, body = [StateCount])))]
async fn get_health_summary(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<Vec<StateCount>>, Fail> {
    let summary = admin.queue.db().health_summary().await?;
    Ok(Json(
        summary
            .into_iter()
            .map(|(state, files)| StateCount {
                state: state.into(),
                files,
            })
            .collect(),
    ))
}

#[derive(Serialize, ToSchema)]
pub struct HealthCheckView {
    /// Unix milliseconds.
    created_at: i64,
    item_id: Uuid,
    path: String,
    result: CheckResult,
    repair: CheckRepair,
    message: Option<String>,
}

impl From<HealthCheck> for HealthCheckView {
    fn from(c: HealthCheck) -> Self {
        Self {
            created_at: c.created_at,
            item_id: c.item_id.0,
            path: c.path,
            result: c.result.into(),
            repair: c.repair.into(),
            message: c.message,
        }
    }
}

/// A health check result in the history.
#[derive(Serialize, ToSchema)]
pub struct HealthHistoryEntry {
    #[serde(flatten)]
    check: HealthCheckView,
    /// The checked file is still in the library.
    in_library: bool,
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct HealthHistoryFilter {
    /// Case-insensitive part of the checked path.
    search: Option<String>,
    result: Option<CheckResult>,
}

/// Recent health check results, newest first; kept for 30 days.
#[utoipa::path(get, path = "/api/v1/health", tag = "health", params(Page, HealthHistoryFilter),
    responses((status = 200, body = [HealthHistoryEntry])))]
async fn get_health(
    State(admin): State<Arc<Admin>>,
    Query(page): Query<Page>,
    Query(filter): Query<HealthHistoryFilter>,
) -> Result<Json<Vec<HealthHistoryEntry>>, Fail> {
    let offset = page.offset.unwrap_or(0);
    let limit = page.limit.unwrap_or(DEFAULT_PAGE).min(MAX_PAGE);
    let wanted = i64::try_from(offset.saturating_add(limit)).unwrap_or(i64::MAX);
    let filter = HealthFilter {
        search: filter.search.as_deref().filter(|s| !s.is_empty()),
        result: filter.result.map(HealthResult::from),
    };
    let checks = admin.queue.db().health_checks(filter, wanted).await?;
    Ok(Json(
        page.apply(checks)
            .into_iter()
            .map(|(check, in_library)| HealthHistoryEntry {
                check: check.into(),
                in_library,
            })
            .collect(),
    ))
}

/// A file waiting for health work.
#[derive(Serialize, ToSchema)]
pub struct ScheduledCheck {
    item_id: Uuid,
    name: String,
    category: Option<String>,
    state: ItemHealth,
    /// Unix milliseconds; absent when due now.
    due: Option<i64>,
}

/// The next files with health work, most urgent first.
#[utoipa::path(get, path = "/api/v1/health/schedule", tag = "health",
    responses((status = 200, body = [ScheduledCheck])))]
async fn get_health_schedule(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<Vec<ScheduledCheck>>, Fail> {
    let due = admin.health.schedule(10).await?;
    Ok(Json(
        due.into_iter()
            .map(|(item, status)| ScheduledCheck {
                item_id: item.id.0,
                category: files::category(&item.path),
                name: item.name,
                state: status.state.into(),
                due: status
                    .next_check
                    .filter(|_| status.recheck == Recheck::None),
            })
            .collect(),
    ))
}

/// A release health asked an Arr to replace. NZBs with its name or any of its articles
/// fail to import until it expires.
#[derive(Serialize, ToSchema)]
struct RejectedRelease {
    name: String,
    /// Unix milliseconds.
    rejected_at: i64,
    /// Unix milliseconds.
    expires_at: i64,
}

/// Rejected releases that have not expired, newest first.
#[utoipa::path(get, path = "/api/v1/health/rejected", tag = "health",
    responses((status = 200, body = [RejectedRelease])))]
async fn get_rejected_releases(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<Vec<RejectedRelease>>, Fail> {
    let rows = admin
        .queue
        .db()
        .rejected_releases(infinidysk_db::now_ms())
        .await?;
    Ok(Json(
        rows.into_iter()
            .map(|r| RejectedRelease {
                name: r.name,
                rejected_at: r.rejected_at,
                expires_at: r.expires_at,
            })
            .collect(),
    ))
}

/// Server-sent events naming what changed: `queue`, `history` or `health`; refetch that
/// list. `resync` means notices were dropped: refetch everything. `live` carries a
/// [`metrics::LiveView`] as JSON every 2 s.
#[utoipa::path(get, path = "/api/v1/events", tag = "events",
    responses((status = 200, content_type = "text/event-stream", body = String)))]
async fn get_events(
    State(admin): State<Arc<Admin>>,
) -> Sse<impl Stream<Item = Result<Event, Infallible>>> {
    let state = (
        admin.queue.db().subscribe(),
        admin.metrics.subscribe(),
        admin.cancel.clone(),
    );
    let events = stream::unfold(state, |(mut changes, mut live, cancel)| async move {
        let name = tokio::select! {
            () = cancel.cancelled() => return None,
            changed = live.changed() => {
                changed.ok()?;
                let data = serde_json::to_string(&*live.borrow_and_update()).ok()?;
                let event = Event::default().event("live").data(data);
                return Some((Ok(event), (changes, live, cancel)));
            }
            change = changes.recv() => match change {
                Ok(Change::Queue) => "queue",
                Ok(Change::History) => "history",
                Ok(Change::Health) => "health",
                Err(RecvError::Lagged(_)) => "resync",
                Err(RecvError::Closed) => return None,
            },
        };
        // Browsers drop events with empty data.
        let event = Event::default().event(name).data(name);
        Some((Ok(event), (changes, live, cancel)))
    });
    Sse::new(events).keep_alive(KeepAlive::default())
}

#[derive(Serialize, ToSchema)]
struct Status {
    version: &'static str,
    /// The full commit this build came from, when known.
    commit: Option<&'static str>,
    /// The entry script of the UI this server serves; tabs running another one are stale.
    ui_entry: Option<&'static str>,
    /// Set when a newer release is out.
    update: Option<crate::update::Update>,
    mount: MountStatus,
    /// The segment cache's disk tier; none while it is off.
    disk_cache: Option<DiskCacheStatus>,
    /// Unix milliseconds this process started; changes after a restart.
    started_ms: i64,
    /// Why scheduled backups need a look; absent when they are fine or off.
    backup_warning: Option<String>,
}

#[derive(Serialize, ToSchema)]
struct MountStatus {
    dir: String,
    /// This process serves the built-in FUSE mount (on in the settings it started with,
    /// and Linux). Off means nothing to warn about.
    enabled: bool,
    mounted: bool,
    /// Why it is not mounted.
    reason: Option<String>,
}

/// Sizes are bytes; counters are since the disk cache started.
#[derive(Default, Serialize, ToSchema)]
struct DiskCacheStatus {
    dir: String,
    /// Why it is enabled but not running; the rest is 0 then.
    reason: Option<String>,
    /// Still indexing files an earlier run left; the sizes grow meanwhile.
    indexing: bool,
    used_bytes: u64,
    segments: u64,
    /// The size limit, lowered so the volume keeps 1 GB free.
    capacity_bytes: u64,
    /// The configured size limit.
    limit_bytes: u64,
    /// Free space on the cache's volume.
    free_bytes: Option<u64>,
    /// Segments not read for this long are deleted.
    max_age_hours: u64,
    hits: u64,
    written_bytes: u64,
    /// Deleted to stay under the size limit or because they were not read in time.
    evicted_bytes: u64,
    /// Segments not cached because the disk fell behind.
    skipped_writes: u64,
    failed_writes: u64,
}

/// What discovery found in each enabled Arr instance: its SAB download clients, whether
/// they are this installation, and the categories they own.
#[utoipa::path(get, path = "/api/v1/arr/status", tag = "arr",
    responses((status = 200, body = arr::ArrStatus)))]
async fn get_arr_status(State(admin): State<Arc<Admin>>) -> Json<arr::ArrStatus> {
    Json(admin.arr.status())
}

/// Checks every enabled Arr instance again now; the results show in the Arr status.
#[utoipa::path(post, path = "/api/v1/arr/discover", tag = "arr",
    responses((status = 202, description = "Discovery started")))]
async fn post_arr_discover(State(admin): State<Arc<Admin>>) -> StatusCode {
    admin.arr.rediscover();
    StatusCode::ACCEPTED
}

#[derive(Serialize, ToSchema)]
struct ArrTest {
    /// The Arr's version when it answered with this key and type.
    version: Option<String>,
    /// Why it did not.
    error: Option<String>,
}

/// Checks that an Arr instance, saved or not, answers with its key and type. A masked
/// key is the saved key of the instance with the same id.
#[utoipa::path(post, path = "/api/v1/arr/test", tag = "arr", request_body = config::ArrInstance,
    responses((status = 200, body = ArrTest), (status = 400, body = ApiError)))]
async fn post_arr_test(
    State(admin): State<Arc<Admin>>,
    Json(mut instance): Json<config::ArrInstance>,
) -> Result<Json<ArrTest>, Fail> {
    if instance.api_key.0 == config::MASK {
        let saved = admin
            .config
            .read()
            .await
            .settings()
            .arr_instances
            .iter()
            .find(|a| a.id == instance.id)
            .map(|a| a.api_key.clone())
            .ok_or_else(|| Fail::Bad("no saved API key for this instance".to_owned()))?;
        instance.api_key.restore(&saved);
    }
    if instance.name.trim().is_empty() {
        instance.name = format!("{:?}", instance.profile);
    }
    instance
        .validate()
        .map_err(|error| Fail::Bad(error.to_string()))?;
    let arr = crate::app::to_arr(&instance);
    Ok(Json(
        match infinidysk_arr::version(admin.arr.http(), &arr).await {
            Ok(version) => ArrTest {
                version: Some(version),
                error: None,
            },
            Err(error) => {
                tracing::warn!(arr = %arr.name, reason = %error, "Arr connection test failed");
                ArrTest {
                    version: None,
                    error: Some(error.to_string()),
                }
            }
        },
    ))
}

/// Daily reconciliations held for deleting more than 5 % of an Arr's items.
#[utoipa::path(get, path = "/api/v1/arr/reconcile", tag = "arr",
    responses((status = 200, body = Vec<arr::HeldReconcile>)))]
async fn get_reconcile(State(admin): State<Arc<Admin>>) -> Json<Vec<arr::HeldReconcile>> {
    Json(admin.arr.held().await)
}

#[derive(Serialize, ToSchema)]
struct Reconciled {
    /// Items deleted; reviewed files the Arr has again, or whose item changed, stay.
    deleted: usize,
}

/// Deletes the reviewed files of a held reconciliation that the Arr still lacks.
#[utoipa::path(post, path = "/api/v1/arr/reconcile/{instance}/{at}/confirm", tag = "arr",
    params(("instance" = Uuid, Path), ("at" = i64, Path, description = "`at_ms` of the held run")),
    responses((status = 200, body = Reconciled), (status = 404, body = ApiError),
        (status = 503, body = ApiError)))]
async fn post_reconcile_confirm(
    State(admin): State<Arc<Admin>>,
    Path((instance, at)): Path<(Uuid, i64)>,
) -> Result<Json<Reconciled>, Fail> {
    match admin.arr.confirm_held(instance, at).await {
        Ok(Some(deleted)) => Ok(Json(Reconciled { deleted })),
        Ok(None) => Err(Fail::NotFound),
        Err(error) => Err(Fail::Unavailable(format!(
            "could not check the Arr: {error}"
        ))),
    }
}

/// Forgets a held reconciliation without deleting anything.
#[utoipa::path(delete, path = "/api/v1/arr/reconcile/{instance}/{at}", tag = "arr",
    params(("instance" = Uuid, Path), ("at" = i64, Path, description = "`at_ms` of the held run")),
    responses((status = 204), (status = 500, body = ApiError)))]
async fn delete_reconcile(
    State(admin): State<Arc<Admin>>,
    Path((instance, at)): Path<(Uuid, i64)>,
) -> Result<StatusCode, Fail> {
    admin.arr.drop_held(instance, at).await.map_err(|error| {
        tracing::warn!(reason = %error, "forgetting a held reconciliation failed");
        Fail::Internal
    })?;
    Ok(StatusCode::NO_CONTENT)
}

/// Server version, whether a newer build exists, FUSE mount and disk cache state.
#[utoipa::path(get, path = "/api/v1/status", tag = "status",
    responses((status = 200, body = Status)))]
async fn get_status(State(admin): State<Arc<Admin>>) -> Json<Status> {
    let mount = admin.mount.borrow().clone();
    let cache = admin.live.cache().stats();
    let disk_cache = match (cache.disk, cache.disk_failure) {
        (Some(d), _) => Some(DiskCacheStatus {
            dir: d.dir.display().to_string(),
            reason: None,
            indexing: d.indexing,
            used_bytes: d.bytes,
            segments: d.items,
            capacity_bytes: d.capacity,
            limit_bytes: d.limit,
            free_bytes: d.free,
            max_age_hours: d.max_age.as_secs() / 3600,
            hits: cache.disk_hits,
            written_bytes: d.written,
            evicted_bytes: d.evicted,
            skipped_writes: d.skipped,
            failed_writes: d.failed,
        }),
        (None, Some((dir, reason))) => Some(DiskCacheStatus {
            dir: dir.display().to_string(),
            reason: Some(reason),
            ..DiskCacheStatus::default()
        }),
        (None, None) => None,
    };
    let backup_warning = admin.backups.warning(
        admin.config.read().await.settings(),
        admin
            .maintenance
            .last_run(config::MaintenanceJob::BackupDatabase)
            .as_ref(),
    );
    Json(Status {
        version: crate::update::VERSION,
        commit: crate::update::COMMIT,
        ui_entry: crate::ui::entry(),
        update: admin.updates.get(),
        mount: MountStatus {
            dir: admin.live.mount_dir().to_owned(),
            enabled: admin.live.fuse_enabled(),
            mounted: mount.is_ok(),
            reason: mount.err(),
        },
        disk_cache,
        started_ms: logs::started_ms(),
        backup_warning,
    })
}

/// Stops gracefully and starts again in the same process, to apply settings read only at
/// startup. Open files and running imports are interrupted.
#[utoipa::path(post, path = "/api/v1/restart", tag = "status",
    responses((status = 202, description = "Restarting")))]
async fn post_restart(State(admin): State<Arc<Admin>>) -> StatusCode {
    tracing::info!("restarting to apply settings");
    admin.restart.store(true, Ordering::Relaxed);
    admin.cancel.cancel();
    StatusCode::ACCEPTED
}

#[derive(Serialize, ToSchema)]
struct PerformanceView {
    /// Every preset's values, to show what picking one changes.
    presets: Vec<PresetView>,
    /// Saved settings with overrides applied.
    effective: config::Tuning,
    cache: CacheView,
    /// Requests that skipped a provider because it recently lacked the article.
    missing_hits: u64,
    /// Free bytes on the volume of `dir` (default: the effective disk cache directory).
    free_bytes: Option<u64>,
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct PerformanceQuery {
    /// Directory to report free space for, before it is saved.
    dir: Option<String>,
}

#[derive(Serialize, ToSchema)]
struct PresetView {
    preset: config::Preset,
    tuning: config::Tuning,
}

/// Counters since start; sizes are bytes.
#[derive(Serialize, ToSchema)]
struct CacheView {
    memory_enabled: bool,
    memory_bytes: u64,
    memory_capacity: u64,
    memory_items: u64,
    memory_hits: u64,
    /// Whether the disk tier is running (enabled and its directory writable).
    disk_enabled: bool,
    disk_bytes: u64,
    disk_capacity: u64,
    disk_items: u64,
    disk_hits: u64,
    /// Segments taken from a fetch another reader already had in flight.
    joined: u64,
    /// Segments fetched from providers.
    misses: u64,
    /// Bytes served without a provider request of their own.
    bytes_served: u64,
}

/// Presets, effective tuning and live cache counters.
#[utoipa::path(get, path = "/api/v1/performance", tag = "settings", params(PerformanceQuery),
    responses((status = 200, body = PerformanceView)))]
async fn get_performance(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<PerformanceQuery>,
) -> Json<PerformanceView> {
    let default_cache_dir = admin.live.default_cache_dir();
    let effective = crate::app::effective_tuning(
        &admin.config.read().await.settings().performance,
        default_cache_dir,
    );
    let dir = query
        .dir
        .unwrap_or_else(|| effective.disk_cache_dir.clone());
    let free_bytes = std::path::Path::new(&dir)
        .is_absolute()
        .then(|| infinidysk_stream::free_space(dir.as_ref()))
        .flatten();
    let s = admin.live.cache().stats();
    let disk = s.disk.as_ref();
    Json(PerformanceView {
        presets: config::Preset::ALL
            .into_iter()
            .map(|preset| PresetView {
                preset,
                tuning: config::Tuning {
                    disk_cache_dir: default_cache_dir.to_owned(),
                    ..preset.tuning()
                },
            })
            .collect(),
        effective,
        cache: CacheView {
            memory_enabled: s.memory_enabled,
            memory_bytes: s.memory_bytes,
            memory_capacity: s.memory_capacity,
            memory_items: s.memory_items,
            memory_hits: s.memory_hits,
            disk_enabled: disk.is_some(),
            disk_bytes: disk.map_or(0, |d| d.bytes),
            disk_capacity: disk.map_or(0, |d| d.capacity),
            disk_items: disk.map_or(0, |d| d.items),
            disk_hits: s.disk_hits,
            joined: s.joined,
            misses: s.misses,
            bytes_served: s.bytes_served,
        },
        missing_hits: admin.client.missing_hits(),
        free_bytes,
    })
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct LogQuery {
    /// Least severe level to include; default `info`.
    level: Option<Level>,
    /// Only events logged for this task (a maintenance job id); the server keeps the
    /// last 1000 per task.
    task: Option<String>,
}

/// Recent log events, oldest first; the server keeps the last 2000.
#[utoipa::path(get, path = "/api/v1/logs", tag = "status", params(LogQuery),
    responses((status = 200, body = [Entry])))]
async fn get_logs(Query(query): Query<LogQuery>) -> Json<Vec<Entry>> {
    Json(logs::LOGS.snapshot(query.level.unwrap_or(Level::Info), query.task.as_deref()))
}

/// Events a trace keeps unless the request says otherwise (a few MB).
pub const TRACE_CAPACITY: usize = 100_000;
const MAX_TRACE_CAPACITY: usize = 1_000_000;
const MAX_TRACE_MINUTES: u32 = 24 * 60;

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum DeliveryView {
    Fuse,
    Webdav,
}

#[derive(Serialize, ToSchema)]
struct ReadView {
    id: u64,
    item_id: Uuid,
    path: String,
    delivery: DeliveryView,
    /// Process, pid and uid/gid (FUSE), or user agent and address (`WebDAV`). Best effort.
    client: String,
    /// Unix milliseconds.
    opened_at: i64,
    closed_at: Option<i64>,
    size: u64,
    /// Bytes returned to the reader.
    served: u64,
    position: u64,
    traced: bool,
    /// Player id from a signed `/view` link.
    player: Option<String>,
    /// From open to the first byte served.
    first_byte_ms: Option<u64>,
    /// Waits for data of at least 1 s, other than for the first byte and the first byte
    /// after a seek.
    stalls: u64,
    stall_ms: u64,
    seeks: u64,
}

fn read_view(session: &Session) -> ReadView {
    let (stalls, stalled) = session.stalls();
    ReadView {
        id: session.id,
        item_id: session.item.0,
        path: session.path.clone(),
        delivery: match session.delivery {
            Delivery::Fuse => DeliveryView::Fuse,
            Delivery::WebDav => DeliveryView::Webdav,
        },
        client: session.client.clone(),
        opened_at: unix_ms(session.opened_at),
        closed_at: session.closed_at().map(unix_ms),
        size: session.size,
        served: session.served(),
        position: session.pos(),
        traced: session.trace.is_some(),
        player: session.player.clone(),
        first_byte_ms: session
            .first_byte()
            .map(|d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX)),
        stalls,
        stall_ms: u64::try_from(stalled.as_millis()).unwrap_or(u64::MAX),
        seeks: session.seeks(),
    }
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct ReadsQuery {
    /// Only reads opened through a `/view` link with this player id.
    player: Option<String>,
}

/// Files open through FUSE or `WebDAV` now, oldest first.
#[utoipa::path(get, path = "/api/v1/reads", tag = "tracing", params(ReadsQuery),
    responses((status = 200, body = [ReadView])))]
async fn get_reads(
    State(admin): State<Arc<Admin>>,
    Query(query): Query<ReadsQuery>,
) -> Json<Vec<ReadView>> {
    Json(
        admin
            .vfs
            .sessions()
            .open()
            .iter()
            .filter(|s| query.player.is_none() || s.player == query.player)
            .map(|s| read_view(s))
            .collect(),
    )
}

#[derive(Serialize, ToSchema)]
struct TracingView {
    /// Unix milliseconds; absent while tracing is off.
    until: Option<i64>,
    /// Events kept per trace.
    capacity: usize,
}

fn tracing_view(tracing: Option<Tracing>) -> TracingView {
    let now = Instant::now();
    TracingView {
        until: tracing.map(|t| unix_ms(SystemTime::now() + t.until.saturating_duration_since(now))),
        capacity: tracing.map_or(TRACE_CAPACITY, |t| t.capacity),
    }
}

/// Whether new reads are being traced.
#[utoipa::path(get, path = "/api/v1/tracing", tag = "tracing",
    responses((status = 200, body = TracingView)))]
async fn get_tracing(State(admin): State<Arc<Admin>>) -> Json<TracingView> {
    Json(tracing_view(admin.vfs.sessions().tracing()))
}

#[derive(Deserialize, ToSchema)]
struct TracingRequest {
    /// Trace reads opened in the next this many minutes (at most 1440); 0 stops.
    minutes: u32,
    /// Events kept per trace; default 100000, at most 1000000.
    capacity: Option<usize>,
}

/// Starts or stops tracing new reads. Reads already open are not affected.
#[utoipa::path(put, path = "/api/v1/tracing", tag = "tracing", request_body = TracingRequest,
    responses((status = 200, body = TracingView), (status = 400, body = ApiError)))]
async fn put_tracing(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<TracingRequest>,
) -> Result<Json<TracingView>, Fail> {
    if request.minutes > MAX_TRACE_MINUTES {
        return Err(Fail::Bad(format!(
            "minutes must be at most {MAX_TRACE_MINUTES}"
        )));
    }
    let capacity = request.capacity.unwrap_or(TRACE_CAPACITY);
    if !(1000..=MAX_TRACE_CAPACITY).contains(&capacity) {
        return Err(Fail::Bad(format!(
            "capacity must be 1000-{MAX_TRACE_CAPACITY}"
        )));
    }
    let tracing = (request.minutes > 0).then(|| Tracing {
        until: Instant::now() + Duration::from_secs(u64::from(request.minutes) * 60),
        capacity,
    });
    admin.vfs.sessions().set_tracing(tracing);
    Ok(Json(tracing_view(tracing)))
}

#[derive(Serialize, ToSchema)]
struct TraceView {
    #[serde(flatten)]
    read: ReadView,
    /// Stall totals and their causes; see the trace viewer.
    #[schema(value_type = Object)]
    summary: Summary,
}

/// Traced reads, open and the last 20 finished, newest first.
#[utoipa::path(get, path = "/api/v1/traces", tag = "tracing",
    responses((status = 200, body = [TraceView])))]
async fn get_traces(State(admin): State<Arc<Admin>>) -> Json<Vec<TraceView>> {
    Json(
        admin
            .vfs
            .sessions()
            .traced()
            .iter()
            .filter_map(|s| {
                Some(TraceView {
                    summary: s.trace.as_ref()?.summary(),
                    read: read_view(s),
                })
            })
            .collect(),
    )
}

/// One trace as JSON lines: a header (`read`, `providers` by id, `summary`, `dropped`),
/// then one event per line in time order.
#[utoipa::path(get, path = "/api/v1/traces/{id}", tag = "tracing",
    params(("id" = u64, Path)),
    responses((status = 200, content_type = "application/x-ndjson", body = String),
        (status = 404, body = ApiError)))]
async fn get_trace(State(admin): State<Arc<Admin>>, Path(id): Path<u64>) -> Result<Response, Fail> {
    let body = trace_body(&admin, id, false).await?;
    Ok(([(header::CONTENT_TYPE, "application/x-ndjson")], body).into_response())
}

/// One trace to share in a bug report: as [`get_trace`], with the server version and
/// tuning in the header, the path cut to its extension, and providers named
/// `provider-N`.
#[utoipa::path(get, path = "/api/v1/traces/{id}/export", tag = "tracing",
    params(("id" = u64, Path)),
    responses((status = 200, content_type = "application/x-ndjson", body = String),
        (status = 404, body = ApiError)))]
async fn export_trace(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<u64>,
) -> Result<Response, Fail> {
    let body = trace_body(&admin, id, true).await?;
    let file = format!("attachment; filename=\"trace-{id}.ndjson\"");
    Ok((
        [
            (header::CONTENT_TYPE, "application/x-ndjson".to_owned()),
            (header::CONTENT_DISPOSITION, file),
        ],
        body,
    )
        .into_response())
}

async fn trace_body(admin: &Admin, id: u64, redact: bool) -> Result<String, Fail> {
    let session = admin.vfs.sessions().get(id).ok_or(Fail::NotFound)?;
    let trace = session.trace.as_ref().ok_or(Fail::NotFound)?;
    let (records, dropped) = trace.records();
    let mut read = read_view(&session);
    let mut providers = admin.client.provider_names();
    let mut header = json!({});
    if redact {
        read.path = std::path::Path::new(&read.path).extension().map_or_else(
            || "file".to_owned(),
            |e| format!("file.{}", e.to_string_lossy()),
        );
        read.item_id = Uuid::nil();
        for (i, name) in providers.iter_mut().enumerate() {
            *name = format!("provider-{}", i + 1);
        }
        header["version"] = json!(crate::update::VERSION);
        let default_cache_dir = admin.live.default_cache_dir();
        let mut tuning = crate::app::effective_tuning(
            &admin.config.read().await.settings().performance,
            default_cache_dir,
        );
        // Folder names can carry a user name; whether it is the default is what helps.
        let shown = if tuning.disk_cache_dir == default_cache_dir {
            "<config>/cache"
        } else {
            "<custom>"
        };
        shown.clone_into(&mut tuning.disk_cache_dir);
        header["tuning"] = json!(tuning);
    }
    header["read"] = json!(read);
    header["providers"] = json!(providers);
    header["summary"] = json!(Summary::of(&records, dropped));
    header["dropped"] = json!(dropped);
    let mut body = header.to_string();
    for record in &records {
        body.push('\n');
        body.push_str(&serde_json::to_string(record).map_err(|_| Fail::Internal)?);
    }
    body.push('\n');
    Ok(body)
}
