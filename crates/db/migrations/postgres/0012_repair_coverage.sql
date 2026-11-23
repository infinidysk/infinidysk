CREATE TABLE repair_coverage (
    job_id UUID NOT NULL REFERENCES repair_jobs(id) ON DELETE CASCADE,
    item_id UUID NOT NULL,
    meta_blob UUID NOT NULL,
    generation BIGINT NOT NULL,
    PRIMARY KEY (job_id, item_id)
);