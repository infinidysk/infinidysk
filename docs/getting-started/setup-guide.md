---
description: "Use InfiniDysk's guided setup for Plex symlinks or Emby/Jellyfin STRM playback, Arr ingestion, backups, and library health."
---

# Setup Guide <span class="nzbdav-since">since 1.3.0</span>

InfiniDysk opens **Setup Guide** after the first administrator signs in on a new
or upgraded installation. Complete it, or choose **Skip setup** to stop the
automatic prompt. You can run it again at any time from the main navigation.

The guide stages changes until **Review and apply**. Skip applies nothing, and a
validation failure does not save a partial configuration. Settings controlled by
`NZBDAV_CONFIG__...` remain read-only and name the environment variable to change.

## Library type

Choose the playback shape used for future imports:

| Choice | Recommended cache | Required values |
|--------|-------------------|-----------------|
| **Symlinks · Plex** | Bounded rclone VFS cache; InfiniDysk Segment Cache is disabled | Rclone mount directory; optional RC connection |
| **STRM · Emby/Jellyfin** | InfiniDysk Segment Cache is enabled | Completed Downloads Dir and media-server-reachable Base URL |

Changing strategy does not convert existing imports. Review the warning before
applying, then use the appropriate [Maintenance](../configuration/maintenance.md)
tool or a deliberate migration plan for existing files.

Segment Cache is analogous in purpose to rclone VFS caching for direct WebDAV
playback, but it is not the same read-ahead mechanism. Enabling or disabling it
requires an InfiniDysk restart.

## Symlink playback and rclone

The guide shows the recommended bounded sidecar flags, including
`--vfs-cache-mode=full`, `--buffer-size=0`, a `50G` cache cap, and
`--vfs-read-ahead=512M`. Rclone's defaults control read chunking.
It also walks through rclone RC notifications:

```text
--rc
--rc-addr=:5572
--rc-no-auth           # trusted isolated network only
```

Enter the matching RC host and credentials in InfiniDysk, then use **Test**. If
rclone exposes VFS statistics, the result includes its cache mode and read-ahead.
A failed or unavailable connection warns but does not block setup; manually
confirm the sidecar flags before continuing.

For a separate rclone sidecar, bind `:5572` so the InfiniDysk container can
reach it. `127.0.0.1:5572` is valid only when both processes share a network
namespace. On a network that is not isolated and trusted, replace `--rc-no-auth`
with `--rc-user` and `--rc-pass` and enter those credentials in the guide.

See [Mounting WebDAV](../guides/mounting-webdav.md) for the complete Compose example.

## Content ingestion

Select one or more methods:

- **Arr apps** — register Radarr or Sonarr with InfiniDysk, test each connection,
  and follow the displayed SABnzbd download-client values inside each Arr app.
- **Built-in Search** — finish configuring [Indexers](../configuration/indexers.md)
  and [Search Profiles](../configuration/profiles.md) afterward.
- **Manual NZB** — upload a smoke-test NZB from Queue after setup.

External connection tests are advisory because services may still be starting.
Required paths and URLs must validate before the guide can be completed.

## Backups and library health

The backup step can enable a daily schedule and set its run time and retention.
With a PostgreSQL main database, this schedule protects only the local auxiliary
SQLite stores; use PostgreSQL-native tooling for the main database.

**Library Directory** is the organized media root visible inside the InfiniDysk
container, usually the parent of Radarr and Sonarr root folders. It must not be
the rclone mount or `completed-symlinks`. You may defer it: health checks and
PAR2 repair still run, while linked-library replacement remains limited.

## Review and completion

Review lists current and proposed values without displaying secrets. It calls
out managed settings, strategy changes, deferred fields, and restart requirements.
After applying, follow the shown next steps to restart if needed, test Arr download
clients, or upload a small NZB.
