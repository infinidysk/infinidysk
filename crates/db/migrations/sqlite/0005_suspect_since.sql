-- When a `suspect` item's first article miss was confirmed; NULL in every other state.
ALTER TABLE items ADD COLUMN suspect_since BIGINT;
