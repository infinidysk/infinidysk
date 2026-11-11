//! The metrics store: always its own SQLite file, whatever backend holds the library, so
//! a lost or corrupt file costs only history.
//!
//! Each write adds one minute of counter deltas to the minute row, the hour row and the
//! lifetime total at once, so pruning old rows never changes a total.

use std::path::{Path, PathBuf};

use infinidysk_core::ItemId;
use sqlx::sqlite::{SqliteConnectOptions, SqliteJournalMode, SqlitePool, SqliteSynchronous};
use tokio::sync::RwLock;
use uuid::Uuid;

use crate::{BUSY_TIMEOUT, Error, SqliteStats};

const MINUTE_MS: i64 = 60_000;
const HOUR_MS: i64 = 60 * MINUTE_MS;
const DAY_MS: i64 = 24 * HOUR_MS;
const KEEP_MINUTES_MS: i64 = 7 * DAY_MS;
const KEEP_HOURS_MS: i64 = 400 * DAY_MS;
const KEEP_SESSIONS_MS: i64 = 90 * DAY_MS;
// Outages draw beside the hour rows in every window, so they last as long.
const KEEP_EVENTS_MS: i64 = KEEP_HOURS_MS;

const UPSERT_COUNTER: &str = "INSERT INTO counters (grain, ts, key, value) VALUES ($1, $2, $3, $4) \
    ON CONFLICT (grain, ts, key) DO UPDATE SET value = CASE WHEN excluded.key LIKE 'peak.%' \
    THEN MAX(counters.value, excluded.value) ELSE counters.value + excluded.value END";
const UPSERT_LIFETIME: &str = "INSERT INTO lifetime (key, value) VALUES ($1, $2) \
    ON CONFLICT (key) DO UPDATE SET value = CASE WHEN excluded.key LIKE 'peak.%' \
    THEN MAX(lifetime.value, excluded.value) ELSE lifetime.value + excluded.value END";

/// Row resolution: minutes are kept 7 days, hours about 13 months.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i64)]
pub enum Grain {
    Minute = 0,
    Hour = 1,
}

/// One finished read.
#[derive(Clone, Debug, PartialEq, Eq, sqlx::FromRow)]
pub struct SessionRecord {
    /// `None` for reads stored before items were recorded.
    #[sqlx(skip)]
    pub item: Option<ItemId>,
    /// Unix milliseconds.
    pub opened_at: i64,
    pub closed_at: i64,
    pub path: String,
    pub delivery: String,
    pub client: String,
    pub size: i64,
    pub served: i64,
    pub first_byte_ms: Option<i64>,
    pub stalls: i64,
    pub stall_ms: i64,
    pub seeks: i64,
    /// `eof` when read to the end, else `closed`.
    pub end_reason: String,
}

#[derive(sqlx::FromRow)]
struct SessionRow {
    #[sqlx(flatten)]
    record: SessionRecord,
    item_id: Option<Uuid>,
}

impl SessionRow {
    fn into_record(self) -> SessionRecord {
        SessionRecord {
            item: self.item_id.map(ItemId),
            ..self.record
        }
    }
}

/// Reads of one item summed by [`Metrics::top_items`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ItemReads {
    pub item: ItemId,
    /// Path of the newest read.
    pub path: String,
    pub reads: u64,
    pub served: u64,
    pub last_closed_at: i64,
}

/// Reads by one program summed by [`Metrics::usage`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Usage {
    pub delivery: String,
    /// Empty when the reading process was hidden.
    pub program: String,
    pub reads: u64,
    pub served: u64,
    pub open_ms: u64,
}

/// A provider's circuit breaker opening.
#[derive(Clone, Debug, PartialEq, Eq, sqlx::FromRow)]
pub struct BreakerEvent {
    pub at: i64,
    /// `None` on events stored before keys were.
    pub provider_key: Option<String>,
    /// Display name when it opened.
    pub provider: String,
    /// How long it stayed open.
    pub open_ms: i64,
}

pub struct Metrics {
    pool: SqlitePool,
    recreated: bool,
    /// Writes share it; [`Metrics::compact`] holds it alone.
    writes: RwLock<()>,
}

impl Metrics {
    /// Opens or creates the store at `path`. A corrupt file is moved aside to
    /// `<path>.unreadable` (replacing an older one) and a new store started; a store
    /// this build cannot migrate is left alone and returned as an error.
    pub async fn open(path: &Path) -> Result<Self, Error> {
        match connect(path).await {
            Ok(pool) => {
                return Ok(Self {
                    pool,
                    recreated: false,
                    writes: RwLock::default(),
                });
            }
            // Anything else (permissions, a full disk, a newer schema) would fail again
            // on a new file or lose history a newer build can still read.
            Err(error) if !unreadable(&error) => return Err(error),
            Err(_) => {}
        }
        let mut aside = path.as_os_str().to_owned();
        aside.push(".unreadable");
        for (from, to) in files(path).iter().zip(files(Path::new(&aside))) {
            let moved = match std::fs::rename(from, &to) {
                // No such companion: drop the older quarantine's so the pair stays whole.
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => std::fs::remove_file(&to),
                moved => moved,
            };
            match moved {
                Err(e) if e.kind() != std::io::ErrorKind::NotFound => {
                    return Err(Error::Sqlx(sqlx::Error::Io(e)));
                }
                _ => {}
            }
        }
        Ok(Self {
            pool: connect(path).await?,
            recreated: true,
            writes: RwLock::default(),
        })
    }

    /// Whether [`open`](Self::open) found the file corrupt and started over.
    pub const fn recreated(&self) -> bool {
        self.recreated
    }

    pub async fn close(&self) {
        self.pool.close().await;
    }

    /// See [`crate::Db::optimize`].
    pub async fn optimize(&self) -> Result<(), Error> {
        let _writing = self.writes.read().await;
        crate::sqlite_optimize(&self.pool).await
    }

    pub async fn stats(&self) -> Result<SqliteStats, Error> {
        crate::sqlite_stats(&self.pool).await
    }

    /// See [`crate::Db::compact`]; this store's own writes wait for it.
    pub async fn compact(&self) -> Result<(), Error> {
        let _paused = self.writes.write().await;
        crate::sqlite_compact(&self.pool).await
    }

    /// Adds one minute of counter deltas (zeros skipped), finished reads and outages. An
    /// outage written again with the same key and start keeps the longer length.
    pub async fn write(
        &self,
        minute_ms: i64,
        counters: &[(String, u64)],
        sessions: &[SessionRecord],
        events: &[BreakerEvent],
    ) -> Result<(), Error> {
        let minute = minute_ms - minute_ms.rem_euclid(MINUTE_MS);
        let hour = minute_ms - minute_ms.rem_euclid(HOUR_MS);
        let _writing = self.writes.read().await;
        let mut tx = self.pool.begin().await?;
        let mut fetched = 0_i64;
        for (key, value) in counters.iter().filter(|(_, v)| *v > 0) {
            let value = i64::try_from(*value).unwrap_or(i64::MAX);
            if key.starts_with("p.") && key.rsplit('.').next() == Some("bytes") {
                fetched = fetched.saturating_add(value);
            }
            for (grain, ts) in [(Grain::Minute, minute), (Grain::Hour, hour)] {
                sqlx::query(UPSERT_COUNTER)
                    .bind(grain as i64)
                    .bind(ts)
                    .bind(key)
                    .bind(value)
                    .execute(&mut *tx)
                    .await?;
            }
            sqlx::query(UPSERT_LIFETIME)
                .bind(key)
                .bind(value)
                .execute(&mut *tx)
                .await?;
        }
        if fetched > 0 {
            sqlx::query(
                "INSERT INTO hourly_bytes (ts, bytes) VALUES ($1, $2) \
                 ON CONFLICT (ts) DO UPDATE SET bytes = bytes + excluded.bytes",
            )
            .bind(hour)
            .bind(fetched)
            .execute(&mut *tx)
            .await?;
        }
        for s in sessions {
            sqlx::query(
                "INSERT INTO sessions (opened_at, closed_at, path, delivery, client, size, \
                 served, first_byte_ms, stalls, stall_ms, seeks, end_reason, item_id) \
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)",
            )
            .bind(s.opened_at)
            .bind(s.closed_at)
            .bind(&s.path)
            .bind(&s.delivery)
            .bind(&s.client)
            .bind(s.size)
            .bind(s.served)
            .bind(s.first_byte_ms)
            .bind(s.stalls)
            .bind(s.stall_ms)
            .bind(s.seeks)
            .bind(&s.end_reason)
            .bind(s.item.map(|i| i.0))
            .execute(&mut *tx)
            .await?;
        }
        for e in events {
            sqlx::query(
                "INSERT INTO breaker_events (at, provider_key, provider, open_ms) \
                 VALUES ($1, $2, $3, $4) \
                 ON CONFLICT (provider_key, at) DO UPDATE SET provider = excluded.provider, \
                 open_ms = MAX(open_ms, excluded.open_ms)",
            )
            .bind(e.at)
            .bind(&e.provider_key)
            .bind(&e.provider)
            .bind(e.open_ms)
            .execute(&mut *tx)
            .await?;
        }
        tx.commit().await?;
        Ok(())
    }

    /// Drops minute rows after 7 days, hour rows and breaker events after about 13
    /// months and reads after 90 days. Lifetime totals stay.
    pub async fn prune(&self, now_ms: i64) -> Result<(), Error> {
        let _writing = self.writes.read().await;
        let mut tx = self.pool.begin().await?;
        for (grain, keep) in [
            (Grain::Minute, KEEP_MINUTES_MS),
            (Grain::Hour, KEEP_HOURS_MS),
        ] {
            sqlx::query("DELETE FROM counters WHERE grain = $1 AND ts < $2")
                .bind(grain as i64)
                .bind(now_ms - keep)
                .execute(&mut *tx)
                .await?;
        }
        sqlx::query("DELETE FROM hourly_bytes WHERE ts < $1")
            .bind(now_ms - KEEP_HOURS_MS)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM sessions WHERE closed_at < $1")
            .bind(now_ms - KEEP_SESSIONS_MS)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM breaker_events WHERE at < $1")
            .bind(now_ms - KEEP_EVENTS_MS)
            .execute(&mut *tx)
            .await?;
        tx.commit().await?;
        Ok(())
    }

    /// `(bucket start, key, value)` from `grain` rows since `since_ms`, merged into
    /// buckets of `bucket_ms`, in time order.
    pub async fn series(
        &self,
        grain: Grain,
        since_ms: i64,
        bucket_ms: i64,
    ) -> Result<Vec<(i64, String, u64)>, Error> {
        let rows: Vec<(i64, String, i64)> = sqlx::query_as(
            "SELECT (ts / $3) * $3 AS bucket, key, \
             CASE WHEN key LIKE 'peak.%' THEN MAX(value) ELSE SUM(value) END \
             FROM counters WHERE grain = $1 AND ts >= $2 GROUP BY bucket, key ORDER BY bucket",
        )
        .bind(grain as i64)
        .bind(since_ms)
        .bind(bucket_ms.max(1))
        .fetch_all(&self.pool)
        .await?;
        Ok(rows
            .into_iter()
            .map(|(ts, key, value)| (ts, key, value.cast_unsigned()))
            .collect())
    }

    /// Totals since the store was created.
    pub async fn lifetime(&self) -> Result<Vec<(String, u64)>, Error> {
        let rows: Vec<(String, i64)> = sqlx::query_as("SELECT key, value FROM lifetime")
            .fetch_all(&self.pool)
            .await?;
        Ok(rows
            .into_iter()
            .map(|(key, value)| (key, value.cast_unsigned()))
            .collect())
    }

    /// The newest finished reads.
    pub async fn sessions(&self, limit: u32) -> Result<Vec<SessionRecord>, Error> {
        let rows: Vec<SessionRow> = sqlx::query_as(
            "SELECT opened_at, closed_at, path, delivery, client, size, served, first_byte_ms, \
             stalls, stall_ms, seeks, end_reason, item_id FROM sessions \
             ORDER BY closed_at DESC LIMIT $1",
        )
        .bind(i64::from(limit))
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(SessionRow::into_record).collect())
    }

    /// The newest finished reads of one item.
    pub async fn item_sessions(
        &self,
        item: ItemId,
        limit: u32,
    ) -> Result<Vec<SessionRecord>, Error> {
        let rows: Vec<SessionRow> = sqlx::query_as(
            "SELECT opened_at, closed_at, path, delivery, client, size, served, first_byte_ms, \
             stalls, stall_ms, seeks, end_reason, item_id FROM sessions \
             WHERE item_id = $1 ORDER BY closed_at DESC LIMIT $2",
        )
        .bind(item.0)
        .bind(i64::from(limit))
        .fetch_all(&self.pool)
        .await?;
        Ok(rows.into_iter().map(SessionRow::into_record).collect())
    }

    /// Items with the most bytes served by reads closed since `since_ms`, most first.
    pub async fn top_items(&self, since_ms: i64, limit: u32) -> Result<Vec<ItemReads>, Error> {
        // With one MAX() in the query, SQLite takes the bare `path` from the newest read.
        let rows: Vec<(Uuid, String, i64, i64, i64)> = sqlx::query_as(
            "SELECT item_id, path, COUNT(*), SUM(served), MAX(closed_at) FROM sessions \
             WHERE item_id IS NOT NULL AND closed_at >= $1 \
             GROUP BY item_id ORDER BY SUM(served) DESC LIMIT $2",
        )
        .bind(since_ms)
        .bind(i64::from(limit))
        .fetch_all(&self.pool)
        .await?;
        Ok(rows
            .into_iter()
            .map(|(item, path, reads, served, last)| ItemReads {
                item: ItemId(item),
                path,
                reads: reads.cast_unsigned(),
                served: served.cast_unsigned(),
                last_closed_at: last,
            })
            .collect())
    }

    /// Reads closed since `since_ms` by delivery and program: the FUSE process name or
    /// the `WebDAV` user agent, without pids and addresses. Most bytes first.
    pub async fn usage(&self, since_ms: i64) -> Result<Vec<Usage>, Error> {
        let rows: Vec<(String, String, i64, i64, i64)> = sqlx::query_as(
            "SELECT delivery, CASE \
               WHEN client LIKE 'pid %' THEN '' \
               WHEN instr(client, ' (pid ') > 0 THEN substr(client, 1, instr(client, ' (pid ') - 1) \
               WHEN instr(client, ' · ') > 0 THEN substr(client, 1, instr(client, ' · ') - 1) \
               ELSE client END AS program, \
             COUNT(*), SUM(served), SUM(MAX(closed_at - opened_at, 0)) FROM sessions \
             WHERE closed_at >= $1 GROUP BY delivery, program ORDER BY SUM(served) DESC",
        )
        .bind(since_ms)
        .fetch_all(&self.pool)
        .await?;
        Ok(rows
            .into_iter()
            .map(|(delivery, program, reads, served, open_ms)| Usage {
                delivery,
                program,
                reads: reads.cast_unsigned(),
                served: served.cast_unsigned(),
                open_ms: open_ms.cast_unsigned(),
            })
            .collect())
    }

    /// Breaker openings still open at or after `since_ms`, newest first.
    pub async fn breaker_events(&self, since_ms: i64) -> Result<Vec<BreakerEvent>, Error> {
        Ok(sqlx::query_as(
            "SELECT at, provider_key, provider, open_ms FROM breaker_events \
             WHERE at + open_ms >= $1 ORDER BY at DESC",
        )
        .bind(since_ms)
        .fetch_all(&self.pool)
        .await?)
    }

    /// Sum of counters starting with `prefix` in `grain` rows from `from_ms` up to `to_ms`.
    pub async fn sum(
        &self,
        grain: Grain,
        from_ms: i64,
        to_ms: i64,
        prefix: &str,
    ) -> Result<u64, Error> {
        let (sum,): (i64,) = sqlx::query_as(
            "SELECT COALESCE(SUM(value), 0) FROM counters \
             WHERE grain = $1 AND ts >= $2 AND ts < $3 AND substr(key, 1, length($4)) = $4",
        )
        .bind(grain as i64)
        .bind(from_ms)
        .bind(to_ms)
        .bind(prefix)
        .fetch_one(&self.pool)
        .await?;
        Ok(sum.cast_unsigned())
    }

    /// The kept hour and UTC day with the most bytes from providers, and the first hour
    /// kept.
    pub async fn records(&self) -> Result<Records, Error> {
        let hour: Option<(i64, i64)> =
            sqlx::query_as("SELECT ts, bytes FROM hourly_bytes ORDER BY bytes DESC LIMIT 1")
                .fetch_optional(&self.pool)
                .await?;
        let day: Option<(i64, i64)> = sqlx::query_as(
            "SELECT (ts / 86400000) * 86400000 AS day, SUM(bytes) AS n FROM hourly_bytes \
             GROUP BY day ORDER BY n DESC LIMIT 1",
        )
        .fetch_optional(&self.pool)
        .await?;
        let (first,): (Option<i64>,) =
            sqlx::query_as("SELECT MIN(ts) FROM counters WHERE grain = 1")
                .fetch_one(&self.pool)
                .await?;
        let pair = |r: Option<(i64, i64)>| r.map(|(ts, n)| (ts, n.cast_unsigned()));
        Ok(Records {
            busiest_hour: pair(hour),
            busiest_day: pair(day),
            first_hour: first,
        })
    }
}

/// `(start, bytes)` pairs, Unix milliseconds.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Records {
    pub busiest_hour: Option<(i64, u64)>,
    pub busiest_day: Option<(i64, u64)>,
    pub first_hour: Option<i64>,
}

async fn connect(path: &Path) -> Result<SqlitePool, Error> {
    let options = SqliteConnectOptions::new()
        .filename(path)
        .create_if_missing(true)
        .journal_mode(SqliteJournalMode::Wal)
        .synchronous(SqliteSynchronous::Normal)
        .busy_timeout(BUSY_TIMEOUT);
    let pool = SqlitePool::connect_with(options).await?;
    let ready = async {
        let check: (String,) = sqlx::query_as("PRAGMA quick_check")
            .fetch_one(&pool)
            .await?;
        if check.0 != "ok" {
            return Err(Error::Corrupt("metrics store failed its integrity check"));
        }
        sqlx::migrate!("./migrations/metrics").run(&pool).await?;
        Ok(())
    }
    .await;
    // Closed before the caller moves the files.
    if let Err(error) = ready {
        pool.close().await;
        return Err(error);
    }
    Ok(pool)
}

/// Whether `error` means the file is corrupt, rather than unreachable or written by a
/// build with other migrations.
fn unreadable(error: &Error) -> bool {
    use sqlx::migrate::MigrateError;
    let sqlx = match error {
        Error::Corrupt(_) => return true,
        Error::Sqlx(e)
        | Error::Migrate(MigrateError::Execute(e) | MigrateError::ExecuteMigration(e, _)) => e,
        _ => return false,
    };
    // SQLITE_CORRUPT (and its extended codes), SQLITE_NOTADB.
    matches!(sqlx, sqlx::Error::Database(e) if e
        .code()
        .and_then(|code| code.parse::<i32>().ok())
        .is_some_and(|code| matches!(code & 0xff, 11 | 26)))
}

fn files(path: &Path) -> [PathBuf; 3] {
    let with = |suffix: &str| {
        let mut name = path.as_os_str().to_owned();
        name.push(suffix);
        PathBuf::from(name)
    };
    [path.to_path_buf(), with("-wal"), with("-shm")]
}
