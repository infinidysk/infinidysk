-- Counter deltas per minute (grain 0) and per hour (grain 1). Keys starting with
-- `peak.` hold maxima instead of sums.
CREATE TABLE counters (
    grain INTEGER NOT NULL,
    ts INTEGER NOT NULL,
    key TEXT NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (grain, ts, key)
) WITHOUT ROWID;

-- Bytes fetched from providers per hour: the sum of the `p.*.bytes` hour counters, kept
-- beside them so the busiest hour and day do not scan every counter.
CREATE TABLE hourly_bytes (
    ts INTEGER PRIMARY KEY,
    bytes INTEGER NOT NULL
);

-- Totals since the store was created; never pruned.
CREATE TABLE lifetime (
    key TEXT PRIMARY KEY,
    value INTEGER NOT NULL
) WITHOUT ROWID;

-- `item_id`: the item a read was of. No foreign key: the library lives in another
-- database, and a read's history outlives its item.
CREATE TABLE sessions (
    id INTEGER PRIMARY KEY,
    opened_at INTEGER NOT NULL,
    closed_at INTEGER NOT NULL,
    item_id BLOB,
    path TEXT NOT NULL,
    delivery TEXT NOT NULL,
    client TEXT NOT NULL,
    size INTEGER NOT NULL,
    served INTEGER NOT NULL,
    first_byte_ms INTEGER,
    stalls INTEGER NOT NULL,
    stall_ms INTEGER NOT NULL,
    seeks INTEGER NOT NULL,
    end_reason TEXT NOT NULL
);
CREATE INDEX sessions_closed_at ON sessions (closed_at);

CREATE TABLE breaker_events (
    at INTEGER NOT NULL,
    provider TEXT NOT NULL,
    open_ms INTEGER NOT NULL
);
CREATE INDEX breaker_events_at ON breaker_events (at);
