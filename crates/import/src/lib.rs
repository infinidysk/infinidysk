//! One-time importer of a C# `/config`, deleted together with C# support: a survey of
//! what an import would carry over, the C# settings, and [`apply`], which writes the data.

mod apply;
mod csharp;
mod memorypack;
mod password;
mod settings;

use std::fmt;
use std::path::{Path, PathBuf};

use sqlx::ConnectOptions as _;
use sqlx::SqliteConnection;
use sqlx::sqlite::SqliteConnectOptions;
use uuid::Uuid;

pub use crate::apply::{
    Imported, Progress, apply, apply_with_progress, retry_files, stamp_download_ids,
};
use crate::csharp::{LongRange, decode};
pub use crate::password::{is_legacy, legacy_hash, verify_legacy};
pub use crate::settings::{ArrInstance, ArrKind, Config, Provider, ProviderKind};

/// The oldest C# schema imported (v1.3.0): the import reads every `DavItems` column up
/// to `HealthRepairPending`.
const OLDEST_MIGRATION: &str = "20260824160000";
const SHOWN_FAILURES: usize = 20;

#[derive(Debug, thiserror::Error)]
pub enum Error {
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

/// Opens the C# database in `config` read-only.
async fn open(config: &Path) -> Result<SqliteConnection, Error> {
    let path = config.join("db.sqlite");
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
    let blobs = config.join("blobs");
    tokio::task::spawn_blocking(move || {
        for file in files {
            s.file(&blobs, file);
        }
        s
    })
    .await
    .map_err(Error::from)
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
        if !nzb.is_some_and(|n| blob_path(blobs, &n).is_some_and(|p| p.is_file())) {
            self.missing_nzb_blobs += 1;
        }
        let Some(path) = meta.and_then(|m| blob_path(blobs, &m)) else {
            self.missing_file_blobs += 1;
            return;
        };
        let bytes = match std::fs::read(&path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                self.missing_file_blobs += 1;
                return;
            }
            Err(error) => return self.fail(id, error.to_string()),
        };
        let size = size.unwrap_or_default();
        let result = match sub_type {
            201 => {
                self.nzb_files += 1;
                decode(&bytes, csharp::nzb_file).map(|f| self.nzb(&f, size))
            }
            202 => {
                self.rar_files += 1;
                decode(&bytes, csharp::rar_file).map(|f| self.rar(&f, size))
            }
            203 => {
                self.multipart_files += 1;
                decode(&bytes, csharp::multipart_file).map(|f| self.multipart(&f, size))
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
fn blob_path(blobs: &Path, id: &str) -> Option<PathBuf> {
    let id = Uuid::parse_str(id).ok()?;
    let hex = id.simple().to_string();
    Some(blobs.join(&hex[..2]).join(&hex[2..4]).join(id.to_string()))
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
}
