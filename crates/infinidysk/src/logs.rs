//! Recent log events kept in memory for the UI, and in rotated files so they survive
//! restarts.

use std::collections::{HashMap, VecDeque};
use std::fmt::{Debug, Write as _};
use std::fs::{File, OpenOptions};
use std::io::{self, BufRead as _, BufReader, Write as _};
use std::os::unix::fs::MetadataExt as _;
use std::path::{Path, PathBuf};
use std::sync::{LazyLock, Mutex, OnceLock, PoisonError};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use tracing::field::{Field, Visit};
use tracing::span::{Attributes, Id};
use tracing::{Event, Subscriber};
use tracing_subscriber::EnvFilter;
use tracing_subscriber::Layer;
use tracing_subscriber::layer::Context;
use tracing_subscriber::registry::LookupSpan;
use utoipa::ToSchema;

/// Events kept; older ones drop off.
const CAPACITY: usize = 2000;
/// Events kept per task, apart from the rest so busy logs do not push them out.
const TASK_CAPACITY: usize = 1000;
/// A log file is rotated at this size.
const FILE_BYTES: u64 = 10_000_000;
/// Rotated files kept beside the current one.
const ROTATED: usize = 3;
const FILE_NAME: &str = "infinidysk.log";
const OWNERSHIP_TARGET: &str = "infinidysk::logs::ownership";

/// Every event the process logs, newest last.
pub static LOGS: Logs = Logs(Mutex::new(Rings {
    all: VecDeque::new(),
    tasks: None,
}));

/// Where log files go, once [`Capture::persisted`] ran.
static DIR: OnceLock<PathBuf> = OnceLock::new();

/// Unix milliseconds this process started; marks which run logged an event.
static RUN: LazyLock<i64> = LazyLock::new(now_ms);

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

/// Unix milliseconds this process started.
pub fn started_ms() -> i64 {
    *RUN
}

/// The FUSE library's request session. It warns whenever an interrupt reply lands after its
/// request finished, a normal race, and opens an INFO span per request.
const FUSE_SESSION: &str = "infinidysk_fuse::raw::session";

/// The log filter for a `RUST_LOG` value (`None` when unset): `info` when unset or invalid.
/// [`FUSE_SESSION`] stays at `error` unless a directive targets it; `EnvFilter` matches
/// targets by prefix, so `infinidysk_fuse=debug` does.
pub fn filter(rust_log: Option<&str>) -> EnvFilter {
    let quiet = format!("{FUSE_SESSION}=error");
    let fallback = || EnvFilter::new(format!("info,{quiet}"));
    let Some(value) = rust_log else {
        return fallback();
    };
    let targets_session = value.split(',').any(|directive| {
        directive
            .trim()
            .split(['[', '='])
            .next()
            .is_some_and(|target| !target.is_empty() && FUSE_SESSION.starts_with(target))
    });
    let value = if targets_session {
        value.to_owned()
    } else {
        format!("{value},{quiet}")
    };
    EnvFilter::try_new(value).unwrap_or_else(|_| fallback())
}

pub struct Logs(Mutex<Rings>);

struct Rings {
    all: VecDeque<Entry>,
    /// Events logged inside a span with a `task` field, per task.
    tasks: Option<HashMap<String, VecDeque<Entry>>>,
}

impl Rings {
    fn push(&mut self, entry: Entry) {
        if let Some(task) = &entry.task {
            let ring = self
                .tasks
                .get_or_insert_default()
                .entry(task.clone())
                .or_default();
            if ring.len() == TASK_CAPACITY {
                ring.pop_front();
            }
            ring.push_back(entry.clone());
        }
        if self.all.len() == CAPACITY {
            self.all.pop_front();
        }
        self.all.push_back(entry);
    }
}

#[derive(Clone, Serialize, Deserialize, ToSchema)]
pub struct Entry {
    /// Unix milliseconds.
    pub time: i64,
    pub level: Level,
    /// Module that logged it.
    pub target: String,
    pub message: String,
    /// Structured fields as `key=value` pairs separated by spaces.
    pub fields: String,
    /// The task (a maintenance job id) it was logged for.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub task: Option<String>,
    /// Unix milliseconds the logging process started; absent on older lines.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub run: Option<i64>,
}

/// Most to least severe.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum Level {
    Error,
    Warn,
    Info,
    Debug,
    Trace,
}

impl Logs {
    /// Kept events at `level` or more severe, of `task` only when given, oldest first.
    pub fn snapshot(&self, level: Level, task: Option<&str>) -> Vec<Entry> {
        let rings = self.lock();
        let ring = match task {
            Some(task) => rings.tasks.as_ref().and_then(|t| t.get(task)),
            None => Some(&rings.all),
        };
        let entries = ring
            .into_iter()
            .flatten()
            .filter(|e| e.level <= level)
            .cloned()
            .collect();
        drop(rings);
        entries
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Rings> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// The [`Layer`] feeding [`LOGS`] and, when persisted, the log file.
pub struct Capture(Option<Mutex<LogFile>>);

struct LogFile {
    dir: PathBuf,
    file: File,
    bytes: u64,
    owner: (u32, u32),
}

impl Capture {
    /// Keeps events in memory only.
    pub const MEMORY: Self = Self(None);

    /// Loads the newest events earlier runs wrote to `dir`, then appends new ones there.
    pub fn persisted(dir: &Path, owner: (u32, u32)) -> io::Result<Self> {
        LazyLock::force(&RUN);
        std::fs::create_dir_all(dir)?;
        own(dir, owner);
        for index in 1..=ROTATED {
            own(&rotated(dir, index), owner);
        }
        let _ = DIR.set(dir.to_owned());
        let mut history = load(dir);
        let file = open(dir, owner)?;
        let bytes = file.metadata()?.len();
        {
            let mut logs = LOGS.lock();
            let mut rings = Rings {
                all: VecDeque::new(),
                tasks: None,
            };
            history.extend(logs.all.drain(..));
            for entry in history {
                rings.push(entry);
            }
            *logs = rings;
        }
        Ok(Self(Some(Mutex::new(LogFile {
            dir: dir.to_owned(),
            file,
            bytes,
            owner,
        }))))
    }
}

impl LogFile {
    /// Appends one JSON line; written straight through so a crash keeps the lead-up.
    fn append(&mut self, entry: &Entry) {
        let Ok(mut line) = serde_json::to_vec(entry) else {
            return;
        };
        line.push(b'\n');
        if self.bytes > 0 && self.bytes + line.len() as u64 > FILE_BYTES {
            for n in (1..ROTATED).rev() {
                let _ = std::fs::rename(rotated(&self.dir, n), rotated(&self.dir, n + 1));
            }
            let _ = std::fs::rename(self.dir.join(FILE_NAME), rotated(&self.dir, 1));
            let Ok(file) = open(&self.dir, self.owner) else {
                return;
            };
            self.file = file;
            self.bytes = 0;
        }
        if self.file.write_all(&line).is_ok() {
            self.bytes += line.len() as u64;
        }
    }
}

/// The newest events in the current and last rotated file, oldest first.
fn load(dir: &Path) -> VecDeque<Entry> {
    tail([rotated(dir, 1), dir.join(FILE_NAME)], CAPACITY, |_| true)
}

/// The last `limit` warnings and errors in every kept log file, oldest first; reads them
/// all, so call it off the async runtime.
pub fn warnings(limit: usize) -> Vec<Entry> {
    let Some(dir) = DIR.get() else {
        return Vec::new();
    };
    let files = (1..=ROTATED)
        .rev()
        .map(|n| rotated(dir, n))
        .chain([dir.join(FILE_NAME)]);
    tail(files, limit, |line| {
        line.contains(r#""level":"warn""#) || line.contains(r#""level":"error""#)
    })
    .into()
}

/// The last `limit` entries in `files` whose line passes `keep`.
fn tail(
    files: impl IntoIterator<Item = PathBuf>,
    limit: usize,
    keep: impl Fn(&str) -> bool,
) -> VecDeque<Entry> {
    let mut history = VecDeque::with_capacity(limit);
    for path in files {
        let Ok(file) = File::open(path) else {
            continue;
        };
        // A line cut short by a crash does not parse and is skipped.
        for entry in BufReader::new(file)
            .lines()
            .map_while(Result::ok)
            .filter(|line| keep(line))
            .filter_map(|line| serde_json::from_str(&line).ok())
        {
            if history.len() == limit {
                history.pop_front();
            }
            history.push_back(entry);
        }
    }
    history
}

fn open(dir: &Path, owner: (u32, u32)) -> io::Result<File> {
    let path = dir.join(FILE_NAME);
    let file = OpenOptions::new().create(true).append(true).open(&path)?;
    own(&path, owner);
    Ok(file)
}

fn own(path: &Path, (uid, gid): (u32, u32)) {
    let Ok(metadata) = std::fs::symlink_metadata(path) else {
        return;
    };
    if !metadata.file_type().is_symlink()
        && metadata.uid() == 0
        && (uid != 0 || metadata.gid() != gid)
        && let Err(error) = std::os::unix::fs::lchown(path, Some(uid), Some(gid))
    {
        tracing::debug!(target: OWNERSHIP_TARGET, path = %path.display(), reason = %error, "could not change log ownership");
    }
}

fn rotated(dir: &Path, n: usize) -> PathBuf {
    dir.join(format!("{FILE_NAME}.{n}"))
}

/// The `task` field of a span, kept in its extensions.
struct Task(String);

#[derive(Default)]
struct TaskField(Option<String>);

impl Visit for TaskField {
    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "task" {
            self.0 = Some(value.to_owned());
        }
    }

    fn record_debug(&mut self, field: &Field, value: &dyn Debug) {
        if field.name() == "task" {
            self.0 = Some(format!("{value:?}"));
        }
    }
}

impl<S: Subscriber + for<'a> LookupSpan<'a>> Layer<S> for Capture {
    fn on_new_span(&self, attrs: &Attributes<'_>, id: &Id, ctx: Context<'_, S>) {
        let mut task = TaskField::default();
        attrs.record(&mut task);
        if let (Some(task), Some(span)) = (task.0, ctx.span(id)) {
            span.extensions_mut().insert(Task(task));
        }
    }

    fn on_event(&self, event: &Event<'_>, ctx: Context<'_, S>) {
        let meta = event.metadata();
        let mut fields = Fields::default();
        event.record(&mut fields);
        let task = ctx.event_scope(event).and_then(|scope| {
            scope
                .into_iter()
                .find_map(|span| span.extensions().get::<Task>().map(|t| t.0.clone()))
        });
        let entry = Entry {
            time: now_ms(),
            level: match *meta.level() {
                tracing::Level::ERROR => Level::Error,
                tracing::Level::WARN => Level::Warn,
                tracing::Level::INFO => Level::Info,
                tracing::Level::DEBUG => Level::Debug,
                tracing::Level::TRACE => Level::Trace,
            },
            target: meta.target().to_owned(),
            message: fields.message,
            fields: fields.rest,
            task,
            run: Some(*RUN),
        };
        if meta.target() != OWNERSHIP_TARGET
            && let Some(file) = &self.0
        {
            file.lock()
                .unwrap_or_else(PoisonError::into_inner)
                .append(&entry);
        }
        LOGS.lock().push(entry);
    }
}

#[derive(Default)]
struct Fields {
    message: String,
    rest: String,
}

impl Fields {
    fn sep(&mut self, field: &Field) {
        if !self.rest.is_empty() {
            self.rest.push(' ');
        }
        self.rest.push_str(field.name());
        self.rest.push('=');
    }
}

impl Visit for Fields {
    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "message" {
            value.clone_into(&mut self.message);
        } else {
            self.sep(field);
            self.rest.push_str(value);
        }
    }

    fn record_debug(&mut self, field: &Field, value: &dyn Debug) {
        if field.name() == "message" {
            let _ = write!(self.message, "{value:?}");
        } else {
            self.sep(field);
            let _ = write!(self.rest, "{value:?}");
        }
    }
}

#[cfg(test)]
mod tests {
    use tracing_subscriber::layer::SubscriberExt as _;

    use super::*;

    #[test]
    fn captures_message_and_fields() {
        let subscriber = tracing_subscriber::registry().with(Capture::MEMORY);
        tracing::subscriber::with_default(subscriber, || {
            tracing::warn!(path = "/a b", count = 3, "capture test");
        });
        let entry = LOGS
            .snapshot(Level::Warn, None)
            .into_iter()
            .find(|e| e.message == "capture test")
            .expect("the event was just logged");
        assert_eq!(entry.fields, "path=/a b count=3");
    }

    #[test]
    fn rust_log_keeps_fuse_session_warnings_out_unless_it_targets_them() {
        let logged = |rust_log: Option<&str>, message: &str| {
            let subscriber = tracing_subscriber::registry()
                .with(filter(rust_log))
                .with(Capture::MEMORY);
            tracing::subscriber::with_default(subscriber, || {
                tracing::warn!(target: "infinidysk_fuse::raw::session", "{message}");
            });
            LOGS.snapshot(Level::Warn, None)
                .iter()
                .any(|e| e.message == message)
        };
        assert!(!logged(None, "fuse session: unset"));
        assert!(!logged(
            Some("info,infinidysk_mount=debug"),
            "fuse session: app only"
        ));
        assert!(logged(
            Some("info,infinidysk_fuse=warn"),
            "fuse session: named"
        ));
    }

    #[test]
    fn events_in_a_task_span_carry_the_task() {
        let subscriber = tracing_subscriber::registry().with(Capture::MEMORY);
        tracing::subscriber::with_default(subscriber, || {
            let span = tracing::info_span!("maintenance", task = "capture-task");
            let _in = span.enter();
            tracing::info_span!("inner").in_scope(|| tracing::info!("in the task"));
        });
        let entries = LOGS.snapshot(Level::Info, Some("capture-task"));
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].message, "in the task");
        assert_eq!(entries[0].task.as_deref(), Some("capture-task"));
    }

    #[test]
    fn ownership_skips_symlinks_and_persistence_still_writes() {
        let dir = std::env::temp_dir().join(format!("infinidysk-log-links-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let target = dir.join("untouched");
        std::fs::write(&target, "keep").unwrap();
        let before = target.metadata().unwrap();
        std::os::unix::fs::symlink(&target, rotated(&dir, 1)).unwrap();
        std::os::unix::fs::symlink(dir.join("absent"), rotated(&dir, 2)).unwrap();
        let capture = Capture::persisted(&dir, (1000, 1000)).unwrap();
        let after = target.metadata().unwrap();
        assert_eq!((after.uid(), after.gid()), (before.uid(), before.gid()));
        assert_eq!(std::fs::read_to_string(&target).unwrap(), "keep");
        capture.0.as_ref().unwrap().lock().unwrap().bytes = FILE_BYTES;
        let subscriber = tracing_subscriber::registry().with(capture);
        tracing::subscriber::with_default(subscriber, || {
            tracing::warn!("ownership repair must not stop persistence");
            tracing::debug!(target: OWNERSHIP_TARGET, "ownership diagnostic");
        });
        assert!(
            load(&dir)
                .iter()
                .any(|entry| entry.message == "ownership repair must not stop persistence")
        );
        assert_eq!(std::fs::read_to_string(&target).unwrap(), "keep");
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn persisted_events_keep_ownership_across_rotation_and_restart() {
        let dir = std::env::temp_dir().join(format!("infinidysk-logs-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let metadata = dir.metadata().unwrap();
        let owner = if metadata.uid() == 0 {
            (1000, 1000)
        } else {
            (metadata.uid(), metadata.gid())
        };
        std::fs::write(dir.join(FILE_NAME), "").unwrap();
        for index in 1..=ROTATED {
            std::fs::write(rotated(&dir, index), "").unwrap();
        }
        let capture = Capture::persisted(&dir, owner).unwrap();
        capture.0.as_ref().unwrap().lock().unwrap().bytes = FILE_BYTES;
        let subscriber = tracing_subscriber::registry().with(capture);
        tracing::subscriber::with_default(subscriber, || {
            tracing::warn!("before the restart");
        });

        assert!(
            load(&dir)
                .iter()
                .any(|e| e.message == "before the restart" && e.run == Some(*RUN))
        );
        for path in [dir.clone(), dir.join(FILE_NAME)]
            .into_iter()
            .chain((1..=ROTATED).map(|index| rotated(&dir, index)))
        {
            let metadata = path.metadata().unwrap();
            assert_eq!((metadata.uid(), metadata.gid()), owner);
        }
        std::fs::remove_dir_all(dir).unwrap();
    }
}
