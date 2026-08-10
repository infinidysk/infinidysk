# Database corruption

SQLite reports corruption as `SQLite Error 11: 'database disk image is malformed'`. It means the database file itself (`/config/db.sqlite`) is damaged — it is **not** a bug in a specific query, and retrying never heals it.

## Symptoms

- Repeating log lines such as `Error processing blob cleanup queue. Reason: Database file is corrupt ...` from background services.
- A startup integrity check failure [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }: after migrations, the backend runs `PRAGMA quick_check` on the main database and logs an Error with recovery guidance when the file is damaged. Startup deliberately continues so you can still reach the restore UI.
- WebDAV listings, queue, or history views failing or returning stale data.

## Causes

Corruption is almost always environmental:

- `/config` on a **network filesystem** (SMB/NFS) that does not honor SQLite's locking and `fsync` requirements — the most common cause.
- **Unclean shutdowns**: container killed mid-write (`docker kill`, OOM killer, power loss).
- **Failing or full storage** underneath `/config`.

InfiniDysk runs SQLite in WAL mode with `synchronous=NORMAL`, which mitigates but cannot prevent corruption on filesystems that lie about durability.

## Recovery

### Restore from a backup (recommended)

Use the guided restore under **Settings → Backup & Restore** — see [Backup and restore](../configuration/backup.md). Restore replaces settings, queue, history, and WebDAV tree metadata, creates a pre-restore safety backup, and restarts into maintenance. Blobs under `/config/blobs/` are not part of SQL dumps; missing blobs are reported in the UI after the restore.

If you do not have a backup, enable **daily backups** after recovering so the next incident is a one-click restore.

### Advanced: salvage with the SQLite CLI

On a copy of the file, with the container stopped:

```bash
sqlite3 /config/db.sqlite ".recover" | sqlite3 /config/db-recovered.sqlite
```

`.recover` extracts whatever rows are still readable into a fresh database. Expect partial data loss: rows on damaged pages are skipped. Point `/config` at the recovered file only after verifying it opens cleanly (`PRAGMA integrity_check` should print `ok`).

### Last resort: fresh start

Stop the container, move `db.sqlite*` aside, and start again. A fresh database loses settings, queue, history, and the WebDAV tree metadata (the content itself lives on Usenet and can be re-added).

## Prevention

- Keep `/config` on **local disk**, not a network share.
- Enable **daily backups** with a retention count under **Settings → Backup & Restore**.
- Stop the container gracefully (`docker stop`, not `docker kill`) so SQLite can checkpoint the WAL.
