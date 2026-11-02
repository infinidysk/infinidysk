//! Disk tier of the segment cache: one checksummed file per article under the cache
//! directory, written behind the reader and evicted least recently used first.

use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError, Weak};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use bytes::Bytes;
use infinidysk_core::trace::{Probe, Step};
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;

const MAGIC: &[u8; 4] = b"IDSC";
const VERSION: u16 = 1;
/// Magic, version, id length, data length, CRC32.
const HEADER: usize = 4 + 2 + 2 + 4 + 4;
/// Writes waiting for the writer; more are dropped, the cache is best effort.
const QUEUED_WRITES: usize = 64;
/// Eviction frees down to this share of the size limit, so it runs in batches.
const EVICT_TO_PERCENT: u64 = 90;
/// Left free on the volume no matter the size limit.
const RESERVE: u64 = 1_000_000_000;
const SWEEP_EVERY: Duration = Duration::from_secs(600);

/// The open instance per canonical directory. A second instance would scan away the
/// first one's unfinished writes and evict files it indexed.
static OWNERS: Mutex<Vec<(PathBuf, Weak<Disk>)>> = Mutex::new(Vec::new());
static OPENED: AtomicU64 = AtomicU64::new(0);

fn owner(owners: &[(PathBuf, Weak<Disk>)], dir: &Path) -> Option<Arc<Disk>> {
    owners
        .iter()
        .find(|(owned, _)| owned == dir)
        .and_then(|(_, disk)| disk.upgrade())
}

fn owners() -> MutexGuard<'static, Vec<(PathBuf, Weak<Disk>)>> {
    OWNERS.lock().unwrap_or_else(PoisonError::into_inner)
}

pub(crate) struct Disk {
    dir: PathBuf,
    max_bytes: AtomicU64,
    max_age_secs: AtomicU64,
    index: Mutex<Index>,
    writes: mpsc::Sender<(Arc<str>, Bytes)>,
    /// Ends this run's unfinished writes, which the scan running alongside must keep.
    tmp_suffix: String,
    indexing: AtomicBool,
    written: AtomicU64,
    evicted: AtomicU64,
    skipped: AtomicU64,
    failed: AtomicU64,
}

#[derive(Default)]
struct Index {
    entries: HashMap<u128, Entry>,
    total: u64,
}

#[derive(Clone, Copy)]
struct Entry {
    bytes: u64,
    /// Unix seconds of the last write or hit.
    used: u64,
}

impl Index {
    fn insert(&mut self, key: u128, entry: Entry) {
        self.total += entry.bytes;
        if let Some(old) = self.entries.insert(key, entry) {
            self.total -= old.bytes;
        }
    }

    fn remove(&mut self, key: u128) {
        if let Some(old) = self.entries.remove(&key) {
            self.total -= old.bytes;
        }
    }
}

/// What the disk tier holds now, and counters since it started; sizes are bytes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DiskStats {
    pub dir: PathBuf,
    /// Still indexing files an earlier run left; `bytes` and `items` grow meanwhile.
    pub indexing: bool,
    pub bytes: u64,
    pub items: u64,
    /// `limit`, lowered so the volume keeps 1 GB free.
    pub capacity: u64,
    pub limit: u64,
    /// Free space on the volume.
    pub free: Option<u64>,
    pub max_age: Duration,
    pub written: u64,
    pub evicted: u64,
    /// Writes dropped because the writer was behind.
    pub skipped: u64,
    pub failed: u64,
}

impl Disk {
    /// Creates `dir` and checks it is writable, then indexes what it already holds in the
    /// background. Returns the instance still open on `dir`, with the new limits, if a
    /// task or reader still holds one.
    pub(crate) async fn open(
        dir: PathBuf,
        max_bytes: u64,
        max_age_secs: u64,
    ) -> io::Result<Arc<Self>> {
        tokio::fs::create_dir_all(&dir).await?;
        // Resolved, so reads survive the configured symlink being removed.
        let dir = tokio::fs::canonicalize(&dir).await?;
        let open = owner(&owners(), &dir);
        if let Some(disk) = open {
            disk.set_limits(max_bytes, max_age_secs);
            return Ok(disk);
        }
        let run = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos());
        let tmp_suffix = format!(".{run:x}-{:x}.tmp", OPENED.fetch_add(1, Ordering::Relaxed));
        let probe = dir.join(format!(".probe{tmp_suffix}"));
        tokio::fs::write(&probe, b"probe").await?;
        tokio::fs::remove_file(&probe).await?;
        let (writes, queue) = mpsc::channel(QUEUED_WRITES);
        let disk = {
            let mut owners = owners();
            if let Some(disk) = owner(&owners, &dir) {
                drop(owners);
                disk.set_limits(max_bytes, max_age_secs);
                return Ok(disk);
            }
            let disk = Arc::new(Self {
                dir: dir.clone(),
                max_bytes: AtomicU64::new(max_bytes),
                max_age_secs: AtomicU64::new(max_age_secs),
                index: Mutex::default(),
                writes,
                tmp_suffix,
                indexing: AtomicBool::new(true),
                written: AtomicU64::new(0),
                evicted: AtomicU64::new(0),
                skipped: AtomicU64::new(0),
                failed: AtomicU64::new(0),
            });
            owners.retain(|(_, disk)| disk.strong_count() > 0);
            owners.push((dir, Arc::downgrade(&disk)));
            disk
        };
        let weak = Arc::downgrade(&disk);
        let (dir, ours) = (disk.dir.clone(), disk.tmp_suffix.clone());
        tokio::spawn(async move {
            scan(weak.clone(), dir, ours).await;
            if let Some(disk) = weak.upgrade() {
                disk.indexing.store(false, Ordering::Relaxed);
            }
        });
        tokio::spawn(write_behind(Arc::downgrade(&disk), queue));
        tokio::spawn(sweep(Arc::downgrade(&disk)));
        Ok(disk)
    }

    pub(crate) fn dir(&self) -> &Path {
        &self.dir
    }

    pub(crate) fn set_limits(&self, max_bytes: u64, max_age_secs: u64) {
        self.max_bytes.store(max_bytes, Ordering::Relaxed);
        self.max_age_secs.store(max_age_secs, Ordering::Relaxed);
    }

    pub(crate) fn stats(&self) -> DiskStats {
        let (bytes, items) = {
            let index = self.lock();
            (index.total, index.entries.len() as u64)
        };
        let limit = self.max_bytes.load(Ordering::Relaxed);
        let free = free_space(&self.dir);
        DiskStats {
            dir: self.dir.clone(),
            indexing: self.indexing.load(Ordering::Relaxed),
            bytes,
            items,
            capacity: capped(limit, bytes, free),
            limit,
            free,
            max_age: Duration::from_secs(self.max_age_secs.load(Ordering::Relaxed)),
            written: self.written.load(Ordering::Relaxed),
            evicted: self.evicted.load(Ordering::Relaxed),
            skipped: self.skipped.load(Ordering::Relaxed),
            failed: self.failed.load(Ordering::Relaxed),
        }
    }

    fn capacity(&self, held: u64) -> u64 {
        capped(
            self.max_bytes.load(Ordering::Relaxed),
            held,
            free_space(&self.dir),
        )
    }

    /// Queues `data` for writing; dropped when the writer is behind.
    pub(crate) fn offer(&self, message_id: &str, data: Bytes) {
        if self.lock().entries.contains_key(&key(message_id)) {
            return;
        }
        if self.writes.try_send((message_id.into(), data)).is_err() {
            self.skipped.fetch_add(1, Ordering::Relaxed);
        }
    }

    pub(crate) async fn get(&self, message_id: &str, probe: Option<&Probe>) -> Option<Bytes> {
        let key = key(message_id);
        if !self.lock().entries.contains_key(&key) {
            return None;
        }
        if let Some(probe) = probe {
            probe.step(Step::DiskRead);
        }
        let path = self.path(key);
        let file = tokio::fs::read(&path).await.ok();
        let Some(data) = file.and_then(|file| decode(file, message_id)) else {
            tracing::debug!(path = %path.display(), "dropping unreadable cache file");
            self.lock().remove(key);
            let _ = tokio::fs::remove_file(&path).await;
            return None;
        };
        if let Some(entry) = self.lock().entries.get_mut(&key) {
            entry.used = now();
        }
        // The next run's scan reads recency back from the file time.
        tokio::task::spawn_blocking(move || {
            std::fs::File::open(path).and_then(|file| file.set_modified(SystemTime::now()))
        });
        Some(data)
    }

    fn path(&self, key: u128) -> PathBuf {
        let name = format!("{key:032x}");
        self.dir.join(&name[..2]).join(name)
    }

    /// Where this instance writes `key` before renaming it into place.
    pub(crate) fn tmp_path(&self, key: u128) -> PathBuf {
        let mut tmp = self.path(key).into_os_string();
        tmp.push(&self.tmp_suffix);
        PathBuf::from(tmp)
    }

    fn lock(&self) -> MutexGuard<'_, Index> {
        self.index.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Deletes least recently used files until under the size limit, and any older than
    /// the age limit.
    async fn evict(&self) {
        let oldest = now().saturating_sub(self.max_age_secs.load(Ordering::Relaxed));
        let victims: Vec<u128> = {
            let mut index = self.lock();
            let capacity = self.capacity(index.total);
            let mut entries: Vec<(u128, Entry)> =
                index.entries.iter().map(|(k, e)| (*k, *e)).collect();
            entries.sort_unstable_by_key(|(_, e)| e.used);
            let target = if index.total > capacity {
                capacity / 100 * EVICT_TO_PERCENT
            } else {
                u64::MAX
            };
            let mut total = index.total;
            let victims: Vec<u128> = entries
                .into_iter()
                .take_while(|(_, e)| {
                    let evict = total > target || e.used < oldest;
                    if evict {
                        total -= e.bytes;
                    }
                    evict
                })
                .map(|(k, _)| k)
                .collect();
            let before = index.total;
            for key in &victims {
                index.remove(*key);
            }
            self.evicted
                .fetch_add(before - index.total, Ordering::Relaxed);
            victims
        };
        for key in victims {
            let _ = tokio::fs::remove_file(self.path(key)).await;
        }
    }
}

/// `limit`, lowered so the volume keeps [`RESERVE`] free.
fn capped(limit: u64, held: u64, free: Option<u64>) -> u64 {
    free.map_or(limit, |free| limit.min(held + free.saturating_sub(RESERVE)))
}

/// Bytes free for unprivileged writes on the volume holding `dir`, or its nearest
/// existing parent.
pub fn free_space(dir: &Path) -> Option<u64> {
    dir.ancestors().find_map(|dir| {
        let stat = nix::sys::statvfs::statvfs(dir).ok()?;
        #[allow(clippy::useless_conversion)] // The block counts are u32 on macOS.
        let free = u64::from(stat.blocks_available()).saturating_mul(stat.fragment_size());
        Some(free)
    })
}

fn key(message_id: &str) -> u128 {
    let digest = Sha256::digest(message_id.as_bytes());
    let mut bytes = [0; 16];
    bytes.copy_from_slice(&digest[..16]);
    u128::from_be_bytes(bytes)
}

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}

fn encode(message_id: &str, data: &[u8]) -> Option<Vec<u8>> {
    let id_len = u16::try_from(message_id.len()).ok()?;
    let data_len = u32::try_from(data.len()).ok()?;
    let mut file = Vec::with_capacity(HEADER + message_id.len() + data.len());
    file.extend_from_slice(MAGIC);
    file.extend_from_slice(&VERSION.to_le_bytes());
    file.extend_from_slice(&id_len.to_le_bytes());
    file.extend_from_slice(&data_len.to_le_bytes());
    file.extend_from_slice(&crc32fast::hash(data).to_le_bytes());
    file.extend_from_slice(message_id.as_bytes());
    file.extend_from_slice(data);
    Some(file)
}

/// The body in `file` if it is intact and belongs to `message_id`. The body shares
/// `file`'s buffer instead of being copied out of it.
fn decode(file: Vec<u8>, message_id: &str) -> Option<Bytes> {
    let (header, rest) = file.split_at_checked(HEADER)?;
    let u16_at = |at: usize| u16::from_le_bytes([header[at], header[at + 1]]);
    let u32_at = |at: usize| {
        u32::from_le_bytes([header[at], header[at + 1], header[at + 2], header[at + 3]])
    };
    if &header[..4] != MAGIC || u16_at(4) != VERSION {
        return None;
    }
    let id_len = usize::from(u16_at(6));
    let (id, data) = rest.split_at_checked(id_len)?;
    let intact = id == message_id.as_bytes()
        && u32::try_from(data.len()).ok()? == u32_at(8)
        && crc32fast::hash(data) == u32_at(12);
    intact.then(|| Bytes::from(file).slice(HEADER + id_len..))
}

/// Indexes files left by earlier runs and removes their unfinished writes; `ours` is
/// the temp suffix of `disk`, whose directory is `dir`.
async fn scan(disk: Weak<Disk>, dir: PathBuf, ours: String) {
    let Ok(mut shards) = tokio::fs::read_dir(&dir).await else {
        return;
    };
    while let Ok(Some(shard)) = shards.next_entry().await {
        let Ok(mut files) = tokio::fs::read_dir(shard.path()).await else {
            continue;
        };
        while let Ok(Some(file)) = files.next_entry().await {
            let name = file.file_name();
            let name = name.to_string_lossy();
            if name.ends_with(".tmp") {
                if !name.ends_with(&ours) {
                    // Held while deleting, so no newer instance can own the directory.
                    let Some(_owner) = disk.upgrade() else {
                        return;
                    };
                    let _ = tokio::fs::remove_file(file.path()).await;
                }
                continue;
            }
            let (Ok(key), Ok(meta)) = (u128::from_str_radix(&name, 16), file.metadata().await)
            else {
                continue;
            };
            let used = meta
                .modified()
                .ok()
                .and_then(|at| at.duration_since(UNIX_EPOCH).ok())
                .map_or(0, |d| d.as_secs());
            let Some(disk) = disk.upgrade() else {
                return;
            };
            let mut index = disk.lock();
            if !index.entries.contains_key(&key) {
                index.insert(
                    key,
                    Entry {
                        bytes: meta.len(),
                        used,
                    },
                );
            }
        }
    }
    if let Some(disk) = disk.upgrade() {
        disk.evict().await;
    }
}

async fn write_behind(disk: Weak<Disk>, mut queue: mpsc::Receiver<(Arc<str>, Bytes)>) {
    while let Some((message_id, data)) = queue.recv().await {
        let Some(disk) = disk.upgrade() else {
            return;
        };
        let Some(file) = encode(&message_id, &data) else {
            continue;
        };
        let bytes = file.len() as u64;
        let key = key(&message_id);
        let path = disk.path(key);
        let tmp = disk.tmp_path(key);
        let written = async {
            if let Some(shard) = path.parent() {
                tokio::fs::create_dir_all(shard).await?;
            }
            // Owned: tokio copies a borrowed buffer before moving it to a blocking thread.
            tokio::fs::write(&tmp, file).await?;
            tokio::fs::rename(&tmp, &path).await
        }
        .await;
        if let Err(error) = written {
            tracing::debug!(reason = %error, "segment cache write failed");
            disk.failed.fetch_add(1, Ordering::Relaxed);
            let _ = tokio::fs::remove_file(&tmp).await;
            continue;
        }
        disk.written.fetch_add(bytes, Ordering::Relaxed);
        let total = {
            let mut index = disk.lock();
            index.insert(key, Entry { bytes, used: now() });
            index.total
        };
        if total > disk.capacity(total) {
            disk.evict().await;
        }
    }
}

async fn sweep(disk: Weak<Disk>) {
    loop {
        tokio::time::sleep(SWEEP_EVERY).await;
        let Some(disk) = disk.upgrade() else {
            return;
        };
        disk.evict().await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn files_round_trip_and_reject_damage() {
        let file = encode("a@b", b"body").unwrap();
        assert_eq!(decode(file.clone(), "a@b").as_deref(), Some(&b"body"[..]));
        assert_eq!(decode(file.clone(), "other@b"), None, "wrong article");
        let mut damaged = file.clone();
        *damaged.last_mut().unwrap() ^= 1;
        assert_eq!(decode(damaged, "a@b"), None, "flipped bit");
        assert_eq!(
            decode(file[..file.len() - 1].to_vec(), "a@b"),
            None,
            "truncated"
        );
    }

    #[tokio::test]
    async fn a_hit_refreshes_the_file_time() {
        let dir =
            std::env::temp_dir().join(format!("infinidysk-disk-touch-{}", std::process::id()));
        let disk = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        disk.offer("a@b", Bytes::from_static(b"body"));
        for _ in 0..500 {
            if disk.stats().items == 1 {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        let path = disk.path(key("a@b"));
        let old = UNIX_EPOCH + Duration::from_secs(1_000_000);
        std::fs::File::open(&path)
            .unwrap()
            .set_modified(old)
            .unwrap();

        assert!(disk.get("a@b", None).await.is_some());
        let modified = || std::fs::metadata(&path).unwrap().modified().unwrap();
        for _ in 0..500 {
            if modified() > old {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert!(modified() > old);
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn the_scan_keeps_writes_in_progress() {
        let dir = std::env::temp_dir().join(format!("infinidysk-disk-scan-{}", std::process::id()));
        let disk = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        let shard = dir.join("00");
        std::fs::create_dir_all(&shard).unwrap();
        let ours = disk.tmp_path(1);
        let earlier = shard.join(format!("{:032x}.tmp", 2));
        std::fs::write(&ours, b"").unwrap();
        std::fs::write(&earlier, b"").unwrap();

        scan(
            Arc::downgrade(&disk),
            disk.dir.clone(),
            disk.tmp_suffix.clone(),
        )
        .await;
        assert!(ours.exists(), "this run's write was removed");
        assert!(!earlier.exists(), "an earlier run's write was kept");
        std::fs::remove_dir_all(dir).unwrap();
    }

    async fn until(done: impl Fn() -> bool) {
        for _ in 0..500 {
            if done() {
                return;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        panic!("condition not reached");
    }

    #[tokio::test]
    async fn reopening_a_directory_keeps_the_live_writer_s_files() {
        let dir =
            std::env::temp_dir().join(format!("infinidysk-disk-reopen-{}", std::process::id()));
        let first = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !first.stats().indexing).await;
        let shard = dir.join("00");
        std::fs::create_dir_all(&shard).unwrap();
        // The cache let go of `first`, but its writer still holds it mid-write.
        let first_writing = first.tmp_path(1);
        std::fs::write(&first_writing, b"").unwrap();

        let second = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !second.stats().indexing).await;
        assert!(first_writing.exists(), "the new scan removed a live write");
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn a_closed_instance_s_scan_leaves_the_next_one_s_writes() {
        let dir =
            std::env::temp_dir().join(format!("infinidysk-disk-old-scan-{}", std::process::id()));
        let first = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !first.stats().indexing).await;
        // What a scan of `first` captured before `first` closed.
        let (first_dir, first_suffix) = (first.dir.clone(), first.tmp_suffix.clone());
        let closed = Arc::downgrade(&first);
        drop(first);
        until(|| closed.strong_count() == 0).await;

        let second = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !second.stats().indexing).await;
        let writing = second.tmp_path(1);
        std::fs::create_dir_all(writing.parent().unwrap()).unwrap();
        std::fs::write(&writing, b"").unwrap();

        scan(closed, first_dir, first_suffix).await;
        assert!(
            writing.exists(),
            "a closed instance's scan removed a live write"
        );
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn reopening_through_another_path_keeps_reading_the_same_files() {
        let base =
            std::env::temp_dir().join(format!("infinidysk-disk-alias-{}", std::process::id()));
        let real = base.join("real");
        let alias = base.join("alias");
        std::fs::create_dir_all(&real).unwrap();
        std::os::unix::fs::symlink(&real, &alias).unwrap();
        let first = Disk::open(alias.clone(), 1 << 30, 3600).await.unwrap();
        first.offer("a@b", Bytes::from_static(b"body"));
        until(|| first.stats().items == 1).await;

        let second = Disk::open(real, 1 << 30, 3600).await.unwrap();
        std::fs::remove_file(alias).unwrap();
        assert_eq!(
            second.get("a@b", None).await.as_deref(),
            Some(&b"body"[..]),
            "the cache still read through the removed link"
        );
        std::fs::remove_dir_all(base).unwrap();
    }

    #[tokio::test]
    async fn reopening_a_directory_removes_abandoned_writes() {
        let dir =
            std::env::temp_dir().join(format!("infinidysk-disk-abandon-{}", std::process::id()));
        let first = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !first.stats().indexing).await;
        let shard = dir.join("00");
        std::fs::create_dir_all(&shard).unwrap();
        let abandoned = first.tmp_path(1);
        let legacy = shard.join(format!("{:032x}.tmp", 2));
        std::fs::write(&abandoned, b"").unwrap();
        std::fs::write(&legacy, b"").unwrap();
        let gone = Arc::downgrade(&first);
        drop(first);
        until(|| gone.strong_count() == 0).await;

        let second = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        until(|| !second.stats().indexing).await;
        assert!(!abandoned.exists(), "a closed run's write was kept");
        assert!(!legacy.exists(), "an earlier version's write was kept");
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[tokio::test]
    async fn stats_count_writes_and_evictions() {
        let dir =
            std::env::temp_dir().join(format!("infinidysk-disk-stats-{}", std::process::id()));
        let disk = Disk::open(dir.clone(), 1 << 30, 3600).await.unwrap();
        disk.offer("a@b", Bytes::from_static(b"body"));
        for _ in 0..500 {
            let stats = disk.stats();
            if stats.items == 1 && !stats.indexing {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        let stats = disk.stats();
        assert!(!stats.indexing);
        assert_eq!((stats.items, stats.written), (1, stats.bytes));

        disk.set_limits(0, 3600);
        disk.evict().await;
        let after = disk.stats();
        assert_eq!((after.items, after.evicted), (0, stats.written));
        std::fs::remove_dir_all(dir).unwrap();
    }
}
