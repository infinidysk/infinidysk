//! Arr discovery (import policy §10.1): which SAB download clients in each enabled Arr
//! instance point at this installation, and the categories they own. Owners hear about
//! finished jobs (§10.2), get stuck imports cleared (§10.3) and tell us about theirs
//! through webhooks (§10.4).

mod queue;
mod reconcile;
mod webhook;

use std::collections::hash_map::Entry;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use infinidysk_arr::{self as arr, Arr, Profile, SabClient};
use infinidysk_core::media::{self, Kind};
use infinidysk_db::{Change, Db, Item, now_ms};
use infinidysk_queue::Queue;
use serde::Serialize;
use tokio::sync::broadcast::error::{RecvError, TryRecvError};
use tokio::sync::{Notify, OnceCell, RwLock, watch};
use utoipa::ToSchema;
use uuid::Uuid;

pub use queue::ArrQueueIssue;
pub use reconcile::{HeldFile, HeldReconcile};
pub use webhook::router;

use crate::config::ConfigStore;
use crate::sab;

const EVERY: Duration = Duration::from_hours(1);
/// Finished jobs within this window share one refresh per Arr.
const COALESCE: Duration = Duration::from_secs(5);
const RETRY_MIN: Duration = Duration::from_mins(1);
const RETRY_MAX: Duration = Duration::from_mins(15);
/// First retry of a failed discovery; Arrs starting alongside us are often up by then.
const DISCOVER_RETRY_MIN: Duration = Duration::from_secs(15);
/// URL-base suffix that marks an Arr's test calls as our probe.
const MARKER: &str = "infinidysk-probe-";

/// Probes in flight: nonce → whether its calls arrived with a valid API key.
#[derive(Default)]
pub struct Probes(Mutex<HashMap<String, bool>>);

/// A probe nonce, carried from the rewritten request path to the SAB API.
#[derive(Clone)]
pub struct Probe(pub String);

impl Probes {
    fn start(&self) -> Pending<'_> {
        let nonce = Uuid::new_v4().simple().to_string();
        self.lock().insert(nonce.clone(), false);
        Pending {
            probes: self,
            nonce,
        }
    }

    /// Records that a probe's call arrived with this installation's API key.
    pub fn arrived(&self, nonce: &str) {
        if let Some(seen) = self.lock().get_mut(nonce) {
            *seen = true;
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, bool>> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Removes its nonce when the probe ends, cancelled or not.
struct Pending<'a> {
    probes: &'a Probes,
    nonce: String,
}

impl Pending<'_> {
    fn marker(&self) -> String {
        format!("{MARKER}{}", self.nonce)
    }

    fn arrived(&self) -> bool {
        self.probes.lock().get(&self.nonce) == Some(&true)
    }
}

impl Drop for Pending<'_> {
    fn drop(&mut self) {
        self.probes.lock().remove(&self.nonce);
    }
}

/// The nonce of a probe call path, `…/infinidysk-probe-<nonce>/api`.
pub fn nonce(path: &str) -> Option<&str> {
    let nonce = path
        .strip_suffix("/api")?
        .rsplit('/')
        .next()?
        .strip_prefix(MARKER)?;
    (!nonce.is_empty() && nonce.bytes().all(|b| b.is_ascii_hexdigit())).then_some(nonce)
}

/// Whether a download client is this installation.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum ArrBinding {
    /// Its API key is ours, or the Arr's test of it reached us with our key.
    Verified,
    /// Its key is masked and the Arr's test of it did not reach us with our key.
    Unverified,
    /// Its key is not ours.
    Other,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum MediaKind {
    Video,
    Audio,
    Book,
}

impl From<Kind> for MediaKind {
    fn from(kind: Kind) -> Self {
        match kind {
            Kind::Video => Self::Video,
            Kind::Audio => Self::Audio,
            Kind::Book => Self::Book,
        }
    }
}

#[derive(Clone)]
struct Known {
    arr: Arr,
    sab_key: String,
    checked_at_ms: Option<i64>,
    version: Option<String>,
    error: Option<String>,
    /// Enabled SAB clients, without their keys.
    clients: Vec<(SabClient, ArrBinding)>,
    webhook_url: Option<String>,
    webhook_error: Option<String>,
    delivery_at_ms: Option<i64>,
    delivery_error: Option<String>,
    queue_issues: Vec<ArrQueueIssue>,
}

/// What discovery last found; secret-free.
#[derive(Serialize, ToSchema)]
pub struct ArrStatus {
    pub instances: Vec<ArrInstanceStatus>,
    /// Categories owned through verified clients, with what they import.
    pub categories: Vec<CategoryOwners>,
}

#[derive(Serialize, ToSchema)]
pub struct ArrInstanceStatus {
    pub id: Uuid,
    pub name: String,
    /// `None` until the first discovery finishes.
    pub checked_at_ms: Option<i64>,
    pub version: Option<String>,
    /// Why the last discovery failed; earlier findings stay listed while it is transient.
    pub error: Option<String>,
    pub clients: Vec<ArrClientStatus>,
    /// Where our webhook on this Arr sends; `None` while it is not registered, such as
    /// when no download client of this Arr is this installation.
    pub webhook_url: Option<String>,
    /// Why registering the webhook failed.
    pub webhook_error: Option<String>,
    /// When a webhook delivery was last handled; `None` if never since the last change.
    pub delivery_at_ms: Option<i64>,
    /// Why the last delivery failed to apply.
    pub delivery_error: Option<String>,
    /// Downloads in its queue whose import only the user can settle.
    pub queue_issues: Vec<ArrQueueIssue>,
}

#[derive(Serialize, ToSchema)]
pub struct ArrClientStatus {
    pub name: String,
    pub category: Option<String>,
    pub binding: ArrBinding,
}

#[derive(Serialize, ToSchema)]
pub struct CategoryOwners {
    pub category: String,
    /// A download in this category must contain one of these.
    pub media: Vec<MediaKind>,
    pub instances: Vec<Uuid>,
}

pub struct Discovery {
    http: reqwest::Client,
    config: Arc<RwLock<ConfigStore>>,
    probes: Arc<Probes>,
    queue: Arc<Queue>,
    known: Mutex<HashMap<Uuid, Known>>,
    /// Wakes the webhook worker.
    wake: Notify,
    /// Starts a discovery now.
    rediscover: Notify,
    install: OnceCell<String>,
    /// Serializes updates of the held reconciliations.
    held_lock: tokio::sync::Mutex<()>,
    /// Whether a discovery pass has finished since start.
    discovered: watch::Sender<bool>,
    /// Library paths Arrs imported from us since the library converter last looked, as
    /// the Arrs see them.
    pub imported: Mutex<Vec<String>>,
}

impl Discovery {
    pub fn new(
        config: Arc<RwLock<ConfigStore>>,
        probes: Arc<Probes>,
        queue: Arc<Queue>,
    ) -> Result<Self, reqwest::Error> {
        Ok(Self {
            http: reqwest::Client::builder()
                .timeout(Duration::from_secs(30))
                .build()?,
            config,
            probes,
            queue,
            known: Mutex::default(),
            wake: Notify::new(),
            rediscover: Notify::new(),
            install: OnceCell::new(),
            held_lock: tokio::sync::Mutex::default(),
            discovered: watch::channel(false).0,
            imported: Mutex::default(),
        })
    }

    pub const fn http(&self) -> &reqwest::Client {
        &self.http
    }

    /// Discovers at start, hourly, on request and whenever `instances` changes; a change
    /// cancels a discovery in progress. A pass where an Arr failed retries with backoff,
    /// so Arrs that start after us are found without a restart.
    pub async fn run(&self, mut instances: watch::Receiver<Vec<Arr>>) {
        let mut retry = Duration::ZERO;
        loop {
            let arrs = instances.borrow_and_update().clone();
            tokio::select! {
                ok = self.discover(&arrs) => {
                    retry = if ok {
                        Duration::ZERO
                    } else {
                        (retry * 2).clamp(DISCOVER_RETRY_MIN, RETRY_MAX)
                    };
                    tokio::select! {
                        changed = instances.changed() => if changed.is_err() { return },
                        () = self.rediscover.notified() => {}
                        () = tokio::time::sleep(if ok { EVERY } else { retry }) => {}
                    }
                },
                changed = instances.changed() => if changed.is_err() { return },
            }
        }
    }

    /// Asks for a discovery now, such as after an Arr came back online.
    pub fn rediscover(&self) {
        self.rediscover.notify_one();
    }

    /// One pass over `arrs`; `true` when every Arr answered and took our webhook.
    async fn discover(&self, arrs: &[Arr]) -> bool {
        let sab_key = self.config.read().await.settings().sab.api_key.0.clone();
        {
            // Removed instances go at once; changed ones start over.
            let mut known = self.lock();
            known.retain(|id, _| arrs.iter().any(|a| a.id == *id));
            for arr in arrs {
                if !known
                    .get(&arr.id)
                    .is_some_and(|k| k.arr == *arr && k.sab_key == sab_key)
                {
                    known.insert(
                        arr.id,
                        Known {
                            arr: arr.clone(),
                            sab_key: sab_key.clone(),
                            checked_at_ms: None,
                            version: None,
                            error: None,
                            clients: Vec::new(),
                            webhook_url: None,
                            webhook_error: None,
                            delivery_at_ms: None,
                            delivery_error: None,
                            queue_issues: Vec::new(),
                        },
                    );
                }
            }
        }
        self.publish();
        let mut ok = true;
        for arr in arrs {
            let found = self.check(arr, &sab_key).await;
            let hook = match &found {
                Ok((_, clients)) => Some(self.register(arr, clients, &sab_key).await),
                Err(_) => None,
            };
            ok &= found.is_ok() && !matches!(hook, Some(Err(_)));
            self.record(arr, found);
            if let (Some(hook), Some(k)) = (hook, self.lock().get_mut(&arr.id)) {
                (k.webhook_url, k.webhook_error) = match hook {
                    Ok(url) => (url, None),
                    Err(error) => (None, Some(error)),
                };
            }
            self.publish();
        }
        self.discovered.send_replace(true);
        ok
    }

    /// Hands the queue what each owned category must contain.
    fn publish(&self) {
        let known: Vec<Known> = self.lock().values().cloned().collect();
        let owners = owners(&known)
            .into_iter()
            .map(|(category, (media, _))| (category, media.into_iter().collect()))
            .collect();
        self.queue.update_rules(|r| r.owners = owners);
    }

    /// Stores a discovery result; a transient failure keeps what was found before.
    fn record(&self, arr: &Arr, found: Result<(String, Vec<(SabClient, ArrBinding)>), arr::Error>) {
        let (update, error) = match found {
            Ok((version, clients)) => (Some((Some(version), clients)), None),
            Err(error) => {
                tracing::warn!(arr = %arr.name, reason = %error, "Arr discovery failed");
                (
                    (!error.is_transient()).then(|| (None, Vec::new())),
                    Some(error.to_string()),
                )
            }
        };
        if let Some(k) = self.lock().get_mut(&arr.id) {
            k.checked_at_ms = Some(now_ms());
            k.error = error;
            if let Some((version, clients)) = update {
                k.version = version;
                k.clients = clients;
            }
        }
    }

    async fn check(
        &self,
        arr: &Arr,
        sab_key: &str,
    ) -> Result<(String, Vec<(SabClient, ArrBinding)>), arr::Error> {
        let version = arr::version(&self.http, arr).await?;
        let mut clients = Vec::new();
        for mut client in arr::sab_clients(&self.http, arr).await? {
            if !client.enabled {
                continue;
            }
            let binding = match client.api_key.take() {
                Some(key) if sab::same(key.as_bytes(), sab_key.as_bytes()) => ArrBinding::Verified,
                Some(_) => ArrBinding::Other,
                None if arr.profile == Profile::Sportarr => ArrBinding::Unverified,
                None => self.probe(arr, client.id).await,
            };
            clients.push((client, binding));
        }
        Ok((version, clients))
    }

    async fn probe(&self, arr: &Arr, id: i64) -> ArrBinding {
        let pending = self.probes.start();
        let sent = arr::probe(&self.http, arr, id, &pending.marker()).await;
        if pending.arrived() {
            return ArrBinding::Verified;
        }
        if let Err(error) = sent {
            tracing::debug!(arr = %arr.name, reason = %error, "download client probe failed");
        }
        ArrBinding::Unverified
    }

    /// Sends `RefreshMonitoredDownloads` to the Arrs owning categories with finished jobs,
    /// once per Arr per burst. What is pending lives in the database, so it survives
    /// restarts; failures retry with backoff and never hold up imports.
    pub async fn notify(&self) {
        let db = self.queue.db().clone();
        let mut changes = db.subscribe();
        let mut retry = Duration::ZERO;
        loop {
            tokio::time::sleep(COALESCE.max(retry)).await;
            // Finishes so far are covered by the read below.
            while !matches!(
                changes.try_recv(),
                Err(TryRecvError::Empty | TryRecvError::Closed)
            ) {}
            if self.refresh(&db).await {
                retry = Duration::ZERO;
                loop {
                    match changes.recv().await {
                        Ok(Change::History) | Err(RecvError::Lagged(_)) => break,
                        Ok(_) => {}
                        Err(RecvError::Closed) => return,
                    }
                }
            } else {
                retry = (retry * 2).clamp(RETRY_MIN, RETRY_MAX);
            }
        }
    }

    /// One refresh round; `true` when nothing is left pending.
    async fn refresh(&self, db: &infinidysk_db::Db) -> bool {
        let _writing = db.write_permit().await;
        let pending = match db.pending_refreshes().await {
            Ok(pending) => pending,
            Err(error) => {
                tracing::warn!(reason = %error, "reading pending Arr refreshes failed");
                return false;
            }
        };
        if pending.is_empty() {
            return true;
        }
        let known: Vec<Known> = self.lock().values().cloned().collect();
        // Until every instance was checked, a category without owners may still get one.
        let settled = known.iter().all(|k| k.checked_at_ms.is_some());
        let owners = owners(&known);
        let mut sent: HashMap<Uuid, bool> = HashMap::new();
        let mut done = true;
        for (category, generation) in pending {
            let ids = owners.get(&category).map(|(_, ids)| ids.clone());
            if ids.is_none() && !settled {
                done = false;
                continue;
            }
            let mut told = true;
            for id in ids.unwrap_or_default() {
                let Some(k) = known.iter().find(|k| k.arr.id == id) else {
                    continue;
                };
                let ok = match sent.entry(id) {
                    Entry::Occupied(ok) => *ok.get(),
                    Entry::Vacant(slot) => {
                        let result = arr::refresh_downloads(&self.http, &k.arr).await;
                        if let Err(error) = &result {
                            tracing::warn!(arr = %k.arr.name, reason = %error, "Arr refresh failed");
                        }
                        *slot.insert(result.is_ok())
                    }
                };
                told &= ok;
            }
            if !told {
                done = false;
            } else if let Err(error) = db.refreshed(&category, generation).await {
                tracing::warn!(reason = %error, "recording an Arr refresh failed");
                done = false;
            }
        }
        done
    }

    pub fn status(&self) -> ArrStatus {
        let known: Vec<Known> = self.lock().values().cloned().collect();
        let mut instances: Vec<ArrInstanceStatus> = known
            .iter()
            .map(|k| ArrInstanceStatus {
                id: k.arr.id,
                name: k.arr.name.clone(),
                checked_at_ms: k.checked_at_ms,
                version: k.version.clone(),
                error: k.error.clone(),
                clients: k
                    .clients
                    .iter()
                    .map(|(c, binding)| ArrClientStatus {
                        name: c.name.clone(),
                        category: c.category.clone(),
                        binding: *binding,
                    })
                    .collect(),
                webhook_url: k.webhook_url.clone(),
                webhook_error: k.webhook_error.clone(),
                delivery_at_ms: k.delivery_at_ms,
                delivery_error: k.delivery_error.clone(),
                queue_issues: k.queue_issues.clone(),
            })
            .collect();
        instances.sort_by(|a, b| a.name.cmp(&b.name));
        ArrStatus {
            instances,
            categories: owners(&known)
                .into_iter()
                .map(|(category, (media, ids))| CategoryOwners {
                    category,
                    media: media.into_iter().map(MediaKind::from).collect(),
                    instances: ids.into_iter().collect(),
                })
                .collect(),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<Uuid, Known>> {
        self.known.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// Lowercased categories of verified clients, with the media their Arrs import.
fn owners(known: &[Known]) -> BTreeMap<String, (BTreeSet<Kind>, BTreeSet<Uuid>)> {
    let mut owners: BTreeMap<String, (BTreeSet<Kind>, BTreeSet<Uuid>)> = BTreeMap::new();
    for k in known {
        for (client, _) in k.clients.iter().filter(|(_, b)| *b == ArrBinding::Verified) {
            let Some(category) = &client.category else {
                continue;
            };
            let (media, ids) = owners.entry(category.to_lowercase()).or_default();
            media.extend(k.arr.profile.media().iter().copied());
            ids.insert(k.arr.id);
        }
    }
    owners
}

/// The item behind `path` in our tree, where an Arr imported from: the file itself, or
/// for a STRM handed off from it, the video it stands for (`<name>.<ext>.strm`, else
/// the one video in its folder with the same stem).
async fn item_at(db: &Db, path: &str) -> Result<Option<Item>, infinidysk_db::Error> {
    if let Some(item) = db.item_by_path(path).await? {
        return Ok(Some(item));
    }
    let Some(base) = path
        .len()
        .checked_sub(".strm".len())
        .filter(|&at| {
            path.get(at..)
                .is_some_and(|e| e.eq_ignore_ascii_case(".strm"))
        })
        .map(|at| &path[..at])
    else {
        return Ok(None);
    };
    if let Some(item) = db.item_by_path(base).await? {
        return Ok(Some(item));
    }
    let Some((dir, stem)) = base.rsplit_once('/') else {
        return Ok(None);
    };
    let Some(folder) = db.item_by_path(dir).await? else {
        return Ok(None);
    };
    let mut videos = db.children(folder.id).await?.into_iter().filter(|i| {
        media::is_video(&i.name)
            && i.name
                .rsplit_once('.')
                .is_some_and(|(s, _)| s.eq_ignore_ascii_case(stem))
    });
    Ok(match (videos.next(), videos.next()) {
        (Some(item), None) => Some(item),
        _ => None,
    })
}
