-- The SAB `storage` path fixed when a job was handed off (docs/strm-support-proposal.md
-- §4.2); NULL derives it from the mount.
ALTER TABLE history ADD COLUMN storage TEXT;

-- Files written outside the tree for an Arr to import (STRMs, symlinks), with the item
-- each points at, so an Arr's import of one maps back to the item.
CREATE TABLE handoff_files (
    path TEXT PRIMARY KEY NOT NULL,
    item_id BLOB NOT NULL REFERENCES items (id) ON DELETE CASCADE
);
CREATE INDEX handoff_files_item ON handoff_files (item_id);
