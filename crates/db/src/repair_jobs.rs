//! Durable, NZB-scoped work. One application owns claims; startup resumes interrupted jobs.

use infinidysk_core::{BlobId, ItemId, JobId};
use uuid::Uuid;

use crate::{
    DELETE_UNUSED_BLOB, Db, Error, Ordering, Pool, RELEASE_NZB, now_ms, on, release_nzb,
    repair::Owner,
};

/// 0 queued, 1 running, 2 verified, 3 repaired, 4 deferred, 5 unavailable, 6 cancelled.
#[derive(Debug, sqlx::FromRow)]
pub struct Job {
    pub id: Uuid,
    pub nzb_blob: Option<Uuid>,
    pub item_id: Option<Uuid>,
    pub meta_blob: Option<Uuid>,
    pub meta_generation: Option<i64>,
    pub generation: i64,
    pub verify_only: bool,
    pub state: i16,
    pub attempts: i32,
    pub reason: String,
    pub transferred_bytes: i64,
    pub stored_bytes: i64,
    pub updated_at: i64,
    pub duration_ms: i64,
    pub verified_bytes: i64,
    pub started_at: Option<i64>,
    pub next_at: i64,
    pub scan_at: Option<i64>,
    pub repair_on_failure: bool,
    pub checked_articles: i64,
    pub total_articles: i64,
    pub completed_requests: i64,
    pub missing_requests: i64,
    #[sqlx(default)]
    pub file_path: Option<String>,
}

macro_rules! columns {
    () => { "id, nzb_blob, item_id, meta_blob, meta_generation, generation, verify_only, state, attempts, reason, transferred_bytes, stored_bytes, updated_at, duration_ms, verified_bytes, started_at, next_at, scan_at, repair_on_failure, checked_articles, total_articles, completed_requests, missing_requests" };
}

impl Db {
    pub async fn update_par2_requests(
        &self,
        id: Uuid,
        completed: i64,
        missing: i64,
    ) -> Result<(), Error> {
        if missing < 0 || completed < missing {
            return Err(Error::RepairBounds("request progress"));
        }
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET completed_requests = $2, missing_requests = $3 WHERE id = $1 AND state IN (1, 6)")
            .bind(id).bind(completed).bind(missing).execute(pool).await?.rows_affected());
        Ok(())
    }

    /// Playback preemption does not consume a provider/storage retry.
    pub async fn yield_par2_attempt(&self, id: Uuid, claimed_attempt: i32) -> Result<(), Error> {
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET attempts = attempts - 1 WHERE id = $1 AND state = 1 AND attempts = $2 AND attempts > 0")
            .bind(id).bind(claimed_attempt).execute(pool).await?.rows_affected());
        Ok(())
    }

    /// Release the availability slot and queue its repair in the same commit, even at capacity.
    pub async fn escalate_par2(&self, id: Uuid, now: i64) -> Result<bool, Error> {
        let _serial = self.searches.lock().await;
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let source: Option<(Uuid, i64, Option<i64>)> = sqlx::query_as(
                "UPDATE repair_jobs SET state = 5, reason = 'Missing or corrupt articles found; PAR2 repair queued separately.', updated_at = $2, duration_ms = duration_ms + CASE WHEN started_at IS NULL OR started_at > $2 THEN 0 ELSE $2 - started_at END, started_at = NULL WHERE id = $1 AND state = 1 AND verify_only = TRUE AND EXISTS (SELECT 1 FROM blobs b WHERE b.id = repair_jobs.nzb_blob AND b.generation = repair_jobs.generation) RETURNING nzb_blob, generation, scan_at")
                .bind(id).bind(now).fetch_optional(&mut *tx).await?;
            let Some((nzb, generation, scan_at)) = source else { return Ok(false) };
            let existing: Option<Uuid> = sqlx::query_scalar("SELECT id FROM repair_jobs WHERE nzb_blob = $1 AND generation = $2 AND verify_only = FALSE")
                .bind(nzb).bind(generation).fetch_optional(&mut *tx).await?;
            if let Some(existing) = existing {
                sqlx::query("UPDATE repair_jobs SET state = 0, attempts = 0, reason = '', next_at = $2, updated_at = $2, started_at = NULL, duration_ms = 0, transferred_bytes = 0, stored_bytes = 0, verified_bytes = 0, completed_requests = 0, missing_requests = 0, scan_at = $3 WHERE id = $1 AND state IN (2, 3, 5, 6)")
                    .bind(existing).bind(now).bind(scan_at).execute(&mut *tx).await?;
            } else {
                sqlx::query("INSERT INTO repair_jobs (id, nzb_blob, generation, verify_only, next_at, updated_at, scan_at) VALUES ($1, $2, $3, FALSE, $4, $4, $5)")
                    .bind(Uuid::new_v4()).bind(nzb).bind(generation).bind(now).bind(scan_at).execute(&mut *tx).await?;
            }
            tx.commit().await?;
            Ok(true)
        })
    }

    pub async fn update_availability_progress(
        &self,
        id: Uuid,
        checked: i64,
        total: i64,
    ) -> Result<(), Error> {
        if checked < 0 || total < checked {
            return Err(Error::RepairBounds("article progress"));
        }
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET checked_articles = $2, total_articles = $3 WHERE id = $1 AND state IN (1, 6)")
            .bind(id).bind(checked).bind(total).execute(pool).await?.rows_affected());
        Ok(())
    }

    /// Independent availability/repair lanes; every fourth claim can prefer scan work.
    pub async fn claim_par2_lane(
        &self,
        now: i64,
        verify: bool,
        prefer_scan: bool,
    ) -> Result<Option<Job>, Error> {
        Ok(on!(self, pool => sqlx::query_as(concat!(
            "UPDATE repair_jobs SET state = 1, attempts = attempts + 1, updated_at = $1, started_at = $1 WHERE id = ",
            "(SELECT id FROM repair_jobs WHERE state IN (0, 4) AND next_at <= $1 AND verify_only = $2 ",
            "ORDER BY CASE WHEN (scan_at IS NOT NULL) = $3 THEN 0 ELSE 1 END, next_at, id LIMIT 1) ",
            "AND state IN (0, 4) RETURNING ", columns!()))
            .bind(now).bind(verify).bind(prefer_scan).fetch_optional(pool).await?))
    }

    /// Tag only a new request or an explicit scan retry; pending manual work keeps its priority.
    pub async fn request_par2_scan(
        &self,
        item: ItemId,
        now: i64,
        scan_at: i64,
        repair: bool,
    ) -> Result<Option<Uuid>, Error> {
        self.request_verification(item, now, Some(scan_at), repair)
            .await
    }

    /// All pending work, followed by the twenty most recent results.
    pub async fn par2_jobs(&self) -> Result<Vec<Job>, Error> {
        Ok(
            on!(self, pool => sqlx::query_as(concat!("SELECT ", columns!(),
            ", (SELECT path FROM items WHERE id = repair_jobs.item_id OR (repair_jobs.item_id IS NULL AND nzb_blob = repair_jobs.nzb_blob AND kind <> 1) ORDER BY id LIMIT 1) AS file_path FROM repair_jobs WHERE state IN (0, 1, 4) OR id IN (SELECT id FROM repair_jobs WHERE state NOT IN (0, 1, 4) ORDER BY updated_at DESC, id LIMIT 20) ORDER BY CASE WHEN state = 1 THEN 0 WHEN state IN (0, 4) THEN 1 ELSE 2 END, updated_at DESC, id"))
            .fetch_all(pool).await?),
        )
    }

    pub async fn par2_counts(&self) -> Result<Vec<(i16, i64, i64, i64)>, Error> {
        Ok(
            on!(self, pool => sqlx::query_as("SELECT state, COUNT(*), SUM(CASE WHEN verify_only = TRUE THEN 1 ELSE 0 END), SUM(CASE WHEN verify_only = TRUE AND state = 5 AND reason LIKE 'Missing or corrupt articles found%' THEN 1 ELSE 0 END) FROM repair_jobs GROUP BY state ORDER BY state")
            .fetch_all(pool).await?),
        )
    }

    /// Persist live progress without overwriting a cancellation or completed result.
    pub async fn update_par2_progress(
        &self,
        id: Uuid,
        phase: &str,
        transferred: i64,
        stored: i64,
        now: i64,
    ) -> Result<(), Error> {
        if transferred < 0 || stored < 0 || phase.len() > 128 {
            return Err(Error::RepairBounds("job progress"));
        }
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET reason = $2, transferred_bytes = $3, stored_bytes = $4, updated_at = $5 WHERE id = $1 AND state = 1")
            .bind(id).bind(phase).bind(transferred).bind(stored).bind(now).execute(pool).await?.rows_affected());
        Ok(())
    }

    pub async fn par2_scan_total(&self, boundary: ItemId, before: i64) -> Result<i64, Error> {
        Ok(
            on!(self, pool => sqlx::query_scalar("SELECT COUNT(*) FROM items WHERE kind <> 1 AND meta_blob IS NOT NULL AND id <= $1 AND created_at <= $2")
            .bind(boundary.0).bind(before).fetch_one(pool).await?),
        )
    }

    pub async fn set_par2_verified(&self, id: Uuid, bytes: i64) -> Result<(), Error> {
        if bytes < 0 {
            return Err(Error::RepairBounds("verified byte count"));
        }
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET verified_bytes = $2 WHERE id = $1 AND state IN (1, 6)")
            .bind(id).bind(bytes).execute(pool).await?.rows_affected());
        Ok(())
    }
    pub async fn advance_par2_scan(&self, old: &str, new: &str) -> Result<bool, Error> {
        Ok(
            on!(self, pool => sqlx::query("UPDATE config SET value = $2 WHERE key = 'par2.scan' AND value = $1")
            .bind(old).bind(new).execute(pool).await?.rows_affected())
                > 0,
        )
    }

    pub async fn cancel_par2(&self, id: Uuid, now: i64) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let source: Option<Option<Uuid>> = sqlx::query_scalar("UPDATE repair_jobs SET state = 6, reason = 'cancelled', updated_at = $2 WHERE id = $1 AND state IN (0, 1, 4) RETURNING nzb_blob")
                .bind(id).bind(now).fetch_optional(&mut *tx).await?;
            if let Some(Some(source)) = source { release_nzb!(self, tx, source); }
            tx.commit().await?;
        });
        Ok(())
    }

    pub async fn par2_cancelled(&self, id: Uuid) -> Result<bool, Error> {
        let state: Option<i16> = on!(self, pool => sqlx::query_scalar("SELECT state FROM repair_jobs WHERE id = $1")
            .bind(id).fetch_optional(pool).await?);
        Ok(state.is_none_or(|state| state == 6))
    }
    pub async fn par2_scan_boundary(&self) -> Result<Option<ItemId>, Error> {
        let id: Option<Uuid> = on!(self, pool => sqlx::query_scalar("SELECT id FROM items WHERE kind <> 1 AND meta_blob IS NOT NULL ORDER BY id DESC LIMIT 1")
            .fetch_optional(pool).await?);
        Ok(id.map(ItemId))
    }

    pub async fn par2_scan_page(
        &self,
        after: Option<ItemId>,
        boundary: ItemId,
        before: i64,
    ) -> Result<Vec<ItemId>, Error> {
        let ids: Vec<Uuid> = on!(self, pool => sqlx::query_scalar("SELECT id FROM items WHERE kind <> 1 AND meta_blob IS NOT NULL AND (id > $1 OR $1 IS NULL) AND id <= $2 AND created_at <= $3 ORDER BY id LIMIT 16")
            .bind(after.map(|id| id.0)).bind(boundary.0).bind(before).fetch_all(pool).await?);
        Ok(ids.into_iter().map(ItemId).collect())
    }

    pub async fn par2_pending(&self) -> Result<i64, Error> {
        Ok(
            on!(self, pool => sqlx::query_scalar("SELECT COUNT(*) FROM repair_jobs WHERE state IN (0, 1, 4)").fetch_one(pool).await?),
        )
    }
    /// Explicit retries may rerun a completed request; automatic requests stay idempotent.
    pub async fn restart_par2(&self, id: Uuid, now: i64) -> Result<(), Error> {
        self.restart_par2_scan(id, now, None, false).await
    }

    pub async fn restart_par2_scan(
        &self,
        id: Uuid,
        now: i64,
        scan_at: Option<i64>,
        repair: bool,
    ) -> Result<(), Error> {
        let _serial = self.searches.lock().await;
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let state: Option<i16> = sqlx::query_scalar("SELECT state FROM repair_jobs WHERE id = $1")
                .bind(id).fetch_optional(&mut *tx).await?;
            if scan_at.is_none() && state.is_some_and(|state| matches!(state, 0 | 4)) {
                sqlx::query("UPDATE repair_jobs SET scan_at = NULL, next_at = $2 WHERE id = $1 AND state IN (0, 4)")
                    .bind(id).bind(now).execute(&mut *tx).await?;
                tx.commit().await?;
                return Ok(());
            }
            if !state.is_some_and(|s| matches!(s, 2 | 3 | 5 | 6)) { return Ok(()) }
            let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM repair_jobs WHERE state IN (0, 1, 4)")
                .fetch_one(&mut *tx).await?;
            if count >= 128 { return Err(Error::RepairBounds("pending job count")); }
            sqlx::query("UPDATE repair_jobs SET state = 0, attempts = 0, reason = '', next_at = $2, updated_at = $2, verified_bytes = 0, duration_ms = 0, transferred_bytes = 0, stored_bytes = 0, started_at = NULL, scan_at = $3, repair_on_failure = $4, checked_articles = 0, total_articles = 0, completed_requests = 0, missing_requests = 0 WHERE id = $1 AND state IN (2, 3, 5, 6)")
                .bind(id).bind(now).bind(scan_at).bind(repair).execute(&mut *tx).await?;
            tx.commit().await?;
            Ok(())
        })
    }
    pub async fn request_arr_replacement(&self, id: ItemId) -> Result<(), Error> {
        on!(self, pool => sqlx::query("UPDATE items SET arr_replacement_requested = TRUE WHERE id = $1")
            .bind(id.0).execute(pool).await?.rows_affected());
        Ok(())
    }

    pub async fn par2_before_arr(&self, id: ItemId) -> Result<bool, Error> {
        Ok(on!(self, pool => sqlx::query_scalar("SELECT NOT arr_replacement_requested FROM items WHERE id = $1")
            .bind(id.0).fetch_optional(pool).await?).unwrap_or(false))
    }
    /// Recheck availability through committed repairs and retry parked imports.
    pub async fn wake_par2(&self, nzb: BlobId, now: i64) -> Result<(), Error> {
        on!(self, pool => sqlx::query("UPDATE items SET next_health_check = $2 WHERE nzb_blob = $1 AND kind <> 1")
            .bind(nzb.0).bind(now).execute(pool).await?.rows_affected());
        on!(self, pool => sqlx::query("UPDATE queue SET retry_at = $2 WHERE nzb_blob = $1")
            .bind(nzb.0).bind(now).execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Acknowledgement follows COMMIT. Repeated requests share completed results too.
    pub async fn request_par2(
        &self,
        owner: Owner,
        verify: bool,
        now: i64,
    ) -> Result<Option<Uuid>, Error> {
        if verify {
            let Owner::Item(item) = owner else {
                return Ok(None);
            };
            return self.request_verification(item, now, None, false).await;
        }
        let Some(source) = self.repair_source(owner).await? else {
            return Ok(None);
        };
        let _serial = self.searches.lock().await;
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM repair_jobs WHERE state IN (0, 1, 4)")
                .fetch_one(&mut *tx).await?;
            let existing: Option<Uuid> = sqlx::query_scalar(
                "SELECT id FROM repair_jobs WHERE nzb_blob = $1 AND generation = $2 AND verify_only = $3")
                .bind(source.nzb().0).bind(source.generation()).bind(verify).fetch_optional(&mut *tx).await?;
            if let Some(id) = existing { return Ok(Some(id)); }
            if count >= 128 { return Err(Error::RepairBounds("pending job count")); }
            let id = Uuid::new_v4();
            sqlx::query("INSERT INTO repair_jobs (id, nzb_blob, generation, verify_only, next_at, updated_at) VALUES ($1, $2, $3, $4, $5, $5)")
                .bind(id).bind(source.nzb().0).bind(source.generation()).bind(verify).bind(now)
                .execute(&mut *tx).await?;
            tx.commit().await?;
            Ok(Some(id))
        })
    }

    pub async fn claim_par2(&self, now: i64) -> Result<Option<Job>, Error> {
        // Atomic UPDATE also prevents duplicate claims from concurrent async callers.
        Ok(on!(self, pool => sqlx::query_as(concat!(
            "UPDATE repair_jobs SET state = 1, attempts = attempts + 1, updated_at = $1, started_at = $1 WHERE id = ",
            "(SELECT id FROM repair_jobs WHERE state IN (0, 4) AND next_at <= $1 ORDER BY next_at, id LIMIT 1) ",
            "AND state IN (0, 4) RETURNING ", columns!()))
            .bind(now).fetch_optional(pool).await?))
    }

    /// Call only at application startup, before launching the single worker.
    pub async fn resume_par2(&self, now: i64) -> Result<(), Error> {
        on!(self, pool => sqlx::query("UPDATE repair_jobs SET state = 4, next_at = $1, reason = 'interrupted; retrying' WHERE state = 1")
            .bind(now).execute(pool).await?.rows_affected());
        Ok(())
    }

    #[allow(clippy::too_many_arguments)] // One persisted result, including its measured byte counters.
    pub async fn finish_par2(
        &self,
        id: Uuid,
        state: i16,
        reason: &str,
        transferred: i64,
        stored: i64,
        now: i64,
        retry: Option<i64>,
    ) -> Result<(), Error> {
        if !(2..=6).contains(&state)
            || (state == 4) != retry.is_some()
            || transferred < 0
            || stored < 0
        {
            return Err(Error::RepairBounds("job result"));
        }
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let result: Option<(i16, Option<Uuid>)> = sqlx::query_as("UPDATE repair_jobs SET state = CASE WHEN state = 6 THEN 6 ELSE $2 END, reason = CASE WHEN state = 6 THEN reason ELSE $3 END, transferred_bytes = $4, stored_bytes = $5, updated_at = $6, next_at = $7, duration_ms = duration_ms + CASE WHEN started_at IS NULL OR started_at > $6 THEN 0 ELSE $6 - started_at END, started_at = NULL WHERE id = $1 AND state IN (1, 6) RETURNING state, nzb_blob")
                .bind(id).bind(state).bind(reason).bind(transferred).bind(stored).bind(now).bind(retry.unwrap_or(now))
                .fetch_optional(&mut *tx).await?;
            if let Some((state, Some(source))) = result && state != 4 { release_nzb!(self, tx, source); }
            tx.commit().await?;
        });
        self.notify(crate::Change::Health);
        Ok(())
    }

    pub async fn par2_result(&self, owner: Owner, verify: bool) -> Result<Option<Job>, Error> {
        if verify {
            let Owner::Item(item) = owner else {
                return Ok(None);
            };
            return Ok(
                on!(self, pool => sqlx::query_as(concat!("SELECT ", columns!(),
                " FROM repair_jobs WHERE id IN (SELECT j.id FROM repair_jobs j JOIN items i ON i.id = j.item_id ",
                "JOIN blobs m ON m.id = i.meta_blob LEFT JOIN blobs b ON b.id = i.nzb_blob ",
                "WHERE i.id = $1 AND j.verify_only = TRUE AND j.meta_blob = m.id AND j.meta_generation = m.generation ",
                "AND j.generation = COALESCE(b.generation, 0) AND (j.nzb_blob = i.nzb_blob OR (j.nzb_blob IS NULL AND i.nzb_blob IS NULL)))"))
                .bind(item.0).fetch_optional(pool).await?),
            );
        }
        let Some(source) = self.repair_source(owner).await? else {
            return Ok(None);
        };
        Ok(
            on!(self, pool => sqlx::query_as(concat!("SELECT ", columns!(),
            " FROM repair_jobs WHERE nzb_blob = $1 AND generation = $2 AND verify_only = $3"))
            .bind(source.nzb().0).bind(source.generation()).bind(verify).fetch_optional(pool).await?),
        )
    }

    pub async fn par2_owner(&self, nzb: BlobId) -> Result<Option<Owner>, Error> {
        let item: Option<Uuid> = on!(self, pool => sqlx::query_scalar("SELECT id FROM items WHERE nzb_blob = $1 AND kind <> 1 ORDER BY id LIMIT 1")
            .bind(nzb.0).fetch_optional(pool).await?);
        if let Some(id) = item {
            return Ok(Some(Owner::Item(ItemId(id))));
        }
        let job: Option<Uuid> = on!(self, pool => sqlx::query_scalar("SELECT id FROM queue WHERE nzb_blob = $1 ORDER BY id LIMIT 1")
            .bind(nzb.0).fetch_optional(pool).await?);
        Ok(job.map(|id| Owner::Queue(JobId(id))))
    }

    async fn request_verification(
        &self,
        item: ItemId,
        now: i64,
        scan_at: Option<i64>,
        repair: bool,
    ) -> Result<Option<Uuid>, Error> {
        let _serial = self.searches.lock().await;
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            let source: Option<(Uuid, i64, Option<Uuid>, Option<i64>)> = sqlx::query_as(
                "SELECT m.id, m.generation, b.id, b.generation FROM items i JOIN blobs m ON m.id = i.meta_blob LEFT JOIN blobs b ON b.id = i.nzb_blob WHERE i.id = $1 AND i.kind <> 1")
                .bind(item.0).fetch_optional(&mut *tx).await?;
            let Some((meta, meta_generation, nzb, generation)) = source else { return Ok(None) };
            let generation = generation.unwrap_or(0);
            let existing: Option<Uuid> = sqlx::query_scalar("SELECT id FROM repair_jobs WHERE item_id = $1 AND meta_blob = $2 AND meta_generation = $3 AND generation = $4 AND verify_only = TRUE")
                .bind(item.0).bind(meta).bind(meta_generation).bind(generation).fetch_optional(&mut *tx).await?;
            if let Some(id) = existing { return Ok(Some(id)); }
            let count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM repair_jobs WHERE state IN (0, 1, 4)").fetch_one(&mut *tx).await?;
            if count >= 128 { return Err(Error::RepairBounds("pending job count")); }
            let id = Uuid::new_v4();
            sqlx::query("INSERT INTO repair_jobs (id, nzb_blob, item_id, meta_blob, meta_generation, generation, verify_only, next_at, updated_at, scan_at, repair_on_failure) VALUES ($1, $2, $3, $4, $5, $6, TRUE, $7, $7, $8, $9)")
                .bind(id).bind(nzb).bind(item.0).bind(meta).bind(meta_generation).bind(generation).bind(now).bind(scan_at).bind(repair).execute(&mut *tx).await?;
            tx.commit().await?;
            Ok(Some(id))
        })
    }
}

#[cfg(test)]
#[allow(clippy::unwrap_used)] // Generated test rows and migration assertions.
mod tests {
    use super::*;

    #[tokio::test]
    async fn legacy_cancelled_checks_are_reclassified_without_changing_real_failures() {
        let db = Db::connect("sqlite::memory:").await.unwrap();
        let cancelled = Uuid::new_v4();
        let failed = Uuid::new_v4();
        for (id, reason) in [
            (cancelled, "repair unavailable: Cancelled"),
            (failed, "repair unavailable: memory allowance"),
        ] {
            on!(&db, pool => sqlx::query("INSERT INTO repair_jobs(id, generation, verify_only, state, reason, next_at, updated_at) VALUES($1, 0, TRUE, 5, $2, 0, 0)")
                .bind(id).bind(reason).execute(pool).await.unwrap().rows_affected());
        }
        for _ in 0..2 {
            on!(&db, pool => sqlx::raw_sql(include_str!("../migrations/sqlite/0016_cancelled_checks.sql")).execute(pool).await.unwrap().rows_affected());
        }
        let jobs = db.par2_jobs().await.unwrap();
        assert_eq!(
            jobs.iter().find(|job| job.id == cancelled).unwrap().state,
            6
        );
        assert_eq!(jobs.iter().find(|job| job.id == failed).unwrap().state, 5);
    }
}
