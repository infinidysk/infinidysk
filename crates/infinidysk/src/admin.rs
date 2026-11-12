//! Admin API for the UI under `/api/v1`, behind [`auth::require`]. `/api/v1/openapi.json`
//! describes it.

use std::collections::HashMap;
use std::convert::Infallible;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

use axum::extract::{DefaultBodyLimit, Multipart, Path, Query, State};
use axum::http::{StatusCode, header};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post, put};
use axum::{Json, Router};
use base64::Engine as _;
use futures_util::{Stream, StreamExt, stream};
use infinidysk_core::JobId;
use infinidysk_core::trace::Summary;
use infinidysk_db::{
    Change, HealthCheck, HealthFilter, HealthResult, HealthState, HistoryQuery, JobStatus, Recheck,
    Repair,
};
use infinidysk_health::Health;
use infinidysk_nntp::{Client, catalog};
use infinidysk_queue::{EnqueueOutcome, Queue, Submission, service};
use infinidysk_vfs::{Delivery, Session, Tracing, Vfs};
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::{Digest as _, Sha256};
use tokio::sync::broadcast::error::RecvError;
use tokio::sync::{RwLock, Semaphore, watch};
use tokio_util::sync::CancellationToken;
use utoipa::openapi::security::{ApiKey, ApiKeyValue, SecurityScheme};
use utoipa::{IntoParams, Modify, OpenApi, ToSchema};
use uuid::Uuid;

use crate::app::Live;
use crate::arr;
use crate::auth::{self, Auth};
use crate::backup;
use crate::config::{self, ConfigStore, ImportStrategy, Indexer, Settings};
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

pub(crate) const INDEXER_WORK_LIMIT: usize = 12;
pub(crate) const INDEXER_DOWNLOAD_LIMIT: usize = 3;
const SEARCH_RESPONSE_MAX_BYTES: usize = 1_000_000;
const SEARCH_RESPONSE_MAX_RESULTS: usize = 1_000;

pub struct Admin {
    pub config: Arc<RwLock<ConfigStore>>,
    pub search_refs: SearchReferences,
    pub indexer_work: Semaphore,
    pub indexer_downloads: Semaphore,
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
    pub leftovers: Arc<upgrade::leftovers::Leftovers>,
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
        get_indexers,
        post_indexer,
        put_indexer,
        patch_indexer_enabled,
        delete_indexer,
        post_indexer_test,
        get_indexer_search,
        post_indexer_search_queue,
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
        upgrade::leftovers::get_leftovers,
        upgrade::leftovers::post_leftovers_scan,
        upgrade::leftovers::post_leftovers_dismiss,
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
        .route("/api/v1/indexers", get(get_indexers).post(post_indexer))
        .route(
            "/api/v1/indexers/{id}",
            put(put_indexer).delete(delete_indexer),
        )
        .route(
            "/api/v1/indexers/{id}/enabled",
            axum::routing::patch(patch_indexer_enabled),
        )
        .route("/api/v1/indexers/{id}/test", post(post_indexer_test))
        .route("/api/v1/indexers/search", get(get_indexer_search))
        .route(
            "/api/v1/indexers/search/queue",
            post(post_indexer_search_queue),
        )
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
        .route(
            "/api/v1/upgrade/leftovers",
            get(upgrade::leftovers::get_leftovers),
        )
        .route(
            "/api/v1/upgrade/leftovers/scan",
            post(upgrade::leftovers::post_leftovers_scan),
        )
        .route(
            "/api/v1/upgrade/leftovers/dismiss",
            post(upgrade::leftovers::post_leftovers_dismiss),
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
    indexers_revision: Uuid,
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
        indexers_revision: store.indexers_revision(),
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

#[derive(Deserialize, ToSchema)]
struct SettingsUpdate {
    #[serde(flatten)]
    settings: Settings,
    indexers_revision: Uuid,
}

/// Saves global settings. Indexer changes use dedicated endpoints.
#[utoipa::path(put, path = "/api/v1/settings", tag = "settings", request_body = SettingsUpdate,
    responses(
        (status = 200, body = SettingsView),
        (status = 400, body = ApiError, description = "Invalid or locked by the environment"),
        (status = 409, body = ApiError, description = "Indexer state changed"),
    ))]
async fn put_settings(
    State(admin): State<Arc<Admin>>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<SettingsView>, Fail> {
    let has_indexers = body.get("indexers").is_some();
    let SettingsUpdate {
        mut settings,
        indexers_revision,
    } = serde_json::from_value(body).map_err(|error| Fail::Bad(error.to_string()))?;
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
    if indexers_revision != store.indexers_revision()
        || (has_indexers && settings.indexers != store.settings().masked().indexers)
    {
        return Err(Fail::Conflict(
            "Indexer settings changed; reload and use the indexer API.".to_owned(),
        ));
    }
    settings.indexers.clone_from(&store.settings().indexers);
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
    /// Hides old stored failures without deleting them; missing-folder completed jobs still match `status=failed` at any age but return `completed`.
    failed_max_age_ms: Option<u64>,
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
        failed_since: filter.failed_max_age_ms.map(|age| {
            infinidysk_db::now_ms().saturating_sub(i64::try_from(age).unwrap_or(i64::MAX))
        }),
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

#[derive(Serialize, ToSchema)]
struct IndexerRead {
    id: Uuid,
    name: String,
    base_url: String,
    api_key: config::Secret,
    enabled: bool,
    revision: String,
}

fn indexer_read(settings: &Settings, indexer: &Indexer) -> IndexerRead {
    let mut digest = Sha256::new();
    digest.update(settings.auth.session_key.0.as_bytes());
    digest.update([0]);
    digest.update(serde_json::to_vec(indexer).unwrap_or_default());
    IndexerRead {
        id: indexer.id,
        name: indexer.name.clone(),
        base_url: indexer.public_base_url().to_owned(),
        api_key: config::Secret(if indexer.api_key.0.is_empty() {
            String::new()
        } else {
            config::MASK.to_owned()
        }),
        enabled: indexer.enabled,
        revision: base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(digest.finalize()),
    }
}

fn saved_indexer(settings: &Settings, id: Uuid) -> Result<IndexerRead, Fail> {
    settings
        .indexers
        .iter()
        .find(|i| i.id == id)
        .map(|i| indexer_read(settings, i))
        .ok_or(Fail::NotFound)
}

#[utoipa::path(get, path = "/api/v1/indexers", tag = "indexer",
    responses((status = 200, body = [IndexerRead])))]
async fn get_indexers(State(admin): State<Arc<Admin>>) -> Json<Vec<IndexerRead>> {
    let store = admin.config.read().await;
    Json(
        store
            .settings()
            .indexers
            .iter()
            .map(|i| indexer_read(store.settings(), i))
            .collect(),
    )
}

#[derive(Deserialize, ToSchema)]
struct IndexerCreate {
    name: String,
    base_url: String,
    api_key: config::Secret,
    #[doc = "Defaults to true when omitted."]
    enabled: Option<bool>,
}

#[utoipa::path(post, path = "/api/v1/indexers", tag = "indexer",
    request_body = IndexerCreate,
    responses((status = 200, body = IndexerRead), (status = 400, body = ApiError)))]
async fn post_indexer(
    State(admin): State<Arc<Admin>>,
    Json(input): Json<IndexerCreate>,
) -> Result<Json<IndexerRead>, Fail> {
    let id = Uuid::now_v7();
    let indexer = Indexer {
        id,
        name: input.name.trim().to_owned(),
        base_url: input.base_url,
        api_key: input.api_key,
        enabled: input.enabled.unwrap_or(true),
    };
    let mut store = admin.config.write().await;
    let mut settings = store.settings().clone();
    settings.indexers.push(indexer);
    store.save(settings).await?;
    Ok(Json(saved_indexer(store.settings(), id)?))
}

#[derive(Deserialize, ToSchema)]
struct IndexerUpdate {
    id: Uuid,
    name: String,
    base_url: String,
    api_key: config::Secret,
    enabled: bool,
    revision: String,
}

#[derive(Deserialize, ToSchema)]
struct IndexerEnabled {
    enabled: bool,
}

#[utoipa::path(patch, path = "/api/v1/indexers/{id}/enabled", tag = "indexer",
    params(("id" = Uuid, Path)),
    request_body = IndexerEnabled,
    responses((status = 200, body = IndexerRead), (status = 404, body = ApiError)))]
async fn patch_indexer_enabled(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(input): Json<IndexerEnabled>,
) -> Result<Json<IndexerRead>, Fail> {
    let mut store = admin.config.write().await;
    let mut settings = store.settings().clone();
    settings
        .indexers
        .iter_mut()
        .find(|i| i.id == id)
        .ok_or(Fail::NotFound)?
        .enabled = input.enabled;
    store.save(settings).await?;
    admin.search_refs.invalidate(id)?;
    Ok(Json(saved_indexer(store.settings(), id)?))
}

#[utoipa::path(put, path = "/api/v1/indexers/{id}", tag = "indexer",
    params(("id" = Uuid, Path)),
    request_body = IndexerUpdate,
    responses((status = 200, body = IndexerRead), (status = 404, body = ApiError),
              (status = 409, body = ApiError)))]
async fn put_indexer(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
    Json(input): Json<IndexerUpdate>,
) -> Result<Json<IndexerRead>, Fail> {
    if id != input.id {
        return Err(Fail::Bad("Indexer ID does not match the URL".to_owned()));
    }
    let mut store = admin.config.write().await;
    let mut settings = store.settings().clone();
    let current_revision = saved_indexer(&settings, id)?.revision;
    if current_revision != input.revision {
        return Err(Fail::Conflict(
            "Indexer was updated in another session.".to_owned(),
        ));
    }
    let indexer = Indexer {
        id,
        name: input.name.trim().to_owned(),
        base_url: input.base_url,
        api_key: input.api_key,
        enabled: input.enabled,
    };
    *settings
        .indexers
        .iter_mut()
        .find(|i| i.id == id)
        .ok_or(Fail::NotFound)? = indexer;
    store.save(settings).await?;
    admin.search_refs.invalidate(id)?;
    Ok(Json(saved_indexer(store.settings(), id)?))
}

#[utoipa::path(delete, path = "/api/v1/indexers/{id}", tag = "indexer",
    params(("id" = Uuid, Path)),
    responses((status = 204), (status = 404, body = ApiError)))]
async fn delete_indexer(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, Fail> {
    let mut store = admin.config.write().await;
    let mut settings = store.settings().clone();
    let count = settings.indexers.len();
    settings.indexers.retain(|i| i.id != id);
    if settings.indexers.len() == count {
        return Err(Fail::NotFound);
    }
    store.save(settings).await?;
    admin.search_refs.forget(id)?;
    drop(store);
    Ok(StatusCode::NO_CONTENT)
}

const SEARCH_REF_TTL: Duration = Duration::from_mins(15);
const SEARCH_REF_LIMIT: usize = 10_000;
const SEARCH_REF_MAX_BYTES: usize = 16 * 1024 * 1024;

#[derive(Clone)]
struct SearchReference {
    indexer_id: Uuid,
    revision: Uuid,
    base_url: String,
    key_hash: [u8; 32],
    url: String,
    title: String,
    source_dns: Option<crate::fetch::IndexerDns>,
    created: Instant,
}

#[derive(Default)]
pub struct SearchReferences(
    Mutex<HashMap<String, SearchReference>>,
    Mutex<HashMap<Uuid, Uuid>>,
);

fn search_reference_bytes(key: &String, reference: &SearchReference) -> usize {
    std::mem::size_of::<(String, SearchReference)>()
        + 48
        + key.capacity()
        + reference.base_url.capacity()
        + reference.url.capacity()
        + reference.title.capacity()
        + reference
            .source_dns
            .as_ref()
            .map_or(0, crate::fetch::IndexerDns::memory_bytes)
}

impl SearchReferences {
    fn revision(&self, id: Uuid) -> Result<Uuid, Fail> {
        let mut revisions = self.1.lock().map_err(|_| Fail::Internal)?;
        Ok(*revisions.entry(id).or_insert_with(Uuid::new_v4))
    }

    fn has_revision(&self, id: Uuid, revision: Uuid) -> Result<bool, Fail> {
        let revisions = self.1.lock().map_err(|_| Fail::Internal)?;
        Ok(revisions.get(&id) == Some(&revision))
    }

    fn invalidate(&self, id: Uuid) -> Result<(), Fail> {
        self.1
            .lock()
            .map_err(|_| Fail::Internal)?
            .insert(id, Uuid::new_v4());
        self.0
            .lock()
            .map_err(|_| Fail::Internal)?
            .retain(|_, entry| entry.indexer_id != id);
        Ok(())
    }

    fn forget(&self, id: Uuid) -> Result<(), Fail> {
        self.1.lock().map_err(|_| Fail::Internal)?.remove(&id);
        self.0
            .lock()
            .map_err(|_| Fail::Internal)?
            .retain(|_, entry| entry.indexer_id != id);
        Ok(())
    }

    fn remember(
        &self,
        indexer: &Indexer,
        revision: Uuid,
        results: &mut [crate::newznab::SearchResult],
    ) -> Result<bool, Fail> {
        let revisions = self.1.lock().map_err(|_| Fail::Internal)?;
        if revisions.get(&indexer.id) != Some(&revision) {
            return Ok(false);
        }
        let mut entries = self.0.lock().map_err(|_| Fail::Internal)?;
        entries.retain(|_, entry| entry.created.elapsed() < SEARCH_REF_TTL);
        let key_hash = Sha256::digest(indexer.api_key.0.as_bytes()).into();
        for result in results.iter() {
            if let Some(url) = &result.download_url {
                entries.insert(
                    result.nzb_ref.clone(),
                    SearchReference {
                        indexer_id: indexer.id,
                        revision,
                        base_url: indexer.base_url.clone(),
                        key_hash,
                        url: url.clone(),
                        title: result.title.clone(),
                        source_dns: result.source_dns.clone(),
                        created: Instant::now(),
                    },
                );
            }
        }
        let mut bytes: usize = entries
            .iter()
            .map(|(key, reference)| search_reference_bytes(key, reference))
            .sum();
        if entries.len() > SEARCH_REF_LIMIT || bytes > SEARCH_REF_MAX_BYTES {
            let mut oldest: Vec<_> = entries
                .iter()
                .map(|(key, reference)| (key.clone(), reference.created))
                .collect();
            oldest.sort_unstable_by_key(|(_, created)| *created);
            for (key, _) in oldest {
                if entries.len() <= SEARCH_REF_LIMIT && bytes <= SEARCH_REF_MAX_BYTES {
                    break;
                }
                if let Some(entry) = entries.remove(&key) {
                    bytes = bytes.saturating_sub(search_reference_bytes(&key, &entry));
                }
            }
        }
        for result in results.iter_mut() {
            if result.can_queue && !entries.contains_key(&result.nzb_ref) {
                result.can_queue = false;
                result.download_url = None;
            }
        }
        drop(entries);
        drop(revisions);
        Ok(true)
    }

    fn mark_available(&self, results: &mut [crate::newznab::SearchResult]) -> Result<(), Fail> {
        let entries = self.0.lock().map_err(|_| Fail::Internal)?;
        for result in results.iter_mut() {
            if result.can_queue
                && entries
                    .get(&result.nzb_ref)
                    .is_none_or(|entry| entry.created.elapsed() >= SEARCH_REF_TTL)
            {
                result.can_queue = false;
                result.download_url = None;
            }
        }
        Ok(())
    }

    fn resolve(&self, reference: &str) -> Result<SearchReference, Fail> {
        if reference.len() > 128 || reference.is_empty() {
            return Err(Fail::NotFound);
        }
        let mut entries = self.0.lock().map_err(|_| Fail::Internal)?;
        entries.retain(|_, entry| entry.created.elapsed() < SEARCH_REF_TTL);
        entries.get(reference).cloned().ok_or(Fail::NotFound)
    }
}

fn reference_is_current(
    settings: &Settings,
    reference: &SearchReference,
    refs: &SearchReferences,
) -> Result<bool, Fail> {
    Ok(refs.has_revision(reference.indexer_id, reference.revision)?
        && settings.indexers.iter().any(|indexer| {
            indexer.id == reference.indexer_id
                && indexer.enabled
                && indexer.base_url == reference.base_url
                && <[u8; 32]>::from(Sha256::digest(indexer.api_key.0.as_bytes()))
                    == reference.key_hash
        }))
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct SearchQueueRequest {
    nzb_ref: String,
    category: String,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum SearchQueueStatus {
    Created,
    Existing,
    Rejected,
}

#[derive(Serialize, ToSchema)]
struct SearchQueued {
    id: Uuid,
    status: SearchQueueStatus,
}

#[utoipa::path(post, path = "/api/v1/indexers/search/queue", tag = "indexer",
    request_body = SearchQueueRequest,
    responses((status = 201, body = SearchQueued), (status = 200, body = SearchQueued),
              (status = 422, body = SearchQueued), (status = 400, body = ApiError),
              (status = 404, body = ApiError), (status = 503, body = ApiError)))]
async fn post_indexer_search_queue(
    State(admin): State<Arc<Admin>>,
    Json(request): Json<SearchQueueRequest>,
) -> Result<(StatusCode, Json<SearchQueued>), Fail> {
    let category = request.category.trim();
    let reference = admin.search_refs.resolve(&request.nzb_ref)?;
    let (endpoint, indexer_key) = {
        let store = admin.config.read().await;
        let settings = store.settings();
        if !reference_is_current(settings, &reference, &admin.search_refs)? {
            return Err(Fail::NotFound);
        }
        if category.is_empty() || !settings.sab.categories().any(|known| known == category) {
            return Err(Fail::Bad("Select a configured queue category.".to_owned()));
        }
        let indexer = settings
            .indexers
            .iter()
            .find(|indexer| indexer.id == reference.indexer_id)
            .ok_or(Fail::NotFound)?;
        let endpoint = crate::newznab::endpoint(indexer).map_err(|_| Fail::NotFound)?;
        let key = indexer.api_key.0.clone();
        drop(store);
        (endpoint, key)
    };
    let _download_permit = admin
        .indexer_downloads
        .acquire()
        .await
        .map_err(|_| Fail::Unavailable("Indexer downloads are unavailable.".to_owned()))?;
    let permit = admin
        .indexer_work
        .acquire()
        .await
        .map_err(|_| Fail::Unavailable("Indexer downloads are unavailable.".to_owned()))?;
    let fetched = crate::fetch::Fetcher::fetch_indexer(
        &reference.url,
        &endpoint,
        &indexer_key,
        reference.source_dns.as_ref(),
    )
    .await
    .map_err(|_| Fail::Unavailable("Could not retrieve the indexer NZB.".to_owned()))?;
    drop(permit);
    let prepared = Queue::prepare(&fetched.body).await;
    let fallback_title = if crate::fetch::contains_api_key(&reference.title, &indexer_key) {
        "Indexer download"
    } else {
        &reference.title
    };
    let file_name = fetched
        .file_name
        .as_deref()
        .filter(|name| !name.trim().trim_start_matches('.').is_empty())
        .and_then(|name| crate::sab::nzb_file_name(name).ok())
        .unwrap_or_else(|| search_nzb_file_name(fallback_title));
    let job = Submission {
        file_name: &file_name,
        category,
        priority: 0,
        post_processing: 0,
    };
    commit_indexer_queue(
        &admin.config,
        &admin.search_refs,
        &admin.queue,
        &reference,
        &job,
        prepared,
    )
    .await
}

async fn commit_indexer_queue(
    config: &RwLock<ConfigStore>,
    refs: &SearchReferences,
    queue: &Queue,
    reference: &SearchReference,
    job: &Submission<'_>,
    prepared: Result<service::PreparedNzb, service::Error>,
) -> Result<(StatusCode, Json<SearchQueued>), Fail> {
    let _writing = queue.db().write_permit().await;
    let store = config.read().await;
    if !reference_is_current(store.settings(), reference, refs)?
        || !store
            .settings()
            .sab
            .categories()
            .any(|known| known == job.category)
    {
        return Err(Fail::NotFound);
    }
    let prepared = match prepared {
        Ok(prepared) => prepared,
        Err(service::Error::Nzb(_)) => return Err(Fail::Bad("Invalid NZB data.".to_owned())),
        Err(_) => return Err(Fail::Internal),
    };
    let (code, status, id) = match queue.enqueue_prepared(job, prepared).await {
        Ok(EnqueueOutcome::Created(id)) => (StatusCode::CREATED, SearchQueueStatus::Created, id),
        Ok(EnqueueOutcome::Existing(id)) => (StatusCode::OK, SearchQueueStatus::Existing, id),
        Ok(EnqueueOutcome::Rejected(id)) => (
            StatusCode::UNPROCESSABLE_ENTITY,
            SearchQueueStatus::Rejected,
            id,
        ),
        Err(_) => return Err(Fail::Internal),
    };
    drop(store);
    Ok((code, Json(SearchQueued { id: id.0, status })))
}

fn search_nzb_file_name(title: &str) -> String {
    let mut cleaned = String::new();
    for c in title.chars() {
        if cleaned.len() + c.len_utf8() > 240 {
            break;
        }
        if c.is_control() || matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|') {
            cleaned.push(' ');
        } else {
            cleaned.push(c);
        }
    }
    let cleaned = cleaned.trim_matches(|c: char| c.is_whitespace() || c == '.');
    if cleaned.is_empty() {
        return "Indexer download.nzb".to_owned();
    }
    crate::sab::nzb_file_name(cleaned).unwrap_or_else(|_| "Indexer download.nzb".to_owned())
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
struct IndexerSearchQuery {
    q: String,
    #[serde(default)]
    limit: Option<u16>,
}

#[cfg(test)]
mod search_reference_tests {
    use super::*;

    #[test]
    fn reference_cache_evicts_oldest_and_ignores_non_downloadable_results() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "mock".into(),
            base_url: "https://indexer.example/api".into(),
            api_key: config::Secret("secret".into()),
            enabled: true,
        };
        let make = |n: usize, can_queue: bool| crate::newznab::SearchResult {
            title: format!("Result {n}"),
            size_bytes: 123,
            indexer_id: indexer.id,
            indexer_name: "mock".into(),
            nzb_ref: format!("{}:{n}", indexer.id),
            can_queue,
            download_url: can_queue.then(|| format!("https://indexer.example/nzb/{n}")),
            source_dns: None,
        };
        let refs = SearchReferences::default();
        let revision = refs.revision(indexer.id).ok().unwrap();
        let mut first: Vec<_> = (0..SEARCH_REF_LIMIT).map(|n| make(n, true)).collect();
        assert_eq!(
            refs.remember(&indexer, revision, &mut first).ok(),
            Some(true)
        );
        assert_eq!(refs.0.lock().unwrap().len(), SEARCH_REF_LIMIT);
        let old = format!("{}:0", indexer.id);
        refs.0.lock().unwrap().get_mut(&old).unwrap().created =
            Instant::now().checked_sub(Duration::from_secs(30)).unwrap();
        let mut nonqueue: Vec<_> = (30_000..45_000).map(|n| make(n, false)).collect();
        assert_eq!(
            refs.remember(&indexer, revision, &mut nonqueue).ok(),
            Some(true)
        );
        assert_eq!(refs.0.lock().unwrap().len(), SEARCH_REF_LIMIT);
        assert!(refs.resolve(&old).is_ok());
        let mut more: Vec<_> = (20_000..20_005).map(|n| make(n, true)).collect();
        assert_eq!(
            refs.remember(&indexer, revision, &mut more).ok(),
            Some(true)
        );
        assert_eq!(refs.0.lock().unwrap().len(), SEARCH_REF_LIMIT);
        assert!(matches!(refs.resolve(&old), Err(Fail::NotFound)));
        assert!(refs.resolve(&format!("{}:9999", indexer.id)).is_ok());
        assert!(refs.resolve(&format!("{}:20004", indexer.id)).is_ok());

        let mut huge: Vec<_> = (50_000..62_345).map(|n| make(n, true)).collect();
        assert_eq!(
            refs.remember(&indexer, revision, &mut huge).ok(),
            Some(true)
        );
        assert!(
            huge.iter()
                .all(|result| !result.can_queue || refs.resolve(&result.nzb_ref).is_ok())
        );
        assert_eq!(refs.0.lock().unwrap().len(), SEARCH_REF_LIMIT);
        assert!(matches!(
            refs.resolve(&format!("{}:50000", indexer.id)),
            Err(Fail::NotFound)
        ));
        assert!(refs.resolve(&format!("{}:62344", indexer.id)).is_ok());
        let mut duplicated = vec![make(62_344, true), make(62_344, true)];
        assert_eq!(
            refs.remember(&indexer, revision, &mut duplicated).ok(),
            Some(true)
        );
        assert_eq!(refs.0.lock().unwrap().len(), SEARCH_REF_LIMIT);
    }

    #[test]
    fn stale_revision_cannot_restore_invalidated_cache_entries() {
        let indexer = Indexer {
            id: Uuid::new_v4(),
            name: "Mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: config::Secret("key".into()),
            enabled: true,
        };
        let refs = SearchReferences::default();
        let stale = refs.revision(indexer.id).ok().unwrap();
        refs.invalidate(indexer.id).ok().unwrap();
        let mut results = [crate::newznab::SearchResult {
            title: "Result".into(),
            size_bytes: 100,
            indexer_id: indexer.id,
            indexer_name: indexer.name.clone().into(),
            nzb_ref: Uuid::new_v4().to_string(),
            can_queue: true,
            download_url: Some("https://indexer.invalid/get".into()),
            source_dns: None,
        }];
        assert_eq!(
            refs.remember(&indexer, stale, &mut results).ok(),
            Some(false)
        );
        assert!(matches!(
            refs.resolve(&results[0].nzb_ref),
            Err(Fail::NotFound)
        ));
        let current = refs.revision(indexer.id).ok().unwrap();
        assert_ne!(current, stale);
        assert_eq!(
            refs.remember(&indexer, current, &mut results).ok(),
            Some(true)
        );
        assert!(refs.resolve(&results[0].nzb_ref).is_ok());
    }

    #[test]
    fn cache_memory_budget_downgrades_unstored_queueable_results() {
        let indexer = Indexer {
            id: Uuid::new_v4(),
            name: "Mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: config::Secret("key".into()),
            enabled: true,
        };
        let refs = SearchReferences::default();
        let revision = refs.revision(indexer.id).ok().unwrap();
        let mut results: Vec<_> = (0..32)
            .map(|n| crate::newznab::SearchResult {
                title: "x".repeat(650_000),
                size_bytes: 10,
                indexer_id: indexer.id,
                indexer_name: "Mock".into(),
                nzb_ref: format!("ref-{n}"),
                can_queue: true,
                download_url: Some(format!("https://indexer.invalid/get/{n}")),
                source_dns: None,
            })
            .collect();
        assert_eq!(
            refs.remember(&indexer, revision, &mut results).ok(),
            Some(true)
        );
        let entries = refs.0.lock().unwrap();
        let bytes: usize = entries
            .iter()
            .map(|(key, value)| search_reference_bytes(key, value))
            .sum();
        assert!(bytes <= SEARCH_REF_MAX_BYTES);
        assert!(results.iter().any(|item| !item.can_queue));
        assert!(
            results
                .iter()
                .all(|item| !item.can_queue || entries.contains_key(&item.nzb_ref))
        );
        drop(entries);
    }

    #[test]
    fn unsafe_download_names_fall_back_to_sanitized_titles() {
        assert_eq!(search_nzb_file_name("My Movie"), "My Movie.nzb");
        assert_eq!(
            search_nzb_file_name(" ../My/Movie:Part?.nzb "),
            "My Movie Part .nzb"
        );
        assert_eq!(
            search_nzb_file_name(" \\\\/:*?<>| "),
            "Indexer download.nzb"
        );
        let long = search_nzb_file_name(&"ü".repeat(1000));
        assert!(long.len() <= 255);
        assert!(
            std::path::Path::new(&long)
                .extension()
                .is_some_and(|ext| ext.eq_ignore_ascii_case("nzb"))
        );
    }

    #[tokio::test]
    async fn search_reference_cache_keeps_local_dns_pin() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "mock".into(),
            base_url: "http://127.0.0.1:1234/api".into(),
            api_key: config::Secret("mock-key".into()),
            enabled: true,
        };
        let dns =
            crate::fetch::IndexerDns::lookup(&reqwest::Url::parse(&indexer.base_url).unwrap())
                .await
                .unwrap();
        let result = crate::newznab::SearchResult {
            title: "Result".into(),
            size_bytes: 123,
            indexer_id: indexer.id,
            indexer_name: indexer.name.clone().into(),
            nzb_ref: "opaque-reference".into(),
            can_queue: true,
            download_url: Some("http://127.0.0.1:1234/get".into()),
            source_dns: Some(dns),
        };
        assert!(
            !serde_json::to_string(&result)
                .unwrap()
                .contains("source_dns")
        );
        let refs = SearchReferences::default();
        let revision = refs.revision(indexer.id).ok().unwrap();
        assert_eq!(
            refs.remember(&indexer, revision, &mut [result]).ok(),
            Some(true)
        );
        assert!(
            refs.resolve("opaque-reference")
                .ok()
                .unwrap()
                .source_dns
                .unwrap()
                .is_local()
        );
    }

    #[test]
    fn refs_expire_and_reject_tampering_or_disabled_indexers() {
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "mock".into(),
            base_url: "https://indexer.invalid/api".into(),
            api_key: config::Secret("secret".into()),
            enabled: true,
        };
        let reference = format!("{}:opaque-token", indexer.id);
        let result = crate::newznab::SearchResult {
            title: "sample".into(),
            size_bytes: 12,
            indexer_id: indexer.id,
            indexer_name: indexer.name.clone().into(),
            nzb_ref: reference.clone(),
            can_queue: true,
            download_url: Some("https://indexer.invalid/get".into()),
            source_dns: None,
        };
        let refs = SearchReferences::default();
        let revision = refs.revision(indexer.id).ok().unwrap();
        assert_eq!(
            refs.remember(&indexer, revision, &mut [result]).ok(),
            Some(true)
        );
        assert!(refs.resolve(&reference).is_ok());
        assert!(matches!(
            refs.resolve(&(reference.clone() + "x")),
            Err(Fail::NotFound)
        ));
        assert!(matches!(
            refs.resolve("http://example.com/file.nzb"),
            Err(Fail::NotFound)
        ));
        let settings = Settings {
            indexers: vec![indexer],
            ..Settings::default()
        };
        let saved = refs.resolve(&reference).ok().unwrap();
        assert_eq!(
            reference_is_current(&settings, &saved, &refs).ok(),
            Some(true)
        );
        let mut disabled = settings.clone();
        disabled.indexers[0].enabled = false;
        assert_eq!(
            reference_is_current(&disabled, &saved, &refs).ok(),
            Some(false)
        );
        disabled.indexers.clear();
        assert_eq!(
            reference_is_current(&disabled, &saved, &refs).ok(),
            Some(false)
        );
        let mut rotated = settings;
        rotated.indexers[0].api_key = config::Secret("new-secret".into());
        assert_eq!(
            reference_is_current(&rotated, &saved, &refs).ok(),
            Some(false)
        );
        refs.0.lock().unwrap().get_mut(&reference).unwrap().created =
            Instant::now().checked_sub(SEARCH_REF_TTL).unwrap();
        assert!(matches!(refs.resolve(&reference), Err(Fail::NotFound)));
    }
}

#[derive(Serialize, ToSchema)]
struct IndexerSearchStatus {
    indexer_id: Uuid,
    indexer_name: String,
    ok: bool,
    result_count: usize,
    error: Option<&'static str>,
}

#[derive(Serialize, ToSchema)]
struct IndexerSearchResponse {
    results: Vec<crate::newznab::SearchResult>,
    indexers: Vec<IndexerSearchStatus>,
}

#[utoipa::path(get, path = "/api/v1/indexers/search", tag = "indexer",
    params(IndexerSearchQuery),
    responses((status = 200, body = IndexerSearchResponse), (status = 400, body = ApiError)))]
async fn get_indexer_search(
    State(admin): State<Arc<Admin>>,
    Query(request): Query<IndexerSearchQuery>,
) -> Result<Json<IndexerSearchResponse>, Fail> {
    let query = request.q.trim();
    if query.is_empty() || query.chars().count() > 250 {
        return Err(Fail::Bad(
            "Search query must contain 1-250 characters.".to_owned(),
        ));
    }
    let limit = request.limit.unwrap_or(100);
    if !(1..=500).contains(&limit) {
        return Err(Fail::Bad("Search limit must be 1-500.".to_owned()));
    }
    let indexers: Vec<_> = {
        let store = admin.config.read().await;
        store
            .settings()
            .indexers
            .iter()
            .filter(|indexer| indexer.enabled)
            .map(|indexer| Ok((indexer.clone(), admin.search_refs.revision(indexer.id)?)))
            .collect::<Result<Vec<_>, Fail>>()?
    };
    let status_room = indexers.iter().try_fold(64usize, |total, (indexer, _)| {
        let public_name = indexer.name.replace(&indexer.api_key.0, "[redacted]");
        let name_bytes = serde_json::to_string(&public_name)
            .map_err(|_| Fail::Internal)?
            .len();
        Ok::<usize, Fail>(total.saturating_add(name_bytes + 256))
    })?;
    let result_budget = SEARCH_RESPONSE_MAX_BYTES.saturating_sub(status_room);
    let mut used_bytes = 0usize;
    let mut results = Vec::new();
    let mut statuses = Vec::with_capacity(indexers.len());
    let pending = indexers.clone();
    let mut searched = stream::iter(pending.into_iter().map(|(indexer, revision)| {
        let admin = Arc::clone(&admin);
        async move {
            let permit = admin
                .indexer_work
                .acquire()
                .await
                .map_err(|_| "Indexer searches are unavailable.")?;
            if !admin
                .search_refs
                .has_revision(indexer.id, revision)
                .map_err(|_| "Indexer searches are unavailable.")?
            {
                return Err("Indexer configuration changed during search.");
            }
            let searched = crate::newznab::search(&indexer, query, limit).await;
            drop(permit);
            searched
        }
    }))
    .buffered(INDEXER_WORK_LIMIT);
    for (indexer, revision) in &indexers {
        let Some(outcome) = searched.next().await else {
            break;
        };
        let public_name = indexer.name.replace(&indexer.api_key.0, "[redacted]");
        match outcome {
            Ok(items) => {
                let mut accepted = Vec::new();
                let mut accepted_bytes = 0usize;
                let mut truncated = false;
                for item in items {
                    if results.len() + accepted.len() >= SEARCH_RESPONSE_MAX_RESULTS {
                        truncated = true;
                        break;
                    }
                    let row_bytes =
                        serde_json::to_vec(&item).map_err(|_| Fail::Internal)?.len() + 2;
                    if row_bytes > result_budget.saturating_sub(used_bytes) {
                        truncated = true;
                        break;
                    }
                    used_bytes += row_bytes;
                    accepted_bytes += row_bytes;
                    accepted.push(item);
                }
                let current = {
                    let store = admin.config.read().await;
                    store
                        .settings()
                        .indexers
                        .iter()
                        .any(|saved| saved.enabled && saved == indexer)
                        && admin
                            .search_refs
                            .remember(indexer, *revision, &mut accepted)?
                };
                if !current {
                    used_bytes -= accepted_bytes;
                    statuses.push(IndexerSearchStatus {
                        indexer_id: indexer.id,
                        indexer_name: public_name,
                        ok: false,
                        result_count: 0,
                        error: Some("Indexer configuration changed during search."),
                    });
                    continue;
                }
                statuses.push(IndexerSearchStatus {
                    indexer_id: indexer.id,
                    indexer_name: public_name,
                    ok: true,
                    result_count: accepted.len(),
                    error: truncated.then_some("Search results truncated to response limit."),
                });
                results.append(&mut accepted);
            }
            Err(error) => {
                statuses.push(IndexerSearchStatus {
                    indexer_id: indexer.id,
                    indexer_name: public_name,
                    ok: false,
                    result_count: 0,
                    error: Some(error),
                });
            }
        }
    }
    drop(searched);
    admin.search_refs.mark_available(&mut results)?;
    let response = IndexerSearchResponse {
        results,
        indexers: statuses,
    };
    debug_assert!(
        serde_json::to_vec(&response).is_ok_and(|bytes| bytes.len() <= SEARCH_RESPONSE_MAX_BYTES)
    );
    Ok(Json(response))
}

#[utoipa::path(post, path = "/api/v1/indexers/{id}/test", tag = "indexer",
    params(("id" = Uuid, Path)),
    responses((status = 200, body = IndexerTest), (status = 404, body = ApiError)))]
async fn post_indexer_test(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<Uuid>,
) -> Result<Json<IndexerTest>, Fail> {
    let indexer = admin
        .config
        .read()
        .await
        .settings()
        .indexers
        .iter()
        .find(|i| i.id == id)
        .cloned()
        .ok_or(Fail::NotFound)?;
    let permit = admin
        .indexer_work
        .acquire()
        .await
        .map_err(|_| Fail::Internal)?;
    let error = crate::newznab::test(&indexer).await.err();
    drop(permit);
    if let Some(reason) = error {
        tracing::warn!(reason, "Indexer connection test failed");
    }
    Ok(Json(IndexerTest {
        success: error.is_none(),
        error,
    }))
}

#[derive(Serialize, ToSchema)]
struct IndexerTest {
    success: bool,
    error: Option<&'static str>,
}

#[cfg(test)]
mod indexer_commit_tests {
    use super::*;
    use infinidysk_db::Db;
    use infinidysk_nntp::Provider;

    #[tokio::test]
    async fn queue_commit_rechecks_indexer_and_category_after_write_gate() {
        for disable_indexer in [true, false] {
            let db = Db::connect("sqlite::memory:").await.unwrap();
            let queue = Arc::new(Queue::new(
                db.clone(),
                Arc::new(Client::new(Vec::<Provider>::new())),
            ));
            let indexer = Indexer {
                id: Uuid::now_v7(),
                name: "sample".to_owned(),
                base_url: "https://indexer.example/api".to_owned(),
                api_key: config::Secret("sample-key".to_owned()),
                enabled: true,
            };
            let mut store = ConfigStore::load(db.clone(), []).await.unwrap();
            let mut settings = store.settings().clone();
            settings.indexers.push(indexer.clone());
            store.save(settings).await.unwrap();
            let config = Arc::new(RwLock::new(store));
            let refs = Arc::new(SearchReferences::default());
            let revision = refs.revision(indexer.id).ok().unwrap();
            let reference = SearchReference {
                indexer_id: indexer.id,
                revision,
                base_url: indexer.base_url,
                key_hash: Sha256::digest(indexer.api_key.0.as_bytes()).into(),
                url: "https://indexer.example/file.nzb".to_owned(),
                title: "sample".to_owned(),
                source_dns: None,
                created: Instant::now(),
            };
            let prepared = Queue::prepare(br#"<?xml version="1.0"?><nzb><file poster="p" date="0" subject="a.mkv"><groups><group>a.b</group></groups><segments><segment bytes="100" number="1">a@b</segment></segments></file></nzb>"#)
                .await
                .unwrap();
            let pause = db.pause_writes().await;
            let start = Arc::new(tokio::sync::Barrier::new(2));
            let task = tokio::spawn({
                let config = Arc::clone(&config);
                let refs = Arc::clone(&refs);
                let queue = Arc::clone(&queue);
                let start = Arc::clone(&start);
                async move {
                    let job = Submission {
                        file_name: "sample.nzb",
                        category: "movies",
                        priority: 0,
                        post_processing: 0,
                    };
                    start.wait().await;
                    commit_indexer_queue(&config, &refs, &queue, &reference, &job, Ok(prepared))
                        .await
                }
            });
            start.wait().await;
            tokio::task::yield_now().await;
            {
                let mut store = tokio::time::timeout(Duration::from_secs(2), config.write())
                    .await
                    .expect("queue commit must not hold config while awaiting DB write gate");
                let mut settings = store.settings().clone();
                if disable_indexer {
                    settings.indexers[0].enabled = false;
                } else {
                    settings
                        .sab
                        .categories
                        .retain(|category| category != "movies");
                }
                store.save(settings).await.unwrap();
                if disable_indexer {
                    assert!(refs.invalidate(indexer.id).is_ok());
                }
                drop(store);
            }
            drop(pause);
            assert!(matches!(task.await.unwrap(), Err(Fail::NotFound)));
            assert_eq!(db.queue().await.unwrap().len(), 0);
            db.close().await;
        }
    }

    #[tokio::test]
    async fn invalid_indexer_nzb_returns_bad_request_without_changing_history() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        let queue = Queue::new(db.clone(), Arc::new(Client::new(Vec::<Provider>::new())));
        let indexer = Indexer {
            id: Uuid::now_v7(),
            name: "sample".to_owned(),
            base_url: "https://indexer.example/api".to_owned(),
            api_key: config::Secret("sample-key".to_owned()),
            enabled: true,
        };
        let mut store = ConfigStore::load(db.clone(), []).await.unwrap();
        let mut settings = store.settings().clone();
        settings.indexers.push(indexer.clone());
        store.save(settings).await.unwrap();
        let config = RwLock::new(store);
        let refs = SearchReferences::default();
        let reference = SearchReference {
            indexer_id: indexer.id,
            revision: refs.revision(indexer.id).ok().unwrap(),
            base_url: indexer.base_url,
            key_hash: Sha256::digest(indexer.api_key.0.as_bytes()).into(),
            url: "https://indexer.example/file.nzb".to_owned(),
            title: "sample".to_owned(),
            source_dns: None,
            created: Instant::now(),
        };
        let prior = Submission {
            file_name: "prior.nzb",
            category: "movies",
            priority: 0,
            post_processing: 0,
        };
        queue
            .refuse(&prior, 0, "prior failure".to_owned())
            .await
            .unwrap();
        let before = db.history().await.unwrap();
        assert_eq!(before.len(), 1);
        let job = Submission {
            file_name: "invalid.nzb",
            category: "movies",
            priority: 0,
            post_processing: 0,
        };
        let prepared = Queue::prepare(b"Not an NZB").await;
        assert!(matches!(prepared, Err(service::Error::Nzb(_))));
        let result = commit_indexer_queue(&config, &refs, &queue, &reference, &job, prepared).await;
        assert!(matches!(result, Err(Fail::Bad(_))));
        assert_eq!(db.history().await.unwrap(), before);
        assert_eq!(db.queue().await.unwrap().len(), 0);
        db.close().await;
    }
}
