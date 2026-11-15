ALTER TABLE items ADD COLUMN arr_replacement_requested BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE repair_patches ADD COLUMN backup_payload BYTEA CHECK (backup_payload IS NULL OR length(backup_payload) <= 33554432);
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
    stored_bytes BIGINT NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX repair_jobs_source ON repair_jobs(nzb_blob, generation) WHERE verify_only = FALSE;
CREATE UNIQUE INDEX repair_jobs_verification ON repair_jobs(item_id, meta_blob, meta_generation, generation) WHERE verify_only = TRUE;
CREATE INDEX repair_jobs_due ON repair_jobs(state, next_at);

-- Temporary importer candidates, never authoritative repaired articles.
CREATE TABLE legacy_repair_articles (
    hash TEXT PRIMARY KEY NOT NULL CHECK (length(hash) = 64),
    message_id TEXT,
    header BYTEA NOT NULL CHECK (length(header) <= 1048576),
    payload BYTEA NOT NULL CHECK (length(payload) BETWEEN 1 AND 33554432),
    sha256 BYTEA NOT NULL CHECK (length(sha256) = 32)
);

