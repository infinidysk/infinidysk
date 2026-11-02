-- Ids are UUIDv7 stored as 16-byte BLOBs; timestamps are unix milliseconds.

-- `unreferenced_at` (import policy §9): when an NZB blob lost its last reference, so the
-- daily cleanup deletes it once the retention window passed. Metadata blobs never get
-- one. `content_hash` (as on queue) lets a resubmission reuse an identical NZB.
CREATE TABLE blobs (
    id BLOB PRIMARY KEY NOT NULL,
    data BLOB NOT NULL,
    unreferenced_at BIGINT,
    content_hash TEXT
);
CREATE INDEX blobs_unreferenced ON blobs (unreferenced_at) WHERE unreferenced_at IS NOT NULL;
CREATE INDEX blobs_content_hash ON blobs (content_hash) WHERE content_hash IS NOT NULL;

-- Health state machine columns: docs/health-policy.md §3, §12. `job_id`/`job_name`: the
-- job a download directory came from, kept after its history row is deleted.
CREATE TABLE items (
    id BLOB PRIMARY KEY NOT NULL,
    parent_id BLOB REFERENCES items (id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    path TEXT NOT NULL UNIQUE,
    kind SMALLINT NOT NULL,
    size BIGINT,
    meta_blob BLOB UNIQUE REFERENCES blobs (id),
    nzb_blob BLOB REFERENCES blobs (id),
    created_at BIGINT NOT NULL,
    release_date BIGINT,
    last_health_check BIGINT,
    next_health_check BIGINT,
    health_state SMALLINT NOT NULL DEFAULT 0,
    repair_attempts SMALLINT NOT NULL DEFAULT 0,
    hidden_until BIGINT,
    recheck_requested SMALLINT NOT NULL DEFAULT 0,
    job_id BLOB,
    job_name TEXT
);
CREATE INDEX items_parent ON items (parent_id, name);
CREATE INDEX items_nzb_blob ON items (nzb_blob);
CREATE INDEX items_next_health_check ON items (next_health_check)
WHERE next_health_check IS NOT NULL;
CREATE INDEX items_recheck ON items (recheck_requested) WHERE recheck_requested <> 0;
-- Job folder lookups (Arr imports).
CREATE INDEX items_job ON items (job_id) WHERE job_id IS NOT NULL;

INSERT INTO items (id, parent_id, name, path, kind, created_at) VALUES
(X'00000000000000000000000000000000', NULL, '', '/', 1, 0),
(X'00000000000000000000000000000001', X'00000000000000000000000000000000', 'content', '/content', 1, 0);

-- Manual pause is separate from the retry time; failed attempts back off and show why.
-- `content_hash`: SHA-256 of the job's sorted, deduplicated message-ids, hex. A
-- submission matching a queued job returns that job. Filled in by the app at startup
-- for rows written without one.
CREATE TABLE queue (
    id BLOB PRIMARY KEY NOT NULL,
    created_at BIGINT NOT NULL,
    sort_order BIGINT NOT NULL,
    file_name TEXT NOT NULL,
    job_name TEXT NOT NULL,
    category TEXT NOT NULL,
    priority SMALLINT NOT NULL,
    post_processing SMALLINT NOT NULL,
    nzb_blob BLOB NOT NULL REFERENCES blobs (id),
    nzb_size BIGINT NOT NULL,
    total_bytes BIGINT NOT NULL,
    retry_at BIGINT,
    paused BOOLEAN NOT NULL DEFAULT FALSE,
    attempts INTEGER NOT NULL DEFAULT 0,
    stuck_count INTEGER NOT NULL DEFAULT 0,
    last_error TEXT,
    content_hash TEXT
);
CREATE INDEX queue_order ON queue (priority DESC, sort_order);
CREATE INDEX queue_content_hash ON queue (content_hash);
-- Reference checks run when an NZB goes away.
CREATE INDEX queue_nzb_blob ON queue (nzb_blob);

CREATE TABLE history (
    id BLOB PRIMARY KEY NOT NULL,
    created_at BIGINT NOT NULL,
    file_name TEXT NOT NULL,
    job_name TEXT NOT NULL,
    category TEXT NOT NULL,
    status SMALLINT NOT NULL,
    total_bytes BIGINT NOT NULL,
    download_seconds INTEGER NOT NULL,
    fail_message TEXT,
    download_dir BLOB REFERENCES items (id) ON DELETE SET NULL,
    nzb_blob BLOB REFERENCES blobs (id)
);
CREATE INDEX history_created ON history (category, created_at DESC);
-- History pruning (import policy §8) by finish time across categories.
CREATE INDEX history_finished ON history (created_at);
-- Reference checks run when an NZB or a download folder goes away.
CREATE INDEX history_nzb_blob ON history (nzb_blob);
CREATE INDEX history_download_dir ON history (download_dir);

-- No foreign key to items: results outlive the items they describe.
CREATE TABLE health_checks (
    id BLOB PRIMARY KEY NOT NULL,
    created_at BIGINT NOT NULL,
    item_id BLOB NOT NULL,
    path TEXT NOT NULL,
    result SMALLINT NOT NULL,
    repair SMALLINT NOT NULL,
    message TEXT
);
CREATE INDEX health_checks_created ON health_checks (created_at DESC);
-- An item's latest check without sorting all of its checks.
CREATE INDEX health_checks_item ON health_checks (item_id, created_at DESC, id DESC);

-- Media analysis and container evidence per file (docs/health-policy.md §5.7).
CREATE TABLE media_probes (
    item_id BLOB PRIMARY KEY NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    requested_at BIGINT,
    attempted_at BIGINT,
    outcome SMALLINT,
    reason TEXT,
    info TEXT,
    info_at BIGINT,
    defect_offset BIGINT,
    defect_size BIGINT,
    defect_seen_at BIGINT,
    defect_source BIGINT,
    structural_hold SMALLINT NOT NULL DEFAULT 0
);
CREATE INDEX media_probes_requested ON media_probes (requested_at)
WHERE requested_at IS NOT NULL;

-- Releases we asked an Arr to replace; incoming NZBs matching them fail.
CREATE TABLE rejected_releases (
    id BLOB PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    expires_at BIGINT NOT NULL
);
CREATE INDEX rejected_releases_name ON rejected_releases (name);

CREATE TABLE rejected_articles (
    message_id TEXT NOT NULL,
    release_id BLOB NOT NULL REFERENCES rejected_releases (id) ON DELETE CASCADE,
    PRIMARY KEY (message_id, release_id)
);
CREATE INDEX rejected_articles_release ON rejected_articles (release_id);

-- Lowercased categories whose owning Arrs still need to hear that a job finished.
-- Written with the history row; `generation` grows on each finish so a send that raced
-- a newer finish does not clear it.
CREATE TABLE arr_refresh (
    category TEXT PRIMARY KEY,
    generation BIGINT NOT NULL
);

-- Searches we asked an Arr to run, per piece of media, for the shared sliding-window
-- search limit. Rows older than the window are pruned on the next reservation.
CREATE TABLE search_reservations (
    instance_id BLOB NOT NULL,
    kind TEXT NOT NULL,
    media_id BIGINT NOT NULL,
    at_ms BIGINT NOT NULL
);
CREATE INDEX search_reservations_media ON search_reservations (instance_id, kind, media_id, at_ms);
CREATE INDEX search_reservations_at ON search_reservations (at_ms);

-- Which Arr library file each of our items was imported as. A file id is absent where
-- the Arr sends none. Gone with the item.
CREATE TABLE arr_owners (
    instance_id BLOB NOT NULL,
    item_id BLOB NOT NULL REFERENCES items (id) ON DELETE CASCADE,
    file_id BIGINT,
    path TEXT NOT NULL,
    job_id BLOB,
    PRIMARY KEY (instance_id, item_id)
);
CREATE INDEX arr_owners_item ON arr_owners (item_id);
CREATE INDEX arr_owners_file ON arr_owners (instance_id, file_id);
CREATE INDEX arr_owners_path ON arr_owners (instance_id, path);
CREATE INDEX arr_owners_job ON arr_owners (job_id);

-- Accepted webhook deliveries not yet applied, oldest id first.
CREATE TABLE arr_events (
    id BLOB PRIMARY KEY NOT NULL,
    instance_id BLOB NOT NULL,
    body BLOB NOT NULL
);

CREATE TABLE config (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);

CREATE TABLE accounts (
    kind SMALLINT NOT NULL,
    username TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    PRIMARY KEY (kind, username)
);
CREATE UNIQUE INDEX accounts_one_admin ON accounts (kind) WHERE kind = 0;
