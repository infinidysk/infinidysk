//! Restore and undo at startup. The intent file `<db>.restore.json` holds a phase; each
//! phase's step is made durable before the next phase is written, so a crash at any point
//! continues where it stopped.

use std::fs::{self, File};
use std::io;
use std::path::{Path, PathBuf};

use infinidysk_db::{Db, now_ms};
use serde::{Deserialize, Serialize};
use tracing::{info, warn};
use uuid::Uuid;

use super::LastRestore;
use super::files::{Part, Steps, WriteError, remove, sync_dir, with, write_atomic};
use crate::app::{Boot, sqlite_path};

pub(super) const FORMAT: u32 = 1;
pub(super) const LAST_RESTORE_KEY: &str = "backup.last_restore";
/// A database file and the files SQLite keeps beside it; the main file last.
const SET: [&str; 4] = ["-journal", "-shm", "-wal", ""];

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Action {
    Restore,
    Undo,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Phase {
    Requested,
    AsideCleared,
    LiveAside,
    Swapped,
    Opened,
    RollingBack,
    IncomingBack,
    RolledBack,
}

/// A restore or undo waiting for the next start, format 1.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Intent {
    pub format: u32,
    /// The id `POST …/restore` or `POST …/undo` returned; `finish` copies it into `last_restore`.
    pub attempt: Uuid,
    pub action: Action,
    pub phase: Phase,
    pub backup: Option<String>,
    pub backup_created_ms: Option<i64>,
    /// When the swap finished; recorded as the restore time, so a repeated finish writes
    /// the same value.
    pub swapped_ms: Option<i64>,
    /// Why the open failed; set when `rolling-back` is written.
    pub failed: Option<String>,
}

/// The files of a restore, named after the live database `L`.
#[derive(Clone, Debug)]
pub struct Paths {
    pub live: PathBuf,
}

impl Paths {
    /// `S`: the checked copy waiting to replace `L`.
    pub fn staged(&self) -> PathBuf {
        with(&self.live, ".restore")
    }

    /// `U`: the database a restore replaced.
    pub fn undo(&self) -> PathBuf {
        with(&self.live, ".before-restore")
    }

    /// `D`: the restored database an undo moved aside.
    pub fn undone(&self) -> PathBuf {
        with(&self.live, ".undone")
    }

    /// `I`: the intent.
    pub fn intent(&self) -> PathBuf {
        with(&self.live, ".restore.json")
    }

    /// The copy while it is written.
    pub fn part(&self) -> PathBuf {
        with(&self.live, ".restore.part")
    }

    pub fn dir(&self) -> &Path {
        self.live
            .parent()
            .filter(|p| !p.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."))
    }

    fn aside(&self, action: Action) -> PathBuf {
        match action {
            Action::Restore => self.undo(),
            Action::Undo => self.undone(),
        }
    }

    fn incoming(&self, action: Action) -> PathBuf {
        match action {
            Action::Restore => self.staged(),
            Action::Undo => self.undo(),
        }
    }

    /// The live file of `boot`, or where it would be on PostgreSQL.
    fn of(boot: &Boot) -> (Self, bool) {
        sqlite_path(&boot.database_url).map_or_else(
            || {
                let live = boot.config_dir.join("infinidysk.sqlite");
                (Self { live }, true)
            },
            |live| (Self { live }, false),
        )
    }
}

pub enum Pending {
    None,
    /// The phase is `swapped`, `opened` or `rolled-back`: open the database, then `finish`.
    Open(Intent),
}

impl Pending {
    /// An open failure may roll back: the phase is `swapped`, and only that.
    pub fn swapped(&self) -> bool {
        matches!(self, Self::Open(i) if i.phase == Phase::Swapped)
    }
}

/// Something boot cannot explain; startup stops and nothing is opened.
#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct Unresolved(pub String);

fn exists(path: &Path) -> bool {
    path.symlink_metadata().is_ok()
}

fn unresolved(step: &str, files: &[&Path], error: &dyn std::fmt::Display) -> Unresolved {
    let files: Vec<String> = files.iter().map(|f| f.display().to_string()).collect();
    Unresolved(format!(
        "A database restore stopped at \"{step}\" ({}): {error}. Fix the cause and restart; \
         infinidysk continues where it stopped.",
        files.join(", ")
    ))
}

/// Deletes a file set; missing files are fine.
fn delete_set(base: &Path, steps: &mut Steps) -> io::Result<()> {
    for suffix in SET {
        let path = with(base, suffix);
        steps.run(|| remove(&path))?;
    }
    Ok(())
}

/// Moves each file of the `from` set that exists to the `to` set. A file on both sides is
/// a conflict, checked before anything moves.
fn move_set(from: &Path, to: &Path, steps: &mut Steps) -> io::Result<()> {
    for suffix in SET {
        let (f, t) = (with(from, suffix), with(to, suffix));
        if exists(&f) && exists(&t) {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!("both {} and {} exist", f.display(), t.display()),
            ));
        }
    }
    for suffix in SET {
        let (f, t) = (with(from, suffix), with(to, suffix));
        if exists(&f) {
            steps.run(|| fs::rename(&f, &t))?;
        }
    }
    Ok(())
}

/// Moves `S` to `L` when no file of the `L` set is there; done when `S` is gone and `L` is
/// there. A stray `L-wal` would be replayed into the restored copy, so it is an error.
fn place(staged: &Path, live: &Path, steps: &mut Steps) -> io::Result<()> {
    let live_set = SET.iter().any(|s| exists(&with(live, s)));
    match (exists(staged), live_set) {
        (true, false) => steps.run(|| fs::rename(staged, live)),
        (false, true) if exists(live) => Ok(()),
        _ => Err(io::Error::other(format!(
            "expected {} without any file of {}",
            staged.display(),
            live.display()
        ))),
    }
}

fn write_phase(
    paths: &Paths,
    intent: &mut Intent,
    phase: Phase,
    steps: &mut Steps,
) -> Result<(), Unresolved> {
    intent.phase = phase;
    let i = paths.intent();
    let bytes = serde_json::to_vec_pretty(intent).map_err(|e| unresolved("saving", &[&i], &e))?;
    write_atomic(&i, &bytes, steps).map_err(|e| unresolved("saving the next phase", &[&i], &e))
}

/// Runs before the database opens.
pub fn apply_pending(boot: &Boot) -> Result<Pending, Unresolved> {
    let (paths, postgres) = Paths::of(boot);
    apply_pending_with(&paths, postgres, &mut Steps::default())
}

pub(super) fn apply_pending_with(
    paths: &Paths,
    postgres: bool,
    steps: &mut Steps,
) -> Result<Pending, Unresolved> {
    let i = paths.intent();
    let bytes = match fs::read(&i) {
        Ok(bytes) => bytes,
        Err(e) if e.kind() == io::ErrorKind::NotFound => {
            if postgres {
                return Ok(Pending::None);
            }
            // A staging that never committed.
            for leftover in [paths.staged(), paths.part(), with(&i, ".tmp")] {
                if let Err(error) = remove(&leftover) {
                    warn!(path = %leftover.display(), reason = %error, "could not delete a restore leftover");
                }
            }
            return Ok(Pending::None);
        }
        Err(e) => return Err(unresolved("reading the restore request", &[&i], &e)),
    };
    if postgres {
        return Err(Unresolved(format!(
            "A SQLite restore is waiting in {}, but INFINIDYSK_DATABASE_URL points at PostgreSQL. \
             Point it back at SQLite to finish it, or delete {} to drop it.",
            paths.dir().display(),
            i.display()
        )));
    }
    let intent: Intent = serde_json::from_slice(&bytes)
        .map_err(|e| unresolved("reading the restore request", &[&i], &e))?;
    if intent.format != FORMAT {
        return Err(unresolved(
            "reading the restore request",
            &[&i],
            &format!("unknown format {}", intent.format),
        ));
    }
    let durable = (|| {
        steps.run(|| remove(&with(&i, ".tmp")))?;
        steps.run(|| File::open(&i)?.sync_all())?;
        steps.run(|| sync_dir(paths.dir()))
    })();
    durable.map_err(|e| unresolved("saving the restore request", &[&i], &e))?;
    run_phases(paths, intent, steps)
}

fn run_phases(paths: &Paths, mut intent: Intent, steps: &mut Steps) -> Result<Pending, Unresolved> {
    let live = paths.live.as_path();
    let dir = paths.dir();
    let action = intent.action;
    let (aside, incoming) = (paths.aside(action), paths.incoming(action));
    loop {
        let next = match intent.phase {
            Phase::Swapped | Phase::Opened | Phase::RolledBack => {
                return Ok(Pending::Open(intent));
            }
            Phase::Requested => {
                if !exists(&incoming) {
                    return Err(unresolved(
                        "starting",
                        &[&incoming],
                        &"the database to switch to is missing",
                    ));
                }
                delete_set(&aside, steps)
                    .and_then(|()| steps.run(|| sync_dir(dir)))
                    .map_err(|e| unresolved("clearing the aside copy", &[&aside], &e))?;
                Phase::AsideCleared
            }
            Phase::AsideCleared => {
                move_set(live, &aside, steps)
                    .and_then(|()| steps.run(|| sync_dir(dir)))
                    .map_err(|e| {
                        unresolved("moving the current database aside", &[live, &aside], &e)
                    })?;
                Phase::LiveAside
            }
            Phase::LiveAside => {
                match action {
                    Action::Restore => place(&incoming, live, steps),
                    Action::Undo => move_set(&incoming, live, steps),
                }
                .and_then(|()| steps.run(|| sync_dir(dir)))
                .map_err(|e| unresolved("moving the new database in", &[&incoming, live], &e))?;
                intent.swapped_ms = Some(now_ms());
                Phase::Swapped
            }
            Phase::RollingBack => {
                match action {
                    Action::Restore => delete_set(live, steps),
                    Action::Undo => move_set(live, &incoming, steps),
                }
                .and_then(|()| steps.run(|| sync_dir(dir)))
                .map_err(|e| unresolved("removing the database that did not open", &[live], &e))?;
                Phase::IncomingBack
            }
            Phase::IncomingBack => {
                move_set(&aside, live, steps)
                    .and_then(|()| steps.run(|| sync_dir(dir)))
                    .map_err(|e| {
                        unresolved("putting the previous database back", &[&aside, live], &e)
                    })?;
                Phase::RolledBack
            }
        };
        write_phase(paths, &mut intent, next, steps)?;
    }
}

/// Only from `swapped`, after `Db::connect` failed: writes `rolling-back` with `reason`
/// and nothing else. The caller restarts; the next `apply_pending` moves the files.
pub fn begin_roll_back(boot: &Boot, pending: Pending, reason: &str) -> Result<(), Unresolved> {
    let (paths, _) = Paths::of(boot);
    begin_roll_back_with(&paths, pending, reason, &mut Steps::default())
}

pub(super) fn begin_roll_back_with(
    paths: &Paths,
    pending: Pending,
    reason: &str,
    steps: &mut Steps,
) -> Result<(), Unresolved> {
    let Pending::Open(mut intent) = pending else {
        return Err(Unresolved("No restore is waiting to roll back.".to_owned()));
    };
    if intent.phase != Phase::Swapped {
        return Err(unresolved(
            "opening the database",
            &[&paths.live],
            &format!(
                "it did not open after a finished restore or undo, so it is not rolled back: {reason}"
            ),
        ));
    }
    intent.failed = Some(reason.to_owned());
    write_phase(paths, &mut intent, Phase::RollingBack, steps)
}

/// After a good open from `swapped`: makes `opened` durable, so no later start rolls back.
pub(super) fn mark_opened(
    paths: &Paths,
    mut intent: Intent,
    steps: &mut Steps,
) -> Result<Intent, Unresolved> {
    if intent.phase == Phase::Swapped {
        write_phase(paths, &mut intent, Phase::Opened, steps)?;
    }
    Ok(intent)
}

/// Deletes what the finished intent leaves behind, then the intent.
pub(super) fn clean_up(paths: &Paths, intent: &Intent, steps: &mut Steps) -> io::Result<()> {
    if intent.action == Action::Undo && intent.phase == Phase::Opened {
        delete_set(&paths.undone(), steps)?;
        steps.run(|| sync_dir(paths.dir()))?;
    }
    steps.run(|| remove(&paths.intent()))?;
    steps.run(|| sync_dir(paths.dir()))
}

async fn record(db: &Db, intent: &Intent) -> Result<(), infinidysk_db::Error> {
    let restored = |failed: Option<String>| LastRestore {
        attempt: intent.attempt,
        backup: intent.backup.clone().unwrap_or_default(),
        backup_created_ms: intent.backup_created_ms,
        at_ms: intent.swapped_ms.unwrap_or_default(),
        failed,
        undo_failed: None,
    };
    let last = match (intent.action, intent.phase) {
        (Action::Restore, Phase::Opened) => Some(restored(None)),
        (Action::Restore, Phase::RolledBack) => Some(restored(intent.failed.clone())),
        (Action::Undo, Phase::Opened) => {
            return db.write_config(&[], &[LAST_RESTORE_KEY]).await;
        }
        (Action::Undo, Phase::RolledBack) => {
            let mut last = super::last_restore(db)
                .await
                .unwrap_or_else(|| restored(None));
            last.undo_failed = Some(intent.failed.clone().unwrap_or_default());
            Some(last)
        }
        _ => None,
    };
    let Some(last) = last else {
        return Ok(());
    };
    let json = serde_json::to_string(&last).unwrap_or_default();
    db.write_config(&[(LAST_RESTORE_KEY, &json)], &[]).await
}

/// After the database opened. From `swapped`, first makes `opened` durable and fails if it
/// cannot; then records the outcome, cleans up and deletes `I`.
pub async fn finish(db: &Db, boot: &Boot, pending: Pending) -> Result<(), Unresolved> {
    let Pending::Open(intent) = pending else {
        return Ok(());
    };
    let (paths, _) = Paths::of(boot);
    let intent = mark_opened(&paths, intent, &mut Steps::default())?;
    if let Err(error) = record(db, &intent).await {
        warn!(reason = %error, "could not record the restore; trying again at the next start");
        return Ok(());
    }
    if let Err(error) = clean_up(&paths, &intent, &mut Steps::default()) {
        warn!(reason = %error, "could not clean up after the restore; trying again at the next start");
        return Ok(());
    }
    match (intent.action, intent.phase) {
        (Action::Restore | Action::Undo, Phase::RolledBack) => {
            warn!(
                reason = intent.failed.as_deref().unwrap_or_default(),
                "the database did not open; the previous one is back"
            );
        }
        (action, _) => info!(?action, "database switched"),
    }
    Ok(())
}

/// Staging step 4: the finished copy becomes `S`, made durable.
pub(super) fn place_staged(paths: &Paths, part: Part, steps: &mut Steps) -> io::Result<Part> {
    let staged = paths.staged();
    steps.run(|| fs::rename(part.path(), &staged))?;
    part.keep();
    let staged = Part::adopt(staged);
    steps.run(|| sync_dir(paths.dir()))?;
    Ok(staged)
}

/// Staging steps 6–7, the commit: publishes `I`. `S` stays once `I` is visible.
pub(super) fn commit(
    paths: &Paths,
    staged: Part,
    intent: &Intent,
    steps: &mut Steps,
) -> Result<(), WriteError> {
    let bytes = serde_json::to_vec_pretty(intent)
        .map_err(|e| WriteError::Unpublished(io::Error::other(e)))?;
    match write_atomic(&paths.intent(), &bytes, steps) {
        Ok(()) => {
            staged.keep();
            Ok(())
        }
        Err(WriteError::NotDurable(e)) => {
            staged.keep();
            Err(WriteError::NotDurable(e))
        }
        Err(e) => Err(e),
    }
}
