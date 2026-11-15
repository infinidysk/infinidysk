-- Retry counters can reset; claim identities must never repeat.
ALTER TABLE repair_jobs ADD COLUMN claim_generation BIGINT NOT NULL DEFAULT 0
    CHECK (typeof(claim_generation) = 'integer' AND claim_generation >= 0);
