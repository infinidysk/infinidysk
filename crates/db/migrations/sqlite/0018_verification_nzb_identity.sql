-- An NZB restored at generation zero must not reuse a check made without it.
DROP INDEX repair_jobs_verification;
CREATE UNIQUE INDEX repair_jobs_verification
    ON repair_jobs(item_id, meta_blob, meta_generation, generation, nzb_blob)
    WHERE verify_only = TRUE AND nzb_blob IS NOT NULL;
CREATE UNIQUE INDEX repair_jobs_verification_without_nzb
    ON repair_jobs(item_id, meta_blob, meta_generation, generation)
    WHERE verify_only = TRUE AND nzb_blob IS NULL;
