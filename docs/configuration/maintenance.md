# Maintenance

Database housekeeping, scheduled orphan cleanup, and one-off tools.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm).
    Schedule times are **minutes from midnight** in container `TZ`
    (`180` = 03:00). One-off maintenance **tasks** remain out of the ENV overlay.

## Settings

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Vacuum on startup | `db.is-startup-vacuum-enabled` | off | Reclaim SQLite space; may slow start |
| SAB history retention (days) | `database.history-retention-days` | `90` | Does not delete WebDAV; `0` = keep all |
| Health-check retention (days) | `database.healthcheck-retention-days` | `30` | `0` = keep all |
| Raw fetch-event retention (hours) [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since } | `metrics.fetch-retention-hours` | `24` | Raw `SegmentFetches` / `FailoverMisses`; rollups kept; `0` = rollup-only (~1 h floor) |
| Enable daily orphan cleanup | `maintenance.remove-orphaned-schedule-enabled` | off | Remove Orphaned Files schedule |
| Daily run time | `maintenance.remove-orphaned-schedule-time` | midnight (`0`) | Minutes from midnight; uses container `TZ` |

## Tasks (actions)

| Task | Purpose | Caution |
|------|---------|---------|
| Remove Orphaned Files | Drop WebDAV files not linked from library | Permanent; dry run available. Library Directory must be the organized library, not the rclone mount |
| Prune Completed History | Remove completed SAB history rows | History-only; mounts stay; unlinked mounts become eligible for orphan cleanup |
| Rename Windows-Invalid Paths | Sanitize existing names | Needs Windows-safe paths; backup + dry run |
| Convert STRM → Symlinks | Strategy migration | Needs library dir + rclone mount |
| Recreate STRM Files [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | Refresh sidecars | Needs STRM output enabled + completed dir + base URL |
| Migrate blobs to blobstore | Background optimization | Usually automatic |
| Reset Health-Check Statistics | Clear HC history | Cannot undo |
| Reset Overview Statistics [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since } | Clear overview metrics | Cannot undo |

[Retention](../operations/retention-cleanup.md) · [Deletion audit](../operations/deletion-audit.md)
