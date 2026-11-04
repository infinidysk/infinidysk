//! Backup files on disk: private part files, atomic writes, manifests and the listing.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Read as _, Write as _};
use std::os::unix::fs::OpenOptionsExt as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::UNIX_EPOCH;

use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};
use tokio_util::sync::CancellationToken;
use utoipa::ToSchema;

/// Read and write size for hashing, copying and uploads.
pub(super) const CHUNK: usize = 8 * 1024 * 1024;
pub(super) const GB: u64 = 1_000_000_000;
const RESERVE_MIN_BYTES: u64 = GB;
const RESERVE_PERCENT: u64 = 10;
const NAME_MAX: usize = 200;
pub(super) const MANIFEST_FORMAT: u32 = 1;
const SQLITE_MAGIC: &[u8; 16] = b"SQLite format 3\0";

/// A file that is deleted on drop unless [`Part::keep`] was called.
pub(super) struct Part(Option<PathBuf>);

impl Part {
    /// Creates an empty `0600` file; fails if `path` exists. Nothing secret is ever
    /// written with a wider mode, whatever the umask or the folder's mode.
    pub(super) fn create(path: PathBuf) -> io::Result<(Self, File)> {
        let file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(&path)?;
        Ok((Self(Some(path)), file))
    }

    /// Takes over an existing file, for example after a rename.
    pub(super) const fn adopt(path: PathBuf) -> Self {
        Self(Some(path))
    }

    pub(super) fn path(&self) -> &Path {
        self.0.as_deref().unwrap_or_else(|| Path::new(""))
    }

    pub(super) fn keep(mut self) -> PathBuf {
        self.0.take().unwrap_or_default()
    }
}

impl Drop for Part {
    fn drop(&mut self) {
        if let Some(path) = self.0.take() {
            let _ = fs::remove_file(path);
        }
    }
}

/// Counts filesystem steps so a test can stop a run after any one of them.
#[derive(Default)]
pub(super) struct Steps {
    #[cfg(test)]
    pub(super) at: Option<(usize, Fault)>,
    #[cfg(test)]
    pub(super) n: usize,
}

#[cfg(test)]
#[derive(Clone, Copy, Debug)]
pub(super) enum Fault {
    /// The operation returns an error; destructors run, as for a failure online.
    Error,
    /// `std::process::abort()`: no destructors, no unwinding; files stay exactly as written.
    Abort,
}

impl Steps {
    /// Runs one filesystem operation and counts it.
    #[cfg_attr(
        not(test),
        allow(clippy::unused_self, clippy::needless_pass_by_ref_mut)
    )] // Reason: the counter exists only in tests.
    pub(super) fn run<T>(&mut self, work: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        #[cfg(test)]
        {
            let n = self.n;
            self.n += 1;
            match self.at {
                Some((at, Fault::Error)) if at == n => {
                    return Err(io::Error::other(format!("injected failure at step {n}")));
                }
                Some((at, Fault::Abort)) if at == n => {
                    let _ = work();
                    std::process::abort();
                }
                _ => {}
            }
        }
        work()
    }
}

/// Why an atomic write failed.
#[derive(Debug)]
pub(super) enum WriteError {
    /// The rename did not happen; the old content (or no file) is still there.
    Unpublished(io::Error),
    /// The rename happened, but the folder sync failed.
    NotDurable(io::Error),
}

impl std::fmt::Display for WriteError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Unpublished(e) | Self::NotDurable(e) => e.fmt(f),
        }
    }
}

impl From<WriteError> for io::Error {
    fn from(error: WriteError) -> Self {
        match error {
            WriteError::Unpublished(e) | WriteError::NotDurable(e) => e,
        }
    }
}

/// `path` with `suffix` appended to its file name.
pub(super) fn with(path: &Path, suffix: &str) -> PathBuf {
    let mut s = path.as_os_str().to_owned();
    s.push(suffix);
    s.into()
}

fn parent(path: &Path) -> &Path {
    path.parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."))
}

pub(super) fn sync_dir(dir: &Path) -> io::Result<()> {
    File::open(dir)?.sync_all()
}

/// Removes `path`; a missing file is fine.
pub(super) fn remove(path: &Path) -> io::Result<()> {
    match fs::remove_file(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        other => other,
    }
}

/// Writes `bytes` to `path` through `path.tmp`: removes a leftover `.tmp` (the caller is
/// the only writer of `path`), `Part::create`, write, `sync_all`, rename, sync the folder.
pub(super) fn write_atomic(path: &Path, bytes: &[u8], steps: &mut Steps) -> Result<(), WriteError> {
    let tmp = with(path, ".tmp");
    let unpublished = WriteError::Unpublished;
    steps.run(|| remove(&tmp)).map_err(unpublished)?;
    let (part, mut file) = steps
        .run(|| Part::create(tmp.clone()))
        .map_err(unpublished)?;
    steps.run(|| file.write_all(bytes)).map_err(unpublished)?;
    steps.run(|| file.sync_all()).map_err(unpublished)?;
    drop(file);
    steps.run(|| fs::rename(&tmp, path)).map_err(unpublished)?;
    part.keep();
    steps
        .run(|| sync_dir(parent(path)))
        .map_err(WriteError::NotDurable)
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as = BackupKind)]
pub enum Kind {
    Scheduled,
    Manual,
    Uploaded,
    /// Put in the folder by hand.
    #[default]
    Added,
}

impl Kind {
    const fn id(self) -> &'static str {
        match self {
            Self::Scheduled => "scheduled",
            Self::Manual => "manual",
            Self::Uploaded => "uploaded",
            Self::Added => "added",
        }
    }
}

/// What a backup's `<name>.json` says about it. Readers default every field, so later
/// formats only add fields.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub(super) struct Manifest {
    pub format: u32,
    pub kind: Kind,
    pub created_ms: i64,
    pub app_version: Option<String>,
    pub schema_version: Option<i64>,
    pub items: Option<u64>,
    pub size_bytes: Option<u64>,
    pub sha256: Option<String>,
    pub pinned: bool,
    pub note: String,
    pub verified_ms: Option<i64>,
    pub verify_error: Option<String>,
    pub source_name: Option<String>,
}

pub(super) fn manifest_path(dir: &Path, name: &str) -> PathBuf {
    dir.join(format!("{name}.json"))
}

fn read_manifest(dir: &Path, name: &str) -> Option<Manifest> {
    let bytes = fs::read(manifest_path(dir, name)).ok()?;
    serde_json::from_slice(&bytes).ok()
}

fn write_manifest(dir: &Path, name: &str, manifest: &Manifest) -> io::Result<()> {
    let bytes = serde_json::to_vec_pretty(manifest).map_err(io::Error::other)?;
    write_atomic(&manifest_path(dir, name), &bytes, &mut Steps::default())?;
    Ok(())
}

/// One listed backup file.
#[derive(Clone, Debug)]
pub(super) struct Entry {
    pub name: String,
    pub path: PathBuf,
    pub size_bytes: u64,
    /// Shorter than its header says.
    pub incomplete: bool,
    pub manifest: Option<Manifest>,
    pub mtime_ms: i64,
}

impl Entry {
    pub(super) fn kind(&self) -> Kind {
        self.manifest.as_ref().map_or(Kind::Added, |m| m.kind)
    }

    pub(super) fn created_ms(&self) -> i64 {
        self.manifest
            .as_ref()
            .map_or(self.mtime_ms, |m| m.created_ms)
    }

    pub(super) fn pinned(&self) -> bool {
        self.manifest.as_ref().is_some_and(|m| m.pinned)
    }
}

/// Page size and in-header page count of a SQLite file; `None` when it is not one. The
/// count is 0 when the header does not vouch for it (bytes 24..28 differ from 92..96).
pub(super) fn header(bytes: &[u8; 100]) -> Option<(u64, u64)> {
    if !bytes.starts_with(SQLITE_MAGIC) {
        return None;
    }
    let page = match u16::from_be_bytes([bytes[16], bytes[17]]) {
        1 => 65_536,
        p if p >= 512 && p.is_power_of_two() => u64::from(p),
        _ => return None,
    };
    let pages = if bytes[24..28] == bytes[92..96] {
        u64::from(u32::from_be_bytes([
            bytes[28], bytes[29], bytes[30], bytes[31],
        ]))
    } else {
        0
    };
    Some((page, pages))
}

/// Whether the first bytes are a SQLite header's magic.
pub(super) fn is_sqlite(bytes: &[u8]) -> bool {
    bytes.starts_with(SQLITE_MAGIC)
}

/// Whether `name` follows the name rule.
pub(super) fn valid_name(name: &str) -> bool {
    name.len() <= NAME_MAX
        && name.len() > ".sqlite".len()
        && name.ends_with(".sqlite")
        && !name.starts_with('.')
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b' ' | b'.' | b'_' | b'-'))
}

/// A free name `infinidysk-<stamp>-<kind>[-n].sqlite` in `dir`.
pub(super) fn new_name(dir: &Path, kind: Kind) -> String {
    let stamp = jiff::Timestamp::now().strftime("%Y%m%dT%H%M%SZ");
    let base = format!("infinidysk-{stamp}-{}", kind.id());
    let taken = |name: &str| {
        dir.join(name).symlink_metadata().is_ok()
            || dir.join(format!(".{name}.part")).symlink_metadata().is_ok()
    };
    let mut name = format!("{base}.sqlite");
    let mut n = 2;
    while taken(&name) {
        name = format!("{base}-{n}.sqlite");
        n += 1;
    }
    name
}

pub(super) const fn reserve(bytes: u64) -> u64 {
    let r = bytes / 100 * RESERVE_PERCENT;
    if r > RESERVE_MIN_BYTES {
        r
    } else {
        RESERVE_MIN_BYTES
    }
}

/// `12.3 GB`, decimal.
pub(super) fn gb(bytes: u64) -> String {
    format!("{}.{} GB", bytes / GB, bytes % GB / (GB / 10))
}

/// `Err` says how much `place` has and needs. `bytes` excludes the reserve.
pub(super) fn room(free: Option<u64>, bytes: u64, place: &str, advice: &str) -> Result<(), String> {
    let reserve = reserve(bytes);
    let need = bytes.saturating_add(reserve);
    match free {
        Some(free) if free >= need => Ok(()),
        Some(free) => Err(format!(
            "{place} has {} free; this needs about {} ({} plus a {} reserve). {advice}",
            gb(free),
            gb(need),
            gb(bytes),
            gb(reserve)
        )),
        None => Err(format!(
            "Could not read the free space of {}.",
            place.to_lowercase()
        )),
    }
}

/// Backups to delete so `keep` scheduled and manual ones remain: unpinned, kind
/// `scheduled` or `manual`, not incomplete, past the newest `keep` by `created_ms`.
pub(super) fn prune_list(entries: &[Entry], keep: usize) -> Vec<&Entry> {
    let mut candidates: Vec<&Entry> = entries
        .iter()
        .filter(|e| matches!(e.kind(), Kind::Scheduled | Kind::Manual))
        .filter(|e| !e.pinned() && !e.incomplete)
        .collect();
    candidates.sort_by_key(|e| std::cmp::Reverse(e.created_ms()));
    candidates.into_iter().skip(keep).collect()
}

fn mtime_ms(meta: &fs::Metadata) -> i64 {
    meta.modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map_or(0, |d| i64::try_from(d.as_millis()).unwrap_or(i64::MAX))
}

fn entry(dir: &Path, name: &str) -> io::Result<Entry> {
    let path = dir.join(name);
    let meta = path.symlink_metadata()?;
    if !meta.is_file() {
        return Err(io::ErrorKind::NotFound.into());
    }
    let mut head = [0u8; 100];
    let mut read = 0;
    let mut file = File::open(&path)?;
    while read < head.len() {
        match file.read(&mut head[read..])? {
            0 => break,
            n => read += n,
        }
    }
    let size_bytes = meta.len();
    let incomplete = (read == head.len())
        .then(|| header(&head))
        .flatten()
        .is_some_and(|(page, pages)| pages > 0 && size_bytes < page.saturating_mul(pages));
    Ok(Entry {
        name: name.to_owned(),
        path,
        size_bytes,
        incomplete,
        manifest: read_manifest(dir, name),
        mtime_ms: mtime_ms(&meta),
    })
}

/// One backup by name; `NotFound` for a bad name or a missing or non-regular file.
pub(super) fn find(dir: &Path, name: &str) -> io::Result<Entry> {
    if !valid_name(name) {
        return Err(io::ErrorKind::NotFound.into());
    }
    entry(dir, name)
}

/// The backups in `dir`, newest first; an absent folder has none.
pub(super) fn entries(dir: &Path) -> io::Result<Vec<Entry>> {
    let read = match fs::read_dir(dir) {
        Ok(read) => read,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(e) => return Err(e),
    };
    let mut out = Vec::new();
    for item in read {
        let item = item?;
        let Some(name) = item.file_name().to_str().map(str::to_owned) else {
            continue;
        };
        if !valid_name(&name) {
            continue;
        }
        if let Ok(entry) = entry(dir, &name) {
            out.push(entry);
        }
    }
    out.sort_by_key(|e| std::cmp::Reverse(e.created_ms()));
    Ok(out)
}

/// Deletes leftover `.*.part` and `*.json.tmp` files and manifests whose file is gone.
pub(super) fn clean_up(dir: &Path) -> io::Result<()> {
    let read = match fs::read_dir(dir) {
        Ok(read) => read,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e),
    };
    for item in read {
        let item = item?;
        let Some(name) = item.file_name().to_str().map(str::to_owned) else {
            continue;
        };
        let leftover = (name.starts_with('.') && name.strip_suffix(".part").is_some())
            || name.ends_with(".json.tmp")
            || name
                .strip_suffix(".json")
                .is_some_and(|file| valid_name(file) && !dir.join(file).exists());
        if leftover && item.file_type()?.is_file() {
            remove(&item.path())?;
        }
    }
    Ok(())
}

/// Makes a finished `part` a listed backup: under the lock, writes `name`'s manifest,
/// then renames `part` to `name` and syncs the folder. `AlreadyExists` when `name` is
/// taken. For new backups and uploads only.
pub(super) async fn publish(
    lock: &tokio::sync::Mutex<()>,
    dir: &Path,
    part: Part,
    name: &str,
    manifest: &Manifest,
) -> io::Result<()> {
    let _held = lock.lock().await;
    let (dir, name, manifest) = (dir.to_owned(), name.to_owned(), manifest.clone());
    blocking(move || {
        let path = dir.join(&name);
        if path.symlink_metadata().is_ok() {
            return Err(io::ErrorKind::AlreadyExists.into());
        }
        write_manifest(&dir, &name, &manifest)?;
        if let Err(e) = fs::rename(part.path(), &path) {
            let _ = fs::remove_file(manifest_path(&dir, &name));
            return Err(e);
        }
        part.keep();
        sync_dir(&dir)
    })
    .await
}

/// For an existing backup: re-reads its manifest under the lock (or starts an `added`
/// one), applies `change`, and writes it. `NotFound` when the file is gone.
pub(super) async fn update_manifest(
    lock: &tokio::sync::Mutex<()>,
    dir: &Path,
    name: &str,
    change: impl FnOnce(&mut Manifest) + Send + 'static,
) -> io::Result<Manifest> {
    let _held = lock.lock().await;
    let (dir, name) = (dir.to_owned(), name.to_owned());
    blocking(move || {
        let entry = find(&dir, &name)?;
        let mut manifest = entry.manifest.unwrap_or_else(|| Manifest {
            format: MANIFEST_FORMAT,
            kind: Kind::Added,
            created_ms: entry.mtime_ms,
            size_bytes: Some(entry.size_bytes),
            ..Manifest::default()
        });
        change(&mut manifest);
        write_manifest(&dir, &name, &manifest)?;
        Ok(manifest)
    })
    .await
}

/// Deletes one backup and its manifest under the lock; `NotFound` when it is gone.
pub(super) async fn delete(
    lock: &tokio::sync::Mutex<()>,
    dir: &Path,
    name: &str,
) -> io::Result<()> {
    let _held = lock.lock().await;
    let (dir, name) = (dir.to_owned(), name.to_owned());
    blocking(move || {
        let entry = find(&dir, &name)?;
        fs::remove_file(&entry.path)?;
        remove(&manifest_path(&dir, &name))
    })
    .await
}

/// Lists the folder under the lock, picks `prune_list(.., keep)` from those fresh
/// manifests, and deletes each file and manifest before releasing it. Never deletes
/// `written`, the backup just made, even when the clock went back. Returns (count, bytes).
pub(super) async fn prune(
    lock: &tokio::sync::Mutex<()>,
    dir: &Path,
    keep: usize,
    written: &str,
) -> io::Result<(u64, u64)> {
    let _held = lock.lock().await;
    let (dir, written) = (dir.to_owned(), written.to_owned());
    blocking(move || {
        let entries = entries(&dir)?;
        let (mut count, mut bytes) = (0, 0);
        for entry in prune_list(&entries, keep) {
            if entry.name == written {
                continue;
            }
            fs::remove_file(&entry.path)?;
            remove(&manifest_path(&dir, &entry.name))?;
            count += 1;
            bytes += entry.size_bytes;
        }
        Ok((count, bytes))
    })
    .await
}

pub(super) async fn blocking<T: Send + 'static>(
    work: impl FnOnce() -> io::Result<T> + Send + 'static,
) -> io::Result<T> {
    tokio::task::spawn_blocking(work)
        .await
        .map_err(io::Error::other)?
}

pub(super) fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    bytes.iter().fold(String::with_capacity(64), |mut s, b| {
        let _ = write!(s, "{b:02x}");
        s
    })
}

/// SHA-256 of `path` in [`CHUNK`] reads; adds each chunk to `progress`, stops on `cancel`.
pub(super) fn hash(
    path: &Path,
    cancel: &CancellationToken,
    progress: &AtomicU64,
) -> io::Result<[u8; 32]> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; CHUNK];
    loop {
        if cancel.is_cancelled() {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        progress.fetch_add(n as u64, Ordering::Relaxed);
    }
    Ok(hasher.finalize().into())
}

/// Creates `dst` with `Part::create` (0600 before any byte), copies `src` into it in
/// CHUNK blocks and returns it synced, with its SHA-256. `progress(done)` returning false
/// stops it.
pub(super) fn copy_to_part(
    src: &Path,
    dst: PathBuf,
    progress: &mut dyn FnMut(u64) -> bool,
) -> io::Result<(Part, [u8; 32])> {
    let mut from = File::open(src)?;
    let (part, mut to) = Part::create(dst)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; CHUNK];
    let mut done = 0u64;
    loop {
        let n = from.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        to.write_all(&buf[..n])?;
        done += n as u64;
        if !progress(done) {
            return Err(io::ErrorKind::Interrupted.into());
        }
    }
    to.sync_all()?;
    Ok((part, hasher.finalize().into()))
}

/// Sets the owner; a failure is logged once (dev machines run unprivileged).
pub(super) fn own(path: &Path, owner: (u32, u32)) {
    if let Err(error) = std::os::unix::fs::chown(path, Some(owner.0), Some(owner.1)) {
        tracing::warn!(path = %path.display(), reason = %error, "could not set the owner of a backup file");
    }
}

/// 1.x backup folders under `<config dir>/backups`.
#[derive(Serialize, ToSchema)]
#[schema(as = LegacyBackups)]
pub struct LegacyView {
    folders: u64,
    bytes: u64,
}

fn legacy_folders(config_dir: &Path) -> Vec<PathBuf> {
    let Ok(read) = fs::read_dir(config_dir.join("backups")) else {
        return Vec::new();
    };
    read.flatten()
        .filter(|item| item.file_type().is_ok_and(|t| t.is_dir()))
        .filter(|item| {
            let path = item.path();
            item.file_name()
                .to_str()
                .is_some_and(|n| n.starts_with(".tmp-"))
                || ["manifest.json", "db.sql", "warden.sql"]
                    .iter()
                    .any(|f| path.join(f).symlink_metadata().is_ok())
        })
        .map(|item| item.path())
        .collect()
}

fn files_bytes(dir: &Path, depth: u8) -> u64 {
    let Ok(read) = fs::read_dir(dir) else {
        return 0;
    };
    read.flatten()
        .filter_map(|item| {
            let meta = item.path().symlink_metadata().ok()?;
            if meta.is_file() {
                Some(meta.len())
            } else if meta.is_dir() && depth > 1 {
                Some(files_bytes(&item.path(), depth - 1))
            } else {
                None
            }
        })
        .sum()
}

pub(super) fn legacy(config_dir: &Path) -> Option<LegacyView> {
    let folders = legacy_folders(config_dir);
    if folders.is_empty() {
        return None;
    }
    Some(LegacyView {
        folders: folders.len() as u64,
        bytes: folders.iter().map(|f| files_bytes(f, 2)).sum(),
    })
}

pub(super) fn delete_legacy(config_dir: &Path) -> io::Result<u64> {
    let folders = legacy_folders(config_dir);
    for folder in &folders {
        fs::remove_dir_all(folder)?;
    }
    Ok(folders.len() as u64)
}
