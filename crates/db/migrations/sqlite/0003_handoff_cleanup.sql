-- Handoff rows outlive their item and job, so the daily cleanup can still find the
-- files to remove (docs/strm-support-proposal.md §4.2). `job_id` is the history row the
-- file was handed off with; `size` is set for copied files (subtitles).
CREATE TABLE handoff_files_new (
    path TEXT PRIMARY KEY NOT NULL,
    item_id BLOB NOT NULL,
    job_id BLOB,
    size INTEGER
);
INSERT INTO handoff_files_new (path, item_id) SELECT path, item_id FROM handoff_files;
DROP TABLE handoff_files;
ALTER TABLE handoff_files_new RENAME TO handoff_files;
CREATE INDEX handoff_files_item ON handoff_files (item_id);
CREATE INDEX handoff_files_job ON handoff_files (job_id);
