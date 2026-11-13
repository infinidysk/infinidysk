//! Segment cache shared by every reader: decoded bodies by message-id in memory and
//! optionally on disk, and one fetch in flight per article that concurrent readers join
//! instead of repeating.

use std::collections::HashMap;
use std::hash::RandomState;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError, RwLock};
use std::time::Duration;

use bytes::Bytes;
use futures_util::future::{BoxFuture, FutureExt, Shared, WeakShared};
use infinidysk_core::trace::{Event, Probe, Step, Tier};
use infinidysk_core::wait::Wait;
use infinidysk_nntp::{Client, Error, Progress, Workload};
use quick_cache::sync::DefaultLifecycle;
use quick_cache::{OptionsBuilder, Weighter};

use crate::disk::{Disk, DiskStats};

/// Typical article size, to size the cache's index up front.
const TYPICAL_SEGMENT: u64 = 700 * 1024;

type Body = Result<Bytes, Arc<Error>>;
/// A body, and whether it came from the disk tier.
type Landed = Result<(Bytes, bool), Arc<Error>>;
type Flight = Shared<BoxFuture<'static, Landed>>;

#[derive(Clone)]
struct ByLength;

impl Weighter<Arc<str>, Bytes> for ByLength {
    fn weight(&self, _: &Arc<str>, data: &Bytes) -> u64 {
        (data.len() as u64).max(1)
    }
}

type Memory = quick_cache::sync::Cache<
    Arc<str>,
    Bytes,
    ByLength,
    RandomState,
    DefaultLifecycle<Arc<str>, Bytes>,
>;

pub struct Cache {
    memory: Memory,
    enabled: AtomicBool,
    /// Held for reading across an insert's enabled check and the insert, and for writing
    /// while the memory tier is switched, so no insert lands after a disable's clear.
    toggle: RwLock<()>,
    /// Stops the next `remember` between its enabled check and its insert.
    #[cfg(test)]
    pause: Mutex<Option<Pause>>,
    disk: RwLock<Option<Arc<Disk>>>,
    /// The directory and reason the configured disk tier did not start.
    disk_failure: Mutex<Option<(PathBuf, String)>>,
    inflight: Mutex<Inflight>,
    memory_hits: AtomicU64,
    disk_hits: AtomicU64,
    joined: AtomicU64,
    misses: AtomicU64,
    bytes_served: AtomicU64,
}

#[derive(Default)]
struct Inflight {
    generation: u64,
    /// Weak, so a fetch every reader abandoned is dropped (and its request cancelled).
    flights: HashMap<Arc<str>, WeakFlight>,
}

/// A flight's generation, future, leader wait and starting workload.
type WeakFlight = (
    u64,
    WeakShared<BoxFuture<'static, Landed>>,
    Option<Arc<Wait>>,
    Workload,
);

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CacheConfig {
    pub memory_enabled: bool,
    pub memory_bytes: u64,
    pub disk: Option<DiskConfig>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DiskConfig {
    pub dir: PathBuf,
    /// Lowered to keep 1 GB of the volume free.
    pub max_bytes: u64,
    /// Files unused this long are deleted.
    pub max_age: Duration,
}

/// Counters since start, and what the tiers hold now.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CacheStats {
    pub memory_enabled: bool,
    pub memory_bytes: u64,
    pub memory_capacity: u64,
    pub memory_items: u64,
    pub memory_hits: u64,
    /// The disk tier while it runs (configured and its directory writable).
    pub disk: Option<DiskStats>,
    /// The directory and reason the configured disk tier did not start.
    pub disk_failure: Option<(PathBuf, String)>,
    pub disk_hits: u64,
    /// Segments taken from a fetch another reader already had in flight.
    pub joined: u64,
    /// Segments fetched from providers.
    pub misses: u64,
    /// Bytes served without a provider request of their own.
    pub bytes_served: u64,
}

impl Cache {
    pub fn new(memory_enabled: bool, memory_bytes: u64) -> Self {
        let options = OptionsBuilder::new()
            .shards(4)
            .estimated_items_capacity(
                usize::try_from(memory_bytes / TYPICAL_SEGMENT).unwrap_or(usize::MAX) + 16,
            )
            .weight_capacity(memory_bytes)
            .build()
            .expect("both required options are set");
        Self {
            memory: Memory::with_options(
                options,
                ByLength,
                RandomState::new(),
                DefaultLifecycle::default(),
            ),
            enabled: AtomicBool::new(memory_enabled),
            toggle: RwLock::new(()),
            #[cfg(test)]
            pause: Mutex::default(),
            disk: RwLock::default(),
            disk_failure: Mutex::default(),
            inflight: Mutex::default(),
            memory_hits: AtomicU64::new(0),
            disk_hits: AtomicU64::new(0),
            joined: AtomicU64::new(0),
            misses: AtomicU64::new(0),
            bytes_served: AtomicU64::new(0),
        }
    }

    /// Applies new settings to the running cache; disabling a tier drops what it holds
    /// in memory. A new disk directory starts empty; one that cannot be written leaves
    /// the disk tier off with a warning.
    pub async fn configure(&self, config: &CacheConfig) {
        self.set_memory(config.memory_enabled, config.memory_bytes);
        let current = self.disk();
        let mut failure = None;
        let next = match (&config.disk, current) {
            (None, _) => None,
            (Some(want), Some(disk)) if disk.dir() == want.dir => {
                disk.set_limits(want.max_bytes, want.max_age.as_secs());
                Some(disk)
            }
            (Some(want), current) => {
                match Disk::open(want.dir.clone(), want.max_bytes, want.max_age.as_secs()).await {
                    Ok(disk) => {
                        // A symlinked or relative path reopens the same instance.
                        if current.is_some_and(|current| !Arc::ptr_eq(&current, &disk)) {
                            tracing::warn!(
                                dir = %want.dir.display(),
                                reason = "cache directory changed",
                                "starting an empty disk cache"
                            );
                        }
                        Some(disk)
                    }
                    Err(error) => {
                        tracing::warn!(
                            dir = %want.dir.display(),
                            reason = %error,
                            "disk cache disabled: directory is not writable"
                        );
                        failure = Some((want.dir.clone(), error.to_string()));
                        None
                    }
                }
            }
        };
        *self.disk.write().unwrap_or_else(PoisonError::into_inner) = next;
        *self
            .disk_failure
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = failure;
    }

    fn set_memory(&self, enabled: bool, bytes: u64) {
        let _toggle = self.toggle.write().unwrap_or_else(PoisonError::into_inner);
        self.enabled.store(enabled, Ordering::Relaxed);
        self.memory.set_capacity(bytes);
        if !enabled {
            self.memory.clear();
        }
    }

    fn disk(&self) -> Option<Arc<Disk>> {
        self.disk
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    /// Keeps a decoded body for later readers.
    pub fn insert(&self, message_id: &str, data: Bytes) {
        if let Some(disk) = self.disk() {
            disk.offer(message_id, data.clone());
        }
        self.remember(message_id, data);
    }

    fn remember(&self, message_id: &str, data: Bytes) {
        let _toggle = self.toggle.read().unwrap_or_else(PoisonError::into_inner);
        if self.enabled.load(Ordering::Relaxed) {
            #[cfg(test)]
            self.paused();
            self.memory.insert(message_id.into(), data);
        }
    }

    #[cfg(test)]
    fn paused(&self) {
        let pause = self
            .pause
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take();
        if let Some(Pause { entered, release }) = pause {
            entered.send(()).ok();
            release.recv().ok();
        }
    }

    pub fn stats(&self) -> CacheStats {
        CacheStats {
            memory_enabled: self.enabled.load(Ordering::Relaxed),
            memory_bytes: self.memory.weight(),
            memory_capacity: self.memory.capacity(),
            memory_items: self.memory.len() as u64,
            memory_hits: self.memory_hits.load(Ordering::Relaxed),
            disk: self.disk().map(|disk| disk.stats()),
            disk_failure: self
                .disk_failure
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .clone(),
            disk_hits: self.disk_hits.load(Ordering::Relaxed),
            joined: self.joined.load(Ordering::Relaxed),
            misses: self.misses.load(Ordering::Relaxed),
            bytes_served: self.bytes_served.load(Ordering::Relaxed),
        }
    }

    /// The article's body from memory, another reader's fetch in flight, or providers.
    /// Only a fetch this call starts sends to `progress`.
    pub(crate) async fn body(
        self: &Arc<Self>,
        client: &Arc<Client>,
        message_id: &str,
        workload: Workload,
        begin: Option<u64>,
        probe: Option<&Probe>,
        progress: Option<Progress>,
    ) -> Body {
        let cached = self
            .enabled
            .load(Ordering::Relaxed)
            .then(|| self.memory.get(message_id))
            .flatten();
        if let Some(data) = cached {
            self.served(&self.memory_hits, &data, Tier::Memory, probe);
            return Ok(data);
        }
        let (flight, joined) = self.flight(client, message_id, workload, begin, probe, progress);
        let (data, from_disk) = flight.await?;
        if joined {
            self.served(&self.joined, &data, Tier::Shared, probe);
        } else if from_disk {
            self.served(&self.disk_hits, &data, Tier::Disk, probe);
        }
        Ok(data)
    }

    /// Fetches from providers outside any shared flight, keeping the result.
    pub(crate) async fn direct(
        &self,
        client: &Client,
        message_id: &str,
        workload: Workload,
        begin: Option<u64>,
        probe: Option<&Probe>,
        progress: Option<&Progress>,
    ) -> Body {
        self.misses.fetch_add(1, Ordering::Relaxed);
        let body = client
            .body_with(message_id, workload, probe, progress, begin)
            .await;
        let data = body.map_err(Arc::new)?.data;
        self.insert(message_id, data.clone());
        Ok(data)
    }

    /// Races a slow fetch from providers outside any shared flight, keeping the result.
    pub(crate) async fn hedge(
        &self,
        client: &Client,
        message_id: &str,
        begin: Option<u64>,
        probe: Option<&Probe>,
    ) -> Body {
        self.misses.fetch_add(1, Ordering::Relaxed);
        let body = client.hedge(message_id, probe, begin).await;
        let data = body.map_err(Arc::new)?.data;
        self.insert(message_id, data.clone());
        Ok(data)
    }

    fn served(&self, counter: &AtomicU64, data: &Bytes, tier: Tier, probe: Option<&Probe>) {
        counter.fetch_add(1, Ordering::Relaxed);
        self.bytes_served
            .fetch_add(data.len() as u64, Ordering::Relaxed);
        if let Some(probe) = probe {
            probe.record(Event::Cached {
                segment: probe.segment,
                tier,
            });
        }
    }

    /// The fetch in flight for `message_id`, and whether it was already running.
    fn flight(
        self: &Arc<Self>,
        client: &Arc<Client>,
        message_id: &str,
        workload: Workload,
        begin: Option<u64>,
        probe: Option<&Probe>,
        progress: Option<Progress>,
    ) -> (Flight, bool) {
        let mut inflight = self.inflight.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some((generation, flight, leader)) = inflight
            .flights
            .get(message_id)
            .filter(|(_, _, _, started)| {
                workload != Workload::Stream || *started == Workload::Stream
            })
            .and_then(|(generation, weak, leader, _)| {
                Some((*generation, weak.upgrade()?, leader.clone()))
            })
        {
            drop(inflight);
            if let Some(probe) = probe {
                probe.step(Step::Joined { flight: generation });
                if let (Some(wait), Some(leader)) = (&probe.wait, leader) {
                    wait.follow(leader);
                }
            }
            return (flight, true);
        }
        inflight.generation += 1;
        let generation = inflight.generation;
        let leader = probe.and_then(|p| p.wait.clone());
        if let Some(wait) = &leader {
            wait.lead(generation);
        }
        let landing = Landing {
            cache: Arc::downgrade(self),
            key: message_id.into(),
            generation,
        };
        let (cache, client, probe) = (Arc::clone(self), Arc::clone(client), probe.cloned());
        let flight = async move {
            if let Some(disk) = cache.disk()
                && let Some(data) = disk.get(&landing.key, probe.as_ref()).await
            {
                cache.remember(&landing.key, data.clone());
                return Ok((data, true));
            }
            let body = cache
                .direct(
                    &client,
                    &landing.key,
                    workload,
                    begin,
                    probe.as_ref(),
                    progress.as_ref(),
                )
                .await;
            drop(landing);
            Ok((body?, false))
        }
        .boxed()
        .shared();
        if let Some(weak) = flight.downgrade() {
            inflight
                .flights
                .insert(message_id.into(), (generation, weak, leader, workload));
        }
        drop(inflight);
        (flight, false)
    }
}

/// Removes its flight from the in-flight map when the fetch ends or every reader
/// waiting on it has gone.
struct Landing {
    cache: std::sync::Weak<Cache>,
    key: Arc<str>,
    generation: u64,
}

impl Drop for Landing {
    fn drop(&mut self) {
        let Some(cache) = self.cache.upgrade() else {
            return;
        };
        let mut inflight = cache
            .inflight
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        if inflight
            .flights
            .get(&self.key)
            .is_some_and(|(generation, _, _, _)| *generation == self.generation)
        {
            inflight.flights.remove(&self.key);
        }
    }
}

#[cfg(test)]
struct Pause {
    entered: std::sync::mpsc::Sender<()>,
    release: std::sync::mpsc::Receiver<()>,
}

#[cfg(test)]
mod tests {
    use std::sync::{TryLockError, mpsc};

    use super::*;

    #[test]
    fn replacing_background_flights_preserves_sharing_and_cleanup() {
        for background in [Workload::Health, Workload::Queue] {
            let client = Arc::new(Client::new([]));
            let cache = Arc::new(Cache::new(true, 1 << 20));
            let flight =
                |workload| cache.flight(&client, "priority@test", workload, None, None, None);
            let (original, joined) = flight(background);
            assert!(!joined);
            let (follower, joined) = flight(background);
            assert!(joined, "background readers still share");
            let (playback, joined) = flight(Workload::Stream);
            assert!(!joined, "playback replaces the background flight");
            drop((original, follower));
            assert!(flight(Workload::Stream).1, "old cleanup preserves playback");
            assert!(flight(background).1, "background can join playback");
            drop(playback);
            assert!(cache.inflight.lock().unwrap().flights.is_empty());
        }
    }

    #[test]
    fn disable_clears_an_insertion_paused_after_its_enabled_check() {
        let cache = Arc::new(Cache::new(true, 1 << 20));
        let (entered_tx, entered) = mpsc::channel();
        let (release, release_rx) = mpsc::channel();
        *cache.pause.lock().unwrap() = Some(Pause {
            entered: entered_tx,
            release: release_rx,
        });
        let inserting = std::thread::spawn({
            let cache = Arc::clone(&cache);
            move || cache.remember("<old@test>", Bytes::from_static(b"old"))
        });
        entered.recv_timeout(Duration::from_secs(10)).unwrap();
        assert!(
            matches!(cache.toggle.try_write(), Err(TryLockError::WouldBlock)),
            "disable must wait for the paused insertion"
        );
        release.send(()).unwrap();
        inserting.join().unwrap();
        cache.set_memory(false, 1 << 20);

        assert_eq!(cache.memory.weight(), 0);
        assert_eq!(cache.memory.len(), 0);

        cache.set_memory(true, 1 << 20);
        assert!(cache.memory.get("<old@test>").is_none());
        cache.remember("<new@test>", Bytes::from_static(b"new"));
        assert!(cache.memory.get("<new@test>").is_some());
    }

    #[tokio::test]
    async fn switching_the_disk_cache_keeps_live_writes() {
        let base =
            std::env::temp_dir().join(format!("infinidysk-cache-switch-{}", std::process::id()));
        let config = |dir: Option<&str>| CacheConfig {
            memory_enabled: true,
            memory_bytes: 1 << 20,
            disk: dir.map(|dir| DiskConfig {
                dir: base.join(dir),
                max_bytes: 1 << 30,
                max_age: Duration::from_secs(3600),
            }),
        };
        let cache = Cache::new(true, 1 << 20);
        cache.configure(&config(Some("a"))).await;
        // A write the first instance is still finishing through every switch below.
        let writer = cache.disk().unwrap();
        let writing = writer.tmp_path(1);
        std::fs::create_dir_all(writing.parent().unwrap()).unwrap();
        std::fs::write(&writing, b"").unwrap();

        for step in [None, Some("a"), Some("b"), Some("a")] {
            cache.configure(&config(step)).await;
            for _ in 0..500 {
                if cache.stats().disk.is_none_or(|disk| !disk.indexing) {
                    break;
                }
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
            assert!(
                writing.exists(),
                "switching to {step:?} removed a live write"
            );
        }
        std::fs::remove_dir_all(base).unwrap();
    }
}
