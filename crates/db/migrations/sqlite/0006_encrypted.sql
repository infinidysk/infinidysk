-- 1 when the file is an encrypted archive member, read through AES. Earlier rows are 0:
-- nothing encrypted was imported before this column.
ALTER TABLE items ADD COLUMN encrypted SMALLINT NOT NULL DEFAULT 0;
CREATE INDEX items_encrypted ON items (encrypted) WHERE encrypted <> 0;
