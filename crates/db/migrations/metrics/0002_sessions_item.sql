-- Per-file read history in the Explorer.
CREATE INDEX sessions_item ON sessions (item_id, closed_at);
