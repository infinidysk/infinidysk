ALTER TABLE repair_jobs ADD COLUMN scan_at BIGINT;
ALTER TABLE repair_jobs ADD COLUMN repair_on_failure BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE repair_jobs ADD COLUMN checked_articles BIGINT NOT NULL DEFAULT 0;
ALTER TABLE repair_jobs ADD COLUMN total_articles BIGINT NOT NULL DEFAULT 0;
-- Existing queued checks join their old scan; subsequent manual requests remain separate.
UPDATE repair_jobs SET scan_at = (SELECT json_extract(value, '$.requested_at') FROM config WHERE key = 'par2.scan')
WHERE verify_only = TRUE AND state IN (0, 1, 4);
CREATE INDEX repair_jobs_lane ON repair_jobs(verify_only, state, next_at);
