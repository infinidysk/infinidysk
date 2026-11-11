//! Backups of the SQLite library database: the backup job, the list, downloads and the
//! 1.x leftovers.

use std::future::Future;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use infinidysk_db::{Db, Error as DbError, check_file, latest_sqlite_schema, now_ms};
use serde::{Deserialize, Serialize};
use tokio::sync::RwLock;
use tokio_util::sync::CancellationToken;
use tracing::warn;
use utoipa::ToSchema;
use uuid::Uuid;

use crate::config::{ConfigStore, MaintenanceJob, Settings};
use crate::maintenance::{MaintenanceRun, RunOutcome, Stop, Tracker};

pub mod api;
pub mod boot;
mod files;

use files::{Entry, Manifest, Part};
pub use files::{Kind, LegacyView};

const STALE_AFTER_MS: i64 = 3 * DAY_MS;
const DAY_MS: i64 = 24 * 60 * 60 * 1000;
const NOTE_MAX: usize = 500;
const PROGRESS_EVERY: Duration = Duration::from_secs(1);
pub const POSTGRES: &str = "Built-in backup and restore supports SQLite only. For PostgreSQL, stop infinidysk, take a database dump with pg_dump and copy /config/repairs as a matched pair. Restore both before restarting infinidysk. A dump alone cannot recover the repair bytes. Startup flags missing repairs for recheck.";
const CHANGED: &str = "The file changed since it was made (checksum differs); it may be damaged.";
const STAGED_CHANGED: &str =
    "The backup file changed since it was made (checksum differs); it may be damaged.";
const UNFINISHED: &str = "A restore or undo has not finished; restart infinidysk.";

pub struct Parts {
    pub db: Db,
    pub repairs: Arc<crate::repair::Repairs>,
    pub config: Arc<RwLock<ConfigStore>>,
    /// The live SQLite file; `None` on PostgreSQL.
    pub live: Option<PathBuf>,
    /// The live database's folder; 1.x backups are in `backups/` below it.
    pub config_dir: PathBuf,
    pub owner: (u32, u32),
    /// The maintenance live view; backup work shows there.
    pub tracker: Arc<Tracker>,
}

pub struct Backups {
    parts: Parts,
    /// Held for every manifest write and for each prune, from its selection to its last delete.
    manifests: tokio::sync::Mutex<()>,
    /// `created_ms` of the newest finished backup, 0 for none; for the stale warning.
    newest_ms: AtomicI64,
    /// The last verify, restore or undo that failed since startup, for the panel.
    task: Mutex<Option<TaskResult>>,
    /// An upload is being written; one at a time.
    uploading: AtomicBool,
    /// The restore or undo of this run.
    attempt: Mutex<Option<Attempt>>,
}

/// Clears [`Backups::uploading`] on drop.
struct Flag<'a>(&'a AtomicBool);

impl Drop for Flag<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

/// The last restore, from config key `backup.last_restore`.
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
pub struct LastRestore {
    pub attempt: Uuid,
    pub backup: String,
    pub backup_created_ms: Option<i64>,
    pub at_ms: i64,
    /// Why the restored database did not open; the previous one is back.
    #[serde(default)]
    pub failed: Option<String>,
    /// Why an undo did not open; the restored database stayed.
    #[serde(default)]
    pub undo_failed: Option<String>,
}

/// The database a restore replaced, kept for undo.
#[derive(Serialize, ToSchema)]
pub struct UndoView {
    size_bytes: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum AttemptAction {
    Restore,
    Undo,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum AttemptState {
    Staging,
    Restarting,
    Failed,
}

/// A restore or undo started in this run.
#[derive(Clone, Serialize, ToSchema)]
pub struct Attempt {
    id: Uuid,
    action: AttemptAction,
    state: AttemptState,
    reason: Option<String>,
}

/// The copy the backup job wrote.
#[derive(Clone, Serialize, ToSchema)]
pub struct BackupReport {
    name: String,
    size_bytes: u64,
    /// Writing, checking and hashing.
    duration_ms: u64,
    /// Old backups deleted after it.
    pruned: u64,
    pruned_bytes: u64,
}

impl BackupReport {
    pub const fn size_bytes(&self) -> u64 {
        self.size_bytes
    }
}

#[derive(Serialize, ToSchema)]
pub struct BackupsView {
    /// Why backups are unavailable (PostgreSQL); the rest is empty then.
    unavailable_reason: Option<String>,
    /// The resolved folder.
    dir: String,
    /// Why the folder cannot be used; backups fail until it is fixed.
    dir_error: Option<String>,
    free_bytes: Option<u64>,
    /// Used pages of the live database: about the size of one backup.
    database_bytes: u64,
    keep: u16,
    /// Newest first.
    backups: Vec<BackupView>,
    /// The last verify that failed since startup.
    task: Option<TaskResult>,
    /// 1.x backup folders, when any exist.
    legacy: Option<LegacyView>,
    /// The database the last restore replaced, when it is still kept.
    undo: Option<UndoView>,
    last_restore: Option<LastRestore>,
    /// The restore or undo of this run.
    attempt: Option<Attempt>,
}

#[derive(Serialize, ToSchema)]
pub struct BackupView {
    name: String,
    kind: Kind,
    created_ms: i64,
    size_bytes: u64,
    /// Shorter than its header says: still being copied, or cut off.
    incomplete: bool,
    schema_version: Option<i64>,
    /// Made by a newer infinidysk; restore is refused.
    newer: bool,
    items: Option<u64>,
    sha256: Option<String>,
    app_version: Option<String>,
    pinned: bool,
    note: String,
    verified_ms: Option<i64>,
    verify_error: Option<String>,
    source_name: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum TaskAction {
    Verify,
    Restore,
    Undo,
}

/// A backup task that failed.
#[derive(Clone, Serialize, ToSchema)]
pub struct TaskResult {
    action: TaskAction,
    name: String,
    reason: String,
    finished_ms: i64,
}

impl BackupView {
    fn of(e: &Entry) -> Self {
        let m = e.manifest.clone().unwrap_or_default();
        Self {
            name: e.name.clone(),
            kind: e.kind(),
            created_ms: e.created_ms(),
            size_bytes: e.size_bytes,
            incomplete: e.incomplete,
            schema_version: m.schema_version,
            newer: newer(m.schema_version),
            items: m.items,
            sha256: m.sha256,
            app_version: m.app_version,
            pinned: m.pinned,
            note: m.note,
            verified_ms: m.verified_ms,
            verify_error: m.verify_error,
            source_name: m.source_name,
        }
    }
}

fn newer(schema_version: Option<i64>) -> bool {
    schema_version.is_some_and(|v| v > latest_sqlite_schema())
}

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The JSON in config key `backup.last_restore`.
async fn last_restore(db: &Db) -> Option<LastRestore> {
    let rows = db.config().await.ok()?;
    let (_, json) = rows
        .into_iter()
        .find(|(k, _)| k == boot::LAST_RESTORE_KEY)?;
    serde_json::from_str(&json).ok()
}

impl Backups {
    /// Deletes leftover part files and manifests whose file is gone; reads `newest_ms`.
    pub async fn new(parts: Parts) -> Self {
        let this = Self {
            parts,
            manifests: tokio::sync::Mutex::new(()),
            newest_ms: AtomicI64::new(0),
            task: Mutex::new(None),
            uploading: AtomicBool::new(false),
            attempt: Mutex::new(None),
        };
        if let Ok(dir) = this.resolve_dir().await {
            let found = files::blocking(move || {
                files::clean_up(&dir)?;
                files::entries(&dir)
            })
            .await;
            match found {
                Ok(entries) => this.newest_ms.store(newest(&entries), Ordering::Relaxed),
                Err(error) => warn!(reason = %error, "could not read the backup folder"),
            }
        }
        this
    }

    /// Re-reads `newest_ms` from the folder after a delete; under the lock so a
    /// backup that publishes meanwhile is not lost.
    pub(super) async fn refresh_newest(&self, dir: PathBuf) {
        let _held = self.manifests.lock().await;
        if let Ok(entries) = files::blocking(move || files::entries(&dir)).await {
            self.newest_ms.store(newest(&entries), Ordering::Relaxed);
        }
    }

    async fn settings(&self) -> Settings {
        self.parts.config.read().await.settings().clone()
    }

    /// Why backups cannot run: the database is PostgreSQL.
    pub fn unavailable(&self) -> Option<&'static str> {
        self.parts.live.is_none().then_some(POSTGRES)
    }

    /// The configured or default folder, not created.
    async fn resolve_dir(&self) -> Result<PathBuf, String> {
        let live = self.parts.live.as_deref().ok_or(POSTGRES)?;
        let dir = self.settings().await.maintenance.backup.dir;
        Ok(if dir.is_empty() {
            live.parent()
                .unwrap_or_else(|| Path::new("."))
                .join("backups")
        } else {
            PathBuf::from(dir)
        })
    }

    /// The backup folder, created `0700` and owned by `PUID:PGID`. `Err` is for people:
    /// PostgreSQL, the database's own folder, or a folder that cannot be created.
    pub async fn dir(&self) -> Result<PathBuf, String> {
        use std::os::unix::fs::DirBuilderExt as _;
        let dir = self.resolve_dir().await?;
        let live_dir = self
            .parts
            .live
            .as_deref()
            .and_then(Path::parent)
            .unwrap_or_else(|| Path::new("."))
            .to_owned();
        let owner = self.parts.owner;
        let shown = dir.display().to_string();
        files::blocking(move || {
            if !dir.exists() {
                std::fs::DirBuilder::new()
                    .recursive(true)
                    .mode(0o700)
                    .create(&dir)?;
                files::own(&dir, owner);
            }
            Ok((dir.canonicalize()?, live_dir.canonicalize().ok(), dir))
        })
        .await
        .map_err(|e| format!("Could not create the backup folder {shown}: {e}"))
        .and_then(|(real, live_dir, dir)| {
            if Some(&real) == live_dir.as_ref() {
                Err("The backup folder must not be the database's folder.".to_owned())
            } else {
                Ok(dir)
            }
        })
    }

    pub async fn view(&self) -> BackupsView {
        let keep = self.settings().await.maintenance.backup.keep;
        if self.parts.live.is_none() {
            return BackupsView {
                unavailable_reason: Some(POSTGRES.to_owned()),
                dir: String::new(),
                dir_error: None,
                free_bytes: None,
                database_bytes: 0,
                keep,
                backups: Vec::new(),
                task: None,
                legacy: None,
                undo: None,
                last_restore: None,
                attempt: None,
            };
        }
        let (dir, mut dir_error) = match self.dir().await {
            Ok(dir) => (dir, None),
            Err(e) => (self.resolve_dir().await.unwrap_or_default(), Some(e)),
        };
        let database_bytes = match self.parts.db.sqlite_stats().await {
            Ok(Some(s)) => s.size_bytes.saturating_sub(s.free_bytes),
            _ => 0,
        };
        let (listed, free_bytes, legacy, undo) = {
            let (dir, config_dir) = (dir.clone(), self.parts.config_dir.clone());
            let paths = self.paths();
            // Held so `newest_ms` below matches the folder even if a backup publishes now.
            let _held = self.manifests.lock().await;
            let found = tokio::task::spawn_blocking(move || {
                (
                    files::entries(&dir),
                    infinidysk_stream::free_space(&dir),
                    files::legacy(&config_dir),
                    paths.and_then(|p| undo_size(&p)),
                )
            })
            .await
            .unwrap_or_else(|e| (Err(io::Error::other(e)), None, None, None));
            if let Ok(entries) = &found.0 {
                self.newest_ms.store(newest(entries), Ordering::Relaxed);
            }
            found
        };
        let backups = match listed {
            Ok(entries) => entries.iter().map(BackupView::of).collect(),
            Err(e) => {
                dir_error.get_or_insert_with(|| format!("Could not read the backup folder: {e}"));
                Vec::new()
            }
        };
        let last_restore = last_restore(&self.parts.db).await;
        BackupsView {
            unavailable_reason: None,
            dir: dir.display().to_string(),
            dir_error,
            free_bytes,
            database_bytes,
            keep,
            backups,
            task: lock(&self.task).clone(),
            legacy,
            undo: undo.map(|size_bytes| UndoView { size_bytes }),
            last_restore,
            attempt: lock(&self.attempt).clone(),
        }
    }

    /// The restore files; `None` on PostgreSQL.
    fn paths(&self) -> Option<boot::Paths> {
        self.parts.live.clone().map(|live| boot::Paths { live })
    }

    fn set_attempt(
        &self,
        id: Uuid,
        action: AttemptAction,
        state: AttemptState,
        reason: Option<String>,
    ) {
        *lock(&self.attempt) = Some(Attempt {
            id,
            action,
            state,
            reason,
        });
    }

    /// Marks the upload running; `None` when one already is.
    fn start_upload(&self) -> Option<Flag<'_>> {
        self.uploading
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .ok()
            .map(|_| Flag(&self.uploading))
    }

    /// Staging steps 1–7: copies, checks and stages `entry`, then commits the intent.
    /// `Err` is for people.
    async fn stage_restore(
        &self,
        dir: &Path,
        entry: Entry,
        attempt: Uuid,
        cancel: &CancellationToken,
    ) -> Result<(), String> {
        let paths = self.paths().ok_or(POSTGRES)?;
        if paths.intent().exists() {
            return Err(UNFINISHED.to_owned());
        }
        let tracker = &self.parts.tracker;
        tracker.step("Restoring: copying the backup", Some(entry.size_bytes));
        let progress = Arc::new(AtomicU64::new(0));
        let job = {
            let (src, part, cancel, progress) = (
                entry.path.clone(),
                paths.part(),
                cancel.clone(),
                Arc::clone(&progress),
            );
            files::blocking(move || {
                files::remove(&part)?;
                files::copy_to_part(&src, part, &mut |done| {
                    progress.store(done, Ordering::Relaxed);
                    !cancel.is_cancelled()
                })
            })
        };
        let (part, sha) = match self.tracked(job, || progress.load(Ordering::Relaxed)).await {
            Ok(done) => done,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => return Err("Cancelled".to_owned()),
            Err(e) => return Err(format!("Could not copy the backup: {e}")),
        };
        let sha256 = files::hex(&sha);
        let saved = entry.manifest.as_ref().and_then(|m| m.sha256.clone());
        if saved.is_some_and(|s| s != sha256) {
            return Err(STAGED_CHANGED.to_owned());
        }
        if let Ok(meta) = std::fs::metadata(&paths.live) {
            use std::os::unix::fs::MetadataExt as _;
            files::own(part.path(), (meta.uid(), meta.gid()));
        }
        tracker.step("Restoring: checking the copy", None);
        let stop = {
            let cancel = cancel.clone();
            move || cancel.is_cancelled()
        };
        let check = match check_file(part.path(), stop).await {
            Ok(check) => check,
            Err(DbError::Interrupted) => return Err("Cancelled".to_owned()),
            Err(e) => return Err(format!("The backup failed its check: {e}")),
        };
        if let Some(failure) = check.failure() {
            return Err(format!("The backup failed its check: {failure}"));
        }
        let staged = {
            let paths = paths.clone();
            files::blocking(move || boot::place_staged(&paths, part, &mut files::Steps::default()))
                .await
                .map_err(|e| format!("Could not stage the backup: {e}"))?
        };
        let name = entry.name.clone();
        let shown = sha256.clone();
        if let Err(error) = files::update_manifest(&self.manifests, dir, &name, move |m| {
            m.verified_ms = Some(now_ms());
            m.verify_error = None;
            m.sha256.get_or_insert(shown);
            m.schema_version = Some(check.schema_version);
            m.items = Some(check.items);
        })
        .await
        {
            warn!(backup = %name, reason = %error, "could not record the check on the backup");
        }
        let intent = boot::Intent {
            format: boot::FORMAT,
            attempt,
            action: boot::Action::Restore,
            phase: boot::Phase::Requested,
            backup: Some(name),
            backup_created_ms: Some(entry.created_ms()),
            swapped_ms: None,
            failed: None,
        };
        commit_intent(paths, Some(staged), intent).await
    }

    /// Writes the undo intent. `Err` is for people.
    async fn stage_undo(&self, attempt: Uuid) -> Result<(), String> {
        let paths = self.paths().ok_or(POSTGRES)?;
        let intent = boot::Intent {
            format: boot::FORMAT,
            attempt,
            action: boot::Action::Undo,
            phase: boot::Phase::Requested,
            backup: None,
            backup_created_ms: None,
            swapped_ms: None,
            failed: None,
        };
        commit_intent(paths, None, intent).await
    }

    /// Runs `job` while copying `progress()` into the live view every second.
    async fn tracked<T>(&self, job: impl Future<Output = T>, progress: impl Fn() -> u64) -> T {
        tokio::pin!(job);
        let mut tick = tokio::time::interval(PROGRESS_EVERY);
        loop {
            tokio::select! {
                out = &mut job => return out,
                _ = tick.tick() => self.parts.tracker.done(progress()),
            }
        }
    }

    async fn hash(&self, path: &Path, cancel: &CancellationToken) -> io::Result<String> {
        let progress = Arc::new(AtomicU64::new(0));
        let job = {
            let (path, cancel, progress) = (path.to_owned(), cancel.clone(), Arc::clone(&progress));
            files::blocking(move || files::hash(&path, &cancel, &progress))
        };
        let sha = self
            .tracked(job, || progress.load(Ordering::Relaxed))
            .await?;
        Ok(files::hex(&sha))
    }

    /// Writes, checks, hashes and keeps one backup, then prunes.
    pub async fn create(
        &self,
        kind: Kind,
        cancel: &CancellationToken,
    ) -> Result<BackupReport, Stop> {
        let started = Instant::now();
        let live = &self.parts.tracker;
        live.step("Checking free space", None);
        let dir = self.dir().await.map_err(Stop::Reason)?;
        let stats = self
            .parts
            .db
            .sqlite_stats()
            .await?
            .ok_or_else(|| Stop::Reason(POSTGRES.to_owned()))?;
        let estimate = stats.size_bytes.saturating_sub(stats.free_bytes);
        files::room(
            infinidysk_stream::free_space(&dir),
            estimate,
            "The backup folder",
            "Delete old backups or lower Copies to keep.",
        )
        .map_err(Stop::Reason)?;
        let name = files::new_name(&dir, kind);
        let (part, file) = Part::create(dir.join(format!(".{name}.part")))
            .map_err(|e| Stop::Reason(format!("Could not create the backup file: {e}")))?;
        drop(file);
        live.step("Writing the copy", Some(estimate));
        let written = self.parts.repairs.backup_into(part.path(), cancel);
        let path = part.path().to_owned();
        let size_now = || std::fs::metadata(&path).map_or(0, |m| m.len());
        match self.tracked(written, size_now).await {
            Ok(()) => {}
            Err(_) if cancel.is_cancelled() => return Err(Stop::Cancelled),
            Err(e)
                if e.downcast_ref::<DbError>()
                    .is_some_and(|e| matches!(e, DbError::DiskFull)) =>
            {
                return Err(Stop::Reason(
                    "The backup folder ran out of space.".to_owned(),
                ));
            }
            Err(e) => return Err(Stop::Reason(format!("Could not write the backup: {e}"))),
        }
        let size = files::blocking(move || {
            let file = std::fs::File::open(&path)?;
            file.sync_all()?;
            Ok(file.metadata()?.len())
        })
        .await
        .map_err(|e| Stop::Reason(format!("Could not save the backup: {e}")))?;
        live.step("Checking the copy", None);
        let stop = {
            let cancel = cancel.clone();
            move || cancel.is_cancelled()
        };
        let check = match check_file(part.path(), stop).await {
            Ok(check) => check,
            Err(DbError::Interrupted) => return Err(Stop::Cancelled),
            Err(e) => {
                return Err(Stop::Reason(format!(
                    "The new backup failed its check: {e}"
                )));
            }
        };
        if let Some(failure) = check.failure() {
            return Err(Stop::Reason(format!(
                "The new backup failed its check: {failure}"
            )));
        }
        live.step("Computing the checksum", Some(size));
        let sha256 = match self.hash(part.path(), cancel).await {
            Ok(sha) => sha,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => return Err(Stop::Cancelled),
            Err(e) => return Err(Stop::Reason(format!("Could not read the new backup: {e}"))),
        };
        let created_ms = now_ms();
        let manifest = Manifest {
            format: files::MANIFEST_FORMAT,
            kind,
            created_ms,
            app_version: Some(crate::update::VERSION.to_owned()),
            schema_version: Some(check.schema_version),
            items: Some(check.items),
            size_bytes: Some(size),
            sha256: Some(sha256),
            verified_ms: Some(created_ms),
            ..Manifest::default()
        };
        files::publish(&self.manifests, &dir, part, &name, &manifest)
            .await
            .map_err(|e| Stop::Reason(format!("Could not save the backup: {e}")))?;
        files::own(&dir.join(&name), self.parts.owner);
        files::own(&files::manifest_path(&dir, &name), self.parts.owner);
        self.newest_ms.fetch_max(created_ms, Ordering::Relaxed);
        live.step("Removing old backups", None);
        let keep = self.settings().await.maintenance.backup.keep;
        let (pruned, pruned_bytes) = files::prune(&self.manifests, &dir, keep.into(), &name)
            .await
            .map_err(|e| {
                Stop::Reason(format!(
                    "The backup {name} was saved, but old backups could not be removed: {e}"
                ))
            })?;
        Ok(BackupReport {
            name,
            size_bytes: size,
            duration_ms: u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX),
            pruned,
            pruned_bytes,
        })
    }

    /// Compares one backup with its saved checksum, checks it, and records the result.
    pub async fn verify(&self, name: &str, cancel: &CancellationToken) -> Result<(), String> {
        let dir = self.dir().await?;
        let entry = {
            let (dir, name) = (dir.clone(), name.to_owned());
            files::blocking(move || files::find(&dir, &name))
                .await
                .map_err(|_| "This backup is gone.".to_owned())?
        };
        self.parts
            .tracker
            .step("Computing the checksum", Some(entry.size_bytes));
        let sha256 = match self.hash(&entry.path, cancel).await {
            Ok(sha) => sha,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => return Err("Cancelled".to_owned()),
            Err(e) => return Err(format!("Could not read the backup: {e}")),
        };
        let saved = entry.manifest.as_ref().and_then(|m| m.sha256.clone());
        let (error, check) = if saved.is_some_and(|s| s != sha256) {
            (Some(CHANGED.to_owned()), None)
        } else {
            self.parts.tracker.step("Checking", None);
            let stop = {
                let cancel = cancel.clone();
                move || cancel.is_cancelled()
            };
            match check_file(&entry.path, stop).await {
                Ok(check) => (check.failure(), Some(check)),
                Err(DbError::Interrupted) => return Err("Cancelled".to_owned()),
                Err(e) => (Some(format!("Could not open the backup: {e}")), None),
            }
        };
        let recorded = error.clone();
        files::update_manifest(&self.manifests, &dir, name, move |m| {
            m.verified_ms = Some(now_ms());
            m.verify_error.clone_from(&recorded);
            if m.sha256.is_none() {
                m.sha256 = Some(sha256);
            }
            if let (None, Some(check)) = (&recorded, check) {
                m.schema_version = Some(check.schema_version);
                m.items = Some(check.items);
            }
        })
        .await
        .map_err(|e| format!("Could not save the result: {e}"))?;
        error.map_or(Ok(()), Err)
    }

    fn fail_task(&self, action: TaskAction, name: &str, reason: &str) {
        warn!(backup = %name, reason, "backup task failed");
        *lock(&self.task) = Some(TaskResult {
            action,
            name: name.to_owned(),
            reason: reason.to_owned(),
            finished_ms: now_ms(),
        });
    }

    /// The Overview warning; `None` when backups are not scheduled or on PostgreSQL.
    pub fn warning(
        &self,
        settings: &Settings,
        last_run: Option<&MaintenanceRun>,
    ) -> Option<String> {
        if self.parts.live.is_none()
            || !settings
                .maintenance
                .scheduled
                .contains(&MaintenanceJob::BackupDatabase)
        {
            return None;
        }
        match last_run.map(|r| (r.outcome, r.reason.as_deref())) {
            Some((RunOutcome::Stopped, reason)) => {
                return Some(format!(
                    "The last backup did not finish: {}",
                    reason.unwrap_or("it stopped")
                ));
            }
            Some((RunOutcome::Interrupted, _)) => {
                return Some(
                    "The last backup did not finish: the maintenance window ended first".to_owned(),
                );
            }
            _ => {}
        }
        let newest = self.newest_ms.load(Ordering::Relaxed);
        let age = now_ms() - newest;
        (newest > 0 && age > STALE_AFTER_MS).then(|| format!("No backup in {} days", age / DAY_MS))
    }
}

/// The newest scheduled or manual backup's `created_ms`; 0 when there is none.
fn newest(entries: &[Entry]) -> i64 {
    entries
        .iter()
        .filter(|e| matches!(e.kind(), Kind::Scheduled | Kind::Manual))
        .map(Entry::created_ms)
        .max()
        .unwrap_or(0)
}

/// Total size of the `U` set; `None` when there is no undo copy or a restore is waiting.
fn undo_size(paths: &boot::Paths) -> Option<u64> {
    let undo = paths.undo();
    if !undo.exists() || paths.intent().exists() {
        return None;
    }
    Some(
        ["", "-wal", "-shm", "-journal"]
            .iter()
            .filter_map(|s| std::fs::metadata(files::with(&undo, s)).ok())
            .map(|m| m.len())
            .sum(),
    )
}

/// Staging steps 6–7 off the runtime. `Err` is for people.
async fn commit_intent(
    paths: boot::Paths,
    staged: Option<Part>,
    intent: boot::Intent,
) -> Result<(), String> {
    let i = paths.intent();
    let done = tokio::task::spawn_blocking(move || {
        let mut steps = files::Steps::default();
        if let Some(staged) = staged {
            boot::commit(&paths, staged, &intent, &mut steps)
        } else {
            let bytes = serde_json::to_vec_pretty(&intent)
                .map_err(|e| files::WriteError::Unpublished(io::Error::other(e)))?;
            files::write_atomic(&paths.intent(), &bytes, &mut steps)
        }
    })
    .await
    .map_err(|e| files::WriteError::Unpublished(io::Error::other(e)));
    match done.and_then(|r| r) {
        Ok(()) => Ok(()),
        Err(files::WriteError::Unpublished(e)) => {
            Err(format!("Could not save the restore request: {e}"))
        }
        Err(files::WriteError::NotDurable(e)) => Err(format!(
            "The restore is ready but could not be saved safely: {e}. Restart infinidysk to \
             apply it, or delete {} to drop it.",
            i.display()
        )),
    }
}

#[cfg(test)]
mod tests;
