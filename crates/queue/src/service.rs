//! The import queue: stores submitted NZBs and imports a few at a time into
//! `/content/<category>/<job>`.

use std::borrow::Cow;
use std::collections::{HashMap, HashSet};
use std::panic::AssertUnwindSafe;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock, PoisonError};
use std::time::{Duration, Instant};

use futures_util::FutureExt;
use infinidysk_core::{BlobId, FileMeta, ItemId, ItemKind, JobId, blob, media};
use infinidysk_db::{
    Change, Db, FirstCheck, Handoff, HistoryEntry, Item, JobStatus, QueueEntry, now_ms,
};
use infinidysk_nntp::Client;
use tokio::sync::Notify;
use tokio::task::JoinSet;

use crate::import::{self, Output, Progress, Snapshot, Step, Verifier};
use crate::{names, nzb};

/// Waits after each failed attempt in a row; the last one repeats forever.
const BACKOFF_MINUTES: [u64; 5] = [1, 2, 4, 8, 15];
/// Upper bound on idle sleeps, so jobs waiting to retry start without a wake-up.
const POLL: Duration = Duration::from_secs(5);
const MAX_DUPLICATES: u32 = 99;
/// Files whose first article id is compared against releases health had replaced.
const REJECTED_PROBES: usize = 1000;
const REJECTED: &str =
    "Rejected: this release was recently replaced because its articles are missing.";
/// Jobs importing at once unless configured.
pub const CONCURRENCY: usize = 2;
/// Config key holding the global pause, `true` or absent.
const PAUSED_KEY: &str = "queue.paused";
/// SAB priority meaning "add paused".
const PAUSED_PRIORITY: i16 = -2;
/// An attempt answering no request for this long, while download slots are free, is stuck.
const STALL: Duration = Duration::from_mins(5);
/// The stuck attempt that fails the job.
const STUCK_LIMIT: u32 = 3;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error(transparent)]
    Nzb(#[from] nzb::Error),
    /// The submission was invalid and is recorded as failed job `job`.
    #[error("{error}")]
    Invalid { job: JobId, error: nzb::Error },
    #[error("NZB blob failed")]
    Blob(#[from] blob::Error),
    #[error(transparent)]
    Db(#[from] infinidysk_db::Error),
    #[error("History item not found.")]
    NotInHistory,
    #[error("Only failed history items can be retried.")]
    NotFailed,
    #[error("The NZB file could not be found.")]
    NoNzb,
    /// Handing a finished job off failed; the job retries with the transient backoff.
    #[error("Could not write the files for the Arr: {0}")]
    Handoff(String),
}

/// A job's submission parameters.
#[derive(Debug, Clone)]
pub struct Submission<'a> {
    pub file_name: &'a str,
    pub category: &'a str,
    pub priority: i16,
    pub post_processing: i16,
}

/// Writes what an Arr imports a finished job from, outside the tree
/// (`infinidysk-lab:docs/strm-support-proposal.md` §4.2).
pub trait HandoffWriter: Send + Sync {
    /// Whether `<category>/<folder>` is already in use where jobs are handed off; an error
    /// keeps the job queued.
    fn taken<'a>(
        &'a self,
        category: &'a str,
        folder: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, String>> + Send + 'a>>;
    /// Writes the handoff of a job about to complete; an error keeps the job queued.
    fn write<'a>(
        &'a self,
        job: HandoffJob<'a>,
    ) -> Pin<Box<dyn Future<Output = Result<Written, String>> + Send + 'a>>;
    /// Removes what [`write`](Self::write) wrote for a job that did not complete.
    fn discard(&self, written: &Written);
}

/// A job to hand off, in `/content/<category>/<folder>`.
pub struct HandoffJob<'a> {
    pub job: JobId,
    pub category: &'a str,
    pub folder: &'a str,
    /// Media kinds the Arrs owning the category import.
    pub kinds: &'a [media::Kind],
    pub files: Vec<HandoffFile<'a>>,
}

pub struct HandoffFile<'a> {
    pub item: ItemId,
    /// Path components below the job folder.
    pub path: &'a [String],
    pub meta: &'a FileMeta,
}

/// A job's handoff: the SAB `storage` path, and the files written for it.
#[derive(Debug, Default)]
pub struct Written {
    pub storage: String,
    /// The folder `files` were written below, kept for removing them later.
    pub root: String,
    pub files: Vec<Handoff>,
}

pub struct Queue {
    db: Db,
    client: Arc<Client>,
    wake: Notify,
    /// Jobs importing right now, in start order.
    active: Mutex<Vec<(JobId, Arc<Progress>)>>,
    /// Serializes claiming jobs and publishing results, so two jobs never claim the same
    /// row or folder, and a claim never sees a row whose result is being written.
    serial: tokio::sync::Mutex<()>,
    /// Serializes the duplicate check with the insert, so the same NZB sent twice at once
    /// queues once.
    submit: tokio::sync::Mutex<()>,
    concurrency: AtomicUsize,
    /// No new job starts while set; running ones finish.
    paused: AtomicBool,
    stall: Duration,
    counters: [AtomicU64; 5],
    rules: Mutex<Arc<import::Rules>>,
    handoff: OnceLock<Arc<dyn HandoffWriter>>,
    verifier: OnceLock<Arc<dyn Verifier>>,
}

/// Imports since start.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Stats {
    pub completed: u64,
    pub failed: u64,
    /// Attempts interrupted and retried later.
    pub retried: u64,
    /// NZB size of completed imports.
    pub bytes: u64,
    /// Time spent on finished imports.
    pub import_ms: u64,
}

const COMPLETED: usize = 0;
const FAILED: usize = 1;
const RETRIED: usize = 2;
const BYTES: usize = 3;
const IMPORT_MS: usize = 4;

enum Outcome {
    Done {
        outputs: Vec<Output>,
        /// First health checks by output index.
        checks: Vec<Option<FirstCheck>>,
        /// Posting time, Unix seconds.
        released: Option<i64>,
    },
    Failed(String),
    Retry(String),
    /// The watchdog cancelled it.
    Stuck,
}

/// Removes a claimed job from [`Queue::active`] when its attempt ends, however it ends.
struct Claim<'a> {
    queue: &'a Queue,
    id: JobId,
}

impl Drop for Claim<'_> {
    fn drop(&mut self) {
        self.queue.lock_active().retain(|(id, _)| *id != self.id);
        self.queue.db.notify(Change::Queue);
    }
}

impl Queue {
    pub fn new(db: Db, client: Arc<Client>) -> Self {
        Self {
            db,
            client,
            wake: Notify::new(),
            active: Mutex::new(Vec::new()),
            serial: tokio::sync::Mutex::new(()),
            submit: tokio::sync::Mutex::new(()),
            concurrency: AtomicUsize::new(CONCURRENCY),
            paused: AtomicBool::new(false),
            stall: STALL,
            counters: Default::default(),
            rules: Mutex::default(),
            handoff: OnceLock::new(),
            verifier: OnceLock::new(),
        }
    }

    /// Hands finished jobs off through `writer`; without one, SAB derives each job's
    /// `storage` from the mount. Set once, before [`Queue::run`].
    pub fn set_handoff(&self, writer: Arc<dyn HandoffWriter>) {
        let _ = self.handoff.set(writer);
    }

    /// Counts an import at a 100 % sample as each file's first health check, run by
    /// `verifier`. Set once, before [`Queue::run`].
    pub fn set_verifier(&self, verifier: Arc<dyn Verifier>) {
        let _ = self.verifier.set(verifier);
    }

    /// Overrides how long an attempt may go without progress, for tests.
    #[must_use]
    pub const fn with_stall_timeout(mut self, stall: Duration) -> Self {
        self.stall = stall;
        self
    }

    /// Loads the stored global pause and hashes jobs queued before content hashes existed.
    /// Call before [`Queue::run`] and before accepting submissions.
    pub async fn restore(&self) -> Result<(), Error> {
        let paused = self
            .db
            .config()
            .await?
            .iter()
            .any(|(k, v)| k == PAUSED_KEY && v == "true");
        self.paused.store(paused, Ordering::Relaxed);
        for (id, nzb_blob) in self.db.unhashed_jobs().await? {
            let hash = match self.db.blob(nzb_blob).await? {
                Some(raw) => {
                    blocking(move || {
                        let xml = blob::decode_nzb(&raw).ok()?;
                        Some(nzb::parse(&xml).ok()?.content_hash())
                    })
                    .await
                }
                None => None,
            };
            // An unreadable NZB fails when it runs; until then it matches nothing.
            if let Some(hash) = hash {
                self.db.set_content_hash(id, &hash).await?;
            }
        }
        Ok(())
    }

    pub fn is_paused(&self) -> bool {
        self.paused.load(Ordering::Relaxed)
    }

    /// Stops or restarts starting jobs, stored before it takes effect.
    pub async fn set_paused(&self, paused: bool) -> Result<(), Error> {
        if paused {
            self.db.write_config(&[(PAUSED_KEY, "true")], &[]).await?;
        } else {
            self.db.write_config(&[], &[PAUSED_KEY]).await?;
        }
        self.paused.store(paused, Ordering::Relaxed);
        self.db.notify(Change::Queue);
        self.wake.notify_one();
        Ok(())
    }

    /// Holds or releases queued jobs; a job already importing finishes. Returns how many
    /// were queued.
    pub async fn pause_jobs(&self, ids: &[JobId], paused: bool) -> Result<u64, Error> {
        let changed = self.db.pause_jobs(ids, paused).await?;
        self.wake.notify_one();
        Ok(changed)
    }

    /// Jobs importing at once, at least 1. Lowering it lets running jobs finish.
    pub fn set_concurrency(&self, n: usize) {
        self.concurrency.store(n.max(1), Ordering::Relaxed);
        self.wake.notify_one();
    }

    /// Changes the rules later attempts import with.
    pub fn update_rules(&self, f: impl FnOnce(&mut import::Rules)) {
        let mut rules = self.rules.lock().unwrap_or_else(PoisonError::into_inner);
        f(Arc::make_mut(&mut rules));
    }

    pub fn stats(&self) -> Stats {
        let get = |i: usize| self.counters[i].load(Ordering::Relaxed);
        Stats {
            completed: get(COMPLETED),
            failed: get(FAILED),
            retried: get(RETRIED),
            bytes: get(BYTES),
            import_ms: get(IMPORT_MS),
        }
    }

    fn count(&self, counter: usize, n: u64) {
        self.counters[counter].fetch_add(n, Ordering::Relaxed);
    }

    pub fn db(&self) -> &Db {
        &self.db
    }

    /// The jobs being imported right now, in start order.
    pub fn active(&self) -> Vec<JobId> {
        self.lock_active().iter().map(|(id, _)| *id).collect()
    }

    /// How far each importing job got, in start order.
    pub fn progress(&self) -> Vec<(JobId, Snapshot)> {
        self.lock_active()
            .iter()
            .map(|(id, p)| (*id, p.snapshot()))
            .collect()
    }

    fn lock_active(&self) -> std::sync::MutexGuard<'_, Vec<(JobId, Arc<Progress>)>> {
        self.active.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Validates and stores an NZB (plain or gzip) and queues it; an identical stored NZB
    /// is reused. The same content already
    /// queued returns that job instead. An invalid NZB is recorded as failed and returns its
    /// error; a release health rejected is recorded as failed and returns that job.
    pub async fn enqueue(&self, job: &Submission<'_>, raw: &[u8]) -> Result<JobId, Error> {
        let raw = raw.to_vec();
        let checked = blocking(move || -> Result<_, nzb::Error> {
            let decompressed = match nzb::decompress(&raw)? {
                Cow::Owned(xml) => Some(xml),
                Cow::Borrowed(_) => None,
            };
            let xml = decompressed.unwrap_or(raw);
            let parsed = nzb::parse(&xml)?;
            let content_hash = parsed.content_hash();
            Ok((Arc::new(xml), parsed, content_hash))
        })
        .await;
        let (xml, parsed, content_hash) = match checked {
            Ok(valid) => valid,
            Err(error) => {
                let job = self.refuse(job, 0, error.to_string()).await?;
                return Err(Error::Invalid { job, error });
            }
        };
        let total_bytes = parsed.files.iter().map(nzb::File::bytes).sum();
        let job_name = names::job_name(job.file_name);
        let _submit = self.submit.lock().await;
        if let Some(id) = self.db.job_with_content(&content_hash).await? {
            tracing::info!(job = %id, "same NZB already queued; returning that job");
            return Ok(id);
        }
        if self.rejected(&job_name, &parsed).await? {
            return self.refuse(job, total_bytes, REJECTED.to_owned()).await;
        }
        let mut nzb_blob = BlobId::new();
        for id in self.db.nzbs_with_content(&content_hash).await? {
            let Some(stored) = self.db.blob(id).await? else {
                continue;
            };
            let xml = Arc::clone(&xml);
            if blocking(move || blob::decode_nzb(&stored).is_ok_and(|s| s == *xml)).await {
                nzb_blob = id;
                break;
            }
        }
        let paused = job.priority == PAUSED_PRIORITY;
        let entry = QueueEntry {
            id: JobId::new(),
            created_at: now_ms(),
            file_name: job.file_name.to_owned(),
            job_name,
            category: job.category.to_owned(),
            priority: if paused { 0 } else { job.priority },
            post_processing: job.post_processing,
            nzb_blob,
            nzb_size: xml.len() as u64,
            total_bytes,
            paused,
            retry_at: None,
            attempts: 0,
            stuck_count: 0,
            last_error: None,
            content_hash: Some(content_hash),
        };
        let encoded = blocking({
            let xml = Arc::clone(&xml);
            move || blob::encode_nzb(&xml)
        })
        .await?;
        self.db.enqueue(&entry, &encoded).await?;
        self.wake.notify_one();
        Ok(entry.id)
    }

    /// Records a submission that never queues as a failed job, without its NZB.
    pub async fn refuse(
        &self,
        job: &Submission<'_>,
        total_bytes: u64,
        reason: String,
    ) -> Result<JobId, Error> {
        let id = JobId::new();
        tracing::warn!(job = %id, %reason, "NZB refused");
        let history = HistoryEntry {
            id,
            created_at: now_ms(),
            file_name: job.file_name.to_owned(),
            job_name: names::job_name(job.file_name),
            category: job.category.to_owned(),
            status: JobStatus::Failed,
            total_bytes,
            download_seconds: 0,
            fail_message: Some(reason),
            download_dir: None,
            nzb_blob: None,
            storage: None,
        };
        self.db.add_history(&history, &[], &[]).await?;
        self.count(FAILED, 1);
        Ok(id)
    }

    /// Whether health asked an Arr to replace this release.
    async fn rejected(&self, job_name: &str, parsed: &nzb::Nzb) -> Result<bool, Error> {
        // Health remembers each rejected file's leading ids, so a file's first id is enough.
        let ids: Vec<&str> = parsed
            .files
            .iter()
            .filter_map(|f| f.segments.first())
            .take(REJECTED_PROBES)
            .map(|s| s.id.as_str())
            .collect();
        Ok(self.db.is_rejected(job_name, &ids, now_ms()).await?)
    }

    /// Queues a failed history job again from its stored NZB, as a new job.
    pub async fn retry(&self, id: JobId) -> Result<JobId, Error> {
        let Some(job) = self.db.history_entry(id).await? else {
            return Err(Error::NotInHistory);
        };
        // A completed job whose folder is gone reports as failed, so it may be retried too.
        if job.status == JobStatus::Completed
            && let Some(dir) = job.download_dir
            && self.db.item(dir).await?.is_some()
        {
            return Err(Error::NotFailed);
        }
        let stored = match job.nzb_blob {
            Some(nzb) => self.db.blob(nzb).await?.ok_or(Error::NoNzb)?,
            None => return Err(Error::NoNzb),
        };
        let xml = blocking(move || blob::decode_nzb(&stored))
            .await
            .map_err(|_| Error::NoNzb)?;
        let resubmit = Submission {
            file_name: &job.file_name,
            category: &job.category,
            priority: 0,
            post_processing: 0,
        };
        self.enqueue(&resubmit, &xml).await
    }

    /// Imports jobs as they become due, up to the concurrency limit at once, forever.
    pub async fn run(self: &Arc<Self>) {
        let mut running = JoinSet::new();
        loop {
            while running.len() < self.concurrency.load(Ordering::Relaxed) {
                let Some((job, progress)) = self.claim().await else {
                    break;
                };
                let queue = Arc::clone(self);
                running.spawn(async move { queue.process(job, progress).await });
            }
            tokio::select! {
                Some(_) = running.join_next() => {}
                () = self.wake.notified() => {}
                () = tokio::time::sleep(POLL) => {}
            }
        }
    }

    /// Imports the next due job; `false` when none is due.
    pub async fn process_next(&self) -> bool {
        match self.claim().await {
            Some((job, progress)) => {
                self.process(job, progress).await;
                true
            }
            None => false,
        }
    }

    /// The next due job not already importing, marked active; none while paused.
    async fn claim(&self) -> Option<(QueueEntry, Arc<Progress>)> {
        let _serial = self.serial.lock().await;
        if self.is_paused() {
            return None;
        }
        let active = self.active();
        let jobs = match self.db.next_jobs(now_ms(), active.len() + 1).await {
            Ok(jobs) => jobs,
            Err(error) => {
                tracing::warn!(reason = %error, "could not read the queue");
                return None;
            }
        };
        let job = jobs.into_iter().find(|j| !active.contains(&j.id))?;
        let progress = Arc::new(Progress::default());
        self.lock_active().push((job.id, Arc::clone(&progress)));
        Some((job, progress))
    }

    async fn process(&self, job: QueueEntry, progress: Arc<Progress>) {
        let claim = Claim {
            queue: self,
            id: job.id,
        };
        let writing = self.db.write_permit().await;
        let started = Instant::now();
        let recorded = match AssertUnwindSafe(self.import(&job, started, progress))
            .catch_unwind()
            .await
        {
            Ok(recorded) => recorded,
            Err(panic) => {
                let message = panic
                    .downcast_ref::<&str>()
                    .copied()
                    .or_else(|| panic.downcast_ref::<String>().map(String::as_str))
                    .unwrap_or("unknown");
                tracing::error!(job = %job.id, panic = message, "import crashed");
                let _serial = self.serial.lock().await;
                let reason = "internal error while importing".to_owned();
                match self.fail(&job, started, reason).await {
                    Ok(()) => true,
                    Err(error) => {
                        tracing::warn!(job = %job.id, reason = %error, "could not record the import result");
                        false
                    }
                }
            }
        };
        drop(writing);
        if !recorded {
            // No retry time was stored; holding the claim keeps the job from being re-run at once.
            let wait = u64::try_from(backoff(job.attempts)).unwrap_or(0);
            tokio::time::sleep(Duration::from_millis(wait)).await;
        }
        drop(claim);
    }

    /// `false` when neither the result nor a retry time could be written.
    async fn import(&self, job: &QueueEntry, started: Instant, progress: Arc<Progress>) -> bool {
        self.db.notify(Change::Queue);
        let outcome = match self.attempt(job, &progress).await {
            Ok(outcome) => outcome,
            Err(error) => Outcome::Retry(error.to_string()),
        };
        progress.plan(Step::Saving, 0);
        let serial = self.serial.lock().await;
        let result = match outcome {
            Outcome::Retry(reason) => {
                tracing::warn!(job = %job.id, %reason, "import interrupted; retrying later");
                self.count(RETRIED, 1);
                self.db
                    .retry_job(job.id, retry_at(job.attempts), &reason)
                    .await
                    .map_err(Error::from)
            }
            Outcome::Failed(reason) => self.fail(job, started, reason).await,
            Outcome::Stuck if job.stuck_count.saturating_add(1) >= STUCK_LIMIT => {
                let reason = "Import stalled three times.".to_owned();
                self.fail(job, started, reason).await
            }
            Outcome::Stuck => {
                tracing::warn!(job = %job.id, reason = "no progress", "import stalled; requeued");
                self.count(RETRIED, 1);
                self.db
                    .requeue_stuck(job.id, "Import stalled; requeued.")
                    .await
                    .map_err(Error::from)
            }
            Outcome::Done {
                outputs,
                checks,
                released,
            } => {
                self.publish(job, started, &outputs, &checks, released)
                    .await
            }
        };
        let result = match result {
            Err(error @ Error::Handoff(_)) => {
                tracing::warn!(job = %job.id, reason = %error, "could not hand off the import; retrying later");
                self.count(RETRIED, 1);
                self.db
                    .retry_job(job.id, retry_at(job.attempts), &error.to_string())
                    .await
                    .map_err(Error::from)
            }
            result => result,
        };
        let recorded = match result {
            Ok(()) => true,
            Err(error) => {
                tracing::warn!(job = %job.id, reason = %error, "could not record the import result");
                let reason = format!("Could not record the import result: {error}");
                self.db
                    .retry_job(job.id, retry_at(job.attempts), &reason)
                    .await
                    .is_ok()
            }
        };
        drop(serial);
        recorded
    }

    /// Moves `job` to failed history; the caller holds `serial`.
    async fn fail(&self, job: &QueueEntry, started: Instant, reason: String) -> Result<(), Error> {
        tracing::warn!(job = %job.id, %reason, "import failed");
        let history = history(job, started, JobStatus::Failed, Some(reason), None);
        if self.db.finish_job(&history, &[], &[], &[], &[]).await? {
            self.count(FAILED, 1);
            self.count(IMPORT_MS, elapsed_ms(started));
        }
        Ok(())
    }

    /// Writes a successful import's folder, files and history; the caller holds `serial`,
    /// so the folder found free stays free until the write.
    async fn publish(
        &self,
        job: &QueueEntry,
        started: Instant,
        outputs: &[Output],
        checks: &[Option<FirstCheck>],
        released: Option<i64>,
    ) -> Result<(), Error> {
        let content = self
            .db
            .item(ItemId::CONTENT)
            .await?
            .ok_or(infinidysk_db::Error::Corrupt("/content is missing"))?;
        let category = names::sanitize(&job.category).unwrap_or_else(|| "default".to_owned());
        let category = self.dir(&content, &category).await?;
        let Some(dir) = self.free_dir(&category, &job.job_name).await? else {
            let reason = "Duplicate NZB: the download folder for this NZB already exists.";
            return self.fail(job, started, reason.to_owned()).await;
        };
        let (items, blobs, files) = tree(dir, job.nzb_blob, released, outputs)?;
        let count = files.len();
        let mut history = history(job, started, JobStatus::Completed, None, Some(items[0].id));
        let written = if let Some(writer) = self.handoff.get() {
            let rules = Arc::clone(&self.rules.lock().unwrap_or_else(PoisonError::into_inner));
            let kinds = rules.owners.get(&job.category.to_lowercase());
            let request = HandoffJob {
                job: job.id,
                category: &category.name,
                folder: &items[0].name,
                kinds: kinds.map_or(&[], Vec::as_slice),
                files: files
                    .iter()
                    .map(|&(item, i)| HandoffFile {
                        item,
                        path: &outputs[i].path,
                        meta: &outputs[i].meta,
                    })
                    .collect(),
            };
            Some(writer.write(request).await.map_err(Error::Handoff)?)
        } else {
            None
        };
        history.storage = written.as_ref().map(|w| w.storage.clone());
        let handoffs = written.as_ref().map_or(&[][..], |w| w.files.as_slice());
        let blobs: Vec<(BlobId, &[u8])> = blobs.iter().map(|(id, b)| (*id, b.as_slice())).collect();
        let checks: Vec<(ItemId, FirstCheck)> = files
            .iter()
            .filter_map(|&(item, i)| Some((item, checks.get(i)?.clone()?)))
            .collect();
        let finished = self
            .db
            .finish_job(&history, &items, &blobs, &checks, handoffs)
            .await;
        if !matches!(finished, Ok(true))
            && let (Some(writer), Some(written)) = (self.handoff.get(), &written)
        {
            writer.discard(written);
        }
        if finished? {
            tracing::info!(job = %job.id, files = count, "import completed");
            self.count(COMPLETED, 1);
            self.count(BYTES, job.total_bytes);
            self.count(IMPORT_MS, elapsed_ms(started));
        } else {
            tracing::info!(job = %job.id, "job removed while importing; result discarded");
        }
        Ok(())
    }

    async fn attempt(&self, job: &QueueEntry, progress: &Arc<Progress>) -> Result<Outcome, Error> {
        let Some(raw) = self.db.blob(job.nzb_blob).await? else {
            return Ok(Outcome::Failed(
                "The NZB is missing from storage.".to_owned(),
            ));
        };
        let decoded =
            blocking(move || -> Result<_, Error> { Ok(nzb::parse(&blob::decode_nzb(&raw)?)?) })
                .await;
        let parsed = match decoded {
            Ok(parsed) => parsed,
            Err(error) => return Ok(Outcome::Failed(error.to_string())),
        };
        // A job queued before health rejected its release.
        if self.rejected(&job.job_name, &parsed).await? {
            return Ok(Outcome::Failed(REJECTED.to_owned()));
        }
        let rules = Arc::clone(&self.rules.lock().unwrap_or_else(PoisonError::into_inner));
        let password = names::password(&job.file_name).or_else(|| parsed.meta("password").next());
        let imported = tokio::select! {
            imported = import::import(
                &self.client,
                &parsed,
                &job.job_name,
                &job.category,
                &rules,
                password,
                Arc::clone(progress),
                self.verifier.get().map(|v| &**v as &dyn Verifier),
            ) => imported,
            () = self.stalled(progress) => return Ok(Outcome::Stuck),
        };
        let (outputs, checks) = match imported {
            Ok(imported) => imported,
            Err(import::Error::Retry(error)) => return Ok(Outcome::Retry(error.to_string())),
            Err(error) => return Ok(Outcome::Failed(error.to_string())),
        };
        Ok(Outcome::Done {
            outputs,
            checks,
            released: parsed.files.iter().filter_map(|f| f.date).min(),
        })
    }

    /// Returns once `progress` stood still for the stall timeout. Time with every download
    /// slot taken (playback or other imports first) does not count; that wait is not a stall.
    // ponytail: a job whose own stuck requests hold every slot is never caught; with the
    // per-job cap of 16 that needs a provider budget below 17 connections.
    async fn stalled(&self, progress: &Progress) {
        let tick = (self.stall / 10).min(Duration::from_secs(5));
        let mut last = progress.answered();
        let mut idle = Duration::ZERO;
        loop {
            tokio::time::sleep(tick).await;
            let now = progress.answered();
            if now != last {
                last = now;
                idle = Duration::ZERO;
            } else if self.client.free_permits() != Some(0) {
                idle += tick;
                if idle >= self.stall {
                    return;
                }
            }
        }
    }

    /// Creates the missing `/content/<category>` folders, so apps checking the folders SAB
    /// reports find them before the first download lands.
    pub async fn add_categories(&self, categories: &[&str]) -> Result<(), Error> {
        let _serial = self.serial.lock().await;
        let content = self
            .db
            .item(ItemId::CONTENT)
            .await?
            .ok_or(infinidysk_db::Error::Corrupt("/content is missing"))?;
        for category in categories {
            if let Some(category) = names::sanitize(category) {
                self.dir(&content, &category).await?;
            }
        }
        Ok(())
    }

    async fn dir(&self, parent: &Item, name: &str) -> Result<Item, Error> {
        let item = Item::child(parent, name, ItemKind::Directory)?;
        if let Some(found) = self.db.item_by_path(&item.path).await? {
            return Ok(found);
        }
        if let Err(error) = self.db.insert(std::slice::from_ref(&item), &[]).await {
            // Created meanwhile by someone else, such as a WebDAV client.
            return self
                .db
                .item_by_path(&item.path)
                .await?
                .ok_or_else(|| error.into());
        }
        Ok(item)
    }

    /// A not yet existing job directory: `name`, then `name (2)` … `name (99)`, compared
    /// case-insensitively as Windows clients do.
    // The span names the caller in slow-query warnings.
    #[tracing::instrument(skip_all)]
    async fn free_dir(&self, parent: &Item, name: &str) -> Result<Option<Item>, Error> {
        // ponytail: reads every sibling name for each import, so the cost grows with the
        // category's folder count. Upgrade: store a case-folded name and look up only the
        // candidates.
        let taken: HashSet<String> = self
            .db
            .child_names(parent.id)
            .await?
            .into_iter()
            .map(|n| n.to_lowercase())
            .collect();
        let handoff = self.handoff.get();
        for n in 1..=MAX_DUPLICATES {
            let name = if n == 1 {
                name.to_owned()
            } else {
                format!("{name} ({n})")
            };
            if taken.contains(&name.to_lowercase()) {
                continue;
            }
            if let Some(handoff) = handoff
                && handoff
                    .taken(&parent.name, &name)
                    .await
                    .map_err(Error::Handoff)?
            {
                continue;
            }
            return Ok(Some(Item::child(parent, &name, ItemKind::Directory)?));
        }
        Ok(None)
    }
}

type Blobs = Vec<(BlobId, Vec<u8>)>;
/// Each file's id with the index of its output.
type Files = Vec<(ItemId, usize)>;

/// The job directory, its subdirectories and files (parents first), metadata blobs and
/// files; `released` is the posting time in Unix seconds.
fn tree(
    mut dir: Item,
    nzb: BlobId,
    released: Option<i64>,
    outputs: &[Output],
) -> Result<(Vec<Item>, Blobs, Files), Error> {
    dir.nzb_blob = Some(nzb);
    let mut items = vec![dir];
    let mut dirs: HashMap<Vec<String>, usize> = HashMap::new();
    let mut blobs = Vec::with_capacity(outputs.len());
    let mut files = Vec::with_capacity(outputs.len());
    for (index, output) in outputs.iter().enumerate() {
        let Some((file, parents)) = output.path.split_last() else {
            continue;
        };
        let mut parent = 0;
        for depth in 1..=parents.len() {
            let key: Vec<String> = parents[..depth].iter().map(|p| p.to_lowercase()).collect();
            parent = if let Some(&at) = dirs.get(&key) {
                at
            } else {
                let item = Item::child(&items[parent], &parents[depth - 1], ItemKind::Directory)?;
                items.push(item);
                dirs.insert(key, items.len() - 1);
                items.len() - 1
            };
        }
        let kind = match output.meta {
            infinidysk_core::FileMeta::Nzb(_) => ItemKind::NzbFile,
            infinidysk_core::FileMeta::Multipart(_) => ItemKind::Multipart,
        };
        let mut item = Item::child(&items[parent], file, kind)?;
        let id = BlobId::new();
        item.size = Some(output.meta.size());
        item.encrypted = output.meta.encrypted();
        item.meta_blob = Some(id);
        item.nzb_blob = Some(nzb);
        item.release_date = released.map(|s| s.saturating_mul(1000));
        blobs.push((id, blob::encode_meta(&output.meta)?));
        files.push((item.id, index));
        items.push(item);
    }
    Ok((items, blobs, files))
}

fn elapsed_ms(started: Instant) -> u64 {
    u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX)
}

/// Runs CPU-heavy NZB work (hundreds of ms for a large NZB) off the async runtime.
async fn blocking<T: Send + 'static>(f: impl FnOnce() -> T + Send + 'static) -> T {
    match tokio::task::spawn_blocking(f).await {
        Ok(value) => value,
        // Cancellation only happens at runtime shutdown.
        Err(error) => std::panic::resume_unwind(
            error
                .try_into_panic()
                .unwrap_or_else(|e| Box::new(e.to_string())),
        ),
    }
}

/// When a job with `attempts` earlier failed attempts may run again after failing once more.
fn retry_at(attempts: u32) -> i64 {
    now_ms().saturating_add(backoff(attempts))
}

/// Milliseconds to wait after the failed attempt following `attempts` earlier ones.
fn backoff(attempts: u32) -> i64 {
    let step = usize::try_from(attempts)
        .unwrap_or(usize::MAX)
        .min(BACKOFF_MINUTES.len() - 1);
    i64::try_from(Duration::from_mins(BACKOFF_MINUTES[step]).as_millis()).unwrap_or(i64::MAX)
}

fn history(
    job: &QueueEntry,
    started: Instant,
    status: JobStatus,
    fail_message: Option<String>,
    download_dir: Option<ItemId>,
) -> HistoryEntry {
    HistoryEntry {
        id: job.id,
        created_at: now_ms(),
        file_name: job.file_name.clone(),
        job_name: job.job_name.clone(),
        category: job.category.clone(),
        status,
        total_bytes: job.total_bytes,
        download_seconds: i32::try_from(started.elapsed().as_secs()).unwrap_or(i32::MAX),
        fail_message,
        download_dir,
        nzb_blob: Some(job.nzb_blob),
        storage: None,
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn backoff_doubles_to_fifteen_minutes_then_stays() {
        let minutes: Vec<i64> = [0, 1, 2, 3, 4, 5, u32::MAX]
            .map(|a| super::backoff(a) / 60_000)
            .into();
        assert_eq!(minutes, [1, 2, 4, 8, 15, 15, 15]);
    }
}
