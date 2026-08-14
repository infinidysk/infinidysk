# Retention and cleanup

## Database retention

| Setting / env | Default | Effect |
|---------------|---------|--------|
| History retention days / `DATABASE_HISTORY_RETENTION_DAYS` | 90 | Prune SAB history rows; **does not** delete WebDAV mounts |
| Health-check retention / `DATABASE_HEALTHCHECK_RETENTION_DAYS` | 30 | Prune health result rows |
| `DATABASE_MAINTENANCE_INTERVAL_HOURS` | 6 | Sweep cadence (env) |

Configure in **Settings → Maintenance**.

## Metrics database [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }

InfiniDysk stores streaming and provider metrics in a separate SQLite file (`metrics.sqlite` under `CONFIG_PATH`). Size grows with throughput because each article fetch writes a raw row to `SegmentFetches`.

| Tier | Tables | Default retention |
|------|--------|-------------------|
| Raw fetch events | `SegmentFetches`, `FailoverMisses` | 24 hours (`metrics.fetch-retention-hours`) |
| Events + minute rollups | `MetricEvents`, `ThroughputMinutes`, `ProviderMinutes` | 7 days |
| Read sessions | `ReadSessions` | 90 days |
| Hourly rollups | `ProviderHourly`, `FailoverHourly` | 365 days (folded into lifetime totals) |

**Size expectations:** on high-throughput hosts, a few hundred MB per TB/day of sustained throughput is normal — proportional growth from raw fetch rows, not a leak. Shrinking `metrics.fetch-retention-hours` reduces raw-row depth; minute and hourly rollups still carry aggregate throughput and provider stats.

Setting `metrics.fetch-retention-hours` to `0` requests rollup-only retention; the hourly sweep enforces a one-hour floor on raw rows. Configure in **Settings → Maintenance** or via `METRICS_FETCH_RETENTION_HOURS` (headless ENV).

## NZB blobs

Blobs under `{CONFIG_PATH}/blobs/` remain while referenced by queue, history, or mounted `/content`. When the last reference drops, background cleanup removes them.

Scheduled history retention and the **Prune Completed History** task delete SAB history rows only (`deleteFiles: false`). They do not delete WebDAV mounts, but they do clear each mount's history link (`HistoryItemId`). **Remove Orphaned Files** then deletes those mounts only if they also have no library symlink or STRM.

## Orphaned files

**Remove Orphaned Files** (Maintenance) deletes WebDAV files that are not linked from the library directory and are no longer tied to a SAB history row. Supports dry run. Schedule optional daily cleanup — set container `TZ`. Direct WebDAV or rclone playback is not a library link.

**Library Directory** must be the organized library root that contains your Arr-imported symlinks or STRMs (the parent of your Radarr/Sonarr root folders). It must be visible inside the InfiniDysk container. Do not point it at the rclone mount (`rclone.mount-dir`) or at `/completed-symlinks` — that folder is InfiniDysk's virtual view of current History rows, so scanning it cannot protect files after history is cleared. Remove Orphaned Files aborts (dry run included) when Library Directory is the mount or a path inside it.

History entries disappearing after an Arr import are client-initiated cleanup, not InfiniDysk deleting the mount: the Arr's **Remove Completed** setting, InfiniDysk **Automatic Queue Management** rules that call the Arr with `removeFromClient=true`, or a WebDAV DELETE of a release folder under `/completed-symlinks`. Mounted files stay streamable; only the History row is removed.

## NZB file backups

Optional copies of incoming NZBs (SABnzbd settings) prune by `api.nzb-backup-retention-days`.

[Deletion audit](deletion-audit.md) · [Maintenance](../configuration/maintenance.md)
