-- Ebooks were skipped as ignored; check them like other media.
UPDATE items SET health_state = 0, next_health_check = NULL
WHERE health_state = 7 AND (
    lower(name) LIKE '%.epub' OR lower(name) LIKE '%.kepub' OR lower(name) LIKE '%.mobi'
    OR lower(name) LIKE '%.azw' OR lower(name) LIKE '%.azw3' OR lower(name) LIKE '%.pdf'
);
