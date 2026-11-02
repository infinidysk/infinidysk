-- Handoff rows outlive their item and job, so the daily cleanup can still find the
-- files to remove (docs/strm-support-proposal.md §4.2). `job_id` is the history row the
-- file was handed off with; `size` is set for copied files (subtitles).
ALTER TABLE handoff_files DROP CONSTRAINT handoff_files_item_id_fkey;
ALTER TABLE handoff_files ADD COLUMN job_id UUID, ADD COLUMN size BIGINT;
CREATE INDEX handoff_files_job ON handoff_files (job_id);
