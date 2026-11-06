//! Which database the app runs on, and moving it from SQLite to PostgreSQL.
//!
//! `database.toml` (the boot file) in the config folder selects PostgreSQL after a move. The
//! `INFINIDYSK_DATABASE_URL` environment variable, when set, overrides it.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write as _};
use std::os::unix::fs::{MetadataExt as _, OpenOptionsExt as _, PermissionsExt as _};
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use anyhow::{Context as _, bail};
use axum::extract::State;
use axum::http::{StatusCode, header};
use axum::response::IntoResponse;
use axum::routing::get;
use axum::{Json, Router};
use infinidysk_db::Db;
use infinidysk_db::transfer::{self, PgConnection};
use serde::{Deserialize, Serialize};
use tokio::net::TcpListener;
use tokio::sync::watch;
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;
use tracing::{info, warn};
use utoipa::ToSchema;

use crate::admin::{Admin, ApiError, Fail};
use crate::app::Boot;

pub const BOOT_FILE: &str = "database.toml";
pub const DEFAULT_SQLITE: &str = "infinidysk.sqlite";
pub const PRE_POSTGRES: &str = "infinidysk.sqlite.pre-postgres";

const HEADER: &str =
    "# Written by infinidysk. INFINIDYSK_DATABASE_URL, when set, overrides this file.\n";

/// `database.toml` in the config folder. Holds a password: no `Debug`, never logged.
#[derive(Clone, Default, Serialize, Deserialize)]
pub struct BootFile {
    pub url: Option<String>,
    pub migration: Option<Migration>,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Migration {
    pub target: String,
    pub state: MigrationState,
    /// SQLite's migration version when the copy started; a different one forces a re-copy.
    pub schema_version: i64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum MigrationState {
    Copying,
    Syncing,
    Cutover,
}

impl BootFile {
    /// A missing file is the default; an unreadable one is an error.
    ///
    /// # Errors
    ///
    /// The file exists but cannot be read or parsed.
    pub fn load(dir: &Path) -> anyhow::Result<Self> {
        let path = dir.join(BOOT_FILE);
        match fs::read_to_string(&path) {
            // The parse error's message only; its full display quotes the line, which may
            // hold the password.
            Ok(text) => toml::from_str(&text).map_err(|error| {
                anyhow::anyhow!("{} is invalid: {}", path.display(), error.message())
            }),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(Self::default()),
            Err(error) => Err(error).with_context(|| format!("reading {}", path.display())),
        }
    }

    /// Writes `database.toml.tmp` (mode 0o600), fsyncs it, renames it over `database.toml`,
    /// then fsyncs `dir`. `Ok`: the new file is visible and survives a host crash.
    ///
    /// # Errors
    ///
    /// [`SaveError`] says whether the new file became visible.
    pub fn save(&self, dir: &Path, owner: (u32, u32)) -> Result<(), SaveError> {
        let path = dir.join(BOOT_FILE);
        let tmp = dir.join(format!("{BOOT_FILE}.tmp"));
        let publish = || -> io::Result<()> {
            let text = toml::to_string(self).map_err(io::Error::other)?;
            let mut file = OpenOptions::new()
                .write(true)
                .create(true)
                .truncate(true)
                .mode(0o600)
                .open(&tmp)?;
            file.set_permissions(fs::Permissions::from_mode(0o600))?;
            file.write_all(HEADER.as_bytes())?;
            file.write_all(text.as_bytes())?;
            file.sync_all()?;
            fs::rename(&tmp, &path)
        };
        publish().map_err(|error| SaveError::Unpublished {
            path: path.clone(),
            error,
        })?;
        own(&path, owner);
        sync_dir(dir).map_err(|error| SaveError::NotDurable { path, error })
    }
}

/// Why [`BootFile::save`] failed.
#[derive(Debug, thiserror::Error)]
pub enum SaveError {
    /// The temp write, its fsync or the rename failed: `database.toml` is unchanged.
    #[error("could not write {}: {error}", .path.display())]
    Unpublished { path: PathBuf, error: io::Error },
    /// Renamed, but the directory fsync failed: visible now, possibly lost on a host crash.
    #[error("could not make {} durable: {error}", .path.display())]
    NotDurable { path: PathBuf, error: io::Error },
}

/// Hands a file root created to the configured owner, as log files are.
fn own(path: &Path, (uid, gid): (u32, u32)) {
    let Ok(meta) = fs::symlink_metadata(path) else {
        return;
    };
    if meta.uid() != 0 || (uid == 0 && gid == meta.gid()) {
        return;
    }
    if let Err(error) = std::os::unix::fs::lchown(path, Some(uid), Some(gid)) {
        tracing::warn!(reason = %error, path = %path.display(), "could not change the boot file's owner");
    }
}

#[cfg(test)]
thread_local! {
    static FAIL_DIR_SYNC: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

/// Makes the next `sync_dir` on this thread fail (tests use the current-thread runtime).
#[cfg(test)]
fn fail_next_dir_sync() {
    FAIL_DIR_SYNC.set(true);
}

fn sync_dir(dir: &Path) -> io::Result<()> {
    #[cfg(test)]
    if FAIL_DIR_SYNC.replace(false) {
        return Err(io::Error::other("injected"));
    }
    File::open(dir)?.sync_all()
}

/// Where the database URL came from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Source {
    /// `INFINIDYSK_DATABASE_URL`; the app does not manage the database.
    Env,
    BootFile,
    Default,
}

/// `INFINIDYSK_DATABASE_URL`, else the boot file's URL, else the default SQLite file.
/// Refuses to start empty when a switch left only the renamed SQLite file behind.
///
/// # Errors
///
/// Only the renamed SQLite file is left.
pub fn choose(
    env_url: Option<String>,
    file: &BootFile,
    config: &Path,
) -> anyhow::Result<(String, Source)> {
    if let Some(url) = env_url {
        return Ok((url, Source::Env));
    }
    if let Some(url) = &file.url {
        return Ok((url.clone(), Source::BootFile));
    }
    let sqlite = config.join(DEFAULT_SQLITE);
    if !sqlite.exists() && config.join(PRE_POSTGRES).exists() {
        bail!(
            "This install moved to PostgreSQL, but {} is missing. Restore it, \
             or rename {PRE_POSTGRES} to {DEFAULT_SQLITE} to go back to SQLite.",
            config.join(BOOT_FILE).display()
        );
    }
    Ok((crate::app::sqlite_url(&sqlite), Source::Default))
}

/// Loads the boot file, makes it durable, then picks the database with [`choose`].
/// Runs before any database opens; a failed sync stops startup.
///
/// # Errors
///
/// The boot file is unreadable or cannot be made durable, or [`choose`] fails.
pub fn select(env_url: Option<String>, dir: &Path) -> anyhow::Result<(BootFile, String, Source)> {
    let file = BootFile::load(dir)?;
    let path = dir.join(BOOT_FILE);
    if path.exists() {
        File::open(&path)
            .and_then(|f| f.sync_all())
            .and_then(|()| sync_dir(dir))
            .with_context(|| {
                format!(
                    "could not make {} durable; fix the volume that holds {} and restart",
                    path.display(),
                    dir.display()
                )
            })?;
    }
    let (url, source) = choose(env_url, &file, dir)?;
    Ok((file, url, source))
}

const CHECK_TIMEOUT: Duration = Duration::from_secs(15);
const SYNC_EVERY: Duration = Duration::from_secs(5);
const RETRY_AFTER: Duration = Duration::from_secs(30);
const NOT_POSTGRES: &str = "Enter a postgres:// URL.";
const EMPTY_ONLY: &str = "Use a new, empty database for infinidysk.";
const COLLATION: &str = "Create the database with LC_COLLATE 'C' so folder order matches SQLite \
                         and path searches can use indexes.";
const MASK: &str = "********";
const CAPTURE_LEFT: &str =
    "Change tracking is still on in SQLite. Restart infinidysk to remove it.";
const TARGET_LEFT: &str =
    "Could not empty the PostgreSQL database. Empty it before using it again.";

/// Time limits for transfer work; tests pass shorter ones.
#[derive(Clone, Copy)]
pub struct Limits {
    /// How long the switch, or one unit of online work, may go without a heartbeat.
    pub stall: Duration,
    /// Server-side `lock_timeout` on transfer connections. Below `stall`, so a lock wait
    /// ends on the server instead of outliving our dropped socket.
    pub lock_timeout: Duration,
    /// Bound on the best-effort target cleanup when a migration is cancelled.
    pub cleanup: Duration,
}

pub const LIMITS: Limits = Limits {
    stall: Duration::from_secs(60),
    lock_timeout: Duration::from_secs(30),
    cleanup: Duration::from_secs(15),
};

/// When progress was last reported; the progress callbacks touch it.
#[derive(Clone)]
pub(crate) struct Beat(Arc<Mutex<Instant>>);

impl Beat {
    pub(crate) fn new() -> Self {
        Self(Arc::new(Mutex::new(Instant::now())))
    }

    pub(crate) fn touch(&self) {
        *self.0.lock().unwrap_or_else(PoisonError::into_inner) = Instant::now();
    }

    fn age(&self) -> Duration {
        self.0
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .elapsed()
    }
}

/// Runs `work` until it ends, or until `beat` has not moved for `stall`.
pub(crate) async fn watchdog<T>(
    stall: Duration,
    beat: &Beat,
    work: impl Future<Output = anyhow::Result<T>>,
) -> anyhow::Result<T> {
    beat.touch();
    tokio::pin!(work);
    let mut tick = tokio::time::interval((stall / 4).min(Duration::from_secs(1)));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    loop {
        tokio::select! {
            result = &mut work => return result,
            _ = tick.tick() => if beat.age() >= stall {
                bail!("PostgreSQL stopped responding");
            },
        }
    }
}

/// One sync pass under the watchdog, connecting first when `conn` is `None`. Every progress
/// report is a heartbeat. Leaves `conn` as `None` after an error or stall.
async fn guarded_sync(
    db: &Db,
    conn: &mut Option<PgConnection>,
    target: &str,
    verify: bool,
    limits: Limits,
    report: impl Fn(transfer::Progress) + Sync,
) -> anyhow::Result<u64> {
    let beat = Beat::new();
    let result = watchdog(limits.stall, &beat, async {
        let conn = match conn {
            Some(conn) => conn,
            None => conn.insert(transfer::connect(target, limits.lock_timeout).await?),
        };
        beat.touch();
        let applied = db
            .sync(conn, verify, |progress| {
                beat.touch();
                report(progress);
            })
            .await?;
        Ok(applied)
    })
    .await;
    if result.is_err() {
        *conn = None;
    }
    result
}

fn is_postgres_url(url: &str) -> bool {
    url.starts_with("postgres:") || url.starts_with("postgresql:")
}

/// `user@host:port/database`, never the password.
fn display(url: &str) -> String {
    transfer::describe(url).unwrap_or_else(|_| "PostgreSQL".to_owned())
}

/// Replaces `url` and its password in `text`; sqlx messages can contain the URL.
fn redact(text: &str, url: &str) -> String {
    let mut out = text.replace(url, MASK);
    if let Some(password) = password(url) {
        let decoded = percent_encoding::percent_decode_str(password).decode_utf8_lossy();
        for p in [password, decoded.as_ref()] {
            if !p.is_empty() {
                out = out
                    .replace(&format!(":{p}@"), &format!(":{MASK}@"))
                    .replace(&format!("={p}"), &format!("={MASK}"));
                // ponytail: a bare short password is left alone, replacing it would
                // garble the message; sqlx quotes it only inside the URL.
                if p.len() >= 8 {
                    out = out.replace(p, MASK);
                }
            }
        }
    }
    out
}

fn password(url: &str) -> Option<&str> {
    let rest = url.split_once("://")?.1;
    let authority = rest.split(['/', '?']).next()?;
    let (userinfo, _) = authority.rsplit_once('@')?;
    userinfo.split_once(':').map(|(_, p)| p)
}

/// The whole error chain, redacted.
fn reason(error: &anyhow::Error, url: &str) -> String {
    redact(&format!("{error:#}"), url)
}

fn needs_full_copy(error: &anyhow::Error) -> bool {
    error
        .chain()
        .find_map(|e| e.downcast_ref::<infinidysk_db::Error>())
        .is_some_and(infinidysk_db::Error::needs_full_copy)
}

fn is_empty(check: &transfer::TargetCheck) -> bool {
    check.foreign_tables.is_empty() && check.used.is_empty() && check.can_create
}

/// In-memory migration status; the boot file holds what must survive a restart.
#[derive(Clone, Default)]
struct Status {
    /// State and target display of the boot file's migration.
    migration: Option<(MigrationState, String)>,
    progress: Option<transfer::Progress>,
    pending_changes: Option<u64>,
    synced_at_ms: Option<i64>,
    error: Option<String>,
}

/// Moves a SQLite database to PostgreSQL while the app runs.
pub struct Migrator {
    db: Db,
    source: Source,
    /// The PostgreSQL database in use, for display.
    current: Option<String>,
    /// The SQLite file in use, for display; `None` on PostgreSQL.
    sqlite: Option<String>,
    config: PathBuf,
    owner: (u32, u32),
    limits: Limits,
    /// In-memory copy of the boot file; serializes boot-file writes.
    file: tokio::sync::Mutex<BootFile>,
    status: watch::Sender<Status>,
    run: Mutex<Option<JoinHandle<()>>>,
    /// Held by `start`, `cancel` and `cutover` for their whole run.
    control: tokio::sync::Mutex<()>,
    #[cfg(test)]
    trace: Mutex<Vec<&'static str>>,
    #[cfg(test)]
    live: std::sync::atomic::AtomicUsize,
    #[cfg(test)]
    max_live: std::sync::atomic::AtomicUsize,
}

impl Migrator {
    /// `file` is the in-memory boot file `serve_cutover` returned; it may be newer than disk.
    /// `cutover_error` is set when the boot-time switch fell back to SQLite.
    pub fn new(
        db: Db,
        boot: &Boot,
        config: &Path,
        file: BootFile,
        limits: Limits,
        cutover_error: Option<String>,
    ) -> Arc<Self> {
        // After a boot-time switch `boot` still names SQLite; the boot file names the target.
        let current = db.is_postgres().then(|| {
            display(match (boot.database_source, &file.url) {
                (Source::Env, _) | (_, None) => &boot.database_url,
                (_, Some(url)) => url,
            })
        });
        let sqlite = if db.is_postgres() {
            None
        } else {
            crate::app::sqlite_path(&boot.database_url).map(|path| path.display().to_string())
        };
        let status = watch::channel(Status {
            error: cutover_error,
            ..Status::default()
        })
        .0;
        mirror(&status, &file);
        Arc::new(Self {
            db,
            source: boot.database_source,
            current,
            sqlite,
            config: config.to_path_buf(),
            owner: (boot.uid, boot.gid),
            limits,
            file: tokio::sync::Mutex::new(file),
            status,
            run: Mutex::new(None),
            control: tokio::sync::Mutex::new(()),
            #[cfg(test)]
            trace: Mutex::new(Vec::new()),
            #[cfg(test)]
            live: std::sync::atomic::AtomicUsize::new(0),
            #[cfg(test)]
            max_live: std::sync::atomic::AtomicUsize::new(0),
        })
    }

    /// At boot, after `serve_cutover` reconciled capture. With `Source::Env` and a leftover
    /// migration: warn, `stop_capture` (if SQLite), drop the section. With Copying/Syncing:
    /// start the worker.
    pub async fn resume(self: &Arc<Self>) {
        let mut file = self.file.lock().await;
        let Some(migration) = &file.migration else {
            return;
        };
        if self.source == Source::Env || self.db.is_postgres() {
            warn!(
                path = %self.config.join(BOOT_FILE).display(),
                "dropping a move to PostgreSQL left in the boot file: INFINIDYSK_DATABASE_URL selects the database"
            );
            if !self.db.is_postgres()
                && let Err(error) = self.db.stop_capture().await
            {
                warn!(reason = %error, "could not remove change capture");
            }
            file.migration = None;
            self.save_logged(&file);
            return;
        }
        if migration.state != MigrationState::Cutover {
            drop(file);
            self.spawn_run();
        }
    }

    /// Checks `url`, prepares it and starts copying.
    ///
    /// # Errors
    ///
    /// The database is not managed here, a move exists, or the target is unusable.
    pub async fn start(self: &Arc<Self>, url: String) -> Result<(), Fail> {
        let _control = self.control.lock().await;
        if self.source == Source::Env {
            return Err(Fail::Conflict(
                "The database is set by INFINIDYSK_DATABASE_URL.".to_owned(),
            ));
        }
        if self.db.is_postgres() {
            return Err(Fail::Conflict(
                "infinidysk already runs on PostgreSQL.".to_owned(),
            ));
        }
        if self.file.lock().await.migration.is_some() {
            return Err(Fail::Conflict(
                "A move to PostgreSQL is already running.".to_owned(),
            ));
        }
        if !is_postgres_url(&url) {
            return Err(Fail::Bad(NOT_POSTGRES.to_owned()));
        }
        let lock_timeout = self.limits.lock_timeout;
        let prepared = tokio::time::timeout(CHECK_TIMEOUT, async {
            if !is_empty(&transfer::check_target(&url).await?) {
                return Ok(false);
            }
            let mut conn = transfer::connect(&url, lock_timeout).await?;
            transfer::migrate(&mut conn).await?;
            Ok::<_, infinidysk_db::Error>(true)
        })
        .await;
        match prepared {
            Err(_) => {
                return Err(Fail::Bad(
                    "PostgreSQL did not answer within 15 s.".to_owned(),
                ));
            }
            Ok(Err(error)) => return Err(Fail::Bad(reason(&error.into(), &url))),
            Ok(Ok(false)) => return Err(Fail::Bad(EMPTY_ONLY.to_owned())),
            Ok(Ok(true)) => {}
        }
        self.db.start_capture().await?;
        let schema_version = self.db.schema_version().await?;
        let mut file = self.file.lock().await;
        let mut next = file.clone();
        next.migration = Some(Migration {
            target: url,
            state: MigrationState::Copying,
            schema_version,
        });
        match self.save(&next) {
            Err(unpublished @ SaveError::Unpublished { .. }) => {
                drop(file);
                if let Err(error) = self.db.stop_capture().await {
                    warn!(reason = %error, "could not remove change capture");
                }
                return Err(Fail::Unavailable(unpublished.to_string()));
            }
            Err(error) => warn!(reason = %error, "the boot file may not survive a host crash"),
            Ok(()) => {}
        }
        *file = next;
        self.publish(&file);
        drop(file);
        info!("copying the database to PostgreSQL");
        self.spawn_run();
        Ok(())
    }

    /// Stops the move and leaves SQLite as it was. Returns what could not be cleaned up;
    /// the move is cancelled either way.
    ///
    /// # Errors
    ///
    /// No move runs, the switch was requested, or the boot file could not be saved.
    pub async fn cancel(self: &Arc<Self>) -> Result<Vec<String>, Fail> {
        let _control = self.control.lock().await;
        let Some(migration) = self.file.lock().await.migration.clone() else {
            return Err(Fail::Conflict(
                "No move to PostgreSQL is running.".to_owned(),
            ));
        };
        if migration.state == MigrationState::Cutover {
            return Err(Fail::Conflict(
                "The switch to PostgreSQL already started.".to_owned(),
            ));
        }
        self.stop_run().await;
        let mut file = self.file.lock().await;
        let mut next = file.clone();
        next.migration = None;
        if let Err(error) = self.save(&next) {
            drop(file);
            self.spawn_run();
            return Err(Fail::Unavailable(error.to_string()));
        }
        *file = next;
        self.publish(&file);
        drop(file);
        let mut warnings = Vec::new();
        // Capture goes after the save: a removed capture under a kept Syncing state would
        // miss changes. A restart removes a leftover capture.
        if let Err(error) = self.db.stop_capture().await {
            warn!(reason = %error, "could not remove change capture");
            warnings.push(CAPTURE_LEFT.to_owned());
        }
        #[cfg(test)]
        self.trace("cleanup");
        let lock_timeout = self.limits.lock_timeout;
        let cleaned = tokio::time::timeout(self.limits.cleanup, async {
            let mut conn = transfer::connect(&migration.target, lock_timeout).await?;
            transfer::truncate_all(&mut conn).await
        })
        .await;
        if !matches!(cleaned, Ok(Ok(()))) {
            warn!("could not empty the PostgreSQL database; empty it before using it again");
            warnings.push(TARGET_LEFT.to_owned());
        }
        info!("cancelled the move to PostgreSQL");
        Ok(warnings)
    }

    /// Stops the worker and writes state Cutover. The caller then restarts.
    ///
    /// # Errors
    ///
    /// The copy is not in sync, or the boot file could not be written.
    pub async fn cutover(self: &Arc<Self>) -> Result<(), Fail> {
        let _control = self.control.lock().await;
        let state = self.file.lock().await.migration.as_ref().map(|m| m.state);
        if state != Some(MigrationState::Syncing) || self.status.borrow().error.is_some() {
            return Err(Fail::Conflict("Wait until the copy is in sync.".to_owned()));
        }
        self.stop_run().await;
        let mut file = self.file.lock().await;
        let mut next = file.clone();
        if let Some(migration) = &mut next.migration {
            migration.state = MigrationState::Cutover;
        }
        match self.save(&next) {
            Err(unpublished @ SaveError::Unpublished { .. }) => {
                drop(file);
                self.spawn_run();
                return Err(Fail::Unavailable(unpublished.to_string()));
            }
            Err(error) => warn!(reason = %error, "the boot file may not survive a host crash"),
            Ok(()) => {}
        }
        *file = next;
        self.publish(&file);
        drop(file);
        info!("switching to PostgreSQL at the restart");
        Ok(())
    }

    pub fn view(&self) -> DatabaseView {
        let status = self.status.borrow();
        let migration = status.migration.as_ref().map(|(state, _)| MigrationView {
            state: *state,
            stage: status.progress.map(|p| p.stage.into()),
            table: status.progress.map(|p| p.table.to_owned()),
            done: status.progress.map_or(0, |p| p.done),
            total: status.progress.map_or(0, |p| p.total),
            pending_changes: status.pending_changes,
            synced_at_ms: status.synced_at_ms,
            error: status.error.clone(),
        });
        DatabaseView {
            backend: if self.db.is_postgres() {
                Backend::Postgres
            } else {
                Backend::Sqlite
            },
            env_managed: self.source == Source::Env,
            sqlite_path: self.sqlite.clone(),
            target: self
                .current
                .clone()
                .or_else(|| status.migration.as_ref().map(|(_, target)| target.clone())),
            migration,
        }
    }

    /// `sqlite` or `postgres`, for support packs.
    pub const fn backend(&self) -> &'static str {
        if self.db.is_postgres() {
            "postgres"
        } else {
            "sqlite"
        }
    }

    /// Shutdown: stops the worker and waits for it.
    pub async fn stop(&self) {
        self.stop_run().await;
    }

    /// Stops the worker and waits until its future, and with it `conn`, is dropped.
    async fn stop_run(&self) {
        let handle = self
            .run
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        if let Some(handle) = handle {
            handle.abort();
            if let Err(error) = handle.await
                && error.is_panic()
            {
                warn!(reason = %error, "the PostgreSQL migration worker panicked");
            }
        }
    }

    /// Starts a worker. Callers run `stop_run` first; a stored handle here is a bug.
    fn spawn_run(self: &Arc<Self>) {
        #[cfg(test)]
        self.trace("worker started");
        let this = Arc::clone(self);
        let handle = tokio::spawn(async move { this.run().await });
        if let Some(old) = self
            .run
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .replace(handle)
        {
            old.abort();
        }
    }

    async fn run(self: Arc<Self>) {
        #[cfg(test)]
        let _guard = WorkerGuard::new(Arc::clone(&self));
        let mut conn = None;
        loop {
            let Some(migration) = self.file.lock().await.migration.clone() else {
                return;
            };
            let result = match migration.state {
                MigrationState::Copying => self.copy(&mut conn, &migration.target).await,
                MigrationState::Syncing => self.sync_pass(&mut conn, &migration.target).await,
                MigrationState::Cutover => return,
            };
            match result {
                Ok(()) => {
                    self.status.send_modify(|s| s.error = None);
                    if migration.state == MigrationState::Syncing {
                        tokio::time::sleep(SYNC_EVERY).await;
                    }
                }
                Err(error) => {
                    conn = None;
                    let why = reason(&error, &migration.target);
                    if migration.state == MigrationState::Syncing && needs_full_copy(&error) {
                        warn!(reason = %why, "the PostgreSQL copy no longer matches SQLite; copying again");
                        let version = self.db.schema_version().await.ok();
                        self.update(|m| {
                            m.state = MigrationState::Copying;
                            if let Some(version) = version {
                                m.schema_version = version;
                            }
                        })
                        .await;
                        continue;
                    }
                    let repeated = self.status.borrow().error.as_deref() == Some(why.as_str());
                    if !repeated {
                        warn!(reason = %why, "moving to PostgreSQL failed; retrying in 30 s");
                    }
                    self.status.send_modify(|s| {
                        s.error = Some(why);
                        s.progress = None;
                    });
                    tokio::time::sleep(RETRY_AFTER).await;
                }
            }
        }
    }

    /// Captures changes, copies everything, then applies what changed during the copy.
    async fn copy(&self, conn: &mut Option<PgConnection>, target: &str) -> anyhow::Result<()> {
        self.db.start_capture().await?;
        let beat = Beat::new();
        let report = |progress| {
            beat.touch();
            self.progress(progress);
        };
        let lock_timeout = self.limits.lock_timeout;
        let copied = watchdog(self.limits.stall, &beat, async {
            let conn = match conn {
                Some(conn) => conn,
                None => conn.insert(transfer::connect(target, lock_timeout).await?),
            };
            beat.touch();
            transfer::migrate(conn).await?;
            beat.touch();
            self.db.copy_all(conn, &report).await?;
            self.db.sync(conn, true, &report).await?;
            Ok(())
        })
        .await;
        if copied.is_err() {
            *conn = None;
        }
        copied?;
        let version = self.db.schema_version().await?;
        self.update(|m| {
            m.state = MigrationState::Syncing;
            m.schema_version = version;
        })
        .await;
        self.status.send_modify(|s| {
            s.progress = None;
            s.pending_changes = Some(0);
            s.synced_at_ms = Some(infinidysk_db::now_ms());
        });
        info!("copied the database to PostgreSQL; keeping it in sync");
        Ok(())
    }

    async fn sync_pass(&self, conn: &mut Option<PgConnection>, target: &str) -> anyhow::Result<()> {
        guarded_sync(&self.db, conn, target, false, self.limits, |progress| {
            self.progress(progress);
        })
        .await?;
        let pending = self.db.pending_changes().await?;
        self.status.send_modify(|s| {
            s.progress = None;
            s.pending_changes = Some(pending);
            s.synced_at_ms = Some(infinidysk_db::now_ms());
        });
        Ok(())
    }

    fn progress(&self, progress: transfer::Progress) {
        self.status.send_modify(|s| s.progress = Some(progress));
    }

    /// Changes the migration in memory and on disk; a failed save only warns.
    async fn update(&self, change: impl FnOnce(&mut Migration)) {
        let mut file = self.file.lock().await;
        if let Some(migration) = &mut file.migration {
            change(migration);
        }
        self.publish(&file);
        self.save_logged(&file);
        drop(file);
    }

    fn save(&self, file: &BootFile) -> Result<(), SaveError> {
        file.save(&self.config, self.owner)?;
        #[cfg(test)]
        self.trace("saved");
        Ok(())
    }

    fn save_logged(&self, file: &BootFile) {
        if let Err(error) = self.save(file) {
            warn!(reason = %error, "could not save the move to PostgreSQL; continuing");
        }
    }

    fn publish(&self, file: &BootFile) {
        mirror(&self.status, file);
    }

    #[cfg(test)]
    fn trace(&self, event: &'static str) {
        self.trace
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .push(event);
    }
}

/// Mirrors the boot file's migration into the status.
fn mirror(status: &watch::Sender<Status>, file: &BootFile) {
    let migration = file
        .migration
        .as_ref()
        .map(|m| (m.state, display(&m.target)));
    status.send_modify(|s| {
        if migration.is_none() && s.migration.is_some() {
            *s = Status::default();
        }
        s.migration = migration;
    });
}

/// Records the worker's end, and how many ran at once.
#[cfg(test)]
struct WorkerGuard(Arc<Migrator>);

#[cfg(test)]
impl WorkerGuard {
    fn new(migrator: Arc<Migrator>) -> Self {
        use std::sync::atomic::Ordering;
        let live = migrator.live.fetch_add(1, Ordering::SeqCst) + 1;
        migrator.max_live.fetch_max(live, Ordering::SeqCst);
        Self(migrator)
    }
}

#[cfg(test)]
impl Drop for WorkerGuard {
    fn drop(&mut self) {
        self.0
            .live
            .fetch_sub(1, std::sync::atomic::Ordering::SeqCst);
        self.0.trace("worker stopped");
    }
}

/// Makes change capture match the boot file. Capture that may have missed a write forces a
/// full copy. Returns why a requested switch became a copy.
async fn reconcile(
    db: &Db,
    file: &mut BootFile,
    dir: &Path,
    owner: (u32, u32),
) -> anyhow::Result<Option<String>> {
    if db.is_postgres() {
        return Ok(None);
    }
    let capture = db.capture().await?;
    let Some(migration) = &mut file.migration else {
        if capture != transfer::Capture::Off {
            db.stop_capture().await?;
            warn!("removed leftover change capture");
        }
        return Ok(None);
    };
    let version = db.schema_version().await?;
    if capture == transfer::Capture::Intact && version == migration.schema_version {
        return Ok(None);
    }
    let why = if capture == transfer::Capture::Intact {
        "the SQLite schema changed since the copy"
    } else {
        "change capture was interrupted"
    };
    db.start_capture().await?;
    let was_cutover = migration.state == MigrationState::Cutover;
    migration.state = MigrationState::Copying;
    migration.schema_version = version;
    warn!(reason = why, "copying the database to PostgreSQL again");
    if let Err(error) = file.save(dir, owner) {
        warn!(reason = %error, "could not save the move to PostgreSQL; continuing");
    }
    Ok(was_cutover.then(|| "change capture was interrupted; copying again".to_owned()))
}

/// Renames `infinidysk.sqlite` and its `-wal` / `-shm` files to `…pre-postgres`. Only warns.
fn retire(dir: &Path) {
    for suffix in ["", "-wal", "-shm"] {
        let from = dir.join(format!("{DEFAULT_SQLITE}{suffix}"));
        if !from.exists() {
            continue;
        }
        let to = dir.join(format!("{PRE_POSTGRES}{suffix}"));
        if to.exists() {
            warn!(path = %to.display(), "not renaming the SQLite file: its renamed copy already exists");
            continue;
        }
        if let Err(error) = fs::rename(&from, &to) {
            warn!(reason = %error, path = %from.display(), "could not rename the SQLite file");
        }
    }
}

/// What boot runs on after [`serve_cutover`].
pub struct Booted {
    pub listener: TcpListener,
    pub db: Db,
    /// In-memory boot file; newer than disk when a save failed.
    pub file: BootFile,
    /// Why a requested switch did not happen (the app then runs on SQLite).
    pub error: Option<String>,
}

fn cutover_router(progress: Arc<watch::Sender<CutoverProgress>>) -> Router {
    Router::new()
        .route(
            "/api/v1/database/cutover",
            get(
                |State(progress): State<Arc<watch::Sender<CutoverProgress>>>| async move {
                    (
                        [(header::CACHE_CONTROL, "no-store")],
                        Json(CutoverStatus {
                            state: CutoverState::Running,
                            started_ms: crate::logs::started_ms(),
                            progress: Some(progress.borrow().clone()),
                        }),
                    )
                },
            ),
        )
        .route(
            crate::healthcheck::PATH,
            get(|| async {
                use crate::healthcheck::{Check, Report};
                Report::new([("database", Check::Pending)])
            }),
        )
        .fallback(|| async {
            (
                StatusCode::SERVICE_UNAVAILABLE,
                [(header::RETRY_AFTER, "60")],
                Json(serde_json::json!({
                    "error": "Switching to PostgreSQL; open the web UI for progress."
                })),
            )
        })
        .with_state(progress)
        .layer(axum::middleware::from_fn(crate::ui::serve))
}

/// Reconciles capture; when the boot file asks for the switch, serves a progress screen
/// while it applies the last changes, verifies and switches.
///
/// # Errors
///
/// The listener failed, SQLite failed, or the switch was published but could not be made
/// durable. Every other failure falls back to SQLite.
pub async fn serve_cutover(
    listener: TcpListener,
    db: Db,
    boot: &Boot,
    dir: &Path,
    limits: Limits,
    cancel: &CancellationToken,
) -> anyhow::Result<Booted> {
    let owner = (boot.uid, boot.gid);
    let mut file = BootFile::load(dir)?;
    let booted = |listener, db, file, error| Booted {
        listener,
        db,
        file,
        error,
    };
    if boot.database_source == Source::Env {
        return Ok(booted(listener, db, file, None));
    }
    if db.is_postgres() {
        retire(dir);
        return Ok(booted(listener, db, file, None));
    }
    if let Some(error) = reconcile(&db, &mut file, dir, owner).await? {
        return Ok(booted(listener, db, file, Some(error)));
    }
    let Some(target) = file
        .migration
        .as_ref()
        .filter(|m| m.state == MigrationState::Cutover)
        .map(|m| m.target.clone())
    else {
        return Ok(booted(listener, db, file, None));
    };
    info!("switching to PostgreSQL");
    let listener = listener.into_std()?;
    let next = listener.try_clone()?;
    let progress = Arc::new(
        watch::channel(CutoverProgress {
            stage: ProgressStage::Syncing,
            table: None,
            done: 0,
            total: 0,
        })
        .0,
    );
    let stop = CancellationToken::new();
    let serving = axum::serve(
        TcpListener::from_std(listener)?,
        cutover_router(Arc::clone(&progress)),
    )
    .with_graceful_shutdown(stop.clone().cancelled_owned())
    .into_future();
    tokio::pin!(serving);
    let beat = Beat::new();
    let switching = watchdog(limits.stall, &beat, async {
        let report = |p: transfer::Progress| {
            beat.touch();
            progress.send_replace(CutoverProgress {
                stage: p.stage.into(),
                table: Some(p.table.to_owned()),
                done: p.done,
                total: p.total,
            });
        };
        let mut conn = transfer::connect(&target, limits.lock_timeout).await?;
        beat.touch();
        transfer::migrate(&mut conn).await?;
        beat.touch();
        db.sync(&mut conn, true, &report).await?;
        drop(conn);
        progress.send_replace(CutoverProgress {
            stage: ProgressStage::Switching,
            table: None,
            done: 0,
            total: 0,
        });
        beat.touch();
        // ponytail: a stall inside this call can leave one pooled connection's
        // return-to-pool ping hanging in the background; once per boot, on a pool never used.
        Ok(Db::connect(&target).await?)
    });
    let outcome = tokio::select! {
        () = cancel.cancelled() => None,
        result = switching => Some(result),
        result = &mut serving => {
            result?;
            bail!("the switch HTTP server stopped unexpectedly");
        }
    };
    stop.cancel();
    serving.await?;
    let listener = TcpListener::from_std(next)?;
    let Some(outcome) = outcome else {
        return Ok(booted(listener, db, file, None));
    };
    let error = match outcome {
        Ok(target_db) => {
            let mut switched = file.clone();
            switched.url = Some(target.clone());
            switched.migration = None;
            match switched.save(dir, owner) {
                Ok(()) => {
                    if let Err(error) = db.stop_capture().await {
                        warn!(reason = %error, "could not remove change capture from SQLite");
                    }
                    if let Err(error) = db.checkpoint().await {
                        warn!(reason = %error, "could not checkpoint SQLite");
                    }
                    db.close().await;
                    retire(dir);
                    info!("switched to PostgreSQL");
                    return Ok(booted(listener, target_db, switched, None));
                }
                Err(error @ SaveError::NotDurable { .. }) => {
                    tracing::error!(
                        reason = %error,
                        "could not make the boot file durable; stopping before either database takes writes"
                    );
                    return Err(error.into());
                }
                Err(error) => anyhow::Error::from(error),
            }
        }
        Err(error) => error,
    };
    let why = reason(&error, &target);
    warn!(reason = %why, "switching to PostgreSQL failed; staying on SQLite");
    if let Some(migration) = &mut file.migration {
        migration.state = if needs_full_copy(&error) {
            MigrationState::Copying
        } else {
            MigrationState::Syncing
        };
    }
    if let Err(error) = file.save(dir, owner) {
        warn!(reason = %error, "could not save the move to PostgreSQL; the next start tries the switch again");
    }
    Ok(booted(listener, db, file, Some(why)))
}

#[derive(Clone, Copy, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum Backend {
    Sqlite,
    Postgres,
}

#[derive(Clone, Copy, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum ProgressStage {
    Copying,
    Syncing,
    Verifying,
    Switching,
}

impl From<transfer::Stage> for ProgressStage {
    fn from(stage: transfer::Stage) -> Self {
        match stage {
            transfer::Stage::Copying => Self::Copying,
            transfer::Stage::Syncing => Self::Syncing,
            transfer::Stage::Verifying => Self::Verifying,
        }
    }
}

#[derive(Serialize, ToSchema)]
pub struct DatabaseView {
    backend: Backend,
    /// `INFINIDYSK_DATABASE_URL` selects the database; it cannot be moved here.
    env_managed: bool,
    /// The SQLite file in use; absent on PostgreSQL.
    sqlite_path: Option<String>,
    /// `user@host:port/database` of the PostgreSQL database in use or being copied to.
    target: Option<String>,
    migration: Option<MigrationView>,
}

#[derive(Serialize, ToSchema)]
pub struct MigrationView {
    state: MigrationState,
    stage: Option<ProgressStage>,
    table: Option<String>,
    done: u64,
    /// 0 while unknown.
    total: u64,
    /// Changes not yet on PostgreSQL after the last sync pass.
    pending_changes: Option<u64>,
    synced_at_ms: Option<i64>,
    error: Option<String>,
}

#[derive(Deserialize, ToSchema)]
pub struct DatabaseUrl {
    /// `postgres://user:password@host:5432/database`.
    url: String,
}

#[derive(Serialize, ToSchema)]
pub struct DatabaseCheck {
    /// The server version, when it answered.
    version: Option<String>,
    /// Why this database cannot be used; absent when it can.
    error: Option<String>,
    warnings: Vec<String>,
}

#[derive(Serialize, ToSchema)]
pub struct MigrationCancelled {
    /// Cleanup that did not finish; the move is cancelled either way.
    warnings: Vec<String>,
}

#[derive(Serialize, ToSchema)]
pub struct CutoverStarted {
    /// This process's start, to tell it from the one that switches.
    started_ms: i64,
}

#[derive(Clone, Copy, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum CutoverState {
    None,
    Running,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct CutoverStatus {
    state: CutoverState,
    /// The answering process's start.
    started_ms: i64,
    progress: Option<CutoverProgress>,
}

#[derive(Clone, Serialize, ToSchema)]
pub struct CutoverProgress {
    stage: ProgressStage,
    table: Option<String>,
    done: u64,
    total: u64,
}

/// Which database runs, and any move to PostgreSQL.
#[utoipa::path(get, path = "/api/v1/database", tag = "database",
    responses((status = 200, body = DatabaseView)))]
pub async fn get_database(State(admin): State<Arc<Admin>>) -> Json<DatabaseView> {
    Json(admin.database.view())
}

/// Connects to a PostgreSQL database and says whether infinidysk can move to it. Read-only.
#[utoipa::path(post, path = "/api/v1/database/check", tag = "database", request_body = DatabaseUrl,
    responses((status = 200, body = DatabaseCheck), (status = 400, body = ApiError)))]
pub async fn post_database_check(
    Json(body): Json<DatabaseUrl>,
) -> Result<Json<DatabaseCheck>, Fail> {
    if !is_postgres_url(&body.url) {
        return Err(Fail::Bad(NOT_POSTGRES.to_owned()));
    }
    let result = tokio::time::timeout(CHECK_TIMEOUT, transfer::check_target(&body.url)).await;
    Ok(Json(match result {
        Err(_) => DatabaseCheck {
            version: None,
            error: Some("PostgreSQL did not answer within 15 s.".to_owned()),
            warnings: Vec::new(),
        },
        Ok(Err(error)) => DatabaseCheck {
            version: None,
            error: Some(reason(&error.into(), &body.url)),
            warnings: Vec::new(),
        },
        Ok(Ok(check)) => DatabaseCheck {
            error: (!is_empty(&check)).then(|| EMPTY_ONLY.to_owned()),
            warnings: if matches!(check.collation.as_str(), "C" | "POSIX") {
                Vec::new()
            } else {
                vec![COLLATION.to_owned()]
            },
            version: Some(check.version),
        },
    }))
}

/// Starts copying to an empty PostgreSQL database and keeps it in sync.
#[utoipa::path(post, path = "/api/v1/database/migration", tag = "database", request_body = DatabaseUrl,
    responses((status = 202, description = "Copying"), (status = 400, body = ApiError),
        (status = 409, body = ApiError)))]
pub async fn post_migration(
    State(admin): State<Arc<Admin>>,
    Json(body): Json<DatabaseUrl>,
) -> Result<StatusCode, Fail> {
    admin.database.start(body.url).await?;
    Ok(StatusCode::ACCEPTED)
}

/// Cancels the move and tries to empty the PostgreSQL database again.
#[utoipa::path(delete, path = "/api/v1/database/migration", tag = "database",
    responses((status = 200, body = MigrationCancelled), (status = 409, body = ApiError),
        (status = 503, body = ApiError)))]
pub async fn delete_migration(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<MigrationCancelled>, Fail> {
    let warnings = admin.database.cancel().await?;
    Ok(Json(MigrationCancelled { warnings }))
}

/// Restarts on PostgreSQL. Playback, `WebDAV` and imports pause until the switch finishes.
#[utoipa::path(post, path = "/api/v1/database/cutover", tag = "database",
    responses((status = 202, body = CutoverStarted), (status = 409, body = ApiError),
        (status = 503, body = ApiError)))]
pub async fn post_cutover(
    State(admin): State<Arc<Admin>>,
) -> Result<(StatusCode, Json<CutoverStarted>), Fail> {
    admin.database.cutover().await?;
    info!("restarting to switch to PostgreSQL");
    admin.restart.store(true, Ordering::Relaxed);
    admin.cancel.cancel();
    Ok((
        StatusCode::ACCEPTED,
        Json(CutoverStarted {
            started_ms: crate::logs::started_ms(),
        }),
    ))
}

/// Whether a switch to PostgreSQL runs. No sign-in needed.
#[utoipa::path(get, path = "/api/v1/database/cutover", tag = "database", security(()),
    responses((status = 200, body = CutoverStatus)))]
pub async fn get_cutover_none() -> impl IntoResponse {
    (
        [(header::CACHE_CONTROL, "no-store")],
        Json(CutoverStatus {
            state: CutoverState::None,
            started_ms: crate::logs::started_ms(),
            progress: None,
        }),
    )
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;

    struct Dir(PathBuf);

    impl Dir {
        fn new() -> Self {
            let dir = std::env::temp_dir().join(format!("infinidysk-db-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&dir).expect("temp dir");
            Self(dir)
        }
    }

    impl Drop for Dir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn owner() -> (u32, u32) {
        let meta = fs::metadata(std::env::temp_dir()).expect("temp dir");
        (meta.uid(), meta.gid())
    }

    fn cutover() -> BootFile {
        BootFile {
            url: None,
            migration: Some(Migration {
                target: "postgres://u:p@127.0.0.1:1/x".to_owned(),
                state: MigrationState::Cutover,
                schema_version: 7,
            }),
        }
    }

    fn switched() -> BootFile {
        BootFile {
            url: Some("postgres://u:p@127.0.0.1:1/x".to_owned()),
            migration: None,
        }
    }

    #[test]
    fn boot_file_round_trips_private() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        let loaded = BootFile::load(&dir.0).expect("load");
        let migration = loaded.migration.expect("migration");
        assert_eq!(migration.target, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(migration.state, MigrationState::Cutover);
        assert_eq!(migration.schema_version, 7);
        assert_eq!(loaded.url, None);
        let mode = fs::metadata(dir.0.join(BOOT_FILE)).expect("meta").mode();
        assert_eq!(mode & 0o777, 0o600);
        let text = fs::read_to_string(dir.0.join(BOOT_FILE)).expect("read");
        assert!(text.starts_with(HEADER));
    }

    #[test]
    fn missing_boot_file_is_default() {
        let dir = Dir::new();
        let file = BootFile::load(&dir.0).expect("load");
        assert!(file.url.is_none() && file.migration.is_none());
    }

    #[test]
    fn save_fails_before_rename_leaves_file() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        fs::create_dir(dir.0.join(format!("{BOOT_FILE}.tmp"))).expect("block tmp");
        assert!(matches!(
            switched().save(&dir.0, owner()),
            Err(SaveError::Unpublished { .. })
        ));
        let loaded = BootFile::load(&dir.0).expect("load");
        assert_eq!(
            loaded.migration.map(|m| m.state),
            Some(MigrationState::Cutover)
        );
    }

    #[test]
    fn save_without_dir_sync_is_not_durable() {
        let dir = Dir::new();
        cutover().save(&dir.0, owner()).expect("save");
        fail_next_dir_sync();
        assert!(matches!(
            switched().save(&dir.0, owner()),
            Err(SaveError::NotDurable { .. })
        ));
        let loaded = BootFile::load(&dir.0).expect("load");
        assert!(loaded.migration.is_none());
        assert_eq!(loaded.url.as_deref(), Some("postgres://u:p@127.0.0.1:1/x"));
    }

    #[test]
    fn env_url_wins_over_boot_file() {
        let dir = Dir::new();
        let (url, source) =
            choose(Some("sqlite:/x.sqlite".to_owned()), &switched(), &dir.0).expect("choose");
        assert_eq!(url, "sqlite:/x.sqlite");
        assert_eq!(source, Source::Env);
        let (url, source) = choose(None, &switched(), &dir.0).expect("choose");
        assert_eq!(url, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(source, Source::BootFile);
        let (url, source) = choose(None, &BootFile::default(), &dir.0).expect("choose");
        assert_eq!(
            url,
            format!("sqlite:{}", dir.0.join(DEFAULT_SQLITE).display())
        );
        assert_eq!(source, Source::Default);
    }

    /// `%` and `?` in the config folder name open the file in that folder, and the path read
    /// back from the URL (restore, the database page) is the same file.
    #[tokio::test]
    async fn the_default_database_opens_in_a_folder_named_with_url_characters() {
        let dir = Dir::new();
        for name in ["percent%41", "question?dir"] {
            let home = dir.0.join(name);
            fs::create_dir_all(&home).expect("create");
            let (url, _) = choose(None, &BootFile::default(), &home).expect("choose");
            let file = home.join(DEFAULT_SQLITE);
            assert_eq!(crate::app::sqlite_path(&url), Some(file.clone()), "{url}");
            Db::connect(&url).await.expect(name).close().await;
            assert!(file.is_file(), "{url}");
        }
        assert!(!dir.0.join("percentA").exists());

        // A leading `//` stays absolute instead of reading as a URL authority.
        let home = PathBuf::from(format!("/{}", dir.0.join("slashes").display()));
        fs::create_dir_all(&home).expect("create");
        let (url, _) = choose(None, &BootFile::default(), &home).expect("choose");
        let file = home.join(DEFAULT_SQLITE);
        assert_eq!(crate::app::sqlite_path(&url), Some(file.clone()), "{url}");
        Db::connect(&url).await.expect("slashes").close().await;
        assert!(file.is_file(), "{url}");
    }

    #[test]
    fn missing_boot_file_after_switch_refuses_to_start() {
        let dir = Dir::new();
        fs::write(dir.0.join(PRE_POSTGRES), b"").expect("write");
        let error = choose(None, &BootFile::default(), &dir.0).expect_err("refuses");
        assert!(error.to_string().contains(BOOT_FILE));
    }

    #[test]
    fn select_stops_when_the_boot_file_cannot_be_made_durable() {
        let dir = Dir::new();
        switched().save(&dir.0, owner()).expect("save");
        fail_next_dir_sync();
        assert!(select(None, &dir.0).is_err());
        let (_, url, source) = select(None, &dir.0).expect("select");
        assert_eq!(url, "postgres://u:p@127.0.0.1:1/x");
        assert_eq!(source, Source::BootFile);
    }

    const UNREACHABLE: &str = "postgres://u:p@127.0.0.1:1/x";

    fn sqlite_url(dir: &Path) -> String {
        format!("sqlite:{}", dir.join(DEFAULT_SQLITE).display())
    }

    fn boot(dir: &Path) -> Boot {
        let (uid, gid) = owner();
        Boot {
            listen: "127.0.0.1:0".parse().expect("addr"),
            database_url: sqlite_url(dir),
            database_source: Source::Default,
            uid,
            gid,
            trace_minutes: 0,
            metrics_path: dir.join("metrics.sqlite"),
            log_dir: dir.join("logs"),
            home: dir.to_path_buf(),
            config_dir: dir.join("csharp"),
            ffprobe: "ffprobe".to_owned(),
        }
    }

    async fn sqlite(dir: &Path) -> Db {
        Db::connect(&sqlite_url(dir)).await.expect("open sqlite")
    }

    fn short() -> Limits {
        Limits {
            stall: Duration::from_secs(3),
            lock_timeout: Duration::from_secs(1),
            cleanup: Duration::from_secs(1),
        }
    }

    fn in_state(target: &str, state: MigrationState) -> BootFile {
        BootFile {
            url: None,
            migration: Some(Migration {
                target: target.to_owned(),
                state,
                schema_version: 0,
            }),
        }
    }

    /// A Migrator over a fresh SQLite database whose boot file holds `file`.
    async fn setup(dir: &Path, file: BootFile, limits: Limits) -> (Db, Arc<Migrator>) {
        let db = sqlite(dir).await;
        file.save(dir, owner()).expect("save");
        let migrator = Migrator::new(db.clone(), &boot(dir), dir, file, limits, None);
        (db, migrator)
    }

    async fn worker_running(migrator: &Migrator) {
        while migrator.live.load(Ordering::SeqCst) == 0 {
            tokio::task::yield_now().await;
        }
    }

    fn traced(migrator: &Migrator) -> Vec<&'static str> {
        migrator
            .trace
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    fn position(trace: &[&str], event: &str) -> usize {
        trace
            .iter()
            .position(|e| *e == event)
            .unwrap_or_else(|| panic!("{event} in {trace:?}"))
    }

    fn migration_state(migrator: &Migrator) -> Option<MigrationState> {
        migrator.view().migration.map(|m| m.state)
    }

    #[tokio::test]
    async fn stopping_waits_for_the_worker() {
        let dir = Dir::new();
        let (db, migrator) = setup(
            &dir.0,
            in_state(UNREACHABLE, MigrationState::Syncing),
            short(),
        )
        .await;
        db.start_capture().await.expect("capture");
        migrator.resume().await;
        worker_running(&migrator).await;
        assert!(migrator.cancel().await.is_ok());
        let trace = traced(&migrator);
        assert!(position(&trace, "worker stopped") < position(&trace, "saved"));

        // A server that never answers keeps the worker busy, so the switch is allowed.
        let listener = std::net::TcpListener::bind("127.0.0.1:0").expect("bind");
        let silent = format!(
            "postgres://u:p@{}/x?sslmode=disable",
            listener.local_addr().expect("addr")
        );
        let dir = Dir::new();
        let limits = Limits {
            stall: Duration::from_secs(60),
            ..short()
        };
        let (_db, migrator) =
            setup(&dir.0, in_state(&silent, MigrationState::Syncing), limits).await;
        migrator.resume().await;
        worker_running(&migrator).await;
        fs::create_dir(dir.0.join(format!("{BOOT_FILE}.tmp"))).expect("block tmp");
        assert!(matches!(
            migrator.cutover().await,
            Err(Fail::Unavailable(_))
        ));
        let trace = traced(&migrator);
        let stopped = position(&trace, "worker stopped");
        let restarted = trace
            .iter()
            .rposition(|e| *e == "worker started")
            .expect("restarted");
        assert!(stopped < restarted, "{trace:?}");
        assert_eq!(migrator.max_live.load(Ordering::SeqCst), 1);
        migrator.stop().await;
    }

    #[tokio::test]
    async fn silent_server_reaches_retry() {
        use tokio::io::AsyncReadExt as _;

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind");
        let target = format!(
            "postgres://u:p@{}/x?sslmode=disable",
            listener.local_addr().expect("addr")
        );
        let dir = Dir::new();
        let limits = Limits {
            stall: Duration::from_secs(1),
            ..short()
        };
        let (_db, migrator) =
            setup(&dir.0, in_state(&target, MigrationState::Syncing), limits).await;
        migrator.resume().await;
        let (mut stream, _) = listener.accept().await.expect("accept");
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let error = migrator.view().migration.and_then(|m| m.error);
            if error.is_some_and(|e| e.contains("stopped responding")) {
                break;
            }
            assert!(Instant::now() < deadline, "no stall reported");
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        let mut buf = [0_u8; 256];
        let closed = tokio::time::timeout(Duration::from_secs(2), async {
            loop {
                if stream.read(&mut buf).await.expect("read") == 0 {
                    break;
                }
            }
        })
        .await;
        assert!(closed.is_ok(), "our socket stayed open");
        migrator.stop().await;
    }

    #[tokio::test]
    async fn cancel_needs_a_durable_removal() {
        let dir = Dir::new();
        let (db, migrator) = setup(
            &dir.0,
            in_state(UNREACHABLE, MigrationState::Syncing),
            short(),
        )
        .await;
        db.start_capture().await.expect("capture");
        migrator.resume().await;
        worker_running(&migrator).await;
        fail_next_dir_sync();
        assert!(matches!(migrator.cancel().await, Err(Fail::Unavailable(_))));
        let trace = traced(&migrator);
        assert!(!trace.contains(&"cleanup"), "{trace:?}");
        let restarted = trace
            .iter()
            .rposition(|e| *e == "worker started")
            .expect("restarted");
        assert!(position(&trace, "worker stopped") < restarted, "{trace:?}");
        assert_eq!(
            db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        assert_eq!(migration_state(&migrator), Some(MigrationState::Syncing));
        migrator.stop().await;
    }

    /// A throwaway PostgreSQL database, when `INFINIDYSK_TEST_POSTGRES_URL` is set.
    struct Pg {
        admin: sqlx::PgPool,
        name: String,
        url: String,
    }

    impl Pg {
        async fn create() -> Option<Self> {
            let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
                eprintln!("skipped: INFINIDYSK_TEST_POSTGRES_URL is not set");
                return None;
            };
            let name = format!("infinidysk_test_{}", uuid::Uuid::new_v4().simple());
            let admin = sqlx::PgPool::connect(&admin_url)
                .await
                .expect("connect admin");
            sqlx::query(sqlx::AssertSqlSafe(format!("CREATE DATABASE {name}")))
                .execute(&admin)
                .await
                .expect("create");
            let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
            let url = format!("{base}/{name}");
            Some(Self { admin, name, url })
        }

        async fn conn(&self) -> PgConnection {
            transfer::connect(&self.url, Duration::from_secs(5))
                .await
                .expect("connect")
        }

        /// A plain connection, not counted as a transfer backend.
        async fn plain(&self) -> PgConnection {
            <PgConnection as sqlx::Connection>::connect(&self.url)
                .await
                .expect("connect")
        }

        async fn execute(&self, sql: &str) {
            let mut conn = self.plain().await;
            sqlx::query(sqlx::AssertSqlSafe(sql.to_owned()))
                .execute(&mut conn)
                .await
                .expect("execute");
        }

        async fn count(&self, sql: &str) -> i64 {
            let mut conn = self.plain().await;
            sqlx::query_scalar(sqlx::AssertSqlSafe(sql.to_owned()))
                .fetch_one(&mut conn)
                .await
                .expect("count")
        }

        async fn drop(self) {
            sqlx::query(sqlx::AssertSqlSafe(format!(
                "DROP DATABASE {} WITH (FORCE)",
                self.name
            )))
            .execute(&self.admin)
            .await
            .expect("drop");
            self.admin.close().await;
        }
    }

    /// Captures, copies and verifies `db` into `pg`, as the worker's Copying step does.
    async fn copy_to(db: &Db, pg: &Pg) {
        db.start_capture().await.expect("capture");
        let mut conn = pg.conn().await;
        transfer::migrate(&mut conn).await.expect("migrate");
        db.copy_all(&mut conn, |_| {}).await.expect("copy");
        db.sync(&mut conn, true, |_| {}).await.expect("verify");
        sqlx::Connection::close(conn).await.expect("close");
    }

    #[tokio::test]
    async fn rejected_targets_stay_unchanged() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let (_db, migrator) = setup(&dir.0, BootFile::default(), short()).await;
        pg.execute("CREATE TABLE other(x int)").await;
        assert!(matches!(
            migrator.start(pg.url.clone()).await,
            Err(Fail::Bad(message)) if message == EMPTY_ONLY
        ));
        assert_eq!(
            pg.count("SELECT count(*) FROM pg_class WHERE relname = '_sqlx_migrations'")
                .await,
            0
        );
        pg.execute("DROP TABLE other").await;
        let mut conn = pg.conn().await;
        transfer::migrate(&mut conn).await.expect("migrate");
        drop(conn);
        pg.execute(
            "INSERT INTO blobs (id, data) VALUES ('00000000-0000-0000-0000-0000000000aa', '\\x00')",
        )
        .await;
        let migrations = pg.count("SELECT count(*) FROM _sqlx_migrations").await;
        assert!(matches!(
            migrator.start(pg.url.clone()).await,
            Err(Fail::Bad(message)) if message == EMPTY_ONLY
        ));
        assert_eq!(pg.count("SELECT count(*) FROM blobs").await, 1);
        assert_eq!(
            pg.count("SELECT count(*) FROM _sqlx_migrations").await,
            migrations
        );
        assert!(migrator.view().migration.is_none());
        pg.drop().await;
    }

    #[tokio::test]
    async fn steady_slow_pass_is_not_a_stall() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        let keys: Vec<(String, String)> = (0..10_000)
            .map(|i| (format!("test.key{i}"), i.to_string()))
            .collect();
        let set: Vec<(&str, &str)> = keys.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect();
        db.write_config(&set, &[]).await.expect("write");
        pg.execute(
            "CREATE FUNCTION slow() RETURNS trigger LANGUAGE plpgsql AS \
             'BEGIN PERFORM pg_sleep(0.25); RETURN NULL; END'",
        )
        .await;
        pg.execute(
            "CREATE TRIGGER slow BEFORE INSERT ON config FOR EACH STATEMENT EXECUTE FUNCTION slow()",
        )
        .await;
        let limits = Limits {
            stall: Duration::from_secs(1),
            lock_timeout: Duration::from_millis(500),
            cleanup: Duration::from_secs(1),
        };
        let started = Instant::now();
        let mut conn = None;
        guarded_sync(&db, &mut conn, &pg.url, false, limits, |_| {})
            .await
            .expect("sync");
        assert!(started.elapsed() >= Duration::from_secs(2));
        assert_eq!(db.pending_changes().await.expect("pending"), 0);
        drop(conn);
        db.close().await;
        pg.drop().await;
    }

    async fn cut(db: Db, boot: &Boot, dir: &Path, limits: Limits) -> anyhow::Result<Booted> {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        serve_cutover(listener, db, boot, dir, limits, &CancellationToken::new()).await
    }

    fn disk_state(dir: &Path) -> Option<MigrationState> {
        BootFile::load(dir)
            .expect("load")
            .migration
            .map(|m| m.state)
    }

    fn file_state(file: &BootFile) -> Option<MigrationState> {
        file.migration.as_ref().map(|m| m.state)
    }

    fn renamed(dir: &Path) -> bool {
        dir.join(PRE_POSTGRES).exists()
    }

    /// Saves a migration to `target` in `state`, at `db`'s schema version.
    async fn request(db: &Db, dir: &Path, target: &str, state: MigrationState) {
        let mut file = in_state(target, state);
        if let Some(migration) = &mut file.migration {
            migration.schema_version = db.schema_version().await.expect("version");
        }
        file.save(dir, owner()).expect("save");
    }

    async fn rebuild_config(dir: &Path) {
        let raw = sqlx::SqlitePool::connect(&sqlite_url(dir))
            .await
            .expect("open raw");
        sqlx::raw_sql(
            "CREATE TABLE config_new (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL); \
             INSERT INTO config_new SELECT key, value FROM config; \
             DROP TABLE config; ALTER TABLE config_new RENAME TO config;",
        )
        .execute(&raw)
        .await
        .expect("rebuild");
        raw.close().await;
    }

    #[tokio::test]
    async fn failed_cutover_stays_on_sqlite() {
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        db.start_capture().await.expect("capture");
        request(&db, &dir.0, UNREACHABLE, MigrationState::Cutover).await;
        let booted = cut(db, &boot(&dir.0), &dir.0, short()).await.expect("boot");
        assert!(!booted.db.is_postgres());
        let error = booted.error.expect("error");
        assert!(
            !error.contains(UNREACHABLE) && !error.contains(":p@"),
            "{error}"
        );
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Syncing));
        assert_eq!(
            booted.db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        assert!(dir.0.join(DEFAULT_SQLITE).exists() && !renamed(&dir.0));
        booted.db.close().await;
    }

    #[tokio::test]
    async fn boot_reconciles_capture() {
        // (a) A crash dropped capture after the switch was requested.
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        request(&db, &dir.0, UNREACHABLE, MigrationState::Cutover).await;
        let booted = cut(db, &boot(&dir.0), &dir.0, short()).await.expect("boot");
        assert!(booted.error.is_some());
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Copying));
        assert_eq!(file_state(&booted.file), Some(MigrationState::Copying));
        assert_eq!(
            booted.db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        booted.db.close().await;

        // (b) A migration rebuilt a captured table.
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        db.start_capture().await.expect("capture");
        request(&db, &dir.0, UNREACHABLE, MigrationState::Syncing).await;
        rebuild_config(&dir.0).await;
        assert_eq!(
            db.capture().await.expect("capture"),
            transfer::Capture::Broken
        );
        let booted = cut(db, &boot(&dir.0), &dir.0, short()).await.expect("boot");
        assert!(booted.error.is_none());
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Copying));
        assert_eq!(
            booted.db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        booted.db.close().await;

        // (c) Capture left behind without a migration.
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        db.start_capture().await.expect("capture");
        let booted = cut(db, &boot(&dir.0), &dir.0, short()).await.expect("boot");
        assert!(booted.error.is_none());
        assert_eq!(
            booted.db.capture().await.expect("capture"),
            transfer::Capture::Off
        );
        booted.db.close().await;
    }

    async fn transfer_backends(pg: &Pg) -> i64 {
        sqlx::query_scalar(
            "SELECT count(*) FROM pg_stat_activity \
             WHERE application_name = 'infinidysk-transfer' AND datname = $1",
        )
        .bind(&pg.name)
        .fetch_one(&pg.admin)
        .await
        .expect("count")
    }

    async fn backends_gone(pg: &Pg, within: Duration) {
        let deadline = Instant::now() + within;
        while transfer_backends(pg).await > 0 {
            assert!(Instant::now() < deadline, "transfer backends stayed");
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }

    #[tokio::test]
    async fn blocked_cutover_cleans_up_and_retries() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let boot = boot(&dir.0);
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        db.write_config(&[("test.key", "1")], &[])
            .await
            .expect("write");
        request(&db, &dir.0, &pg.url, MigrationState::Cutover).await;

        // Lock: the final pass waits on a lock and gives up on the server.
        let mut locker = pg.plain().await;
        let mut tx = sqlx::Connection::begin(&mut locker).await.expect("begin");
        sqlx::query("LOCK TABLE config IN ACCESS EXCLUSIVE MODE")
            .execute(&mut *tx)
            .await
            .expect("lock");
        let booted = tokio::time::timeout(Duration::from_secs(30), cut(db, &boot, &dir.0, short()))
            .await
            .expect("in time")
            .expect("boot");
        assert!(!booted.db.is_postgres());
        assert!(booted.error.is_some());
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Syncing));
        assert!(!renamed(&dir.0));
        backends_gone(&pg, Duration::from_secs(5)).await;
        tx.rollback().await.expect("unlock");
        drop(locker);

        // Stall: the server stops answering mid-pass.
        pg.execute(
            "CREATE FUNCTION stall() RETURNS trigger LANGUAGE plpgsql AS \
             'BEGIN PERFORM pg_sleep(10); RETURN NULL; END'",
        )
        .await;
        pg.execute(
            "CREATE TRIGGER stall BEFORE INSERT ON config FOR EACH STATEMENT EXECUTE FUNCTION stall()",
        )
        .await;
        request(&booted.db, &dir.0, &pg.url, MigrationState::Cutover).await;
        let booted = tokio::time::timeout(
            Duration::from_secs(8),
            cut(booted.db, &boot, &dir.0, short()),
        )
        .await
        .expect("in time")
        .expect("boot");
        assert!(!booted.db.is_postgres());
        let error = booted.error.expect("error");
        assert!(error.contains("stopped responding"), "{error}");
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Syncing));
        backends_gone(&pg, Duration::from_secs(15)).await;

        // Retry: the next boot switches.
        pg.execute("DROP TRIGGER stall ON config").await;
        request(&booted.db, &dir.0, &pg.url, MigrationState::Cutover).await;
        let booted = cut(booted.db, &boot, &dir.0, short()).await.expect("boot");
        assert!(booted.db.is_postgres());
        assert!(booted.error.is_none());
        assert!(renamed(&dir.0));
        booted.db.close().await;
        pg.drop().await;
    }

    #[tokio::test]
    async fn unpublished_switch_stays_on_sqlite() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let boot = boot(&dir.0);
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        request(&db, &dir.0, &pg.url, MigrationState::Cutover).await;
        let tmp = dir.0.join(format!("{BOOT_FILE}.tmp"));
        fs::create_dir(&tmp).expect("block tmp");
        let booted = cut(db, &boot, &dir.0, short()).await.expect("boot");
        assert!(!booted.db.is_postgres());
        let error = booted.error.expect("error");
        assert!(error.contains(BOOT_FILE), "{error}");
        assert_eq!(file_state(&booted.file), Some(MigrationState::Syncing));
        assert_eq!(disk_state(&dir.0), Some(MigrationState::Cutover));
        assert_eq!(
            booted.db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        assert!(!renamed(&dir.0));
        fs::remove_dir(&tmp).expect("unblock tmp");
        let booted = cut(booted.db, &boot, &dir.0, short()).await.expect("boot");
        assert!(booted.db.is_postgres());
        assert!(renamed(&dir.0));
        booted.db.close().await;
        pg.drop().await;
    }

    /// The whole switch through `serve` with `home` = a custom folder: the boot file and the
    /// retired SQLite file stay there, and the next start picks PostgreSQL from it.
    #[tokio::test]
    async fn serve_switches_to_postgresql_in_its_config_folder() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        request(&db, &dir.0, &pg.url, MigrationState::Cutover).await;
        db.close().await;
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let vars = vec![
            (
                "INFINIDYSK__AUTH__API_KEY".to_owned(),
                "adminkey".to_owned(),
            ),
            (
                "INFINIDYSK__MOUNT__DIR".to_owned(),
                dir.0.join("mnt").display().to_string(),
            ),
        ];
        let cancel = CancellationToken::new();
        let server = tokio::spawn({
            let (boot, cancel) = (boot(&dir.0), cancel.clone());
            async move { crate::app::serve(listener, &boot, vars, cancel).await }
        });
        tokio::time::timeout(Duration::from_secs(60), async {
            while !renamed(&dir.0) {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
        })
        .await
        .expect("the switch did not finish");
        cancel.cancel();
        let exit = tokio::time::timeout(Duration::from_secs(60), server)
            .await
            .expect("the server did not stop")
            .expect("join")
            .expect("serve");
        assert_eq!(exit, crate::app::Exit::Stopped);
        assert!(!dir.0.join(DEFAULT_SQLITE).exists());
        // What `Boot::from_env` asks at the next start, with `home` = this folder.
        let (_, url, source) = select(None, &dir.0).expect("select");
        assert_eq!(url, pg.url);
        assert_eq!(source, Source::BootFile);
        pg.drop().await;
    }

    #[tokio::test]
    async fn switch_stops_when_selection_is_not_durable() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        request(&db, &dir.0, &pg.url, MigrationState::Cutover).await;
        fail_next_dir_sync();
        let error = match cut(db, &boot(&dir.0), &dir.0, short()).await {
            Ok(_) => panic!("the switch went on"),
            Err(error) => format!("{error:#}"),
        };
        assert!(error.contains(BOOT_FILE), "{error}");
        assert!(!renamed(&dir.0));
        let db = sqlite(&dir.0).await;
        assert_eq!(
            db.capture().await.expect("capture"),
            transfer::Capture::Intact
        );
        db.close().await;
        let visible = BootFile::load(&dir.0).expect("load");
        assert_eq!(visible.url.as_deref(), Some(pg.url.as_str()));

        // An immediate restart whose sync fails again opens nothing.
        fail_next_dir_sync();
        assert!(select(None, &dir.0).is_err());
        assert!(!renamed(&dir.0));

        // A restart with a working sync runs on PostgreSQL and retires SQLite.
        let (_, url, source) = select(None, &dir.0).expect("select");
        assert_eq!(url, pg.url);
        assert_eq!(source, Source::BootFile);
        let mut boot = boot(&dir.0);
        boot.database_url = url;
        boot.database_source = source;
        let pg_db = Db::connect(&boot.database_url).await.expect("connect");
        let booted = cut(pg_db, &boot, &dir.0, short()).await.expect("boot");
        assert!(booted.db.is_postgres());
        assert!(renamed(&dir.0) && !dir.0.join(DEFAULT_SQLITE).exists());
        booted.db.close().await;
        pg.drop().await;
    }

    #[tokio::test]
    async fn cancel_is_durable_with_target_locked() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let boot = boot(&dir.0);
        let db = sqlite(&dir.0).await;
        copy_to(&db, &pg).await;
        request(&db, &dir.0, &pg.url, MigrationState::Syncing).await;
        let limits = Limits {
            cleanup: Duration::from_secs(1),
            ..short()
        };
        let migrator = Migrator::new(
            db.clone(),
            &boot,
            &dir.0,
            BootFile::load(&dir.0).expect("load"),
            limits,
            None,
        );
        migrator.resume().await;
        let mut locker = pg.plain().await;
        let mut tx = sqlx::Connection::begin(&mut locker).await.expect("begin");
        sqlx::query("LOCK TABLE blobs IN ACCESS EXCLUSIVE MODE")
            .execute(&mut *tx)
            .await
            .expect("lock");
        let cancelled = tokio::time::timeout(Duration::from_secs(10), migrator.cancel())
            .await
            .expect("in time");
        assert_eq!(cancelled.ok(), Some(vec![TARGET_LEFT.to_owned()]));
        assert!(BootFile::load(&dir.0).expect("load").migration.is_none());
        assert_eq!(db.capture().await.expect("capture"), transfer::Capture::Off);

        let booted = cut(db, &boot, &dir.0, limits).await.expect("boot");
        assert!(booted.error.is_none());
        let next = Migrator::new(
            booted.db.clone(),
            &boot,
            &dir.0,
            booted.file,
            limits,
            booted.error,
        );
        next.resume().await;
        assert!(next.view().migration.is_none());
        tx.rollback().await.expect("unlock");
        drop(locker);
        booted.db.close().await;
        pg.drop().await;
    }

    #[tokio::test]
    async fn divergence_recopies_then_switches() {
        let Some(pg) = Pg::create().await else {
            return;
        };
        let dir = Dir::new();
        let boot = boot(&dir.0);
        let db = sqlite(&dir.0).await;
        db.write_config(&[("test.key", "sqlite")], &[])
            .await
            .expect("write");
        copy_to(&db, &pg).await;
        pg.execute("UPDATE config SET value = 'postgres' WHERE key = 'test.key'")
            .await;
        request(&db, &dir.0, &pg.url, MigrationState::Cutover).await;
        let booted = cut(db, &boot, &dir.0, short()).await.expect("boot");
        assert!(!booted.db.is_postgres());
        let error = booted.error.clone().expect("error");
        assert!(error.contains("config"), "{error}");
        assert_eq!(file_state(&booted.file), Some(MigrationState::Copying));

        let migrator = Migrator::new(
            booted.db.clone(),
            &boot,
            &dir.0,
            booted.file,
            short(),
            booted.error,
        );
        migrator.resume().await;
        let deadline = Instant::now() + Duration::from_secs(30);
        loop {
            let view = migrator.view().migration.expect("migration");
            if view.state == MigrationState::Syncing && view.error.is_none() {
                break;
            }
            assert!(Instant::now() < deadline, "no sync: {:?}", view.error);
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        assert!(migrator.cutover().await.is_ok());
        let booted = cut(booted.db, &boot, &dir.0, short()).await.expect("boot");
        assert!(booted.db.is_postgres());
        let value = booted
            .db
            .config()
            .await
            .expect("config")
            .into_iter()
            .find(|(key, _)| key == "test.key")
            .map(|(_, value)| value);
        assert_eq!(value.as_deref(), Some("sqlite"));
        let disk = BootFile::load(&dir.0).expect("load");
        assert_eq!(disk.url.as_deref(), Some(pg.url.as_str()));
        assert!(disk.migration.is_none());
        assert!(renamed(&dir.0));
        let migrator = Migrator::new(booted.db.clone(), &boot, &dir.0, booted.file, short(), None);
        assert_eq!(
            migrator.view().target,
            Some(transfer::describe(&pg.url).expect("describe"))
        );
        booted.db.close().await;
        pg.drop().await;
    }
}
