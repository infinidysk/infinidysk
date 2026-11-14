-- Outage lookups match on when an outage ended, so a short window does not scan a year.
CREATE INDEX breaker_events_end ON breaker_events (at + open_ms);
