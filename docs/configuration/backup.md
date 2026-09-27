# Backup and restore

Logical SQL dumps of databases, schedule/retention, upload/download/restore.

!!! tip "Headless ENV"

    Map schedule/retention keys to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`backup.schedule-time` → `NZBDAV_CONFIG__BACKUP__SCHEDULE_TIME`, minutes from midnight).
    Create / upload / restore **actions** remain out of the ENV overlay.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable daily backup | `backup.schedule-enabled` | off | `.sql` under config volume |
| Daily run time | `backup.schedule-time` | midnight (`0`) | Minutes from midnight; uses `TZ` |
| Keep newest backups | `backup.retention-count` | `5` | Prune non-preserved; `0` = no prune |

Actions: Create / Upload / Download / Preserve / Restore / Delete.

!!! warning

    In SQLite mode, dumps include `db.sqlite` and `warden.db` as SQL. In PostgreSQL mode, in-app backups include only the local `warden.db`; back up the PostgreSQL main database separately with `pg_dump`. Neither mode includes `metrics.sqlite` (metrics history) or `blobs/`. Copy `metrics.sqlite` from the config volume yourself if you want to keep metrics history; a restore leaves the current metrics database unchanged, even when an older backup still contains `metrics.sql`. Missing blobs after restore are reported in the UI. Restore replaces settings, queue, history, and WebDAV tree metadata in SQLite mode; creates a pre-restore safety backup; server restarts into maintenance.

!!! note "Migration database is disposable"

    Backups deliberately exclude `usenet-migration.db` (experimental Altmount migration wizard state and provenance). Deleting that file only loses migration bookkeeping — never mounted WebDAV content. If you restore a database backup mid-migration and the wizard shows many `evicted` releases, reset or forget migration data and re-scan; already-imported releases are re-detected and not resubmitted. See [Migrate from Altmount](../guides/altmount-migration.md).

Restoring after `SQLite Error 11` ("database disk image is malformed")? See [Database corruption](../operations/database-corruption.md).

[Backups and upgrades](../guides/backups-upgrades.md)
