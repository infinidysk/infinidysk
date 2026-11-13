//! Inspection: the planner run on an NZB that is never imported, and the report of what
//! it saw.

use std::collections::HashSet;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use infinidysk_archive::Format;
use infinidysk_archive::crypt::Kdf;
use infinidysk_nntp::Client;

use super::{
    Error, Job, NameSource, Output, Progress, Rules, Step, failed, filter, inner_nzb, name,
    path_of, plan,
};
use crate::names::{self, Kind};
use crate::nzb::Nzb;

const TOO_MANY_FILES: &str = "The NZB lists more files than one inspection reads.";
const TOO_MANY_SETS: &str = "The NZB holds more archive sets than one inspection opens.";
const OVER_BUDGET: &str = "The inspection used up its article budget.";
const TIMED_OUT: &str = "The inspection ran out of time.";

/// What one inspection may cost. Fixed, so one admin request cannot occupy the providers
/// for long. Work limits count across every NZB it follows; `reported` caps the final report.
#[derive(Clone, Copy)]
struct Limits {
    /// NZB files.
    files: usize,
    /// Archive sets, nested ones included.
    sets: usize,
    /// Files in the report.
    reported: usize,
    /// Article requests.
    requests: u64,
    /// Decoded article bytes; the job holds them in memory until it ends.
    // ponytail: counted on arrival, so the requests in flight can pass it. Charge the
    // sizes the NZB declares before sending if that overshoot ever matters.
    bytes: u64,
    timeout: Duration,
}

impl Limits {
    const DEFAULT: Self = Self {
        files: 1_000,
        sets: 200,
        reported: 5_000,
        requests: 3_000,
        bytes: 2_000_000_000,
        timeout: Duration::from_mins(2),
    };
}

/// The budget of one inspection, shared by every NZB it follows, and what the planner saw
/// in the NZB it reads now.
pub(super) struct Run {
    limits: Limits,
    requests: AtomicU64,
    bytes: AtomicU64,
    sets: AtomicUsize,
    notes: Mutex<Notes>,
}

impl Run {
    fn notes(&self) -> MutexGuard<'_, Notes> {
        self.notes.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

#[derive(Default)]
struct Notes {
    sets: Vec<ArchiveSet>,
    /// Members the headers list but the job cannot read.
    unread: Vec<Unread>,
    /// The first error an import would stop at that the inspection went past.
    failure: Option<Error>,
    /// Message-ids every provider reported missing.
    missed: HashSet<String>,
    /// An archive stayed a file, so its members are not listed.
    opaque: bool,
    /// A limit was reached; stays set whatever became of the error.
    exceeded: bool,
}

struct Unread {
    path: Vec<String>,
    size: Option<u64>,
    set: Option<usize>,
}

/// The format of an archive set.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArchiveType {
    Rar4,
    Rar5,
    SevenZip,
    /// A raw byte split, `video.mkv.001`, …
    Split,
}

/// What an archive set encrypts, weakest first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Encrypted {
    /// Its headers were not read to the end.
    Unknown,
    None,
    /// Member data; names and sizes are readable.
    Data,
    /// Headers too: nothing is readable without the password.
    Headers,
}

/// Whether the inspection could read an archive set's content.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Access {
    Plain,
    /// Encrypted, and the password is absent or wrong.
    PasswordRequired,
    /// Encrypted, and the archive itself confirmed the password.
    PasswordValidated,
    /// Content that cannot be streamed with any password.
    Unsupported,
    /// Not established: the headers were not read to the end, or the archive has nothing
    /// to check the password against.
    Unknown,
}

/// One archive set the planner opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArchiveSet {
    pub kind: ArchiveType,
    /// 0 when its volumes are posted files; one more for each archive opened to reach
    /// them.
    pub depth: usize,
    pub encryption: Encrypted,
    /// `Some(false)` once a member is compressed or encrypted with a scheme older than
    /// AES; `Some(true)` once every member was checked.
    stream: Option<bool>,
    /// The password is absent or wrong.
    pub(super) locked: bool,
    /// The archive confirmed the password.
    pub(super) validated: bool,
    /// Folder of its volumes inside the outer archives.
    dir: Vec<String>,
}

impl ArchiveSet {
    /// Raises [`Self::encryption`]; it never goes back.
    pub(super) fn seen(&mut self, encryption: Encrypted) {
        self.encryption = self.encryption.max(encryption);
    }

    /// Sets the RAR version once a volume's signature was read.
    // ponytail: a RAR set that fails before any signature is read stays `Rar5`. Carry
    // the format `detect` found when the set is grouped if that ever matters.
    pub(super) fn rar(&mut self, format: Option<Format>) {
        if format == Some(Format::Rar4) {
            self.kind = ArchiveType::Rar4;
        }
    }

    /// Every header was read. Encrypted headers only decode with the right password.
    pub(super) fn scanned(&mut self) {
        self.seen(Encrypted::None);
        self.validated |= self.encryption == Encrypted::Headers;
    }

    pub(super) const fn checked(&mut self) {
        if self.stream.is_none() {
            self.stream = Some(true);
        }
    }

    pub fn access(&self) -> Access {
        if self.stream == Some(false) {
            Access::Unsupported
        } else if self.locked {
            Access::PasswordRequired
        } else if self.encryption == Encrypted::None {
            Access::Plain
        } else if self.validated {
            Access::PasswordValidated
        } else {
            Access::Unknown
        }
    }

    /// Whether its content can be streamed at all, with the right password; `None` until
    /// every member was checked.
    pub const fn streamable(&self) -> Option<bool> {
        self.stream
    }
}

/// When each planning step ended, from the start of the attempt; `None` when it did not.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Timings {
    pub headers: Option<Duration>,
    pub par2: Option<Duration>,
    pub layouts: Option<Duration>,
    pub archives: Option<Duration>,
}

impl Timings {
    /// The step that had not ended.
    const fn step(&self) -> Step {
        match self {
            Self { headers: None, .. } => Step::Headers,
            Self { par2: None, .. } => Step::Par2,
            Self { layouts: None, .. } => Step::Layouts,
            _ => Step::Archives,
        }
    }
}

/// Why an import of the inspected NZB would not complete.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailureKind {
    /// Health asked an Arr to replace this release.
    Rejected,
    /// Providers could not answer; an import would retry.
    ProvidersUnavailable,
    MissingArticles,
    /// An archive password is absent or wrong.
    Password,
    /// Archive content that cannot be streamed.
    Unsupported,
    /// The inspection reached a limit; says nothing about an import.
    Limit,
    Other,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Failure {
    pub kind: FailureKind,
    /// The step that failed; [`Step::Starting`] before any provider request for its NZB.
    pub step: Step,
    pub message: String,
}

impl Failure {
    fn of(error: &Error, step: Step) -> Self {
        let kind = match error {
            Error::Retry(_) => FailureKind::ProvidersUnavailable,
            Error::Missing(_) => FailureKind::MissingArticles,
            Error::Password(_) => FailureKind::Password,
            Error::Unsupported(_) => FailureKind::Unsupported,
            Error::Limit(_) => FailureKind::Limit,
            Error::Failed(_) | Error::Unrepairable(_) => FailureKind::Other,
        };
        Self {
            kind,
            step,
            message: error.to_string(),
        }
    }
}

/// One file an import would publish, or a member it could not read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Inspected {
    /// The path as posted or as stored in the archive, made safe for a file system.
    pub raw_path: String,
    /// The path an import publishes below the job folder: obfuscated videos renamed,
    /// duplicates numbered. A member that cannot be read keeps its raw path.
    pub planned_name: String,
    /// `None` when the member cannot be read and its archive does not record a size.
    pub size: Option<u64>,
    pub source: NameSource,
    /// Index into [`Inspection::archives`]; `None` for a posted file.
    pub set: Option<usize>,
}

/// What an inspection established about an NZB.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent facts of one report, each an API field"
)]
pub struct Inspection {
    /// Whether the inner file list is complete. An archive left as a file is listed
    /// without its members. Such an archive, a reached limit or an unfinished plan
    /// makes this false. Says nothing about passwords or article availability.
    pub manifest_complete: bool,
    pub files: Vec<Inspected>,
    pub archives: Vec<ArchiveSet>,
    /// Names of the NZB files whose first article every provider reported missing.
    pub missing_first_segments: Vec<String>,
    /// A password was used on an archive that has nothing to check it against.
    pub password_not_validated: bool,
    /// A limit cut the inspection short.
    pub limit_exceeded: bool,
    /// An archive inside an archive stayed a file, so its members are not listed.
    pub nested_archive_opaque: bool,
    pub failure: Option<Failure>,
    pub timings: Timings,
    /// Article requests the planner issued for every NZB read; failover inside the client
    /// is not counted.
    pub article_requests: u64,
    /// NZB files followed to reach the NZB the report describes.
    pub inner_nzbs: usize,
}

fn is_archive(path: &[String]) -> bool {
    path.last().is_some_and(|leaf| {
        matches!(
            names::classify(leaf),
            Kind::Rar { .. } | Kind::SevenZip { .. }
        )
    })
}

/// The planner's hooks. Each does nothing during an import.
impl Job {
    pub(super) const fn inspected(&self) -> bool {
        self.inspection.is_some()
    }

    /// Whether the inspection went past an error an import stops at.
    fn went_past(&self) -> bool {
        self.inspection
            .as_ref()
            .is_some_and(|run| run.notes().failure.is_some())
    }

    fn over(&self, why: &'static str) -> Error {
        if let Some(run) = &self.inspection {
            run.notes().exceeded = true;
        }
        Error::Limit(why)
    }

    /// Counts an article request against the budget before it is sent.
    pub(super) fn charge(&self) -> Result<(), Error> {
        let Some(run) = &self.inspection else {
            return Ok(());
        };
        if run.requests.fetch_add(1, Ordering::Relaxed) >= run.limits.requests
            || run.bytes.load(Ordering::Relaxed) >= run.limits.bytes
        {
            return Err(self.over(OVER_BUDGET));
        }
        Ok(())
    }

    /// Counts a decoded body against the budget. The body that reaches it ends the
    /// inspection, so the outcome does not depend on what else was in flight.
    pub(super) fn received(&self, bytes: usize) -> Result<(), Error> {
        let Some(run) = &self.inspection else {
            return Ok(());
        };
        let bytes = bytes as u64;
        if run.bytes.fetch_add(bytes, Ordering::Relaxed) + bytes >= run.limits.bytes {
            return Err(self.over(OVER_BUDGET));
        }
        Ok(())
    }

    /// Notes an article every provider reported missing; corrupt or misplaced copies are
    /// not misses.
    pub(super) fn missed(&self, id: &str) {
        if let Some(run) = &self.inspection {
            run.notes().missed.insert(id.to_owned());
        }
    }

    /// Records an archive set about to be read, whose volumes lie in `dir` of the outer
    /// archives; its index is what members and later notes refer to.
    pub(super) fn open_set(
        &self,
        kind: ArchiveType,
        depth: usize,
        dir: &[String],
    ) -> Result<Option<usize>, Error> {
        let Some(run) = &self.inspection else {
            return Ok(None);
        };
        if run.sets.fetch_add(1, Ordering::Relaxed) >= run.limits.sets {
            return Err(self.over(TOO_MANY_SETS));
        }
        let mut notes = run.notes();
        notes.sets.push(ArchiveSet {
            kind,
            depth,
            encryption: if kind == ArchiveType::Split {
                Encrypted::None
            } else {
                Encrypted::Unknown
            },
            // A split set has no headers; its parts are the posted file.
            stream: (kind == ArchiveType::Split).then_some(true),
            locked: false,
            validated: false,
            dir: dir.to_vec(),
        });
        Ok(Some(notes.sets.len() - 1))
    }

    pub(super) fn note_set(&self, set: Option<usize>, note: impl FnOnce(&mut ArchiveSet)) {
        if let (Some(run), Some(set)) = (&self.inspection, set)
            && let Some(set) = run.notes().sets.get_mut(set)
        {
            note(set);
        }
    }

    /// Notes that `set` did not open with the job's password.
    pub(super) fn lock(&self, set: Option<usize>, error: &Error) {
        if matches!(error, Error::Password(_)) {
            self.note_set(set, |s| s.locked = true);
        }
    }

    /// Notes that a key for `kdf` was derived: RAR5 archives can store a check value that
    /// only the right password passes.
    pub(super) fn unlocked(&self, set: Option<usize>, kdf: &Kdf) {
        if matches!(kdf, Kdf::Rar5 { check: Some(_), .. }) {
            self.note_set(set, |s| s.validated = true);
        }
    }

    /// A member of `set` that cannot be imported: no usable password, or content that
    /// cannot be streamed. An import fails with `error`; an inspection lists the member
    /// from its header, keeps the first such error and goes on.
    pub(super) fn unreadable(
        &self,
        set: Option<usize>,
        error: Error,
        name: &str,
        size: Option<u64>,
    ) -> Result<(), Error> {
        let Some(run) = &self.inspection else {
            return Err(error);
        };
        let unsupported = match error {
            Error::Password(_) => false,
            Error::Unsupported(_) => true,
            error => return Err(error),
        };
        let mut notes = run.notes();
        let mut path = Vec::new();
        if let Some(set) = set.and_then(|set| notes.sets.get_mut(set)) {
            if unsupported {
                set.stream = Some(false);
            }
            set.locked |= !unsupported;
            path.clone_from(&set.dir);
        }
        path.extend(path_of(name));
        notes.opaque |= is_archive(&path);
        if !path.is_empty() {
            notes.unread.push(Unread { path, size, set });
        }
        notes.failure.get_or_insert(error);
        drop(notes);
        Ok(())
    }

    /// Notes when one of `outputs` is an archive that stays a file.
    pub(super) fn opaque(&self, outputs: &[Output]) {
        if let Some(run) = &self.inspection
            && outputs.iter().any(|o| is_archive(&o.path))
        {
            run.notes().opaque = true;
        }
    }
}

/// What one NZB of an inspection holds.
#[derive(Debug)]
pub enum Level {
    /// What an import would publish from it, or why the import would stop.
    Report(Inspection),
    /// An NZB file in place of a release, as posted (plain or gzip); an import follows it.
    Nzb(Vec<u8>),
}

/// One inspection, with one budget across every NZB it follows.
pub struct Inspector {
    run: Arc<Run>,
    /// Counts the article requests of every NZB.
    progress: Arc<Progress>,
    deadline: tokio::time::Instant,
    /// NZB files followed so far.
    followed: usize,
    files_read: usize,
}

impl Default for Inspector {
    fn default() -> Self {
        Self::with(Limits::DEFAULT)
    }
}

impl Inspector {
    fn with(limits: Limits) -> Self {
        Self {
            run: Arc::new(Run {
                limits,
                requests: AtomicU64::new(0),
                bytes: AtomicU64::new(0),
                sets: AtomicUsize::new(0),
                notes: Mutex::default(),
            }),
            progress: Arc::default(),
            deadline: tokio::time::Instant::now() + limits.timeout,
            followed: 0,
            files_read: 0,
        }
    }

    pub(crate) const fn deadline(&self) -> tokio::time::Instant {
        self.deadline
    }

    pub(crate) fn timed_out(&self) -> Inspection {
        self.failed(FailureKind::Limit, TIMED_OUT.to_owned())
    }

    /// Reads `nzb` as an import does, without the checks that read media content: the
    /// existence sample, media readiness and the first health check. Names use `release`
    /// and `rules.blocklist`; encrypted archives open with `password`. Provider misses
    /// are not remembered.
    pub async fn inspect(
        &mut self,
        client: &Arc<Client>,
        nzb: &Nzb,
        release: &str,
        rules: &Rules,
        password: Option<&str>,
    ) -> Level {
        let started = Instant::now();
        let limits = self.run.limits;
        let (progress, run) = (Arc::clone(&self.progress), Arc::clone(&self.run));
        let job = &super::job(client, rules, password, progress, Some(run), None);
        let mut timings = Timings::default();
        self.files_read = self.files_read.saturating_add(nzb.files.len());
        let (planned, step) = if self.files_read > limits.files {
            (Err(job.over(TOO_MANY_FILES)), Step::Starting)
        } else {
            let planning = async {
                let outputs = plan(job, nzb, rules.check_percent, started, &mut timings).await?;
                // An import stops at an unreadable member before it looks for an NZB file.
                let inner = if job.went_past() {
                    None
                } else {
                    inner_nzb(job, &outputs).await?
                };
                Ok::<_, Error>((outputs, inner))
            };
            let planned = tokio::time::timeout_at(self.deadline, planning)
                .await
                .unwrap_or_else(|_| Err(job.over(TIMED_OUT)));
            (planned, timings.step())
        };
        let planned = match planned {
            Ok((_, Some(raw))) => {
                *self.run.notes() = Notes::default();
                self.followed += 1;
                return Level::Nzb(raw);
            }
            Ok((outputs, None)) => self.files(job, outputs, release).await,
            Err(error) => Err(error),
        };
        let mut notes = std::mem::take(&mut *self.run.notes());
        // An import stops at the first error the inspection went past, before any later one.
        let failure = notes
            .failure
            .as_ref()
            .map(|error| Failure::of(error, Step::Archives))
            .or_else(|| planned.as_ref().err().map(|error| Failure::of(error, step)));
        let listed = planned.is_ok();
        let mut files = planned.unwrap_or_default();
        let remaining = limits.reported.saturating_sub(files.len());
        notes.exceeded |= notes.unread.len() > remaining;
        files.extend(notes.unread.drain(..).take(remaining).map(|unread| {
            let path = unread.path.join("/");
            Inspected {
                raw_path: path.clone(),
                planned_name: path,
                size: unread.size,
                source: NameSource::ArchiveHeader,
                set: unread.set,
            }
        }));
        let missing_first_segments = nzb
            .files
            .iter()
            .filter(|file| {
                file.segments
                    .first()
                    .is_some_and(|first| notes.missed.contains(&first.id))
            })
            .map(|file| file.name().to_owned())
            .collect();
        Level::Report(Inspection {
            manifest_complete: listed && !notes.opaque && !notes.exceeded,
            files,
            password_not_validated: notes
                .sets
                .iter()
                .any(|s| s.encryption >= Encrypted::Data && s.access() == Access::Unknown),
            archives: notes.sets,
            missing_first_segments,
            limit_exceeded: notes.exceeded,
            nested_archive_opaque: notes.opaque,
            failure,
            timings,
            article_requests: self.progress.issued.load(Ordering::Relaxed),
            inner_nzbs: self.followed,
        })
    }

    /// Filters and names the report within the same deadline as the planner.
    async fn files(
        &self,
        job: &Job,
        outputs: Vec<Output>,
        release: &str,
    ) -> Result<Vec<Inspected>, Error> {
        let listing = async {
            let outputs = filter(outputs, job);
            if outputs.is_empty() && self.run.notes().unread.is_empty() {
                return Err(failed("No importable files found."));
            }
            let limit = self.run.limits.reported;
            if outputs.len() > limit {
                self.run.notes().exceeded = true;
            }
            let raw: Vec<_> = outputs
                .iter()
                .take(limit)
                .map(|o| o.path.join("/"))
                .collect();
            Ok(name(outputs, release, limit)
                .await
                .into_iter()
                .zip(raw)
                .map(|(output, raw_path)| Inspected {
                    raw_path,
                    planned_name: output.path.join("/"),
                    size: Some(output.meta.size()),
                    source: output.origin.source,
                    set: output.origin.set,
                })
                .collect())
        };
        tokio::time::timeout_at(self.deadline, listing)
            .await
            .unwrap_or_else(|_| Err(job.over(TIMED_OUT)))
    }

    /// The report when the inspection stops before it reads the next NZB.
    pub fn failed(&self, kind: FailureKind, message: String) -> Inspection {
        Inspection {
            limit_exceeded: kind == FailureKind::Limit,
            failure: Some(Failure {
                kind,
                step: Step::Starting,
                message,
            }),
            article_requests: self.progress.issued.load(Ordering::Relaxed),
            inner_nzbs: self.followed,
            ..Inspection::default()
        }
    }
}

#[cfg(test)]
mod tests {
    use infinidysk_nntp::{Downloads, Endpoint, Pool, PoolConfig, Provider, TlsMode};
    use infinidysk_testkit::{Article, LoopbackServer, post};

    use super::*;
    use crate::nzb::{File, Segment};

    fn client(server: &LoopbackServer) -> Arc<Client> {
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
        Arc::new(Client::new([Provider {
            pool: Pool::new(endpoint, config, Downloads::new(32)).unwrap(),
            key: "test".to_owned(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]))
    }

    /// `names` posted as 4000-byte videos in four articles each, and their NZB.
    fn release(names: &[&str]) -> (Vec<Article>, Nzb) {
        let mut data = vec![0u8; 4000];
        data[..4].copy_from_slice(&[0x1A, 0x45, 0xDF, 0xA3]);
        let mut articles = Vec::new();
        let mut nzb = Nzb::default();
        for (i, name) in names.iter().enumerate() {
            let posted = post(&format!("f{i}"), name, &data, 1000);
            nzb.files.push(File {
                subject: (*name).to_owned(),
                date: None,
                segments: posted
                    .iter()
                    .enumerate()
                    .map(|(index, a)| Segment {
                        id: a.message_id.clone(),
                        bytes: 1000,
                        number: i64::try_from(index + 1).unwrap(),
                    })
                    .collect(),
                gap: false,
            });
            articles.extend(posted);
        }
        (articles, nzb)
    }

    /// The report of `nzb`, which names a release, inspected within `limits`.
    async fn inspect_within(client: &Arc<Client>, nzb: &Nzb, limits: Limits) -> Inspection {
        let mut inspector = Inspector::with(limits);
        match inspector
            .inspect(client, nzb, "job", &Rules::default(), None)
            .await
        {
            Level::Report(report) => report,
            Level::Nzb(_) => panic!("the release holds no NZB file"),
        }
    }

    #[tokio::test]
    async fn every_limit_cuts_the_inspection_short_and_says_so() {
        let one = ["One.mkv"];
        let plain = ["One.mkv", "Two.mkv"];
        let split = ["a.mkv.001", "a.mkv.002", "b.mkv.001", "b.mkv.002"];
        // More files than one window of requests, so only the slots can bound the overshoot.
        let many: Vec<String> = (0..100).map(|i| format!("File{i}.mkv")).collect();
        let many: Vec<&str> = many.iter().map(String::as_str).collect();
        let roomy = Limits::DEFAULT;
        let cases = [
            (&plain[..], Limits { files: 1, ..roomy }, TOO_MANY_FILES),
            (&split[..], Limits { sets: 1, ..roomy }, TOO_MANY_SETS),
            (
                &plain[..],
                Limits {
                    requests: 3,
                    ..roomy
                },
                OVER_BUDGET,
            ),
            (&many[..], Limits { bytes: 1, ..roomy }, OVER_BUDGET),
            // One.mkv needs its first and last article, 2000 bytes, and no request after.
            (
                &one[..],
                Limits {
                    bytes: 2000,
                    ..roomy
                },
                OVER_BUDGET,
            ),
        ];
        for (names, limits, message) in cases {
            let (articles, nzb) = release(names);
            let server = LoopbackServer::start(articles).await.unwrap();
            let client = client(&server);
            let report = inspect_within(&client, &nzb, limits).await;
            let failure = report.failure.expect(message);
            assert_eq!(
                (failure.kind, failure.message.as_str()),
                (FailureKind::Limit, message)
            );
            assert!(
                report.limit_exceeded && !report.manifest_complete,
                "{message}"
            );
            assert!(report.files.is_empty(), "{message}");
            let in_flight = limits.requests.min(crate::import::CONCURRENCY as u64);
            assert!(report.article_requests <= in_flight, "{message}");

            // The same release fits the real limits.
            let report = inspect_within(&client, &nzb, roomy).await;
            assert!(
                report.manifest_complete && !report.limit_exceeded,
                "{message}"
            );
        }

        // A report cut to its file limit is not complete, and no import would fail.
        let (articles, nzb) = release(&plain);
        let server = LoopbackServer::start(articles).await.unwrap();
        let limits = Limits {
            reported: 1,
            ..roomy
        };
        let report = inspect_within(&client(&server), &nzb, limits).await;
        assert_eq!((report.files.len(), report.failure), (1, None));
        assert!(report.limit_exceeded && !report.manifest_complete);
    }

    #[tokio::test]
    async fn an_inspection_keeps_its_budget_across_inner_nzbs() {
        use std::fmt::Write as _;

        let (mut articles, inner) = release(&["One.mkv"]);
        let mut segments = String::new();
        for (index, segment) in inner.files[0].segments.iter().enumerate() {
            write!(
                segments,
                "<segment bytes=\"1000\" number=\"{}\">{}</segment>",
                index + 1,
                segment.id
            )
            .unwrap();
        }
        let xml = format!(
            "<nzb><file poster=\"p\" date=\"0\" subject=\"One.mkv\">\
             <groups><group>a.b</group></groups><segments>{segments}</segments></file></nzb>"
        );
        let posted = post("outer", "release.nzb", xml.as_bytes(), 16 << 10);
        let outer = Nzb {
            files: vec![File {
                subject: "release.nzb".to_owned(),
                date: None,
                segments: posted
                    .iter()
                    .enumerate()
                    .map(|(index, article)| Segment {
                        id: article.message_id.clone(),
                        bytes: u32::try_from(xml.len()).unwrap(),
                        number: i64::try_from(index + 1).unwrap(),
                    })
                    .collect(),
                gap: false,
            }],
            ..Nzb::default()
        };
        articles.extend(posted);
        let server = LoopbackServer::start(articles).await.unwrap();
        let client = client(&server);
        let roomy = Limits::DEFAULT;
        for limits in [
            Limits { files: 1, ..roomy },
            Limits {
                requests: 2,
                ..roomy
            },
            Limits {
                bytes: xml.len() as u64 + 1000,
                ..roomy
            },
        ] {
            let mut inspector = Inspector::with(limits);
            let deadline = inspector.deadline();
            let Level::Nzb(raw) = inspector
                .inspect(&client, &outer, "job", &Rules::default(), None)
                .await
            else {
                panic!("the outer NZB fits every budget")
            };
            assert_eq!(raw, xml.as_bytes());
            assert_eq!(inspector.deadline(), deadline);
            let parsed = crate::nzb::parse(&raw).unwrap();
            let Level::Report(report) = inspector
                .inspect(&client, &parsed, "job", &Rules::default(), None)
                .await
            else {
                panic!("the inner NZB names media")
            };
            assert_eq!(report.inner_nzbs, 1);
            assert_eq!(report.failure.unwrap().kind, FailureKind::Limit);
            assert!(report.limit_exceeded && !report.manifest_complete);
            assert!(report.article_requests <= 2);
        }
    }

    #[tokio::test]
    async fn an_inspection_can_time_out_while_naming_its_report() {
        use infinidysk_core::{FileMeta, MultipartFile};

        let inspector = Inspector::with(Limits {
            reported: 1,
            timeout: Duration::ZERO,
            ..Limits::DEFAULT
        });
        let client = Arc::new(Client::new([]));
        let job = super::super::job(
            &client,
            &Rules::default(),
            None,
            Arc::clone(&inspector.progress),
            Some(Arc::clone(&inspector.run)),
            None,
        );
        let output = Output {
            path: vec!["Readable Name.txt".to_owned()],
            meta: FileMeta::Multipart(MultipartFile {
                parts: Vec::new(),
                crypt: None,
            }),
            origin: crate::import::Origin {
                source: NameSource::Subject,
                set: None,
            },
        };
        let result = inspector.files(&job, vec![output; 512], "job").await;
        assert!(matches!(result, Err(Error::Limit(TIMED_OUT))));
        assert!(inspector.run.notes().exceeded);
    }

    #[tokio::test]
    async fn an_inspection_stops_at_its_timeout() {
        let (articles, nzb) = release(&["One.mkv"]);
        let server = LoopbackServer::start_stalling(articles, 0, Duration::from_secs(30))
            .await
            .unwrap();
        let limits = Limits {
            timeout: Duration::from_millis(200),
            ..Limits::DEFAULT
        };
        let started = Instant::now();
        let report = inspect_within(&client(&server), &nzb, limits).await;
        assert!(started.elapsed() < Duration::from_secs(10));
        let failure = report.failure.expect("timed out");
        assert_eq!(
            (failure.kind, failure.step, failure.message.as_str()),
            (FailureKind::Limit, Step::Headers, TIMED_OUT)
        );
        assert!(report.limit_exceeded && !report.manifest_complete);
        assert_eq!(report.article_requests, 1);
    }

    #[tokio::test]
    async fn a_provider_outage_is_not_a_missing_article() {
        let (_, nzb) = release(&["One.mkv"]);
        let client = Arc::new(Client::new(Vec::<Provider>::new()));
        let report = inspect_within(&client, &nzb, Limits::DEFAULT).await;
        let failure = report.failure.expect("no provider answered");
        assert_eq!(
            (failure.kind, failure.step),
            (FailureKind::ProvidersUnavailable, Step::Headers)
        );
        assert!(!report.manifest_complete && report.files.is_empty());
        assert!(
            report.missing_first_segments.is_empty(),
            "an outage is not a miss"
        );
    }
}
