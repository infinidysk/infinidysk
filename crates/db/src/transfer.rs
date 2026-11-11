//! Copying a SQLite database to PostgreSQL, then keeping it in sync while the app runs.
//!
//! While a move runs, SQLite triggers log the key of every changed row in
//! `pg_sync_changes`, and [`Db::sync`] applies those rows to PostgreSQL. This is the one
//! module that builds SQL at run time: one string per table, from [`TABLES`] and the
//! target's own catalog. Identifiers are quoted and every value is bound.

use std::collections::{BTreeSet, HashSet};
use std::fmt::Write as _;
use std::str::FromStr as _;
use std::time::Duration;

use futures_util::TryStreamExt as _;
use infinidysk_core::ItemId;
use sha2::{Digest as _, Sha256};
use sqlx::postgres::{PgArguments, PgConnectOptions, PgRow, Postgres};
use sqlx::query::Query;
use sqlx::sqlite::{Sqlite, SqliteArguments, SqliteConnection, SqlitePool, SqliteRow};
use sqlx::{AssertSqlSafe, ConnectOptions as _, Connection as _, Row as _};
use uuid::Uuid;

pub use sqlx::postgres::PgConnection;

use crate::{Db, Error, Pool, migrated};

/// A table's primary key. A table without one is replaced whole when it changes.
#[derive(Clone, Copy, Debug)]
pub(crate) enum Key {
    None,
    One(&'static str),
    Two(&'static str, &'static str),
}

impl Key {
    fn names(self) -> Vec<&'static str> {
        match self {
            Self::None => Vec::new(),
            Self::One(a) => vec![a],
            Self::Two(a, b) => vec![a, b],
        }
    }

    /// The trigger's `k1, k2` values for the `NEW` or `OLD` row.
    fn logged(self, row: &str) -> String {
        match self {
            Self::None => "NULL, NULL".to_owned(),
            Self::One(a) => format!("{row}.{}, NULL", quote(a)),
            Self::Two(a, b) => format!("{row}.{}, {row}.{}", quote(a), quote(b)),
        }
    }
}

/// Every table, parents before children (foreign keys).
pub(crate) const TABLES: &[(&str, Key)] = &[
    ("blobs", Key::One("id")),
    ("repair_patches", Key::One("id")),
    ("items", Key::One("id")),
    ("queue", Key::One("id")),
    ("repair_jobs", Key::One("id")),
    ("legacy_repair_articles", Key::One("hash")),
    ("history", Key::One("id")),
    ("health_checks", Key::One("id")),
    ("media_probes", Key::One("item_id")),
    ("rejected_releases", Key::One("id")),
    ("rejected_articles", Key::Two("message_id", "release_id")),
    ("arr_refresh", Key::One("category")),
    ("search_reservations", Key::None),
    ("arr_owners", Key::Two("instance_id", "item_id")),
    ("arr_events", Key::One("id")),
    ("config", Key::One("key")),
    ("accounts", Key::Two("kind", "username")),
    ("handoff_files", Key::One("path")),
    ("library_files", Key::One("source")),
];

/// Tables a foreign key points at. Their deletes run after every upsert.
const PARENTS: &[&str] = &["blobs", "items", "rejected_releases"];

const CHANGES: &str = "pg_sync_changes";
const BATCH_ROWS: usize = 1000;
const BATCH_BYTES: usize = 16 * 1024 * 1024;
/// PostgreSQL's limit on bind parameters per statement.
const MAX_BINDS: usize = 65_535;
const KEY_BATCH: usize = 500;

/// How far a copy, sync pass or verification got. Every report is also a heartbeat.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Progress {
    pub stage: Stage,
    pub table: &'static str,
    pub done: u64,
    pub total: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Stage {
    Copying,
    Syncing,
    Verifying,
}

/// The change capture objects in a SQLite file.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Capture {
    Off,
    Intact,
    /// Some capture objects exist but not all, e.g. after a migration rebuilt a table.
    Broken,
}

/// What a candidate target holds. Read-only: no DDL, no migrations.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TargetCheck {
    /// `SHOW server_version`.
    pub version: String,
    /// Base tables in the current schema that are not ours.
    pub foreign_tables: Vec<String>,
    /// Our tables that exist and hold a row; `items` ignores the two seed items.
    pub used: Vec<&'static str>,
    /// May create tables in the current schema.
    pub can_create: bool,
    /// The database's collation (`datcollate`).
    pub collation: String,
}

/// A dedicated, unpooled connection for transfer work. A lock wait longer than
/// `lock_timeout` fails on the server. Dropping the connection closes its socket at once.
pub async fn connect(url: &str, lock_timeout: Duration) -> Result<PgConnection, Error> {
    if !(url.starts_with("postgres:") || url.starts_with("postgresql:")) {
        return Err(Error::NotPostgres);
    }
    let options = PgConnectOptions::from_str(url)?
        .application_name("infinidysk-transfer")
        .options([("lock_timeout", format!("{}ms", lock_timeout.as_millis()))]);
    Ok(options.connect().await?)
}

/// `user@host:port/database` for a PostgreSQL URL; never the password.
pub fn describe(url: &str) -> Result<String, Error> {
    if !(url.starts_with("postgres:") || url.starts_with("postgresql:")) {
        return Err(Error::NotPostgres);
    }
    let options = PgConnectOptions::from_str(url)?;
    let user = options.get_username();
    Ok(format!(
        "{user}@{}:{}/{}",
        options.get_host(),
        options.get_port(),
        options.get_database().unwrap_or(user)
    ))
}

/// Brings `conn`'s database to this build's schema.
pub async fn migrate(conn: &mut PgConnection) -> Result<(), Error> {
    // `run_direct` is `run` without the generic `Acquire`, whose future fails rustc's
    // higher-ranked `Send` check inside spawned tasks.
    migrated(
        sqlx::migrate!("./migrations/postgres")
            .run_direct(None, conn, false)
            .await,
    )
}

/// Empties every table we own.
pub async fn truncate_all(conn: &mut PgConnection) -> Result<(), Error> {
    let names: Vec<String> = TABLES.iter().map(|(name, _)| quote(name)).collect();
    dynamic_pg(format!("TRUNCATE {}", names.join(", ")))
        .execute(conn)
        .await?;
    Ok(())
}

/// Looks at a candidate target over one connection, dropped after.
pub async fn check_target(url: &str) -> Result<TargetCheck, Error> {
    let mut conn = connect(url, Duration::from_secs(5)).await?;
    let version: String = sqlx::query_scalar("SHOW server_version")
        .fetch_one(&mut conn)
        .await?;
    let tables: Vec<String> = sqlx::query_scalar(
        "SELECT table_name::text FROM information_schema.tables \
         WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY 1",
    )
    .fetch_all(&mut conn)
    .await?;
    let foreign_tables = tables
        .iter()
        .filter(|name| *name != "_sqlx_migrations" && !TABLES.iter().any(|(t, _)| t == name))
        .cloned()
        .collect();
    let mut used = Vec::new();
    for &(name, _) in TABLES {
        if !tables.iter().any(|t| t == name) {
            continue;
        }
        let filter = if name == "items" {
            " WHERE id NOT IN ($1, $2)"
        } else {
            ""
        };
        let mut query = sqlx::query_scalar::<_, bool>(AssertSqlSafe(format!(
            "SELECT EXISTS (SELECT 1 FROM {}{filter})",
            quote(name)
        )))
        .persistent(false);
        if name == "items" {
            query = query.bind(ItemId::ROOT.0).bind(ItemId::CONTENT.0);
        }
        if query.fetch_one(&mut conn).await? {
            used.push(name);
        }
    }
    let can_create: bool = sqlx::query_scalar(
        "SELECT COALESCE(has_schema_privilege(current_schema(), 'CREATE'), false)",
    )
    .fetch_one(&mut conn)
    .await?;
    let collation: String = sqlx::query_scalar(
        "SELECT COALESCE(datcollate::text, '') FROM pg_database WHERE datname = current_database()",
    )
    .fetch_one(&mut conn)
    .await?;
    Ok(TargetCheck {
        version,
        foreign_tables,
        used,
        can_create,
        collation,
    })
}

impl Db {
    #[must_use]
    pub const fn is_postgres(&self) -> bool {
        matches!(self.pool, Pool::Postgres(_))
    }

    fn sqlite(&self) -> Result<&SqlitePool, Error> {
        match &self.pool {
            Pool::Sqlite(pool) => Ok(pool),
            Pool::Postgres(_) => Err(Error::NotSqlite),
        }
    }

    /// The highest applied migration version.
    pub async fn schema_version(&self) -> Result<i64, Error> {
        const SQL: &str = "SELECT COALESCE(MAX(version), 0) FROM _sqlx_migrations WHERE success";
        Ok(match &self.pool {
            Pool::Sqlite(pool) => sqlx::query_scalar(SQL).fetch_one(pool).await?,
            Pool::Postgres(pool) => sqlx::query_scalar(SQL).fetch_one(pool).await?,
        })
    }

    /// SQLite only. Creates the change table and its triggers; idempotent.
    pub async fn start_capture(&self) -> Result<(), Error> {
        let mut tx = self.sqlite()?.begin().await?;
        sqlx::query(
            "CREATE TABLE IF NOT EXISTS pg_sync_changes \
             (seq INTEGER PRIMARY KEY, tbl TEXT NOT NULL, k1, k2)",
        )
        .execute(&mut *tx)
        .await?;
        for &(table, key) in TABLES {
            let (new, old) = (key.logged("NEW"), key.logged("OLD"));
            let quoted = quote(table);
            for (op, values) in [
                ("insert", format!("('{table}', {new})")),
                ("update", format!("('{table}', {old}), ('{table}', {new})")),
                ("delete", format!("('{table}', {old})")),
            ] {
                let event = op.to_ascii_uppercase();
                dynamic_sqlite(format!(
                    "CREATE TRIGGER IF NOT EXISTS pg_sync_{table}_{op} AFTER {event} ON {quoted} \
                     BEGIN INSERT INTO {CHANGES} (tbl, k1, k2) VALUES {values}; END"
                ))
                .execute(&mut *tx)
                .await?;
            }
        }
        tx.commit().await?;
        Ok(())
    }

    /// SQLite only. Drops every capture trigger and the change table; idempotent.
    pub async fn stop_capture(&self) -> Result<(), Error> {
        let pool = self.sqlite()?;
        let found = capture_objects(pool).await?;
        let mut tx = pool.begin().await?;
        for kind in ["trigger", "table"] {
            for (_, name, _) in found.iter().filter(|(k, _, _)| k == kind) {
                let what = kind.to_ascii_uppercase();
                dynamic_sqlite(format!("DROP {what} IF EXISTS {}", quote(name)))
                    .execute(&mut *tx)
                    .await?;
            }
        }
        tx.commit().await?;
        Ok(())
    }

    /// SQLite only. Whether the change table and every trigger, each on its own table, exist.
    pub async fn capture(&self) -> Result<Capture, Error> {
        let found = capture_objects(self.sqlite()?).await?;
        if found.is_empty() {
            return Ok(Capture::Off);
        }
        Ok(if found == capture_set() {
            Capture::Intact
        } else {
            Capture::Broken
        })
    }

    /// Folds the SQLite WAL back into the main file. Does nothing on PostgreSQL.
    pub async fn checkpoint(&self) -> Result<(), Error> {
        if let Pool::Sqlite(pool) = &self.pool {
            sqlx::query("PRAGMA wal_checkpoint(TRUNCATE)")
                .execute(pool)
                .await?;
        }
        Ok(())
    }

    /// Rows waiting in the change table; `0` without one.
    pub async fn pending_changes(&self) -> Result<u64, Error> {
        let pool = self.sqlite()?;
        let mut conn = pool.acquire().await?;
        if !has_changes(&mut conn).await? {
            return Ok(0);
        }
        let rows: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM pg_sync_changes")
            .fetch_one(&mut *conn)
            .await?;
        Ok(u64::try_from(rows).unwrap_or(0))
    }

    /// Empties `target`'s tables, then copies every table from one SQLite snapshot.
    /// `target` must be migrated to this build's schema.
    pub async fn copy_all(
        &self,
        target: &mut PgConnection,
        progress: impl Fn(Progress) + Sync,
    ) -> Result<(), Error> {
        let pool = self.sqlite()?;
        let mut snap = pool.begin().await?;
        if !has_changes(&mut snap).await? {
            return Err(Error::NotCapturing);
        }
        let tables = load_tables(target).await?;
        truncate_all(target).await?;
        // The first read starts the snapshot; later changes have a higher `seq`.
        let hw: i64 = sqlx::query_scalar("SELECT COALESCE(MAX(seq), 0) FROM pg_sync_changes")
            .fetch_one(&mut *snap)
            .await?;
        let mut totals = Vec::with_capacity(tables.len());
        for table in &tables {
            totals.push(count(&mut snap, table).await?);
        }
        // ponytail: one snapshot holds the WAL back for the whole copy, fine below a few GB;
        // a larger install wants `COPY` (`copy_in_raw`) and a resumable copy.
        for (table, total) in tables.iter().zip(totals) {
            let report = |done| {
                progress(Progress {
                    stage: Stage::Copying,
                    table: table.name,
                    done,
                    total,
                });
            };
            report(0);
            let mut batch = Batch::new(table, false);
            let mut done = 0;
            let mut rows = dynamic_sqlite(table.select("")).fetch(&mut *snap);
            while let Some(row) = rows.try_next().await? {
                if batch.push(table.sqlite_row(&row)?) {
                    done += batch.flush(target).await?;
                    report(done);
                }
            }
            drop(rows);
            done += batch.flush(target).await?;
            report(done);
        }
        snap.commit().await?;
        sqlx::query("DELETE FROM pg_sync_changes WHERE seq <= $1")
            .bind(hw)
            .execute(pool)
            .await?;
        Ok(())
    }

    /// Applies logged changes up to one SQLite snapshot. With `verify`, then compares every
    /// table. Returns how many keys it applied.
    pub async fn sync(
        &self,
        target: &mut PgConnection,
        verify: bool,
        progress: impl Fn(Progress) + Sync,
    ) -> Result<u64, Error> {
        let progress: &(dyn Fn(Progress) + Sync) = &progress;
        let pool = self.sqlite()?;
        let mut snap = pool.begin().await?;
        if !has_changes(&mut snap).await? {
            return Err(Error::NotCapturing);
        }
        let tables = load_tables(target).await?;
        let hw: i64 = sqlx::query_scalar("SELECT COALESCE(MAX(seq), 0) FROM pg_sync_changes")
            .fetch_one(&mut *snap)
            .await?;
        let keys: i64 = sqlx::query_scalar(
            "SELECT COUNT(*) FROM (SELECT DISTINCT tbl, k1, k2 FROM pg_sync_changes WHERE seq <= $1)",
        )
        .bind(hw)
        .fetch_one(&mut *snap)
        .await?;
        let keys = u64::try_from(keys).unwrap_or(0);
        if keys > 0 {
            apply(&mut snap, target, &tables, hw, keys, progress).await?;
        }
        if verify {
            compare(&mut snap, target, &tables, progress).await?;
        }
        snap.commit().await?;
        if keys > 0 {
            sqlx::query("DELETE FROM pg_sync_changes WHERE seq <= $1")
                .bind(hw)
                .execute(pool)
                .await?;
        }
        Ok(keys)
    }
}

/// Writes the logged changes up to `hw` to `target` in one transaction.
async fn apply(
    snap: &mut SqliteConnection,
    target: &mut PgConnection,
    tables: &[Table],
    hw: i64,
    total: u64,
    progress: &(dyn Fn(Progress) + Sync),
) -> Result<(), Error> {
    let changed: HashSet<String> =
        sqlx::query_scalar("SELECT DISTINCT tbl FROM pg_sync_changes WHERE seq <= $1")
            .bind(hw)
            .fetch_all(&mut *snap)
            .await?
            .into_iter()
            .collect();
    let mut done = 0;
    let report = |table, done| {
        progress(Progress {
            stage: Stage::Syncing,
            table,
            done,
            total,
        });
    };
    let mut gone = Vec::with_capacity(tables.len());
    for table in tables {
        let keys = if changed.contains(table.name) && !table.key_at.is_empty() {
            table.gone(snap, hw).await?
        } else {
            Vec::new()
        };
        report(table.name, done);
        gone.push(keys);
    }
    let mut tx = target.begin().await?;
    if changed.contains("items") {
        // Parking frees every changed item's path and metadata blob, so renames, swaps
        // and reuse never collide with a `UNIQUE` constraint mid-transaction.
        let ids: Vec<Uuid> = sqlx::query_scalar(
            "SELECT DISTINCT k1 FROM pg_sync_changes WHERE tbl = 'items' AND seq <= $1",
        )
        .bind(hw)
        .fetch_all(&mut *snap)
        .await?;
        for chunk in ids.chunks(KEY_BATCH) {
            sqlx::query(
                "UPDATE items SET path = '//parked/' || id::text, meta_blob = NULL \
                 WHERE id = ANY($1)",
            )
            .bind(chunk)
            .execute(&mut *tx)
            .await?;
            report("items", done);
        }
    }
    // Child deletes first: they free unique keys such as the one admin account.
    for (table, keys) in tables.iter().zip(&gone) {
        if !PARENTS.contains(&table.name) {
            let base = done;
            done += table
                .delete(&mut tx, keys, &|n| report(table.name, base + n))
                .await?;
        }
    }
    for table in tables.iter().filter(|t| changed.contains(t.name)) {
        let query = if table.key_at.is_empty() {
            dynamic_pg(format!("DELETE FROM {}", quote(table.name)))
                .execute(&mut *tx)
                .await?;
            dynamic_sqlite(table.select(""))
        } else {
            dynamic_sqlite(table.select(&table.changed_filter()))
                .bind(table.name)
                .bind(hw)
        };
        let mut batch = Batch::new(table, true);
        let mut rows = query.fetch(&mut *snap);
        while let Some(row) = rows.try_next().await? {
            if batch.push(table.sqlite_row(&row)?) {
                done += batch.flush(&mut tx).await?;
                report(table.name, done);
            }
        }
        drop(rows);
        done += batch.flush(&mut tx).await?;
        report(table.name, done);
    }
    // Parent deletes last, children before parents: anything that moved out of a deleted
    // folder is already under its new parent when the cascade runs.
    for (table, keys) in tables.iter().zip(&gone).rev() {
        if PARENTS.contains(&table.name) {
            let base = done;
            done += table
                .delete(&mut tx, keys, &|n| report(table.name, base + n))
                .await?;
        }
    }
    tx.commit().await?;
    Ok(())
}

/// Compares every table's row count and order-independent row hash on both sides.
async fn compare(
    snap: &mut SqliteConnection,
    target: &mut PgConnection,
    tables: &[Table],
    progress: &(dyn Fn(Progress) + Sync),
) -> Result<(), Error> {
    for table in tables {
        let total = count(snap, table).await?;
        let report = |done| {
            progress(Progress {
                stage: Stage::Verifying,
                table: table.name,
                done,
                total,
            });
        };
        report(0);
        let mut ours = Sum::default();
        let mut rows = dynamic_sqlite(table.select("")).fetch(&mut *snap);
        while let Some(row) = rows.try_next().await? {
            ours.add(&table.sqlite_row(&row)?);
            if ours.count % BATCH_ROWS as u64 == 0 {
                report(ours.count);
            }
        }
        drop(rows);
        let mut theirs = Sum::default();
        let mut rows = dynamic_pg(table.select("")).fetch(&mut *target);
        while let Some(row) = rows.try_next().await? {
            theirs.add(&table.postgres_row(&row)?);
            if theirs.count % BATCH_ROWS as u64 == 0 {
                report(theirs.count);
            }
        }
        drop(rows);
        if ours != theirs {
            return Err(Error::Mismatch(table.name));
        }
        report(total);
    }
    Ok(())
}

async fn count(snap: &mut SqliteConnection, table: &Table) -> Result<u64, Error> {
    let rows: i64 = sqlx::query_scalar(AssertSqlSafe(format!(
        "SELECT COUNT(*) FROM {}",
        quote(table.name)
    )))
    .persistent(false)
    .fetch_one(snap)
    .await?;
    Ok(u64::try_from(rows).unwrap_or(0))
}

async fn has_changes(conn: &mut SqliteConnection) -> Result<bool, Error> {
    Ok(sqlx::query_scalar(
        "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'pg_sync_changes')",
    )
    .fetch_one(conn)
    .await?)
}

/// `(type, name, tbl_name)` of every object `start_capture` creates.
fn capture_set() -> BTreeSet<(String, String, String)> {
    let mut set = BTreeSet::from([("table".to_owned(), CHANGES.to_owned(), CHANGES.to_owned())]);
    for &(table, _) in TABLES {
        for op in ["insert", "update", "delete"] {
            set.insert((
                "trigger".to_owned(),
                format!("pg_sync_{table}_{op}"),
                table.to_owned(),
            ));
        }
    }
    set
}

/// The capture objects present, matched by type and name only, so a trigger moved to
/// another table still counts.
async fn capture_objects(pool: &SqlitePool) -> Result<BTreeSet<(String, String, String)>, Error> {
    let ours: BTreeSet<(String, String)> = capture_set()
        .into_iter()
        .map(|(kind, name, _)| (kind, name))
        .collect();
    let rows: Vec<(String, String, String)> = sqlx::query_as(
        "SELECT type, name, tbl_name FROM sqlite_master WHERE substr(name, 1, 8) = 'pg_sync_'",
    )
    .fetch_all(pool)
    .await?;
    Ok(rows
        .into_iter()
        .filter(|(kind, name, _)| ours.contains(&(kind.clone(), name.clone())))
        .collect())
}

/// One table as the target's catalog describes it.
struct Table {
    name: &'static str,
    key: Key,
    kinds: Vec<Kind>,
    /// Quoted column names, comma-separated.
    list: String,
    /// Position of each key column in `kinds`.
    key_at: Vec<usize>,
    /// ` ON CONFLICT … DO …`; empty without a key.
    conflict: String,
}

impl Table {
    /// `SELECT <columns> FROM <table><filter>`, parents first for `items`.
    fn select(&self, filter: &str) -> String {
        let order = if self.name == "items" {
            " ORDER BY length(path)"
        } else {
            ""
        };
        format!(
            "SELECT {} FROM {}{filter}{order}",
            self.list,
            quote(self.name)
        )
    }

    /// Rows whose key is logged as changed (`$1` table, `$2` high-water mark).
    fn changed_filter(&self) -> String {
        let names = self.key.names();
        let (lhs, logged) = if names.len() == 1 {
            (quote(names[0]), "k1")
        } else {
            (
                format!(
                    "({})",
                    names
                        .iter()
                        .map(|n| quote(n))
                        .collect::<Vec<_>>()
                        .join(", ")
                ),
                "k1, k2",
            )
        };
        format!(" WHERE {lhs} IN (SELECT {logged} FROM {CHANGES} WHERE tbl = $1 AND seq <= $2)")
    }

    /// Logged keys with no row in the snapshot.
    async fn gone(&self, snap: &mut SqliteConnection, hw: i64) -> Result<Vec<Vec<Val>>, Error> {
        let matches: Vec<String> = self
            .key
            .names()
            .iter()
            .zip(["k1", "k2"])
            .map(|(name, logged)| format!("t.{} = c.{logged}", quote(name)))
            .collect();
        let rows = dynamic_sqlite(format!(
            "SELECT DISTINCT c.k1, c.k2 FROM {CHANGES} c WHERE c.tbl = $1 AND c.seq <= $2 \
             AND NOT EXISTS (SELECT 1 FROM {} t WHERE {})",
            quote(self.name),
            matches.join(" AND ")
        ))
        .bind(self.name)
        .bind(hw)
        .fetch_all(snap)
        .await?;
        rows.iter()
            .map(|row| {
                self.key_at
                    .iter()
                    .enumerate()
                    .map(|(i, &at)| Val::sqlite(row, i, self.kinds[at]))
                    .collect()
            })
            .collect()
    }

    /// Deletes rows by key in `KEY_BATCH` chunks; reports each chunk.
    async fn delete(
        &self,
        conn: &mut PgConnection,
        keys: &[Vec<Val>],
        report: &(dyn Fn(u64) + Sync),
    ) -> Result<u64, Error> {
        let names: Vec<String> = self.key.names().iter().map(|n| quote(n)).collect();
        let mut done = 0;
        for chunk in keys.chunks(KEY_BATCH) {
            let mut sql = format!(
                "DELETE FROM {} WHERE ({}) IN (",
                quote(self.name),
                names.join(", ")
            );
            placeholders(&mut sql, chunk.len(), names.len());
            sql.push(')');
            let mut query = dynamic_pg(sql);
            for value in chunk.iter().flatten() {
                query = value.clone().bind(query);
            }
            query.execute(&mut *conn).await?;
            done += chunk.len() as u64;
            report(done);
        }
        Ok(done)
    }

    fn sqlite_row(&self, row: &SqliteRow) -> Result<Vec<Val>, Error> {
        self.kinds
            .iter()
            .enumerate()
            .map(|(i, &kind)| Val::sqlite(row, i, kind))
            .collect()
    }

    fn postgres_row(&self, row: &PgRow) -> Result<Vec<Val>, Error> {
        self.kinds
            .iter()
            .enumerate()
            .map(|(i, &kind)| Val::postgres(row, i, kind))
            .collect()
    }
}

/// Reads every table's columns and types from the target's catalog, so a migration that
/// adds a column needs no change here.
async fn load_tables(conn: &mut PgConnection) -> Result<Vec<Table>, Error> {
    let mut tables = Vec::with_capacity(TABLES.len());
    for &(name, key) in TABLES {
        let columns: Vec<(String, String)> = sqlx::query_as(
            "SELECT column_name::text, data_type::text FROM information_schema.columns \
             WHERE table_schema = current_schema() AND table_name = $1 ORDER BY ordinal_position",
        )
        .bind(name)
        .fetch_all(&mut *conn)
        .await?;
        if columns.is_empty() {
            return Err(Error::Corrupt("a table is missing on PostgreSQL"));
        }
        let mut kinds = Vec::with_capacity(columns.len());
        for (column, data_type) in &columns {
            kinds.push(match data_type.as_str() {
                "uuid" => Kind::Uuid,
                "bytea" => Kind::Bytes,
                "text" => Kind::Text,
                "smallint" => Kind::I16,
                "integer" => Kind::I32,
                "bigint" => Kind::I64,
                "boolean" => Kind::Bool,
                _ => return Err(Error::UnsupportedColumn(format!("{name}.{column}"))),
            });
        }
        let key_at = key
            .names()
            .iter()
            .map(|k| {
                columns
                    .iter()
                    .position(|(column, _)| column == k)
                    .ok_or(Error::Corrupt("a key column is missing on PostgreSQL"))
            })
            .collect::<Result<Vec<_>, _>>()?;
        let list = columns
            .iter()
            .map(|(column, _)| quote(column))
            .collect::<Vec<_>>()
            .join(", ");
        let conflict = if key_at.is_empty() {
            String::new()
        } else {
            let target: Vec<String> = key.names().iter().map(|k| quote(k)).collect();
            let set: Vec<String> = columns
                .iter()
                .enumerate()
                .filter(|(i, _)| !key_at.contains(i))
                .map(|(_, (column, _))| format!("{0} = EXCLUDED.{0}", quote(column)))
                .collect();
            if set.is_empty() {
                format!(" ON CONFLICT ({}) DO NOTHING", target.join(", "))
            } else {
                format!(
                    " ON CONFLICT ({}) DO UPDATE SET {}",
                    target.join(", "),
                    set.join(", ")
                )
            }
        };
        tables.push(Table {
            name,
            key,
            kinds,
            list,
            key_at,
            conflict,
        });
    }
    Ok(tables)
}

/// Rows waiting for one multi-row `INSERT`.
struct Batch<'t> {
    table: &'t Table,
    upsert: bool,
    rows: Vec<Vec<Val>>,
    bytes: usize,
    limit: usize,
}

impl<'t> Batch<'t> {
    fn new(table: &'t Table, upsert: bool) -> Self {
        Self {
            table,
            upsert,
            rows: Vec::new(),
            bytes: 0,
            limit: BATCH_ROWS.min(MAX_BINDS / table.kinds.len().max(1)),
        }
    }

    /// Adds a row; `true` when the batch is full.
    fn push(&mut self, row: Vec<Val>) -> bool {
        self.bytes += row.iter().map(Val::size).sum::<usize>();
        self.rows.push(row);
        self.rows.len() >= self.limit || self.bytes >= BATCH_BYTES
    }

    async fn flush(&mut self, conn: &mut PgConnection) -> Result<u64, Error> {
        if self.rows.is_empty() {
            return Ok(0);
        }
        let rows = std::mem::take(&mut self.rows);
        self.bytes = 0;
        let mut sql = format!(
            "INSERT INTO {} ({}) VALUES ",
            quote(self.table.name),
            self.table.list
        );
        placeholders(&mut sql, rows.len(), self.table.kinds.len());
        if self.upsert {
            sql.push_str(&self.table.conflict);
        }
        let written = rows.len() as u64;
        let mut query = dynamic_pg(sql);
        for value in rows.into_iter().flatten() {
            query = value.bind(query);
        }
        query.execute(conn).await?;
        Ok(written)
    }
}

/// Appends `($1, $2), ($3, $4)` for `rows` tuples of `width` values.
fn placeholders(sql: &mut String, rows: usize, width: usize) {
    for row in 0..rows {
        sql.push_str(if row == 0 { "(" } else { ", (" });
        for column in 0..width {
            if column > 0 {
                sql.push_str(", ");
            }
            let _ = write!(sql, "${}", row * width + column + 1);
        }
        sql.push(')');
    }
}

/// A query built at run time: kept out of the prepared statement cache. Concrete per
/// backend: the generic form trips rustc's higher-ranked `Send` check in spawned tasks.
fn dynamic_sqlite(sql: String) -> Query<'static, Sqlite, SqliteArguments> {
    sqlx::query(AssertSqlSafe(sql)).persistent(false)
}

fn dynamic_pg(sql: String) -> Query<'static, Postgres, PgArguments> {
    sqlx::query(AssertSqlSafe(sql)).persistent(false)
}

fn quote(name: &str) -> String {
    format!("\"{}\"", name.replace('"', "\"\""))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Kind {
    Uuid,
    Bytes,
    Text,
    I16,
    I32,
    I64,
    Bool,
}

/// One cell, typed so a NULL binds with the right PostgreSQL type.
#[derive(Clone, Debug)]
enum Val {
    Uuid(Option<Uuid>),
    Bytes(Option<Vec<u8>>),
    Text(Option<String>),
    I16(Option<i16>),
    I32(Option<i32>),
    I64(Option<i64>),
    Bool(Option<bool>),
}

impl Val {
    fn sqlite(row: &SqliteRow, i: usize, kind: Kind) -> Result<Self, Error> {
        fn narrow<T: TryFrom<i64>>(value: Option<i64>) -> Result<Option<T>, Error> {
            value
                .map(T::try_from)
                .transpose()
                .map_err(|_| Error::Corrupt("integer out of range"))
        }
        Ok(match kind {
            Kind::Uuid => Self::Uuid(row.try_get(i)?),
            Kind::Bytes => Self::Bytes(row.try_get(i)?),
            Kind::Text => Self::Text(row.try_get(i)?),
            Kind::I16 => Self::I16(narrow(row.try_get(i)?)?),
            Kind::I32 => Self::I32(narrow(row.try_get(i)?)?),
            Kind::I64 => Self::I64(row.try_get(i)?),
            Kind::Bool => Self::Bool(row.try_get(i)?),
        })
    }

    fn postgres(row: &PgRow, i: usize, kind: Kind) -> Result<Self, Error> {
        Ok(match kind {
            Kind::Uuid => Self::Uuid(row.try_get(i)?),
            Kind::Bytes => Self::Bytes(row.try_get(i)?),
            Kind::Text => Self::Text(row.try_get(i)?),
            Kind::I16 => Self::I16(row.try_get(i)?),
            Kind::I32 => Self::I32(row.try_get(i)?),
            Kind::I64 => Self::I64(row.try_get(i)?),
            Kind::Bool => Self::Bool(row.try_get(i)?),
        })
    }

    fn bind(
        self,
        query: Query<'static, Postgres, PgArguments>,
    ) -> Query<'static, Postgres, PgArguments> {
        match self {
            Self::Uuid(v) => query.bind(v),
            Self::Bytes(v) => query.bind(v),
            Self::Text(v) => query.bind(v),
            Self::I16(v) => query.bind(v),
            Self::I32(v) => query.bind(v),
            Self::I64(v) => query.bind(v),
            Self::Bool(v) => query.bind(v),
        }
    }

    /// Rough encoded size, for batching.
    fn size(&self) -> usize {
        match self {
            Self::Bytes(Some(v)) => v.len(),
            Self::Text(Some(v)) => v.len(),
            _ => 8,
        }
    }

    /// Canonical encoding: kind tag, NULL flag, then the value.
    fn hash(&self, hasher: &mut Sha256) {
        fn int(hasher: &mut Sha256, tag: u8, value: Option<i64>) {
            hasher.update([tag, u8::from(value.is_some())]);
            if let Some(value) = value {
                hasher.update(value.to_le_bytes());
            }
        }
        fn bytes(hasher: &mut Sha256, tag: u8, value: Option<&[u8]>) {
            hasher.update([tag, u8::from(value.is_some())]);
            if let Some(value) = value {
                hasher.update((value.len() as u64).to_le_bytes());
                hasher.update(value);
            }
        }
        match self {
            Self::Uuid(v) => bytes(hasher, 0, v.as_ref().map(|v| v.as_bytes().as_slice())),
            Self::Bytes(v) => bytes(hasher, 1, v.as_deref()),
            Self::Text(v) => bytes(hasher, 2, v.as_deref().map(str::as_bytes)),
            Self::I16(v) => int(hasher, 3, v.map(i64::from)),
            Self::I32(v) => int(hasher, 4, v.map(i64::from)),
            Self::I64(v) => int(hasher, 5, *v),
            Self::Bool(v) => int(hasher, 6, v.map(i64::from)),
        }
    }
}

/// A table's row count and the wrapping sum of its row hashes: equal for equal row sets
/// in any order.
#[derive(Default, PartialEq, Eq)]
struct Sum {
    count: u64,
    sum: u128,
}

impl Sum {
    fn add(&mut self, row: &[Val]) {
        let mut hasher = Sha256::new();
        for value in row {
            value.hash(&mut hasher);
        }
        let digest = hasher.finalize();
        let mut head = [0; 16];
        head.copy_from_slice(&digest[..16]);
        self.count += 1;
        self.sum = self.sum.wrapping_add(u128::from_le_bytes(head));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn listed() -> Vec<String> {
        let mut names: Vec<String> = TABLES.iter().map(|(n, _)| (*n).to_owned()).collect();
        names.sort_unstable();
        names
    }

    /// Fails when a migration adds a table the copy does not know.
    #[tokio::test]
    async fn table_list_matches_schema() {
        let path = std::env::temp_dir().join(format!("infinidysk-db-{}.sqlite", ItemId::new()));
        let db = Db::connect(&format!("sqlite:{}", path.display()))
            .await
            .expect("connect");
        let tables: Vec<String> = sqlx::query_scalar(
            "SELECT name FROM sqlite_master WHERE type = 'table' \
             AND name NOT IN ('_sqlx_migrations') AND name NOT LIKE 'sqlite_%' ORDER BY name",
        )
        .fetch_all(db.sqlite().expect("sqlite"))
        .await
        .expect("query");
        assert_eq!(tables, listed());
        db.close().await;
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
        }

        let Ok(admin_url) = std::env::var("INFINIDYSK_TEST_POSTGRES_URL") else {
            return;
        };
        let name = format!("infinidysk_test_{}", ItemId::new().0.simple());
        let mut admin = connect(&admin_url, Duration::from_secs(5))
            .await
            .expect("admin");
        sqlx::query(AssertSqlSafe(format!("CREATE DATABASE {name}")))
            .execute(&mut admin)
            .await
            .expect("create");
        let (base, _) = admin_url.rsplit_once('/').expect("url has a database path");
        let mut conn = connect(&format!("{base}/{name}"), Duration::from_secs(5))
            .await
            .expect("connect");
        migrate(&mut conn).await.expect("migrate");
        let tables: Vec<String> = sqlx::query_scalar(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema = \
             current_schema() AND table_type = 'BASE TABLE' AND table_name <> '_sqlx_migrations' \
             ORDER BY 1",
        )
        .fetch_all(&mut conn)
        .await
        .expect("query");
        assert_eq!(tables, listed());
        for &(table, key) in TABLES {
            let columns: Vec<String> = sqlx::query_scalar(
                "SELECT a.attname::text FROM pg_index i \
                 JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) \
                 WHERE i.indrelid = to_regclass($1) AND i.indisprimary ORDER BY a.attnum",
            )
            .bind(table)
            .fetch_all(&mut conn)
            .await
            .expect("key");
            let mut want: Vec<String> = key.names().into_iter().map(str::to_owned).collect();
            want.sort_unstable();
            let mut columns = columns;
            columns.sort_unstable();
            assert_eq!(columns, want, "{table}");
        }
        drop(conn);
        sqlx::query(AssertSqlSafe(format!("DROP DATABASE {name} WITH (FORCE)")))
            .execute(&mut admin)
            .await
            .expect("drop");
    }
}
