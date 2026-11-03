//! Hands finished jobs to the Arrs (`infinidysk-lab:docs/strm-support-proposal.md` §4.2): a
//! folder in `/completed-symlinks`, or STRM files plus release subtitles in the completed STRM
//! directory. The daily cleanup removes what no Arr took.

use std::collections::HashMap;
use std::fs::{self, DirBuilder};
use std::io;
use std::os::unix::fs::{DirBuilderExt as _, MetadataExt as _};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, PoisonError, RwLock};
use std::time::Duration;

use infinidysk_core::{FileMeta, ItemId, media};
use infinidysk_db::{Db, Handoff};
use infinidysk_queue::{HandoffJob, HandoffWriter, Written};
use infinidysk_vfs::Vfs;
use tokio::io::AsyncReadExt as _;
use tokio::sync::oneshot;
use tracing::{info, warn};

use crate::config::{ImportStrategy, Settings};
use crate::confined::{Entry, Root};

/// Release subtitles written beside the STRMs; Arrs import them with the video.
const SUBTITLES: [&str; 7] = ["srt", "ass", "ssa", "vtt", "sub", "idx", "sup"];
pub const MAX_SUBTITLE_BYTES: u64 = 10_000_000;
/// Anything larger is not one of our STRMs.
pub const MAX_STRM_BYTES: u64 = 8192;
/// Progress sampling window for completed STRM directory calls.
const FS_TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Clone)]
struct Config {
    strategy: ImportStrategy,
    strm_dir: String,
    public_url: String,
    strm_key: String,
    mount_dir: String,
}

impl Config {
    fn new(s: &Settings) -> Self {
        Self {
            strategy: s.imports.strategy,
            strm_dir: s.imports.strm_dir.trim_end_matches('/').to_owned(),
            public_url: s.network.public_url.clone(),
            strm_key: s.imports.strm_key.0.clone(),
            mount_dir: s.mount.dir.trim_end_matches('/').to_owned(),
        }
    }

    /// The STRM playback URL of `item`, C#'s format.
    fn strm_url(&self, item: ItemId, ext: &str) -> String {
        strm_url(&self.public_url, &self.strm_key, item, ext)
    }
}

/// The STRM playback URL of `item` at `public_url`, signed with `key`, C#'s format.
pub fn strm_url(public_url: &str, key: &str, item: ItemId, ext: &str) -> String {
    let ids = item.ids_path();
    let key = infinidysk_webdav::strm_key(key, &ids);
    format!("{public_url}/view/{ids}?downloadKey={key}&extension={ext}")
}

/// Writes each job's handoff with the settings current at the time.
pub struct Writer {
    vfs: Arc<Vfs>,
    /// `PUID`/`PGID`: owner of what we write when running as root, so Arrs can move it.
    owner: (u32, u32),
    config: RwLock<Config>,
    /// Held while a call into the completed STRM directory runs, even after the job gave up
    /// on it, so a dead share ties up one blocking thread at most.
    busy: Arc<tokio::sync::Mutex<()>>,
    /// `busy` for a folder settings would switch to, so a hung one leaves handoffs alone.
    probing: Arc<tokio::sync::Mutex<()>>,
}

impl Writer {
    pub fn new(vfs: Arc<Vfs>, owner: (u32, u32), settings: &Settings) -> Self {
        Self {
            vfs,
            owner,
            config: RwLock::new(Config::new(settings)),
            busy: Arc::default(),
            probing: Arc::default(),
        }
    }

    pub fn configure(&self, settings: &Settings) {
        *self.config.write().unwrap_or_else(PoisonError::into_inner) = Config::new(settings);
    }

    /// Checks that STRM files can be written in `dir`, creating it if missing.
    pub async fn probe(&self, dir: &str) -> Result<(), String> {
        let (dir, owner) = (PathBuf::from(dir), self.owner);
        let gate = if dir == Path::new(&self.config().strm_dir) {
            &self.busy
        } else {
            &self.probing
        };
        bounded(
            gate,
            FS_TIMEOUT,
            move |_| {
                let probed = (|| -> io::Result<()> {
                    make_dir(&dir, owner)?;
                    let probe = dir.join(format!(".infinidysk-probe-{}", ItemId::new()));
                    fs::write(&probe, b"")?;
                    fs::remove_file(&probe)
                })();
                probed.map_err(|e| e.to_string())
            },
            |()| {},
        )
        .await
    }

    fn config(&self) -> Config {
        self.config
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    async fn hand_off(&self, job: HandoffJob<'_>) -> Result<Written, String> {
        let c = self.config();
        let needs_mount = job
            .kinds
            .iter()
            .any(|k| matches!(k, media::Kind::Audio | media::Kind::Book));
        if c.strategy == ImportStrategy::Symlinks || needs_mount {
            return Ok(Written {
                storage: format!(
                    "{}/completed-symlinks/{}/{}",
                    c.mount_dir, job.category, job.folder
                ),
                ..Written::default()
            });
        }
        let storage = format!("{}/{}/{}", c.strm_dir, job.category, job.folder);
        let paths: Vec<&[String]> = job.files.iter().map(|f| f.path).collect();
        let mut writes: Vec<(PathBuf, Vec<u8>, Handoff)> = Vec::new();
        for (file, strm) in job.files.iter().zip(strm_names(&paths)) {
            let Some((name, dirs)) = file.path.split_last() else {
                continue;
            };
            let dir = dirs.iter().fold(PathBuf::from(&storage), |p, d| p.join(d));
            let (target, bytes, size) = if let Some(strm) = strm {
                let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
                let url = c.strm_url(file.item, ext);
                (dir.join(strm), url.into_bytes(), None)
            } else if is_subtitle(name) {
                match self.read(file.meta).await {
                    Ok(bytes) => {
                        let size = bytes.len() as u64;
                        (dir.join(name), bytes, Some(size))
                    }
                    Err(reason) => {
                        warn!(job = %job.job, file = %name, %reason, "subtitle left out of the handoff");
                        continue;
                    }
                }
            } else {
                continue;
            };
            let handoff = Handoff {
                path: target.to_string_lossy().into_owned(),
                item: file.item,
                job: Some(job.job),
                size,
            };
            writes.push((target, bytes, handoff));
        }
        let (owner, root) = (self.owner, strm_root(&c));
        let (undo_root, written_root) = (root.clone(), root.display().to_string());
        bounded(
            &self.busy,
            FS_TIMEOUT,
            move |progress| {
                let root = make_dir(&root, owner)
                    .and_then(|()| Root::open(&root))
                    .map_err(|e| format!("{}: {e}", root.display()))?;
                let mut done: Vec<Handoff> = Vec::with_capacity(writes.len());
                for (target, bytes, handoff) in writes {
                    if let Err(error) = root.write(&target, &bytes, owner) {
                        for f in &done {
                            remove(&root, Path::new(&f.path));
                        }
                        return Err(format!("{}: {error}", target.display()));
                    }
                    done.push(handoff);
                    progress.fetch_add(1, Ordering::Relaxed);
                }
                Ok(Written {
                    storage,
                    root: written_root,
                    files: done,
                })
            },
            // The job went back to the queue while these were written.
            move |late: Written| {
                if let Ok(root) = Root::open(&undo_root) {
                    for f in &late.files {
                        remove(&root, Path::new(&f.path));
                    }
                }
            },
        )
        .await
    }

    pub const fn owner(&self) -> (u32, u32) {
        self.owner
    }

    /// A subtitle's bytes, up to [`MAX_SUBTITLE_BYTES`].
    async fn read(&self, meta: &FileMeta) -> Result<Vec<u8>, String> {
        let size = meta.size();
        if size > MAX_SUBTITLE_BYTES {
            return Err(format!("larger than {MAX_SUBTITLE_BYTES} bytes"));
        }
        let mut reader = self.vfs.open_meta(meta).map_err(|e| e.to_string())?;
        reader.set_end(size);
        let mut bytes = Vec::with_capacity(usize::try_from(size).unwrap_or_default());
        reader
            .read_to_end(&mut bytes)
            .await
            .map_err(|e| e.to_string())?;
        Ok(bytes)
    }

    /// Removes handoff files no Arr took: those whose job's history or item is gone, if
    /// they still hold what we wrote.
    pub async fn sweep(&self, db: &Db) {
        let stale = match db.stale_handoffs().await {
            Ok(stale) if stale.is_empty() => return,
            Ok(stale) => stale,
            Err(error) => {
                warn!(reason = %error, "could not read the handed-off files");
                return;
            }
        };
        let root = strm_root(&self.config());
        // Records of files that could not be checked stay for a later sweep.
        let swept = tokio::task::spawn_blocking(move || {
            let mut removed = 0;
            let mut checked = Vec::with_capacity(stale.len());
            let Ok(root) = Root::open(&root) else {
                return (checked, removed);
            };
            for h in stale {
                match ours(&root, &h) {
                    Ok(true) => {
                        remove(&root, Path::new(&h.path));
                        removed += 1;
                    }
                    Ok(false) => {}
                    Err(_) => continue,
                }
                checked.push(h.path);
            }
            (checked, removed)
        })
        .await;
        let Ok((paths, removed)) = swept else {
            return;
        };
        if removed > 0 {
            info!(
                files = removed,
                "removed handed-off files not imported by an Arr"
            );
        }
        if let Err(error) = db.forget_handoffs(&paths).await {
            warn!(reason = %error, "could not forget the removed handoff files");
        }
    }
}

impl HandoffWriter for Writer {
    fn taken<'a>(
        &'a self,
        category: &'a str,
        folder: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, String>> + Send + 'a>> {
        let c = self.config();
        if c.strategy != ImportStrategy::Strm {
            return Box::pin(std::future::ready(Ok(false)));
        }
        let root = strm_root(&c);
        let path = root.join(category).join(folder);
        Box::pin(bounded(
            &self.busy,
            FS_TIMEOUT,
            move |_| {
                Ok(Root::open(&root)
                    .and_then(|root| root.entry(&path))
                    .is_ok_and(|e| e.is_some()))
            },
            |_| {},
        ))
    }

    fn write<'a>(
        &'a self,
        job: HandoffJob<'a>,
    ) -> Pin<Box<dyn Future<Output = Result<Written, String>> + Send + 'a>> {
        Box::pin(self.hand_off(job))
    }

    fn discard(&self, written: &Written) {
        if written.files.is_empty() {
            return;
        }
        if let Ok(root) = Root::open(Path::new(&written.root)) {
            for f in &written.files {
                remove(&root, Path::new(&f.path));
            }
        }
    }
}

pub fn is_subtitle(name: &str) -> bool {
    name.rsplit_once('.')
        .is_some_and(|(_, e)| SUBTITLES.iter().any(|s| e.eq_ignore_ascii_case(s)))
}

/// The STRM name of each video among `paths` (components below the job folder):
/// `<stem>.strm`, or `<name>.strm` where videos in one folder share a stem.
fn strm_names(paths: &[&[String]]) -> Vec<Option<String>> {
    let key = |path: &[String]| {
        let (name, dirs) = path.split_last()?;
        let stem = name.rsplit_once('.').map_or(name.as_str(), |(s, _)| s);
        media::is_video(name).then(|| (dirs.join("/").to_lowercase(), stem.to_lowercase()))
    };
    let mut stems: HashMap<(String, String), usize> = HashMap::new();
    for path in paths {
        if let Some(k) = key(path) {
            *stems.entry(k).or_default() += 1;
        }
    }
    paths
        .iter()
        .map(|path| {
            let k = key(path)?;
            let name = path.last()?;
            Some(if stems[&k] > 1 {
                format!("{name}.strm")
            } else {
                let stem = name.rsplit_once('.').map_or(name.as_str(), |(s, _)| s);
                format!("{stem}.strm")
            })
        })
        .collect()
}

/// The item a STRM file's URL plays, whatever its host, prefix or key.
fn strm_item(text: &str) -> Option<ItemId> {
    let path = text.trim().split(['?', '#']).next()?;
    let (_, ids) = path.rsplit_once("/view/")?;
    ItemId::from_ids_path(ids)
}

/// Whether the file at a handoff path still holds what we wrote there. A path outside
/// `root` is not ours; an error means it could not be checked.
fn ours(root: &Root, h: &Handoff) -> io::Result<bool> {
    let path = Path::new(&h.path);
    let checked: io::Result<bool> = (|| {
        Ok(match (root.entry(path)?, h.size) {
            (Some(Entry::File(len)), Some(size)) => len == size,
            (Some(Entry::File(len)), None) => {
                len <= MAX_STRM_BYTES
                    && strm_item(&root.read_to_string(path, MAX_STRM_BYTES)?) == Some(h.item)
            }
            _ => false,
        })
    })();
    match checked {
        Err(e)
            if matches!(
                e.kind(),
                io::ErrorKind::InvalidInput | io::ErrorKind::InvalidData
            ) =>
        {
            Ok(false)
        }
        other => other,
    }
}

/// The STRM folder as a path; settings trim `/` to nothing.
fn strm_root(c: &Config) -> PathBuf {
    PathBuf::from(if c.strm_dir.is_empty() {
        "/"
    } else {
        &c.strm_dir
    })
}

/// Creates `dir` and its missing parents. Only for a configured folder; folders below it
/// are made by [`Root::write`].
fn make_dir(dir: &Path, owner: (u32, u32)) -> io::Result<()> {
    if dir.is_dir() {
        return Ok(());
    }
    if let Some(parent) = dir.parent() {
        make_dir(parent, owner)?;
    }
    match DirBuilder::new().mode(0o755).create(dir) {
        Ok(()) => own(dir, owner),
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => Ok(()),
        Err(error) => Err(error),
    }
}

/// Gives what root created to `PUID`/`PGID`; otherwise it is ours already.
fn own(path: &Path, (uid, gid): (u32, u32)) -> io::Result<()> {
    if fs::symlink_metadata(path)?.uid() == 0 && uid != 0 {
        std::os::unix::fs::chown(path, Some(uid), Some(gid))?;
    }
    Ok(())
}

/// Removes a handoff file, then the folders this empties below its category folder.
fn remove(root: &Root, path: &Path) {
    if let Err(error) = root.remove(path, 1)
        && error.kind() != io::ErrorKind::NotFound
    {
        warn!(path = %path.display(), reason = %error, "could not remove a handed-off file");
    }
}

/// Samples blocking filesystem progress every `timeout`; an unchanged count ends the wait.
/// Late successful results go to `undo` under the same gate.
async fn bounded<T: Send + 'static>(
    busy: &Arc<tokio::sync::Mutex<()>>,
    timeout: Duration,
    work: impl FnOnce(&AtomicUsize) -> Result<T, String> + Send + 'static,
    undo: impl FnOnce(T) + Send + 'static,
) -> Result<T, String> {
    let Ok(held) = Arc::clone(busy).try_lock_owned() else {
        return Err(
            "the completed STRM directory has not answered an earlier request yet".to_owned(),
        );
    };
    let progress = Arc::new(AtomicUsize::new(0));
    let (tx, mut rx) = oneshot::channel();
    tokio::task::spawn_blocking({
        let progress = Arc::clone(&progress);
        move || {
            let result = work(&progress);
            if let Err((result, held)) = tx.send((result, held)) {
                if let Ok(late) = result {
                    undo(late);
                }
                drop(held);
            }
        }
    });
    let mut seen = 0;
    loop {
        match tokio::time::timeout(timeout, &mut rx).await {
            Ok(Ok((result, held))) => {
                drop(held);
                return result;
            }
            Ok(Err(_)) => return Err("the filesystem call crashed".to_owned()),
            Err(_) => {
                let now = progress.load(Ordering::Relaxed);
                if now == seen {
                    break;
                }
                seen = now;
            }
        }
    }
    // A result sent before `close` is still ours; any later one goes to `undo`.
    rx.close();
    match rx.try_recv() {
        Ok((result, held)) => {
            drop(held);
            result
        }
        Err(_) => Err(format!(
            "the completed STRM directory did not answer within {} s",
            timeout.as_secs()
        )),
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use infinidysk_core::{JobId, NzbFile, Segment, Segments};
    use infinidysk_nntp::{Client, Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_queue::HandoffFile;
    use infinidysk_stream::Budget;
    use infinidysk_testkit::{LoopbackServer, post};

    use super::*;

    #[tokio::test]
    async fn writes_strms_and_subtitles_then_removes_what_no_arr_took() {
        let subtitle = b"1\n00:00:01,000 --> 00:00:02,000\nhello\n".repeat(500);
        let articles = post("s", "a.en.srt", &subtitle, 4096);
        let server = LoopbackServer::start(articles.clone()).await.unwrap();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections: 2,
            pipeline_depth: 2,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        };
        let client = Arc::new(Client::new([Provider {
            pool: Pool::new(endpoint, config, Downloads::new(8)).unwrap(),
            key: String::new(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]));
        let dir = std::env::temp_dir().join(format!("infinidysk-handoff-{}", ItemId::new()));
        let db = Db::connect(&format!(
            "sqlite:{}",
            dir.with_extension("sqlite").display()
        ))
        .await
        .unwrap();
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            client,
            Budget::new(1 << 20),
            0,
            "/mnt",
        ));
        let mut settings = Settings::default();
        settings.mount.dir = "/mnt".into();
        settings.imports.strategy = ImportStrategy::Strm;
        settings.imports.strm_dir = dir.display().to_string();
        settings.imports.strm_key.0 = "key".into();
        settings.network.public_url = "https://media.example".into();
        let writer = Writer::new(vfs, (1000, 1000), &settings);

        let sub_meta = FileMeta::Nzb(NzbFile {
            segments: Segments {
                items: articles
                    .iter()
                    .enumerate()
                    .map(|(i, a)| Segment {
                        id: a.message_id.clone(),
                        size: u32::try_from(4096.min(subtitle.len() - i * 4096)).unwrap(),
                        fallbacks: Vec::new(),
                    })
                    .collect(),
                ..Segments::default()
            },
            proof: None,
        });
        let other = FileMeta::Nzb(NzbFile {
            segments: Segments::default(),
            proof: None,
        });
        let path = |p: &str| p.split('/').map(str::to_owned).collect::<Vec<_>>();
        let (video, srt, nfo) = (path("s1/a.mkv"), path("s1/a.en.srt"), path("a.nfo"));
        let ids = [ItemId::new(), ItemId::new(), ItemId::new()];
        let job = || HandoffJob {
            job: JobId::new(),
            category: "tv",
            folder: "job",
            kinds: &[media::Kind::Video],
            files: [(&video, &other), (&srt, &sub_meta), (&nfo, &other)]
                .iter()
                .zip(ids)
                .map(|((path, meta), item)| HandoffFile {
                    item,
                    path: path.as_slice(),
                    meta,
                })
                .collect(),
        };

        assert!(!writer.taken("tv", "job").await.unwrap());
        let written = writer.hand_off(job()).await.unwrap();
        let root = dir.join("tv/job");
        assert_eq!(written.storage, root.display().to_string());
        assert!(writer.taken("tv", "job").await.unwrap());
        let strm = fs::read_to_string(root.join("s1/a.strm")).unwrap();
        assert_eq!(strm, Config::new(&settings).strm_url(ids[0], "mkv"));
        assert_eq!(fs::read(root.join("s1/a.en.srt")).unwrap(), subtitle);
        assert!(!root.join("a.nfo").exists(), "only video and subtitles");
        let sizes: Vec<_> = written.files.iter().map(|f| f.size).collect();
        assert_eq!(sizes, [None, Some(subtitle.len() as u64)]);

        writer.discard(&written);
        assert!(!root.exists(), "the emptied job folder goes");
        assert!(dir.join("tv").is_dir(), "the category folder stays");

        // Only what still holds what we wrote goes; the job behind these rows is gone.
        let written = writer.hand_off(job()).await.unwrap();
        fs::write(root.join("s1/a.en.srt"), "edited").unwrap();
        db.record_handoffs(&written.files).await.unwrap();
        writer.sweep(&db).await;
        assert!(!root.join("s1/a.strm").exists());
        assert!(root.join("s1/a.en.srt").exists());
        assert_eq!(db.stale_handoffs().await.unwrap(), []);

        let audio = HandoffJob {
            kinds: &[media::Kind::Audio],
            ..job()
        };
        let written = writer.hand_off(audio).await.unwrap();
        assert_eq!(written.storage, "/mnt/completed-symlinks/tv/job");
        assert!(written.files.is_empty(), "audio Arrs need the mount");
        fs::remove_dir_all(&dir).unwrap();
    }

    #[tokio::test]
    async fn sweep_ignores_paths_that_climb_out_of_the_folder() {
        let tmp = std::env::temp_dir().join(format!("infinidysk-handoff-{}", ItemId::new()));
        let strm_dir = tmp.join("strm");
        fs::create_dir_all(strm_dir.join("sub")).unwrap();
        fs::create_dir_all(tmp.join("x")).unwrap();
        let db = Db::connect(&format!("sqlite:{}", tmp.join("db.sqlite").display()))
            .await
            .unwrap();
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            Arc::new(Client::new([])),
            Budget::new(1 << 20),
            0,
            "/mnt",
        ));
        let mut settings = Settings::default();
        settings.imports.strategy = ImportStrategy::Strm;
        settings.imports.strm_dir = strm_dir.display().to_string();
        settings.imports.strm_key.0 = "key".into();
        settings.network.public_url = "https://media.example".into();
        let writer = Writer::new(vfs, (1000, 1000), &settings);

        let item = ItemId::new();
        let outside = tmp.join("outside.strm");
        fs::write(&outside, Config::new(&settings).strm_url(item, "mkv")).unwrap();
        let climbing = Handoff {
            path: strm_dir.join("../outside.strm").display().to_string(),
            item,
            job: Some(JobId::new()),
            size: None,
        };
        db.record_handoffs(std::slice::from_ref(&climbing))
            .await
            .unwrap();
        writer.sweep(&db).await;
        assert!(outside.exists(), "a path with \"..\" is never swept");

        let inside = strm_dir.join("a.strm");
        fs::write(&inside, Config::new(&settings).strm_url(item, "mkv")).unwrap();
        let in_root = Handoff {
            path: strm_dir.join("sub/../a.strm").display().to_string(),
            ..climbing.clone()
        };
        db.record_handoffs(&[in_root]).await.unwrap();
        writer.sweep(&db).await;
        assert!(inside.exists(), "nor one that stays inside");

        // A configured root may itself hold "..".
        settings.imports.strm_dir = tmp.join("x/../strm").display().to_string();
        writer.configure(&settings);
        let below = Handoff {
            path: tmp.join("x/../strm/a.strm").display().to_string(),
            ..climbing
        };
        db.record_handoffs(&[below]).await.unwrap();
        writer.sweep(&db).await;
        assert!(!inside.exists(), "a clean path below such a root is swept");
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[tokio::test]
    async fn discards_under_the_written_root_and_keeps_rows_it_cannot_check() {
        let tmp = std::env::temp_dir().join(format!("infinidysk-handoff-{}", ItemId::new()));
        let strm_dir = tmp.join("strm");
        fs::create_dir_all(&strm_dir).unwrap();
        fs::create_dir_all(tmp.join("elsewhere")).unwrap();
        let db = Db::connect(&format!("sqlite:{}", tmp.join("db.sqlite").display()))
            .await
            .unwrap();
        let vfs = Arc::new(Vfs::new(
            db.clone(),
            Arc::new(Client::new([])),
            Budget::new(1 << 20),
            0,
            "/mnt",
        ));
        let mut settings = Settings::default();
        settings.imports.strategy = ImportStrategy::Strm;
        settings.imports.strm_dir = strm_dir.display().to_string();
        settings.imports.strm_key.0 = "key".into();
        settings.network.public_url = "https://media.example".into();
        let writer = Writer::new(vfs, (1000, 1000), &settings);

        let video = vec!["a.mkv".to_owned()];
        let meta = FileMeta::Nzb(NzbFile {
            segments: Segments::default(),
            proof: None,
        });
        let item = ItemId::new();
        let written = writer
            .hand_off(HandoffJob {
                job: JobId::new(),
                category: "tv",
                folder: "job",
                kinds: &[media::Kind::Video],
                files: vec![HandoffFile {
                    item,
                    path: &video,
                    meta: &meta,
                }],
            })
            .await
            .unwrap();
        let strm = strm_dir.join("tv/job/a.strm");
        assert!(strm.exists());
        let mut moved = settings.clone();
        moved.imports.strm_dir = tmp.join("other").display().to_string();
        writer.configure(&moved);
        writer.discard(&written);
        assert!(!strm.exists(), "removed below the folder it was written in");
        writer.configure(&settings);

        let planted = tmp.join("elsewhere/a.strm");
        fs::write(&planted, Config::new(&settings).strm_url(item, "mkv")).unwrap();
        std::os::unix::fs::symlink(tmp.join("elsewhere"), strm_dir.join("s")).unwrap();
        let h = Handoff {
            path: strm_dir.join("s/a.strm").display().to_string(),
            item,
            job: Some(JobId::new()),
            size: None,
        };
        db.record_handoffs(std::slice::from_ref(&h)).await.unwrap();
        writer.sweep(&db).await;
        assert!(planted.exists());
        assert_eq!(
            db.stale_handoffs().await.unwrap().len(),
            1,
            "a row below a symlinked folder stays for a later sweep"
        );
        fs::remove_file(strm_dir.join("s")).unwrap();
        writer.sweep(&db).await;
        assert_eq!(db.stale_handoffs().await.unwrap(), []);
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[tokio::test]
    async fn a_hung_strm_directory_call_gives_up_and_undoes_its_late_result() {
        let busy = Arc::new(tokio::sync::Mutex::new(()));
        let (release, hang) = std::sync::mpsc::channel::<()>();
        let (undone, late) = tokio::sync::oneshot::channel();
        let (finish_cleanup, cleanup) = std::sync::mpsc::channel::<()>();
        let hung = bounded(
            &busy,
            Duration::from_millis(50),
            move |progress| {
                progress.fetch_add(1, Ordering::Relaxed);
                let _ = hang.recv();
                Ok(1)
            },
            move |result| {
                let _ = undone.send(result);
                let _ = cleanup.recv();
            },
        )
        .await;
        assert!(hung.unwrap_err().contains("did not answer"));

        let second = bounded(&busy, Duration::from_secs(5), |_| Ok(2), |_| {}).await;
        assert!(
            second.unwrap_err().contains("not answered"),
            "no second call while the first hangs"
        );

        release.send(()).unwrap();
        assert_eq!(
            tokio::time::timeout(Duration::from_secs(5), late)
                .await
                .unwrap(),
            Ok(1),
            "the late result reaches cleanup"
        );
        let during_cleanup = bounded(&busy, Duration::from_secs(5), |_| Ok(2), |_| {}).await;
        assert!(
            during_cleanup.unwrap_err().contains("not answered"),
            "cleanup keeps the gate"
        );

        finish_cleanup.send(()).unwrap();
        drop(
            tokio::time::timeout(Duration::from_secs(5), busy.lock())
                .await
                .unwrap(),
        );
        let third = bounded(&busy, Duration::from_secs(5), |_| Ok(3), |_| {}).await;
        assert_eq!(third, Ok(3));
    }

    #[tokio::test]
    async fn a_hung_folder_refuses_only_probes_of_itself() {
        let dir = std::env::temp_dir().join(format!("infinidysk-handoff-{}", ItemId::new()));
        let db = Db::connect(&format!(
            "sqlite:{}",
            dir.with_extension("sqlite").display()
        ))
        .await
        .unwrap();
        let vfs = Arc::new(Vfs::new(
            db,
            Arc::new(Client::new([])),
            Budget::new(1 << 20),
            0,
            "/mnt",
        ));
        let mut settings = Settings::default();
        settings.imports.strategy = ImportStrategy::Strm;
        settings.imports.strm_dir = dir.join("strm").display().to_string();
        let writer = Writer::new(vfs, (1000, 1000), &settings);
        let current = settings.imports.strm_dir.clone();
        let candidate = dir.join("other").display().to_string();
        let refused = |r: Result<(), String>| {
            r.unwrap_err()
                .contains("has not answered an earlier request yet")
        };

        // A call into the current folder still running.
        let held = writer.busy.try_lock().unwrap();
        assert!(refused(writer.probe(&current).await));
        assert_eq!(writer.probe(&candidate).await, Ok(()));
        drop(held);

        // A candidate probe that has not returned.
        let held = writer.probing.try_lock().unwrap();
        assert!(refused(writer.probe(&candidate).await));
        assert_eq!(writer.probe(&current).await, Ok(()));
        assert_eq!(writer.taken("tv", "job").await, Ok(false));
        drop(held);
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn videos_sharing_a_stem_keep_their_extension() {
        let path = |p: &str| p.split('/').map(str::to_owned).collect::<Vec<_>>();
        let paths = [
            path("a.mkv"),
            path("a.en.srt"),
            path("b.mkv"),
            path("B.mp4"),
            path("s1/a.mkv"),
        ];
        let paths: Vec<&[String]> = paths.iter().map(Vec::as_slice).collect();
        assert_eq!(
            strm_names(&paths),
            [
                Some("a.strm".to_owned()),
                None,
                Some("b.mkv.strm".to_owned()),
                Some("B.mp4.strm".to_owned()),
                Some("a.strm".to_owned()),
            ]
        );
    }

    #[test]
    fn reads_the_item_back_from_any_strm_url() {
        let id = ItemId::new();
        let c = Config {
            strategy: ImportStrategy::Strm,
            strm_dir: String::new(),
            public_url: "https://media.example/dysk".to_owned(),
            strm_key: "k".to_owned(),
            mount_dir: String::new(),
        };
        assert_eq!(strm_item(&c.strm_url(id, "mkv")), Some(id));
        let other_host = format!("http://old:3000/view/{}?downloadKey=x\n", id.ids_path());
        assert_eq!(strm_item(&other_host), Some(id));
        assert_eq!(strm_item("https://example/content/a.mkv"), None);
    }
}
