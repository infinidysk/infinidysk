//! Temporary importer payloads remain unverified until the worker checks complete PAR2 sources.

use sha2::{Digest as _, Sha256};

use crate::{Db, Error, Pool, on};

impl Db {
    pub async fn stage_legacy_repair(
        &self,
        hash: &str,
        header: &[u8],
        bytes: &[u8],
        checksum: &[u8],
    ) -> Result<(), Error> {
        if hash.len() != 64
            || !hash
                .bytes()
                .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_lowercase())
            || header.len() > 1_048_576
            || bytes.is_empty()
            || bytes.len() > 33_554_432
            || checksum != Sha256::digest(bytes).as_slice()
        {
            return Err(Error::Corrupt("invalid legacy repair candidate"));
        }
        on!(self, pool => sqlx::query("INSERT INTO legacy_repair_articles (hash, header, payload, sha256) VALUES ($1, $2, $3, $4) ON CONFLICT (hash) DO NOTHING")
            .bind(hash).bind(header).bind(bytes).bind(checksum).execute(pool).await?.rows_affected());
        Ok(())
    }

    pub async fn legacy_repair(
        &self,
        hash: &str,
    ) -> Result<Option<(Vec<u8>, Vec<u8>, Vec<u8>)>, Error> {
        Ok(
            on!(self, pool => sqlx::query_as("SELECT header, payload, sha256 FROM legacy_repair_articles WHERE hash = $1")
            .bind(hash).fetch_optional(pool).await?),
        )
    }

    pub async fn legacy_repair_used(&self, hash: &str, id: &str, now: i64) -> Result<(), Error> {
        on!(self, pool => {
            let mut tx = pool.begin_with(self.begin_write()).await?;
            sqlx::query("UPDATE legacy_repair_articles SET message_id = $2 WHERE hash = $1").bind(hash).bind(id).execute(&mut *tx).await?;
            sqlx::query("INSERT INTO config (key, value) VALUES ('legacy.repair.hits', '1') ON CONFLICT (key) DO UPDATE SET value = CAST(CAST(config.value AS BIGINT) + 1 AS TEXT)").execute(&mut *tx).await?;
            sqlx::query("INSERT INTO config (key, value) VALUES ('legacy.repair.last_used_ms', $1) ON CONFLICT (key) DO UPDATE SET value = excluded.value")
                .bind(now.to_string()).execute(&mut *tx).await?;
            tx.commit().await?;
        });
        Ok(())
    }

    pub async fn retire_legacy_repairs(&self) -> Result<i64, Error> {
        // An eligible committed patch already passed full source verification and durability.
        on!(self, pool => sqlx::query(
            "DELETE FROM legacy_repair_articles WHERE EXISTS (SELECT 1 FROM repair_patches p JOIN blobs b ON b.id = p.nzb_blob \
             WHERE p.message_id = legacy_repair_articles.message_id AND p.generation = b.generation \
             AND (EXISTS (SELECT 1 FROM items i WHERE i.nzb_blob = b.id) OR EXISTS (SELECT 1 FROM queue q WHERE q.nzb_blob = b.id)))",
        ).execute(pool).await?.rows_affected());
        Ok(
            on!(self, pool => sqlx::query_scalar("SELECT COUNT(*) FROM legacy_repair_articles").fetch_one(pool).await?),
        )
    }
}
