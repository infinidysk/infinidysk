# Health and repairs

## Background repairs

**Settings → Repairs** monitors library items and can trigger *Arr replacements when content is unhealthy.

Requires:

- **Library Directory** visible inside the container
- At least one configured Radarr/Sonarr instance
- **Enable Background Repairs**

Tune concurrency, health-check depth, aging [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }, and streaming-failure thresholds — [Repairs settings](../configuration/repairs.md).

For streaming-triggered failures, **Repair After Streaming Failures** can require consecutive
failures before InfiniDysk starts a repair or asks *Arr to find a replacement. A successful full-file
playback or background health check resets that count. The counter is in memory, so it also resets
when InfiniDysk restarts.

## Health-check retention

Health result rows prune by age (**Maintenance** retention or `DATABASE_HEALTHCHECK_RETENTION_DAYS`). Reset counters from Maintenance when needed.

## Manual checks

Use the Health UI / repairs flows in the app to inspect failures. Known transport issues should appear as clear warnings in logs rather than opaque crashes — see [Logs](logs-crash-dumps.md).
