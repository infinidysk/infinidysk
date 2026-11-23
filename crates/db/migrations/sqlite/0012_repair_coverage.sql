CREATE TABLE repair_coverage (
    job_id BLOB NOT NULL REFERENCES repair_jobs(id) ON DELETE CASCADE,
    item_id BLOB NOT NULL,
    meta_blob BLOB NOT NULL,
    generation BIGINT NOT NULL,
    PRIMARY KEY (job_id, item_id)
);