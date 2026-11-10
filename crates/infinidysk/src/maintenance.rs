//! Maintenance jobs (`infinidysk-lab:docs/maintenance-policy.md`): each runs by hand, as a dry
//! run, or once per daily window; one runs at a time.

use std::collections::hash_map::Entry;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::os::unix::fs::MetadataExt as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use axum::Json;
use axum::extract::{Path as UrlPath, Query, State};
use axum::http::StatusCode;
use infinidysk_arr::Arr;
use infinidysk_arr::library::{self as arr_library, Media, MediaFile};
use infinidysk_core::ItemId;
use infinidysk_db::metrics::Metrics as Store;
use infinidysk_db::{BUSY_TIMEOUT, Db, SqliteStats, now_ms};
use infinidysk_queue::Queue;
use infinidysk_vfs::Vfs;
use jiff::Timestamp;
use jiff::civil::Date;
use jiff::tz::TimeZone;
use percent_encoding::percent_decode_str;
use serde::{Deserialize, Serialize};
use tokio::sync::RwLock;
use tokio_util::sync::CancellationToken;
use tracing::{Instrument as _, info, info_span, warn};
use utoipa::{IntoParams, ToSchema};
use uuid::Uuid;

use crate::admin::{Admin, ApiError, Fail};
use crate::arr::Discovery;
use crate::config::{ConfigStore, MaintenanceJob, Settings};
use crate::handoff::{self, MAX_STRM_BYTES};
use crate::library::{self, Library};

const RUNS_KEY: &str = "maintenance.runs";
const WINDOW_KEY: &str = "maintenance.window";
const RUNS_KEPT: usize = 20;
const TICK: Duration = Duration::from_secs(30);
/// Fewer distinct pointers than this means the library folders are probably wrong.
const MIN_POINTERS: usize = 5;
/// A run that would remove more than this share of the files it checked waits for review.
const HOLD_PERCENT: u64 = 90;
/// A remove dead links run that finds more than this share of the links dead waits for
/// review.
const DEAD_HOLD_PERCENT: u64 = 5;
const SKIPPED_PENDING: &str = "Files the C# import skipped are not brought back yet; that runs on \
                               the next start once the library folders are readable.";
const PAGE_MAX: usize = 1000;
const NO_LIBRARY: &str = "Set your library folders under Imports first.";
const MB: u64 = 1_000_000;
/// The window compacts a database once this share of it is unused, and at least
/// [`COMPACT_MIN_FREE_BYTES`].
const COMPACT_MIN_FREE_PERCENT: u64 = 25;
const COMPACT_MIN_FREE_BYTES: u64 = 100 * MB;
const IMPORTING: &str = "Imports are running; compacting waits until they finish.";
/// How long compacting waits for background work to pause before it skips.
const PAUSE_WAIT: Duration = Duration::from_mins(2);

/// Config key set once remove dead links was scheduled; turning it off afterwards sticks.
const DEAD_LINKS_SCHEDULED_KEY: &str = "maintenance.dead_links_scheduled";

/// Schedules remove dead links once on installs that saved their own job list before
/// it existed; new installs have it by default.
pub async fn schedule_dead_links(db: &Db, store: &mut ConfigStore) {
    match db.config().await {
        Ok(stored) if stored.iter().any(|(k, _)| k == DEAD_LINKS_SCHEDULED_KEY) => return,
        Ok(_) => {}
        Err(error) => {
            warn!(reason = %error, "could not read the maintenance schedule");
            return;
        }
    }
    let mut next = store.settings().clone();
    if !next
        .maintenance
        .scheduled
        .contains(&MaintenanceJob::RemoveDeadLinks)
    {
        next.maintenance
            .scheduled
            .push(MaintenanceJob::RemoveDeadLinks);
        match store.save(next).await {
            Ok(()) => info!("scheduled removing dead library links in the maintenance window"),
            Err(error @ crate::config::Error::Locked(_)) => {
                info!(reason = %error, "kept the configured maintenance schedule");
            }
            Err(error) => {
                warn!(reason = %error, "could not schedule removing dead library links");
                return;
            }
        }
    }
    if let Err(error) = db
        .write_config(&[(DEAD_LINKS_SCHEDULED_KEY, "true")], &[])
        .await
    {
        warn!(reason = %error, "could not record the maintenance schedule change");
    }
}

pub struct Parts {
    pub db: Db,
    pub vfs: Arc<Vfs>,
    pub handoff: Arc<handoff::Writer>,
    pub config: Arc<RwLock<ConfigStore>>,
    pub library: Arc<Library>,
    pub discovery: Arc<Discovery>,
    pub queue: Arc<Queue>,
    pub metrics: Arc<crate::metrics::Metrics>,
    /// The FUSE mount folder; never walked as a library folder.
    pub mount_dir: String,
    pub live: Arc<Tracker>,
    pub backups: Arc<crate::backup::Backups>,
}

pub struct Maintenance {
    parts: Parts,
    state: Mutex<MaintenanceState>,
}

#[derive(Default)]
struct MaintenanceState {
    /// Stops the running job; set while one runs.
    cancel: Option<CancellationToken>,
    /// The latest run of each job.
    reports: HashMap<MaintenanceJob, MaintenanceReport>,
    /// Newest first.
    runs: Vec<MaintenanceRun>,
}

/// The running job, for the live view.
#[derive(Default)]
pub struct Tracker(Mutex<Option<LiveMaintenance>>);

impl Tracker {
    #[must_use]
    pub fn get(&self) -> Option<LiveMaintenance> {
        self.lock().clone()
    }

    fn lock(&self) -> MutexGuard<'_, Option<LiveMaintenance>> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub(crate) fn step(&self, step: &'static str, total: Option<u64>) {
        if let Some(live) = self.lock().as_mut() {
            live.step = step;
            live.done = 0;
            live.total = total;
        }
    }

    pub(crate) fn done(&self, done: u64) {
        if let Some(live) = self.lock().as_mut() {
            live.done = done;
        }
    }
}

/// A maintenance job running now.
#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct LiveMaintenance {
    job: MaintenanceJob,
    dry_run: bool,
    trigger: RunTrigger,
    /// What it is doing, for people.
    step: &'static str,
    done: u64,
    /// Absent while the amount of work is unknown.
    total: Option<u64>,
    /// Unix milliseconds.
    started_ms: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum RunTrigger {
    /// Started from the UI or API.
    Manual,
    /// Started by the daily window.
    Window,
    /// Removes the files of a reviewed report.
    Review,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum RunOutcome {
    Finished,
    /// Found more than the job removes on its own; confirm the report to go on.
    Held,
    /// A safety check failed; `reason` says which.
    Stopped,
    Cancelled,
    /// The window ended; the next window runs it again.
    Interrupted,
}

/// The latest run of a job.
#[derive(Clone, Serialize, ToSchema)]
pub struct MaintenanceReport {
    job: MaintenanceJob,
    dry_run: bool,
    trigger: RunTrigger,
    /// When the run started, Unix milliseconds; confirming names the report by it.
    at_ms: i64,
    finished_ms: i64,
    outcome: RunOutcome,
    reason: Option<String>,
    /// Problems a real run would stop on.
    warnings: Vec<String>,
    /// Files outside the download history when the run started; for dead links, the
    /// library links and STRM files read.
    checked: u64,
    /// Files no library pointer leads to; for dead links, links whose download is gone.
    unlinked: u64,
    unlinked_bytes: u64,
    removed: u64,
    removed_bytes: u64,
    /// Left for the next run: open by a reader, or for dead links, the Arr searched the
    /// media too often lately.
    skipped_open: u64,
    folders_removed: u64,
    /// Compact database: each database it looked at, in order.
    databases: Vec<DatabaseCompaction>,
    /// Back up database: the copy it wrote.
    backup: Option<crate::backup::BackupReport>,
    #[serde(skip)]
    files: Vec<MaintenanceFile>,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct DatabaseCompaction {
    /// `library` or `metrics`.
    name: &'static str,
    size_bytes_before: u64,
    free_bytes_before: u64,
    /// Absent when it was skipped.
    size_bytes_after: Option<u64>,
    /// How long writes waited on it.
    duration_ms: Option<u64>,
    skipped_reason: Option<String>,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct MaintenanceFile {
    /// The download's item; for dead links, the missing item the link names.
    id: Uuid,
    path: String,
    size_bytes: u64,
    /// Unix milliseconds.
    created_ms: i64,
    removed: bool,
    /// Dead links: the Arrs that list the link as a library file.
    arrs: Vec<String>,
}

/// One finished run, kept for the run history.
#[derive(Clone, Serialize, Deserialize, ToSchema)]
pub struct MaintenanceRun {
    job: MaintenanceJob,
    dry_run: bool,
    trigger: RunTrigger,
    started_ms: i64,
    finished_ms: i64,
    pub(crate) outcome: RunOutcome,
    pub(crate) reason: Option<String>,
    /// Files removed, or for dry runs and held runs the files found; databases
    /// compacted for compact database.
    files: u64,
    /// Bytes removed, found or reclaimed, like `files`.
    bytes: u64,
}

/// What the window has run so far.
#[derive(Default, Serialize, Deserialize)]
struct WindowRun {
    start_ms: i64,
    done: Vec<MaintenanceJob>,
}

struct Order {
    job: MaintenanceJob,
    dry_run: bool,
    trigger: RunTrigger,
    /// Review runs remove only these.
    only: Option<HashSet<ItemId>>,
}

pub enum Stop {
    Held,
    Cancelled,
    Reason(String),
}

impl From<infinidysk_db::Error> for Stop {
    fn from(error: infinidysk_db::Error) -> Self {
        Self::Reason(format!("database error: {error}"))
    }
}

/// The one running job; dropping it frees the slot.
pub(crate) struct Slot(Arc<Maintenance>);

impl Drop for Slot {
    fn drop(&mut self) {
        self.0.lock().cancel = None;
        *self.0.parts.live.lock() = None;
    }
}

impl Maintenance {
    pub async fn load(parts: Parts) -> Arc<Self> {
        let this = Self {
            parts,
            state: Mutex::default(),
        };
        let runs = this.stored(RUNS_KEY).await.unwrap_or_default();
        this.lock().runs = runs;
        Arc::new(this)
    }

    fn lock(&self) -> MutexGuard<'_, MaintenanceState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Runs the scheduled jobs in each window; never returns.
    pub async fn run(self: Arc<Self>) {
        loop {
            if let Some((job, start_ms, end_ms)) = self.due().await {
                self.scheduled(job, start_ms, end_ms).await;
            }
            tokio::time::sleep(TICK).await;
        }
    }

    /// The first scheduled job the current window has not run.
    async fn due(&self) -> Option<(MaintenanceJob, i64, i64)> {
        let settings = self.settings().await;
        let minutes = settings.maintenance.window.minutes()?;
        let (start_ms, end_ms, active) = window(Timestamp::now(), &TimeZone::system(), minutes)?;
        if !active {
            return None;
        }
        let done = self
            .stored::<WindowRun>(WINDOW_KEY)
            .await
            .filter(|w| w.start_ms == start_ms)
            .map(|w| w.done)
            .unwrap_or_default();
        // Compacting blocks writes, so it never starts under an import.
        let importing = !self.parts.queue.active().is_empty();
        let job = MaintenanceJob::ALL.into_iter().find(|j| {
            (j.always_scheduled() || settings.maintenance.scheduled.contains(j))
                && !done.contains(j)
                && !(importing && *j == MaintenanceJob::CompactDatabase)
                && !(*j == MaintenanceJob::BackupDatabase
                    && self.parts.backups.unavailable().is_some())
        })?;
        Some((job, start_ms, end_ms))
    }

    async fn scheduled(self: &Arc<Self>, job: MaintenanceJob, start_ms: i64, end_ms: i64) {
        let run = Order {
            job,
            dry_run: false,
            trigger: RunTrigger::Window,
            only: None,
        };
        // A manual run holds the slot; try again on the next tick.
        let Ok((slot, cancel)) = self.begin(&run) else {
            return;
        };
        let interrupted = Arc::new(AtomicBool::new(false));
        let timer = tokio::spawn({
            let (interrupted, cancel) = (Arc::clone(&interrupted), cancel.clone());
            let left = u64::try_from(end_ms - now_ms()).unwrap_or(0);
            async move {
                tokio::time::sleep(Duration::from_millis(left)).await;
                interrupted.store(true, Ordering::Relaxed);
                cancel.cancel();
            }
        });
        let outcome = self
            .execute(run, &cancel, &interrupted)
            .instrument(info_span!("maintenance", task = job.id()))
            .await;
        timer.abort();
        drop(slot);
        if outcome != RunOutcome::Interrupted {
            let mut done = self
                .stored::<WindowRun>(WINDOW_KEY)
                .await
                .filter(|w| w.start_ms == start_ms)
                .unwrap_or(WindowRun {
                    start_ms,
                    done: Vec::new(),
                });
            done.done.push(job);
            self.store(WINDOW_KEY, &done).await;
        }
    }

    fn begin(self: &Arc<Self>, run: &Order) -> Result<(Slot, CancellationToken), Fail> {
        let mut state = self.lock();
        if state.cancel.is_some() {
            return Err(Fail::Conflict("a maintenance job is running".to_owned()));
        }
        let cancel = CancellationToken::new();
        state.cancel = Some(cancel.clone());
        drop(state);
        *self.parts.live.lock() = Some(LiveMaintenance {
            job: run.job,
            dry_run: run.dry_run,
            trigger: run.trigger,
            step: "Starting",
            done: 0,
            total: None,
            started_ms: now_ms(),
        });
        Ok((Slot(Arc::clone(self)), cancel))
    }

    /// Takes the slot for backup work outside a job run (verify, restore); the live view
    /// shows it under the backup job with `step`.
    pub(crate) fn claim(
        self: &Arc<Self>,
        step: &'static str,
    ) -> Result<(Slot, CancellationToken), Fail> {
        let claimed = self.begin(&Order {
            job: MaintenanceJob::BackupDatabase,
            dry_run: false,
            trigger: RunTrigger::Manual,
            only: None,
        })?;
        self.parts.live.step(step, None);
        Ok(claimed)
    }

    /// Whether the slot holds backup work now.
    pub(crate) fn backup_busy(&self) -> bool {
        self.parts
            .live
            .get()
            .is_some_and(|l| l.job == MaintenanceJob::BackupDatabase)
    }

    /// The newest finished run of `job`.
    pub fn last_run(&self, job: MaintenanceJob) -> Option<MaintenanceRun> {
        self.lock().runs.iter().find(|r| r.job == job).cloned()
    }

    fn start(self: &Arc<Self>, run: Order) -> Result<(), Fail> {
        let (slot, cancel) = self.begin(&run)?;
        let this = Arc::clone(self);
        let span = info_span!("maintenance", task = run.job.id());
        tokio::spawn(
            async move {
                let _slot = slot;
                this.execute(run, &cancel, &AtomicBool::new(false)).await;
            }
            .instrument(span),
        );
        Ok(())
    }

    async fn execute(
        &self,
        run: Order,
        cancel: &CancellationToken,
        interrupted: &AtomicBool,
    ) -> RunOutcome {
        let mut report = MaintenanceReport {
            job: run.job,
            dry_run: run.dry_run,
            trigger: run.trigger,
            at_ms: now_ms(),
            finished_ms: 0,
            outcome: RunOutcome::Finished,
            reason: None,
            warnings: Vec::new(),
            checked: 0,
            unlinked: 0,
            unlinked_bytes: 0,
            removed: 0,
            removed_bytes: 0,
            skipped_open: 0,
            folders_removed: 0,
            databases: Vec::new(),
            backup: None,
            files: Vec::new(),
        };
        info!(dry_run = run.dry_run, trigger = ?run.trigger, "maintenance job started");
        let result = match run.job {
            MaintenanceJob::BackupDatabase => {
                let kind = if run.trigger == RunTrigger::Window {
                    crate::backup::Kind::Scheduled
                } else {
                    crate::backup::Kind::Manual
                };
                self.parts
                    .backups
                    .create(kind, cancel)
                    .await
                    .map(|backup| report.backup = Some(backup))
            }
            MaintenanceJob::RemoveUnlinked => self.remove_unlinked(&run, cancel, &mut report).await,
            MaintenanceJob::RemoveDeadLinks => {
                self.remove_dead_links(&run, cancel, &mut report).await
            }
            MaintenanceJob::CompactDatabase => {
                self.compact_database(&run, cancel, &mut report).await
            }
        };
        report.outcome = match result {
            Ok(()) => RunOutcome::Finished,
            Err(Stop::Held) => RunOutcome::Held,
            Err(Stop::Cancelled) if interrupted.load(Ordering::Relaxed) => RunOutcome::Interrupted,
            Err(Stop::Cancelled) => RunOutcome::Cancelled,
            Err(Stop::Reason(reason)) => {
                report.reason = Some(reason);
                RunOutcome::Stopped
            }
        };
        report.finished_ms = now_ms();
        let duration_ms = report.finished_ms - report.at_ms;
        let (files, bytes) = if run.job == MaintenanceJob::BackupDatabase {
            report
                .backup
                .as_ref()
                .map_or((0, 0), |b| (1, b.size_bytes()))
        } else if run.job == MaintenanceJob::CompactDatabase {
            let compacted = report.databases.iter().filter_map(|d| {
                d.size_bytes_after
                    .map(|after| d.size_bytes_before.saturating_sub(after))
            });
            compacted.fold((0, 0), |(n, sum), b| (n + 1, sum + b))
        } else if run.dry_run || report.outcome == RunOutcome::Held {
            (report.unlinked, report.unlinked_bytes)
        } else {
            (report.removed, report.removed_bytes)
        };
        match (&report.outcome, &report.reason) {
            (RunOutcome::Stopped, Some(reason)) => {
                warn!(
                    files,
                    bytes,
                    dry_run = run.dry_run,
                    reason,
                    duration_ms,
                    "maintenance job stopped"
                );
            }
            (RunOutcome::Held, _) => warn!(
                files,
                checked = report.checked,
                "maintenance job found more than it removes on its own; review the list"
            ),
            (outcome, _) => info!(
                outcome = ?outcome,
                files,
                bytes,
                duration_ms,
                "maintenance job ended"
            ),
        }
        let summary = MaintenanceRun {
            job: run.job,
            dry_run: run.dry_run,
            trigger: run.trigger,
            started_ms: report.at_ms,
            finished_ms: report.finished_ms,
            outcome: report.outcome,
            reason: report.reason.clone(),
            files,
            bytes,
        };
        let outcome = report.outcome;
        let runs = {
            let mut state = self.lock();
            state.reports.insert(run.job, report);
            state.runs.insert(0, summary);
            state.runs.truncate(RUNS_KEPT);
            state.runs.clone()
        };
        self.store(RUNS_KEY, &runs).await;
        outcome
    }

    async fn remove_unlinked(
        &self,
        run: &Order,
        cancel: &CancellationToken,
        report: &mut MaintenanceReport,
    ) -> Result<(), Stop> {
        let live = &self.parts.live;
        live.step("Checking settings", None);
        let settings = self.settings().await;
        let dirs = &settings.imports.library_dirs;
        if dirs.is_empty() {
            return Err(Stop::Reason(NO_LIBRARY.to_owned()));
        }
        self.library_settled().await?;
        for arr in crate::app::arr_instances(&settings.arr_instances) {
            let roots = infinidysk_arr::library::root_folders(self.parts.discovery.http(), &arr)
                .await
                .map_err(|e| Stop::Reason(format!("{} did not answer: {e}", arr.name)))?;
            for root in roots {
                if !dirs
                    .iter()
                    .any(|d| Path::new(&root).starts_with(d.arr_path()))
                {
                    let problem = format!(
                        "{}'s root folder {root} is not inside a library folder",
                        arr.name
                    );
                    if !run.dry_run {
                        return Err(Stop::Reason(problem));
                    }
                    report.warnings.push(problem);
                }
            }
        }

        live.step("Listing downloads", None);
        let files = self.parts.db.files_outside_history(report.at_ms).await?;
        report.checked = files.len() as u64;

        live.step("Reading library folders", None);
        let pointers = self.read_library(&settings, cancel).await?;
        if pointers.len() < MIN_POINTERS {
            return Err(Stop::Reason(format!(
                "found {} links or STRM files in your library folders; expected at least \
                 {MIN_POINTERS}",
                pointers.len()
            )));
        }
        report.files = files
            .into_iter()
            .filter(|f| !pointers.keep(f.id, &f.path))
            .filter(|f| run.only.as_ref().is_none_or(|only| only.contains(&f.id)))
            .map(|f| MaintenanceFile {
                id: f.id.0,
                path: f.path,
                size_bytes: f.size.unwrap_or(0),
                created_ms: f.created_at,
                removed: false,
                arrs: Vec::new(),
            })
            .collect();
        report.unlinked = report.files.len() as u64;
        report.unlinked_bytes = report.files.iter().map(|f| f.size_bytes).sum();
        if run.only.is_none() && report.unlinked * 100 > report.checked * HOLD_PERCENT {
            return Err(Stop::Held);
        }
        // With no files left, a stopped run's empty folders still go.
        if run.dry_run {
            return Ok(());
        }

        self.library_settled().await?;
        live.step("Removing files", Some(report.unlinked));
        for (n, file) in (1..).zip(report.files.iter_mut()) {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            let id = ItemId(file.id);
            if self
                .parts
                .vfs
                .sessions()
                .open()
                .iter()
                .any(|s| s.item == id)
            {
                report.skipped_open += 1;
            } else if self.parts.db.delete_unlinked(id, report.at_ms).await? {
                info!(path = %file.path, "removed an unlinked file");
                file.removed = true;
                report.removed += 1;
                report.removed_bytes += file.size_bytes;
            }
            live.done(n);
        }
        live.step("Removing empty folders", None);
        loop {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            let removed = self.parts.db.delete_empty_dirs(report.at_ms).await?;
            if removed == 0 {
                break;
            }
            report.folders_removed += removed;
            live.done(report.folders_removed);
        }
        self.parts.handoff.sweep(&self.parts.db).await;
        Ok(())
    }

    /// Removes library links and STRM files whose download is gone. An Arr listing one as
    /// a library file gets that record deleted and its monitored media searched; the link
    /// goes first, so the Arr never deletes a real file at that path.
    // ponytail: a crash between removing a link and telling the Arr leaves the media in
    // its Missing list without a search; journal the step if that shows up in practice.
    async fn remove_dead_links(
        &self,
        run: &Order,
        cancel: &CancellationToken,
        report: &mut MaintenanceReport,
    ) -> Result<(), Stop> {
        let live = &self.parts.live;
        live.step("Checking settings", None);
        let settings = self.settings().await;
        let dirs = &settings.imports.library_dirs;
        if dirs.is_empty() {
            return Err(Stop::Reason(NO_LIBRARY.to_owned()));
        }
        self.library_settled().await?;
        if crate::upgrade::skipped_pending(&self.parts.db).await? {
            return Err(Stop::Reason(SKIPPED_PENDING.to_owned()));
        }

        live.step("Reading library folders", None);
        let links = self
            .parts
            .library
            .pointers(&settings)
            .await
            .map_err(Stop::Reason)?;
        report.checked = links.len() as u64;
        if cancel.is_cancelled() {
            return Err(Stop::Cancelled);
        }
        let items: HashSet<ItemId> = self.parts.db.item_ids().await?.into_iter().collect();
        let dead: Vec<library::Found> = links
            .into_iter()
            .filter(|l| !items.contains(&l.item))
            .filter(|l| run.only.as_ref().is_none_or(|only| only.contains(&l.item)))
            .collect();
        report.unlinked = dead.len() as u64;
        if dead.is_empty() {
            return Ok(());
        }

        live.step("Reading the Arr libraries", None);
        let http = self.parts.discovery.http();
        let wanted: HashSet<&Path> = dead.iter().map(|l| l.path.as_path()).collect();
        let mut listed: HashMap<PathBuf, Vec<(Arr, MediaFile)>> = HashMap::new();
        let arrs = crate::app::arr_instances(&settings.arr_instances);
        for arr in arrs.into_iter().filter(arr_library::converts) {
            let files = arr_library::media_files(http, &arr)
                .await
                .map_err(|e| Stop::Reason(format!("{} did not answer: {e}", arr.name)))?;
            for file in files {
                if let Some(local) = library::local(dirs, &file.path)
                    && wanted.contains(local.as_path())
                {
                    listed.entry(local).or_default().push((arr.clone(), file));
                }
            }
        }
        report.files = dead
            .iter()
            .map(|l| MaintenanceFile {
                id: l.item.0,
                path: l.path.display().to_string(),
                size_bytes: 0,
                created_ms: 0,
                removed: false,
                arrs: listed
                    .get(&l.path)
                    .into_iter()
                    .flatten()
                    .map(|(arr, _)| arr.name.clone())
                    .collect(),
            })
            .collect();
        if run.only.is_none() && report.unlinked * 100 > report.checked * DEAD_HOLD_PERCENT {
            return Err(Stop::Held);
        }
        if run.dry_run {
            return Ok(());
        }

        self.library_settled().await?;
        live.step("Removing links", Some(report.unlinked));
        let mut monitored: HashMap<(Uuid, Media), Vec<(i64, i64)>> = HashMap::new();
        'links: for (n, (link, file)) in (1..).zip(dead.into_iter().zip(report.files.iter_mut())) {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            live.done(n);
            // Searches are reserved before anything changes; without one the link waits.
            let mut replace = Vec::new();
            for (arr, arr_file) in listed.get(&link.path).into_iter().flatten() {
                let all = match monitored.entry((arr.id, arr_file.media)) {
                    Entry::Occupied(e) => e.into_mut(),
                    Entry::Vacant(e) => e.insert(
                        arr_library::monitored(http, arr, arr_file.media)
                            .await
                            .map_err(|e| {
                                Stop::Reason(format!("{} did not answer: {e}", arr.name))
                            })?,
                    ),
                };
                let ids: Vec<i64> = all
                    .iter()
                    .filter(|(_, f)| *f == arr_file.id)
                    .map(|(id, _)| *id)
                    .collect();
                let kind = match arr_file.media {
                    Media::Series(_) => "episode",
                    Media::Movie(_) => "movie",
                };
                if !self
                    .parts
                    .db
                    .reserve_searches(arr.id, kind, &ids, now_ms())
                    .await?
                {
                    report.skipped_open += 1;
                    continue 'links;
                }
                replace.push((arr, arr_file, ids));
            }
            let removed = tokio::task::spawn_blocking({
                let (link, folders) = (link.clone(), library::folders(&settings));
                move || library::unlink(&link, &folders)
            })
            .await
            .map_err(|e| Stop::Reason(format!("removing a library link failed: {e}")))?;
            match removed {
                Ok(true) => {}
                // Replaced or removed since the folders were read.
                Ok(false) => continue,
                Err(error) => {
                    warn!(path = %file.path, reason = %error, "could not remove a dead library link");
                    continue;
                }
            }
            info!(path = %file.path, "removed a dead library link");
            file.removed = true;
            report.removed += 1;
            for (arr, arr_file, ids) in replace {
                match arr_library::replace_file(http, arr, arr_file, &ids).await {
                    Ok(true) => info!(
                        arr = %arr.name,
                        path = %arr_file.path,
                        "removed the Arr's record of a dead library link and searched again"
                    ),
                    Ok(false) => {}
                    Err(error) => warn!(
                        arr = %arr.name,
                        path = %arr_file.path,
                        reason = %error,
                        "could not remove the Arr's record of a dead library link"
                    ),
                }
            }
        }
        Ok(())
    }

    /// `VACUUM`s the library database (SQLite only) and the metrics store when enough of
    /// them is unused and the disks have room for the copy.
    async fn compact_database(
        &self,
        run: &Order,
        cancel: &CancellationToken,
        report: &mut MaintenanceReport,
    ) -> Result<(), Stop> {
        let live = &self.parts.live;
        let manual = run.trigger != RunTrigger::Window;
        let mut databases = vec![("library", Database::Library(&self.parts.db))];
        if let Some(store) = self.parts.metrics.store() {
            databases.push(("metrics", Database::Metrics(store)));
        }
        live.step("Compacting databases", Some(databases.len() as u64));
        for (n, (name, database)) in (1..).zip(databases) {
            if cancel.is_cancelled() {
                return Err(Stop::Cancelled);
            }
            if !self.parts.queue.active().is_empty() {
                return Err(Stop::Reason(IMPORTING.to_owned()));
            }
            let Some(before) = database.stats().await? else {
                info!(
                    database = name,
                    reason = "PostgreSQL reclaims space on its own",
                    "skipped compacting the database"
                );
                live.done(n);
                continue;
            };
            let check = should_compact(
                &before,
                before.path.parent().and_then(infinidysk_stream::free_space),
                infinidysk_stream::free_space(&sqlite_temp_dir()),
                manual,
            );
            let mut entry = DatabaseCompaction {
                name,
                size_bytes_before: before.size_bytes,
                free_bytes_before: before.free_bytes,
                size_bytes_after: None,
                duration_ms: None,
                skipped_reason: None,
            };
            // The metrics store pauses its own writes.
            let paused = match (&check, &database) {
                (Ok(()), Database::Library(db)) => tokio::select! {
                    () = cancel.cancelled() => return Err(Stop::Cancelled),
                    paused = tokio::time::timeout(PAUSE_WAIT, db.pause_writes()) => paused
                        .map(Some)
                        .map_err(|_| "background work did not pause within 2 minutes".to_owned()),
                },
                _ => Ok(None),
            };
            match check.and(paused) {
                Err(reason) => {
                    info!(database = name, reason, "skipped compacting the database");
                    entry.skipped_reason = Some(reason);
                }
                Ok(paused) => {
                    let started = Instant::now();
                    database.compact().await?;
                    drop(paused);
                    let took = started.elapsed();
                    let after = database.stats().await?.map_or(0, |s| s.size_bytes);
                    let duration_ms = u64::try_from(took.as_millis()).unwrap_or(u64::MAX);
                    let reclaimed_bytes = before.size_bytes.saturating_sub(after);
                    if took >= BUSY_TIMEOUT && matches!(database, Database::Library(_)) {
                        warn!(
                            database = name,
                            duration_ms,
                            size_bytes_before = before.size_bytes,
                            size_bytes_after = after,
                            reclaimed_bytes,
                            reason = "UI and settings writes made while it ran may have failed",
                            "compacted the database"
                        );
                    } else {
                        info!(
                            database = name,
                            duration_ms,
                            size_bytes_before = before.size_bytes,
                            size_bytes_after = after,
                            reclaimed_bytes,
                            "compacted the database"
                        );
                    }
                    entry.size_bytes_after = Some(after);
                    entry.duration_ms = Some(duration_ms);
                }
            }
            report.databases.push(entry);
            live.done(n);
        }
        Ok(())
    }

    async fn library_settled(&self) -> Result<(), Stop> {
        match self.parts.library.moving().await? {
            Some(why) => Err(Stop::Reason(format!("{why}; run it again once it ends"))),
            None => Ok(()),
        }
    }

    async fn read_library(
        &self,
        settings: &Settings,
        cancel: &CancellationToken,
    ) -> Result<Pointers, Stop> {
        let roots: Vec<PathBuf> = settings
            .imports
            .library_dirs
            .iter()
            .map(|d| PathBuf::from(&d.path))
            .collect();
        let strm_dir = PathBuf::from(&settings.imports.strm_dir);
        let mounts = [
            PathBuf::from(&settings.mount.dir),
            PathBuf::from(&self.parts.mount_dir),
        ];
        let (cancel, live) = (cancel.clone(), Arc::clone(&self.parts.live));
        tokio::task::spawn_blocking(move || {
            read_folders(&roots, &strm_dir, &mounts, &cancel, &|n| live.done(n))
        })
        .await
        .map_err(|e| Stop::Reason(format!("reading the library folders failed: {e}")))?
    }

    async fn settings(&self) -> Settings {
        self.parts.config.read().await.settings().clone()
    }

    async fn stored<T: serde::de::DeserializeOwned>(&self, key: &str) -> Option<T> {
        match self.parts.db.config().await {
            Ok(config) => config
                .into_iter()
                .find(|(k, _)| k == key)
                .and_then(|(_, v)| serde_json::from_str(&v).ok()),
            Err(error) => {
                warn!(reason = %error, key, "could not read maintenance state");
                None
            }
        }
    }

    async fn store<T: Serialize>(&self, key: &str, value: &T) {
        let value = serde_json::to_string(value).unwrap_or_default();
        if let Err(error) = self.parts.db.write_config(&[(key, &value)], &[]).await {
            warn!(reason = %error, key, "could not save maintenance state");
        }
    }

    async fn view(&self) -> MaintenanceView {
        let settings = self.settings().await;
        let tz = TimeZone::system();
        let now = Timestamp::now();
        let time_zone = zone_label(&tz, now);
        let local = |ms: i64| {
            Timestamp::from_millisecond(ms)
                .map(|t| t.to_zoned(tz.clone()).datetime().to_string())
                .unwrap_or_default()
        };
        let window = settings
            .maintenance
            .window
            .minutes()
            .and_then(|m| window(now, &tz, m))
            .map(|(start_ms, end_ms, active)| MaintenanceWindowView {
                start_ms,
                end_ms,
                start_local: local(start_ms),
                end_local: local(end_ms),
                active,
            });
        let state = self.lock();
        let jobs = MaintenanceJob::ALL
            .into_iter()
            .map(|job| {
                let unavailable_reason = match job {
                    MaintenanceJob::BackupDatabase => {
                        self.parts.backups.unavailable().map(str::to_owned)
                    }
                    MaintenanceJob::RemoveUnlinked | MaintenanceJob::RemoveDeadLinks => settings
                        .imports
                        .library_dirs
                        .is_empty()
                        .then(|| NO_LIBRARY.to_owned()),
                    MaintenanceJob::CompactDatabase => {
                        (!self.parts.queue.active().is_empty()).then(|| IMPORTING.to_owned())
                    }
                };
                MaintenanceJobView {
                    job,
                    scheduled: job.always_scheduled()
                        || settings.maintenance.scheduled.contains(&job),
                    always_scheduled: job.always_scheduled(),
                    has_dry_run: job.has_dry_run(),
                    unavailable_reason,
                    report: state.reports.get(&job).cloned().map(|mut r| {
                        r.files = Vec::new();
                        r
                    }),
                }
            })
            .collect();
        MaintenanceView {
            time_zone,
            window,
            running: self.parts.live.get(),
            jobs,
            runs: state.runs.clone(),
        }
    }
}

enum Database<'a> {
    Library(&'a Db),
    Metrics(&'a Store),
}

impl Database<'_> {
    /// `None` on PostgreSQL.
    async fn stats(&self) -> Result<Option<SqliteStats>, infinidysk_db::Error> {
        match self {
            Self::Library(db) => db.sqlite_stats().await,
            Self::Metrics(store) => store.stats().await.map(Some),
        }
    }

    async fn compact(&self) -> Result<(), infinidysk_db::Error> {
        match self {
            Self::Library(db) => db.compact().await,
            Self::Metrics(store) => store.compact().await,
        }
    }
}

/// Why a database is not compacted now. The window waits for enough unused space; a
/// manual run compacts any. Both need room for `VACUUM`'s copy: twice the file next to
/// it (the new file goes through the WAL) and once in SQLite's temp folder.
fn should_compact(
    stats: &SqliteStats,
    free_dir: Option<u64>,
    free_tmp: Option<u64>,
    manual: bool,
) -> Result<(), String> {
    let (size, free) = (stats.size_bytes, stats.free_bytes);
    if free == 0 {
        return Err("nothing to reclaim".to_owned());
    }
    if !manual && (free * 100 < size * COMPACT_MIN_FREE_PERCENT || free < COMPACT_MIN_FREE_BYTES) {
        return Err(format!(
            "{} MB of its {} MB can be reclaimed; the window compacts at \
             {COMPACT_MIN_FREE_PERCENT}% and {} MB",
            free / MB,
            size / MB,
            COMPACT_MIN_FREE_BYTES / MB
        ));
    }
    for (free_disk, need, place) in [
        (free_dir, size * 2, "its folder"),
        (free_tmp, size, "the temp folder"),
    ] {
        match free_disk {
            None => return Err(format!("could not read the free space in {place}")),
            Some(f) if f < need => {
                return Err(format!(
                    "{place} has {} MB free; compacting needs {} MB",
                    f / MB,
                    need / MB
                ));
            }
            Some(_) => {}
        }
    }
    Ok(())
}

/// Where SQLite writes `VACUUM`'s temporary copy on Unix.
fn sqlite_temp_dir() -> PathBuf {
    ["SQLITE_TMPDIR", "TMPDIR"]
        .into_iter()
        .filter_map(std::env::var_os)
        .map(PathBuf::from)
        .chain(["/var/tmp", "/usr/tmp", "/tmp"].map(PathBuf::from))
        .find(|dir| dir.is_dir())
        .unwrap_or_else(|| PathBuf::from("/tmp"))
}

/// The zone's IANA name, else `UTC` or `UTC±HH[:MM]`; says so when `TZ` was not detected.
fn zone_label(tz: &TimeZone, now: Timestamp) -> String {
    if tz.is_unknown() {
        return "UTC (TZ not set)".to_owned();
    }
    if let Some(name) = tz.iana_name() {
        return name.to_owned();
    }
    let offset = tz.to_offset(now);
    if offset.seconds() == 0 {
        "UTC".to_owned()
    } else {
        format!("UTC{offset}")
    }
}

/// The window containing `now` (`active`) or else the next one, as Unix milliseconds
/// `(start, end, active)`. `start` and `end` are minutes after midnight in `tz`.
fn window(now: Timestamp, tz: &TimeZone, (start, end): (u16, u16)) -> Option<(i64, i64, bool)> {
    let at = |date: Date, minutes: u16| {
        let (h, m) = (
            i8::try_from(minutes / 60).ok()?,
            i8::try_from(minutes % 60).ok()?,
        );
        let zoned = date.at(h, m, 0, 0).to_zoned(tz.clone()).ok()?;
        Some(zoned.timestamp().as_millisecond())
    };
    let today = now.to_zoned(tz.clone()).date();
    let now = now.as_millisecond();
    for day in [today.yesterday().ok()?, today, today.tomorrow().ok()?] {
        let start_ms = at(day, start)?;
        let end_day = if end > start {
            day
        } else {
            day.tomorrow().ok()?
        };
        let end_ms = at(end_day, end)?;
        if now < end_ms {
            return Some((start_ms, end_ms, start_ms <= now));
        }
    }
    None
}

/// What the library's symlinks and STRM files lead to.
#[derive(Default)]
pub struct Pointers {
    ids: HashSet<ItemId>,
    /// `/content` paths; a file under one is kept.
    paths: HashSet<String>,
}

impl Pointers {
    /// Records what a symlink target or STRM URL names: an id under `/.ids/`, and
    /// paths under `/content/` or `/completed-symlinks/`.
    fn add(&mut self, target: &str) {
        let target = target.split(['?', '#']).next().unwrap_or_default();
        if let Some((_, rest)) = target.rsplit_once("/.ids/")
            && let Some(name) = Path::new(rest).file_stem().and_then(|name| name.to_str())
            && let Ok(id) = name.parse()
        {
            self.ids.insert(id);
        }
        for root in ["/content/", "/completed-symlinks/"] {
            for (at, _) in target.match_indices(root) {
                let rest = target[at + root.len()..].trim_end_matches('/');
                if !rest.is_empty() {
                    self.paths.insert(format!("/content/{rest}"));
                }
            }
        }
    }

    fn len(&self) -> usize {
        self.ids.len() + self.paths.len()
    }

    pub fn keep(&self, id: ItemId, path: &str) -> bool {
        if self.ids.contains(&id) {
            return true;
        }
        let mut path = path;
        loop {
            if self.paths.contains(path) {
                return true;
            }
            match path.rfind('/') {
                Some(at) if at > 0 => path = &path[..at],
                _ => return false,
            }
        }
    }
}

/// Walks the library folders and the STRM folder for pointers. Stops on any read
/// error, an empty or missing library folder, or one inside the mount.
pub fn read_folders(
    roots: &[PathBuf],
    strm_dir: &Path,
    mounts: &[PathBuf],
    cancel: &CancellationToken,
    tick: &dyn Fn(u64),
) -> Result<Pointers, Stop> {
    let mount_paths: Vec<PathBuf> = mounts
        .iter()
        .filter(|m| !m.as_os_str().is_empty())
        .flat_map(|m| [fs::canonicalize(m).ok(), Some(m.clone())])
        .flatten()
        .collect();
    let mut walk = Walk {
        skip: mount_paths
            .iter()
            .filter_map(|m| fs::metadata(m).ok())
            .map(|m| (m.dev(), m.ino()))
            .collect(),
        pointers: Pointers::default(),
        seen: 0,
        cancel,
        tick,
    };
    for root in roots {
        let shown = root.display();
        let unreadable = |e: io::Error| Stop::Reason(format!("library folder {shown}: {e}"));
        let resolved = fs::canonicalize(root).map_err(unreadable)?;
        if mount_paths.iter().any(|m| resolved.starts_with(m)) {
            return Err(Stop::Reason(format!(
                "library folder {shown} is inside the mount"
            )));
        }
        if fs::read_dir(&resolved)
            .map_err(unreadable)?
            .next()
            .is_none()
        {
            return Err(Stop::Reason(format!("library folder {shown} is empty")));
        }
        walk.dir(&resolved)?;
    }
    if strm_dir.is_dir() && !roots.iter().any(|r| strm_dir.starts_with(r)) {
        walk.dir(strm_dir)?;
    }
    Ok(walk.pointers)
}

struct Walk<'a> {
    /// `(device, inode)` of folders not to enter.
    skip: Vec<(u64, u64)>,
    pointers: Pointers,
    seen: u64,
    cancel: &'a CancellationToken,
    tick: &'a dyn Fn(u64),
}

impl Walk<'_> {
    fn dir(&mut self, dir: &Path) -> Result<(), Stop> {
        if self.cancel.is_cancelled() {
            return Err(Stop::Cancelled);
        }
        let failed = |e: io::Error, path: &Path| {
            Stop::Reason(format!("could not read {}: {e}", path.display()))
        };
        for entry in fs::read_dir(dir).map_err(|e| failed(e, dir))? {
            let path = entry.map_err(|e| failed(e, dir))?.path();
            let meta = fs::symlink_metadata(&path).map_err(|e| failed(e, &path))?;
            self.seen += 1;
            if self.seen.is_multiple_of(1000) {
                (self.tick)(self.seen);
            }
            if meta.is_symlink() {
                let target = fs::read_link(&path).map_err(|e| failed(e, &path))?;
                self.pointers
                    .add(&library::normalize(&dir.join(target)).to_string_lossy());
            } else if meta.is_dir() {
                if !self.skip.contains(&(meta.dev(), meta.ino())) {
                    self.dir(&path)?;
                }
            } else if meta.is_file()
                && meta.len() <= MAX_STRM_BYTES
                && path
                    .extension()
                    .is_some_and(|e| e.eq_ignore_ascii_case("strm"))
            {
                let bytes = fs::read(&path).map_err(|e| failed(e, &path))?;
                let url = String::from_utf8_lossy(&bytes);
                self.pointers
                    .add(&percent_decode_str(url.trim()).decode_utf8_lossy());
            }
        }
        Ok(())
    }
}

#[derive(Serialize, ToSchema)]
pub struct MaintenanceView {
    /// The server's time zone, which the window follows.
    time_zone: String,
    /// The current window, else the next; absent when the window setting is invalid.
    window: Option<MaintenanceWindowView>,
    running: Option<LiveMaintenance>,
    jobs: Vec<MaintenanceJobView>,
    /// Newest first; the last 20.
    runs: Vec<MaintenanceRun>,
}

#[derive(Serialize, ToSchema)]
struct MaintenanceWindowView {
    start_ms: i64,
    end_ms: i64,
    /// `start_ms` as wall-clock time in the server's zone, `YYYY-MM-DDTHH:MM:SS`.
    start_local: String,
    /// `end_ms` as wall-clock time in the server's zone.
    end_local: String,
    active: bool,
}

#[derive(Serialize, ToSchema)]
struct MaintenanceJobView {
    job: MaintenanceJob,
    /// Runs in the window.
    scheduled: bool,
    /// Runs in every window; `scheduled` cannot be turned off.
    always_scheduled: bool,
    has_dry_run: bool,
    /// Why the job cannot run now.
    unavailable_reason: Option<String>,
    /// The latest run since start; its files come from the report endpoint.
    report: Option<MaintenanceReport>,
}

#[derive(Deserialize, ToSchema)]
pub struct MaintenanceRunRequest {
    /// List what the job would do without changing anything.
    #[serde(default)]
    dry_run: bool,
}

#[derive(Deserialize, ToSchema)]
pub struct MaintenanceConfirm {
    /// The report's `at_ms`.
    at_ms: i64,
}

#[derive(Deserialize, IntoParams)]
#[into_params(parameter_in = Query)]
pub struct PageQuery {
    offset: Option<usize>,
    /// At most 1000; default 100.
    limit: Option<usize>,
}

#[derive(Serialize, ToSchema)]
pub struct MaintenanceReportPage {
    report: MaintenanceReport,
    files: Vec<MaintenanceFile>,
    total: usize,
}

/// Maintenance jobs, the window and recent runs.
#[utoipa::path(get, path = "/api/v1/maintenance", tag = "maintenance",
    responses((status = 200, body = MaintenanceView)))]
pub async fn get_maintenance(State(admin): State<Arc<Admin>>) -> Json<MaintenanceView> {
    Json(admin.maintenance.view().await)
}

/// Starts a job now, outside the window.
#[utoipa::path(post, path = "/api/v1/maintenance/{job}/run", tag = "maintenance",
    params(("job" = MaintenanceJob, Path)), request_body = MaintenanceRunRequest,
    responses((status = 200, body = MaintenanceView),
        (status = 400, body = ApiError, description = "the job cannot run now"),
        (status = 409, body = ApiError, description = "a job is running")))]
pub async fn post_run(
    State(admin): State<Arc<Admin>>,
    UrlPath(job): UrlPath<MaintenanceJob>,
    Json(body): Json<MaintenanceRunRequest>,
) -> Result<Json<MaintenanceView>, Fail> {
    let view = admin.maintenance.view().await;
    if let Some(reason) = view
        .jobs
        .iter()
        .find(|j| j.job == job)
        .and_then(|j| j.unavailable_reason.clone())
    {
        return Err(Fail::Bad(reason));
    }
    if body.dry_run && !job.has_dry_run() {
        return Err(Fail::Bad("this job has no dry run".to_owned()));
    }
    admin.maintenance.start(Order {
        job,
        dry_run: body.dry_run,
        trigger: RunTrigger::Manual,
        only: None,
    })?;
    Ok(Json(admin.maintenance.view().await))
}

/// Stops the running job; files it already removed stay removed.
#[utoipa::path(post, path = "/api/v1/maintenance/cancel", tag = "maintenance",
    responses((status = 204), (status = 409, body = ApiError, description = "nothing runs")))]
pub async fn post_cancel(State(admin): State<Arc<Admin>>) -> Result<StatusCode, Fail> {
    match &admin.maintenance.lock().cancel {
        Some(cancel) => {
            cancel.cancel();
            Ok(StatusCode::NO_CONTENT)
        }
        None => Err(Fail::Conflict("no maintenance job is running".to_owned())),
    }
}

/// The latest run's report with a page of its files, in path order.
#[utoipa::path(get, path = "/api/v1/maintenance/{job}/report", tag = "maintenance",
    params(("job" = MaintenanceJob, Path), PageQuery),
    responses((status = 200, body = MaintenanceReportPage), (status = 404, body = ApiError)))]
pub async fn get_report(
    State(admin): State<Arc<Admin>>,
    UrlPath(job): UrlPath<MaintenanceJob>,
    Query(page): Query<PageQuery>,
) -> Result<Json<MaintenanceReportPage>, Fail> {
    let state = admin.maintenance.lock();
    let report = state.reports.get(&job).ok_or(Fail::NotFound)?;
    let offset = page.offset.unwrap_or(0);
    let limit = page.limit.unwrap_or(100).min(PAGE_MAX);
    let files = report
        .files
        .iter()
        .skip(offset)
        .take(limit)
        .cloned()
        .collect();
    let total = report.files.len();
    let mut head = report.clone();
    drop(state);
    head.files = Vec::new();
    Ok(Json(MaintenanceReportPage {
        report: head,
        files,
        total,
    }))
}

/// Removes the files a held run or a dry run listed that are still unlinked.
#[utoipa::path(post, path = "/api/v1/maintenance/{job}/report/confirm", tag = "maintenance",
    params(("job" = MaintenanceJob, Path)), request_body = MaintenanceConfirm,
    responses((status = 200, body = MaintenanceView),
        (status = 404, body = ApiError),
        (status = 409, body = ApiError, description = "a job is running or the report changed")))]
pub async fn post_confirm(
    State(admin): State<Arc<Admin>>,
    UrlPath(job): UrlPath<MaintenanceJob>,
    Json(body): Json<MaintenanceConfirm>,
) -> Result<Json<MaintenanceView>, Fail> {
    let report = admin.maintenance.lock().reports.get(&job).cloned();
    let report = report.ok_or(Fail::NotFound)?;
    let reviewable = report.outcome == RunOutcome::Held
        || (report.dry_run && report.outcome == RunOutcome::Finished);
    if report.at_ms != body.at_ms || !reviewable || report.files.is_empty() {
        return Err(Fail::Conflict(
            "this report changed or has nothing to remove".to_owned(),
        ));
    }
    let only = report.files.iter().map(|f| ItemId(f.id)).collect();
    admin.maintenance.start(Order {
        job,
        dry_run: false,
        trigger: RunTrigger::Review,
        only: Some(only),
    })?;
    Ok(Json(admin.maintenance.view().await))
}

/// Forgets the latest report.
#[utoipa::path(delete, path = "/api/v1/maintenance/{job}/report", tag = "maintenance",
    params(("job" = MaintenanceJob, Path)), responses((status = 204)))]
pub async fn delete_report(
    State(admin): State<Arc<Admin>>,
    UrlPath(job): UrlPath<MaintenanceJob>,
) -> StatusCode {
    admin.maintenance.lock().reports.remove(&job);
    StatusCode::NO_CONTENT
}

#[cfg(test)]
mod tests {
    use std::os::unix::fs::symlink;

    use super::*;

    #[tokio::test]
    async fn a_database_stop_says_what_the_database_reported() {
        let reason = |e: sqlx::Error| match Stop::from(infinidysk_db::Error::from(e)) {
            Stop::Reason(r) => r,
            _ => unreachable!("a database error is a reason"),
        };
        let timeout = reason(sqlx::Error::PoolTimedOut);
        let closed = reason(sqlx::Error::PoolClosed);
        assert!(timeout.contains("pool timed out"), "{timeout}");
        assert!(closed.contains("closed pool"), "{closed}");

        let pool = sqlx::SqlitePool::connect("sqlite::memory:")
            .await
            .expect("open");
        let missing = sqlx::query("SELECT * FROM missing")
            .execute(&pool)
            .await
            .expect_err("no such table");
        let missing = reason(missing);
        assert!(missing.contains("(code: 1)"), "{missing}");
        assert!(missing.contains("no such table"), "{missing}");
    }

    #[test]
    fn windows_cross_midnight_and_find_the_next() {
        let tz = TimeZone::UTC;
        let at = |s: &str| s.parse::<Timestamp>().unwrap().as_millisecond();
        let now = |s: &str| s.parse::<Timestamp>().unwrap();
        assert_eq!(
            window(now("2026-01-02T04:00:00Z"), &tz, (180, 360)),
            Some((at("2026-01-02T03:00:00Z"), at("2026-01-02T06:00:00Z"), true))
        );
        assert_eq!(
            window(now("2026-01-02T07:00:00Z"), &tz, (180, 360)),
            Some((
                at("2026-01-03T03:00:00Z"),
                at("2026-01-03T06:00:00Z"),
                false
            ))
        );
        assert_eq!(
            window(now("2026-01-02T01:00:00Z"), &tz, (22 * 60, 120)),
            Some((at("2026-01-01T22:00:00Z"), at("2026-01-02T02:00:00Z"), true))
        );
    }

    #[test]
    fn zone_labels_never_show_a_bare_offset() {
        let now = Timestamp::UNIX_EPOCH;
        let fixed = |s| TimeZone::fixed(jiff::tz::Offset::from_seconds(s).unwrap());
        assert_eq!(zone_label(&TimeZone::unknown(), now), "UTC (TZ not set)");
        assert_eq!(zone_label(&TimeZone::UTC, now), "UTC");
        assert_eq!(zone_label(&fixed(0), now), "UTC");
        assert_eq!(zone_label(&fixed(5 * 3600 + 1800), now), "UTC+05:30");
    }

    #[test]
    fn compacting_needs_unused_space_and_room_on_disk() {
        let stats = |size_bytes, free_bytes| SqliteStats {
            path: PathBuf::from("/data/db.sqlite"),
            size_bytes,
            free_bytes,
        };
        let gb = 1000 * MB;
        let room = (Some(10 * gb), Some(10 * gb));
        let check = |s: &SqliteStats, (dir, tmp): (Option<u64>, Option<u64>), manual| {
            should_compact(s, dir, tmp, manual)
        };
        assert!(check(&stats(gb, 300 * MB), room, false).is_ok());
        // Under 25% or under 100 MB waits for the window, but not by hand.
        assert!(check(&stats(gb, 200 * MB), room, false).is_err());
        assert!(check(&stats(200 * MB, 90 * MB), room, false).is_err());
        assert!(check(&stats(gb, 200 * MB), room, true).is_ok());
        assert!(check(&stats(gb, 0), room, true).is_err());
        // Twice the file next to it, once in the temp folder.
        assert!(check(&stats(gb, 300 * MB), (Some(2 * gb - 1), Some(gb)), true).is_err());
        assert!(check(&stats(gb, 300 * MB), (Some(2 * gb), Some(gb - 1)), true).is_err());
        assert!(check(&stats(gb, 300 * MB), (Some(2 * gb), Some(gb)), true).is_ok());
        assert!(check(&stats(gb, 300 * MB), (None, Some(gb)), true).is_err());
    }

    #[test]
    fn library_pointers_keep_their_files() {
        let item = ItemId(Uuid::new_v4());
        for suffix in ["", ".mkv", ".strm?k=1", ".mkv#fragment"] {
            let mut pointers = Pointers::default();
            pointers.add(&format!("/mnt/x/.ids/a/b/c/d/e/{}{suffix}", item.0));
            assert!(pointers.keep(item, "/content/job/file.mkv"), "{suffix}");
        }
        let root = std::env::temp_dir().join(format!("infinidysk-maint-{}", Uuid::new_v4()));
        let (library, mount) = (root.join("library"), root.join("library/mount"));
        fs::create_dir_all(library.join("show")).unwrap();
        fs::create_dir_all(&mount).unwrap();
        let (linked, streamed, inside) = (
            ItemId(Uuid::new_v4()),
            ItemId(Uuid::new_v4()),
            ItemId(Uuid::new_v4()),
        );
        symlink(
            format!("/mnt/x/.ids/a/b/c/d/e/{}.mkv", linked.0),
            library.join("show/a.mkv"),
        )
        .unwrap();
        symlink(
            "../../mnt/x/completed-symlinks/tv/job",
            library.join("show/b"),
        )
        .unwrap();
        fs::write(
            library.join("show/c.STRM"),
            format!("https://h/view/.ids/a/b/c/d/e/{}.strm?k=1", streamed.0),
        )
        .unwrap();
        fs::write(
            library.join("show/d.strm"),
            "https://h/view/content/movies/My%20Job/f.mkv",
        )
        .unwrap();
        symlink("/mnt/x/content/other", library.join("e")).unwrap();
        // The mount is skipped even inside a library folder.
        symlink(format!("/mnt/x/.ids/{}", inside.0), mount.join("f")).unwrap();

        let cancel = CancellationToken::new();
        let read = |roots: &[PathBuf]| {
            read_folders(
                roots,
                Path::new(""),
                std::slice::from_ref(&mount),
                &cancel,
                &|_| {},
            )
        };
        let Ok(found) = read(std::slice::from_ref(&library)) else {
            panic!("walk stopped")
        };
        assert!(found.keep(linked, "/content/a/x.mkv"));
        assert!(found.keep(streamed, "/content/b/x.mkv"));
        assert!(found.keep(ItemId(Uuid::nil()), "/content/tv/job/x.mkv"));
        assert!(found.keep(ItemId(Uuid::nil()), "/content/movies/My Job/f.mkv"));
        assert!(!found.keep(inside, "/content/tv/jobs/x.mkv"));
        assert!(!found.keep(ItemId(Uuid::nil()), "/content/movies/My Job/g.mkv"));

        fs::create_dir(root.join("empty")).unwrap();
        assert!(matches!(read(&[root.join("empty")]), Err(Stop::Reason(r)) if r.contains("empty")));
        assert!(matches!(
            read(&[root.join("missing")]),
            Err(Stop::Reason(_))
        ));
        assert!(
            matches!(read(std::slice::from_ref(&mount)), Err(Stop::Reason(r)) if r.contains("mount"))
        );
        fs::remove_dir_all(root).unwrap();
    }
}
