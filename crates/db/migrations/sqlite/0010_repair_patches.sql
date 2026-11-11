-- SQLite promotes overflowing integer arithmetic to REAL instead of failing it.
ALTER TABLE blobs ADD COLUMN generation BIGINT NOT NULL DEFAULT 0
    CHECK (typeof(generation) = 'integer' AND generation >= 0);

-- Physical files are durable before these references commit. Removing the last NZB
-- owner makes references ineligible; deleting the NZB removes them for later file cleanup.
CREATE TABLE repair_patches (
    id BLOB PRIMARY KEY NOT NULL,
    nzb_blob BLOB NOT NULL REFERENCES blobs (id) ON DELETE CASCADE,
    generation BIGINT NOT NULL CHECK (generation >= 0),
    set_id BLOB NOT NULL CHECK (length(set_id) = 16),
    file_id BLOB NOT NULL CHECK (length(file_id) = 16),
    start_byte BIGINT NOT NULL CHECK (start_byte >= 0),
    end_byte BIGINT NOT NULL CHECK (end_byte > start_byte),
    message_id TEXT,
    descriptor BLOB NOT NULL CHECK (length(descriptor) BETWEEN 10 AND 8192),
    UNIQUE (nzb_blob, generation, set_id, file_id, start_byte, end_byte),
    UNIQUE (nzb_blob, generation, message_id)
);
