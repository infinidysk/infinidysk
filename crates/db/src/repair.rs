//! Committed references to immutable repair files. File durability belongs to the caller.

use infinidysk_core::{
    BlobId, ByteRange, ItemId, JobId,
    repair::{Omitted, Patch},
};
use sqlx::Connection as _;
use uuid::Uuid;

use crate::{DELETE_UNUSED_BLOB, Db, Error, Ordering, Pool, RELEASE_NZB, now_ms, on};

/// Bound the descriptors held for one atomic publication.
pub const MAX_PATCHES: usize = 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Owner {
    Item(ItemId),
    Queue(JobId),
}

/// Captured before reading source bytes. Metadata changes invalidate publication;
/// already published bytes remain usable while their original NZB is unchanged and owned.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Source {
    owner: Owner,
    nzb: BlobId,
    generation: i64,
    metadata: Option<(BlobId, i64)>,
}

impl Source {
    pub const fn nzb(&self) -> BlobId {
        self.nzb
    }

    pub const fn generation(&self) -> i64 {
        self.generation
    }
}

const ITEM: &str = "SELECT b.id, b.generation, m.id, m.generation FROM items i \
    JOIN blobs b ON b.id = i.nzb_blob JOIN blobs m ON m.id = i.meta_blob \
    WHERE i.id = $1 AND i.kind <> 1";
const QUEUE: &str = "SELECT b.id, b.generation FROM queue q \
    JOIN blobs b ON b.id = q.nzb_blob WHERE q.id = $1";

macro_rules! capture {
    ($executor:expr, $owner:expr) => {
        match $owner {
            Owner::Item(id) => {
                let row: Option<(Uuid, i64, Uuid, i64)> = sqlx::query_as(ITEM)
                    .bind(id.0)
                    .fetch_optional($executor)
                    .await?;
                row.map(|(nzb, generation, meta, version)| Source {
                    owner: $owner,
                    nzb: BlobId(nzb),
                    generation,
                    metadata: Some((BlobId(meta), version)),
                })
            }
            Owner::Queue(id) => {
                let row: Option<(Uuid, i64)> = sqlx::query_as(QUEUE)
                    .bind(id.0)
                    .fetch_optional($executor)
                    .await?;
                row.map(|(nzb, generation)| Source {
                    owner: $owner,
                    nzb: BlobId(nzb),
                    generation,
                    metadata: None,
                })
            }
        }
    };
}

// Every lookup checks current NZB bytes and a live source owner in the same statement.
macro_rules! active {
    () => {
        " FROM repair_patches p JOIN blobs b ON b.id = p.nzb_blob \
         WHERE p.nzb_blob = $1 AND p.generation = $2 AND b.generation = p.generation \
         AND (EXISTS (SELECT 1 FROM items WHERE nzb_blob = b.id) \
           OR EXISTS (SELECT 1 FROM queue WHERE nzb_blob = b.id)) "
    };
}

macro_rules! columns {
    () => {
        "SELECT p.id, p.set_id, p.file_id, p.start_byte, p.end_byte, p.message_id, p.descriptor"
    };
}

#[derive(sqlx::FromRow)]
struct Row {
    id: Uuid,
    set_id: Vec<u8>,
    file_id: Vec<u8>,
    start_byte: i64,
    end_byte: i64,
    message_id: Option<String>,
    descriptor: Vec<u8>,
}

fn decode(row: &Row) -> Result<Patch, Error> {
    let patch = Patch::decode(&row.descriptor)?;
    if patch.id.0 != row.id
        || patch.source.set.as_slice() != row.set_id
        || patch.source.file.as_slice() != row.file_id
        || i64::try_from(patch.source.range.start) != Ok(row.start_byte)
        || i64::try_from(patch.source.range.end) != Ok(row.end_byte)
        || patch.source.message_id != row.message_id
    {
        return Err(Error::Corrupt("repair descriptor differs from its index"));
    }
    Ok(patch)
}

impl Db {
    /// Keep a damaged inner NZB as the queued repair source before parking its import.
    pub async fn replace_queued_repair_nzb(
        &self,
        source: &Source,
        encoded: &[u8],
        total_bytes: u64,
        file_name: &str,
    ) -> Result<bool, Error> {
        let Owner::Queue(job) = source.owner else {
            return Err(Error::RepairBounds("queued repair owner"));
        };
        let total = i64::try_from(total_bytes).map_err(|_| Error::RepairBounds("NZB size"))?;
        let replacement = BlobId::new();
        let changed = on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            if matches!(self.pool, Pool::Postgres(_)) {
                sqlx::query("SELECT id FROM queue WHERE id = $1 FOR UPDATE").bind(job.0).execute(&mut *tx).await?;
                sqlx::query("SELECT id FROM blobs WHERE id = $1 FOR UPDATE").bind(source.nzb.0).execute(&mut *tx).await?;
            }
            if capture!(&mut *tx, source.owner) != Some(source.clone()) {
                return Ok(false);
            }
            sqlx::query("INSERT INTO blobs (id, data) VALUES ($1, $2)")
                .bind(replacement.0).bind(encoded).execute(&mut *tx).await?;
            sqlx::query("UPDATE queue SET nzb_blob = $2, total_bytes = $3, file_name = $4, content_hash = NULL WHERE id = $1")
                .bind(job.0).bind(replacement.0).bind(total).bind(file_name).execute(&mut *tx).await?;
            crate::release_nzb!(self, tx, source.nzb.0);
            tx.commit().await?;
            true
        });
        if changed {
            self.notify(crate::Change::Queue);
        }
        Ok(changed)
    }

    /// Bound compressed source allocation before loading it from either database.
    pub async fn repair_blob(&self, id: BlobId, limit: i64) -> Result<Option<Vec<u8>>, Error> {
        Ok(
            on!(self, pool => sqlx::query_scalar("SELECT data FROM blobs WHERE id = $1 AND length(data) <= $2")
            .bind(id.0).bind(limit).fetch_optional(pool).await?),
        )
    }
    pub async fn patch_nzb(&self, id: infinidysk_core::PatchId) -> Result<Option<BlobId>, Error> {
        let id: Option<Uuid> = on!(self, pool => sqlx::query_scalar("SELECT nzb_blob FROM repair_patches WHERE id = $1")
            .bind(id.0).fetch_optional(pool).await?);
        Ok(id.map(BlobId))
    }

    pub async fn drain_repair_writes(&self) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            if matches!(self.pool, Pool::Postgres(_)) {
                sqlx::query("LOCK TABLE repair_patches IN SHARE MODE").execute(&mut *tx).await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }

    pub async fn recheck_missing_repairs(&self) -> Result<(), Error> {
        on!(self, pool => sqlx::query("UPDATE items SET health_state = CASE WHEN health_state = 5 THEN 5 ELSE 0 END, next_health_check = 0, recheck_requested = 1 WHERE nzb_blob IN (SELECT nzb_blob FROM repair_patches)")
            .execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Catalog order is stable across deletion; at most 64 descriptors per page.
    pub async fn repair_files(
        &self,
        after: Option<infinidysk_core::PatchId>,
    ) -> Result<Vec<Patch>, Error> {
        let rows: Vec<Row> = on!(self, pool => sqlx::query_as(concat!(columns!(),
            " FROM repair_patches p WHERE p.id > $1 OR $1 IS NULL ORDER BY p.id LIMIT 64"))
            .bind(after.map(|id| id.0)).fetch_all(pool).await?);
        rows.iter().map(decode).collect()
    }

    pub async fn repair_payload(
        &self,
        id: infinidysk_core::PatchId,
    ) -> Result<Option<Vec<u8>>, Error> {
        Ok(on!(self, pool => sqlx::query_scalar::<_, Option<Vec<u8>>>("SELECT backup_payload FROM repair_patches WHERE id = $1")
            .bind(id.0).fetch_optional(pool).await?).flatten())
    }

    /// Backup copies embed payloads. Live catalogs clear them only after durable restore.
    pub async fn set_repair_payload(
        &self,
        patch: &Patch,
        data: Option<&[u8]>,
    ) -> Result<(), Error> {
        if let Some(data) = data {
            use sha2::{Digest as _, Sha256};
            if data.len() as u64 != patch.source.range.len()
                || <[u8; 32]>::from(Sha256::digest(data)) != patch.sha256
            {
                return Err(Error::Corrupt(
                    "backup repair payload differs from its descriptor",
                ));
            }
        }
        on!(self, pool => sqlx::query("UPDATE repair_patches SET backup_payload = $2 WHERE id = $1 AND descriptor = $3")
            .bind(patch.id.0).bind(data).bind(patch.encode()?).execute(pool).await?.rows_affected());
        Ok(())
    }

    pub async fn remove_unused_patch(&self, id: infinidysk_core::PatchId) -> Result<bool, Error> {
        Ok(on!(self, pool => sqlx::query(
            "DELETE FROM repair_patches WHERE id = $1 AND NOT EXISTS (SELECT 1 FROM blobs b WHERE b.id = repair_patches.nzb_blob \
             AND b.generation = repair_patches.generation AND (EXISTS (SELECT 1 FROM items WHERE nzb_blob = b.id) \
             OR EXISTS (SELECT 1 FROM queue WHERE nzb_blob = b.id)))",
        ).bind(id.0).execute(pool).await?.rows_affected())
            > 0)
    }
    /// Publish recovered proof/layout metadata under the same owner/blob source check.
    pub async fn publish_repair_metadata(
        &self,
        source: &Source,
        data: &[u8],
    ) -> Result<bool, Error> {
        let Some((meta, generation)) = source.metadata else {
            return Ok(false);
        };
        let Owner::Item(item) = source.owner else {
            return Ok(false);
        };
        let changed = on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            if matches!(self.pool, Pool::Postgres(_)) {
                sqlx::query("SELECT id FROM items WHERE id = $1 FOR UPDATE").bind(item.0).execute(&mut *tx).await?;
                sqlx::query("SELECT id FROM blobs WHERE id = $1 OR id = $2 ORDER BY id FOR UPDATE")
                    .bind(source.nzb.0).bind(meta.0).execute(&mut *tx).await?;
            }
            let changed = sqlx::query(
            "UPDATE blobs SET data = $2, generation = generation + 1 WHERE id = $1 AND generation = $3 \
             AND EXISTS (SELECT 1 FROM items i JOIN blobs n ON n.id = i.nzb_blob \
             WHERE i.id = $4 AND i.meta_blob = $1 AND n.id = $5 AND n.generation = $6)")
            .bind(meta.0).bind(data).bind(generation).bind(item.0).bind(source.nzb.0).bind(source.generation)
            .execute(&mut *tx).await?.rows_affected();
            if changed > 0 {
                sqlx::query("UPDATE repair_jobs SET meta_generation = meta_generation + 1 WHERE item_id = $1 AND meta_blob = $2 AND meta_generation = $3 AND generation = $4 AND state = 1 AND verify_only = TRUE")
                    .bind(item.0).bind(meta.0).bind(generation).bind(source.generation).execute(&mut *tx).await?;
                self.blob_epoch.fetch_add(1, Ordering::Release);
            }
            tx.commit().await?;
            changed
        });
        Ok(changed > 0)
    }
    /// Capture a queued original and its generation in one SQL snapshot.
    pub async fn queued_repair_nzb(
        &self,
        job: JobId,
        nzb: BlobId,
    ) -> Result<Option<(Source, Vec<u8>)>, Error> {
        let row: Option<(i64, Vec<u8>)> = on!(self, pool => sqlx::query_as(
            "SELECT b.generation, b.data FROM queue q JOIN blobs b ON b.id = q.nzb_blob \
             WHERE q.id = $1 AND q.nzb_blob = $2")
            .bind(job.0).bind(nzb.0).fetch_optional(pool).await?);
        Ok(row.map(|(generation, bytes)| {
            (
                Source {
                    owner: Owner::Queue(job),
                    nzb,
                    generation,
                    metadata: None,
                },
                bytes,
            )
        }))
    }

    /// Bind an omitted-article layout to the NZB generation from the same SQL snapshot.
    /// Expected links prevent an old VFS node from using a replacement item's source.
    pub async fn repair_metadata(
        &self,
        item: ItemId,
        meta: BlobId,
        nzb: BlobId,
    ) -> Result<Option<(i64, Vec<u8>)>, Error> {
        Ok(on!(self, pool => sqlx::query_as(
            "SELECT b.generation, m.data FROM items i \
             JOIN blobs b ON b.id = i.nzb_blob JOIN blobs m ON m.id = i.meta_blob \
             WHERE i.id = $1 AND i.meta_blob = $2 AND i.nzb_blob = $3 AND i.kind <> 1")
            .bind(item.0).bind(meta.0).bind(nzb.0).fetch_optional(pool).await?))
    }

    /// Exact omitted article only, never a global empty-message-ID lookup or an overlap.
    pub async fn omitted_patch(&self, key: &Omitted) -> Result<Option<Patch>, Error> {
        if key.generation < 0
            || key.range.is_empty()
            || key.range.end > key.file_length
            || key.range.end > i64::MAX as u64
        {
            return Err(Error::RepairBounds("omitted article identity"));
        }
        let row: Option<Row> = on!(self, pool => sqlx::query_as(concat!(columns!(), active!(),
            "AND p.set_id = $3 AND p.file_id = $4 AND p.start_byte = $5 AND p.end_byte = $6 \
             AND p.message_id IS NULL"))
            .bind(key.nzb.0).bind(key.generation).bind(key.set.as_slice()).bind(key.file.as_slice())
            .bind(i64::try_from(key.range.start).expect("validated SQL offset"))
            .bind(i64::try_from(key.range.end).expect("validated SQL offset"))
            .fetch_optional(pool).await?);
        let patch = row.as_ref().map(decode).transpose()?;
        if patch
            .as_ref()
            .is_some_and(|p| p.source.file_length != key.file_length)
        {
            return Err(Error::Corrupt(
                "omitted article length differs from its proof",
            ));
        }
        Ok(patch)
    }

    /// Conservative fast path for this database owner and its clones. False is known
    /// empty; true may include failed publications or references no longer eligible.
    pub fn may_have_repairs(&self) -> bool {
        self.repair_present.load(Ordering::Acquire)
    }

    /// Named-article candidates across current owners. Callers must reject different
    /// payload/range identities before selecting a local file; message IDs alone do not
    /// justify choosing between conflicting repairs.
    pub async fn article_repairs(&self, message_id: &str) -> Result<Vec<Patch>, Error> {
        let rows: Vec<Row> = on!(self, pool => sqlx::query_as(
            concat!(columns!(),
                " FROM repair_patches p JOIN blobs b ON b.id = p.nzb_blob \
                 WHERE p.message_id = $1 AND b.generation = p.generation \
                 AND (EXISTS (SELECT 1 FROM items WHERE nzb_blob = b.id) \
                   OR EXISTS (SELECT 1 FROM queue WHERE nzb_blob = b.id)) \
                 ORDER BY p.id LIMIT $2"))
            .bind(message_id).bind(i64::try_from(MAX_PATCHES + 1).expect("1025 fits i64"))
            .fetch_all(pool).await?);
        if rows.len() > MAX_PATCHES {
            return Err(Error::RepairBounds("article candidates"));
        }
        rows.iter().map(decode).collect()
    }

    pub async fn repair_source(&self, owner: Owner) -> Result<Option<Source>, Error> {
        Ok(on!(self, pool => capture!(pool, owner)))
    }

    /// Publish one bounded batch atomically. The caller must hold durability receipts
    /// for every descriptor and must have verified the reconstructed source bytes first.
    /// False means the source changed or disappeared. Errors never publish a partial batch.
    /// An uncertain commit may have succeeded. Retry the same IDs; retain files until
    /// reference reconciliation proves they can be removed.
    pub async fn publish_patches(&self, source: &Source, patches: &[Patch]) -> Result<bool, Error> {
        if patches.is_empty() || patches.len() > MAX_PATCHES {
            return Err(Error::RepairBounds("batch size"));
        }
        let encoded: Vec<_> = patches
            .iter()
            .map(|patch| {
                if patch.source.range.end > i64::MAX as u64 {
                    return Err(Error::RepairBounds("source offset"));
                }
                Ok(patch.encode()?)
            })
            .collect::<Result<_, Error>>()?;
        let postgres = matches!(self.pool, Pool::Postgres(_));
        on!(self, pool => {
            // Never return a connection with a changed durability setting to the pool,
            // including when this future is cancelled before COMMIT.
            let mut conn = pool.acquire().await?.detach();
            if !postgres {
                sqlx::query("PRAGMA synchronous = FULL").execute(&mut conn).await?;
            }
            let mut tx = conn.begin_with(self.begin_write()).await?;
            if postgres {
                sqlx::query("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")
                    .execute(&mut *tx).await?;
                sqlx::query("SET LOCAL synchronous_commit = on").execute(&mut *tx).await?;
                let (lock, id) = match source.owner {
                    Owner::Item(id) => ("SELECT 1 FROM items WHERE id = $1 FOR UPDATE", id.0),
                    Owner::Queue(id) => ("SELECT 1 FROM queue WHERE id = $1 FOR UPDATE", id.0),
                };
                sqlx::query(lock).bind(id).execute(&mut *tx).await?;
                // Owner first, then blobs in ID order: deletion takes the same direction.
                sqlx::query("SELECT id FROM blobs WHERE id = $1 OR id = $2 ORDER BY id FOR UPDATE")
                    .bind(source.nzb.0).bind(source.metadata.map(|(id, _)| id.0))
                    .execute(&mut *tx).await?;
            }
            if capture!(&mut *tx, source.owner) != Some(source.clone()) {
                tx.rollback().await?;
                return Ok(false);
            }
            for (patch, descriptor) in patches.iter().zip(&encoded) {
                let s = &patch.source;
                let start = i64::try_from(s.range.start).expect("validated SQL offset");
                let end = i64::try_from(s.range.end).expect("validated SQL offset");
                let existing: Vec<(Uuid, i64, Vec<u8>)> = sqlx::query_as(
                    "SELECT nzb_blob, generation, descriptor FROM repair_patches WHERE id = $1 \
                     OR (nzb_blob = $2 AND generation = $3 AND \
                       ((set_id = $4 AND file_id = $5 AND start_byte < $7 AND end_byte > $6) \
                         OR message_id = $8)) LIMIT 2",
                ).bind(patch.id.0).bind(source.nzb.0).bind(source.generation)
                    .bind(s.set.as_slice()).bind(s.file.as_slice()).bind(start).bind(end)
                    .bind(&s.message_id).fetch_all(&mut *tx).await?;
                if !existing.is_empty() {
                    if existing.len() != 1
                        || existing[0].0 != source.nzb.0
                        || existing[0].1 != source.generation
                        || &existing[0].2 != descriptor
                    {
                        return Err(Error::RepairConflict);
                    }
                    continue;
                }
                sqlx::query(
                    "INSERT INTO repair_patches (id, nzb_blob, generation, set_id, file_id, \
                     start_byte, end_byte, message_id, descriptor) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)",
                ).bind(patch.id.0).bind(source.nzb.0).bind(source.generation)
                    .bind(s.set.as_slice()).bind(s.file.as_slice()).bind(start).bind(end)
                    .bind(&s.message_id).bind(descriptor).execute(&mut *tx).await?;
            }
            // Enabled before visibility so a concurrent read cannot skip a committed row.
            self.repair_present.store(true, Ordering::Release);
            // Released health observations must retry even after an uncertain COMMIT.
            self.blob_epoch.fetch_add(1, Ordering::Release);
            tx.commit().await?;
        });
        Ok(true)
    }

    /// Only committed patches for this unchanged original NZB, shared by its live owners.
    pub async fn article_patch(
        &self,
        source: &Source,
        message_id: &str,
    ) -> Result<Option<Patch>, Error> {
        let row: Option<Row> = on!(self, pool => sqlx::query_as(concat!(columns!(), active!(),
            "AND p.message_id = $3"))
            .bind(source.nzb.0).bind(source.generation).bind(message_id)
            .fetch_optional(pool).await?);
        row.as_ref().map(decode).transpose()
    }

    /// Intersections include omitted articles whose message ID is absent.
    pub async fn range_patches(
        &self,
        source: &Source,
        set: [u8; 16],
        file: [u8; 16],
        range: ByteRange,
    ) -> Result<Vec<Patch>, Error> {
        if range.is_empty() || range.end > i64::MAX as u64 {
            return Err(Error::RepairBounds("source range"));
        }
        let rows: Vec<Row> = on!(self, pool => sqlx::query_as(concat!(columns!(), active!(),
            "AND p.set_id = $3 AND p.file_id = $4 AND p.start_byte < $6 AND p.end_byte > $5 \
             ORDER BY p.start_byte LIMIT $7"))
            .bind(source.nzb.0).bind(source.generation).bind(set.as_slice()).bind(file.as_slice())
            .bind(i64::try_from(range.start).expect("validated SQL offset"))
            .bind(i64::try_from(range.end).expect("validated SQL offset"))
            .bind(i64::try_from(MAX_PATCHES + 1).expect("1025 fits i64"))
            .fetch_all(pool).await?);
        if rows.len() > MAX_PATCHES {
            return Err(Error::RepairBounds("lookup size"));
        }
        rows.iter().map(decode).collect()
    }
}
