ALTER TABLE blobs ADD COLUMN generation BIGINT NOT NULL DEFAULT 0 CHECK (generation >= 0);

-- Physical files are durable before these references commit. Removing the last NZB
-- owner makes references ineligible; deleting the NZB removes them for later file cleanup.
CREATE TABLE repair_patches (
    id UUID PRIMARY KEY,
    nzb_blob UUID NOT NULL REFERENCES blobs (id) ON DELETE CASCADE,
    generation BIGINT NOT NULL CHECK (generation >= 0),
    set_id BYTEA NOT NULL CHECK (length(set_id) = 16),
    file_id BYTEA NOT NULL CHECK (length(file_id) = 16),
    start_byte BIGINT NOT NULL CHECK (start_byte >= 0),
    end_byte BIGINT NOT NULL CHECK (end_byte > start_byte),
    message_id TEXT,
    descriptor BYTEA NOT NULL CHECK (length(descriptor) BETWEEN 10 AND 8192),
    backup_payload BYTEA CHECK (backup_payload IS NULL OR length(backup_payload) <= 33554432),
    UNIQUE (nzb_blob, generation, set_id, file_id, start_byte, end_byte),
    UNIQUE (nzb_blob, generation, message_id)
);
CREATE INDEX repair_patches_message ON repair_patches (message_id) WHERE message_id IS NOT NULL;

CREATE TABLE repair_jobs (
    id UUID PRIMARY KEY NOT NULL,
    nzb_blob UUID REFERENCES blobs(id) ON DELETE CASCADE,
    item_id UUID REFERENCES items(id) ON DELETE CASCADE,
    meta_blob UUID REFERENCES blobs(id) ON DELETE CASCADE,
    meta_generation BIGINT,
    generation BIGINT NOT NULL CHECK (generation >= 0),
    verify_only BOOLEAN NOT NULL,
    state SMALLINT NOT NULL DEFAULT 0 CHECK (state BETWEEN 0 AND 6),
    attempts INTEGER NOT NULL DEFAULT 0,
    next_at BIGINT NOT NULL,
    updated_at BIGINT NOT NULL,
    started_at BIGINT,
    duration_ms BIGINT NOT NULL DEFAULT 0,
    verified_bytes BIGINT NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    transferred_bytes BIGINT NOT NULL DEFAULT 0,
    stored_bytes BIGINT NOT NULL DEFAULT 0,
    scan_at BIGINT,
    repair_on_failure BOOLEAN NOT NULL DEFAULT FALSE,
    checked_articles BIGINT NOT NULL DEFAULT 0,
    total_articles BIGINT NOT NULL DEFAULT 0,
    completed_requests BIGINT NOT NULL DEFAULT 0 CHECK (completed_requests >= 0),
    missing_requests BIGINT NOT NULL DEFAULT 0
        CHECK (missing_requests >= 0 AND missing_requests <= completed_requests),
    -- Retry counters can reset; claim identities must never repeat.
    claim_generation BIGINT NOT NULL DEFAULT 0 CHECK (claim_generation >= 0)
);
CREATE UNIQUE INDEX repair_jobs_source ON repair_jobs(nzb_blob, generation) WHERE verify_only = FALSE;
-- An NZB restored at generation zero must not reuse a check made without it.
CREATE UNIQUE INDEX repair_jobs_verification
    ON repair_jobs(item_id, meta_blob, meta_generation, generation, nzb_blob)
    WHERE verify_only = TRUE AND nzb_blob IS NOT NULL;
CREATE UNIQUE INDEX repair_jobs_verification_without_nzb
    ON repair_jobs(item_id, meta_blob, meta_generation, generation)
    WHERE verify_only = TRUE AND nzb_blob IS NULL;
CREATE INDEX repair_jobs_due ON repair_jobs(state, next_at);
CREATE INDEX repair_jobs_lane ON repair_jobs(verify_only, state, next_at);
CREATE INDEX repair_jobs_updated ON repair_jobs(updated_at DESC, id);

-- Temporary importer candidates, never authoritative repaired articles.
CREATE TABLE legacy_repair_articles (
    hash TEXT PRIMARY KEY NOT NULL CHECK (length(hash) = 64),
    message_id TEXT,
    header BYTEA NOT NULL CHECK (length(header) <= 1048576),
    payload BYTEA NOT NULL CHECK (length(payload) BETWEEN 1 AND 33554432),
    sha256 BYTEA NOT NULL CHECK (length(sha256) = 32)
);
