//! Explicit, read-only verification of existing files from stored PAR2 proofs.

use std::sync::Arc;

use futures_util::{StreamExt as _, stream};
use infinidysk_core::{BlobId, FileMeta, ItemId, blob};
use infinidysk_nntp::{Articles, Error as SourceError};
use infinidysk_par2::{Error as ContentError, Slice, Verifier};
use tokio_util::sync::CancellationToken;

use super::check::Direct;
use super::{CONCURRENCY_IDLE, CONCURRENCY_STREAMING, Error, Health};

/// An observation only: it never clears damage, changes health, or requests replacement.
#[derive(Debug)]
pub enum Verification {
    Verified {
        bytes: u64,
        slices: usize,
    },
    /// Required proof/layout information is absent or cannot describe a complete file.
    Unavailable(&'static str),
    Mismatch(ContentError),
    /// A failed source read, including inconclusive provider errors; never playback filler.
    Source(SourceError),
    /// The item was removed or its metadata changed while bytes were being read.
    Changed,
    Cancelled,
}

impl Health {
    async fn verification_meta(
        &self,
        item: &infinidysk_db::Item,
    ) -> Result<Option<(BlobId, FileMeta)>, Error> {
        const LIMIT: i64 = 16_000_000;
        let Some(id) = item.meta_blob else {
            return Ok(None);
        };
        let Some(bytes) = self.db.repair_blob(id, LIMIT).await? else {
            return Ok(None);
        };
        let limit = usize::try_from(LIMIT).expect("16 MB fits the supported targets");
        Ok(Some((id, blob::decode_meta_limited(&bytes, limit)?)))
    }

    /// Check a plain file without requiring its original NZB or recovery data.
    /// Results are observations, not persisted health updates. Archives need complete
    /// volume planning and currently return `Unavailable`. Hashing runs off async workers.
    /// Call as top-level work, without an existing database write permit.
    pub async fn verify_existing(
        &self,
        id: ItemId,
        cancel: &CancellationToken,
    ) -> Result<Verification, Error> {
        self.verify_existing_with_probe(id, cancel, None).await
    }

    pub async fn verify_existing_with_probe(
        &self,
        id: ItemId,
        cancel: &CancellationToken,
        probe: Option<infinidysk_core::trace::Probe>,
    ) -> Result<Verification, Error> {
        self.verify_existing_bounded(id, cancel, probe, 256_000_000, 100_000_000_000)
            .await
    }

    /// Respect the worker's per-attempt source allowance even with a stored proof.
    pub async fn verify_existing_bounded(
        &self,
        id: ItemId,
        cancel: &CancellationToken,
        probe: Option<infinidysk_core::trace::Probe>,
        memory_bytes: usize,
        decoded_bytes: u64,
    ) -> Result<Verification, Error> {
        let cancel = cancel.child_token();
        let _cancel_on_drop = cancel.clone().drop_guard();
        let permit = tokio::select! {
            biased;
            () = cancel.cancelled() => return Ok(Verification::Cancelled),
            permit = Arc::clone(&self.verification).acquire_owned() =>
                permit.expect("verification semaphore is never closed"),
        };
        let writing = tokio::select! {
            biased;
            () = cancel.cancelled() => return Ok(Verification::Cancelled),
            writing = self.db.write_permit() => writing,
        };
        let Some(item) = self.db.item(id).await? else {
            return Ok(Verification::Unavailable("file no longer exists"));
        };
        let Some((blob_id, meta)) = self.verification_meta(&item).await? else {
            return Ok(Verification::Unavailable("file metadata is unavailable"));
        };
        let source = self.omitted_source(&item, blob_id, &meta).await?;
        let admitted = memory_bytes.saturating_sub(96_000_000) / infinidysk_nntp::MAX_DECODED_LEN;
        if admitted == 0 {
            return Ok(Verification::Unavailable(
                "working memory allowance cannot admit a source article",
            ));
        }
        let concurrency = if (self.streaming)() {
            CONCURRENCY_STREAMING
        } else {
            self.client
                .free_permits()
                .unwrap_or(CONCURRENCY_IDLE)
                .max(1)
        }
        .min(admitted); // Full verification can retain a maximum-sized article per request.
        let client = Arc::clone(&self.client);
        let stopped = cancel.clone();
        let runtime = tokio::runtime::Handle::current();
        let (meta, result, _writing) = tokio::task::spawn_blocking(move || {
            // Keep admission until cancellation has stopped hashing and released source futures.
            let _permit = permit;
            let articles = Direct {
                client: &client,
                source,
                meter: probe.as_ref(),
            };
            let result =
                runtime.block_on(run(&articles, &meta, concurrency, decoded_bytes, &stopped));
            // Cancellation keeps the publication barrier with the actual reader task.
            (meta, result, writing)
        })
        .await?;
        if cancel.is_cancelled() {
            return Ok(Verification::Cancelled);
        }
        let Some(current) = self.db.item(id).await? else {
            return Ok(Verification::Changed);
        };
        let Some((current_blob, current_meta)) = self.verification_meta(&current).await? else {
            return Ok(Verification::Changed);
        };
        if current_blob != blob_id
            || current_meta != meta
            || self
                .omitted_source(&current, current_blob, &current_meta)
                .await?
                != source
        {
            return Ok(Verification::Changed);
        }
        Ok(result)
    }
}

async fn run(
    articles: &dyn Articles,
    meta: &FileMeta,
    concurrency: usize,
    decoded_bytes: u64,
    cancel: &CancellationToken,
) -> Verification {
    let FileMeta::Nzb(file) = meta else {
        return Verification::Unavailable(
            "full archive verification needs complete source volumes",
        );
    };
    let Some(proof) = &file.proof else {
        return Verification::Unavailable(
            "no stored PAR2 proof; source metadata must be recovered first",
        );
    };
    let count = proof.slice_md5.len();
    if count != proof.slice_crc32.len()
        || count > 32_768
        || file.segments.items.len() > 100_000
        || proof.file_length != file.segments.size()
    {
        return Verification::Unavailable("stored proof or article layout is inconsistent");
    }
    if proof.file_length > decoded_bytes {
        return Verification::Unavailable("source read allowance exceeded");
    }
    let mut slices = Vec::new();
    if slices.try_reserve_exact(count).is_err() {
        return Verification::Unavailable("cannot allocate verification checksums");
    }
    slices.extend(
        proof
            .slice_md5
            .iter()
            .zip(&proof.slice_crc32)
            .map(|(&md5, &crc32)| Slice { md5, crc32 }),
    );
    let Ok(mut verifier) = Verifier::new(
        proof.file_length,
        proof.file_md5,
        u64::from(proof.slice_size),
        &slices,
    ) else {
        return Verification::Unavailable("stored PAR2 proof is inconsistent");
    };
    let requests = file.segments.items.iter().scan(0u64, |offset, segment| {
        let begin = file.segments.sizes_trusted.then_some(*offset);
        *offset += u64::from(segment.size);
        Some(async move {
            let body = articles.read(segment, begin, Some(proof)).await;
            (segment.size, body)
        })
    });
    let mut reads = stream::iter(requests).buffered(concurrency);
    loop {
        let result = tokio::select! {
            biased;
            () = cancel.cancelled() => return Verification::Cancelled,
            result = reads.next() => result,
        };
        let Some((size, result)) = result else {
            break;
        };
        let data = match result {
            Ok(data) => data,
            Err(error) => return Verification::Source(error),
        };
        if data.len() as u64 != u64::from(size) {
            return Verification::Unavailable("stored article sizes need to be refreshed");
        }
        if let Err(error) = verifier.push(&data, || cancel.is_cancelled()) {
            return failed(error);
        }
    }
    match verifier.finish(|| cancel.is_cancelled()) {
        Ok(verified) => Verification::Verified {
            bytes: verified.bytes,
            slices: verified.slices,
        },
        Err(error) => failed(error),
    }
}

fn failed(error: ContentError) -> Verification {
    if error == ContentError::Cancelled {
        Verification::Cancelled
    } else {
        Verification::Mismatch(error)
    }
}
