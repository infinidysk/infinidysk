-- The provider's stable key, so a renamed provider keeps its outages. Older rows have
-- only the display name.
ALTER TABLE breaker_events ADD COLUMN provider_key TEXT;
