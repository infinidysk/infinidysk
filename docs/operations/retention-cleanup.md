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

## Missing streaming payloads [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

A database restore without the matching `blobs/` directory can leave mounted files whose streaming metadata no longer exists. Health checks report these as **Action needed** and back off to weekly checks after three consecutive confirmations. This is local data loss, not evidence that the Usenet release is bad.

Use **Settings → Maintenance → Clean Missing Payloads** to resolve the broken references. Run the dry run first and review its audit. Its approval lasts 15 minutes and is rejected if the candidate or link snapshot changes. A candidate is included only when both its physical payload blob and its legacy database fallback are absent. The cleanup rechecks each candidate and each library symlink or STRM target immediately before deletion.

For a verified Sonarr or Radarr library file, cleanup removes the media-file record and requests a replacement search through the matching Arr instance. It does **not** mark the original download failed or blocklist the release. Replacement searches use the configured per-media search budget; searches over the limit are withheld. Items are left untouched when an Arr instance is unreachable, ownership is ambiguous, or a link changes after the dry run.

Back up `/config` and pause Arr imports before running cleanup. Restore the missing `blobs/` directory instead if a matching backup still exists. This task is manual only and has no automatic schedule.

## Orphaned files

**Remove Orphaned Files** (Maintenance) deletes WebDAV files that are not linked from the library directory and are no longer tied to a SAB history row. Generated STRM sidecars owned by an orphaned item are deleted with it [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }. Supports dry run. Schedule optional daily cleanup — set container `TZ`. Direct WebDAV or rclone playback is not a library link, and neither are sidecars under the configured completed-downloads directory [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } — those are InfiniDysk's own outputs, not library links, even when the completed-downloads directory sits inside the Library Directory.

**Library Directory** must be the organized library root that contains your Arr-imported symlinks or STRMs (the parent of your Radarr/Sonarr root folders). It must be visible inside the InfiniDysk container. Do not point it at the rclone mount (`rclone.mount-dir`) or at `/completed-symlinks` — that folder is InfiniDysk's virtual view of current History rows, so scanning it cannot protect files after history is cleared. Remove Orphaned Files aborts (dry run included) when Library Directory is the mount or a path inside it.

History entries disappearing after an Arr import are client-initiated cleanup, not InfiniDysk deleting the mount: the Arr's **Remove Completed** setting, InfiniDysk **Automatic Queue Management** rules that call the Arr with `removeFromClient=true`, or a WebDAV DELETE of a release folder under `/completed-symlinks`. Mounted files stay streamable; only the History row is removed.

## NZB file backups

Optional copies of incoming NZBs (SABnzbd settings) prune by `api.nzb-backup-retention-days`.

[Deletion audit](deletion-audit.md) · [Maintenance](../configuration/maintenance.md)
