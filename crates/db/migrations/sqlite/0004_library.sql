-- Library conversion journal (docs/strm-support-proposal.md §4.5): one row per library
-- pointer moving to a new name, so a crash at any step resumes. `original` is what the
-- source held and `written` what the target holds (link text or STRM body); `owners` is
-- the Arr files the source was, as JSON.
CREATE TABLE library_files (
    source TEXT PRIMARY KEY NOT NULL,
    target TEXT NOT NULL,
    item_id BLOB NOT NULL,
    state SMALLINT NOT NULL,
    from_link BOOLEAN NOT NULL,
    to_link BOOLEAN NOT NULL,
    original TEXT NOT NULL,
    written TEXT NOT NULL,
    owners TEXT NOT NULL,
    reason TEXT,
    updated_at BIGINT NOT NULL
);
CREATE INDEX library_files_state ON library_files (state);

-- Set while a converted file waits for its Arr's rescan to give it a new file id.
ALTER TABLE arr_owners ADD COLUMN remap_pending BOOLEAN NOT NULL DEFAULT FALSE;
