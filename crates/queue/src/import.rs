//! Turns a parsed NZB into importable files: names from PAR2, segment layouts from yEnc
//! headers, archive members mapped onto volume segments, then filtering, a STAT sample
//! and media checks that fail releases the Arr should replace.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::future::Future;
use std::ops::Range;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use bytes::Bytes;
use futures_util::{StreamExt as _, stream};
use infinidysk_archive::crypt::{Derived, Encryption, Kdf};
use infinidysk_archive::{Format, rar, sevenzip};
use infinidysk_core::crypt::{BLOCK, Cipher, Crypt};
use infinidysk_core::trace::{self, Causes, Probe, Trace};
use infinidysk_core::{
    ByteRange, FileMeta, FilePart, MultipartFile, NzbFile, Par2Proof, Segment, Segments, media,
};
use infinidysk_db::FirstCheck;
use infinidysk_nntp::{ArticleFuture, Articles, Body, Client, Workload};
use tokio::sync::Semaphore;
use tokio::task::JoinSet;

use crate::names::{self, Kind};
use crate::nzb::{self, Nzb};

mod inspect;

pub use inspect::{
    Access, ArchiveSet, ArchiveType, Encrypted, Failure, FailureKind, Inspected, Inspection,
    Inspector, Level, Timings,
};

/// Requests in flight per import, across all its steps.
const CONCURRENCY: usize = 16;
/// Futures one [`join_all`] runs at once: enough to keep every request slot busy.
const WINDOW: usize = 4 * CONCURRENCY;
const PAR2_MAGIC: &[u8] = b"PAR2\0PKT";
const PAR2_MAX_BYTES: usize = 32_000_000;
const PAR2_MAX_ARTICLES: usize = 64;
/// Budget across every PAR2 file of a job, for packs that post one index per file.
const PAR2_TOTAL_BYTES: usize = 128_000_000;
const PAR2_TOTAL_ARTICLES: usize = 256;
const STAT_ATTEMPTS: usize = 3;
/// Archive key derivations running at once across all imports: half the cores, at least one,
/// so the rest stay free for playback.
static DERIVATIONS: LazyLock<Semaphore> = LazyLock::new(|| Semaphore::new(derivation_slots()));

fn derivation_slots() -> usize {
    std::thread::available_parallelism().map_or(1, |n| n.get().div_ceil(2))
}
/// Requests a first health check expects per file: two random bodies and a box walk; the
/// first segment is usually held already.
const FIRST_CHECK_REQUESTS: usize = 3;
/// Trace events kept per request: a few attempts' steps.
const REQUEST_EVENTS: usize = 64;
/// A video named like a sample and smaller than this fraction of the largest video is dropped.
const SAMPLE_FRACTION: u64 = 5;
/// Time left is not guessed before this share is done and this much time has passed.
const ETA_MIN_PERCENT: u8 = 3;
const ETA_MIN_ELAPSED: Duration = Duration::from_secs(5);
const ETA_MAX: Duration = Duration::from_hours(24);

/// What an import attempt is doing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Step {
    Starting,
    Headers,
    Par2,
    Layouts,
    Archives,
    Checking,
    Verifying,
    Saving,
}

impl Step {
    const ALL: [Self; 8] = [
        Self::Starting,
        Self::Headers,
        Self::Par2,
        Self::Layouts,
        Self::Archives,
        Self::Checking,
        Self::Verifying,
        Self::Saving,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            Self::Starting => "Starting",
            Self::Headers => "Reading file headers",
            Self::Par2 => "Reading PAR2",
            Self::Layouts => "Measuring files",
            Self::Archives => "Reading archives",
            Self::Checking => "Checking articles",
            Self::Verifying => "Verifying media",
            Self::Saving => "Saving",
        }
    }
}

/// How far one import attempt got: provider requests answered against an estimate that
/// each step revises once it knows its own work.
#[derive(Debug)]
pub struct Progress {
    started: Instant,
    answered: AtomicU64,
    issued: AtomicU64,
    expected: AtomicU64,
    step: AtomicU8,
    /// Highest percent reported, so the shown value never goes back.
    shown: AtomicU8,
}

impl Default for Progress {
    fn default() -> Self {
        Self {
            started: Instant::now(),
            answered: AtomicU64::new(0),
            issued: AtomicU64::new(0),
            expected: AtomicU64::new(0),
            step: AtomicU8::new(Step::Starting as u8),
            shown: AtomicU8::new(0),
        }
    }
}

/// A point-in-time view of [`Progress`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Snapshot {
    /// 0 to 99; 100 is only reached by finishing.
    pub percent: u8,
    pub step: Step,
    /// Time left, once there is enough to go on.
    pub eta: Option<Duration>,
}

impl Progress {
    /// Provider requests answered so far.
    pub fn answered(&self) -> u64 {
        self.answered.load(Ordering::Relaxed)
    }

    fn request(&self) {
        let issued = self.issued.fetch_add(1, Ordering::Relaxed) + 1;
        self.expected.fetch_max(issued, Ordering::Relaxed);
    }

    fn answer(&self) {
        self.answered.fetch_add(1, Ordering::Relaxed);
    }

    /// Enters `step`, expecting `remaining` more requests after those already sent.
    pub fn plan(&self, step: Step, remaining: usize) {
        self.step.store(step as u8, Ordering::Relaxed);
        let issued = self.issued.load(Ordering::Relaxed);
        self.expected
            .store(issued + remaining as u64, Ordering::Relaxed);
    }

    pub fn snapshot(&self) -> Snapshot {
        let step = Step::ALL[usize::from(self.step.load(Ordering::Relaxed))];
        let expected = self.expected.load(Ordering::Relaxed).max(1);
        let answered = self.answered().min(expected);
        let now = u8::try_from(answered * 100 / expected).map_or(99, |p| p.min(99));
        let percent = self.shown.fetch_max(now, Ordering::Relaxed).max(now);
        let elapsed = self.started.elapsed();
        let eta = (percent >= ETA_MIN_PERCENT && elapsed >= ETA_MIN_ELAPSED).then(|| {
            elapsed
                .mul_f64(f64::from(100 - percent) / f64::from(percent))
                .min(ETA_MAX)
        });
        Snapshot { percent, step, eta }
    }
}

/// Requests the article sample and the media checks take.
fn checks(segments: usize, media: usize, percent: u8) -> usize {
    media::sample(segments, percent).len() + 2 * media
}

/// What an import checks beyond the fixed policy.
#[derive(Debug, Clone)]
pub struct Rules {
    /// Share of segments the existence sample probes.
    pub check_percent: u8,
    /// Extra filename globs to drop.
    pub blocklist: Vec<String>,
    /// Media kinds the Arrs owning each lowercased category import.
    pub owners: HashMap<String, Vec<media::Kind>>,
}

impl Default for Rules {
    fn default() -> Self {
        Self {
            check_percent: media::SAMPLE_PERCENT,
            blocklist: Vec::new(),
            owners: HashMap::new(),
        }
    }
}

/// One file to create under the job directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Output {
    /// Sanitized path components below the job directory.
    pub path: Vec<String>,
    pub meta: FileMeta,
    pub origin: Origin,
}

/// What gave an output its name.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NameSource {
    /// The PAR2 index, matched by the hash of the file's first 16 KB.
    Par2,
    /// The NZB subject.
    Subject,
    /// A RAR or 7z header.
    ArchiveHeader,
}

/// How the planner found an output; an inspection reports it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Origin {
    pub source: NameSource,
    /// The inspected archive set it is a member of; always `None` in an import.
    pub set: Option<usize>,
}

/// What an import found.
#[derive(Debug)]
pub enum Imported {
    Files {
        outputs: Vec<Output>,
        /// First health checks by output index.
        checks: Vec<Option<FirstCheck>>,
    },
    /// The job holds this NZB file in place of a release, as posted (plain or gzip).
    Nzb(Vec<u8>),
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    /// Providers could not answer; the job should be retried later.
    #[error("providers unavailable: {0}")]
    Retry(#[source] infinidysk_nntp::Error),
    /// Articles are gone on every provider.
    #[error("{0}")]
    Missing(String),
    /// An archive is encrypted and the job has no password, or a wrong one.
    #[error("{0}")]
    Password(String),
    /// Archive content that cannot be streamed with any password.
    #[error("{0}")]
    Unsupported(String),
    #[error("{0}")]
    Failed(String),
    /// An inspection reached one of its limits; an import has none.
    #[error("{0}")]
    Limit(&'static str),
}

fn failed(msg: impl Into<String>) -> Error {
    Error::Failed(msg.into())
}

fn missing(name: &str) -> Error {
    Error::Missing(format!(
        "Missing articles: {name} has segments missing on every provider. \
         The NZB is likely taken down or expired."
    ))
}

fn lacks(name: &str) -> Error {
    Error::Missing(format!("{name}: the NZB lacks segments of this file"))
}

/// Runs a file's first health check within its import.
pub trait Verifier: Send + Sync {
    /// The first health check of `output`, posted at `posted` (Unix ms), reading through
    /// `articles`; `None` when the file gets none. Confirmed damage is
    /// [`Error::Missing`], a provider failure [`Error::Retry`].
    fn verify<'a>(
        &'a self,
        articles: &'a dyn Articles,
        output: &'a Output,
        posted: Option<i64>,
    ) -> Pin<Box<dyn Future<Output = Result<Option<FirstCheck>, Error>> + Send + 'a>>;
}

/// One NZB file entry.
struct Input {
    name: String,
    source: NameSource,
    kind: Kind,
    ids: Vec<String>,
    first: Option<Body>,
    proof: Option<Par2Proof>,
}

/// A file read through its segments: a posted file, or an archive member that is itself
/// an archive.
struct Volume {
    name: String,
    parts: Vec<FilePart>,
    /// Where each part's bytes start in the volume.
    starts: Vec<u64>,
    /// Set when the parts hold ciphertext; reads return plaintext.
    crypt: Option<Crypt>,
}

/// One import's access to the providers.
// ponytail: the budget is per attempt; requests of a cancelled attempt that are still
// draining do not count against the next one. Carry the slot in the transport ticket if
// that overshoot ever matters.
struct Job {
    client: Arc<Client>,
    requests: Semaphore,
    progress: Arc<Progress>,
    /// Extra filename globs to drop.
    blocklist: Vec<String>,
    /// Validated bodies by message-id: first and last segments and archive headers, read
    /// again by later steps.
    bodies: Mutex<HashMap<String, Bytes>>,
    /// Archive password from the NZB name or its metadata.
    password: Option<String>,
    /// Keys derived from `password`, once per KDF: volumes of a set share one salt.
    keys: Mutex<HashMap<Kdf, Arc<tokio::sync::OnceCell<Derived>>>>,
    /// Request time by what it waited on; `budget` is a slot of [`CONCURRENCY`].
    waits: Mutex<Causes>,
    /// Set for a dry run: the inspection's budget and what the planner saw.
    inspection: Option<Arc<inspect::Run>>,
}

impl Job {
    fn blocks(&self, name: &str) -> bool {
        names::is_blocked(name, &self.blocklist)
    }

    fn bodies(&self) -> MutexGuard<'_, HashMap<String, Bytes>> {
        self.bodies.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Takes a request slot, traced from now until [`Job::done`].
    async fn slot(&self) -> (Option<tokio::sync::SemaphorePermit<'_>>, Probe) {
        let probe = Probe {
            trace: Some(Arc::new(Trace::new(REQUEST_EVENTS))),
            wait: None,
            watch: None,
            segment: 0,
            hedge: false,
            size: 0,
        };
        probe.step(trace::Step::Queued);
        let slot = self.requests.acquire().await.ok();
        probe.step(trace::Step::Budgeted);
        (slot, probe)
    }

    fn done(&self, probe: &Probe) {
        if let Some(trace) = &probe.trace {
            let (records, _) = trace.records();
            self.waits
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .add_waits(&records);
        }
    }
}

/// A [`Verifier`]'s requests, within the job's budget and progress, reusing the bodies
/// earlier steps verified.
impl Articles for Job {
    fn stat<'a>(&'a self, id: &'a str) -> ArticleFuture<'a, ()> {
        Box::pin(async move {
            let (_slot, probe) = self.slot().await;
            self.progress.request();
            let found = self.client.stat(id, Workload::Queue, Some(&probe)).await;
            self.progress.answer();
            self.done(&probe);
            found
        })
    }

    fn body<'a>(&'a self, id: &'a str, begin: Option<u64>) -> ArticleFuture<'a, Bytes> {
        Box::pin(async move {
            let cached = self.bodies().get(id).cloned();
            if let Some(data) = cached {
                return Ok(data);
            }
            let (_slot, probe) = self.slot().await;
            self.progress.request();
            let body = self
                .client
                .body_with(id, Workload::Queue, Some(&probe), None, begin)
                .await;
            self.progress.answer();
            self.done(&probe);
            Ok(body?.data)
        })
    }
}

/// One attempt's access to the providers; with `inspection` it is a dry run.
fn job(
    client: &Arc<Client>,
    rules: &Rules,
    password: Option<&str>,
    progress: Arc<Progress>,
    inspection: Option<Arc<inspect::Run>>,
) -> Arc<Job> {
    Arc::new(Job {
        client: Arc::clone(client),
        requests: Semaphore::new(CONCURRENCY),
        progress,
        blocklist: rules.blocklist.clone(),
        bodies: Mutex::default(),
        password: password.map(str::to_owned),
        keys: Mutex::default(),
        waits: Mutex::default(),
        inspection,
    })
}

/// The files of `nzb` before filtering and naming: first articles, PAR2 names, segment
/// layouts, then archive members. `at` gets the time each step ended; `percent` only
/// sizes the progress estimate.
async fn plan(
    job: &Arc<Job>,
    nzb: &Nzb,
    percent: u8,
    started: Instant,
    at: &mut Timings,
) -> Result<Vec<Output>, Error> {
    let data: Vec<_> = nzb
        .files
        .iter()
        .filter(|f| names::classify(f.name()) != Kind::Par2)
        .collect();
    let rest = data.iter().filter(|f| f.segments.len() > 1).count()
        + checks(
            data.iter().map(|f| f.segments.len()).sum(),
            data.iter()
                .filter(|f| media::kind(f.name()).is_some())
                .count()
                .max(1),
            percent,
        );
    job.progress.plan(Step::Headers, nzb.files.len() + rest);
    let firsts = join_all(nzb.files.iter().map(|f| {
        let (job, id) = (Arc::clone(job), f.segments[0].id.clone());
        async move { fetch(&job, &id, Some(0)).await }
    }))
    .await
    .into_iter()
    .collect::<Result<Vec<_>, _>>()?;
    at.headers = Some(started.elapsed());

    job.progress.plan(Step::Par2, rest);
    let par2 = read_par2(job, nzb, &firsts).await?;
    at.par2 = Some(started.elapsed());
    let inputs: Vec<Input> = nzb
        .files
        .iter()
        .zip(firsts)
        .map(|(file, first)| {
            let matched = first.as_ref().and_then(|b| {
                let bytes = file.bytes();
                let whole_16k = b.data.len() >= 16 << 10 || b.data.len() as u64 == b.file_size;
                if b.offset != 0 || !whole_16k {
                    return None;
                }
                let hash = infinidysk_par2::hash_16k(&b.data);
                par2.iter().find(|(_, f)| {
                    f.hash_16k == hash && f.length <= bytes && f.length * 100 >= bytes * 95
                })
            });
            let name = matched.map_or_else(|| file.name().to_owned(), |(_, f)| f.name.clone());
            let kind = match (names::classify(&name), first.as_ref()) {
                (Kind::Par2, _) => Kind::Par2,
                (kind, None) => kind,
                (kind, Some(b)) => match (kind, infinidysk_archive::detect(&b.data)) {
                    (kind @ Kind::Rar { .. }, Some(Format::Rar4 | Format::Rar5)) => kind,
                    (Kind::Rar { .. }, _) => Kind::Plain,
                    (Kind::Plain, Some(Format::Rar4 | Format::Rar5)) => Kind::Rar {
                        set: String::new(),
                        volume: u32::MAX,
                    },
                    (kind, _) => kind,
                },
            };
            Input {
                name,
                source: if matched.is_some() {
                    NameSource::Par2
                } else {
                    NameSource::Subject
                },
                kind,
                ids: file.segments.iter().map(|s| s.id.clone()).collect(),
                first,
                proof: matched.and_then(|(slice_size, f)| proof(*slice_size, f)),
            }
        })
        .collect();

    let wanted: Vec<usize> = (0..inputs.len())
        .filter(|&i| {
            let input = &inputs[i];
            match input.kind {
                Kind::Par2 => false,
                Kind::Plain => !job.blocks(&input.name),
                _ => true,
            }
        })
        .collect();
    let wanted_media = wanted
        .iter()
        .filter(|&&i| media::kind(&inputs[i].name).is_some())
        .count()
        .max(1);
    let wanted_segments: usize = wanted.iter().map(|&i| inputs[i].ids.len()).sum();
    let downstream = checks(wanted_segments, wanted_media, percent);
    job.progress.plan(
        Step::Layouts,
        wanted.iter().filter(|&&i| inputs[i].ids.len() > 1).count() + downstream,
    );
    let layouts = join_all(wanted.iter().map(|&i| {
        let job = Arc::clone(job);
        let input = &inputs[i];
        let (name, ids, first, proof) = (
            input.name.clone(),
            input.ids.clone(),
            input.first.clone(),
            input.proof.clone(),
        );
        let gap = nzb.files[i].gap;
        // RAR headers say how much of each volume the members use, so a volume cut short
        // fails when its sets are read.
        let archive = matches!(input.kind, Kind::Rar { .. });
        async move {
            let Some(first) = first else {
                // The first listed segment may be a later part: the NZB, not the
                // providers, lacks the start of the file.
                return Err(match fetch(&job, &ids[0], None).await? {
                    Some(b) if b.offset != 0 => lacks(&name),
                    _ => missing(&name),
                });
            };
            layout(&job, name, ids, first, proof, gap, archive).await
        }
    }))
    .await;
    let layouts_at = started.elapsed();
    let mut volumes: BTreeMap<usize, Volume> = BTreeMap::new();
    for (&i, layout) in wanted.iter().zip(layouts) {
        match layout {
            Ok(volume) => {
                volumes.insert(i, volume);
            }
            // A sidecar may go missing; media files and archive volumes fail the release.
            Err(error @ Error::Missing(_))
                if inputs[i].kind == Kind::Plain && media::kind(&inputs[i].name).is_none() =>
            {
                tracing::warn!(file = %inputs[i].name, reason = %error, "dropping file");
            }
            Err(error) => return Err(error),
        }
    }
    // Set only now: a layout that failed above fails this step.
    at.layouts = Some(layouts_at);

    // Archive and split sets, keyed by kind and set name, volumes in name order.
    let mut sets: BTreeMap<(u8, String), Vec<(u32, usize)>> = BTreeMap::new();
    let mut outputs = Vec::new();
    for &i in &wanted {
        match &inputs[i].kind {
            Kind::Rar { set, volume } => {
                sets.entry((0, set.clone())).or_default().push((*volume, i));
            }
            Kind::SevenZip { set, volume } => {
                sets.entry((1, set.clone())).or_default().push((*volume, i));
            }
            Kind::Split { set, volume } => {
                sets.entry((2, set.clone())).or_default().push((*volume, i));
            }
            Kind::Par2 => {}
            Kind::Plain => {
                let origin = Origin {
                    source: inputs[i].source,
                    set: None,
                };
                outputs.extend(volumes.remove(&i).and_then(|v| plain(v, origin)));
            }
        }
    }
    // Releases first bodies no later step reads (PAR2, skipped files) before the
    // archive reads; wanted files keep theirs in `job.bodies`.
    drop(inputs);
    job.progress.plan(Step::Archives, sets.len() + downstream);
    for ((kind, _), mut members) in sets {
        members.sort_unstable();
        let vols: Vec<Volume> = members
            .iter()
            .filter_map(|(_, i)| volumes.remove(i))
            .collect();
        let numbers: Vec<u32> = members.iter().map(|(n, _)| *n).collect();
        let archive = match kind {
            0 => ArchiveType::Rar5,
            1 => ArchiveType::SevenZip,
            _ => ArchiveType::Split,
        };
        let set = job.open_set(archive, 0, &[])?;
        let found = match kind {
            0 => rar_set(job, vols, set).await?,
            1 => sevenzip_set(job, vols, &numbers, set).await?,
            _ => {
                let origin = Origin {
                    source: inputs[members[0].1].source,
                    set,
                };
                split_set(vols, origin).into_iter().collect()
            }
        };
        outputs.extend(expand(job, found, 1).await?);
    }
    at.archives = Some(started.elapsed());
    Ok(outputs)
}

/// Imports `nzb` into `category`. Non-media files with missing articles are left out;
/// missing media data, broken archives and media of the wrong kind fail the import.
/// Obfuscated names are replaced using `release`. Encrypted archives open with
/// `password`. Every provider request is counted in `progress`. At a 100 % sample,
/// `verifier` runs each output's first health check, returned by output index.
/// A job holding only an NZB file returns that file instead, as SABnzbd queues it.
#[allow(
    clippy::too_many_arguments,
    reason = "one caller; each is a separate input of the attempt"
)]
pub async fn import(
    client: &Arc<Client>,
    nzb: &Nzb,
    release: &str,
    category: &str,
    rules: &Rules,
    password: Option<&str>,
    progress: Arc<Progress>,
    verifier: Option<&dyn Verifier>,
) -> Result<Imported, Error> {
    let started = Instant::now();
    let job = &job(client, rules, password, progress, None);
    let mut at = Timings::default();
    let outputs = plan(job, nzb, rules.check_percent, started, &mut at).await?;
    let [firsts_at, par2_at, layouts_at, sets_at] =
        [at.headers, at.par2, at.layouts, at.archives].map(Option::unwrap_or_default);

    if let Some(inner) = inner_nzb(job, &outputs).await? {
        return Ok(Imported::Nzb(inner));
    }
    let outputs = filter(outputs, job);
    if outputs.is_empty() {
        return Err(failed("No importable files found."));
    }
    let required = rules.owners.get(&category.to_lowercase());
    require(&outputs, required.map_or(&[], Vec::as_slice))?;
    let outputs = name(outputs, release, usize::MAX).await;
    let outputs = check_articles(job, outputs, rules.check_percent).await?;
    let check_at = started.elapsed();
    // Every article answered STAT just now; only the health check's reads remain.
    let verifier = verifier.filter(|_| rules.check_percent >= 100);
    let first_checks = verifier.map_or(0, |_| {
        let checkable = outputs
            .iter()
            .filter(|o| o.path.last().is_some_and(|n| media::is_checkable(n)))
            .count();
        FIRST_CHECK_REQUESTS * checkable
    });
    job.progress
        .plan(Step::Verifying, 2 * media_outputs(&outputs) + first_checks);
    ready(job, &outputs).await?;
    let ready_at = started.elapsed();
    let first_checks = match verifier {
        Some(verifier) => {
            let posted = nzb
                .files
                .iter()
                .filter_map(|f| f.date)
                .min()
                .map(|s| s.saturating_mul(1000));
            verify(job, &outputs, verifier, posted).await?
        }
        None => Vec::new(),
    };
    let ms = |from: Duration, to: Duration| {
        u64::try_from(to.saturating_sub(from).as_millis()).unwrap_or(u64::MAX)
    };
    let waits = *job.waits.lock().unwrap_or_else(PoisonError::into_inner);
    let wait_ms = |ns: u64| ns / 1_000_000;
    tracing::debug!(
        files = nzb.files.len(),
        firsts_ms = ms(Duration::ZERO, firsts_at),
        par2_ms = ms(firsts_at, par2_at),
        layouts_ms = ms(par2_at, layouts_at),
        sets_ms = ms(layouts_at, sets_at),
        check_ms = ms(sets_at, check_at),
        ready_ms = ms(check_at, ready_at),
        verify_ms = ms(ready_at, started.elapsed()),
        requests = job.progress.issued.load(Ordering::Relaxed),
        slot_ms = wait_ms(waits.budget),
        permit_ms = wait_ms(waits.permit),
        connection_ms = wait_ms(waits.connection),
        pipeline_ms = wait_ms(waits.pipeline),
        reply_ms = wait_ms(waits.latency),
        transfer_ms = wait_ms(waits.transfer),
        decode_ms = wait_ms(waits.decode),
        "import steps"
    );
    Ok(Imported::Files {
        outputs,
        checks: first_checks,
    })
}

/// The NZB file a job holds in place of a release: every file that is not blocked is an
/// NZB. `None` when the job holds anything else
/// (`infinidysk-lab:docs/import-policy.md` §5.2.1).
async fn inner_nzb(job: &Job, outputs: &[Output]) -> Result<Option<Vec<u8>>, Error> {
    let is_nzb = |name: &str| {
        name.rsplit_once('.')
            .is_some_and(|(_, ext)| ext.eq_ignore_ascii_case("nzb"))
    };
    let mut nzbs = Vec::new();
    for output in outputs {
        let Some(name) = output.path.last() else {
            continue;
        };
        if job.blocks(name) {
            continue;
        }
        if !is_nzb(name) {
            return Ok(None);
        }
        nzbs.push((name, &output.meta));
    }
    let (name, meta) = match nzbs[..] {
        [] => return Ok(None),
        [one] => one,
        _ => {
            return Err(failed(
                "The download contains several NZB files; only one is supported.",
            ));
        }
    };
    if meta.size() > nzb::MAX_NZB {
        return Err(failed(format!("{name}: {}", nzb::Error::TooLarge)));
    }
    let data = Volume::of(name.clone(), meta)
        .read(job, 0, meta.size())
        .await?;
    Ok(Some(data))
}

/// Runs `futures` concurrently, returning results in input order. Requests are bounded by
/// the job's budget in [`fetch`] and [`stat`], so nested calls do not multiply it.
/// At most [`WINDOW`] of them run at once.
async fn join_all<'a, T, F, I>(futures: I) -> Vec<T>
where
    I: IntoIterator<Item = F>,
    I::IntoIter: Send + 'a,
    F: Future<Output = T> + Send + 'static,
    T: Send + 'static,
{
    // Erased: a caller's borrowing closure held across `.await` defeats the `Send` proof.
    let mut pending: Box<dyn Iterator<Item = (usize, F)> + Send + 'a> =
        Box::new(futures.into_iter().enumerate());
    let mut set = JoinSet::new();
    let mut out: Vec<Option<T>> = Vec::new();
    loop {
        while set.len() < WINDOW {
            let Some((index, future)) = pending.next() else {
                break;
            };
            out.push(None);
            set.spawn(async move { (index, future.await) });
        }
        let Some(joined) = set.join_next().await else {
            break;
        };
        match joined {
            Ok((index, value)) => out[index] = Some(value),
            Err(error) => {
                if let Ok(panic) = error.try_into_panic() {
                    std::panic::resume_unwind(panic);
                }
            }
        }
    }
    out.into_iter()
        .map(|v| v.expect("tasks are never aborted and panics were re-raised"))
        .collect()
}

/// A body, or `None` when every provider lacks it or holds it corrupt. With `begin`, an
/// article for another part of the posted file counts as corrupt.
async fn fetch(job: &Job, id: &str, begin: Option<u64>) -> Result<Option<Body>, Error> {
    use infinidysk_nntp::Error as E;
    let (_slot, probe) = job.slot().await;
    // After the slot: only the requests in flight can pass the budget.
    job.charge()?;
    job.progress.request();
    let body = if job.inspected() {
        job.client
            .body_unremembered(id, Workload::Queue, Some(&probe), begin)
            .await
    } else {
        job.client
            .body_with(id, Workload::Queue, Some(&probe), None, begin)
            .await
    };
    job.progress.answer();
    job.done(&probe);
    match body {
        Ok(body) => {
            job.received(body.data.len())?;
            Ok(Some(body))
        }
        Err(E::NotFound) => {
            job.missed(id);
            Ok(None)
        }
        Err(E::CrcMismatch { .. } | E::Yenc(_) | E::TooLarge | E::InvalidArgument(_)) => Ok(None),
        Err(error) => Err(Error::Retry(error)),
    }
}

/// Whether any provider has the article; inconclusive answers are retried.
async fn stat(job: &Job, id: &str) -> Result<bool, Error> {
    use infinidysk_nntp::Error as E;
    let (_slot, probe) = job.slot().await;
    let mut attempt = 0;
    let found = loop {
        job.progress.request();
        let found = job.client.stat(id, Workload::Queue, Some(&probe)).await;
        job.progress.answer();
        match found {
            Ok(()) => break Ok(true),
            Err(E::NotFound | E::InvalidArgument(_)) => break Ok(false),
            Err(error) => {
                attempt += 1;
                if attempt == STAT_ATTEMPTS {
                    break Err(Error::Retry(error));
                }
            }
        }
    };
    job.done(&probe);
    found
}

/// Files described by every PAR2 index file of the release, with their set's slice size;
/// the smallest recovery volume stands in when no index file describes any.
async fn read_par2(
    job: &Job,
    nzb: &Nzb,
    firsts: &[Option<Body>],
) -> Result<Vec<(u64, infinidysk_par2::File)>, Error> {
    let mut candidates: Vec<usize> = (0..firsts.len())
        .filter(|&i| {
            firsts[i]
                .as_ref()
                .is_some_and(|b| b.data.starts_with(PAR2_MAGIC))
        })
        .collect();
    candidates.sort_by_key(|&i| {
        let file = &nzb.files[i];
        (
            file.name().to_ascii_lowercase().contains(".vol"),
            file.segments.len(),
        )
    });
    let mut files: Vec<(u64, infinidysk_par2::File)> = Vec::new();
    let mut seen = HashSet::new();
    let (mut articles, mut bytes) = (0, 0);
    for i in candidates {
        let vol = nzb.files[i].name().to_ascii_lowercase().contains(".vol");
        if vol && !files.is_empty() {
            break;
        }
        let mut reader = infinidysk_par2::Reader::default();
        if let Some(b) = &firsts[i] {
            reader.push(&b.data);
        }
        for segment in nzb.files[i]
            .segments
            .iter()
            .skip(1)
            .take(PAR2_MAX_ARTICLES - 1)
        {
            if reader.reached_recovery()
                || reader.buffered() >= PAR2_MAX_BYTES
                || articles >= PAR2_TOTAL_ARTICLES
            {
                break;
            }
            let Some(body) = fetch(job, &segment.id, None).await? else {
                break;
            };
            articles += 1;
            if reader.buffered() + body.data.len() > PAR2_MAX_BYTES
                || bytes + reader.buffered() + body.data.len() > PAR2_TOTAL_BYTES
            {
                break;
            }
            reader.push(&body.data);
        }
        bytes += reader.buffered();
        let index = reader.index();
        for f in index.files {
            if seen.insert(f.id) {
                files.push((index.slice_size, f));
            }
        }
    }
    Ok(files)
}

fn proof(slice_size: u64, f: &infinidysk_par2::File) -> Option<Par2Proof> {
    Some(Par2Proof {
        file_length: f.length,
        slice_size: u32::try_from(slice_size).ok()?,
        slice_md5: f.slices.iter().map(|s| s.md5).collect(),
        slice_crc32: f.slices.iter().map(|s| s.crc32).collect(),
        file_id: f.id,
        file_md5: f.hash,
        md5_16k: f.hash_16k,
    })
}

/// Decoded segment sizes. Posters split files into equal parts, so the first part's size
/// and the file size give every offset; the last segment's yEnc header confirms it. Some
/// posters write a wrong `=ybegin size=` but right offsets: a last segment ending past it
/// counts when the file is a RAR volume or matches its PAR2 length. `gap`: the NZB skips
/// part numbers.
async fn layout(
    job: &Arc<Job>,
    name: String,
    ids: Vec<String>,
    first: Body,
    proof: Option<Par2Proof>,
    gap: bool,
    archive: bool,
) -> Result<Volume, Error> {
    let n = ids.len() as u64;
    let part = first.data.len() as u64;
    let total = first.file_size;
    let last = total
        .checked_sub(part * (n - 1))
        .filter(|&last| first.offset == 0 && last > 0 && last <= part);
    let proven = proof.as_ref().map(|p| p.file_length);
    let complete = |end: u64| end == total || proven == Some(end) || (archive && end > total);
    // Known short: measuring it would download every segment only to fail.
    let mut short = gap || first.total.is_some_and(|t| u64::from(t) > n);
    let head = ids[0].clone();
    let tail = usize::try_from(n - 1).expect("segment count came from a Vec");
    let uniform = |ids: Vec<String>, last: u64| -> Vec<(String, u64)> {
        ids.into_iter()
            .zip(1..)
            .map(|(id, i)| (id, if i == n { last } else { part }))
            .collect()
    };
    let mut cached = Vec::new();
    let mut trusted = false;
    let sizes: Vec<(String, u64)> = match last {
        Some(last) if n == 1 => {
            trusted = true;
            ids.into_iter().map(|id| (id, last)).collect()
        }
        // No expected offset: a wrong part must reach `measure`, not look missing.
        _ if n > 1 && first.offset == 0 && (last.is_some() || archive || proven.is_some()) => {
            match (last, fetch(job, &ids[tail], None).await?) {
                (Some(last), Some(b))
                    if b.offset == part * (n - 1) && b.data.len() as u64 == last =>
                {
                    cached.push((ids[tail].clone(), b.data));
                    trusted = true;
                    uniform(ids, last)
                }
                // The STAT check confirms the miss and drops the file.
                (Some(last), None) => uniform(ids, last),
                (_, Some(b))
                    if b.offset == part * (n - 1)
                        && (1..=part).contains(&(b.data.len() as u64))
                        && b.offset + b.data.len() as u64 > total
                        && complete(b.offset + b.data.len() as u64) =>
                {
                    tracing::debug!(file = name, claimed = total, "posted size disproved");
                    let last = b.data.len() as u64;
                    cached.push((ids[tail].clone(), b.data));
                    trusted = true;
                    uniform(ids, last)
                }
                (_, b) => {
                    if (archive || proven.is_some())
                        && b.is_some_and(|b| b.offset + b.data.len() as u64 > total)
                    {
                        short = gap;
                    }
                    measure(job, &name, &ids, complete, short).await?
                }
            }
        }
        _ => measure(job, &name, &ids, complete, short).await?,
    };
    cached.push((head, first.data));
    job.bodies().extend(cached);
    let end = sizes.iter().map(|&(_, size)| size).sum();
    let segments: Vec<Segment> = sizes
        .into_iter()
        .map(|(id, size)| Segment {
            id,
            size: u32::try_from(size).unwrap_or(u32::MAX),
            fallbacks: Vec::new(),
        })
        .collect();
    Ok(Volume::new(
        name,
        vec![FilePart {
            segments: Segments {
                items: segments,
                sizes_trusted: trusted,
                missing: Vec::new(),
                corrupt: Vec::new(),
            },
            offset: 0,
            data: ByteRange { start: 0, end },
            split_after: false,
            proof,
        }],
    ))
}

/// Sizes of unevenly split files, read from every segment's yEnc header. Entries whose
/// bytes earlier ones already hold (reposts, extra entries), found or missing, are
/// dropped: SABnzbd writes articles by offset and ignores them too.
// ponytail: downloads every body of an unevenly split file once at import; read only the
// yEnc headers (or probe lazily while streaming) if such posts turn out to be common.
// ponytail: a surplus entry listed before the part it repeats still fails; sort by offset
// if such NZBs show up.
async fn measure(
    job: &Arc<Job>,
    name: &str,
    ids: &[String],
    complete: impl Fn(u64) -> bool,
    short: bool,
) -> Result<Vec<(String, u64)>, Error> {
    if short {
        return Err(lacks(name));
    }
    let bodies = join_all(ids.iter().map(|id| {
        let (job, id) = (Arc::clone(job), id.clone());
        async move {
            fetch(&job, &id, None)
                .await
                .map(|b| b.map(|b| (b.offset, b.data.len() as u64)))
        }
    }))
    .await;
    let mut sizes = Vec::with_capacity(ids.len());
    let mut end = 0;
    let mut lost = false;
    let fail = |lost| if lost { missing(name) } else { lacks(name) };
    for (id, body) in ids.iter().zip(bodies) {
        let Some((mut offset, mut len)) = body? else {
            lost = true;
            continue;
        };
        if offset != end {
            // A provider served another part under this id; the others may hold the right one.
            if let Some(b) = fetch(job, id, Some(end)).await? {
                (offset, len) = (b.offset, b.data.len() as u64);
            }
        }
        if offset != end && offset + len <= end {
            tracing::debug!(file = name, "dropped a segment that repeats earlier bytes");
            continue;
        }
        if offset != end {
            return Err(fail(lost));
        }
        end += len;
        sizes.push((id.clone(), len));
    }
    if !complete(end) {
        return Err(fail(lost));
    }
    Ok(sizes)
}

impl Volume {
    fn new(name: String, parts: Vec<FilePart>) -> Self {
        let starts = parts
            .iter()
            .scan(0, |start, p| {
                let at = *start;
                *start += p.data.end - p.data.start;
                Some(at)
            })
            .collect();
        Self {
            name,
            parts,
            starts,
            crypt: None,
        }
    }

    /// The file `meta` describes, read as `name`.
    fn of(name: String, meta: &FileMeta) -> Self {
        let (parts, crypt) = match meta {
            FileMeta::Nzb(f) => (
                vec![FilePart {
                    segments: f.segments.clone(),
                    offset: 0,
                    data: ByteRange {
                        start: 0,
                        end: f.segments.size(),
                    },
                    split_after: false,
                    proof: f.proof.clone(),
                }],
                None,
            ),
            FileMeta::Multipart(m) => (m.parts.clone(), m.crypt.clone()),
        };
        Self {
            crypt,
            ..Self::new(name, parts)
        }
    }

    /// Posted bytes, ciphertext when encrypted.
    fn size(&self) -> u64 {
        self.parts.iter().map(|p| p.data.end - p.data.start).sum()
    }

    /// Bytes [`Self::read`] returns.
    fn plain_size(&self) -> u64 {
        self.crypt.as_ref().map_or_else(|| self.size(), |c| c.size)
    }

    /// For each part overlapping volume bytes `range`: the part and the overlap in the
    /// coordinates of its segments.
    fn overlaps(&self, range: Range<u64>) -> impl Iterator<Item = (&FilePart, Range<u64>)> {
        self.parts
            .iter()
            .zip(&self.starts)
            .filter_map(move |(p, &start)| {
                let end = start + (p.data.end - p.data.start);
                let (lo, hi) = (range.start.max(start), range.end.min(end));
                (lo < hi).then(|| (p, p.data.start + lo - start..p.data.start + hi - start))
            })
    }

    /// Bytes `offset..offset + len`, cut at the end of the file; decrypted when encrypted.
    async fn read(&self, job: &Job, offset: u64, len: u64) -> Result<Vec<u8>, Error> {
        let Some(crypt) = &self.crypt else {
            return self.read_raw(job, offset, len).await;
        };
        let end = offset.saturating_add(len).min(crypt.size);
        if offset >= end {
            return Ok(Vec::new());
        }
        // CBC: the block before the first one wanted is its IV.
        let block = offset - offset % BLOCK as u64;
        let from = block.saturating_sub(BLOCK as u64);
        let to = end.next_multiple_of(BLOCK as u64);
        let mut data = self.read_raw(job, from, to - from).await?;
        if (data.len() as u64) < to - from {
            return Err(Error::Missing(format!(
                "{}: archive volumes end before the encrypted file",
                self.name
            )));
        }
        let mut iv = crypt.iv;
        if block > 0 {
            iv.copy_from_slice(&data[..BLOCK]);
            data.drain(..BLOCK);
        }
        Cipher::new(&crypt.key).decrypt(&mut iv, &mut data);
        let within = usize::try_from(offset - block).expect("less than a block");
        let n = usize::try_from(end - offset).expect("read into memory");
        data.truncate(within + n);
        data.drain(..within);
        Ok(data)
    }

    /// Posted bytes `offset..offset + len`, cut at the end of the volume.
    async fn read_raw(&self, job: &Job, offset: u64, len: u64) -> Result<Vec<u8>, Error> {
        let end = offset.saturating_add(len).min(self.size());
        let mut out = Vec::new();
        for (part, range) in self.overlaps(offset..end) {
            let mut at = part.offset;
            for segment in &part.segments.items {
                let next = at + u64::from(segment.size);
                if next > range.start && at < range.end {
                    let from = range.start.max(at) - at;
                    let to = range.end.min(next) - at;
                    let begin = part.segments.sizes_trusted.then_some(at);
                    out.extend(self.segment(job, segment, begin, from..to).await?);
                }
                if next >= range.end {
                    break;
                }
                at = next;
            }
        }
        Ok(out)
    }

    /// Bytes `range` of one segment's body, fetched once per job.
    async fn segment(
        &self,
        job: &Job,
        segment: &Segment,
        begin: Option<u64>,
        range: Range<u64>,
    ) -> Result<Vec<u8>, Error> {
        let slice = |data: &[u8]| {
            let r = usize::try_from(range.start).expect("within a segment in memory")
                ..usize::try_from(range.end).expect("within a segment in memory");
            data[r].to_vec()
        };
        if let Some(data) = job.bodies().get(&segment.id) {
            return Ok(slice(data));
        }
        let body = fetch(job, &segment.id, begin)
            .await?
            .ok_or_else(|| missing(&self.name))?;
        if body.data.len() as u64 != u64::from(segment.size) {
            return Err(failed(format!(
                "{}: segment sizes disagree with the yEnc headers",
                self.name
            )));
        }
        let out = slice(&body.data);
        job.bodies().insert(segment.id.clone(), body.data);
        Ok(out)
    }

    /// The parts covering volume bytes `range`, each cut to the segments it needs.
    fn part(&self, range: Range<u64>, split_after: bool) -> Result<Vec<FilePart>, Error> {
        if range.end > self.size() {
            return Err(Error::Missing(format!(
                "{}: archive headers point past the end of the volume",
                self.name
            )));
        }
        let pieces: Vec<_> = self.overlaps(range).collect();
        let count = pieces.len();
        Ok(pieces
            .into_iter()
            .enumerate()
            .map(|(i, (part, range))| {
                let mut at = part.offset;
                let (mut first, mut offset, mut last) = (None, 0, 0);
                for (s, segment) in part.segments.items.iter().enumerate() {
                    let next = at + u64::from(segment.size);
                    if first.is_none() && next > range.start {
                        (first, offset) = (Some(s), at);
                    }
                    last = s;
                    if next >= range.end {
                        break;
                    }
                    at = next;
                }
                FilePart {
                    segments: Segments {
                        items: part.segments.items[first.unwrap_or(last)..=last].to_vec(),
                        sizes_trusted: part.segments.sizes_trusted,
                        missing: Vec::new(),
                        corrupt: Vec::new(),
                    },
                    offset,
                    data: ByteRange {
                        start: range.start,
                        end: range.end,
                    },
                    split_after: split_after || i + 1 < count,
                    proof: part.proof.clone(),
                }
            })
            .collect())
    }
}

fn path_of(name: &str) -> Vec<String> {
    name.split(['/', '\\'])
        .filter_map(names::sanitize)
        .collect()
}

/// A posted file whole; its volume has a single part.
fn plain(v: Volume, origin: Origin) -> Option<Output> {
    let path = path_of(&v.name);
    let part = v.parts.into_iter().next()?;
    (!path.is_empty()).then_some(Output {
        path,
        meta: FileMeta::Nzb(NzbFile {
            segments: part.segments,
            proof: part.proof,
        }),
        origin,
    })
}

/// Checks a member's flags before it is imported. `encrypted` means with a scheme we
/// cannot decrypt.
fn streamable(name: &str, kind: &str, stored: bool, encrypted: bool) -> Result<(), Error> {
    if !stored {
        return Err(Error::Unsupported(format!(
            "{name}: compressed {kind} archives cannot be streamed; \
             only stored (uncompressed) archive contents are supported."
        )));
    }
    if encrypted {
        return Err(Error::Unsupported(format!(
            "{name}: {kind} encryption older than AES is not supported."
        )));
    }
    Ok(())
}

/// The key for `kdf` from the job's password, derived once per job off the runtime.
async fn key(job: &Job, name: &str, kdf: &Kdf) -> Result<Derived, Error> {
    let Some(password) = &job.password else {
        return Err(Error::Password(format!(
            "{name}: the archive is encrypted and the NZB has no password. \
             Add it to the NZB name as {{{{password}}}}."
        )));
    };
    let cell = Arc::clone(
        job.keys
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .entry(kdf.clone())
            .or_default(),
    );
    let derived = cell
        .get_or_try_init(|| {
            let (kdf, password) = (kdf.clone(), password.clone());
            async move {
                let permit = DERIVATIONS
                    .acquire()
                    .await
                    .expect("DERIVATIONS is a static semaphore that is never closed");
                // A blocking task outlives a dropped caller (an inspection deadline, a
                // client that went away); this stops its rounds so it frees the slot.
                let stop = StopOnDrop::default();
                let flag = Arc::clone(&stop.0);
                match tokio::task::spawn_blocking(move || {
                    let _permit = permit;
                    kdf.derive_until(&password, &flag)
                })
                .await
                {
                    Ok(derived) => derived.map_err(|e| archive_error(name, &e)),
                    Err(e) => Err(failed(format!("{name}: key derivation failed: {e}"))),
                }
            }
        })
        .await?;
    Ok(derived.clone())
}

/// Sets its flag when dropped, which tells a key derivation to stop.
#[derive(Default)]
struct StopOnDrop(Arc<AtomicBool>);

impl Drop for StopOnDrop {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Relaxed);
    }
}

/// How to decrypt a member of plaintext `size` encrypted with `enc`.
async fn member_crypt(job: &Job, name: &str, enc: &Encryption, size: u64) -> Result<Crypt, Error> {
    let derived = key(job, name, &enc.kdf).await?;
    Ok(Crypt {
        key: derived.key.clone(),
        iv: enc.iv(&derived),
        size,
    })
}

struct Member {
    name: String,
    size: Option<u64>,
    stored: bool,
    encrypted: bool,
    /// From the first volume: the whole member is one CBC chain.
    crypt: Option<Encryption>,
    open: bool,
    parts: Vec<FilePart>,
}

async fn rar_set(
    job: &Arc<Job>,
    vols: Vec<Volume>,
    set: Option<usize>,
) -> Result<Vec<Output>, Error> {
    let scans = join_all(vols.into_iter().map(|vol| {
        let job = Arc::clone(job);
        // A wrong password fails the key (RAR5) or the header decode (RAR4).
        async move {
            scan_rar(&job, vol, set)
                .await
                .inspect_err(|error| job.lock(set, error))
        }
    }))
    .await
    .into_iter()
    .collect::<Result<Vec<_>, _>>()?;
    job.note_set(set, ArchiveSet::scanned);
    let mut scans = scans;
    // Repeated numbers are interleaved sets; member names keep them apart.
    if scans.iter().all(|(_, v, _)| v.number.is_some()) {
        scans.sort_by_key(|(_, v, _)| v.number);
    }

    let mut members: Vec<Member> = Vec::new();
    let mut headless = HashSet::new();
    for (vol, _, entries) in &scans {
        for e in entries {
            let part = vol.part(e.data.clone(), e.split_after)?;
            let opened = members
                .iter()
                .rposition(|m| e.split_before && m.open && m.name == e.name);
            let member = if let Some(i) = opened {
                &mut members[i]
            } else {
                // A continuation of a member no earlier volume opened.
                if e.split_before {
                    headless.insert(members.len());
                }
                members.push(Member {
                    name: e.name.clone(),
                    size: e.size,
                    stored: true,
                    encrypted: false,
                    crypt: e.crypt.clone(),
                    open: false,
                    parts: Vec::new(),
                });
                members.last_mut().expect("pushed above")
            };
            member.parts.extend(part);
            member.stored &= e.stored;
            member.encrypted |= e.encrypted;
            member.open = e.split_after;
            member.size = e.size.or(member.size);
        }
    }

    let mut out = Vec::new();
    for (i, m) in members.into_iter().enumerate() {
        // A member never imported needs no streamable flags.
        if job.blocks(&m.name) {
            continue;
        }
        if m.open || headless.contains(&i) {
            // Like a missing sidecar: an incomplete plain non-media member is left out.
            let leaf = m.name.rsplit(['/', '\\']).next().unwrap_or(&m.name);
            if names::classify(leaf) == Kind::Plain && media::kind(leaf).is_none() {
                tracing::warn!(member = %m.name, reason = "RAR volumes are missing", "leaving out incomplete archive member");
                continue;
            }
            return Err(Error::Missing(format!(
                "{}: RAR volumes are missing",
                m.name
            )));
        }
        if m.encrypted {
            job.note_set(set, |s| s.seen(Encrypted::Data));
        }
        if let Err(error) = streamable(&m.name, "RAR", m.stored, m.encrypted && m.crypt.is_none()) {
            job.unreadable(set, error, &m.name, m.size)?;
            continue;
        }
        let len: u64 = m.parts.iter().map(|p| p.data.len()).sum();
        let crypt = match (&m.crypt, m.size) {
            (Some(enc), Some(size)) if m.encrypted => {
                match member_crypt(job, &m.name, enc, size).await {
                    Ok(crypt) => {
                        job.unlocked(set, &enc.kdf);
                        Some(crypt)
                    }
                    Err(error) => {
                        job.unreadable(set, error, &m.name, m.size)?;
                        continue;
                    }
                }
            }
            _ => None,
        };
        // Encrypted data is padded to whole blocks.
        let posted = |size: u64| match &crypt {
            Some(_) => size.next_multiple_of(BLOCK as u64),
            None => size,
        };
        if (m.encrypted && crypt.is_none()) || m.size.is_some_and(|size| posted(size) != len) {
            return Err(Error::Missing(format!(
                "{}: RAR volumes are missing or inconsistent",
                m.name
            )));
        }
        let meta = MultipartFile {
            parts: m.parts,
            crypt,
        };
        let path = path_of(&m.name);
        if !path.is_empty() {
            out.push(Output {
                path,
                meta: FileMeta::Multipart(meta),
                origin: Origin {
                    source: NameSource::ArchiveHeader,
                    set,
                },
            });
        }
    }
    job.note_set(set, ArchiveSet::checked);
    Ok(out)
}

async fn scan_rar(
    job: &Job,
    vol: Volume,
    set: Option<usize>,
) -> Result<(Volume, rar::Volume, Vec<rar::Entry>), Error> {
    let mut scanner = rar::Scanner::new();
    let mut entries = Vec::new();
    let (mut at, mut buf) = (0, Vec::new());
    loop {
        match scanner
            .next(at, &buf)
            .map_err(|e| archive_error(&vol.name, &e))?
        {
            rar::Step::Need { offset, len } => {
                // Volumes may end without an end-of-archive header.
                if offset >= vol.size() {
                    break;
                }
                let have = buf.len() as u64;
                if offset == at && (have >= len || at + have >= vol.size()) {
                    return Err(Error::Missing(format!(
                        "{}: RAR headers run past the end of the volume",
                        vol.name
                    )));
                }
                buf = vol.read(job, offset, len).await?;
                at = offset;
            }
            rar::Step::Entry(entry) => entries.push(entry),
            rar::Step::Key(kdf) => {
                job.note_set(set, |s| {
                    s.rar(scanner.format());
                    s.seen(Encrypted::Headers);
                });
                let derived = key(job, &vol.name, &kdf).await?;
                scanner.add_key(kdf, derived);
            }
            rar::Step::End { .. } => break,
        }
    }
    job.note_set(set, |s| s.rar(scanner.format()));
    let header = scanner.volume().clone();
    Ok((vol, header, entries))
}

fn archive_error(name: &str, error: &infinidysk_archive::Error) -> Error {
    match error {
        infinidysk_archive::Error::WrongPassword => {
            Error::Password(format!("{name}: wrong archive password."))
        }
        other => failed(format!("{name}: {other}")),
    }
}

async fn sevenzip_set(
    job: &Job,
    vols: Vec<Volume>,
    numbers: &[u32],
    set: Option<usize>,
) -> Result<Vec<Output>, Error> {
    let name = vols.first().map(|v| v.name.clone()).unwrap_or_default();
    if numbers.iter().enumerate().any(|(i, n)| *n as usize != i) {
        return Err(Error::Missing(format!("{name}: 7z volumes are missing")));
    }
    let starts: Vec<u64> = vols
        .iter()
        .scan(0, |start, v| {
            let s = *start;
            *start += v.size();
            Some(s)
        })
        .collect();
    let total = starts
        .last()
        .zip(vols.last())
        .map_or(0, |(s, v)| s + v.size());

    let mut reader = sevenzip::Reader::new();
    let (mut at, mut buf) = (0, Vec::new());
    let entries = loop {
        // A wrong password fails the header decode; 7z keys take any password.
        match reader
            .next(at, &buf)
            .map_err(|e| archive_error(&name, &e))
            .inspect_err(|error| job.lock(set, error))?
        {
            sevenzip::Step::Need { offset, len } => {
                if offset.saturating_add(len) > total || (offset == at && buf.len() as u64 >= len) {
                    return Err(Error::Missing(format!(
                        "{name}: 7z headers point past the end of the archive"
                    )));
                }
                buf.clear();
                for (v, &start) in vols.iter().zip(&starts) {
                    let end = start + v.size();
                    if end <= offset || start >= offset + len {
                        continue;
                    }
                    let from = offset.max(start) - start;
                    let to = (offset + len).min(end) - start;
                    buf.extend(v.read(job, from, to - from).await?);
                }
                at = offset;
            }
            sevenzip::Step::Key(kdf) => {
                job.note_set(set, |s| s.seen(Encrypted::Headers));
                let derived = key(job, &name, &kdf)
                    .await
                    .inspect_err(|error| job.lock(set, error))?;
                reader.add_key(kdf, derived);
            }
            sevenzip::Step::Done(entries) => break entries,
        }
    };
    job.note_set(set, ArchiveSet::scanned);

    let mut out = Vec::new();
    for e in entries {
        // Mapping an entry rescans every volume's segments and may not await, so a large
        // archive would otherwise run past the inspection deadline without a cancel point.
        tokio::task::consume_budget().await;
        if job.blocks(&e.name) {
            continue;
        }
        if e.encrypted {
            job.note_set(set, |s| s.seen(Encrypted::Data));
        }
        let parts = match e.data {
            Some(range) => {
                let mut parts = Vec::new();
                for (v, &start) in vols.iter().zip(&starts) {
                    let end = start + v.size();
                    if end <= range.start || start >= range.end {
                        continue;
                    }
                    let local = range.start.max(start) - start..range.end.min(end) - start;
                    parts.extend(v.part(local, range.end > end)?);
                }
                parts
            }
            None if e.size == 0 => Vec::new(),
            None => {
                if let Err(error) = streamable(&e.name, "7z", false, e.encrypted) {
                    job.unreadable(set, error, &e.name, Some(e.size))?;
                }
                continue;
            }
        };
        let crypt = match &e.crypt {
            Some(enc) => match member_crypt(job, &e.name, enc, e.size).await {
                Ok(crypt) => Some(crypt),
                Err(error) => {
                    job.unreadable(set, error, &e.name, Some(e.size))?;
                    continue;
                }
            },
            None => None,
        };
        let path = path_of(&e.name);
        if !path.is_empty() {
            out.push(Output {
                path,
                meta: FileMeta::Multipart(MultipartFile { parts, crypt }),
                origin: Origin {
                    source: NameSource::ArchiveHeader,
                    set,
                },
            });
        }
    }
    job.note_set(set, ArchiveSet::checked);
    Ok(out)
}

fn split_set(vols: Vec<Volume>, origin: Origin) -> Option<Output> {
    let name = vols.first()?.name.clone();
    let (stem, _) = name.rsplit_once('.')?;
    let path = path_of(stem);
    let count = vols.len();
    let mut parts = Vec::with_capacity(count);
    for (i, v) in vols.into_iter().enumerate() {
        // Whole volumes always lie inside their own segments.
        parts.extend(v.part(0..v.size(), i + 1 < count).unwrap_or_default());
    }
    (!path.is_empty()).then_some(Output {
        path,
        meta: FileMeta::Multipart(MultipartFile { parts, crypt: None }),
        origin,
    })
}

/// Archive levels inside the posted archives that are opened; deeper ones stay files.
const NESTED_DEPTH: usize = 3;

type Outputs<'a> = Pin<Box<dyn Future<Output = Result<Vec<Output>, Error>> + Send + 'a>>;

/// Archive sets keyed by kind, directory and set name, with numbered volumes.
type Sets = BTreeMap<(u8, Vec<String>, String), Vec<(u32, Volume)>>;

/// Replaces stored RAR and 7z archives among `outputs`, found at nesting `level`, with
/// their members, down to [`NESTED_DEPTH`].
fn expand(job: &Arc<Job>, outputs: Vec<Output>, level: usize) -> Outputs<'_> {
    Box::pin(async move {
        if level > NESTED_DEPTH {
            job.opaque(&outputs);
            return Ok(outputs);
        }
        let mut sets = Sets::new();
        let mut out = Vec::new();
        for o in outputs {
            let Some((leaf, dir)) = o.path.split_last() else {
                continue;
            };
            // ponytail: archives inside encrypted members stay files; mapping their
            // members needs reads of decrypted volumes threaded through `Volume::part`.
            if matches!(&o.meta, FileMeta::Multipart(m) if m.crypt.is_some()) {
                job.opaque(std::slice::from_ref(&o));
                out.push(o);
                continue;
            }
            let key = match names::classify(leaf) {
                Kind::Rar { set, volume } => Some((0, set, volume)),
                Kind::SevenZip { set, volume } => Some((1, set, volume)),
                _ => None,
            };
            let volume = Volume::of(o.path.join("/"), &o.meta);
            // A RAR volume must carry the signature; 7z continuation volumes have none.
            let key = match key {
                Some((0, set, n)) => {
                    let head = volume.read(job, 0, 8).await?;
                    matches!(
                        infinidysk_archive::detect(&head),
                        Some(Format::Rar4 | Format::Rar5)
                    )
                    .then_some((0, set, n))
                }
                key => key,
            };
            match key {
                Some((kind, set, n)) => sets
                    .entry((kind, dir.to_vec(), set))
                    .or_default()
                    .push((n, volume)),
                None => out.push(o),
            }
        }
        for ((kind, dir, _), mut members) in sets {
            members.sort_unstable_by_key(|(n, _)| *n);
            let numbers: Vec<u32> = members.iter().map(|(n, _)| *n).collect();
            let vols = members.into_iter().map(|(_, v)| v).collect();
            let archive = if kind == 0 {
                ArchiveType::Rar5
            } else {
                ArchiveType::SevenZip
            };
            let set = job.open_set(archive, level, &dir)?;
            let mut found = match kind {
                0 => rar_set(job, vols, set).await?,
                _ => sevenzip_set(job, vols, &numbers, set).await?,
            };
            for o in &mut found {
                o.path.splice(0..0, dir.iter().cloned());
            }
            out.extend(expand(job, found, level + 1).await?);
        }
        Ok(out)
    })
}

/// STATs a sample of the outputs' segments; after a confirmed miss outside media, samples
/// every output as well. An output with a miss is dropped, or fails the import when it is
/// media; the first miss in media ends the check.
async fn check_articles(
    job: &Arc<Job>,
    outputs: Vec<Output>,
    percent: u8,
) -> Result<Vec<Output>, Error> {
    // Each distinct segment once; archive members can share a volume's segments.
    let mut ids: Vec<String> = Vec::new();
    let mut index: HashMap<String, usize> = HashMap::new();
    let uses: Vec<Vec<usize>> = outputs
        .iter()
        .map(|o| {
            let mut used: Vec<usize> = segment_ids(&o.meta)
                .map(|id| {
                    *index.entry(id.to_owned()).or_insert_with(|| {
                        ids.push(id.to_owned());
                        ids.len() - 1
                    })
                })
                .collect();
            used.dedup();
            used
        })
        .collect();
    let fatal: HashSet<usize> = outputs
        .iter()
        .zip(&uses)
        .filter(|(o, _)| o.path.last().and_then(|n| media::kind(n)).is_some())
        .flat_map(|(_, used)| used.iter().copied())
        .collect();
    let first = media::sample(ids.len(), percent);
    job.progress
        .plan(Step::Checking, first.len() + 2 * media_outputs(&outputs));
    let mut missed = stat_all(job, &ids, &first, &fatal).await?;
    if !missed.is_empty() && missed.is_disjoint(&fatal) {
        let probed: HashSet<usize> = first.into_iter().collect();
        let more: Vec<usize> = uses
            .iter()
            .flat_map(|u| {
                let n = u.len();
                media::spread(n, 1, 1, 2 + n / 100)
                    .into_iter()
                    .map(|s| u[s])
            })
            .filter(|k| !probed.contains(k))
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        missed.extend(stat_all(job, &ids, &more, &fatal).await?);
    }
    let mut kept = Vec::with_capacity(outputs.len());
    let mut dropped = None;
    for (output, used) in outputs.into_iter().zip(&uses) {
        if !used.iter().any(|k| missed.contains(k)) {
            kept.push(output);
            continue;
        }
        let name = output.path.join("/");
        if output.path.last().and_then(|n| media::kind(n)).is_some() {
            return Err(missing(&name));
        }
        tracing::warn!(file = %name, reason = "articles missing on every provider", "dropping file");
        dropped.get_or_insert_with(|| missing(&name));
    }
    match dropped {
        Some(error) if kept.is_empty() => Err(error),
        _ => Ok(kept),
    }
}

fn segment_ids(meta: &FileMeta) -> impl Iterator<Item = &str> {
    let spans: Vec<&Segments> = match meta {
        FileMeta::Nzb(f) => vec![&f.segments],
        FileMeta::Multipart(m) => m.parts.iter().map(|p| &p.segments).collect(),
    };
    spans
        .into_iter()
        .flat_map(|s| s.items.iter().map(|s| s.id.as_str()))
}

/// Which of `probes`, indices into `ids`, are missing on every provider. Returns at the
/// first miss in `fatal`, which fails the import whatever the other probes find.
async fn stat_all(
    job: &Arc<Job>,
    ids: &[String],
    probes: &[usize],
    fatal: &HashSet<usize>,
) -> Result<HashSet<usize>, Error> {
    // Owned indices: a closure over `&usize` makes the spawned import future fail `Send`.
    let mut pending = stream::iter(
        probes
            .iter()
            .copied()
            .enumerate()
            .map(
                |(position, index)| async move { (position, index, stat(job, &ids[index]).await) },
            ),
    )
    .buffer_unordered(CONCURRENCY);
    let mut missed = HashSet::new();
    let mut inconclusive: Option<(usize, Error)> = None;
    while let Some((position, index, found)) = pending.next().await {
        match found {
            Ok(true) => {}
            Ok(false) => {
                missed.insert(index);
                if fatal.contains(&index) {
                    return Ok(missed);
                }
            }
            Err(error) => {
                if inconclusive
                    .as_ref()
                    .is_none_or(|(first, _)| position < *first)
                {
                    inconclusive = Some((position, error));
                }
            }
        }
    }
    if let Some((_, error)) = inconclusive {
        return Err(error);
    }
    Ok(missed)
}

fn media_outputs(outputs: &[Output]) -> usize {
    outputs.iter().filter(|o| is_media(o)).count()
}

fn is_media(output: &Output) -> bool {
    output.path.last().and_then(|n| media::kind(n)).is_some()
}

/// Each output's first health check, by output index. Damage fails the import only for
/// media; another damaged file just gets no check.
async fn verify(
    job: &Job,
    outputs: &[Output],
    verifier: &dyn Verifier,
    posted: Option<i64>,
) -> Result<Vec<Option<FirstCheck>>, Error> {
    futures_util::future::try_join_all(outputs.iter().map(|output| async move {
        match verifier.verify(job, output, posted).await {
            Err(Error::Missing(_) | Error::Failed(_)) if !is_media(output) => Ok(None),
            result => result,
        }
    }))
    .await
}

/// Checks each media file's last segment and the container signature at its start.
async fn ready(job: &Arc<Job>, outputs: &[Output]) -> Result<(), Error> {
    let checks = join_all(
        outputs
            .iter()
            .filter(|o| o.path.last().and_then(|n| media::kind(n)).is_some())
            .map(|o| {
                let job = Arc::clone(job);
                let file = Volume::of(o.path.join("/"), &o.meta);
                async move { check_ready(&job, &file).await }
            }),
    )
    .await;
    checks.into_iter().collect()
}

async fn check_ready(job: &Job, file: &Volume) -> Result<(), Error> {
    let name = &file.name;
    if let Some((need, valid)) = signature(name)
        && !valid(&file.read(job, 0, need).await?)
    {
        let ext = name.rsplit_once('.').map_or("", |(_, e)| e);
        if file.crypt.is_some() {
            return Err(failed(format!(
                "Not a valid {ext} file after decryption; the archive password is likely \
                 wrong: {name}"
            )));
        }
        return Err(failed(format!("Not a valid {ext} file: {name}")));
    }
    let size = file.plain_size();
    if size > 0 {
        file.read(job, size - 1, 1).await?;
    }
    Ok(())
}

/// How many leading bytes show a container's signature, and the check on them.
fn signature(name: &str) -> Option<(u64, Signature)> {
    let ext = name.rsplit_once('.')?.1.to_ascii_lowercase();
    Some(match ext.as_str() {
        "mkv" | "mk3d" | "mka" | "webm" => (4, |b| b.starts_with(&[0x1A, 0x45, 0xDF, 0xA3])),
        "mp4" | "m4v" | "mov" | "m4a" | "m4b" => (8, |b| {
            b.len() == 8 && {
                let size = u32::from_be_bytes([b[0], b[1], b[2], b[3]]);
                (size >= 8 || size <= 1) && b[4..].iter().all(|c| (0x20..=0x7E).contains(c))
            }
        }),
        "ts" => (189, |b| b.len() == 189 && b[0] == 0x47 && b[188] == 0x47),
        "m2ts" | "mts" => (197, |b| b.len() == 197 && b[4] == 0x47 && b[196] == 0x47),
        _ => return None,
    })
}

type Signature = fn(&[u8]) -> bool;

/// Drops blocked files and video samples.
fn filter(outputs: Vec<Output>, job: &Job) -> Vec<Output> {
    let video_size = |o: &Output| {
        o.path
            .last()
            .filter(|n| names::is_video(n))
            .map(|_| o.meta.size())
    };
    let largest = outputs.iter().filter_map(video_size).max().unwrap_or(0);
    outputs
        .into_iter()
        .filter(|output| {
            let (file, dirs) = output.path.split_last().expect("paths are never empty");
            if job.blocks(file) {
                return false;
            }
            let Some(size) = video_size(output) else {
                return true;
            };
            let in_sample_dir = dirs
                .iter()
                .any(|d| d.eq_ignore_ascii_case("sample") || d.eq_ignore_ascii_case("samples"));
            let stem = file.rsplit_once('.').map_or(file.as_str(), |(s, _)| s);
            let small = size.saturating_mul(SAMPLE_FRACTION) < largest;
            !(in_sample_dir || (names::is_sample(stem) && small))
        })
        .collect()
}

/// Fails unless some file is of a kind the category's Arr imports; no kinds means any.
/// Video Arrs cannot import full Blu-ray or DVD discs: they pick a stream, often an extra.
fn require(outputs: &[Output], kinds: &[media::Kind]) -> Result<(), Error> {
    let disc = |o: &Output| {
        o.path.split_last().is_some_and(|(_, dirs)| {
            dirs.iter()
                .any(|d| d.eq_ignore_ascii_case("BDMV") || d.eq_ignore_ascii_case("VIDEO_TS"))
        })
    };
    if kinds.contains(&media::Kind::Video) && outputs.iter().any(disc) {
        return Err(failed("Full disc releases are not supported."));
    }
    if kinds.is_empty()
        || outputs
            .iter()
            .filter_map(|o| o.path.last())
            .any(|n| kinds.iter().any(|&k| media::fits(n, k)))
    {
        return Ok(());
    }
    let wanted: Vec<&str> = [
        (media::Kind::Video, "video"),
        (media::Kind::Audio, "audio"),
        (media::Kind::Book, "book"),
    ]
    .into_iter()
    .filter(|(k, _)| kinds.contains(k))
    .map(|(_, word)| word)
    .collect();
    Err(failed(format!("No {} files found.", wanted.join(" or "))))
}

/// Renames obfuscated videos and gives duplicate paths a ` (n)` suffix. Deobfuscates the
/// whole list before limiting the output, yielding so inspections can time out.
async fn name(outputs: Vec<Output>, release: &str, limit: usize) -> Vec<Output> {
    let mut kept = outputs;
    deobfuscate(&mut kept, release).await;
    let mut taken = HashSet::new();
    let mut out = Vec::new();
    for mut output in kept.into_iter().take(limit) {
        tokio::task::consume_budget().await;
        let base = output.path.last().cloned().unwrap_or_default();
        let mut n = 1;
        // Case-insensitive, as Windows clients see the tree.
        while !taken.insert(output.path.join("/").to_lowercase()) {
            tokio::task::consume_budget().await;
            n += 1;
            let renamed = match base.rsplit_once('.') {
                Some((stem, ext)) if !stem.is_empty() => format!("{stem} ({n}).{ext}"),
                _ => format!("{base} ({n})"),
            };
            if let Some(file) = output.path.last_mut() {
                *file = renamed;
            }
        }
        out.push(output);
    }
    out
}

/// Names obfuscated videos, and files sharing their stem such as subtitles, after the
/// nearest folder with a readable name, or the release.
async fn deobfuscate(outputs: &mut [Output], release: &str) {
    let mut renames: Vec<(Vec<String>, String, String)> = Vec::new();
    for output in outputs.iter() {
        tokio::task::consume_budget().await;
        let (file, dirs) = output.path.split_last().expect("paths are never empty");
        let Some((stem, _)) = file.rsplit_once('.') else {
            continue;
        };
        if names::is_video(file) && names::is_obfuscated(stem) {
            let name = dirs
                .iter()
                .rev()
                .find(|d| !names::is_obfuscated(d))
                .map_or(release, String::as_str);
            renames.push((dirs.to_vec(), format!("{stem}."), name.to_owned()));
        }
    }
    for output in outputs {
        tokio::task::consume_budget().await;
        let (file, dirs) = output.path.split_last_mut().expect("paths are never empty");
        for (dir, prefix, name) in &renames {
            tokio::task::consume_budget().await;
            if dir == dirs && file.starts_with(prefix.as_str()) {
                *file = format!("{name}.{}", &file[prefix.len()..]);
                break;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const POSTED: Origin = Origin {
        source: NameSource::Subject,
        set: None,
    };

    #[tokio::test]
    async fn a_full_check_fails_at_the_first_missing_video_segment() {
        use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
        use infinidysk_testkit::LoopbackServer;

        let server = LoopbackServer::start(Vec::new()).await.unwrap();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections: 4,
            pipeline_depth: 4,
            idle_timeout: Duration::from_secs(5),
            min_connections: 0,
        };
        let client = Client::new([Provider {
            pool: Pool::new(endpoint, config, Downloads::new(32)).unwrap(),
            key: "test".to_owned(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]);
        let job = Arc::new(Job {
            client: Arc::new(client),
            requests: Semaphore::new(CONCURRENCY),
            progress: Arc::new(Progress::default()),
            blocklist: Vec::new(),
            bodies: Mutex::default(),
            password: None,
            keys: Mutex::default(),
            waits: Mutex::default(),
            inspection: None,
        });
        let output = Output {
            path: vec!["media.mkv".to_owned()],
            meta: FileMeta::Nzb(NzbFile {
                segments: Segments {
                    items: (0..200)
                        .map(|index| Segment {
                            id: format!("missing-{index}@loopback"),
                            size: 1000,
                            ..Segment::default()
                        })
                        .collect(),
                    sizes_trusted: true,
                    ..Segments::default()
                },
                proof: None,
            }),
            origin: POSTED,
        };

        let error = check_articles(&job, vec![output], 100).await.unwrap_err();
        assert!(matches!(&error, Error::Missing(_)), "{error}");
        assert_eq!(error.to_string(), missing("media.mkv").to_string());
        let issued = job.progress.issued.load(Ordering::Relaxed);
        assert!(issued > 0 && issued <= CONCURRENCY as u64, "{issued}");
    }

    #[tokio::test]
    async fn key_derivations_wait_for_a_free_slot() {
        let job = Arc::new(Job {
            client: Arc::new(Client::new([])),
            requests: Semaphore::new(CONCURRENCY),
            progress: Arc::new(Progress::default()),
            blocklist: Vec::new(),
            bodies: Mutex::default(),
            password: Some("secret".to_owned()),
            keys: Mutex::default(),
            waits: Mutex::default(),
            inspection: None,
        });
        let kdf = Kdf::SevenZip {
            salt: Vec::new(),
            cycles: 0,
        };
        // Other tests share the static semaphore, so wait for every slot, not just the free ones.
        let held = DERIVATIONS
            .acquire_many(u32::try_from(derivation_slots()).unwrap())
            .await
            .unwrap();

        let mut task = tokio::spawn({
            let job = Arc::clone(&job);
            async move { key(&job, "archive.7z", &kdf).await }
        });
        assert!(
            tokio::time::timeout(Duration::from_millis(100), &mut task)
                .await
                .is_err(),
            "derived while every slot was taken"
        );

        drop(held);
        let derived = tokio::time::timeout(Duration::from_secs(5), task)
            .await
            .unwrap()
            .unwrap();
        assert!(derived.is_ok());
    }

    #[tokio::test]
    async fn a_dropped_key_derivation_frees_its_slot() {
        let job = Arc::new(Job {
            client: Arc::new(Client::new([])),
            requests: Semaphore::new(CONCURRENCY),
            progress: Arc::new(Progress::default()),
            blocklist: Vec::new(),
            bodies: Mutex::default(),
            password: Some("p".repeat(4096)),
            keys: Mutex::default(),
            waits: Mutex::default(),
            inspection: None,
        });
        // 2^24 rounds over a long password take minutes when nothing stops them.
        let kdf = Kdf::SevenZip {
            salt: Vec::new(),
            cycles: 24,
        };
        let task = tokio::spawn({
            let job = Arc::clone(&job);
            async move { key(&job, "archive.7z", &kdf).await }
        });
        tokio::time::sleep(Duration::from_millis(200)).await;
        task.abort();

        let slots = u32::try_from(derivation_slots()).unwrap();
        let all = tokio::time::timeout(Duration::from_secs(10), DERIVATIONS.acquire_many(slots))
            .await
            .expect("the abandoned derivation kept its slot");
        drop(all);
    }

    #[test]
    fn progress_never_goes_back_and_stops_short_of_done() {
        let p = Progress::default();
        p.plan(Step::Headers, 10);
        for _ in 0..5 {
            p.request();
            p.answer();
        }
        assert_eq!(p.snapshot().percent, 50);
        assert_eq!(p.snapshot().eta, None, "too early to guess");
        p.plan(Step::Checking, 15);
        let later = p.snapshot();
        assert_eq!((later.percent, later.step), (50, Step::Checking));
        p.plan(Step::Saving, 0);
        assert_eq!(p.snapshot().percent, 99);
    }

    #[tokio::test]
    async fn names_obfuscated_videos_after_their_folder_or_release() {
        let out = |path: &str| Output {
            path: path.split('/').map(str::to_owned).collect(),
            meta: FileMeta::Multipart(MultipartFile {
                parts: Vec::new(),
                crypt: None,
            }),
            origin: POSTED,
        };
        let mut outputs = vec![
            out("a1b2c3d4e5f6.mkv"),
            out("a1b2c3d4e5f6.en.srt"),
            out("Episode Folder 01/x9y8z7w6.mkv"),
            out("Readable Name.mkv"),
        ];
        outputs.push(out("READABLE Name.mkv"));
        let outputs = name(outputs, "Release", usize::MAX).await;
        let paths: Vec<String> = outputs.iter().map(|o| o.path.join("/")).collect();
        assert_eq!(
            paths,
            [
                "Release.mkv",
                "Release.en.srt",
                "Episode Folder 01/Episode Folder 01.mkv",
                "Readable Name.mkv",
                "READABLE Name (2).mkv"
            ]
        );
        let capped = name(
            vec![out("a1b2c3d4e5f6.en.srt"), out("a1b2c3d4e5f6.mkv")],
            "Release",
            1,
        )
        .await;
        assert_eq!(capped.len(), 1);
        assert_eq!(capped[0].path, ["Release.en.srt"]);
    }

    #[test]
    fn fails_full_discs_only_for_video_arrs() {
        let disc = [Output {
            path: ["Release", "BDMV", "STREAM", "00086.m2ts"]
                .map(str::to_owned)
                .to_vec(),
            meta: FileMeta::Multipart(MultipartFile {
                parts: Vec::new(),
                crypt: None,
            }),
            origin: POSTED,
        }];
        assert!(require(&disc, &[media::Kind::Video]).is_err());
        assert!(require(&disc, &[]).is_ok(), "unowned categories keep it");
        let audiobook = [Output {
            path: ["Release", "book.mp4"].map(str::to_owned).to_vec(),
            ..disc[0].clone()
        }];
        assert!(require(&audiobook, &[media::Kind::Audio]).is_ok());
    }

    #[tokio::test]
    async fn join_all_admits_a_bounded_window_and_keeps_order() {
        use std::sync::atomic::AtomicUsize;

        struct Drops(Arc<AtomicUsize>);
        impl Drop for Drops {
            fn drop(&mut self) {
                self.0.fetch_add(1, Ordering::SeqCst);
            }
        }

        fn gated(
            n: usize,
            started: &Arc<AtomicUsize>,
            dropped: &Arc<AtomicUsize>,
            gate: &Arc<Semaphore>,
        ) -> tokio::task::JoinHandle<Vec<usize>> {
            let futures: Vec<_> = (0..n)
                .map(|i| {
                    let (started, gate) = (Arc::clone(started), Arc::clone(gate));
                    let dropped = Arc::clone(dropped);
                    async move {
                        let _guard = Drops(dropped);
                        started.fetch_add(1, Ordering::SeqCst);
                        let _ = gate.acquire().await;
                        i
                    }
                })
                .collect();
            tokio::spawn(join_all(futures))
        }

        async fn until(what: impl Fn() -> bool) {
            tokio::time::timeout(Duration::from_secs(10), async {
                while !what() {
                    tokio::time::sleep(Duration::from_millis(1)).await;
                }
            })
            .await
            .expect("condition reached in time");
        }

        let started = Arc::new(AtomicUsize::new(0));
        let dropped = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Semaphore::new(0));
        let task = gated(1000, &started, &dropped, &gate);
        until(|| started.load(Ordering::SeqCst) >= WINDOW).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(started.load(Ordering::SeqCst), WINDOW);
        gate.close();
        assert_eq!(task.await.unwrap(), (0..1000).collect::<Vec<_>>());

        let started = Arc::new(AtomicUsize::new(0));
        let dropped = Arc::new(AtomicUsize::new(0));
        let gate = Arc::new(Semaphore::new(0));
        let task = gated(1000, &started, &dropped, &gate);
        until(|| started.load(Ordering::SeqCst) >= WINDOW).await;
        task.abort();
        until(|| dropped.load(Ordering::SeqCst) == started.load(Ordering::SeqCst)).await;
    }
}
