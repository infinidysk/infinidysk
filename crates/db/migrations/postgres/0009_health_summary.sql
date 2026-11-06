-- Counts files per health state for the health summary without scanning every item.
CREATE INDEX items_health_summary ON items (health_state, kind) WHERE meta_blob IS NOT NULL;
