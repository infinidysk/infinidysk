//! 1.x data an upgraded install no longer reads: what is still on disk and about how
//! much space it takes. Read-only; deleting it is up to the user.

use std::collections::HashSet;
use std::fs;
use std::os::unix::fs::MetadataExt as _;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use axum::Json;
use axum::extract::State;
use axum::http::StatusCode;
use infinidysk_import::Source;
use serde::Serialize;
use utoipa::ToSchema;

use super::{DOWNLOAD_IDS_KEY, SKIPPED_RETRIED_KEY, STATE_KEY};
use crate::admin::{Admin, Fail};

/// Config key, set once the admin has dismissed the Overview notice.
const DISMISSED_KEY: &str = "upgrade.leftovers_dismissed";
/// 1.x files and folders in its `/config`.
const CANDIDATES: [&str; 5] = [
    "db.sqlite",
    "db.sqlite-wal",
    "db.sqlite-shm",
    "db.sqlite-journal",
    "blobs",
];

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "lowercase")]
pub enum LeftoverSource {
    Sqlite,
    Postgres,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum LeftoverStatus {
    /// On disk and measured.
    Present,
    /// On disk but not measured; see the reason.
    NotAssessed,
    Missing,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum LeftoverReason {
    /// It is, holds or sits in a file or folder 2.x uses.
    LiveData,
    Symlink,
    /// Its type or identity could not be read.
    Unreadable,
    /// On disk now, but not when last measured.
    Changed,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct Leftover {
    /// A path inside this installation.
    pub path: String,
    pub status: LeftoverStatus,
    /// Why it was not measured.
    pub reason: Option<LeftoverReason>,
    /// Estimated disk usage: allocated blocks, hard links counted once.
    pub bytes: Option<u64>,
    /// `false` when part of it could not be read; `bytes` is then too low.
    pub complete: bool,
}

#[derive(Clone, Debug, Serialize, ToSchema)]
pub struct LeftoverScan {
    /// Unix milliseconds.
    pub at_ms: i64,
    pub items: Vec<Leftover>,
    /// Estimated disk usage of every measured item.
    pub bytes: u64,
    /// Every item is missing.
    pub gone: bool,
}

#[derive(Default, Serialize, ToSchema)]
pub struct LeftoversView {
    /// This install finished an import from 1.x; everything else is empty when not.
    pub imported: bool,
    /// Where 1.x kept its database, as far as the environment and `/config` still tell.
    pub source: Option<LeftoverSource>,
    /// Why infinidysk still reads the 1.x data; absent once it no longer does.
    pub in_use: Option<String>,
    /// Some 1.x data is still on disk; a quick check without sizes.
    pub present: bool,
    pub dismissed: bool,
    /// The last measurement since startup.
    pub scan: Option<LeftoverScan>,
    /// Why the last measurement failed; `scan` is then older.
    pub scan_error: Option<String>,
    /// Unix milliseconds.
    pub scan_failed_at_ms: Option<i64>,
}

#[derive(Clone, Default)]
struct Last {
    scan: Option<LeftoverScan>,
    error: Option<(String, i64)>,
    /// Measurements finished, failed ones included.
    runs: u64,
}

/// The 1.x data in `config_dir`, and the last measurement of it.
pub struct Leftovers {
    config_dir: PathBuf,
    /// 2.x files and folders that do not change while running.
    fixed: Vec<PathBuf>,
    measuring: tokio::sync::Mutex<()>,
    last: Mutex<Last>,
}

impl Leftovers {
    /// `database` is the live SQLite file, `None` on PostgreSQL.
    pub fn new(
        config_dir: PathBuf,
        database: Option<&Path>,
        metrics: &Path,
        log_dir: PathBuf,
    ) -> Self {
        let mut fixed = Vec::new();
        for file in database.into_iter().chain([metrics]) {
            fixed.extend(with_sidecars(file));
        }
        fixed.push(log_dir);
        Self {
            config_dir,
            fixed,
            measuring: tokio::sync::Mutex::new(()),
            last: Mutex::default(),
        }
    }

    fn last(&self) -> MutexGuard<'_, Last> {
        self.last.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn live(&self, more: impl IntoIterator<Item = PathBuf>) -> Vec<PathBuf> {
        self.fixed.iter().cloned().chain(more).collect()
    }

    /// Whether any 1.x item is on disk and not 2.x data, and the last measurement with
    /// its items checked again against `live`; no new sizes.
    async fn check(&self, live: Vec<PathBuf>) -> (bool, Option<LeftoverScan>) {
        let dir = self.config_dir.clone();
        let scan = self.last().scan.clone();
        tokio::task::spawn_blocking(move || {
            let live = identities(&live);
            let present = CANDIDATES
                .iter()
                .any(|name| matches!(classify(&dir.join(name), &live), Class::Present(_)));
            (present, scan.map(|s| recheck(s, &live)))
        })
        .await
        .unwrap_or((false, None))
    }

    /// Measures the 1.x data, or waits for the measurement already running. The
    /// measurement finishes and is kept even when the caller stops waiting.
    async fn measure(self: Arc<Self>, live: Vec<PathBuf>) {
        let seen = self.last().runs;
        let task = tokio::spawn(async move {
            let _measuring = self.measuring.lock().await;
            if self.last().runs != seen {
                return;
            }
            let dir = self.config_dir.clone();
            let measured = tokio::task::spawn_blocking(move || scan(&dir, &live)).await;
            let mut last = self.last();
            last.runs += 1;
            match measured {
                Ok(scan) => {
                    last.scan = Some(scan);
                    last.error = None;
                }
                Err(error) => {
                    tracing::warn!(reason = %error, "could not measure the 1.x data");
                    last.error = Some((
                        "The measurement stopped unexpectedly; see the logs.".to_owned(),
                        infinidysk_db::now_ms(),
                    ));
                }
            }
        });
        if let Err(error) = task.await {
            tracing::warn!(reason = %error, "could not measure the 1.x data");
        }
    }
}

fn with_sidecars(file: &Path) -> impl Iterator<Item = PathBuf> + use<> {
    let base = file.as_os_str().to_owned();
    ["", "-wal", "-shm", "-journal"]
        .into_iter()
        .map(move |suffix| {
            let mut path = base.clone();
            path.push(suffix);
            PathBuf::from(path)
        })
}

/// A path resolved through symlinks, with its file identity when it exists.
struct Identity {
    path: PathBuf,
    id: Option<(u64, u64)>,
}

/// `paths` resolved as far as they exist: a missing file still has a resolved folder.
fn identities(paths: &[PathBuf]) -> Vec<Identity> {
    paths
        .iter()
        .map(|p| {
            let path = fs::canonicalize(p).unwrap_or_else(|_| {
                match (p.parent().map(fs::canonicalize), p.file_name()) {
                    (Some(Ok(parent)), Some(name)) => parent.join(name),
                    _ => p.clone(),
                }
            });
            let id = fs::metadata(p).ok().map(|m| (m.dev(), m.ino()));
            Identity { path, id }
        })
        .collect()
}

enum Class {
    Present(fs::Metadata),
    NotAssessed(LeftoverReason),
    Missing,
}

fn classify(path: &Path, live: &[Identity]) -> Class {
    let meta = match fs::symlink_metadata(path) {
        Ok(meta) => meta,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Class::Missing,
        Err(_) => return Class::NotAssessed(LeftoverReason::Unreadable),
    };
    if meta.file_type().is_symlink() {
        return Class::NotAssessed(LeftoverReason::Symlink);
    }
    let Ok(real) = fs::canonicalize(path) else {
        return Class::NotAssessed(LeftoverReason::Unreadable);
    };
    let id = (meta.dev(), meta.ino());
    let overlaps =
        |l: &Identity| l.id == Some(id) || l.path.starts_with(&real) || real.starts_with(&l.path);
    if live.iter().any(overlaps) {
        return Class::NotAssessed(LeftoverReason::LiveData);
    }
    Class::Present(meta)
}

/// Measures each 1.x item in `config_dir` that is not one of the `live` 2.x paths.
fn scan(config_dir: &Path, live: &[PathBuf]) -> LeftoverScan {
    let live = identities(live);
    let mut linked = HashSet::new();
    let items: Vec<Leftover> = CANDIDATES
        .iter()
        .map(|name| {
            let path = config_dir.join(name);
            let shown = path.display().to_string();
            match classify(&path, &live) {
                Class::Missing => Leftover {
                    path: shown,
                    status: LeftoverStatus::Missing,
                    reason: None,
                    bytes: None,
                    complete: true,
                },
                Class::NotAssessed(reason) => Leftover {
                    path: shown,
                    status: LeftoverStatus::NotAssessed,
                    reason: Some(reason),
                    bytes: None,
                    complete: false,
                },
                Class::Present(meta) => {
                    let (bytes, complete) = usage(&path, &meta, &mut linked);
                    Leftover {
                        path: shown,
                        status: LeftoverStatus::Present,
                        reason: None,
                        bytes: Some(bytes),
                        complete,
                    }
                }
            }
        })
        .collect();
    LeftoverScan {
        at_ms: infinidysk_db::now_ms(),
        bytes: items.iter().filter_map(|i| i.bytes).sum(),
        gone: items.iter().all(|i| i.status == LeftoverStatus::Missing),
        items,
    }
}

/// `scan` with each item classified again: settings may have moved a 2.x folder into it,
/// or files may have come or gone since the measurement. Only measured items keep sizes.
fn recheck(mut scan: LeftoverScan, live: &[Identity]) -> LeftoverScan {
    for item in &mut scan.items {
        let (status, reason) = match classify(Path::new(&item.path), live) {
            Class::Present(_) if item.status == LeftoverStatus::Present => continue,
            Class::Present(_) => (LeftoverStatus::NotAssessed, Some(LeftoverReason::Changed)),
            Class::Missing => (LeftoverStatus::Missing, None),
            Class::NotAssessed(reason) => (LeftoverStatus::NotAssessed, Some(reason)),
        };
        item.status = status;
        item.reason = reason;
        item.bytes = None;
        item.complete = status == LeftoverStatus::Missing;
    }
    scan.bytes = scan.items.iter().filter_map(|i| i.bytes).sum();
    scan.gone = scan
        .items
        .iter()
        .all(|i| i.status == LeftoverStatus::Missing);
    scan
}

/// Allocated bytes below `root`, without following symlinks; each hard-linked file once
/// across calls sharing `linked`. `false` when something could not be read.
fn usage(root: &Path, meta: &fs::Metadata, linked: &mut HashSet<(u64, u64)>) -> (u64, bool) {
    let mut complete = true;
    let mut total = 0u64;
    let mut add = |meta: &fs::Metadata| {
        if meta.nlink() < 2 || meta.is_dir() || linked.insert((meta.dev(), meta.ino())) {
            total += meta.blocks() * 512;
        }
    };
    add(meta);
    if !meta.is_dir() {
        return (total, complete);
    }
    let mut dirs = vec![root.to_owned()];
    while let Some(dir) = dirs.pop() {
        let Ok(read) = fs::read_dir(&dir) else {
            complete = false;
            continue;
        };
        for entry in read {
            let Ok(meta) = entry.and_then(|e| Ok((e.path(), e.metadata()?))) else {
                complete = false;
                continue;
            };
            add(&meta.1);
            if meta.1.is_dir() {
                dirs.push(meta.0);
            }
        }
    }
    (total, complete)
}

fn source(config_dir: &Path) -> LeftoverSource {
    let vars: Vec<(String, String)> = std::env::vars().collect();
    match infinidysk_import::source(config_dir, &vars) {
        Ok(Source::Sqlite) => LeftoverSource::Sqlite,
        Ok(Source::Postgres(_))
        | Err(
            infinidysk_import::Error::NoConnectionString
            | infinidysk_import::Error::PostgresNotConfigured(_),
        ) => LeftoverSource::Postgres,
        Ok(Source::None) | Err(_) => LeftoverSource::Unknown,
    }
}

/// Why the one-time passes after the import still need the 1.x data.
fn in_use(has: impl Fn(&str) -> bool) -> Option<String> {
    let mut why = Vec::new();
    if !has(DOWNLOAD_IDS_KEY) {
        why.push("Finding the downloads of 1.x folders has not finished; it runs again at the next start.");
    }
    if !has(SKIPPED_RETRIED_KEY) {
        why.push(
            "Bringing back files the 1.x import skipped has not finished; it runs again at each \
             start and needs your library folders in the settings.",
        );
    }
    (!why.is_empty()).then(|| why.join(" "))
}

async fn view(admin: &Admin, measure: bool) -> Result<LeftoversView, Fail> {
    let config = admin.queue.db().config().await?;
    let has = |key: &str| config.iter().any(|(k, _)| k == key);
    if !config
        .iter()
        .any(|(k, v)| k == STATE_KEY && v == "\"done\"")
    {
        return Ok(LeftoversView::default());
    }
    let leftovers = &admin.leftovers;
    let mut more: Vec<PathBuf> = {
        let settings = admin.config.read().await.settings().clone();
        let cache =
            crate::app::effective_tuning(&settings.performance, admin.live.default_cache_dir())
                .disk_cache_dir;
        [cache, settings.imports.strm_dir.clone()]
            .into_iter()
            .chain(settings.imports.library_dirs.iter().map(|d| d.path.clone()))
            .map(PathBuf::from)
            .collect()
    };
    more.extend(admin.backups.resolve_dir().await.ok());
    let live = leftovers.live(more);
    if measure {
        Arc::clone(leftovers).measure(live.clone()).await;
    }
    let (present, scan) = leftovers.check(live).await;
    let last = leftovers.last().clone();
    let config_dir = leftovers.config_dir.clone();
    Ok(LeftoversView {
        imported: true,
        source: Some(
            tokio::task::spawn_blocking(move || source(&config_dir))
                .await
                .unwrap_or(LeftoverSource::Unknown),
        ),
        in_use: in_use(has),
        present,
        dismissed: has(DISMISSED_KEY),
        scan,
        scan_error: last.error.as_ref().map(|(e, _)| e.clone()),
        scan_failed_at_ms: last.error.map(|(_, at)| at),
    })
}

/// The 1.x data still on disk after an import from 1.x, with the last measurement.
#[utoipa::path(get, path = "/api/v1/upgrade/leftovers", tag = "upgrade",
    responses((status = 200, body = LeftoversView)))]
pub(crate) async fn get_leftovers(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LeftoversView>, Fail> {
    Ok(Json(view(&admin, false).await?))
}

/// Measures the 1.x data again, or waits for the measurement already running.
#[utoipa::path(post, path = "/api/v1/upgrade/leftovers/scan", tag = "upgrade",
    responses((status = 200, body = LeftoversView)))]
pub(crate) async fn post_leftovers_scan(
    State(admin): State<Arc<Admin>>,
) -> Result<Json<LeftoversView>, Fail> {
    Ok(Json(view(&admin, true).await?))
}

/// Hides the Overview notice about 1.x data; the System page still lists it.
#[utoipa::path(post, path = "/api/v1/upgrade/leftovers/dismiss", tag = "upgrade",
    responses((status = 204)))]
pub(crate) async fn post_leftovers_dismiss(
    State(admin): State<Arc<Admin>>,
) -> Result<StatusCode, Fail> {
    admin
        .queue
        .db()
        .write_config(&[(DISMISSED_KEY, "true")], &[])
        .await?;
    Ok(StatusCode::NO_CONTENT)
}

#[cfg(test)]
mod tests {
    use std::os::unix::fs::PermissionsExt as _;

    use super::*;

    fn dir() -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "infinidysk-leftovers-{}",
            infinidysk_core::ItemId::new()
        ));
        fs::create_dir_all(dir.join("blobs/ab")).unwrap();
        fs::write(dir.join("db.sqlite"), vec![1u8; 64 * 1024]).unwrap();
        fs::write(dir.join("blobs/ab/one"), vec![2u8; 64 * 1024]).unwrap();
        dir
    }

    fn item<'a>(scan: &'a LeftoverScan, name: &str) -> &'a Leftover {
        scan.items.iter().find(|i| i.path.ends_with(name)).unwrap()
    }

    fn allocated(path: &Path) -> u64 {
        fs::symlink_metadata(path).unwrap().blocks() * 512
    }

    #[test]
    fn measures_the_1x_files_and_folders() {
        let dir = dir();
        let scan = scan(&dir, &[]);
        let db = item(&scan, "db.sqlite");
        assert_eq!(db.status, LeftoverStatus::Present);
        assert_eq!(db.bytes, Some(allocated(&dir.join("db.sqlite"))));
        let blobs = item(&scan, "blobs");
        assert!(blobs.complete);
        assert!(blobs.bytes.unwrap() >= allocated(&dir.join("blobs/ab/one")));
        assert_eq!(item(&scan, "db.sqlite-wal").status, LeftoverStatus::Missing);
        assert!(!scan.gone);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn data_2x_uses_is_never_measured_and_keeps_the_section() {
        let dir = dir();
        // 2.x pointed at the 1.x database file, and its cache inside `blobs`.
        let live = [dir.join("db.sqlite"), dir.join("blobs/ab")];
        let scan = scan(&dir, &live);
        for name in ["db.sqlite", "blobs"] {
            let i = item(&scan, name);
            assert_eq!(i.status, LeftoverStatus::NotAssessed, "{name}");
            assert_eq!(i.reason, Some(LeftoverReason::LiveData), "{name}");
            assert_eq!(i.bytes, None);
        }
        assert_eq!(scan.bytes, 0);
        assert!(!scan.gone);

        // A hard link to the 1.x database is the same file under another name.
        let elsewhere = dir.join("live.sqlite");
        fs::hard_link(dir.join("db.sqlite"), &elsewhere).unwrap();
        let scan = super::scan(&dir, &[elsewhere]);
        assert_eq!(
            item(&scan, "db.sqlite").reason,
            Some(LeftoverReason::LiveData)
        );
        // Only the parent of a 2.x file: still measured.
        let scan = super::scan(&dir, &[dir.join("infinidysk.sqlite")]);
        assert_eq!(item(&scan, "db.sqlite").status, LeftoverStatus::Present);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn a_2x_folder_moved_into_a_measured_item_is_not_counted_any_more() {
        let dir = dir();
        let measured = scan(&dir, &[]);
        assert_eq!(item(&measured, "blobs").status, LeftoverStatus::Present);
        // Backups moved into `blobs` after the measurement.
        let scan = recheck(measured, &identities(&[dir.join("blobs/backups")]));
        let blobs = item(&scan, "blobs");
        assert_eq!(blobs.status, LeftoverStatus::NotAssessed);
        assert_eq!(blobs.reason, Some(LeftoverReason::LiveData));
        assert_eq!(blobs.bytes, None);
        assert_eq!(scan.bytes, allocated(&dir.join("db.sqlite")));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn items_that_came_or_went_since_the_measurement_are_rechecked() {
        let dir = dir();
        fs::remove_dir_all(dir.join("blobs")).unwrap();
        let measured = scan(&dir, &[]);
        fs::create_dir(dir.join("blobs")).unwrap();
        fs::remove_file(dir.join("db.sqlite")).unwrap();
        let scan = recheck(measured, &[]);
        let blobs = item(&scan, "blobs");
        assert_eq!(blobs.reason, Some(LeftoverReason::Changed));
        assert_eq!(blobs.bytes, None);
        assert_eq!(item(&scan, "db.sqlite").status, LeftoverStatus::Missing);
        assert!(!scan.gone);

        // An item 2.x used, deleted since: nothing is left.
        let measured = super::scan(&dir, &[dir.join("blobs")]);
        fs::remove_dir_all(dir.join("blobs")).unwrap();
        assert!(recheck(measured, &[]).gone);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn a_symlinked_item_is_not_followed() {
        let dir = dir();
        let target = dir.join("elsewhere");
        fs::rename(dir.join("blobs"), &target).unwrap();
        std::os::unix::fs::symlink(&target, dir.join("blobs")).unwrap();
        let scan = scan(&dir, &[]);
        let blobs = item(&scan, "blobs");
        assert_eq!(blobs.status, LeftoverStatus::NotAssessed);
        assert_eq!(blobs.reason, Some(LeftoverReason::Symlink));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn hard_links_count_once() {
        let dir = dir();
        fs::hard_link(dir.join("blobs/ab/one"), dir.join("blobs/two")).unwrap();
        fs::hard_link(dir.join("db.sqlite"), dir.join("blobs/three")).unwrap();
        let scan = scan(&dir, &[]);
        let file = allocated(&dir.join("blobs/ab/one"));
        let folders = allocated(&dir.join("blobs")) + allocated(&dir.join("blobs/ab"));
        assert_eq!(item(&scan, "blobs").bytes, Some(file + folders));
        assert_eq!(
            scan.bytes,
            file + folders + allocated(&dir.join("db.sqlite"))
        );
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn an_unreadable_folder_makes_a_partial_measurement() {
        let dir = dir();
        let locked = dir.join("blobs/ab");
        fs::set_permissions(&locked, fs::Permissions::from_mode(0o000)).unwrap();
        // Root reads it anyway.
        let readable = fs::read_dir(&locked).is_ok();
        let scan = scan(&dir, &[]);
        fs::set_permissions(&locked, fs::Permissions::from_mode(0o700)).unwrap();
        fs::remove_dir_all(&dir).unwrap();
        if readable {
            return;
        }
        let blobs = item(&scan, "blobs");
        assert_eq!(blobs.status, LeftoverStatus::Present);
        assert!(!blobs.complete);
        assert!(!scan.gone);
    }

    #[test]
    fn an_unreadable_identity_is_not_assessed() {
        let dir = dir();
        fs::set_permissions(&dir, fs::Permissions::from_mode(0o600)).unwrap();
        let readable = fs::symlink_metadata(dir.join("db.sqlite")).is_ok();
        let scan = scan(&dir, &[]);
        fs::set_permissions(&dir, fs::Permissions::from_mode(0o700)).unwrap();
        fs::remove_dir_all(&dir).unwrap();
        if readable {
            return;
        }
        let db = item(&scan, "db.sqlite");
        assert_eq!(db.status, LeftoverStatus::NotAssessed);
        assert_eq!(db.reason, Some(LeftoverReason::Unreadable));
        assert!(!scan.gone);
    }

    #[test]
    fn only_a_scan_finding_nothing_is_gone() {
        let dir = dir();
        fs::remove_file(dir.join("db.sqlite")).unwrap();
        fs::remove_dir_all(dir.join("blobs")).unwrap();
        let scan = scan(&dir, &[]);
        assert!(scan.gone);
        assert_eq!(scan.bytes, 0);
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn pending_passes_say_why() {
        assert!(in_use(|_| true).is_none());
        let why = in_use(|k| k != SKIPPED_RETRIED_KEY).unwrap();
        assert!(why.contains("skipped"));
        assert!(!why.contains("downloads"));
        let why = in_use(|_| false).unwrap();
        assert!(why.contains("downloads") && why.contains("skipped"));
    }

    #[test]
    fn the_source_is_unknown_without_a_1x_database() {
        let dir = std::env::temp_dir().join(format!(
            "infinidysk-leftovers-{}",
            infinidysk_core::ItemId::new()
        ));
        fs::create_dir_all(&dir).unwrap();
        // Reads the real environment; a 1.x PostgreSQL setting there changes the answer.
        if std::env::var_os("DATABASE_PROVIDER").is_none() {
            assert_eq!(source(&dir), LeftoverSource::Unknown);
            fs::write(dir.join("db.sqlite"), b"").unwrap();
            assert_eq!(source(&dir), LeftoverSource::Sqlite);
        }
        fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn concurrent_measurements_share_one_run() {
        let dir = dir();
        let leftovers = Arc::new(Leftovers::new(
            dir.clone(),
            None,
            &dir.join("metrics.sqlite"),
            dir.join("logs"),
        ));
        let live = leftovers.live([]);
        tokio::join!(
            Arc::clone(&leftovers).measure(live.clone()),
            Arc::clone(&leftovers).measure(live.clone())
        );
        assert_eq!(leftovers.last().runs, 1);
        Arc::clone(&leftovers).measure(live).await;
        assert_eq!(leftovers.last().runs, 2);
        assert!(leftovers.last().scan.is_some());
        fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_measurement_the_caller_stopped_waiting_for_still_finishes() {
        use std::future::Future as _;

        let dir = dir();
        let leftovers = Arc::new(Leftovers::new(
            dir.clone(),
            None,
            &dir.join("metrics.sqlite"),
            dir.join("logs"),
        ));
        let mut request = Box::pin(Arc::clone(&leftovers).measure(leftovers.live([])));
        std::future::poll_fn(|cx| {
            assert!(request.as_mut().poll(cx).is_pending());
            std::task::Poll::Ready(())
        })
        .await;
        drop(request);
        tokio::time::timeout(std::time::Duration::from_secs(10), async {
            while leftovers.last().runs == 0 {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        assert!(leftovers.last().scan.is_some());
        fs::remove_dir_all(dir).unwrap();
    }
}
