//! One-time importer of a C# `/config`, deleted together with C# support: a survey of
//! what an import would carry over, the C# settings, and [`apply`], which writes the data.

mod apply;
mod csharp;
pub mod legacy_repairs;
mod memorypack;
mod password;
pub mod postgres;
mod settings;

use std::fmt;
use std::fs::File;
use std::io::{self, Read as _};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use nix::fcntl::{OFlag, openat};
use nix::sys::stat::Mode;
use sqlx::ConnectOptions as _;
use sqlx::SqliteConnection;
use sqlx::sqlite::SqliteConnectOptions;
use uuid::Uuid;

pub use crate::apply::{
    Imported, Progress, Stage, apply, apply_with_progress, restore_nzb_downloads, retry_files,
    stamp_download_ids,
};
use crate::csharp::{LongRange, decode};
pub use crate::password::{is_legacy, legacy_hash, verify_legacy};
pub use crate::settings::{ArrInstance, ArrKind, Config, Provider, ProviderKind};

/// The oldest C# schema imported (v1.3.0): the import reads every `DavItems` column up
/// to `HealthRepairPending`.
const OLDEST_MIGRATION: &str = "20260824160000";
/// Written by 1.x (v1.3.0 and later) into its `/config` at every start.
const CONTRACT: &str = "db-contract.json";
const PROVIDER_VAR: &str = "DATABASE_PROVIDER";
const CONNECTION_VAR: &str = "DATABASE_CONNECTION_STRING";
const SHOWN_FAILURES: usize = 20;
const PROGRESS_EVERY: Duration = Duration::from_secs(1);
/// Progress reports between log lines while checking file metadata.
const LOG_EVERY: u32 = 30;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("saved C# repair articles could not be preserved: {0}")]
    LegacyRepairs(#[source] io::Error),
    #[error("no C# database at {0}")]
    NoDatabase(PathBuf),
    #[error("reading the C# database: {0}")]
    Db(#[from] sqlx::Error),
    #[error("reading C# blobs: {0}")]
    Task(#[from] tokio::task::JoinError),
    #[error("writing the import: {0}")]
    Write(#[from] infinidysk_db::Error),
    #[error("encoding an NZB: {0}")]
    Blob(#[from] infinidysk_core::blob::Error),
    #[error(
        "DATABASE_PROVIDER is postgres, but DATABASE_CONNECTION_STRING is not set; set it as it was for 1.x"
    )]
    NoConnectionString,
    #[error(
        "{} says the 1.x install kept its data in PostgreSQL, but DATABASE_PROVIDER and \
         DATABASE_CONNECTION_STRING are not set; set them as they were for 1.x, or delete that \
         file to start empty", .0.display()
    )]
    PostgresNotConfigured(PathBuf),
    #[error("DATABASE_PROVIDER must be either 'sqlite' or 'postgres', as 1.x required")]
    InvalidProvider,
    #[error("{path} cannot be read ({1}); fix or delete it", path = .0.display())]
    Contract(PathBuf, String),
    #[error(transparent)]
    ConnectionString(#[from] postgres::ConnectionError),
    #[error("the 1.x PostgreSQL database did not answer within 15 s")]
    ConnectTimeout,
    #[error("could not connect to the 1.x PostgreSQL database: {0}")]
    Connect(#[source] sqlx::Error),
    #[error("the 1.x PostgreSQL database has no {0} table; check DATABASE_CONNECTION_STRING")]
    MissingTable(&'static str),
    #[error("the copy of the 1.x PostgreSQL database: {0}")]
    Stage(#[source] io::Error),
}

/// Where a 1.x install keeps its database. Holds a connection string: no `Debug`.
pub enum Source {
    /// No 1.x install here.
    None,
    /// `db.sqlite` in its `/config`.
    Sqlite,
    /// PostgreSQL, with 1.x's `DATABASE_CONNECTION_STRING`.
    Postgres(String),
}

/// How 1.x chose its database: `DATABASE_PROVIDER` when set, else the provider in
/// `db-contract.json`, else `db.sqlite` when present. `vars` is the process environment.
pub fn source(config: &Path, vars: &[(String, String)]) -> Result<Source, Error> {
    let var = |name: &str| {
        vars.iter()
            .find(|(k, _)| k == name)
            .map(|(_, v)| v.trim())
            .filter(|v| !v.is_empty())
    };
    let provider = match var(PROVIDER_VAR).map(str::to_ascii_lowercase) {
        Some(provider) if known_provider(&provider) => Some(provider),
        Some(_) => return Err(Error::InvalidProvider),
        None => contract_provider(config)?,
    };
    match provider.as_deref() {
        Some("postgres" | "postgresql") => match var(CONNECTION_VAR) {
            Some(connection) => Ok(Source::Postgres(connection.to_owned())),
            None if var(PROVIDER_VAR).is_some() => Err(Error::NoConnectionString),
            None => Err(Error::PostgresNotConfigured(config.join(CONTRACT))),
        },
        _ if config.join("db.sqlite").is_file() => Ok(Source::Sqlite),
        _ => Ok(Source::None),
    }
}

fn known_provider(provider: &str) -> bool {
    matches!(provider, "sqlite" | "postgres" | "postgresql")
}

/// The lowercased `provider` in `db-contract.json`; `None` when the file is missing.
fn contract_provider(config: &Path) -> Result<Option<String>, Error> {
    #[derive(serde::Deserialize)]
    struct Contract {
        provider: String,
    }
    let path = config.join(CONTRACT);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(Error::Contract(path, error.to_string())),
    };
    let contract: Contract = serde_json::from_str(&text)
        .map_err(|error| Error::Contract(path.clone(), error.to_string()))?;
    let provider = contract.provider.trim().to_ascii_lowercase();
    if !known_provider(&provider) {
        return Err(Error::Contract(
            path,
            format!("unknown provider {provider:?}"),
        ));
    }
    Ok(Some(provider))
}

/// What a C# `/config` holds, and what importing it involves.
#[derive(Debug, Default)]
pub struct Survey {
    /// Latest applied C# database migration.
    pub migration: Option<String>,
    pub directories: u64,
    pub nzb_files: u64,
    pub rar_files: u64,
    pub multipart_files: u64,
    pub decoded: u64,
    /// `(item id, reason)`, the first [`SHOWN_FAILURES`].
    pub failures: Vec<(String, String)>,
    pub failed: u64,
    pub missing_file_blobs: u64,
    pub missing_nzb_blobs: u64,
    /// Segment runs (a file or one of its parts) without recorded segment sizes; C#
    /// inferred them on read.
    pub sizes_unknown: u64,
    /// RAR members whose trailing volumes C# had not resolved yet.
    pub lazy: u64,
    /// Encrypted members; they keep the key C# derived.
    pub encrypted: u64,
    /// Files whose segment ranges disagree with their segments or their size.
    pub inconsistent: u64,
    /// Sizes not validated from yEnc headers; seeks probe instead of computing offsets.
    pub untrusted: u64,
    /// Files with segments recorded missing or corrupt on every provider.
    pub degraded: u64,
    pub with_fallbacks: u64,
    pub with_proof: u64,
    pub strm_outputs: u64,
    pub history_completed: u64,
    pub history_failed: u64,
    pub queued: u64,
}

impl Survey {
    pub fn supported(&self) -> bool {
        self.migration
            .as_deref()
            .is_some_and(|m| m >= OLDEST_MIGRATION)
    }
}

/// Opens the C# database in `config` read-only: the copy of a PostgreSQL install while
/// one exists, else `db.sqlite`.
async fn open(config: &Path) -> Result<SqliteConnection, Error> {
    let staged = config.join(postgres::STAGED);
    let path = if staged.is_file() {
        staged
    } else {
        config.join("db.sqlite")
    };
    if !path.is_file() {
        return Err(Error::NoDatabase(path));
    }
    Ok(SqliteConnectOptions::new()
        .filename(&path)
        .read_only(true)
        .connect()
        .await?)
}

/// Surveys the C# `/config` at `config` without changing it.
pub async fn survey(config: &Path) -> Result<Survey, Error> {
    survey_with_progress(config, |_| {}).await
}

/// Like [`survey`], reporting [`Stage::Checking`] as it reads the file metadata blobs.
pub async fn survey_with_progress(
    config: &Path,
    progress: impl Fn(Progress),
) -> Result<Survey, Error> {
    progress(Progress::counting(Stage::Checking));
    let mut db = open(config).await?;
    let count = |n: i64| u64::try_from(n).unwrap_or_default();
    let migration = sqlx::query_scalar(
        "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1",
    )
    .fetch_optional(&mut db)
    .await?;
    let mut s = Survey {
        migration,
        ..Survey::default()
    };
    // Older schemas lack columns the queries below read.
    if !s.supported() {
        return Ok(s);
    }
    s.directories = count(
        sqlx::query_scalar("SELECT COUNT(*) FROM DavItems WHERE SubType = 101")
            .fetch_one(&mut db)
            .await?,
    );
    s.queued = count(
        sqlx::query_scalar("SELECT COUNT(*) FROM QueueItems")
            .fetch_one(&mut db)
            .await?,
    );
    let history: Vec<(i64, i64)> =
        sqlx::query_as("SELECT DownloadStatus, COUNT(*) FROM HistoryItems GROUP BY 1")
            .fetch_all(&mut db)
            .await?;
    for (status, n) in history {
        match status {
            1 => s.history_completed = count(n),
            2 => s.history_failed = count(n),
            _ => {}
        }
    }
    let files: Vec<FileRow> = sqlx::query_as(
        "SELECT Id, SubType, FileSize, FileBlobId, NzbBlobId, GeneratedStrmPath IS NOT NULL \
         FROM DavItems WHERE Type = 2",
    )
    .fetch_all(&mut db)
    .await?;
    drop(db);
    let total = files.len() as u64;
    progress(Progress::at(Stage::Checking, 0, total));
    tracing::info!(files = total, "C# import: checking file metadata");
    let started = Instant::now();
    let checked = Arc::new(AtomicU64::new(0));
    let counter = Arc::clone(&checked);
    let blobs = config.join("blobs");
    let scan = tokio::task::spawn_blocking(move || {
        for file in files {
            s.file(&blobs, file);
            counter.fetch_add(1, Ordering::Relaxed);
        }
        s
    });
    let survey = track_scan(scan, &checked, total, &progress).await?;
    tracing::info!(
        files = total,
        seconds = started.elapsed().as_secs(),
        "C# import: file metadata checked"
    );
    Ok(survey)
}

async fn track_scan(
    mut scan: tokio::task::JoinHandle<Survey>,
    checked: &AtomicU64,
    total: u64,
    progress: &impl Fn(Progress),
) -> Result<Survey, Error> {
    let mut tick = tokio::time::interval(PROGRESS_EVERY);
    let mut ticks = 0_u32;
    let s = loop {
        tokio::select! {
            result = &mut scan => break result?,
            _ = tick.tick() => {
                let done = checked.load(Ordering::Relaxed);
                progress(Progress::at(Stage::Checking, done, total));
                ticks = ticks.wrapping_add(1);
                if ticks.is_multiple_of(LOG_EVERY) {
                    tracing::info!(checked = done, total, "C# import: checking file metadata");
                }
            }
        }
    };
    progress(Progress::at(Stage::Checking, total, total));
    Ok(s)
}

/// `(id, sub type, size, metadata blob, NZB blob, has a STRM output)`.
type FileRow = (
    String,
    i64,
    Option<i64>,
    Option<String>,
    Option<String>,
    bool,
);

impl Survey {
    fn file(&mut self, blobs: &Path, (id, sub_type, size, meta, nzb, strm): FileRow) {
        self.strm_outputs += u64::from(strm);
        // Counts what `import_nzbs` will carry over: no symlinks, nothing above the cap.
        let carried = nzb.and_then(|n| open_blob(blobs, &n).ok().flatten());
        if !carried.is_some_and(|f| f.metadata().is_ok_and(|m| m.len() <= MAX_NZB_BLOB_BYTES)) {
            self.missing_nzb_blobs += 1;
        }
        let Some(meta) = meta else {
            self.missing_file_blobs += 1;
            return;
        };
        let file = match open_blob(blobs, &meta) {
            Ok(Some(file)) => file,
            Ok(None) => {
                self.missing_file_blobs += 1;
                return;
            }
            Err(error) => return self.fail(id, error.to_string()),
        };
        let size = size.unwrap_or_default();
        let result = match sub_type {
            201 => {
                self.nzb_files += 1;
                decode(file, csharp::nzb_file).map(|f| self.nzb(&f, size))
            }
            202 => {
                self.rar_files += 1;
                decode(file, csharp::rar_file).map(|f| self.rar(&f, size))
            }
            203 => {
                self.multipart_files += 1;
                decode(file, csharp::multipart_file).map(|f| self.multipart(&f, size))
            }
            other => return self.fail(id, format!("unknown file type {other}")),
        };
        match result {
            Ok(()) => self.decoded += 1,
            Err(error) => self.fail(id, error.to_string()),
        }
    }

    fn fail(&mut self, id: String, reason: String) {
        self.failed += 1;
        if self.failures.len() < SHOWN_FAILURES {
            self.failures.push((id, reason));
        }
    }

    fn nzb(&mut self, f: &csharp::NzbFile, size: i64) {
        self.segments(
            f.segment_ids.len(),
            f.ranges.as_deref(),
            f.fallbacks.as_deref(),
            f.ranges_trusted,
        );
        if f.ranges
            .as_deref()
            .and_then(<[_]>::last)
            .map(|r| r.end)
            .is_some_and(|end| end != size)
        {
            self.inconsistent += 1;
        }
        self.degraded += u64::from(
            f.missing.as_ref().is_some_and(|m| !m.is_empty())
                || f.corrupt.as_ref().is_some_and(|c| !c.is_empty()),
        );
        self.with_proof += u64::from(f.proof.is_some());
    }

    fn rar(&mut self, f: &csharp::RarFile, size: i64) {
        for p in &f.parts {
            self.segments(
                p.segment_ids.len(),
                p.ranges.as_deref(),
                p.fallbacks.as_deref(),
                p.ranges_trusted,
            );
        }
        if f.parts.iter().map(|p| p.byte_count).sum::<i64>() != size {
            self.inconsistent += 1;
        }
    }

    fn multipart(&mut self, f: &csharp::MultipartFile, size: i64) {
        for p in &f.parts {
            self.segments(
                p.segment_ids.len(),
                p.ranges.as_deref(),
                p.fallbacks.as_deref(),
                p.ranges_trusted,
            );
        }
        let lazy = f.is_lazy || !f.pending.is_empty();
        self.lazy += u64::from(lazy);
        self.encrypted += u64::from(f.aes.is_some());
        let data: i64 = f.parts.iter().map(|p| p.data_range.len()).sum();
        if !lazy && f.aes.is_none() && data != size {
            self.inconsistent += 1;
        }
        self.with_proof += u64::from(f.parts.iter().any(|p| p.proof.is_some()));
    }

    /// Tallies one run of segments (a file or one of its parts).
    fn segments(
        &mut self,
        count: usize,
        ranges: Option<&[LongRange]>,
        fallbacks: Option<&[Vec<String>]>,
        trusted: Option<bool>,
    ) {
        match ranges {
            None => self.sizes_unknown += 1,
            Some(r) if r.len() != count => self.inconsistent += 1,
            Some(_) => {}
        }
        self.untrusted += u64::from(trusted != Some(true));
        self.with_fallbacks +=
            u64::from(fallbacks.is_some_and(|f| f.iter().any(|s| !s.is_empty())));
    }
}

/// `blobs/<2 hex>/<2 hex>/<id>`, as C# lays them out.
#[cfg(test)]
fn blob_path(blobs: &Path, id: &str) -> Option<PathBuf> {
    let id = Uuid::parse_str(id).ok()?;
    let hex = id.simple().to_string();
    Some(blobs.join(&hex[..2]).join(&hex[2..4]).join(id.to_string()))
}

/// Matches C#'s current NZB ingestion limit; new uploads use the queue's separate cap.
pub(crate) const MAX_NZB_BLOB_BYTES: u64 = 268_435_456;

/// Reads blob `id` below the folder `blobs`, following no symlink below it and reading at
/// most `limit` bytes; `None` when it is not there.
fn read_blob(blobs: &Path, id: &str, limit: u64) -> io::Result<Option<Vec<u8>>> {
    let Some(file) = open_blob(blobs, id)? else {
        return Ok(None);
    };
    let mut bytes = Vec::new();
    file.take(limit + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > limit {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("larger than {limit} bytes"),
        ));
    }
    Ok(Some(bytes))
}

/// Opens blob `id` below the folder `blobs`, following no symlink below it; the blob must
/// be a regular file. `None` when it is not there.
fn open_blob(blobs: &Path, id: &str) -> io::Result<Option<File>> {
    let Ok(id) = Uuid::parse_str(id) else {
        return Ok(None);
    };
    let hex = id.simple().to_string();
    let dir = OFlag::O_RDONLY | OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW | OFlag::O_CLOEXEC;
    // O_NONBLOCK: opening a planted FIFO must not wait for a writer.
    let leaf = OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK | OFlag::O_CLOEXEC;
    let opened = (|| -> io::Result<File> {
        // Follows a symlinked root; O_DIRECTORY refuses a FIFO there instead of waiting.
        let root = nix::fcntl::open(blobs, dir.difference(OFlag::O_NOFOLLOW), Mode::empty())?;
        let a = openat(&root, &hex[..2], dir, Mode::empty())?;
        let b = openat(&a, &hex[2..4], dir, Mode::empty())?;
        Ok(File::from(openat(
            &b,
            id.to_string().as_str(),
            leaf,
            Mode::empty(),
        )?))
    })();
    let file = match opened {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error),
    };
    if !file.metadata()?.is_file() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "not a regular file",
        ));
    }
    Ok(Some(file))
}

impl fmt::Display for Survey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let migration = self.migration.as_deref().unwrap_or("none");
        let verdict = if self.supported() {
            "supported"
        } else {
            "too old; upgrade C# first"
        };
        writeln!(f, "C# schema {migration}: {verdict}")?;
        writeln!(
            f,
            "Items: {} directories; files: {} NZB, {} multipart, {} RAR (legacy)",
            self.directories, self.nzb_files, self.multipart_files, self.rar_files
        )?;
        writeln!(
            f,
            "  decoded {}, failed {}, metadata blob missing {}, NZB blob missing {}",
            self.decoded, self.failed, self.missing_file_blobs, self.missing_nzb_blobs
        )?;
        writeln!(f, "Import must:")?;
        writeln!(
            f,
            "  derive segment sizes for {} segment runs that never recorded them",
            self.sizes_unknown
        )?;
        writeln!(f, "  resolve trailing RAR volumes of {} files", self.lazy)?;
        writeln!(
            f,
            "  check {} files whose ranges disagree with their size",
            self.inconsistent
        )?;
        writeln!(f, "Carried over:")?;
        writeln!(f, "  {} encrypted files, decrypted on read", self.encrypted)?;
        writeln!(
            f,
            "  {} segment runs with unvalidated sizes",
            self.untrusted
        )?;
        writeln!(
            f,
            "  {} files with segments missing or corrupt everywhere",
            self.degraded
        )?;
        writeln!(
            f,
            "  {} segment runs with fallback message-ids",
            self.with_fallbacks
        )?;
        writeln!(f, "  {} files with PAR2 proofs", self.with_proof)?;
        writeln!(f, "  {} STRM outputs", self.strm_outputs)?;
        writeln!(
            f,
            "History: {} completed, {} failed; queue: {}",
            self.history_completed, self.history_failed, self.queued
        )?;
        if self.failed > 0 {
            writeln!(
                f,
                "Failures (first {} of {}):",
                self.failures.len(),
                self.failed
            )?;
            for (id, reason) in &self.failures {
                writeln!(f, "  {id}: {reason}")?;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn checking_reports_progress_before_the_scan_finishes() {
        let checked = Arc::new(AtomicU64::new(0));
        let counter = Arc::clone(&checked);
        let (reported, received) = std::sync::mpsc::channel();
        let scan = tokio::task::spawn_blocking(move || {
            for done in 1..3 {
                counter.store(done, Ordering::Relaxed);
                assert_eq!(received.recv_timeout(Duration::from_secs(5)).unwrap(), done);
            }
            counter.store(3, Ordering::Relaxed);
            Survey::default()
        });
        let updates = std::sync::Mutex::new(Vec::<Progress>::new());
        track_scan(scan, &checked, 3, &|progress| {
            let mut updates = updates.lock().unwrap();
            let previous = updates.last().map_or(0, |previous| previous.done);
            updates.push(progress);
            drop(updates);
            if progress.done > previous && progress.done < 3 {
                reported.send(progress.done).unwrap();
            }
        })
        .await
        .unwrap();
        let updates = updates.into_inner().unwrap();
        assert!(updates.contains(&Progress::at(Stage::Checking, 1, 3)));
        assert!(updates.contains(&Progress::at(Stage::Checking, 2, 3)));
        assert_eq!(updates.last(), Some(&Progress::at(Stage::Checking, 3, 3)));
        assert!(updates.windows(2).all(|pair| pair[0].done <= pair[1].done));
    }

    #[test]
    fn the_install_source_follows_how_1x_chose_its_database() {
        let case = |contract: Option<&str>, sqlite: bool, vars: &[(&str, &str)]| {
            let dir = std::env::temp_dir().join(format!("infinidysk-source-{}", Uuid::now_v7()));
            std::fs::create_dir_all(&dir).unwrap();
            if let Some(contract) = contract {
                std::fs::write(dir.join(CONTRACT), contract).unwrap();
            }
            if sqlite {
                std::fs::write(dir.join("db.sqlite"), "").unwrap();
            }
            let vars: Vec<(String, String)> = vars
                .iter()
                .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
                .collect();
            let source = source(&dir, &vars);
            std::fs::remove_dir_all(&dir).unwrap();
            source
        };
        let pg = r#"{"provider":"postgres"}"#;
        let conn = (CONNECTION_VAR, "Host=h");

        let explicit = [(PROVIDER_VAR, "postgres"), conn];
        assert!(matches!(case(None, true, &explicit), Ok(Source::Postgres(c)) if c == "Host=h"));
        let explicit = [(PROVIDER_VAR, "PostgreSQL"), conn];
        assert!(matches!(
            case(None, false, &explicit),
            Ok(Source::Postgres(_))
        ));
        assert!(matches!(
            case(None, true, &[(PROVIDER_VAR, "postgres")]),
            Err(Error::NoConnectionString)
        ));
        assert!(matches!(
            case(Some(pg), true, &[conn]),
            Ok(Source::Postgres(_))
        ));
        assert!(matches!(
            case(Some(pg), true, &[(PROVIDER_VAR, " "), (CONNECTION_VAR, "")]),
            Err(Error::PostgresNotConfigured(_))
        ));
        assert!(matches!(case(None, true, &[]), Ok(Source::Sqlite)));
        assert!(matches!(case(None, false, &[conn]), Ok(Source::None)));
        assert!(matches!(
            case(Some("not json"), true, &[]),
            Err(Error::Contract(..))
        ));
        assert!(matches!(
            case(Some("not json"), true, &[(PROVIDER_VAR, "sqlite")]),
            Ok(Source::Sqlite)
        ));
        assert!(matches!(
            case(Some(r#"{"provider":"postgre"}"#), true, &[conn]),
            Err(Error::Contract(..))
        ));
        assert!(matches!(
            case(Some(pg), true, &[(PROVIDER_VAR, "postgre"), conn]),
            Err(Error::InvalidProvider)
        ));
        assert!(matches!(
            case(Some(r#"{"provider":" PostgreSQL "}"#), false, &[conn]),
            Ok(Source::Postgres(_))
        ));
        assert!(matches!(
            case(Some(pg), true, &[(PROVIDER_VAR, "sqlite"), conn]),
            Ok(Source::Sqlite)
        ));
    }

    #[test]
    fn a_schema_before_the_oldest_is_refused_not_failed() {
        let dir = std::env::temp_dir().join(format!("infinidysk-survey-{}", Uuid::now_v7()));
        std::fs::create_dir_all(&dir).unwrap();
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let survey = rt.block_on(async {
            let mut c = SqliteConnectOptions::new()
                .filename(dir.join("db.sqlite"))
                .create_if_missing(true)
                .connect()
                .await
                .unwrap();
            sqlx::raw_sql(
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT, ProductVersion TEXT);
                 INSERT INTO __EFMigrationsHistory
                   VALUES ('20260824143000_Add-Generated-Symlink-Metadata', '10.0.0');
                 CREATE TABLE DavItems (Id TEXT, Type INTEGER, SubType INTEGER);",
            )
            .execute(&mut c)
            .await
            .unwrap();
            drop(c);
            survey(&dir).await
        });
        std::fs::remove_dir_all(&dir).unwrap();
        assert!(!survey.unwrap().supported());
    }

    #[test]
    fn the_survey_counts_nzb_blobs_the_import_would_skip_as_missing() {
        let dir = std::env::temp_dir().join(format!("infinidysk-nzbs-{}", Uuid::now_v7()));
        let blobs = dir.join("blobs");
        let place = |id: &str| {
            let path = blob_path(&blobs, id).unwrap();
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            path
        };
        let regular = Uuid::now_v7().to_string();
        std::fs::write(place(&regular), b"nzb").unwrap();
        std::fs::OpenOptions::new()
            .write(true)
            .open(place(&regular))
            .unwrap()
            .set_len(MAX_NZB_BLOB_BYTES)
            .unwrap();
        let linked = Uuid::now_v7().to_string();
        std::os::unix::fs::symlink(place(&regular), place(&linked)).unwrap();

        let mut survey = Survey::default();
        for nzb in [regular, linked] {
            survey.file(&blobs, (String::new(), 201, None, None, Some(nzb), false));
        }
        let oversized = Uuid::now_v7().to_string();
        File::create(place(&oversized))
            .unwrap()
            .set_len(MAX_NZB_BLOB_BYTES + 1)
            .unwrap();
        survey.file(
            &blobs,
            (String::new(), 201, None, None, Some(oversized), false),
        );
        std::fs::remove_dir_all(&dir).unwrap();
        assert_eq!(survey.missing_nzb_blobs, 2);
    }

    #[test]
    fn read_blob_follows_no_symlink_below_the_blobs_folder() {
        use std::os::unix::fs::symlink;
        let dir = std::env::temp_dir().join(format!("infinidysk-blobs-{}", Uuid::now_v7()));
        let (blobs, outside) = (dir.join("blobs"), dir.join("outside"));
        std::fs::create_dir_all(&blobs).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        let write = |id: &str, bytes: &[u8]| {
            let path = blob_path(&blobs, id).unwrap();
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, bytes).unwrap();
        };

        let id = Uuid::now_v7().to_string();
        write(&id, b"blob");
        assert_eq!(read_blob(&blobs, &id, 64).unwrap(), Some(b"blob".to_vec()));
        assert_eq!(
            read_blob(&blobs, &Uuid::now_v7().to_string(), 64).unwrap(),
            None
        );
        let linked = dir.join("linked-blobs");
        symlink(&blobs, &linked).unwrap();
        assert_eq!(read_blob(&linked, &id, 64).unwrap(), Some(b"blob".to_vec()));

        let big = Uuid::now_v7().to_string();
        write(&big, b"12345");
        let error = read_blob(&blobs, &big, 4).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::InvalidData);

        std::fs::write(outside.join("secret"), b"secret").unwrap();
        let leaf = Uuid::now_v7().to_string();
        let path = blob_path(&blobs, &leaf).unwrap();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        symlink(outside.join("secret"), &path).unwrap();
        let error = read_blob(&blobs, &leaf, 64).unwrap_err();
        assert_ne!(error.kind(), io::ErrorKind::NotFound);

        // v7 ids above share their first byte; flip it so the symlink is the only way in.
        let mut bytes = Uuid::now_v7().into_bytes();
        bytes[0] ^= 0xff;
        let parent = Uuid::from_bytes(bytes);
        let hex = parent.simple().to_string();
        let target = outside.join("p").join(&hex[2..4]);
        std::fs::create_dir_all(&target).unwrap();
        std::fs::write(target.join(parent.to_string()), b"secret").unwrap();
        symlink(outside.join("p"), blobs.join(&hex[..2])).unwrap();
        assert!(read_blob(&blobs, &parent.to_string(), 64).is_err());

        let fifo = Uuid::now_v7().to_string();
        let path = blob_path(&blobs, &fifo).unwrap();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        nix::unistd::mkfifo(&path, Mode::S_IRUSR | Mode::S_IWUSR).unwrap();
        let (tx, rx) = std::sync::mpsc::channel();
        let reader = blobs.clone();
        std::thread::spawn(move || tx.send(read_blob(&reader, &fifo, 64).map_err(|e| e.kind())));
        let read = rx
            .recv_timeout(std::time::Duration::from_secs(5))
            .expect("a FIFO must not block the read");
        assert_eq!(read, Err(io::ErrorKind::InvalidData));

        let root = dir.join("fifo-blobs");
        nix::unistd::mkfifo(&root, Mode::S_IRUSR | Mode::S_IWUSR).unwrap();
        let (tx, rx) = std::sync::mpsc::channel();
        std::thread::spawn(move || tx.send(read_blob(&root, &id, 64).is_err()));
        let refused = rx
            .recv_timeout(std::time::Duration::from_secs(5))
            .expect("a FIFO blobs folder must not block the read");
        assert!(refused);

        std::fs::remove_dir_all(&dir).unwrap();
    }
}
