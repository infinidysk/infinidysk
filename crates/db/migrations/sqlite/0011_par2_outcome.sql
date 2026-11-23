-- Why a job ended unavailable: 0 other, 1 damage found, 2 retry limit reached.
ALTER TABLE repair_jobs ADD COLUMN outcome SMALLINT NOT NULL DEFAULT 0
    CHECK (outcome BETWEEN 0 AND 2);
