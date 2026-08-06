# Backups and upgrades

## In-app Backup & Restore

**Settings → Backup & Restore** dumps SQLite databases (`db.sqlite`, `metrics.sqlite`, `warden.db`) as `.sql` under `{CONFIG_PATH}/backups/`.

- Create on demand, schedule daily, set retention, preserve important snapshots.
- Download as zip; upload a previous zip/`.sql`.
- Restore stages import, creates a pre-restore safety backup, then restarts into maintenance to swap DBs.

!!! warning "Blobs are not in the SQL dump"

    Mounted content depends on `{CONFIG_PATH}/blobs/`. Back up that directory separately if you need a full restore of WebDAV content.

See [Backup settings](../configuration/backup.md).

## Config volume

Also back up:

- `/config` settings DB and session key
- `blobs/` for NZB payloads still referenced by mounts
- Optional NZB backup copies if enabled under SABnzbd settings

## Upgrades

```bash
docker compose pull
docker compose up -d
```

Database migrations apply automatically on startup. **Back up `/config` before upgrading across versions that include schema migrations.** Irreversible migrations appear under Breaking Changes in the changelog; routine additive migrations are noted in the release announcement instead.

Coming from nzbdav-dev `v0.6.4` or a community fork? See [Migration paths](../getting-started/migration.md).

Tags: `latest`, version tags, `dev` (unversioned tip snapshot), and `rc` (latest release candidate) — see [GitHub Releases](https://github.com/infinidysk/infinidysk/releases) and [Changelog](../community/changelog.md).

## Watchtower / Arr updates

If you auto-update containers, pin a known-good tag or use the in-app backup schedule before unsupervised upgrades.
