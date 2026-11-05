//! Arr webhooks (import policy §10.4): registering ours on each Arr that downloads
//! through us, accepting deliveries, and applying them to the items the Arrs own.

use std::collections::BTreeSet;
use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::body::Bytes;
use axum::extract::{DefaultBodyLimit, Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::routing::post;
use infinidysk_arr::webhook::{self, Event, File, Hook};
use infinidysk_arr::{self as arr, Arr, SabClient};
use infinidysk_core::JobId;
use infinidysk_db::{Db, OwnedFile, now_ms};
use tokio::sync::watch;
use uuid::Uuid;

use super::{ArrBinding, Discovery, RETRY_MAX, RETRY_MIN, item_at};

/// Basic auth user name; the password is our SAB API key, which every Arr calling us
/// already holds and which already grants more than a webhook can do.
const USER: &str = "infinidysk";
/// Config key of the id that tells our webhooks apart from another install's.
const INSTALL_KEY: &str = "install_id";
/// Largest delivery accepted; a season-pack import lists every file.
const MAX_BODY: usize = 4_000_000;

pub fn router(discovery: Arc<Discovery>) -> Router {
    Router::new()
        .route(
            "/api/v1/arr/{id}/webhook",
            post(receive).layer(DefaultBodyLimit::max(MAX_BODY)),
        )
        .with_state(discovery)
}

/// Stores a delivery for the worker; answers once it is durable.
async fn receive(
    State(discovery): State<Arc<Discovery>>,
    Path(id): Path<Uuid>,
    headers: HeaderMap,
    body: Bytes,
) -> StatusCode {
    let key = discovery
        .config
        .read()
        .await
        .settings()
        .sab
        .api_key
        .0
        .clone();
    let auth = infinidysk_webdav::Auth::Basic {
        user: USER.to_owned(),
        password: key,
    };
    if !infinidysk_webdav::authorized(&auth, &headers) {
        return StatusCode::UNAUTHORIZED;
    }
    if !discovery.lock().contains_key(&id) {
        return StatusCode::NOT_FOUND;
    }
    match webhook::parse(&body) {
        Err(_) => StatusCode::BAD_REQUEST,
        Ok(Event::Test | Event::Ignored) => StatusCode::OK,
        Ok(_) => {
            let db = discovery.queue.db();
            let _writing = db.write_permit().await;
            match db.push_arr_event(id, &body).await {
                Ok(()) => {
                    discovery.wake.notify_one();
                    StatusCode::OK
                }
                Err(error) => {
                    tracing::warn!(reason = %error, "storing an Arr webhook delivery failed");
                    StatusCode::SERVICE_UNAVAILABLE
                }
            }
        }
    }
}

/// Why a delivery did not apply.
enum Failure {
    /// Try again later.
    Retry(String),
    /// Never applies.
    Drop(String),
}

impl From<infinidysk_db::Error> for Failure {
    fn from(error: infinidysk_db::Error) -> Self {
        Self::Retry(error.to_string())
    }
}

impl From<arr::Error> for Failure {
    fn from(error: arr::Error) -> Self {
        if error.is_transient() {
            Self::Retry(error.to_string())
        } else {
            Self::Drop(error.to_string())
        }
    }
}

impl Discovery {
    /// Registers our webhook on `arr` when one of its verified clients is us. Returns the
    /// URL it sends to, or why it could not be registered.
    pub(super) async fn register(
        &self,
        arr: &Arr,
        clients: &[(SabClient, ArrBinding)],
        sab_key: &str,
    ) -> Result<Option<String>, String> {
        let bases: BTreeSet<String> = clients
            .iter()
            .filter(|(_, b)| *b == ArrBinding::Verified)
            .map(|(c, _)| base(c))
            .collect();
        if bases.is_empty() {
            return Ok(None);
        }
        let public = self
            .config
            .read()
            .await
            .settings()
            .network
            .public_url
            .clone();
        let base = match (public.is_empty(), bases.len()) {
            (false, _) => public,
            (true, 1) => bases.into_iter().next().unwrap_or_default(),
            (true, _) => {
                return Err(
                    "its download clients reach this app at different addresses; set a public URL"
                        .to_owned(),
                );
            }
        };
        let url = format!("{base}/api/v1/arr/{}/webhook", arr.id);
        let install = self.install_id().await.map_err(|e| e.to_string())?;
        let hook = Hook {
            name: &format!("infinidysk-{install}"),
            url: &url,
            username: USER,
            password: sab_key,
        };
        match webhook::register(&self.http, arr, &hook).await {
            Ok(()) => Ok(Some(url)),
            Err(error) => {
                tracing::warn!(arr = %arr.name, reason = %error, "registering the Arr webhook failed");
                Err(error.to_string())
            }
        }
    }

    async fn install_id(&self) -> Result<&str, infinidysk_db::Error> {
        self.install
            .get_or_try_init(|| async {
                let db = self.queue.db();
                let stored = db
                    .config()
                    .await?
                    .into_iter()
                    .find(|(k, _)| k == INSTALL_KEY)
                    .and_then(|(_, v)| serde_json::from_str::<String>(&v).ok());
                if let Some(id) = stored {
                    return Ok(id);
                }
                let mut id = Uuid::new_v4().simple().to_string();
                id.truncate(8);
                db.write_config(&[(INSTALL_KEY, &format!("\"{id}\""))], &[])
                    .await?;
                Ok(id)
            })
            .await
            .map(String::as_str)
    }

    /// Applies stored deliveries oldest first. Deliveries from instances no longer
    /// enabled are dropped; transient failures retry with backoff, in order.
    pub async fn apply_events(&self, instances: watch::Receiver<Vec<Arr>>) {
        let db = self.queue.db().clone();
        let mut retry = Duration::ZERO;
        loop {
            let next = match db.next_arr_event().await {
                Ok(Some(next)) => next,
                Ok(None) => {
                    self.wake.notified().await;
                    continue;
                }
                Err(error) => {
                    tracing::warn!(reason = %error, "reading Arr webhook deliveries failed");
                    retry = (retry * 2).clamp(RETRY_MIN, RETRY_MAX);
                    tokio::time::sleep(retry).await;
                    continue;
                }
            };
            let (id, instance, body) = next;
            let writing = db.write_permit().await;
            let arr = instances
                .borrow()
                .iter()
                .find(|a| a.id == instance)
                .cloned();
            let result = match &arr {
                Some(arr) => self.apply(&db, arr, &body).await,
                None => Err(Failure::Drop("the instance is not enabled".to_owned())),
            };
            let error = match result {
                Ok(()) => None,
                Err(Failure::Retry(reason)) => {
                    tracing::warn!(reason, "applying an Arr webhook delivery failed; retrying");
                    self.delivered(instance, Some(reason));
                    drop(writing);
                    retry = (retry * 2).clamp(RETRY_MIN, RETRY_MAX);
                    tokio::time::sleep(retry).await;
                    continue;
                }
                Err(Failure::Drop(reason)) => {
                    tracing::warn!(reason, "dropping an Arr webhook delivery");
                    Some(reason)
                }
            };
            retry = Duration::ZERO;
            self.delivered(instance, error);
            if let Err(error) = db.drop_arr_event(id).await {
                tracing::warn!(reason = %error, "removing an applied Arr webhook delivery failed");
            }
        }
    }

    fn delivered(&self, instance: Uuid, error: Option<String>) {
        if let Some(k) = self.lock().get_mut(&instance) {
            k.delivery_at_ms = Some(now_ms());
            k.delivery_error = error;
        }
    }

    async fn apply(&self, db: &Db, arr: &Arr, body: &[u8]) -> Result<(), Failure> {
        let event = webhook::parse(body).map_err(|e| Failure::Drop(e.to_string()))?;
        match event {
            Event::Test | Event::Ignored => {}
            Event::Imported {
                download_id,
                files,
                replaced,
            } => {
                for file in &replaced {
                    release(db, arr, which(file.id, &file.path)).await?;
                }
                let job = download_id.and_then(|d| Uuid::parse_str(&d).ok());
                if let Some(job) = job {
                    self.adopt(db, arr, JobId(job), files).await?;
                }
            }
            Event::Deleted(files) => {
                for file in &files {
                    release(db, arr, which(file.id, &file.path)).await?;
                }
            }
            Event::FolderDeleted(folder) => release(db, arr, OwnedFile::Under(folder)).await?,
            Event::Reconcile => self.request_reconcile(arr.id),
            Event::Renamed(moves) => {
                for (file, previous) in moves {
                    db.rename_owner(arr.id, &which(file.id, &previous), &file.path)
                        .await?;
                }
            }
        }
        Ok(())
    }

    /// Records `arr` as the owner of the items of job `job` it imported as `files`.
    async fn adopt(&self, db: &Db, arr: &Arr, job: JobId, files: Vec<File>) -> Result<(), Failure> {
        let Some(dir) = db.job_dir(job).await? else {
            return Ok(());
        };
        let storage = db.history_entry(job).await?.and_then(|h| h.storage);
        let mut history = None;
        for file in files {
            if file.source.is_none() && history.is_none() {
                history = Some(arr::imports(&self.http, arr, &job.to_string()).await?);
            }
            let dropped = file.source.as_deref().or_else(|| {
                history
                    .iter()
                    .flatten()
                    .find(|(_, imported)| *imported == file.path)
                    .map(|(dropped, _)| dropped.as_str())
            });
            let Some(rel) = dropped.and_then(|d| relative(&dir, d)) else {
                continue;
            };
            let handed_off = match &storage {
                Some(storage) => db.handoff_item(&format!("{storage}/{rel}")).await?,
                None => None,
            };
            let item = match handed_off {
                Some(id) => db.item(id).await?,
                None => item_at(db, &format!("{dir}/{rel}")).await?,
            };
            if let Some(item) = item {
                db.record_owner(arr.id, item.id, file.id, &file.path, Some(job))
                    .await?;
                self.imported
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .push(file.path);
            }
        }
        Ok(())
    }
}

/// Forgets `arr`'s files matching `which` and deletes the items nobody owns anymore.
async fn release(db: &Db, arr: &Arr, which: OwnedFile) -> Result<(), Failure> {
    for item in db.forget_owners(arr.id, &which).await? {
        if db.delete_unowned(item).await? {
            tracing::info!(arr = %arr.name, item = %item, "deleted a file its Arr deleted");
        }
    }
    Ok(())
}

fn which(id: Option<i64>, path: &str) -> OwnedFile {
    id.map_or_else(|| OwnedFile::Path(path.to_owned()), OwnedFile::Id)
}

/// Where a SAB client reaches us.
fn base(client: &SabClient) -> String {
    let scheme = if client.use_ssl { "https" } else { "http" };
    let url_base = client.url_base.trim_matches('/');
    let url_base = if url_base.is_empty() {
        String::new()
    } else {
        format!("/{url_base}")
    };
    format!("{scheme}://{}:{}{url_base}", client.host, client.port)
}

/// The path below job directory `dir` (`/content/<category>/<job>`) of a file the Arr
/// imported from `dropped`, wherever it mounted us.
fn relative(dir: &str, dropped: &str) -> Option<String> {
    let key = format!("/{}/", dir.strip_prefix("/content/")?);
    let dropped = dropped.replace('\\', "/");
    let (_, rel) = dropped.split_once(&key)?;
    (!rel.is_empty()).then(|| rel.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_the_file_below_the_job_wherever_the_arr_mounted_us() {
        let rel = |dropped| relative("/content/tv/Job", dropped);
        assert_eq!(
            rel("/mnt/dysk/content/tv/Job/a/b.mkv").as_deref(),
            Some("a/b.mkv")
        );
        assert_eq!(rel("Z:\\tv\\Job\\b.mkv").as_deref(), Some("b.mkv"));
        assert_eq!(rel("/mnt/tv/Job2/b.mkv"), None);
        assert_eq!(rel("/mnt/tv/Job/"), None);
    }
}
