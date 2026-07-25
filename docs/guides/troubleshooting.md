# Troubleshooting

## Container unhealthy / won't start

- Check `docker logs nzbdav` for migration or backend health failures.
- Ensure `CONFIG_PATH` (`/config`) is writable by `PUID`/`PGID`.
- Frontend `/healthz` should pass during long migrations; backend `/health` must eventually succeed.

## WebDAV or playback fails

- Confirm WebDAV username/password.
- Behind a proxy: TLS, `/ws` Upgrade, `SECURE_COOKIES`, Base URL / `TRUST_PROXY`.
- Overview **Active Reads**: unexpected traffic → rclone VFS or media-server scans.
- Try disabling segment cache or adjusting Max Download Connections — [WebDAV](../configuration/webdav.md).

## Overview “Served” spikes overnight

**Served** is actual bytes delivered over WebDAV/HTTP, not declared file sizes. Overnight spikes with Plex deep-analysis jobs disabled usually mean something is still reading the mount (library scans, Bazarr, backups) and rclone is amplifying each probe:

- Prefer `--buffer-size=0M` and modest `--vfs-read-ahead` (see [Mounting WebDAV](mounting-webdav.md))
- Prefer a long `--vfs-cache-max-age` and a large `--vfs-cache-max-size` so scan probes are not re-downloaded every night
- Confirm **Settings → Rclone Server** can reach RC (`--rc-addr` must not be `127.0.0.1` across containers)

## *Arr won't import

- Paths must match exactly between NzbDAV completed path and *Arr containers.
- Symlinks: rclone mount healthy? `ls` shows `completed-symlinks` and `.ids`?
- STRM: Base URL reachable from Emby/Jellyfin?
- Check Automatic Queue Management rules — [Arrs](../configuration/arrs.md).

## `addurl` SSRF / private indexer [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since }

Allow Docker DNS or LAN hosts under **Trusted local hosts** — [SABnzbd API](../features/sab-api.md).

## Why did files disappear?

See [Deletion audit](../operations/deletion-audit.md) — history retention ≠ deleting mounts; orphan cleanup and *Arr actions can remove content.

## Provider / missing articles

- Circuit breaker may pause a bad provider — check Usenet settings and Overview.
- Storage groups skip sibling resellers after a miss — only group identical upstream storage.
- Health/repairs can replace unhealthy library items — [Health and repairs](../operations/health-repairs.md).

## Still stuck

Generate a [technical support pack](../configuration/support.md) from **Settings → Support**,
review it for personal paths and names, then [open an issue](https://github.com/nzbdav/nzbdav/issues).
For local stream debugging, see [Contributing](../community/contributing.md).
