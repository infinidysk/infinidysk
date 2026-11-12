-- Older workers recorded interrupted checks as unavailable.
UPDATE repair_jobs SET state = 6, reason = 'cancelled (recorded by an older build)'
WHERE state = 5 AND verify_only = TRUE AND reason = 'repair unavailable: Cancelled';
