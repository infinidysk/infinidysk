ALTER TABLE repair_jobs ADD COLUMN completed_requests BIGINT NOT NULL DEFAULT 0 CHECK (completed_requests >= 0);
ALTER TABLE repair_jobs ADD COLUMN missing_requests BIGINT NOT NULL DEFAULT 0 CHECK (missing_requests >= 0 AND missing_requests <= completed_requests);
