//! Backup files of the SQLite database: writing one from the live database and checking
//! one before it is kept or restored.

use std::path::Path;

use sqlx::sqlite::{SqliteConnectOptions, SqliteConnection};
use sqlx::{ConnectOptions as _, Connection as _};

use crate::{Db, Error, Pool, SQLITE_MIGRATOR};

/// VM steps between two calls of a long statement's `stop` check.
const PROGRESS_OPS: i32 = 100_000;
/// `PRAGMA quick_check` lines kept.
const MAX_PROBLEMS: usize = 10;

/// `SQLITE_INTERRUPT` (9) and `SQLITE_FULL` (13) become their own variants.
pub(crate) fn sqlite_error(error: sqlx::Error) -> Error {
    match primary_code(&error) {
        Some(9) => Error::Interrupted,
        Some(13) => Error::DiskFull,
        _ => Error::Sqlx(error),
    }
}

/// The primary SQLite result code; sqlx reports the extended one.
fn primary_code(error: &sqlx::Error) -> Option<i32> {
    let sqlx::Error::Database(e) = error else {
        return None;
    };
    e.code()?.parse::<i32>().ok().map(|code| code & 0xff)
}

impl Db {
    /// SQLite only. Writes a consistent, compacted copy of the database to `path`, which
    /// must not exist or be empty, with `VACUUM INTO`. Writers keep going; the WAL grows
    /// until it ends. SQLite does not fsync the copy; the caller does. `stop` returning
    /// `true` interrupts it with [`Error::Interrupted`].
    ///
    /// # Errors
    ///
    /// [`Error::NotSqlite`] on PostgreSQL, [`Error::Interrupted`], [`Error::DiskFull`], or
    /// the failed statement.
    pub async fn backup_into(
        &self,
        path: &Path,
        mut stop: impl FnMut() -> bool + Send + 'static,
    ) -> Result<(), Error> {
        let Pool::Sqlite(pool) = &self.pool else {
            return Err(Error::NotSqlite);
        };
        let path = path
            .to_str()
            .ok_or(Error::Corrupt("backup path is not UTF-8"))?;
        // Detached: the progress handler must not stay on a pooled connection.
        let mut conn = pool.acquire().await?.detach();
        conn.lock_handle()
            .await?
            .set_progress_handler(PROGRESS_OPS, move || !stop());
        // The path is a bound value, never spliced into the SQL.
        let result = sqlx::query("VACUUM INTO $1")
            .bind(path)
            .execute(&mut conn)
            .await;
        let _ = conn.close().await;
        result.map(drop).map_err(sqlite_error)
    }
}

/// What [`check_file`] found in a backup file.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FileCheck {
    /// Highest migration it applied; 0 when it has none.
    pub schema_version: i64,
    /// Why this build cannot open it; `None` when it can. Older files migrate on open.
    pub incompatible: Option<String>,
    /// What `PRAGMA quick_check` reported, at most 10 lines; empty when sound.
    pub problems: Vec<String>,
    pub items: u64,
}

impl FileCheck {
    fn incompatible(schema_version: i64, why: String) -> Self {
        Self {
            schema_version,
            incompatible: Some(why),
            problems: Vec::new(),
            items: 0,
        }
    }

    /// Why the file may not be kept or restored, for people; `None` when it is fine.
    #[must_use]
    pub fn failure(&self) -> Option<String> {
        if let Some(why) = &self.incompatible {
            return Some(why.clone());
        }
        (!self.problems.is_empty())
            .then(|| format!("The database is damaged: {}", self.problems.join("; ")))
    }
}

/// Highest SQLite migration this build applies.
#[must_use]
pub fn latest_sqlite_schema() -> i64 {
    SQLITE_MIGRATOR.iter().map(|m| m.version).max().unwrap_or(0)
}

/// Opens `path` read-only and immutable (nothing may write it meanwhile), checks this
/// build can open it, then runs `PRAGMA quick_check`. `stop` as in [`Db::backup_into`].
/// The file is closed when this returns, whatever the outcome.
///
/// # Errors
///
/// [`Error::Interrupted`] on a stop, or the failed open or statement.
pub async fn check_file(
    path: &Path,
    mut stop: impl FnMut() -> bool + Send + 'static,
) -> Result<FileCheck, Error> {
    let result = match SqliteConnectOptions::new()
        .filename(path)
        .read_only(true)
        .immutable(true)
        .connect()
        .await
    {
        Ok(mut conn) => {
            let result = async {
                conn.lock_handle()
                    .await?
                    .set_progress_handler(PROGRESS_OPS, move || !stop());
                inspect(&mut conn).await
            }
            .await;
            let _ = conn.close().await;
            result
        }
        Err(error) => Err(Error::Sqlx(error)),
    };
    match result {
        // SQLITE_NOTADB: not a database at all.
        Err(Error::Sqlx(e)) if primary_code(&e) == Some(26) => Ok(FileCheck::incompatible(
            0,
            "This is not a SQLite database file.".to_owned(),
        )),
        Err(Error::Sqlx(e)) => Err(sqlite_error(e)),
        result => result,
    }
}

async fn inspect(conn: &mut SqliteConnection) -> Result<FileCheck, Error> {
    let tables: Vec<String> = sqlx::query_scalar(
        "SELECT name FROM sqlite_master WHERE type = 'table' \
         AND name IN ('_sqlx_migrations', '__EFMigrationsHistory')",
    )
    .fetch_all(&mut *conn)
    .await?;
    if !tables.iter().any(|t| t == "_sqlx_migrations") {
        let why = if tables.iter().any(|t| t == "__EFMigrationsHistory") {
            "This is an infinidysk 1.x database. 2.x imports 1.x data on first start, not from a backup."
        } else {
            "This is not an infinidysk database."
        };
        return Ok(FileCheck::incompatible(0, why.to_owned()));
    }
    let applied: Vec<(i64, Vec<u8>)> = sqlx::query_as(
        "SELECT version, checksum FROM _sqlx_migrations WHERE success ORDER BY version",
    )
    .fetch_all(&mut *conn)
    .await?;
    let schema_version = applied.last().map_or(0, |(v, _)| *v);
    let dirty: Option<i64> = sqlx::query_scalar(
        "SELECT version FROM _sqlx_migrations WHERE NOT success ORDER BY version LIMIT 1",
    )
    .fetch_optional(&mut *conn)
    .await?;
    if let Some(version) = dirty {
        return Ok(FileCheck::incompatible(
            schema_version,
            format!("A database change did not finish (version {version}), so it cannot open."),
        ));
    }
    let latest = latest_sqlite_schema();
    for (version, checksum) in &applied {
        let known = SQLITE_MIGRATOR.iter().find(|m| m.version == *version);
        let why = match known {
            None if *version > latest => format!(
                "Made by a newer infinidysk (database version {version}; this one knows up \
                 to {latest}). Update infinidysk first."
            ),
            Some(m) if *m.checksum == **checksum => continue,
            _ => format!("Has a database change this version does not know (version {version})."),
        };
        return Ok(FileCheck::incompatible(schema_version, why));
    }
    let items: i64 = sqlx::query_scalar("SELECT count(*) FROM items")
        .fetch_one(&mut *conn)
        .await?;
    let problems: Vec<String> = sqlx::query_scalar(concat!("PRAGMA quick_check(", 10, ")"))
        .fetch_all(&mut *conn)
        .await?;
    let problems = if problems.len() == 1 && problems[0] == "ok" {
        Vec::new()
    } else {
        problems.into_iter().take(MAX_PROBLEMS).collect()
    };
    Ok(FileCheck {
        schema_version,
        incompatible: None,
        problems,
        items: u64::try_from(items).unwrap_or(0),
    })
}
