//! Full article availability with bounded body spot checks, never a full-file download.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use futures_util::{StreamExt as _, stream};
use infinidysk_core::{BlobId, ItemId, blob};
use infinidysk_nntp::{Error as SourceError, MAX_DECODED_LEN};
use tokio::sync::{Mutex as AsyncMutex, Semaphore};
use tokio_util::sync::CancellationToken;

use super::check::{self, Direct, Probe};
use super::{Error, Health};

#[derive(Debug)]
pub enum Availability {
    Available { articles: u64 },
    Damaged { missing: u64, corrupt: u64 },
    Unavailable(&'static str),
    Source(SourceError),
    Changed,
    Cancelled,
    Yielded,
}

#[derive(Default)]
pub struct AvailabilityProgress {
    pub checked: AtomicU64,
    pub total: AtomicU64,
    pub body_bytes: AtomicU64,
}

pub struct AvailabilityOptions {
    pub concurrency: usize,
    pub memory_bytes: usize,
    pub decoded_bytes: u64,
    pub evidence: Option<Arc<Evidence>>,
    pub progress: Arc<AvailabilityProgress>,
}

impl Default for AvailabilityOptions {
    fn default() -> Self {
        Self {
            concurrency: 64,
            memory_bytes: 256_000_000,
            decoded_bytes: 100_000_000_000,
            evidence: None,
            progress: Arc::new(AvailabilityProgress::default()),
        }
    }
}

#[derive(Hash, PartialEq, Eq)]
struct Key {
    source: Option<(BlobId, i64)>,
    id: String,
    begin: Option<u64>,
    size: Option<u32>,
}

#[derive(Default)]
struct Entries {
    slots: HashMap<Arc<Key>, Arc<AsyncMutex<bool>>>,
    order: VecDeque<Arc<Key>>,
    bytes: usize,
}

/// Positive observations shared only by jobs from the same scan. Stores no article bytes.
#[derive(Default)]
pub struct Evidence(Mutex<Entries>);

impl Evidence {
    fn slot(&self, key: Key) -> Arc<AsyncMutex<bool>> {
        let mut entries = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        if let Some(slot) = entries.slots.get(&key) {
            return Arc::clone(slot);
        }
        let bytes = key.id.len() + 512;
        if bytes > 16_000_000 {
            return Arc::new(AsyncMutex::new(false));
        }
        // shortcut: 16 MB of observations; revisit capacity if shared-source benchmarks repeat reads.
        while entries.bytes + bytes > 16_000_000 {
            let Some(old) = entries.order.pop_front() else {
                break;
            };
            entries.bytes -= old.id.len() + 512;
            entries.slots.remove(&old);
        }
        let key = Arc::new(key);
        let slot = Arc::new(AsyncMutex::new(false));
        entries.bytes += bytes;
        entries.order.push_back(Arc::clone(&key));
        entries.slots.insert(key, Arc::clone(&slot));
        slot
    }
}

impl Health {
    /// Checks stored plain and archive metadata. Results never claim full byte integrity.
    /// Call as top-level work, without holding a database write permit.
    pub async fn check_availability(
        &self,
        id: ItemId,
        cancel: &CancellationToken,
        options: AvailabilityOptions,
    ) -> Result<Availability, Error> {
        let stopped = cancel.child_token();
        let _stop = stopped.clone().drop_guard();
        if stopped.is_cancelled() {
            return Ok(Availability::Cancelled);
        }
        if self.playback_active() {
            return Ok(Availability::Yielded);
        }
        let writing = tokio::select! {
            biased;
            () = stopped.cancelled() => return Ok(Availability::Cancelled),
            writing = self.db.write_permit() => writing,
        };
        let Some(item) = self.db.item(id).await? else {
            return Ok(Availability::Unavailable("file no longer exists"));
        };
        let Some(meta_id) = item.meta_blob else {
            return Ok(Availability::Unavailable("file metadata is unavailable"));
        };
        let Some(raw) = self.db.repair_blob(meta_id, 16_000_000).await? else {
            return Ok(Availability::Unavailable("file metadata is unavailable"));
        };
        let meta = blob::decode_meta_limited(&raw, 16_000_000)?;
        let source = self.omitted_source(&item, meta_id, &meta).await?;
        let providers = self.client.stats();
        let first = providers.iter().map(|provider| provider.priority).min();
        // Keep at least half the primary pool's connections free for playback startup.
        let headroom = providers
            .iter()
            .filter(|provider| Some(provider.priority) == first)
            .map(|provider| provider.max_connections / 2)
            .sum::<usize>()
            .max(1);
        let concurrency = options
            .concurrency
            .min(headroom)
            .min(self.client.free_permits().unwrap_or(64).max(1))
            .clamp(1, 256);
        // Covers decoded metadata, observation keys and at least one maximum-sized body.
        let bodies = options.memory_bytes.saturating_sub(80_000_000) / MAX_DECODED_LEN;
        if bodies == 0 {
            return Ok(Availability::Unavailable(
                "working memory cannot admit a spot check",
            ));
        }
        let client = Arc::clone(&self.client);
        let streaming = Arc::clone(&self.streaming);
        let runtime = tokio::runtime::Handle::current();
        let (meta, result, writing) = tokio::task::spawn_blocking(move || {
            let direct = Direct {
                client: &client,
                source,
                meter: None,
            };
            let result = runtime.block_on(async {
                let (segments, _, _) = check::flatten(&meta);
                let total = segments.len();
                options
                    .progress
                    .total
                    .store(total as u64, Ordering::Relaxed);
                if total == 0 {
                    return Availability::Unavailable("no articles in file metadata");
                }
                let body_permits = Semaphore::new(bodies.min(3));
                let remaining = AtomicU64::new(options.decoded_bytes);
                let evidence = options.evidence.as_deref();
                let progress = &options.progress;
                let requests = segments
                    .iter()
                    .enumerate()
                    .map(|(i, &(segment, begin, proof))| {
                        let body = segment.id.is_empty() || [0, total / 2, total - 1].contains(&i);
                        let slot = evidence.filter(|_| !segment.id.is_empty()).map(|evidence| {
                            evidence.slot(Key {
                                source,
                                id: segment.id.clone(),
                                begin: body.then_some(begin).flatten(),
                                size: body.then_some(segment.size),
                            })
                        });
                        let body_permits = &body_permits;
                        let remaining = &remaining;
                        let direct = &direct;
                        async move {
                            let mut receipt = if let Some(slot) = &slot {
                                Some(slot.lock().await)
                            } else {
                                None
                            };
                            if receipt.as_deref().copied() == Some(true) {
                                drop(receipt);
                                progress.checked.fetch_add(1, Ordering::Release);
                                return Ok(Probe::Present);
                            }
                            let result = if body {
                                let _permit = body_permits
                                    .acquire()
                                    .await
                                    .expect("spot check semaphore never closes");
                                if remaining
                                    .try_update(Ordering::Relaxed, Ordering::Relaxed, |left| {
                                        left.checked_sub(MAX_DECODED_LEN as u64)
                                    })
                                    .is_err()
                                {
                                    return Err("source read allowance exceeded");
                                }
                                let (result, bytes) =
                                    check::fetch(direct, segment, begin, proof).await;
                                if let Some(bytes) = bytes {
                                    progress
                                        .body_bytes
                                        .fetch_add(bytes.len() as u64, Ordering::Relaxed);
                                    remaining.fetch_add(
                                        MAX_DECODED_LEN as u64 - bytes.len() as u64,
                                        Ordering::Relaxed,
                                    );
                                }
                                result
                            } else {
                                check::stat(direct, segment).await
                            };
                            if let Some(receipt) = &mut receipt {
                                **receipt = matches!(result, Probe::Present);
                            }
                            drop(receipt);
                            progress.checked.fetch_add(1, Ordering::Release);
                            Ok(result)
                        }
                    });
                let mut requests = stream::iter(requests).buffer_unordered(concurrency);
                let (mut missing, mut corrupt) = (0, 0);
                loop {
                    if streaming() {
                        return Availability::Yielded;
                    }
                    let result = tokio::select! {
                        biased;
                        () = stopped.cancelled() => return Availability::Cancelled,
                        () = tokio::time::sleep(std::time::Duration::from_millis(25)) => continue,
                        result = requests.next() => result,
                    };
                    match result {
                        None => break,
                        Some(Ok(Probe::Present)) => {}
                        Some(Ok(Probe::Missing)) => missing += 1,
                        Some(Ok(Probe::Corrupt)) => corrupt += 1,
                        Some(Ok(Probe::Inconclusive(error))) => return Availability::Source(error),
                        Some(Err(reason)) => return Availability::Unavailable(reason),
                    }
                }
                if missing + corrupt == 0 {
                    Availability::Available {
                        articles: total as u64,
                    }
                } else {
                    Availability::Damaged { missing, corrupt }
                }
            });
            (meta, result, writing)
        })
        .await?;
        let _writing = writing;
        if cancel.is_cancelled() {
            return Ok(Availability::Cancelled);
        }
        let Some(current) = self.db.item(id).await? else {
            return Ok(Availability::Changed);
        };
        if current.meta_blob != Some(meta_id) || current.nzb_blob != item.nzb_blob {
            return Ok(Availability::Changed);
        }
        let Some(raw) = self.db.repair_blob(meta_id, 16_000_000).await? else {
            return Ok(Availability::Changed);
        };
        if blob::decode_meta_limited(&raw, 16_000_000)? != meta
            || self.omitted_source(&current, meta_id, &meta).await? != source
        {
            return Ok(Availability::Changed);
        }
        Ok(result)
    }
}
