# Health and repairs

## Background repairs

**Settings → Repairs** monitors library items and can trigger *Arr replacements when content is unhealthy.

Requires:

- **Library Directory** visible inside the container — the organized library root (parent of Arr root folders), never the rclone mount or `/completed-symlinks`
- At least one configured [Radarr/Sonarr instance](../configuration/arrs.md)
- **Enable Background Repairs**

Tune concurrency, health-check depth, aging [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }, and streaming-failure thresholds — [Repairs settings](../configuration/repairs.md).

For streaming-triggered failures, **Repair After Streaming Failures** can require consecutive
failures before InfiniDysk starts a repair or asks *Arr to find a replacement. A successful full-file
playback or background health check resets that count. The counter is in memory, so it also resets
when InfiniDysk restarts.

Corrupt-but-present articles (CRC failures on otherwise complete files) now follow the same
escalation path when playback breaks. Confirmed corrupt segments are recorded and included in
full-coverage health classification so those files are not reported healthy — see
[Realtime corruption detection](../configuration/repairs.md).

## Health-check retention

Health result rows prune by age (**Maintenance** retention or `DATABASE_HEALTHCHECK_RETENTION_DAYS`). Reset counters from Maintenance when needed.

## Repair history [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

The **Health** page lists items that Background Repairs automatically deleted or repaired, with the
time and reason for the action. New rows retain the original NZB filename and release name so you
can locate a replacement; rows created before this feature show the affected WebDAV path instead.

The list follows Health-check retention and is cleared with the Health-check statistics reset in
**Settings → Maintenance**. It records automatic health actions only — deleting items manually or
through the API does not add a repair-history row.

## Manual checks

Use the Health UI / repairs flows in the app to inspect failures. Known transport issues should appear as clear warnings in logs rather than opaque crashes — see [Logs](logs-crash-dumps.md).
