-- The provider's stable key, so a renamed provider keeps its outages. Older rows have
-- only the display name.
ALTER TABLE breaker_events ADD COLUMN provider_key TEXT;
-- An open outage is written each minute; later writes update its length.
CREATE UNIQUE INDEX breaker_events_outage ON breaker_events (provider_key, at);
