//! Health monitoring and repair as specified in `infinidysk-lab:docs/health-policy.md`: check
//! stored files on an age-based schedule, confirm misses twice, and get broken files replaced
//! through the Arr that imported them.

mod availability;
mod check;
mod structure;

pub use availability::{Availability, AvailabilityOptions, AvailabilityProgress, Evidence};

use std::collections::HashMap;
use std::panic::AssertUnwindSafe;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use futures_util::FutureExt as _;
use futures_util::future::BoxFuture;
use infinidysk_arr as arr;
use infinidysk_core::{BlobId, FileMeta, ItemId, JobId, blob, media};
use infinidysk_db::{
    Db, Defect, FirstCheck, HealthCheck, HealthResult, HealthState, HealthStatus, Item, MediaProbe,
    ProbeAttempt, ProbeOutcome, Recheck, Repair, now_ms,
};
use infinidysk_nntp::{Articles, Client};
use tokio::sync::{Notify, OwnedRwLockReadGuard, watch};

use check::Outcome;
pub use infinidysk_arr::{Arr, Profile};
use structure::Verdict;

/// Lets the mount and the first streams settle before checks compete for connections.
const STARTUP_DELAY: Duration = Duration::from_secs(20);
const IDLE_POLL: Duration = Duration::from_secs(60);
const MINUTE_MS: i64 = 60 * 1000;
const HOUR_MS: i64 = 60 * MINUTE_MS;
const DAY_MS: i64 = 24 * HOUR_MS;
const SUSPECT_RECHECK_MS: i64 = 15 * MINUTE_MS;
const INCONCLUSIVE_RECHECK_MS: i64 = 30 * MINUTE_MS;
const CADENCE_CAP_MS: i64 = 90 * DAY_MS;
/// Consecutive inconclusive checks of one file before a warning.
const INCONCLUSIVE_WARN: u32 = 3;
/// Probes in flight per file.
const CONCURRENCY_IDLE: usize = 16;
const CONCURRENCY_STREAMING: usize = 4;
const ARR_WAIT: Duration = Duration::from_secs(120);
const HIDDEN_WAIT_MS: i64 = DAY_MS;
const ARR_RETRY_MS: i64 = HOUR_MS;
/// Retry delay for a repair held back by the shared search limit.
const SEARCH_WAIT_MS: i64 = infinidysk_db::SEARCH_WINDOW_MS;
const REPAIR_BUDGET: i16 = 3;
const REJECT_FOR_MS: i64 = 14 * DAY_MS;
const REJECT_IDS: usize = 200;
/// Identical `action_needed` results before checks go weekly.
const ACTION_NEEDED_SETTLE: i64 = 3;
const RETENTION_MS: i64 = 30 * DAY_MS;
/// Config key set once every enabled Arr was reconciled, so its owner backfill and link
/// adoption ran.
pub const BACKFILLED_KEY: &str = "reconcile.linked_at";

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("health storage failed")]
    Db(#[from] infinidysk_db::Error),
    #[error("stored metadata is unreadable")]
    Meta(#[from] infinidysk_core::blob::Error),
    #[error("HTTP client setup failed")]
    Http(#[from] reqwest::Error),
    #[error("verification worker failed")]
    VerificationTask(#[from] tokio::task::JoinError),
}

/// The user-settable choices of §10.
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent user toggles, mirrored from settings"
)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Settings {
    /// Run routine checks; rechecks and repairs run regardless.
    pub enabled: bool,
    /// Share of segments a check probes, 1 to 100.
    pub sample_percent: u8,
    pub auto_repair: bool,
    pub degraded_tolerance: bool,
    pub unowned_action: UnownedAction,
    /// Analyze good files' media automatically once.
    pub media_probe: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: true,
            sample_percent: media::SAMPLE_PERCENT,
            auto_repair: true,
            degraded_tolerance: true,
            unowned_action: UnownedAction::Delete,
            media_probe: true,
        }
    }
}

/// What happens to a broken file no Arr imported.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum UnownedAction {
    #[default]
    Delete,
    ActionNeeded,
}

type Gate = Box<dyn Fn() -> bool + Send + Sync>;
/// The write permit of one health step; `None` while it waits on an Arr or a provider.
type Writing = Option<OwnedRwLockReadGuard<()>>;

/// What one media analysis of a file found.
#[derive(Debug)]
pub enum Analysis {
    /// Metadata JSON, carrying its own schema version.
    Discovered(String),
    NoStreams,
    /// The analyzer does not read this container; why.
    Unsupported(String),
    /// The attempt proved nothing; why.
    Inconclusive(String),
    /// Foreground work started; the request stays pending.
    Preempted,
}

/// Analyzes one file's media (§5.7).
pub type Analyzer = Arc<dyn Fn(Item) -> BoxFuture<'static, Analysis> + Send + Sync>;

pub struct Health {
    db: Db,
    client: Arc<Client>,
    arr: watch::Receiver<Vec<Arr>>,
    settings: watch::Receiver<Settings>,
    importing: Gate,
    streaming: Arc<dyn Fn() -> bool + Send + Sync>,
    in_use: Box<dyn Fn(ItemId) -> bool + Send + Sync>,
    analyzer: Option<Analyzer>,
    http: reqwest::Client,
    arr_wait: Duration,
    counters: [AtomicU64; 4],
    inconclusive: Mutex<HashMap<ItemId, u32>>,
    /// Repairs whose release is blocklisted but not yet replaced: the Arr's id and the
    /// newest grab id read before the blocklist.
    searching: Mutex<HashMap<ItemId, (uuid::Uuid, i64)>>,
    wake: Notify,
    par2: bool,
}

/// Checks since start, by result.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Stats {
    /// Healthy or degraded.
    pub healthy: u64,
    pub unhealthy: u64,
    /// A provider failed, so the check proved nothing.
    pub inconclusive: u64,
    /// Replacements requested from an Arr.
    pub repaired: u64,
}

const HEALTHY: usize = 0;
const UNHEALTHY: usize = 1;
const INCONCLUSIVE: usize = 2;
const REPAIRED: usize = 3;

/// Who can replace a broken file.
enum Lookup {
    Found {
        arr: Arr,
        owner: Box<arr::Owner>,
        /// The download it came from, when known.
        job: Option<JobId>,
        job_name: String,
    },
    /// No Arr imported it; the reason why.
    Unowned(String),
    /// An Arr owns it but cannot be asked for a replacement; the reason why.
    Unrepairable(String),
    /// An Arr failed; `transient` when retrying later may work.
    Failed { message: String, transient: bool },
}

impl Health {
    /// `arr` carries the enabled Arr instances and `settings` the §10 choices; both follow
    /// settings edits.
    pub fn new(
        db: Db,
        client: Arc<Client>,
        arr: watch::Receiver<Vec<Arr>>,
        settings: watch::Receiver<Settings>,
    ) -> Result<Self, Error> {
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .build()?;
        Ok(Self {
            db,
            client,
            arr,
            settings,
            importing: Box::new(|| false),
            streaming: Arc::new(|| false),
            in_use: Box::new(|_| false),
            analyzer: None,
            http,
            arr_wait: ARR_WAIT,
            counters: Default::default(),
            inconclusive: Mutex::default(),
            searching: Mutex::default(),
            wake: Notify::new(),
            par2: false,
        })
    }

    /// Pauses health work while `importing` and lowers probe concurrency while
    /// `streaming`.
    #[must_use]
    pub fn with_gates(
        mut self,
        importing: impl Fn() -> bool + Send + Sync + 'static,
        streaming: impl Fn() -> bool + Send + Sync + 'static,
    ) -> Self {
        self.importing = Box::new(importing);
        self.streaming = Arc::new(streaming);
        self
    }

    pub fn playback_active(&self) -> bool {
        (self.streaming)()
    }

    /// Holds back the repair of a file while `in_use` says it is open.
    #[must_use]
    pub fn with_in_use(mut self, in_use: impl Fn(ItemId) -> bool + Send + Sync + 'static) -> Self {
        self.in_use = Box::new(in_use);
        self
    }

    /// Try durable local reconstruction before requesting an Arr replacement.
    #[must_use]
    pub const fn with_par2(mut self) -> Self {
        self.par2 = true;
        self
    }

    /// How long an Arr gets to queue a replacement on its own after a blocklist.
    #[must_use]
    pub const fn with_arr_wait(mut self, wait: Duration) -> Self {
        self.arr_wait = wait;
        self
    }

    /// Runs pending media analyses with `analyzer` while nothing is importing or
    /// streaming.
    #[must_use]
    pub fn with_analyzer(mut self, analyzer: Analyzer) -> Self {
        self.analyzer = Some(analyzer);
        self
    }

    /// Whether media analysis is available.
    pub const fn analyzes(&self) -> bool {
        self.analyzer.is_some()
    }

    pub fn stats(&self) -> Stats {
        let get = |i: usize| self.counters[i].load(Ordering::Relaxed);
        Stats {
            healthy: get(HEALTHY),
            unhealthy: get(UNHEALTHY),
            inconclusive: get(INCONCLUSIVE),
            repaired: get(REPAIRED),
        }
    }

    fn count(&self, counter: usize) {
        self.counters[counter].fetch_add(1, Ordering::Relaxed);
    }

    /// Awaits Arr or provider `work` with the step's write permit released, so a database
    /// compaction need not wait for it, then takes the permit back for the writes after.
    async fn released<T>(&self, writing: &mut Writing, work: impl Future<Output = T>) -> T {
        *writing = None;
        let output = work.await;
        *writing = Some(self.db.write_permit().await);
        output
    }

    pub async fn run(&self) {
        tokio::time::sleep(STARTUP_DELAY).await;
        loop {
            match self.step().await {
                Ok(true) => {}
                Ok(false) => {
                    self.housekeep().await;
                    let woken = self.wake.notified();
                    let _ = tokio::time::timeout(IDLE_POLL, woken).await;
                }
                Err(error) => {
                    tracing::warn!(reason = %error, "health work failed");
                    tokio::time::sleep(IDLE_POLL).await;
                }
            }
        }
    }

    async fn housekeep(&self) {
        let _writing = self.db.write_permit().await;
        let now = now_ms();
        if let Err(error) = self.db.prune_health_checks(now - RETENTION_MS).await {
            tracing::warn!(reason = %error, "could not prune health check results");
        }
        if let Err(error) = self.db.prune_rejected_releases(now).await {
            tracing::warn!(reason = %error, "could not prune rejected releases");
        }
    }

    /// Does the most urgent due piece of health work; `false` when none is due or an
    /// import is running, which gets the connections first.
    pub async fn step(&self) -> Result<bool, Error> {
        if (self.importing)() {
            return Ok(false);
        }
        let mut writing = Some(self.db.write_permit().await);
        if let Some(analyzer) = &self.analyzer
            && !(self.streaming)()
            && let Some((item, requested)) = self.db.next_media_probe().await?
        {
            self.analyze(analyzer, item, requested, &mut writing)
                .await?;
            return Ok(true);
        }
        let settings = *self.settings.borrow();
        let Some((item, status)) = self
            .db
            .due_health_work(now_ms(), settings.enabled, 1)
            .await?
            .pop()
        else {
            return Ok(false);
        };
        let work = async {
            match status.state {
                HealthState::Repairing => self.repair(&item, status, &settings, &mut writing).await,
                HealthState::Hidden => self.reconcile(&item, status, &mut writing).await,
                _ if !media::is_checkable(&item.name) => {
                    let ignored = HealthStatus {
                        state: HealthState::Ignored,
                        next_check: None,
                        ..status
                    };
                    Ok(self
                        .db
                        .set_health(item.id, &ignored, status.recheck)
                        .await?)
                }
                _ => self.check(&item, status, &settings, &mut writing).await,
            }
        };
        // One file's failure must not keep it first in line and starve the rest.
        let reason = match AssertUnwindSafe(work).catch_unwind().await {
            Ok(Ok(())) => return Ok(true),
            Ok(Err(error)) => {
                std::iter::successors(Some(&error as &dyn std::error::Error), |e| e.source())
                    .map(ToString::to_string)
                    .collect::<Vec<_>>()
                    .join(": ")
            }
            Err(panic) => panic
                .downcast_ref::<&str>()
                .map(|s| (*s).to_owned())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".to_owned()),
        };
        tracing::warn!(item = %item.id, path = %item.path, reason = %reason, "health work on a file failed; retrying in an hour");
        // A panic during network work leaves `writing` released.
        drop(writing);
        let _writing = self.db.write_permit().await;
        self.db.postpone_health(item.id, now_ms() + HOUR_MS).await?;
        Ok(true)
    }

    /// The next `limit` files with health work, in the order [`Self::step`] takes them.
    pub async fn schedule(
        &self,
        limit: i64,
    ) -> Result<Vec<(Item, HealthStatus)>, infinidysk_db::Error> {
        let routine = self.settings.borrow().enabled;
        self.db.due_health_work(i64::MAX, routine, limit).await
    }

    /// Asks for a check of `id` soon; an `action_needed` file starts over as unchecked.
    /// `false` if there is no such file.
    pub async fn request_check(&self, id: ItemId) -> Result<bool, Error> {
        let Some((_, mut status)) = self.db.health(id).await? else {
            return Ok(false);
        };
        if status.state == HealthState::ActionNeeded {
            status.state = HealthState::Unchecked;
            status.repair_attempts = 0;
            self.db.set_health(id, &status, Recheck::None).await?;
        }
        self.db.request_recheck(id, Recheck::User).await?;
        self.wake.notify_one();
        Ok(true)
    }

    /// Starts a repair of `id` now, whatever its checks found; the repair budget still
    /// applies. `false` if there is no such file.
    pub async fn request_repair(&self, id: ItemId) -> Result<bool, Error> {
        let Some((_, mut status)) = self.db.health(id).await? else {
            return Ok(false);
        };
        if status.state != HealthState::Hidden {
            self.db.request_arr_replacement(id).await?;
            status.state = HealthState::Repairing;
            status.next_check = Some(now_ms());
            self.db.set_health(id, &status, Recheck::None).await?;
            self.wake.notify_one();
        }
        Ok(true)
    }

    /// Asks for a media analysis of `id` soon. `false` if there is no such file.
    pub async fn request_analysis(&self, id: ItemId) -> Result<bool, Error> {
        if self.db.health(id).await?.is_none() {
            return Ok(false);
        }
        self.db.request_media_probe(id, now_ms()).await?;
        self.wake.notify_one();
        Ok(true)
    }

    async fn analyze(
        &self,
        analyzer: &Analyzer,
        item: Item,
        requested: i64,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        let (id, path) = (item.id, item.path.clone());
        let (outcome, reason, info) = match self.released(writing, analyzer(item)).await {
            Analysis::Preempted => {
                tracing::debug!(path = %path, "media analysis yielded to foreground work");
                return Ok(());
            }
            Analysis::Discovered(info) => (ProbeOutcome::Discovered, None, Some(info)),
            Analysis::NoStreams => (
                ProbeOutcome::NoStreams,
                Some("no audio or video stream".to_owned()),
                None,
            ),
            Analysis::Unsupported(reason) => (ProbeOutcome::Unsupported, Some(reason), None),
            Analysis::Inconclusive(reason) => {
                tracing::debug!(path = %path, reason = %reason, "media analysis inconclusive");
                (ProbeOutcome::Inconclusive, Some(reason), None)
            }
        };
        let attempt = ProbeAttempt {
            at: now_ms(),
            outcome,
            reason: reason.as_deref(),
            info: info.as_deref(),
        };
        Ok(self.db.finish_media_probe(id, requested, &attempt).await?)
    }

    /// Records segments playback found `missing` or `corrupt` (flat indices across parts)
    /// as the first confirmed miss of `id`, and checks it next.
    // ponytail: a check of `id` running meanwhile may overwrite these holes; the next
    // playback reports them again.
    pub async fn report(
        &self,
        id: ItemId,
        missing: &[usize],
        corrupt: &[usize],
    ) -> Result<(), Error> {
        let Some((item, mut status)) = self.db.health(id).await? else {
            return Ok(());
        };
        if let Some((blob_id, mut meta)) = self.meta(&item).await?
            && check::mark(&mut meta, missing, corrupt)
        {
            self.db
                .update_blob(blob_id, &blob::encode_meta(&meta)?)
                .await?;
        }
        if matches!(
            status.state,
            HealthState::Unchecked | HealthState::Healthy | HealthState::Degraded
        ) {
            status.state = HealthState::Suspect;
            status.suspect_since = Some(now_ms());
            self.db.set_health(id, &status, Recheck::None).await?;
        }
        if status.state == HealthState::Suspect {
            // A file suspect only for its container now has its first article miss.
            let probe = self.db.media_probe(id).await?;
            if probe.structural_hold {
                self.db
                    .set_structural(id, probe.defect.as_ref(), false)
                    .await?;
                status.suspect_since = Some(now_ms());
                self.db.set_health(id, &status, Recheck::None).await?;
            }
            self.db.request_recheck(id, Recheck::Playback).await?;
            self.wake.notify_one();
        }
        Ok(())
    }

    async fn check(
        &self,
        item: &Item,
        status: HealthStatus,
        settings: &Settings,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        let Some((blob_id, mut meta)) = self.meta(item).await? else {
            tracing::warn!(item = %item.id, path = %item.path, reason = "its stored metadata is gone", "cannot check a file; retrying tomorrow");
            self.db.postpone_health(item.id, now_ms() + DAY_MS).await?;
            return Ok(());
        };
        let opts = check::Options {
            percent: settings.sample_percent,
            tolerant: settings.degraded_tolerance,
            concurrency: if (self.streaming)() {
                CONCURRENCY_STREAMING
            } else {
                CONCURRENCY_IDLE
            },
            proven: false,
        };
        let source = self.omitted_source(item, blob_id, &meta).await?;
        let original = source.map(|source| (source, meta.clone()));
        let client = check::Direct {
            client: &self.client,
            source,
            meter: None,
        };
        let observed = self.db.blob_epoch();
        let (outcome, changed, head) = self
            .released(writing, check::run(&client, &mut meta, &item.name, &opts))
            .await;
        if self.db.blob_epoch() != observed {
            self.db.postpone_health(item.id, now_ms()).await?;
            return Ok(());
        }
        if let Some((source, original)) = original {
            let current = self.meta(item).await?;
            if current
                .as_ref()
                .is_none_or(|(id, meta)| *id != blob_id || *meta != original)
                || self.omitted_source(item, blob_id, &original).await? != Some(source)
            {
                return Err(infinidysk_db::Error::Corrupt(
                    "repair source changed during health check",
                )
                .into());
            }
        }
        if changed {
            self.db
                .update_blob(blob_id, &blob::encode_meta(&meta)?)
                .await?;
        }
        let probe = self.db.media_probe(item.id).await?;
        let now = now_ms();
        let mut next = HealthStatus {
            last_check: Some(now),
            ..status
        };
        let streak = {
            let mut map = self
                .inconclusive
                .lock()
                .unwrap_or_else(PoisonError::into_inner);
            if matches!(outcome, Outcome::Inconclusive(_)) {
                let n = map.entry(item.id).or_default();
                *n += 1;
                *n
            } else {
                map.remove(&item.id);
                0
            }
        };
        match outcome {
            Outcome::Inconclusive(error) => {
                self.count(INCONCLUSIVE);
                if streak == INCONCLUSIVE_WARN {
                    tracing::warn!(path = %item.path, reason = %error, "cannot confirm file availability; retrying every 30 minutes");
                } else {
                    tracing::debug!(path = %item.path, reason = %error, "health check inconclusive");
                }
                next.next_check = Some(now + INCONCLUSIVE_RECHECK_MS);
            }
            Outcome::Missing(message) => {
                self.count(UNHEALTHY);
                // The second miss counts only 15 minutes after the first; `None` predates
                // the record and is old enough.
                let confirm_at = status.suspect_since.unwrap_or(0) + SUSPECT_RECHECK_MS;
                let message = match status.state {
                    HealthState::ActionNeeded => {
                        self.record(item, HealthResult::Missing, Repair::None, &message)
                            .await?;
                        next.next_check = Some(now + self.action_needed_interval(item).await?);
                        None
                    }
                    HealthState::Suspect if !probe.structural_hold && now < confirm_at => {
                        tracing::debug!(path = %item.path, "miss confirmed too soon after the first; confirming again later");
                        next.next_check = Some(confirm_at);
                        None
                    }
                    HealthState::Suspect if !probe.structural_hold => {
                        next.state = HealthState::Repairing;
                        next.next_check = Some(now);
                        Some(format!("{message}; confirmed, repairing"))
                    }
                    _ => {
                        if probe.structural_hold {
                            self.db
                                .set_structural(item.id, probe.defect.as_ref(), false)
                                .await?;
                        }
                        tracing::warn!(path = %item.path, reason = %message, "file has missing articles; re-checking in 15 minutes");
                        next.state = HealthState::Suspect;
                        next.suspect_since = Some(now);
                        next.next_check = Some(now + SUSPECT_RECHECK_MS);
                        Some(format!("{message}; re-checking in 15 minutes"))
                    }
                };
                if let Some(message) = message {
                    self.record(item, HealthResult::Missing, Repair::None, &message)
                        .await?;
                }
            }
            Outcome::Healthy | Outcome::Degraded(_) => {
                let observed = self.db.blob_epoch();
                let found = self
                    .released(writing, structure::walk(&client, &meta, &item.name, head))
                    .await;
                if self.db.blob_epoch() != observed {
                    self.db.postpone_health(item.id, now_ms()).await?;
                    return Ok(());
                }
                let source = structure::source(&meta);
                let verdict =
                    structure::judge(probe.defect, &found, source, now, SUSPECT_RECHECK_MS);
                if self
                    .structural(item, status, &mut next, verdict, now)
                    .await?
                {
                    self.db.set_health(item.id, &next, status.recheck).await?;
                    return Ok(());
                }
                self.count(HEALTHY);
                let (state, result, message) = match outcome {
                    Outcome::Degraded(m) => (HealthState::Degraded, HealthResult::Degraded, m),
                    _ => (HealthState::Healthy, HealthResult::Healthy, String::new()),
                };
                next.state = state;
                next.repair_attempts = 0;
                next.next_check = Some(cadence(item.release_date.unwrap_or(item.created_at), now));
                self.record(item, result, Repair::None, &message).await?;
                if settings.media_probe
                    && self.analyzer.is_some()
                    && media::is_analyzable(&item.name)
                    && wants_analysis(&probe)
                {
                    self.db.request_media_probe(item.id, now).await?;
                }
            }
        }
        self.db.set_health(item.id, &next, status.recheck).await?;
        Ok(())
    }

    /// Applies container evidence to a file whose articles are fine; `true` when the
    /// evidence decided `next`.
    async fn structural(
        &self,
        item: &Item,
        status: HealthStatus,
        next: &mut HealthStatus,
        verdict: Verdict,
        now: i64,
    ) -> Result<bool, Error> {
        let defect = match verdict {
            Verdict::Nothing => return Ok(false),
            Verdict::Clear => {
                self.db.set_structural(item.id, None, false).await?;
                return Ok(false);
            }
            Verdict::Keep(_) => {
                self.count(INCONCLUSIVE);
                next.next_check = Some(now + INCONCLUSIVE_RECHECK_MS);
                return Ok(true);
            }
            Verdict::First(d) | Verdict::Wait(d) | Verdict::Confirm(d) => d,
        };
        self.count(UNHEALTHY);
        let message = broken(&defect);
        let (hold, message) = match (status.state, &verdict) {
            (HealthState::ActionNeeded, _) => {
                next.next_check = Some(now + self.action_needed_interval(item).await?);
                (false, Some(message))
            }
            (_, Verdict::Confirm(_)) => {
                next.state = HealthState::Repairing;
                next.next_check = Some(now);
                (false, Some(format!("{message}; confirmed, repairing")))
            }
            _ => {
                next.state = HealthState::Suspect;
                next.next_check = Some(defect.seen_at + SUSPECT_RECHECK_MS);
                let first = matches!(verdict, Verdict::First(_));
                if first {
                    tracing::warn!(path = %item.path, reason = %message, "file has a broken container; re-checking in 15 minutes");
                }
                (
                    true,
                    first.then(|| format!("{message}; re-checking in 15 minutes")),
                )
            }
        };
        self.db.set_structural(item.id, Some(&defect), hold).await?;
        if let Some(message) = message {
            self.record(item, HealthResult::Damaged, Repair::None, &message)
                .await?;
        }
        Ok(true)
    }

    /// Daily, weekly once the last few results were identical.
    async fn action_needed_interval(&self, item: &Item) -> Result<i64, Error> {
        let recent = self.db.item_checks(item.id, ACTION_NEEDED_SETTLE).await?;
        let settled = i64::try_from(recent.len()) == Ok(ACTION_NEEDED_SETTLE)
            && recent
                .windows(2)
                .all(|w| (w[0].result, &w[0].message) == (w[1].result, &w[1].message));
        Ok(if settled { 7 * DAY_MS } else { DAY_MS })
    }

    /// One step of §7.1 for a `repairing` file.
    async fn repair(
        &self,
        item: &Item,
        status: HealthStatus,
        settings: &Settings,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        if !settings.auto_repair {
            return self.give_up(item, status, "automatic repair is off").await;
        }
        if status.repair_attempts >= REPAIR_BUDGET {
            return self
                .give_up(
                    item,
                    status,
                    "repeated replacements brought no working copy",
                )
                .await;
        }
        let now = now_ms();
        if (self.in_use)(item.id) {
            tracing::debug!(path = %item.path, "file is open; repair waits");
            let next = HealthStatus {
                next_check: Some(now + SUSPECT_RECHECK_MS),
                ..status
            };
            self.db.set_health(item.id, &next, status.recheck).await?;
            return Ok(());
        }
        if self.db.item_remap_pending(item.id).await? {
            let reason = "its Arr is rescanning a converted library file";
            return self.retry(item, status, reason, true).await;
        }
        if self.par2 && self.db.par2_before_arr(item.id).await? {
            let owner = infinidysk_db::repair::Owner::Item(item.id);
            let result = self.db.par2_result(owner, false).await?;
            let pending = match result {
                Some(job) if matches!(job.state, 0 | 1 | 4) => true,
                Some(job)
                    if matches!(job.state, 2 | 3 | 5 | 6)
                        && status.last_check.unwrap_or(0) <= job.updated_at =>
                {
                    let next = HealthStatus {
                        state: HealthState::Unchecked,
                        next_check: Some(now),
                        ..status
                    };
                    self.db.set_health(item.id, &next, status.recheck).await?;
                    return Ok(());
                }
                Some(_) => false,
                None => self.db.request_par2(owner, false, now).await?.is_some(),
            };
            if pending {
                let next = HealthStatus {
                    next_check: Some(now + MINUTE_MS),
                    ..status
                };
                self.db.set_health(item.id, &next, status.recheck).await?;
                return Ok(());
            }
        }
        let (arr, owner, job, job_name) = match self.released(writing, self.owner(item)).await? {
            Lookup::Found {
                arr,
                owner,
                job,
                job_name,
            } => (arr, owner, job, job_name),
            Lookup::Unowned(reason) => {
                return match settings.unowned_action {
                    UnownedAction::Delete => {
                        tracing::warn!(path = %item.path, reason = %reason, "deleted a broken file no Arr can replace");
                        self.record(
                            item,
                            HealthResult::Missing,
                            Repair::Deleted,
                            &format!("{reason}; deleted"),
                        )
                        .await?;
                        Ok(self.db.delete_item(item.id).await?)
                    }
                    UnownedAction::ActionNeeded => self.give_up(item, status, &reason).await,
                };
            }
            Lookup::Unrepairable(reason) => return self.give_up(item, status, &reason).await,
            Lookup::Failed { message, transient } => {
                return self.retry(item, status, &message, transient).await;
            }
        };
        // Blocklisting makes the Arr search, so the shared search limit applies first.
        let (kind, ids) = owner.searches();
        if !owner.replaced() && !self.db.reserve_searches(arr.id, kind, ids, now).await? {
            tracing::info!(path = %item.path, arr = %arr.name, "search limit reached; repair waits");
            let next = HealthStatus {
                next_check: Some(now + SEARCH_WAIT_MS),
                ..status
            };
            self.db.set_health(item.id, &next, status.recheck).await?;
            return Ok(());
        }
        let job_id = job.map(|j| j.to_string());
        let mut resume = self
            .searching
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .remove(&item.id)
            .and_then(|(id, since)| (id == arr.id).then_some(since));
        let replaced = arr::replace(
            &self.http,
            &arr,
            job_id.as_deref(),
            &owner,
            self.arr_wait,
            &mut resume,
        );
        let replaced = self.released(writing, replaced).await;
        if let Some(since) = resume {
            // ponytail: memory only, so a restart re-blocklists; a health column would keep it.
            self.searching
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .insert(item.id, (arr.id, since));
        }
        match replaced {
            Ok(()) => {
                self.reject(item, &job_name).await?;
                self.count(REPAIRED);
                tracing::info!(path = %item.path, arr = %arr.name, "replacement requested; hiding the file");
                let message = format!("{} is searching for a replacement", arr.name);
                self.record(
                    item,
                    HealthResult::Missing,
                    Repair::ReplacementRequested,
                    &message,
                )
                .await?;
                let next = HealthStatus {
                    state: HealthState::Hidden,
                    next_check: Some(now + HOUR_MS),
                    repair_attempts: status.repair_attempts + 1,
                    hidden_until: Some(now + HIDDEN_WAIT_MS),
                    ..status
                };
                self.db.set_health(item.id, &next, status.recheck).await?;
                self.searching
                    .lock()
                    .unwrap_or_else(PoisonError::into_inner)
                    .remove(&item.id);
                Ok(())
            }
            Err(error) => {
                let message = format!("{}: {error}", arr.name);
                self.retry(item, status, &message, error.is_transient())
                    .await
            }
        }
    }

    /// After an Arr failure: a transient one retries in an hour for free, others spend
    /// budget and back off.
    async fn retry(
        &self,
        item: &Item,
        status: HealthStatus,
        message: &str,
        transient: bool,
    ) -> Result<(), Error> {
        let now = now_ms();
        let mut next = status;
        if transient {
            tracing::warn!(path = %item.path, reason = %message, "Arr unavailable for repair; retrying in an hour");
            next.next_check = Some(now + ARR_RETRY_MS);
        } else {
            next.repair_attempts += 1;
            if next.repair_attempts >= REPAIR_BUDGET {
                return self.give_up(item, next, message).await;
            }
            let backoff = (HOUR_MS << (next.repair_attempts - 1)).min(DAY_MS);
            tracing::warn!(path = %item.path, reason = %message, "repair request failed");
            self.record(item, HealthResult::Missing, Repair::None, message)
                .await?;
            next.next_check = Some(now + backoff);
        }
        self.db.set_health(item.id, &next, status.recheck).await?;
        Ok(())
    }

    async fn give_up(&self, item: &Item, status: HealthStatus, reason: &str) -> Result<(), Error> {
        tracing::warn!(path = %item.path, reason = %reason, "broken file needs attention");
        self.record(item, HealthResult::Missing, Repair::ActionNeeded, reason)
            .await?;
        let next = HealthStatus {
            state: HealthState::ActionNeeded,
            next_check: Some(now_ms() + DAY_MS),
            hidden_until: None,
            ..status
        };
        self.db.set_health(item.id, &next, status.recheck).await?;
        self.searching
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .remove(&item.id);
        Ok(())
    }

    /// Hourly look at a `hidden` file: gone once the Arr has its replacement, back to
    /// repairing (or `action_needed`) when the wait runs out.
    async fn reconcile(
        &self,
        item: &Item,
        status: HealthStatus,
        writing: &mut Writing,
    ) -> Result<(), Error> {
        let now = now_ms();
        let mut next = HealthStatus {
            next_check: Some(now + HOUR_MS),
            ..status
        };
        let lookup = self.released(writing, self.owner(item)).await?;
        match lookup {
            Lookup::Found { arr, owner, .. } if owner.replaced() => {
                tracing::info!(path = %item.path, arr = %arr.name, "replacement imported; deleting the broken file");
                let message = format!("{} imported a replacement", arr.name);
                self.record(item, HealthResult::Missing, Repair::Replaced, &message)
                    .await?;
                return Ok(self.db.delete_item(item.id).await?);
            }
            Lookup::Failed {
                transient: true, ..
            } => {}
            _ if status.hidden_until.is_some_and(|t| now >= t) => {
                if status.repair_attempts >= REPAIR_BUDGET {
                    return self
                        .give_up(
                            item,
                            status,
                            "no replacement arrived after repeated requests",
                        )
                        .await;
                }
                self.record(
                    item,
                    HealthResult::Missing,
                    Repair::None,
                    "no replacement arrived in time; asking again",
                )
                .await?;
                next.state = HealthState::Repairing;
                next.next_check = Some(now);
                next.hidden_until = None;
            }
            _ => {}
        }
        // Rechecks wait until the file is visible again.
        self.db.set_health(item.id, &next, Recheck::None).await?;
        Ok(())
    }

    /// Finds the Arr instance and media that imported `item`.
    async fn owner(&self, item: &Item) -> Result<Lookup, Error> {
        // `/content/<category>/<job>/<file…>`; the Arr saw it under `<category>/<job>/<file…>`.
        let Some(dropped) = item.path.strip_prefix("/content") else {
            return Ok(Lookup::Unowned("not part of a download".to_owned()));
        };
        // A video handed off as a STRM was imported under `<stem>.strm` or `<name>.strm`.
        let mut dropped = vec![dropped.to_owned()];
        if media::is_video(&item.name) {
            if let Some((stem, _)) = dropped[0].rsplit_once('.') {
                dropped.push(format!("{stem}.strm"));
            }
            dropped.push(format!("{}.strm", dropped[0]));
        }
        let job_dir: String = item.path.split_inclusive('/').take(4).collect();
        let job_dir = job_dir.trim_end_matches('/');
        let stamped = self.db.job_at(job_dir).await?;
        // Files carried over from another install may know their download only from an Arr.
        let recorded_jobs = match stamped {
            Some(_) => Vec::new(),
            None => self.db.owner_jobs(item.id).await?,
        };
        let recorded = self.db.owner_instances(item.id).await?;
        let (mut instances, unrepairable): (Vec<Arr>, Vec<Arr>) = self
            .arr
            .borrow()
            .iter()
            .filter(|a| a.profile.repairs() || recorded.contains(&a.id))
            .cloned()
            .partition(|a| a.profile.repairs());
        // A recorded owner that cannot repair still owns it, so it is never unowned.
        let unrepairable = unrepairable.first().map(|a| {
            Lookup::Unrepairable(format!(
                "{} owns it but cannot be asked for a replacement",
                a.name
            ))
        });
        if instances.is_empty() {
            return Ok(unrepairable.unwrap_or_else(|| {
                Lookup::Unowned("no Arr instance is configured to replace it".to_owned())
            }));
        }
        let dir_name = job_dir.rsplit('/').next().unwrap_or_default();
        // Instances whose webhook told us they own it go first.
        instances.sort_by_key(|a| !recorded.contains(&a.id));
        let (mut failures, mut transient) = (Vec::new(), true);
        for instance in instances {
            let job = stamped.clone().or_else(|| {
                let own = recorded_jobs.iter().find(|(i, _)| *i == instance.id);
                own.or_else(|| recorded_jobs.first())
                    .map(|&(_, job)| (job, dir_name.to_owned()))
            });
            let recorded_file = self.db.owner_file(instance.id, item.id).await?;
            let found = match &job {
                Some((job, _)) => {
                    let job_id = job.to_string();
                    arr::owner(&self.http, &instance, &job_id, &dropped, recorded_file).await
                }
                None => Ok(None),
            };
            // Without the download in its history, the file id we recorded names the media.
            let found = match (found, recorded_file) {
                (Ok(None), Some(file)) => arr::owner_of_file(&self.http, &instance, file).await,
                (found, _) => found,
            };
            match found {
                Ok(Some(owner)) => {
                    let (job, job_name) = job.unzip();
                    return Ok(Lookup::Found {
                        arr: instance,
                        owner: Box::new(owner),
                        job,
                        job_name: job_name.unwrap_or_else(|| dir_name.to_owned()),
                    });
                }
                Ok(None) => {}
                Err(error) => {
                    transient &= error.is_transient();
                    failures.push(format!("{}: {error}", instance.name));
                }
            }
        }
        if !failures.is_empty() {
            return Ok(Lookup::Failed {
                message: failures.join("; "),
                transient,
            });
        }
        if let Some(unrepairable) = unrepairable {
            return Ok(unrepairable);
        }
        if stamped.is_none() && recorded_jobs.is_empty() {
            // The owner backfill may still adopt it.
            let backfilled = self.db.config().await?;
            if !backfilled.iter().any(|(k, _)| k == BACKFILLED_KEY) {
                return Ok(Lookup::Failed {
                    message: "waiting for the Arrs to report which files they imported".to_owned(),
                    transient: true,
                });
            }
            return Ok(Lookup::Unowned(
                "the download it came from is unknown".to_owned(),
            ));
        }
        Ok(Lookup::Unowned(
            "no Arr instance imported this file".to_owned(),
        ))
    }

    /// Remembers the release so the queue refuses it if the Arr grabs it again.
    async fn reject(&self, item: &Item, job_name: &str) -> Result<(), Error> {
        let ids: Vec<String> = match self.meta(item).await? {
            Some((_, FileMeta::Nzb(f))) => f
                .segments
                .items
                .into_iter()
                .take(REJECT_IDS)
                .map(|s| s.id)
                .collect(),
            Some((_, FileMeta::Multipart(f))) => {
                // Each volume's first article, which the queue compares.
                let firsts = f.parts.iter().filter_map(|p| p.segments.items.first());
                let rest = f.parts.iter().flat_map(|p| &p.segments.items);
                firsts
                    .chain(rest)
                    .take(REJECT_IDS)
                    .map(|s| s.id.clone())
                    .collect()
            }
            None => Vec::new(),
        };
        let ids: Vec<&str> = ids
            .iter()
            .map(String::as_str)
            .filter(|id| !id.is_empty())
            .collect();
        self.db
            .reject_release(job_name, &ids, now_ms() + REJECT_FOR_MS)
            .await?;
        Ok(())
    }

    async fn omitted_source(
        &self,
        item: &Item,
        metadata: BlobId,
        meta: &FileMeta,
    ) -> Result<Option<(BlobId, i64)>, Error> {
        if !self.db.may_have_repairs() || !meta.has_placeholders() {
            return Ok(None);
        }
        let Some(nzb) = item.nzb_blob else {
            return Ok(None);
        };
        let (generation, raw) = self
            .db
            .repair_metadata(item.id, metadata, nzb)
            .await?
            .ok_or(infinidysk_db::Error::Corrupt(
                "repair source changed while reading metadata",
            ))?;
        if blob::decode_meta(&raw)? != *meta {
            return Err(
                infinidysk_db::Error::Corrupt("repair metadata changed while reading").into(),
            );
        }
        Ok(Some((nzb, generation)))
    }

    async fn meta(&self, item: &Item) -> Result<Option<(BlobId, FileMeta)>, Error> {
        let Some(id) = item.meta_blob else {
            return Ok(None);
        };
        // `None` when deleted between the query and the read.
        let Some(bytes) = self.db.blob(id).await? else {
            return Ok(None);
        };
        Ok(Some((id, blob::decode_meta(&bytes)?)))
    }

    async fn record(
        &self,
        item: &Item,
        result: HealthResult,
        repair: Repair,
        message: &str,
    ) -> Result<(), Error> {
        let check = HealthCheck {
            created_at: now_ms(),
            item_id: item.id,
            path: item.path.clone(),
            result,
            repair,
            message: (!message.is_empty()).then(|| message.to_owned()),
        };
        Ok(self.db.record_health_check(&check).await?)
    }
}

/// Whether a good file should be analyzed: metadata is missing and no attempt is pending
/// or found a lasting answer.
// ponytail: analysis runs once per file; re-analysis is a user action.
const fn wants_analysis(probe: &MediaProbe) -> bool {
    probe.info.is_none()
        && probe.requested_at.is_none()
        && !matches!(
            probe.outcome,
            Some(ProbeOutcome::NoStreams | ProbeOutcome::Unsupported)
        )
}

/// When a good file is next checked: after as long again as it has been posted (Unix ms),
/// at least an hour and at most 90 days, ±10 %.
fn cadence(posted: i64, now: i64) -> i64 {
    let age = (now - posted).clamp(HOUR_MS, CADENCE_CAP_MS);
    let jitter = check::random_below((age / 5).cast_unsigned()).cast_signed() - age / 10;
    now + age + jitter
}

fn broken(defect: &Defect) -> String {
    format!(
        "the container is broken: the box at byte {} claims {} bytes",
        defect.offset, defect.size
    )
}

/// Why an import's check of a file cannot count as its first health check.
#[derive(Debug)]
pub enum Unverified {
    /// Confirmed missing or corrupt articles; what was found.
    Damaged(String),
    /// A provider failed, so the check proved nothing.
    Inconclusive(infinidysk_nntp::Error),
}

/// The first health check of file `name`, run by its import right after every article
/// answered STAT (a 100 % sample): the routine check's BODY reads and container walk,
/// through the import's `articles`. `posted` is when it was posted (Unix ms), and
/// `analyze` asks for a media analysis of a good file. `None` for files health never
/// checks.
pub async fn first_check(
    articles: &dyn Articles,
    meta: &FileMeta,
    name: &str,
    posted: Option<i64>,
    analyze: bool,
) -> Result<Option<FirstCheck>, Unverified> {
    if !media::is_checkable(name) {
        return Ok(None);
    }
    let opts = check::Options {
        percent: 100,
        tolerant: false,
        concurrency: CONCURRENCY_IDLE,
        proven: true,
    };
    let (outcome, _, head) = check::run(articles, &mut meta.clone(), name, &opts).await;
    match outcome {
        Outcome::Healthy => {}
        Outcome::Degraded(message) | Outcome::Missing(message) => {
            return Err(Unverified::Damaged(message));
        }
        Outcome::Inconclusive(error) => return Err(Unverified::Inconclusive(error)),
    }
    let found = structure::walk(articles, meta, name, head).await;
    let now = now_ms();
    let verdict = structure::judge(
        None,
        &found,
        structure::source(meta),
        now,
        SUSPECT_RECHECK_MS,
    );
    let mut status = HealthStatus {
        last_check: Some(now),
        ..HealthStatus::default()
    };
    Ok(Some(if let Verdict::First(defect) = verdict {
        let message = broken(&defect);
        tracing::warn!(file = %name, reason = %message, "imported file has a broken container; re-checking in 15 minutes");
        status.state = HealthState::Suspect;
        status.next_check = Some(defect.seen_at + SUSPECT_RECHECK_MS);
        FirstCheck {
            status,
            result: HealthResult::Damaged,
            message: Some(format!("{message}; re-checking in 15 minutes")),
            defect: Some(defect),
            analyze: false,
        }
    } else {
        status.state = HealthState::Healthy;
        status.next_check = Some(cadence(posted.unwrap_or(now), now));
        FirstCheck {
            status,
            result: HealthResult::Healthy,
            message: None,
            defect: None,
            analyze: analyze && media::is_analyzable(name),
        }
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    // Compile-time guard: borrowed iterators in `stream::iter` once made this future `!Send`.
    #[allow(dead_code, reason = "only needs to type-check")]
    fn run_is_spawnable(health: Arc<Health>) {
        tokio::spawn(async move { health.run().await });
    }

    #[test]
    fn cadence_follows_age_within_jitter() {
        let now = 400 * DAY_MS;
        let next = cadence(now - 10 * DAY_MS, now) - now;
        assert!((9 * DAY_MS..=11 * DAY_MS).contains(&next));
        assert!(cadence(0, now) - now <= CADENCE_CAP_MS * 11 / 10);
        assert!(cadence(now, now) - now >= HOUR_MS * 9 / 10);
    }
}
