//! Library conversion (`infinidysk-lab:docs/strm-support-proposal.md` §4.4-§4.5 and §5):
//! moves the pointers in the library folders to the confirmed form (symlinks or STRM files, the
//! STRM key, the public URL, the mount directory), tells the Arrs, and retires C#'s
//! earlier STRM key form and replaced keys once nothing uses them.

use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::io;
use std::os::unix::fs::MetadataExt as _;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use axum::Json;
use axum::extract::State;
use infinidysk_arr::Arr;
use infinidysk_arr::library::{self as arr_library, Media};
use infinidysk_core::{ItemId, media};
use infinidysk_db::{Conversion, Db, Item, now_ms};
use infinidysk_vfs::Vfs;
use infinidysk_webdav::WebDav;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::io::AsyncReadExt as _;
use tokio::sync::{Notify, RwLock};
use tokio_util::sync::CancellationToken;
use tracing::{info, warn};
use utoipa::ToSchema;
use uuid::Uuid;

use crate::admin::{Admin, ApiError, Fail};
use crate::arr::Discovery;
use crate::config::{self, ConfigStore, ImportStrategy, Settings};
use crate::confined::{Entry, Root};
use crate::handoff::{self, MAX_STRM_BYTES, MAX_SUBTITLE_BYTES};
use crate::upgrade::{LEGACY_STRM_KEY, LegacyStrm};

/// Config key: the [`Confirmed`] form.
const FORM_KEY: &str = "library.form";
/// Config key: a [`Run`] that must finish.
const RUN_KEY: &str = "library.run";
/// Config key: [`PreviousKey`]s still accepted.
const KEYS_KEY: &str = "library.previous_keys";
/// Config key: the [`Sweep`] of C#'s earlier key form.
const SWEEP_KEY: &str = "library.sweep";
/// How long a replaced key or C#'s earlier form keeps working once unused.
const GRACE_MS: i64 = 7 * 24 * 60 * 60 * 1000;
const TICK: Duration = Duration::from_secs(60);
const HOUR_MS: i64 = 60 * 60 * 1000;
/// Problems listed in full; the rest are counted.
const LISTED: usize = 200;
const RESCAN_POLL: Duration = Duration::from_secs(2);
const RESCAN_TIMEOUT: Duration = Duration::from_mins(30);
/// Pause between sweep rewrites, so the sweep never competes with playback.
const SWEEP_PACE: Duration = Duration::from_millis(20);

/// Journal steps (§4.5): recorded, target written, owners moved, then waiting for the
/// Arr rescan. Rows are forgotten once done.
const INTENT: i16 = 1;
const WRITTEN: i16 = 2;
const MOVED: i16 = 3;
const CONVERTED: i16 = 4;

/// What library pointers look like in one form. The STRM key is kept as an id only.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
struct Form {
    strategy: ImportStrategy,
    public_url: String,
    key_id: String,
    mount_dir: String,
}

impl Form {
    fn of(s: &Settings) -> Self {
        Self {
            strategy: s.imports.strategy,
            public_url: s.network.public_url.clone(),
            key_id: infinidysk_webdav::strm_key(&s.imports.strm_key.0, "library-form")[..16]
                .to_owned(),
            mount_dir: s.mount.dir.trim_end_matches('/').to_owned(),
        }
    }

    /// Whether a library in the `confirmed` form already suits `self`; symlinks ignore
    /// the STRM address and key.
    fn fits(&self, confirmed: &Self) -> bool {
        if self.strategy == ImportStrategy::Symlinks
            && confirmed.strategy == ImportStrategy::Symlinks
        {
            self.mount_dir == confirmed.mount_dir
        } else {
            self == confirmed
        }
    }
}

/// The form confirmed through a preview, plus every mount directory library symlinks
/// pointed into before.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct Confirmed {
    form: Option<Form>,
    mount_dirs: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct Run {
    cancelled: bool,
    /// Library folders the preview found unusable and the admin left out.
    excluded: Vec<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct PreviousKey {
    key: String,
    added_ms: i64,
    uses: u64,
    last_used_ms: Option<i64>,
    /// When the library was rewritten away from it; its grace period starts then.
    rewritten_ms: Option<i64>,
}

impl PreviousKey {
    fn retires_ms(&self) -> Option<i64> {
        let quiet = self.rewritten_ms?.max(self.last_used_ms.unwrap_or(0));
        Some(quiet + GRACE_MS)
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct Sweep {
    /// Last path rewritten per root in the current pass.
    positions: BTreeMap<String, String>,
    /// Earlier-form STRMs found in the current pass.
    found: u64,
    /// Total rewritten.
    rewritten: u64,
    /// When a pass first found none.
    clean_ms: Option<i64>,
    /// No pass before this.
    next_ms: i64,
}

/// A pointer to one of our items.
#[derive(Clone, Debug, PartialEq, Eq)]
enum Pointer {
    /// A symlink and its link text.
    Link(String),
    /// A STRM file and its exact content.
    Strm(String),
}

impl Pointer {
    const fn is_link(&self) -> bool {
        matches!(self, Self::Link(_))
    }

    fn text(&self) -> &str {
        match self {
            Self::Link(t) | Self::Strm(t) => t,
        }
    }

    const fn new(link: bool, text: String) -> Self {
        if link {
            Self::Link(text)
        } else {
            Self::Strm(text)
        }
    }
}

/// What counts as one of our pointers.
#[derive(Clone, Debug, Default)]
struct Known {
    mount_dirs: Vec<String>,
    keys: Vec<String>,
    /// C#'s earlier key form is still accepted.
    legacy: bool,
    /// Folders a walk does not enter: the mount and the completed STRM directory, which
    /// may sit inside a library folder.
    skip: Vec<PathBuf>,
}

#[derive(Clone, Debug)]
pub(crate) struct Found {
    pub(crate) path: PathBuf,
    pub(crate) item: ItemId,
    pointer: Pointer,
    /// A STRM signed in C#'s earlier key form.
    legacy: bool,
}

/// The form pointers are converted to.
#[derive(Clone, Debug)]
struct Target {
    strategy: ImportStrategy,
    public_url: String,
    key: String,
    mount_dir: String,
}

impl Target {
    fn of(s: &Settings) -> Self {
        Self {
            strategy: s.imports.strategy,
            public_url: s.network.public_url.clone(),
            key: s.imports.strm_key.0.clone(),
            mount_dir: s.mount.dir.trim_end_matches('/').to_owned(),
        }
    }

    fn link(&self, item: ItemId) -> Pointer {
        Pointer::Link(format!("{}/{}", self.mount_dir, item.ids_path()))
    }

    fn strm(&self, item: ItemId, name: &str) -> Pointer {
        let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
        Pointer::Strm(handoff::strm_url(&self.public_url, &self.key, item, ext))
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
enum Action {
    Keep,
    /// Same path, new content.
    Rewrite(Pointer),
    /// A subtitle pointer becomes the subtitle's bytes.
    Copy,
    /// A new name in the other form.
    Move(PathBuf, Pointer),
    Skip(&'static str),
    /// Its item no longer exists.
    Dangling,
}

/// An Arr file a converted pointer was, as journalled.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
struct Owned {
    instance: Uuid,
    /// Arr-side paths.
    from: String,
    to: String,
    file_id: Option<i64>,
    /// Absent where the Arr takes no rescans.
    media: Option<Media>,
    /// What its rescan resets.
    details: Value,
    /// Episodes or the movie to monitor again.
    monitor: Vec<i64>,
    settled: bool,
}

/// Why a job stopped early.
enum Stop {
    Cancelled,
    Wait(String),
}

/// What the job loop does next.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Next {
    /// Nothing to convert: sweep, then sleep.
    Idle,
    /// Look again at once.
    Again,
    /// Sleep until woken or the next tick.
    Later,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum ConversionState {
    #[default]
    Idle,
    Running,
    Waiting,
    Cancelled,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum PreviewState {
    #[default]
    None,
    Running,
    Ready,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct Problem {
    pub path: String,
    pub reason: String,
}

#[derive(Clone, Debug, Default, Serialize, ToSchema)]
pub struct Progress {
    /// `walking`, `converting`, `rescanning`, `sweeping`.
    pub phase: String,
    pub folder: Option<String>,
    /// Pointers to look at and looked at.
    pub total: u64,
    pub done: u64,
    pub converted: u64,
    pub rewritten: u64,
    pub copied: u64,
    pub rescans_total: u64,
    pub rescans_done: u64,
    /// Pointers to items that no longer exist; never deleted.
    pub dangling: u64,
    pub problems: Vec<Problem>,
    pub problems_total: u64,
}

impl Progress {
    fn problem(&mut self, path: &Path, reason: impl Into<String>) {
        let (path, reason) = (path.display().to_string(), reason.into());
        if self
            .problems
            .iter()
            .any(|p| p.path == path && p.reason == reason)
        {
            return;
        }
        self.problems_total += 1;
        if self.problems.len() < LISTED {
            self.problems.push(Problem { path, reason });
        }
    }
}

/// A change to preview: what is not given stays as saved.
#[derive(Clone, Debug, Default, Deserialize, ToSchema)]
#[serde(default)]
pub struct PreviewRequest {
    pub strategy: Option<ImportStrategy>,
    pub public_url: Option<String>,
    pub mount_dir: Option<String>,
    /// Generate a new STRM key.
    pub new_key: bool,
}

impl PreviewRequest {
    fn apply(&self, s: &mut Settings, key: Option<&str>) {
        if let Some(strategy) = self.strategy {
            s.imports.strategy = strategy;
        }
        if let Some(url) = &self.public_url {
            url.clone_into(&mut s.network.public_url);
        }
        if let Some(dir) = &self.mount_dir {
            dir.clone_into(&mut s.mount.dir);
        }
        if let Some(key) = key {
            key.clone_into(&mut s.imports.strm_key.0);
        }
    }
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct FolderCheck {
    pub path: String,
    pub arr_path: String,
    /// Usable; otherwise left out with `reason`.
    pub ok: bool,
    pub reason: Option<String>,
    pub convert: u64,
    pub rewrite: u64,
    /// Arr root folders inside it, as `<Arr>: <root>`.
    pub arr_roots: Vec<String>,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct ArrCount {
    pub name: String,
    pub files: u64,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct Preview {
    pub strategy: ImportStrategy,
    pub public_url: String,
    pub mount_dir: String,
    pub new_key: bool,
    pub folders: Vec<FolderCheck>,
    /// STRMs in the completed STRM directory rewritten in place.
    pub strm_dir_rewrite: u64,
    pub convert: u64,
    pub rewrite: u64,
    /// Subtitle pointers that become real files.
    pub copy: u64,
    pub dangling: u64,
    pub arrs: Vec<ArrCount>,
    pub skipped: Vec<Problem>,
    pub skipped_total: u64,
    pub warnings: Vec<String>,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct KeyView {
    pub added_ms: i64,
    pub uses: u64,
    pub last_used_ms: Option<i64>,
    /// Absent until the library was rewritten away from it.
    pub retires_ms: Option<i64>,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct LegacyView {
    pub uses: u64,
    pub last_used_ms: Option<i64>,
    pub rewritten: u64,
    /// When a full sweep first found none left.
    pub clean_ms: Option<i64>,
    pub retires_ms: Option<i64>,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct LibraryView {
    pub state: ConversionState,
    /// Why it waits.
    pub reason: Option<String>,
    /// The strategy the running or cancelled conversion converts to.
    pub target: Option<ImportStrategy>,
    pub progress: Progress,
    pub preview_state: PreviewState,
    pub preview: Option<Preview>,
    /// The saved settings are the confirmed form; otherwise a preview applies them.
    pub form_current: bool,
    /// Arr files waiting for a rescan.
    pub pending_remaps: u64,
    pub previous_keys: Vec<KeyView>,
    /// On installs imported from C#, while its earlier STRM key form is accepted.
    pub legacy: Option<LegacyView>,
}

#[derive(Default)]
struct Job {
    state: ConversionState,
    reason: Option<String>,
    target: Option<ImportStrategy>,
    progress: Progress,
    preview_state: PreviewState,
    /// Requested, or finished with its result; the generated key stays here.
    preview: Option<(PreviewRequest, Option<String>, Option<Preview>)>,
    cancel: CancellationToken,
    /// Last attempt at pending Arr rescans.
    remapped_ms: i64,
}

/// What the converter needs from the rest of the app.
pub struct Parts {
    pub db: Db,
    pub vfs: Arc<Vfs>,
    pub handoff: Arc<handoff::Writer>,
    pub dav: Arc<WebDav>,
    pub config: Arc<RwLock<ConfigStore>>,
    pub discovery: Arc<Discovery>,
    /// What FUSE mounted.
    pub mount_dir: String,
    pub busy: Box<dyn Fn() -> bool + Send + Sync>,
}

pub struct Library {
    parts: Parts,
    job: Mutex<Job>,
    wake: Notify,
}

impl Library {
    /// Loads the confirmed form (the saved settings on first start) and the replaced keys
    /// `/view` still accepts. The mounted folder and the stored one (before environment
    /// overrides; a C# install's mount folder after an upgrade) join the former mount
    /// folders, so links into them stay ours when the mount moves.
    pub async fn load(parts: Parts) -> Self {
        let library = Self {
            parts,
            job: Mutex::default(),
            wake: Notify::new(),
        };
        let mut confirmed: Confirmed = library.stored(FORM_KEY).await.unwrap_or_default();
        let settings = library.settings().await;
        let mut mount_dirs = confirmed.mount_dirs.clone();
        let stored_mount = library
            .stored::<crate::config::Mount>("mount")
            .await
            .unwrap_or_default();
        for dir in [
            &settings.mount.dir,
            &library.parts.mount_dir,
            &stored_mount.dir,
        ] {
            mount_dirs.push(dir.trim_end_matches('/').to_owned());
        }
        mount_dirs.retain(|d| !d.is_empty());
        mount_dirs.sort();
        mount_dirs.dedup();
        if confirmed.form.is_none() || mount_dirs != confirmed.mount_dirs {
            confirmed.form.get_or_insert_with(|| Form::of(&settings));
            confirmed.mount_dirs = mount_dirs;
            library.store(FORM_KEY, &confirmed).await;
        }
        let keys: Vec<PreviousKey> = library.stored(KEYS_KEY).await.unwrap_or_default();
        library.publish_keys(&keys);
        if let Some(run) = library.stored::<Run>(RUN_KEY).await {
            library.lock().state = if run.cancelled {
                ConversionState::Cancelled
            } else {
                ConversionState::Running
            };
        }
        library
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Job> {
        self.job.lock().unwrap_or_else(PoisonError::into_inner)
    }

    async fn settings(&self) -> Settings {
        self.parts.config.read().await.settings().clone()
    }

    async fn stored<T: serde::de::DeserializeOwned>(&self, key: &str) -> Option<T> {
        stored(&self.parts.db, key).await
    }

    async fn store<T: Serialize>(&self, key: &str, value: &T) {
        let value = serde_json::to_string(value).unwrap_or_default();
        if let Err(error) = self.parts.db.write_config(&[(key, &value)], &[]).await {
            warn!(reason = %error, key, "could not save library state");
        }
    }

    async fn forget(&self, key: &str) {
        if let Err(error) = self.parts.db.write_config(&[], &[key]).await {
            warn!(reason = %error, key, "could not save library state");
        }
    }

    /// Lets `/view` accept `keys` beside the current one.
    fn publish_keys(&self, keys: &[PreviousKey]) {
        let mut access = self
            .parts
            .dav
            .access
            .write()
            .unwrap_or_else(PoisonError::into_inner);
        let counters: HashMap<String, Arc<AtomicU64>> = access.previous_strm.drain(..).collect();
        access.previous_strm = keys
            .iter()
            .map(|k| {
                let uses = counters.get(&k.key).cloned().unwrap_or_default();
                (k.key.clone(), uses)
            })
            .collect();
    }

    fn legacy_accepted(&self) -> bool {
        self.parts
            .dav
            .access
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .legacy_strm
            .is_some()
    }

    async fn known(&self, settings: &Settings) -> Known {
        let keys: Vec<PreviousKey> = self.stored(KEYS_KEY).await.unwrap_or_default();
        let mut mount_dirs = mount_dirs(&self.parts.db, settings).await;
        mount_dirs.push(self.parts.mount_dir.trim_end_matches('/').to_owned());
        mount_dirs.sort();
        mount_dirs.dedup();
        let skip = mount_dirs
            .iter()
            .chain(std::iter::once(&settings.imports.strm_dir))
            .map(PathBuf::from)
            .flat_map(|p| [fs::canonicalize(&p).ok(), Some(p)])
            .flatten()
            .collect();
        Known {
            mount_dirs,
            keys: std::iter::once(settings.imports.strm_key.0.clone())
                .chain(keys.into_iter().map(|k| k.key))
                .filter(|k| !k.is_empty())
                .collect(),
            legacy: self.legacy_accepted(),
            skip,
        }
    }

    /// Our pointers in the library folders, in path order; any unreadable folder fails.
    pub(crate) async fn pointers(&self, settings: &Settings) -> Result<Vec<Found>, String> {
        let known = self.known(settings).await;
        let mut all = Vec::new();
        for dir in &settings.imports.library_dirs {
            let found = walk_root(&dir.path, &known)
                .await
                .map_err(|e| format!("library folder {}: {e}", dir.path))?;
            all.extend(found);
        }
        Ok(all)
    }

    /// Runs previews, conversions, rescans and the C# key sweep, one at a time.
    pub async fn run(self: Arc<Self>) {
        loop {
            let writing = self.parts.db.write_permit().await;
            self.tick().await;
            drop(writing);
            let cancel = {
                let mut job = self.lock();
                job.cancel = CancellationToken::new();
                job.cancel.clone()
            };
            let preview = {
                let job = self.lock();
                job.preview
                    .as_ref()
                    .filter(|(_, _, done)| done.is_none())
                    .map(|(req, key, _)| (req.clone(), key.clone()))
            };
            let next = if let Some((req, key)) = preview {
                self.preview(&req, key.as_deref(), &cancel).await;
                Next::Again
            } else {
                // A whole conversion run holds it, so compaction waits for or skips one.
                let _writing = self.parts.db.write_permit().await;
                self.convert(&cancel).await
            };
            if next == Next::Idle {
                self.sweep(&cancel).await;
            }
            if next != Next::Again || cancel.is_cancelled() {
                tokio::select! {
                    () = self.wake.notified() => {}
                    () = tokio::time::sleep(TICK) => {}
                }
            }
        }
    }

    fn interrupt(&self) {
        self.lock().cancel.cancel();
        self.wake.notify_one();
    }

    /// Why library pointers may be between two places now: a conversion runs or left
    /// moves unfinished.
    pub(crate) async fn moving(&self) -> Result<Option<&'static str>, infinidysk_db::Error> {
        if self.lock().state == ConversionState::Running {
            return Ok(Some("a library conversion is running"));
        }
        let journal = self.parts.db.conversions().await?;
        // A row outside every configured folder waits for its folder to come back;
        // the folders maintenance walks are not part of it.
        let folders = folders(&self.settings().await);
        Ok(journal
            .iter()
            .any(|c| {
                c.state < CONVERTED && folders.iter().any(|f| Path::new(&c.source).starts_with(f))
            })
            .then_some("a library conversion left moves unfinished"))
    }

    /// Saves replaced-key use and retires keys and C#'s earlier form when due.
    async fn tick(&self) {
        let now = now_ms();
        let mut keys: Vec<PreviousKey> = self.stored(KEYS_KEY).await.unwrap_or_default();
        let mut changed = false;
        {
            let access = self
                .parts
                .dav
                .access
                .read()
                .unwrap_or_else(PoisonError::into_inner);
            for (key, uses) in &access.previous_strm {
                let n = uses.swap(0, Ordering::Relaxed);
                if let Some(k) = keys.iter_mut().find(|k| k.key == *key)
                    && n > 0
                {
                    k.uses += n;
                    k.last_used_ms = Some(now);
                    changed = true;
                }
            }
        }
        let before = keys.len();
        keys.retain(|k| k.retires_ms().is_none_or(|at| at > now));
        if keys.len() != before {
            info!(
                keys = before - keys.len(),
                "retired replaced STRM keys nothing used for 7 days"
            );
            changed = true;
        }
        if changed {
            self.store(KEYS_KEY, &keys).await;
            self.publish_keys(&keys);
        }
        if let Some(view) = self.legacy_view().await
            && view.retires_ms.is_some_and(|at| at <= now)
        {
            info!(
                "retired C#'s earlier STRM key form: no library STRM and no player used it for 7 days"
            );
            self.retire_legacy().await;
        }
    }

    async fn retire_legacy(&self) {
        self.parts
            .dav
            .access
            .write()
            .unwrap_or_else(PoisonError::into_inner)
            .legacy_strm = None;
        if let Err(error) = self
            .parts
            .db
            .write_config(&[], &[LEGACY_STRM_KEY, SWEEP_KEY])
            .await
        {
            warn!(reason = %error, "could not save the retired C# STRM key form");
        }
    }

    async fn legacy_view(&self) -> Option<LegacyView> {
        if !self.legacy_accepted() {
            return None;
        }
        let state: LegacyStrm = self.stored(LEGACY_STRM_KEY).await.unwrap_or_default();
        let sweep: Sweep = self.stored(SWEEP_KEY).await.unwrap_or_default();
        Some(LegacyView {
            uses: state.uses,
            last_used_ms: state.last_used_ms,
            rewritten: sweep.rewritten,
            clean_ms: sweep.clean_ms,
            retires_ms: sweep
                .clean_ms
                .map(|clean| clean.max(state.last_used_ms.unwrap_or(0)) + GRACE_MS),
        })
    }

    /// The state the UI shows.
    async fn view(&self) -> Result<LibraryView, Fail> {
        let settings = self.settings().await;
        let confirmed: Confirmed = self.stored(FORM_KEY).await.unwrap_or_default();
        let keys: Vec<PreviousKey> = self.stored(KEYS_KEY).await.unwrap_or_default();
        let pending_remaps = self.parts.db.remap_pending(None).await?;
        let legacy = self.legacy_view().await;
        let job = self.lock();
        Ok(LibraryView {
            state: job.state,
            reason: job.reason.clone(),
            target: job.target,
            progress: job.progress.clone(),
            preview_state: job.preview_state,
            preview: job.preview.as_ref().and_then(|(_, _, p)| p.clone()),
            form_current: confirmed
                .form
                .as_ref()
                .is_some_and(|f| Form::of(&settings).fits(f)),
            pending_remaps,
            previous_keys: keys
                .iter()
                .map(|k| KeyView {
                    added_ms: k.added_ms,
                    uses: k.uses,
                    last_used_ms: k.last_used_ms,
                    retires_ms: k.retires_ms(),
                })
                .collect(),
            legacy,
        })
    }

    /// Starts a preview of `req`, replacing any earlier one.
    fn request_preview(&self, req: PreviewRequest) {
        let key = req.new_key.then(|| Uuid::new_v4().simple().to_string());
        {
            let mut job = self.lock();
            job.preview = Some((req, key, None));
            job.preview_state = PreviewState::Running;
        }
        self.interrupt();
    }

    fn drop_preview(&self) {
        let mut job = self.lock();
        let running = job.preview_state == PreviewState::Running;
        job.preview = None;
        job.preview_state = PreviewState::None;
        if running {
            job.cancel.cancel();
        }
    }

    /// The finished preview, taken to confirm.
    fn take_preview(&self) -> Option<(PreviewRequest, Option<String>, Preview)> {
        let mut job = self.lock();
        if !matches!(job.preview, Some((_, _, Some(_)))) {
            return None;
        }
        job.preview_state = PreviewState::None;
        let (req, key, preview) = job.preview.take()?;
        drop(job);
        Some((req, key, preview?))
    }

    /// Records `after` as the confirmed form and starts converting the library to it.
    async fn confirm(&self, before: &Settings, after: &Settings, excluded: Vec<String>) {
        let now = now_ms();
        let (old, new) = (&before.imports.strm_key.0, &after.imports.strm_key.0);
        if old != new && !old.is_empty() {
            let mut keys: Vec<PreviousKey> = self.stored(KEYS_KEY).await.unwrap_or_default();
            keys.retain(|k| k.key != *new);
            keys.push(PreviousKey {
                key: old.clone(),
                added_ms: now,
                uses: 0,
                last_used_ms: None,
                rewritten_ms: None,
            });
            self.store(KEYS_KEY, &keys).await;
            self.publish_keys(&keys);
        }
        let mut confirmed: Confirmed = self.stored(FORM_KEY).await.unwrap_or_default();
        let old_mount = before.mount.dir.trim_end_matches('/').to_owned();
        if !confirmed.mount_dirs.contains(&old_mount) {
            confirmed.mount_dirs.push(old_mount);
        }
        confirmed.form = Some(Form::of(after));
        self.store(FORM_KEY, &confirmed).await;
        self.start(Run {
            cancelled: false,
            excluded,
        })
        .await;
    }

    async fn start(&self, run: Run) {
        self.store(RUN_KEY, &run).await;
        {
            let mut job = self.lock();
            job.state = ConversionState::Running;
            job.reason = None;
            job.progress = Progress::default();
        }
        self.interrupt();
    }

    async fn cancel(&self) -> bool {
        let Some(mut run) = self.stored::<Run>(RUN_KEY).await else {
            return false;
        };
        run.cancelled = true;
        self.store(RUN_KEY, &run).await;
        {
            let mut job = self.lock();
            job.state = ConversionState::Cancelled;
            job.reason = None;
        }
        self.interrupt();
        true
    }

    /// Converts the library when a run is due, finishes interrupted steps, converts
    /// what Arrs imported in the old form and retries pending rescans.
    async fn convert(&self, cancel: &CancellationToken) -> Next {
        let run: Option<Run> = self.stored(RUN_KEY).await;
        let run = run.filter(|r| !r.cancelled);
        let imported: Vec<String> = std::mem::take(
            &mut *self
                .parts
                .discovery
                .imported
                .lock()
                .unwrap_or_else(PoisonError::into_inner),
        );
        let journal = match self.parts.db.conversions().await {
            Ok(rows) => rows,
            Err(error) => {
                warn!(reason = %error, "could not read the library conversion journal");
                return Next::Later;
            }
        };
        let unfinished = journal.iter().any(|c| c.state < CONVERTED);
        let remaps = journal
            .iter()
            .any(|c| c.state == CONVERTED && c.reason.is_none())
            && now_ms() - self.lock().remapped_ms > HOUR_MS;
        if run.is_none() && imported.is_empty() && !unfinished && !remaps {
            return Next::Idle;
        }
        let settings = self.settings().await;
        {
            let mut job = self.lock();
            job.target = Some(settings.imports.strategy);
            if run.is_some() {
                job.state = ConversionState::Running;
                job.reason = None;
            }
        }
        let result = self
            .convert_once(&settings, run.as_ref(), &imported, cancel)
            .await;
        let mut job = self.lock();
        match result {
            Ok(left) => {
                if run.is_some() {
                    job.state = ConversionState::Idle;
                    job.reason = None;
                }
                // Rows that could not finish wait for the next tick instead of spinning.
                if left { Next::Later } else { Next::Again }
            }
            Err(Stop::Cancelled) => Next::Again,
            Err(Stop::Wait(reason)) => {
                if run.is_some() {
                    warn!(%reason, "the library conversion waits");
                    job.state = ConversionState::Waiting;
                    job.reason = Some(reason);
                } else {
                    warn!(%reason, "converting newly imported library files waits");
                }
                drop(job);
                // Left for the next look; the walk would find them anyway.
                self.parts
                    .discovery
                    .imported
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .extend(imported);
                Next::Later
            }
        }
    }

    async fn convert_once(
        &self,
        settings: &Settings,
        run: Option<&Run>,
        imported: &[String],
        cancel: &CancellationToken,
    ) -> Result<bool, Stop> {
        let target = Target::of(settings);
        if target.mount_dir != self.parts.mount_dir.trim_end_matches('/') {
            return Err(Stop::Wait(format!(
                "restart to move the mount to {}",
                target.mount_dir
            )));
        }
        if target.strategy == ImportStrategy::Symlinks
            && !Path::new(&target.mount_dir).join(".ids").is_dir()
        {
            return Err(Stop::Wait(
                "symlinks need the mount, which is not up".to_owned(),
            ));
        }
        let known = self.known(settings).await;
        let mut arr_files = ArrFiles::default();
        if run.is_some() {
            self.progress(|p| *p = Progress::default());
        }
        let left = self.finish_unfinished(settings, cancel).await?;
        if let Some(run) = run {
            let roots: Vec<_> = settings
                .imports
                .library_dirs
                .iter()
                .filter(|d| !run.excluded.contains(&d.path))
                .collect();
            for dir in roots {
                self.progress(|p| {
                    "walking".clone_into(&mut p.phase);
                    p.folder = Some(dir.path.clone());
                });
                let found = walk_root(&dir.path, &known)
                    .await
                    .map_err(|e| Stop::Wait(format!("library folder {}: {e}", dir.path)))?;
                self.progress(|p| {
                    "converting".clone_into(&mut p.phase);
                    p.total += found.len() as u64;
                });
                self.convert_found(settings, &target, found, false, &mut arr_files, cancel)
                    .await?;
            }
            let strm_dir = Path::new(&settings.imports.strm_dir);
            if strm_dir.is_dir() {
                self.progress(|p| p.folder = Some(settings.imports.strm_dir.clone()));
                let found = walk_root(&settings.imports.strm_dir, &known)
                    .await
                    .map_err(|e| {
                        Stop::Wait(format!("STRM directory {}: {e}", settings.imports.strm_dir))
                    })?;
                self.progress(|p| p.total += found.len() as u64);
                self.convert_found(settings, &target, found, true, &mut arr_files, cancel)
                    .await?;
            }
        }
        let stragglers = self.stragglers(settings, imported, &known).await;
        if !stragglers.is_empty() {
            info!(
                files = stragglers.len(),
                "converting files an Arr imported in the earlier form"
            );
            self.convert_found(settings, &target, stragglers, false, &mut arr_files, cancel)
                .await?;
        }
        self.remap(settings, run.is_some(), cancel).await?;
        if run.is_some() {
            self.forget(RUN_KEY).await;
            let mut keys: Vec<PreviousKey> = self.stored(KEYS_KEY).await.unwrap_or_default();
            if keys.iter().any(|k| k.rewritten_ms.is_none()) {
                let now = now_ms();
                for k in &mut keys {
                    k.rewritten_ms.get_or_insert(now);
                }
                self.store(KEYS_KEY, &keys).await;
            }
            let p = self.lock().progress.clone();
            info!(
                converted = p.converted,
                rewritten = p.rewritten,
                copied = p.copied,
                problems = p.problems_total,
                dangling = p.dangling,
                "library conversion finished"
            );
        }
        self.progress(|p| {
            p.phase = String::new();
            p.folder = None;
        });
        Ok(left)
    }

    fn progress(&self, f: impl FnOnce(&mut Progress)) {
        f(&mut self.lock().progress);
    }

    /// Library pointers at the local paths of `imported` (Arr-side) not in the form.
    async fn stragglers(
        &self,
        settings: &Settings,
        imported: &[String],
        known: &Known,
    ) -> Vec<Found> {
        let paths: Vec<PathBuf> = imported
            .iter()
            .filter_map(|p| local(&settings.imports.library_dirs, p))
            .collect();
        if paths.is_empty() {
            return Vec::new();
        }
        let known = known.clone();
        tokio::task::spawn_blocking(move || {
            paths
                .iter()
                .filter_map(|p| {
                    let meta = fs::symlink_metadata(p).ok()?;
                    classify(p, &meta, &known)
                })
                .collect()
        })
        .await
        .unwrap_or_default()
    }

    /// Applies the plan of each of `found`; `in_place` only rewrites (the completed STRM
    /// directory, whose STRMs Arrs import later).
    async fn convert_found(
        &self,
        settings: &Settings,
        target: &Target,
        found: Vec<Found>,
        in_place: bool,
        arr_files: &mut ArrFiles,
        cancel: &CancellationToken,
    ) -> Result<(), Stop> {
        let mut stems = Stems::default();
        for f in found {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            let item = self
                .parts
                .db
                .item(f.item)
                .await
                .map_err(|e| Stop::Wait(e.to_string()))?;
            let action = match &item {
                Some(item) if in_place => match &f.pointer {
                    Pointer::Strm(text) => {
                        let want = target.strm(item.id, &item.name);
                        if text.trim() == want.text() {
                            Action::Keep
                        } else {
                            Action::Rewrite(want)
                        }
                    }
                    Pointer::Link(_) => Action::Keep,
                },
                Some(item) => {
                    let action = plan(&f, item, target, &mut stems);
                    self.hold(&settings.arr_instances, target, &f, action)
                        .await
                        .map_err(Stop::Wait)?
                }
                None => Action::Dangling,
            };
            let outcome = match (action, item) {
                (Action::Dangling, _) => {
                    self.progress(|p| p.dangling += 1);
                    Ok(())
                }
                (Action::Rewrite(to), _) => self.rewrite(settings, &f, to).await,
                (Action::Copy, Some(item)) => self.copy(settings, &f, &item).await,
                (Action::Move(path, to), _) => {
                    self.move_pointer(settings, &f, path, to, arr_files).await
                }
                (Action::Keep | Action::Skip(_) | Action::Copy, _) => Ok(()),
            };
            match outcome {
                Ok(()) => {}
                Err(Fault::Wait(reason)) => return Err(Stop::Wait(reason)),
                Err(Fault::File(reason)) => {
                    warn!(path = %f.path.display(), %reason, "could not convert a library file");
                    self.progress(|p| p.problem(&f.path, reason));
                }
            }
            self.progress(|p| p.done += 1);
        }
        Ok(())
    }

    /// Keeps a symlink Lidarr or Chaptarr owns, enabled or not: they import symlinks only.
    async fn hold(
        &self,
        arrs: &[config::ArrInstance],
        target: &Target,
        f: &Found,
        action: Action,
    ) -> Result<Action, String> {
        if !matches!(action, Action::Move(_, Pointer::Strm(_))) {
            return Ok(action);
        }
        let owners = self
            .parts
            .db
            .item_owners(f.item)
            .await
            .map_err(|e| e.to_string())?;
        let stays = owners.iter().any(|(instance, _)| {
            arrs.iter().any(|a| {
                a.id == *instance
                    && matches!(
                        a.profile,
                        config::ArrProfile::Lidarr | config::ArrProfile::Chaptarr
                    )
            })
        });
        Ok(if stays {
            kept_link(f, target, "its Arr keeps symlinks")
        } else {
            action
        })
    }

    async fn rewrite(&self, settings: &Settings, f: &Found, to: Pointer) -> Result<(), Fault> {
        let (path, from, owner, folders) = (
            f.path.clone(),
            f.pointer.clone(),
            self.parts.handoff.owner(),
            folders(settings),
        );
        let rewritten = blocking(move || {
            let root = Root::below(&folders, &path)?;
            if holds(&root, &path, &from)? != Some(true) {
                return Ok(false);
            }
            put(&root, &path, &to, owner)?;
            Ok(true)
        })
        .await?;
        if rewritten {
            self.progress(|p| p.rewritten += 1);
        }
        Ok(())
    }

    async fn copy(&self, settings: &Settings, f: &Found, item: &Item) -> Result<(), Fault> {
        let size = item.size.unwrap_or_default();
        if size > MAX_SUBTITLE_BYTES {
            return Err(Fault::File(format!(
                "subtitle larger than {MAX_SUBTITLE_BYTES} bytes stays a symlink"
            )));
        }
        let mut reader = self
            .parts
            .vfs
            .open_analysis(item)
            .await
            .map_err(|e| Fault::File(format!("reading the subtitle: {e}")))?;
        reader.set_end(size);
        let mut bytes = Vec::with_capacity(usize::try_from(size).unwrap_or_default());
        reader
            .read_to_end(&mut bytes)
            .await
            .map_err(|e| Fault::File(format!("reading the subtitle: {e}")))?;
        let (path, from, owner, folders) = (
            f.path.clone(),
            f.pointer.clone(),
            self.parts.handoff.owner(),
            folders(settings),
        );
        let copied = blocking(move || {
            let root = Root::below(&folders, &path)?;
            if holds(&root, &path, &from)? != Some(true) {
                return Ok(false);
            }
            root.write(&path, &bytes, owner)?;
            Ok(true)
        })
        .await?;
        if copied {
            self.progress(|p| p.copied += 1);
        }
        Ok(())
    }

    /// Journals a pointer's move to `path` with the Arr files it is, then runs the steps.
    async fn move_pointer(
        &self,
        settings: &Settings,
        f: &Found,
        path: PathBuf,
        to: Pointer,
        arr_files: &mut ArrFiles,
    ) -> Result<(), Fault> {
        let source = f.path.to_string_lossy().into_owned();
        let target = path.to_string_lossy().into_owned();
        let db = &self.parts.db;
        let mut owned = Vec::new();
        let arrs = crate::app::arr_instances(&settings.arr_instances);
        for (instance, owner) in db
            .item_owners(f.item)
            .await
            .map_err(|e| Fault::Wait(e.to_string()))?
        {
            if local(&settings.imports.library_dirs, &owner.path).as_deref()
                != Some(f.path.as_path())
            {
                continue;
            }
            let name = path
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default();
            let to_arr = match owner.path.rsplit_once('/') {
                Some((dir, _)) => format!("{dir}/{name}"),
                None => name,
            };
            let mut o = Owned {
                instance,
                from: owner.path.clone(),
                to: to_arr,
                file_id: owner.file_id,
                media: None,
                details: Value::Null,
                monitor: Vec::new(),
                settled: false,
            };
            if let Some(arr) = arrs
                .iter()
                .find(|a| a.id == instance && arr_library::converts(a))
            {
                let http = self.parts.discovery.http();
                if let Some(file) = arr_files.file(http, arr, &owner).await?.cloned() {
                    o.media = Some(file.media);
                    o.details = file.details.clone();
                    o.monitor = arr_files
                        .monitored(http, arr, file.media)
                        .await?
                        .iter()
                        .filter(|(_, file_id)| *file_id == file.id)
                        .map(|(id, _)| *id)
                        .collect();
                }
            }
            owned.push(o);
        }
        let c = Conversion {
            source,
            target,
            item: f.item,
            state: INTENT,
            from_link: f.pointer.is_link(),
            to_link: to.is_link(),
            original: f.pointer.text().to_owned(),
            written: to.text().to_owned(),
            owners: serde_json::to_string(&owned).unwrap_or_default(),
            reason: None,
        };
        db.put_conversion(&c)
            .await
            .map_err(|e| Fault::Wait(e.to_string()))?;
        if self.steps(c, owned, false, &folders(settings)).await? {
            self.progress(|p| p.converted += 1);
        }
        Ok(())
    }

    /// Finishes journal rows a restart or failure interrupted. Returns whether some are
    /// left for a later look.
    async fn finish_unfinished(
        &self,
        settings: &Settings,
        cancel: &CancellationToken,
    ) -> Result<bool, Stop> {
        let folders = folders(settings);
        let rows = self
            .parts
            .db
            .conversions()
            .await
            .map_err(|e| Stop::Wait(e.to_string()))?;
        let mut left = false;
        for c in rows.into_iter().filter(|c| c.state < CONVERTED) {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            let owned: Vec<Owned> = serde_json::from_str(&c.owners).unwrap_or_default();
            let source = PathBuf::from(&c.source);
            match self.steps(c, owned, true, &folders).await {
                Ok(_) => {}
                Err(Fault::Wait(reason)) => return Err(Stop::Wait(reason)),
                Err(Fault::File(reason)) => {
                    left = true;
                    self.progress(|p| p.problem(&source, reason));
                }
            }
        }
        Ok(left)
    }

    /// Runs the journal steps of `c` from its state. Returns whether it converted; when
    /// the source changed meanwhile, what was done is undone. `resumed`: after a restart,
    /// a missing source was removed by us. Source and target share a folder, which lies
    /// in one of `folders`.
    async fn steps(
        &self,
        mut c: Conversion,
        owned: Vec<Owned>,
        resumed: bool,
        folders: &[PathBuf],
    ) -> Result<bool, Fault> {
        let db = &self.parts.db;
        let (source, target) = (PathBuf::from(&c.source), PathBuf::from(&c.target));
        let from = Pointer::new(c.from_link, c.original.clone());
        let to = Pointer::new(c.to_link, c.written.clone());
        let owner = self.parts.handoff.owner();
        if c.state == INTENT {
            let (s, t, from, to) = (source.clone(), target.clone(), from.clone(), to.clone());
            let (item, folders) = (c.item, folders.to_vec());
            let written = blocking(move || {
                let root = Root::below(&folders, &s)?;
                if holds(&root, &s, &from)? != Some(true) {
                    return Ok(Err(None));
                }
                match fs::symlink_metadata(&t) {
                    Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                    Err(e) => return Err(e),
                    Ok(meta) => {
                        let same = holds(&root, &t, &to)? == Some(true)
                            || classify(&t, &meta, &Known::any()).is_some_and(|f| f.item == item);
                        if !same {
                            return Ok(Err(Some(format!(
                                "{} already exists and is not ours",
                                t.display()
                            ))));
                        }
                        return Ok(Ok(()));
                    }
                }
                put(&root, &t, &to, owner)?;
                Ok(Ok(()))
            })
            .await;
            match written {
                Ok(Ok(())) => {}
                Ok(Err(conflict)) => {
                    db.forget_conversion(&c.source)
                        .await
                        .map_err(|e| Fault::Wait(e.to_string()))?;
                    return conflict.map_or(Ok(false), |reason| Err(Fault::File(reason)));
                }
                Err(error) => {
                    db.forget_conversion(&c.source)
                        .await
                        .map_err(|e| Fault::Wait(e.to_string()))?;
                    return Err(error);
                }
            }
            c.state = WRITTEN;
            db.put_conversion(&c)
                .await
                .map_err(|e| Fault::Wait(e.to_string()))?;
        }
        if c.state == WRITTEN {
            for o in &owned {
                db.move_owner(o.instance, c.item, &o.from, &o.to, o.media.is_some())
                    .await
                    .map_err(|e| Fault::Wait(e.to_string()))?;
            }
            c.state = MOVED;
            db.put_conversion(&c)
                .await
                .map_err(|e| Fault::Wait(e.to_string()))?;
        }
        if c.state == MOVED {
            let (s, owned_folders) = (source.clone(), folders.to_vec());
            let removed = blocking(move || {
                let root = Root::below(&owned_folders, &s)?;
                match holds(&root, &s, &from)? {
                    Some(true) => root.remove(&s, usize::MAX).map(|()| true),
                    None if resumed => Ok(true),
                    _ => Ok(false),
                }
            })
            .await;
            match removed {
                Ok(true) => {}
                Ok(false) => {
                    self.undo(&c, &owned, &to, folders).await?;
                    return Ok(false);
                }
                // Whether the source changed is unknown, so the journal stays for a later run.
                Err(error) => return Err(error),
            }
            if owned.iter().any(|o| o.media.is_some()) {
                c.state = CONVERTED;
                db.put_conversion(&c)
                    .await
                    .map_err(|e| Fault::Wait(e.to_string()))?;
            } else {
                db.forget_conversion(&c.source)
                    .await
                    .map_err(|e| Fault::Wait(e.to_string()))?;
            }
        }
        Ok(true)
    }

    /// Removes the target if it still holds what we wrote and gives the owners their
    /// source path back. When the target cannot be reached, the journal and owners stay
    /// for a later run.
    async fn undo(
        &self,
        c: &Conversion,
        owned: &[Owned],
        to: &Pointer,
        folders: &[PathBuf],
    ) -> Result<(), Fault> {
        let (t, to, folders) = (PathBuf::from(&c.target), to.clone(), folders.to_vec());
        blocking(move || {
            let root = Root::below(&folders, &t)?;
            if holds(&root, &t, &to)? == Some(true) {
                root.remove(&t, usize::MAX)?;
            }
            Ok(())
        })
        .await?;
        let db = &self.parts.db;
        for o in owned {
            db.move_owner(o.instance, c.item, &o.to, &o.from, false)
                .await
                .map_err(|e| Fault::Wait(e.to_string()))?;
        }
        db.forget_conversion(&c.source)
            .await
            .map_err(|e| Fault::Wait(e.to_string()))
    }

    /// Asks each Arr to rescan what was converted, then gives its re-imported files the
    /// recorded details and monitoring back. One rescan in flight per Arr. Files an
    /// earlier rescan did not pick up are retried only with `retry`.
    async fn remap(
        &self,
        settings: &Settings,
        retry: bool,
        cancel: &CancellationToken,
    ) -> Result<(), Stop> {
        self.lock().remapped_ms = now_ms();
        let db = &self.parts.db;
        let mut rows: Vec<(Conversion, Vec<Owned>)> = db
            .conversions()
            .await
            .map_err(|e| Stop::Wait(e.to_string()))?
            .into_iter()
            .filter(|c| c.state == CONVERTED && (retry || c.reason.is_none()))
            .map(|c| {
                let owned = serde_json::from_str(&c.owners).unwrap_or_default();
                (c, owned)
            })
            .collect();
        let mut groups: BTreeMap<Uuid, BTreeMap<Media, Vec<usize>>> = BTreeMap::new();
        for (i, (_, owned)) in rows.iter().enumerate() {
            for o in owned.iter().filter(|o| !o.settled) {
                if let Some(media) = o.media {
                    let list = groups
                        .entry(o.instance)
                        .or_default()
                        .entry(media)
                        .or_default();
                    if list.last() != Some(&i) {
                        list.push(i);
                    }
                }
            }
        }
        let total: usize = groups.values().map(BTreeMap::len).sum();
        if total == 0 {
            return Ok(());
        }
        self.progress(|p| {
            "rescanning".clone_into(&mut p.phase);
            p.folder = None;
            p.rescans_total = total as u64;
            p.rescans_done = 0;
        });
        let arrs = crate::app::arr_instances(&settings.arr_instances);
        let mut wait = None;
        for (instance, medias) in groups {
            let Some(arr) = arrs.iter().find(|a| a.id == instance) else {
                // The Arr is gone; nothing will remap its files.
                for i in medias.into_values().flatten() {
                    let (c, owned) = &mut rows[i];
                    for o in owned.iter_mut().filter(|o| o.instance == instance) {
                        db.move_owner(instance, c.item, &o.to, &o.to, false)
                            .await
                            .map_err(|e| Stop::Wait(e.to_string()))?;
                        o.settled = true;
                    }
                }
                continue;
            };
            for (media, list) in medias {
                if cancel.is_cancelled() {
                    wait = Some(Stop::Cancelled);
                    break;
                }
                match self.remap_media(arr, media, &list, &mut rows, cancel).await {
                    Ok(()) => self.progress(|p| p.rescans_done += 1),
                    Err(stop) => {
                        wait = Some(stop);
                        break;
                    }
                }
            }
        }
        for (c, owned) in &rows {
            let result = if owned.iter().all(|o| o.settled || o.media.is_none()) {
                db.forget_conversion(&c.source).await
            } else {
                let mut c = c.clone();
                c.owners = serde_json::to_string(owned).unwrap_or_default();
                db.put_conversion(&c).await
            };
            result.map_err(|e| Stop::Wait(e.to_string()))?;
        }
        wait.map_or(Ok(()), Err)
    }

    async fn remap_media(
        &self,
        arr: &Arr,
        media: Media,
        list: &[usize],
        rows: &mut [(Conversion, Vec<Owned>)],
        cancel: &CancellationToken,
    ) -> Result<(), Stop> {
        let http = self.parts.discovery.http();
        let unreachable = |e: infinidysk_arr::Error| Stop::Wait(format!("{}: {e}", arr.name));
        let command = arr_library::rescan(http, arr, media)
            .await
            .map_err(unreachable)?;
        let started = tokio::time::Instant::now();
        loop {
            tokio::select! {
                () = cancel.cancelled() => return Err(Stop::Cancelled),
                () = tokio::time::sleep(RESCAN_POLL) => {}
            }
            match arr_library::command_done(http, arr, command)
                .await
                .map_err(unreachable)?
            {
                None if started.elapsed() < RESCAN_TIMEOUT => {}
                None => return Err(Stop::Wait(format!("{}: a rescan did not finish", arr.name))),
                Some(true) => break,
                Some(false) => return Err(Stop::Wait(format!("{}: a rescan failed", arr.name))),
            }
        }
        let files = arr_library::files_of(http, arr, media)
            .await
            .map_err(unreachable)?;
        let mut monitor = Vec::new();
        for &i in list {
            let (c, owned) = &mut rows[i];
            for o in owned
                .iter_mut()
                .filter(|o| o.instance == arr.id && o.media == Some(media) && !o.settled)
            {
                let Some(file) = files.iter().find(|f| f.path == o.to) else {
                    c.reason = Some(format!(
                        "{} did not pick up {} in its rescan",
                        arr.name, o.to
                    ));
                    continue;
                };
                self.parts
                    .db
                    .settle_owner(arr.id, c.item, file.id)
                    .await
                    .map_err(|e| Stop::Wait(e.to_string()))?;
                if o.details.as_object().is_some_and(|d| !d.is_empty())
                    && let Err(error) =
                        arr_library::restore(http, arr, media, file.id, &o.details).await
                {
                    warn!(arr = %arr.name, reason = %error, "could not restore a converted file's quality details");
                }
                monitor.extend_from_slice(&o.monitor);
                o.settled = true;
            }
        }
        monitor.sort_unstable();
        monitor.dedup();
        if let Err(error) = arr_library::monitor(http, arr, media, &monitor).await {
            warn!(arr = %arr.name, reason = %error, "could not monitor converted files again");
        }
        Ok(())
    }

    /// Walks every library folder of the would-be settings and counts what a conversion
    /// would do; nothing is written but a probe file per folder.
    async fn preview(&self, req: &PreviewRequest, key: Option<&str>, cancel: &CancellationToken) {
        let settings = self.settings().await;
        let mut next = settings.clone();
        req.apply(&mut next, key);
        let result = self.preview_of(&settings, &next, req, cancel).await;
        let saved = req.strategy.is_none()
            && req.public_url.is_none()
            && req.mount_dir.is_none()
            && !req.new_key;
        if saved
            && !cancel.is_cancelled()
            && result.convert + result.rewrite + result.copy + result.strm_dir_rewrite == 0
            && result.skipped_total == 0
            && result.folders.iter().all(|f| f.ok)
        {
            // Nothing to convert: the library already is in the saved form.
            let mut confirmed: Confirmed = self.stored(FORM_KEY).await.unwrap_or_default();
            if let Some(old) = confirmed.form.take()
                && !confirmed.mount_dirs.contains(&old.mount_dir)
            {
                confirmed.mount_dirs.push(old.mount_dir);
            }
            confirmed.form = Some(Form::of(&settings));
            self.store(FORM_KEY, &confirmed).await;
        }
        let mut job = self.lock();
        if cancel.is_cancelled() {
            return;
        }
        if let Some((_, _, done)) = job.preview.as_mut() {
            *done = Some(result);
            job.preview_state = PreviewState::Ready;
        }
    }

    async fn preview_of(
        &self,
        settings: &Settings,
        next: &Settings,
        req: &PreviewRequest,
        cancel: &CancellationToken,
    ) -> Preview {
        let target = Target::of(next);
        let known = self.known(settings).await;
        let arrs = crate::app::arr_instances(&settings.arr_instances);
        let http = self.parts.discovery.http();
        let mut roots: Vec<(String, Vec<String>)> = Vec::new();
        for arr in arrs.iter().filter(|a| arr_library::converts(a)) {
            match arr_library::root_folders(http, arr).await {
                Ok(list) => roots.push((arr.name.clone(), list)),
                Err(error) => roots.push((arr.name.clone(), vec![format!("unreachable: {error}")])),
            }
        }
        let mut p = Preview {
            strategy: next.imports.strategy,
            public_url: next.network.public_url.clone(),
            mount_dir: next.mount.dir.clone(),
            new_key: req.new_key,
            folders: Vec::new(),
            strm_dir_rewrite: 0,
            convert: 0,
            rewrite: 0,
            copy: 0,
            dangling: 0,
            arrs: Vec::new(),
            skipped: Vec::new(),
            skipped_total: 0,
            warnings: warnings(settings, next),
        };
        let mut per_arr: BTreeMap<Uuid, u64> = BTreeMap::new();
        let mut stems = Stems::default();
        for dir in &next.imports.library_dirs {
            if cancel.is_cancelled() {
                break;
            }
            let mut check = FolderCheck {
                path: dir.path.clone(),
                arr_path: dir.arr_path().to_owned(),
                ok: true,
                reason: None,
                convert: 0,
                rewrite: 0,
                arr_roots: roots
                    .iter()
                    .flat_map(|(name, list)| {
                        list.iter()
                            .filter(|r| {
                                Path::new(r.as_str()).starts_with(dir.arr_path())
                                    || r.starts_with("unreachable")
                            })
                            .map(move |r| format!("{name}: {r}"))
                    })
                    .collect(),
            };
            let probe = probe_folder(
                dir.path.clone(),
                next.mount.dir.clone(),
                next.imports.strm_dir.clone(),
                self.parts.handoff.owner(),
            )
            .await;
            let found = match probe {
                Ok(()) => walk_root(&dir.path, &known).await,
                Err(reason) => Err(reason),
            };
            match found {
                Err(reason) => {
                    check.ok = false;
                    check.reason = Some(reason);
                }
                Ok(found) => {
                    for f in &found {
                        let action = match self.parts.db.item(f.item).await {
                            Ok(Some(item)) => {
                                let action = plan(f, &item, &target, &mut stems);
                                self.hold(&next.arr_instances, &target, f, action)
                                    .await
                                    .unwrap_or(Action::Skip("database unavailable"))
                            }
                            Ok(None) => Action::Dangling,
                            Err(_) => Action::Skip("database unavailable"),
                        };
                        match action {
                            Action::Keep => {}
                            Action::Rewrite(_) => check.rewrite += 1,
                            Action::Copy => p.copy += 1,
                            Action::Dangling => p.dangling += 1,
                            Action::Skip(reason) => {
                                p.skipped_total += 1;
                                if p.skipped.len() < LISTED {
                                    p.skipped.push(Problem {
                                        path: f.path.display().to_string(),
                                        reason: reason.to_owned(),
                                    });
                                }
                            }
                            Action::Move(path, _) => {
                                check.convert += 1;
                                if let Ok(owners) = self.parts.db.item_owners(f.item).await {
                                    for (instance, o) in owners {
                                        if local(&next.imports.library_dirs, &o.path).as_deref()
                                            == Some(f.path.as_path())
                                        {
                                            *per_arr.entry(instance).or_default() += 1;
                                        }
                                    }
                                }
                                if taken(&path, f.item) {
                                    p.skipped_total += 1;
                                    if p.skipped.len() < LISTED {
                                        p.skipped.push(Problem {
                                            path: f.path.display().to_string(),
                                            reason: format!(
                                                "name conflict: {} exists",
                                                path.display()
                                            ),
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            p.convert += check.convert;
            p.rewrite += check.rewrite;
            p.folders.push(check);
        }
        if Path::new(&next.imports.strm_dir).is_dir()
            && let Ok(found) = walk_root(&next.imports.strm_dir, &known).await
        {
            for f in &found {
                if let (Pointer::Strm(text), Ok(Some(item))) =
                    (&f.pointer, self.parts.db.item(f.item).await)
                    && text.trim() != target.strm(item.id, &item.name).text()
                {
                    p.strm_dir_rewrite += 1;
                }
            }
        }
        p.arrs = per_arr
            .into_iter()
            .map(|(id, files)| ArrCount {
                name: settings
                    .arr_instances
                    .iter()
                    .find(|a| a.id == id)
                    .map_or_else(|| id.to_string(), |a| a.name.clone()),
                files,
            })
            .collect();
        p
    }

    /// Rewrites STRMs in C#'s earlier key form in place, a few at a time while nothing
    /// streams or imports, with a saved position per folder (§5).
    async fn sweep(&self, cancel: &CancellationToken) {
        if !self.legacy_accepted() || (self.parts.busy)() {
            return;
        }
        let mut state: Sweep = self.stored(SWEEP_KEY).await.unwrap_or_default();
        let now = now_ms();
        if now < state.next_ms {
            return;
        }
        let settings = self.settings().await;
        let known = self.known(&settings).await;
        let target = Target::of(&settings);
        let roots: Vec<String> = settings
            .imports
            .library_dirs
            .iter()
            .map(|d| d.path.clone())
            .chain(std::iter::once(settings.imports.strm_dir.clone()))
            .filter(|r| Path::new(r).is_dir())
            .collect();
        for root in &roots {
            let Ok(found) = walk_root(root, &known).await else {
                // An unreadable folder is not a clean one; the next pass tries again.
                state.next_ms = now + HOUR_MS;
                self.store(SWEEP_KEY, &state).await;
                return;
            };
            let after = state.positions.get(root).cloned().unwrap_or_default();
            for f in found
                .into_iter()
                .filter(|f| f.legacy && f.path.to_string_lossy().as_ref() > after.as_str())
            {
                if cancel.is_cancelled() || (self.parts.busy)() {
                    self.store(SWEEP_KEY, &state).await;
                    return;
                }
                state.found += 1;
                let Pointer::Strm(text) = &f.pointer else {
                    continue;
                };
                let rewritten = rekey(text, &target.key);
                let owner = self.parts.handoff.owner();
                let (path, from, folders) = (f.path.clone(), f.pointer.clone(), folders(&settings));
                let done = blocking(move || {
                    let root = Root::below(&folders, &path)?;
                    if holds(&root, &path, &from)? != Some(true) {
                        return Ok(false);
                    }
                    put(&root, &path, &Pointer::Strm(rewritten), owner)?;
                    Ok(true)
                })
                .await;
                match done {
                    Ok(true) => state.rewritten += 1,
                    Ok(false) => {}
                    Err(Fault::File(reason) | Fault::Wait(reason)) => {
                        warn!(path = %f.path.display(), %reason, "could not rewrite a STRM in C#'s earlier key form");
                    }
                }
                state
                    .positions
                    .insert(root.clone(), f.path.to_string_lossy().into_owned());
                tokio::time::sleep(SWEEP_PACE).await;
            }
            state.positions.insert(root.clone(), char::MAX.to_string());
        }
        if state.found == 0 {
            state.clean_ms.get_or_insert(now);
        } else {
            info!(
                files = state.found,
                "rewrote STRMs in C#'s earlier key form"
            );
        }
        state.found = 0;
        state.positions.clear();
        state.next_ms = now + 24 * HOUR_MS;
        self.store(SWEEP_KEY, &state).await;
    }
}

/// Why a file was not converted: `Wait` stops the job until later, `File` skips the file.
enum Fault {
    Wait(String),
    File(String),
}

impl From<io::Error> for Fault {
    fn from(error: io::Error) -> Self {
        Self::File(error.to_string())
    }
}

async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> io::Result<T> + Send + 'static,
) -> Result<T, Fault> {
    tokio::task::spawn_blocking(f)
        .await
        .map_err(|e| Fault::Wait(e.to_string()))?
        .map_err(Fault::from)
}

/// Arr libraries fetched once per run.
#[derive(Default)]
struct ArrFiles {
    files: HashMap<Uuid, Vec<arr_library::MediaFile>>,
    monitored: HashMap<(Uuid, Media), Vec<(i64, i64)>>,
}

impl ArrFiles {
    async fn file(
        &mut self,
        http: &reqwest::Client,
        arr: &Arr,
        owner: &infinidysk_db::ArrOwner,
    ) -> Result<Option<&arr_library::MediaFile>, Fault> {
        let files = match self.files.entry(arr.id) {
            std::collections::hash_map::Entry::Occupied(e) => e.into_mut(),
            std::collections::hash_map::Entry::Vacant(e) => e.insert(
                arr_library::media_files(http, arr)
                    .await
                    .map_err(|e| Fault::Wait(format!("{}: {e}", arr.name)))?,
            ),
        };
        Ok(owner
            .file_id
            .and_then(|id| files.iter().find(|f| f.id == id))
            .or_else(|| files.iter().find(|f| f.path == owner.path)))
    }

    async fn monitored(
        &mut self,
        http: &reqwest::Client,
        arr: &Arr,
        media: Media,
    ) -> Result<&[(i64, i64)], Fault> {
        let key = (arr.id, media);
        Ok(match self.monitored.entry(key) {
            std::collections::hash_map::Entry::Occupied(e) => e.into_mut(),
            std::collections::hash_map::Entry::Vacant(e) => e.insert(
                arr_library::monitored(http, arr, media)
                    .await
                    .map_err(|e| Fault::Wait(format!("{}: {e}", arr.name)))?,
            ),
        })
    }
}

/// Lower-case video stems per folder, to name STRMs as handoff does.
#[derive(Default)]
struct Stems(HashMap<PathBuf, HashMap<String, usize>>);

impl Stems {
    fn shared(&mut self, dir: &Path, stem: &str) -> bool {
        let stems = self.0.entry(dir.to_path_buf()).or_insert_with(|| {
            let mut stems: HashMap<String, usize> = HashMap::new();
            for entry in fs::read_dir(dir).into_iter().flatten().flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if media::is_video(&name) {
                    *stems.entry(stem_of(&name).to_lowercase()).or_default() += 1;
                }
            }
            stems
        });
        stems.get(&stem.to_lowercase()).copied().unwrap_or_default() > 1
    }
}

fn stem_of(name: &str) -> &str {
    name.rsplit_once('.').map_or(name, |(s, _)| s)
}

/// What to do with the pointer `f` to `item` for `target`.
fn plan(f: &Found, item: &Item, target: &Target, stems: &mut Stems) -> Action {
    let video = media::is_video(&item.name);
    let name = f
        .path
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default();
    let dir = f.path.parent().unwrap_or_else(|| Path::new("/"));
    let (want, path) = match (target.strategy, &f.pointer) {
        (ImportStrategy::Symlinks, Pointer::Link(_)) => (target.link(item.id), f.path.clone()),
        (ImportStrategy::Strm, Pointer::Strm(_)) => {
            (target.strm(item.id, &item.name), f.path.clone())
        }
        (ImportStrategy::Symlinks, Pointer::Strm(_)) => {
            let base = name
                .rsplit_once('.')
                .filter(|(_, e)| e.eq_ignore_ascii_case("strm"))
                .map_or(name.as_str(), |(b, _)| b);
            let ext = item.name.rsplit_once('.').map_or("", |(_, e)| e);
            let link = if media::is_video(base) || ext.is_empty() {
                base.to_owned()
            } else {
                format!("{base}.{ext}")
            };
            (target.link(item.id), dir.join(link))
        }
        (ImportStrategy::Strm, Pointer::Link(_)) => {
            if handoff::is_subtitle(&item.name) {
                return if item.size.unwrap_or_default() > MAX_SUBTITLE_BYTES {
                    kept_link(f, target, "subtitle larger than 10 MB stays a symlink")
                } else {
                    Action::Copy
                };
            }
            if !video || !media::is_video(&name) {
                return kept_link(f, target, "not a video; stays a symlink");
            }
            let stem = stem_of(&name);
            let strm = if stems.shared(dir, stem) {
                format!("{name}.strm")
            } else {
                format!("{stem}.strm")
            };
            (target.strm(item.id, &item.name), dir.join(strm))
        }
    };
    if path == f.path {
        let same = match (&want, &f.pointer) {
            (Pointer::Strm(w), Pointer::Strm(t)) => t.trim() == w,
            (w, t) => w == t,
        };
        if same {
            Action::Keep
        } else {
            Action::Rewrite(want)
        }
    } else {
        Action::Move(path, want)
    }
}

/// A symlink that stays one under STRM, pointed at the current mount once it is up.
fn kept_link(f: &Found, target: &Target, reason: &'static str) -> Action {
    let want = target.link(f.item);
    if f.pointer == want {
        Action::Skip(reason)
    } else if !Path::new(&target.mount_dir).join(".ids").is_dir() {
        Action::Skip("the mount is not up; the symlink keeps its target")
    } else {
        Action::Rewrite(want)
    }
}

/// Whether something other than a pointer to `item` is at `path`.
fn taken(path: &Path, item: ItemId) -> bool {
    fs::symlink_metadata(path)
        .is_ok_and(|meta| classify(path, &meta, &Known::any()).is_none_or(|f| f.item != item))
}

/// The local path of the Arr-side `path`, inside a library folder.
pub(crate) fn local(dirs: &[config::LibraryDir], path: &str) -> Option<PathBuf> {
    dirs.iter().find_map(|d| {
        let rest = Path::new(path).strip_prefix(d.arr_path()).ok()?;
        // `..` would leave the library folder.
        if !rest.components().all(|c| matches!(c, Component::Normal(_))) {
            return None;
        }
        Some(Path::new(&d.path).join(rest))
    })
}

impl Known {
    /// Any pointer to an item, whatever its key or mount directory.
    fn any() -> Self {
        Self::default()
    }
}

async fn stored<T: serde::de::DeserializeOwned>(db: &Db, key: &str) -> Option<T> {
    match db.config().await {
        Ok(config) => config
            .into_iter()
            .find(|(k, _)| k == key)
            .and_then(|(_, v)| serde_json::from_str(&v).ok()),
        Err(error) => {
            warn!(reason = %error, key, "could not read library state");
            None
        }
    }
}

/// The mount folders our links may lead into: the current one and every former one.
pub(crate) async fn mount_dirs(db: &Db, settings: &Settings) -> Vec<String> {
    let confirmed: Confirmed = stored(db, FORM_KEY).await.unwrap_or_default();
    let mut dirs = confirmed.mount_dirs;
    dirs.push(settings.mount.dir.trim_end_matches('/').to_owned());
    dirs.sort();
    dirs.dedup();
    dirs
}

/// The item the pointer at `path` names: a link into the `.ids` folder of one of
/// `mount_dirs`, or a STRM whatever its key.
pub(crate) fn pointed(path: &Path, mount_dirs: &[String]) -> Option<ItemId> {
    let meta = fs::symlink_metadata(path).ok()?;
    let known = if meta.file_type().is_symlink() {
        Known {
            mount_dirs: mount_dirs.to_vec(),
            ..Known::default()
        }
    } else {
        Known::any()
    };
    classify(path, &meta, &known).map(|f| f.item)
}

/// One of our pointers at `path`, if it is one. With [`Known::any`], any link into a
/// `.ids` folder and any STRM naming an item count.
fn classify(path: &Path, meta: &fs::Metadata, known: &Known) -> Option<Found> {
    let name = path.file_name()?.to_string_lossy();
    if name.starts_with('.') && name.ends_with(".partial") {
        return None;
    }
    if meta.file_type().is_symlink() {
        let text = fs::read_link(path).ok()?.to_string_lossy().into_owned();
        let resolved = normalize(&path.parent()?.join(&text));
        let item = if known.mount_dirs.is_empty() {
            let s = resolved.to_string_lossy();
            let (_, ids) = s.rsplit_once("/.ids/")?;
            ItemId::from_ids_path(&format!(".ids/{ids}"))?
        } else {
            known.mount_dirs.iter().find_map(|m| {
                let rest = resolved.strip_prefix(m).ok()?;
                ItemId::from_ids_path(rest.to_str()?)
            })?
        };
        return Some(Found {
            path: path.to_path_buf(),
            item,
            pointer: Pointer::Link(text),
            legacy: false,
        });
    }
    let is_strm = name
        .rsplit_once('.')
        .is_some_and(|(_, e)| e.eq_ignore_ascii_case("strm"));
    if !meta.is_file() || !is_strm || meta.len() > MAX_STRM_BYTES {
        return None;
    }
    let text = fs::read_to_string(path).ok()?;
    let (item, ids, got) = parse_strm(&text)?;
    let any = known.keys.is_empty() && known.mount_dirs.is_empty();
    let current = || {
        known
            .keys
            .iter()
            .any(|k| infinidysk_webdav::strm_key(k, ids) == got)
    };
    let legacy = !any && !current();
    if legacy
        && !(known.legacy
            && known
                .keys
                .iter()
                .any(|k| infinidysk_webdav::legacy_strm_key(k, ids) == got))
    {
        return None;
    }
    Some(Found {
        path: path.to_path_buf(),
        item,
        pointer: Pointer::Strm(text),
        legacy,
    })
}

/// The item, `.ids/…` path and `downloadKey` of a STRM's URL.
fn parse_strm(text: &str) -> Option<(ItemId, &str, &str)> {
    let text = text.trim();
    let (path, query) = text.split_once('?').unwrap_or((text, ""));
    let (_, ids) = path.rsplit_once("/view/")?;
    let item = ItemId::from_ids_path(ids)?;
    let key = query
        .split(['&', '#'])
        .find_map(|p| p.strip_prefix("downloadKey="))?;
    Some((item, ids, key))
}

/// `text` with its `downloadKey` signed by `key` in the current form.
fn rekey(text: &str, key: &str) -> String {
    let Some((_, ids, old)) = parse_strm(text) else {
        return text.to_owned();
    };
    text.replacen(
        &format!("downloadKey={old}"),
        &format!("downloadKey={}", infinidysk_webdav::strm_key(key, ids)),
        1,
    )
}

/// `path` with `.` and `..` resolved without touching the filesystem.
pub(crate) fn normalize(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            Component::CurDir => {}
            Component::ParentDir => {
                out.pop();
            }
            other => out.push(other),
        }
    }
    out
}

/// Whether `path` still holds `pointer`; `None` when it is gone.
fn holds(root: &Root, path: &Path, pointer: &Pointer) -> io::Result<Option<bool>> {
    let Some(entry) = root.entry(path)? else {
        return Ok(None);
    };
    Ok(Some(match (pointer, entry) {
        (Pointer::Link(text), Entry::Link) => root.read_link(path)?.as_os_str() == text.as_str(),
        (Pointer::Strm(text), Entry::File(len)) => {
            len <= MAX_STRM_BYTES
                && match root.read_to_string(path, MAX_STRM_BYTES) {
                    Ok(read) => read == *text,
                    Err(e) if e.kind() == io::ErrorKind::InvalidData => false,
                    Err(e) => return Err(e),
                }
        }
        _ => false,
    }))
}

/// Removes `f` if it still holds the pointer it was found with; whether it did. `f` lies
/// in one of `folders`.
pub(crate) fn unlink(f: &Found, folders: &[PathBuf]) -> io::Result<bool> {
    let root = Root::below(folders, &f.path)?;
    if holds(&root, &f.path, &f.pointer)? != Some(true) {
        return Ok(false);
    }
    root.remove(&f.path, usize::MAX)?;
    Ok(true)
}

/// Writes `pointer` at `path`, replacing what is there.
fn put(root: &Root, path: &Path, pointer: &Pointer, owner: (u32, u32)) -> io::Result<()> {
    match pointer {
        Pointer::Link(text) => root.link(path, text, owner),
        Pointer::Strm(text) => root.write(path, text.as_bytes(), owner),
    }
}

/// The folders our pointers live in: the library folders and the completed STRM directory.
/// Writes and removals stay below the one a path lies in.
pub(crate) fn folders(settings: &Settings) -> Vec<PathBuf> {
    settings
        .imports
        .library_dirs
        .iter()
        .map(|d| PathBuf::from(&d.path))
        .chain(std::iter::once(PathBuf::from(&settings.imports.strm_dir)))
        .collect()
}

/// Our pointers below `root`, in path order. Directory symlinks are not followed.
async fn walk_root(root: &str, known: &Known) -> Result<Vec<Found>, String> {
    let (root, known) = (PathBuf::from(root), known.clone());
    tokio::task::spawn_blocking(move || {
        let mut found = Vec::new();
        walk(&root, &known, &mut found).map(|()| found)
    })
    .await
    .map_err(|e| e.to_string())?
    .map_err(|e| e.to_string())
}

fn walk(dir: &Path, known: &Known, found: &mut Vec<Found>) -> io::Result<()> {
    let mut entries: Vec<_> = fs::read_dir(dir)?.collect::<Result<_, _>>()?;
    entries.sort_by_key(fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let meta = fs::symlink_metadata(&path)?;
        if meta.is_dir() {
            if !known.skip.contains(&path) {
                walk(&path, known, found)?;
            }
        } else if let Some(f) = classify(&path, &meta, known) {
            found.push(f);
        }
    }
    Ok(())
}

/// Checks a library folder can be converted: it resolves, lies inside neither the mount
/// nor the completed STRM directory, is readable and takes a file.
async fn probe_folder(
    dir: String,
    mount_dir: String,
    strm_dir: String,
    (uid, gid): (u32, u32),
) -> Result<(), String> {
    tokio::task::spawn_blocking(move || {
        let resolved = fs::canonicalize(&dir).map_err(|e| format!("unreadable: {e}"))?;
        for (other, what) in [
            (&mount_dir, "the mount"),
            (&strm_dir, "the completed STRM directory"),
        ] {
            let other = fs::canonicalize(other).unwrap_or_else(|_| PathBuf::from(other));
            if resolved.starts_with(&other) {
                return Err(format!(
                    "resolves to {}, which is inside {what}",
                    resolved.display()
                ));
            }
        }
        fs::read_dir(&resolved).map_err(|e| format!("unreadable: {e}"))?;
        let probe = resolved.join(format!(".infinidysk-probe-{}", ItemId::new()));
        fs::write(&probe, b"").map_err(|e| format!("not writable: {e}"))?;
        if fs::metadata(&probe).is_ok_and(|m| m.uid() == 0) && uid != 0 {
            let _ = std::os::unix::fs::chown(&probe, Some(uid), Some(gid));
        }
        fs::remove_file(&probe).map_err(|e| format!("not writable: {e}"))
    })
    .await
    .map_err(|e| e.to_string())?
}

fn warnings(before: &Settings, after: &Settings) -> Vec<String> {
    let mut w = Vec::new();
    let switch = before.imports.strategy != after.imports.strategy;
    match after.imports.strategy {
        ImportStrategy::Strm if switch => {
            w.push("Plex cannot play STRM files.".to_owned());
        }
        ImportStrategy::Symlinks if switch => w.push(
            "Symlinks need the mount on this host and on the media server's host.".to_owned(),
        ),
        _ => {}
    }
    if switch {
        w.push(
            "Media servers see converted files as new items: watch state and cached metadata \
             may reset."
                .to_owned(),
        );
    }
    if after.imports.strategy == ImportStrategy::Strm
        && after.arr_instances.iter().any(|a| {
            matches!(
                a.profile,
                config::ArrProfile::Lidarr | config::ArrProfile::Chaptarr
            )
        })
    {
        w.push("Lidarr and Chaptarr libraries keep symlinks, which need the mount.".to_owned());
    }
    if before.network.public_url != after.network.public_url
        && after.imports.strategy == ImportStrategy::Strm
    {
        w.push(format!(
            "STRM files media servers have not re-read keep using {}, which works only while it \
             stays reachable.",
            before.network.public_url
        ));
    }
    if before.imports.strm_key != after.imports.strm_key {
        w.push(
            "The previous key keeps playing until nothing used it for 7 days after the rewrite."
                .to_owned(),
        );
    }
    if before.mount.dir != after.mount.dir {
        w.push("Library symlinks are rewritten after the restart that moves the mount.".to_owned());
    }
    if after.imports.library_dirs.is_empty() {
        w.push("No library folders are set, so only new imports change form.".to_owned());
    }
    w
}

#[utoipa::path(get, path = "/api/v1/library", tag = "library",
    responses((status = 200, body = LibraryView)))]
pub async fn get_library(State(admin): State<Arc<Admin>>) -> Result<Json<LibraryView>, Fail> {
    Ok(Json(admin.library.view().await?))
}

/// Previews converting the library to the saved settings changed by the request.
#[utoipa::path(post, path = "/api/v1/library/preview", tag = "library",
    request_body = PreviewRequest,
    responses((status = 200, body = LibraryView), (status = 400, body = ApiError)))]
pub async fn post_library_preview(
    State(admin): State<Arc<Admin>>,
    Json(req): Json<PreviewRequest>,
) -> Result<Json<LibraryView>, Fail> {
    let mut next = admin.config.read().await.settings().clone();
    req.apply(&mut next, req.new_key.then_some("x"));
    next.validate_in(admin.live.default_cache_dir())?;
    admin.library.request_preview(req);
    Ok(Json(admin.library.view().await?))
}

#[utoipa::path(delete, path = "/api/v1/library/preview", tag = "library",
    responses((status = 200, body = LibraryView)))]
pub async fn delete_library_preview(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    admin.library.drop_preview();
    Ok(Json(admin.library.view().await?))
}

/// Saves the previewed settings and converts the library to them, leaving out the
/// folders the preview found unusable.
#[utoipa::path(post, path = "/api/v1/library/confirm", tag = "library",
    responses(
        (status = 200, body = LibraryView),
        (status = 400, body = ApiError),
        (status = 409, body = ApiError, description = "No finished preview"),
    ))]
pub async fn post_library_confirm(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    let Some((req, key, preview)) = admin.library.take_preview() else {
        return Err(Fail::Conflict("no finished preview to confirm".to_owned()));
    };
    let mut store = admin.config.write().await;
    let before = store.settings().clone();
    let mut next = before.clone();
    req.apply(&mut next, key.as_deref());
    if next.imports.strategy == ImportStrategy::Strm
        && let Err(reason) = admin.live.probe_strm_dir(&next.imports.strm_dir).await
    {
        return Err(Fail::Setting(
            "imports.strm_dir".to_owned(),
            format!(
                "Cannot write STRM files in {:?}: {reason}",
                next.imports.strm_dir
            ),
        ));
    }
    store.save(next).await?;
    let after = store.settings().clone();
    if let Err(error) = admin.live.apply(&after).await {
        return Err(Fail::Bad(format!("saved, but could not apply: {error:#}")));
    }
    drop(store);
    let excluded = preview
        .folders
        .iter()
        .filter(|f| !f.ok)
        .map(|f| f.path.clone())
        .collect();
    admin.library.confirm(&before, &after, excluded).await;
    Ok(Json(admin.library.view().await?))
}

/// Stops the conversion after the current file; the library stays mixed, which plays.
#[utoipa::path(post, path = "/api/v1/library/cancel", tag = "library",
    responses((status = 200, body = LibraryView), (status = 409, body = ApiError)))]
pub async fn post_library_cancel(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    if !admin.library.cancel().await {
        return Err(Fail::Conflict("no conversion runs".to_owned()));
    }
    Ok(Json(admin.library.view().await?))
}

/// Converts whatever is not in the confirmed form: resumes a cancelled conversion,
/// retries failed files and pending rescans, and checks the whole library.
#[utoipa::path(post, path = "/api/v1/library/check", tag = "library",
    responses((status = 200, body = LibraryView)))]
pub async fn post_library_check(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    admin.library.lock().remapped_ms = 0;
    admin.library.start(Run::default()).await;
    Ok(Json(admin.library.view().await?))
}

/// Stops accepting replaced STRM keys at once, for a leaked key.
#[utoipa::path(post, path = "/api/v1/library/keys/revoke", tag = "library",
    responses((status = 200, body = LibraryView)))]
pub async fn post_library_revoke_keys(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    admin
        .library
        .store(KEYS_KEY, &Vec::<PreviousKey>::new())
        .await;
    admin.library.publish_keys(&[]);
    info!("revoked every replaced STRM key");
    Ok(Json(admin.library.view().await?))
}

/// Stops accepting C#'s earlier STRM key form at once.
#[utoipa::path(post, path = "/api/v1/library/legacy/stop", tag = "library",
    responses((status = 200, body = LibraryView)))]
pub async fn post_library_legacy_stop(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LibraryView>, Fail> {
    admin.library.retire_legacy().await;
    info!("stopped accepting C#'s earlier STRM key form");
    Ok(Json(admin.library.view().await?))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp() -> PathBuf {
        let dir = std::env::temp_dir().join(format!("infinidysk-library-{}", ItemId::new()));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn symlink_libraries_ignore_strm_settings() {
        let form = |strategy, url: &str| Form {
            strategy,
            public_url: url.to_owned(),
            key_id: url.to_owned(),
            mount_dir: "/mnt".to_owned(),
        };
        let links = form(ImportStrategy::Symlinks, "a");
        assert!(form(ImportStrategy::Symlinks, "b").fits(&links));
        assert!(!form(ImportStrategy::Strm, "a").fits(&links));
        assert!(!form(ImportStrategy::Strm, "b").fits(&form(ImportStrategy::Strm, "a")));
        let moved = Form {
            mount_dir: "/other".to_owned(),
            ..links.clone()
        };
        assert!(!moved.fits(&links));
    }

    #[test]
    fn recognizes_only_our_pointers() {
        let dir = temp();
        let item = ItemId::new();
        let known = Known {
            mount_dirs: vec!["/mnt/old".to_owned(), "/mnt/new".to_owned()],
            keys: vec!["key".to_owned()],
            legacy: true,
            skip: Vec::new(),
        };
        let ids = item.ids_path();
        let link = |name: &str, text: &str| {
            let path = dir.join(name);
            std::os::unix::fs::symlink(text, &path).unwrap();
            path
        };
        let file = |name: &str, text: &str| {
            let path = dir.join(name);
            fs::write(&path, text).unwrap();
            path
        };
        let check = |path: &Path| {
            let meta = fs::symlink_metadata(path).unwrap();
            classify(path, &meta, &known).map(|f| (f.item, f.legacy))
        };
        assert_eq!(
            check(&link("a.mkv", &format!("/mnt/old/{ids}"))),
            Some((item, false))
        );
        assert_eq!(
            check(&link("b.mkv", &format!("/mnt/new/x/../{ids}"))),
            Some((item, false)),
            "link text is normalized"
        );
        assert_eq!(check(&link("c.mkv", &format!("/elsewhere/{ids}"))), None);
        let url = |key: &str| format!("http://h/view/{ids}?downloadKey={key}&extension=mkv");
        let hmac = infinidysk_webdav::strm_key("key", &ids);
        let legacy = infinidysk_webdav::legacy_strm_key("key", &ids);
        assert_eq!(check(&file("d.strm", &url(&hmac))), Some((item, false)));
        assert_eq!(check(&file("e.strm", &url(&legacy))), Some((item, true)));
        assert_eq!(check(&file("f.strm", &url("forged"))), None);
        assert_eq!(check(&file("g.strm", &"x".repeat(9000))), None);
        assert_eq!(
            rekey(&url(&legacy), "key").len(),
            url(&legacy).len(),
            "a rewritten earlier-form STRM keeps its size"
        );
        assert_eq!(rekey(&url(&legacy), "key"), url(&hmac));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn unlink_stays_in_the_library_folder_of_its_path() {
        let dir = temp();
        let (library, outside) = (dir.join("library"), dir.join("outside"));
        fs::create_dir_all(library.join("show")).unwrap();
        fs::create_dir_all(&outside).unwrap();
        std::os::unix::fs::symlink(&outside, library.join("moved")).unwrap();
        let item = ItemId::new();
        let text = format!("/mnt/{}", item.ids_path());
        let found = |path: PathBuf| Found {
            path,
            item,
            pointer: Pointer::Link(text.clone()),
            legacy: false,
        };
        std::os::unix::fs::symlink(&text, outside.join("a.mkv")).unwrap();
        std::os::unix::fs::symlink(&text, library.join("show/a.mkv")).unwrap();
        let folders = [library.clone()];

        assert!(unlink(&found(library.join("moved/a.mkv")), &folders).is_err());
        assert!(unlink(&found(outside.join("a.mkv")), &folders).is_err());
        assert!(fs::symlink_metadata(outside.join("a.mkv")).is_ok());
        assert!(unlink(&found(library.join("show/a.mkv")), &folders).unwrap());
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn local_rejects_paths_that_climb_out() {
        let dirs = [config::LibraryDir {
            path: "/lib".to_owned(),
            arr_path: "/arr".to_owned(),
        }];
        assert_eq!(
            local(&dirs, "/arr/show/a.strm"),
            Some(PathBuf::from("/lib/show/a.strm"))
        );
        assert_eq!(local(&dirs, "/arr/../../outside.strm"), None);
        assert_eq!(local(&dirs, "/arr/show/../../x.strm"), None);
    }

    #[test]
    fn walks_skip_the_strm_directory_inside_a_library_folder() {
        let library = temp();
        let strm_dir = library.join("completed");
        fs::create_dir_all(&strm_dir).unwrap();
        let ids = ItemId::new().ids_path();
        for path in [library.join("a.mkv"), strm_dir.join("b.mkv")] {
            std::os::unix::fs::symlink(format!("/mnt/x/{ids}"), path).unwrap();
        }
        let known = Known {
            mount_dirs: vec!["/mnt/x".to_owned()],
            skip: vec![strm_dir],
            ..Known::default()
        };
        let mut found = Vec::new();
        walk(&library, &known, &mut found).unwrap();
        let paths: Vec<_> = found.into_iter().map(|f| f.path).collect();
        assert_eq!(paths, [library.join("a.mkv")]);
        fs::remove_dir_all(library).unwrap();
    }

    #[test]
    fn strm_to_symlink_for_an_extensionless_item_has_no_trailing_dot() {
        let id = ItemId::new();
        let item = Item {
            id,
            parent: Some(ItemId::CONTENT),
            name: "data".into(),
            path: "/content/c/j/data".into(),
            kind: infinidysk_core::ItemKind::NzbFile,
            size: Some(1),
            meta_blob: None,
            nzb_blob: None,
            created_at: 0,
            release_date: None,
            health_state: infinidysk_db::HealthState::Healthy,
            encrypted: false,
        };
        let found = Found {
            path: PathBuf::from("/lib/Show/Episode.strm"),
            item: id,
            pointer: Pointer::Strm("http://x/strm".into()),
            legacy: false,
        };
        let target = Target {
            strategy: ImportStrategy::Symlinks,
            public_url: "http://x".into(),
            key: "k".into(),
            mount_dir: "/mnt".into(),
        };
        let action = plan(&found, &item, &target, &mut Stems::default());
        let Action::Move(path, _) = action else {
            panic!("{action:?}")
        };
        assert_eq!(path, PathBuf::from("/lib/Show/Episode"));
    }

    #[test]
    fn a_kept_symlink_follows_the_mount_only_once_it_is_up() {
        let mount = std::env::temp_dir().join(format!("infinidysk-kept-{}", ItemId::new()));
        let id = ItemId::new();
        let found = Found {
            path: PathBuf::from("/lib/Album/01.mp3"),
            item: id,
            pointer: Pointer::Link(format!("/old/{}", id.ids_path())),
            legacy: false,
        };
        let target = Target {
            strategy: ImportStrategy::Strm,
            public_url: "http://x".into(),
            key: "k".into(),
            mount_dir: mount.display().to_string(),
        };
        assert!(matches!(
            kept_link(&found, &target, "kept"),
            Action::Skip(r) if r != "kept"
        ));
        fs::create_dir_all(mount.join(".ids")).unwrap();
        assert!(matches!(
            kept_link(&found, &target, "kept"),
            Action::Rewrite(p) if p == target.link(id)
        ));
        fs::remove_dir_all(mount).unwrap();
    }
}
