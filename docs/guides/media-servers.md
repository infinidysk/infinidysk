# Media servers

## Plex

Use **Symlinks — Plex** import strategy and an [rclone mount](mounting-webdav.md).

1. Library folders should follow the symlinks under your media root (often via *Arr hardlink/move into `/mnt/media/...` that still points at `.ids` content).
2. Prefer analysis settings that do not thrash the entire WebDAV tree continuously.
3. Watch Overview **Active Reads** — unexpected sustained bandwidth often means library scans or VFS cache churn.

## Emby / Jellyfin

Use **STRM Files** when the server supports `.strm` playback:

1. Completed downloads dir shared with *Arr.
2. **Base URL** reachable from Emby/Jellyfin (same Docker network or public HTTPS).
3. WebDAV auth must remain valid for STRM URLs.

Alternatively, mount WebDAV with rclone and treat the library like Plex if you prefer filesystem entries.

## Path and permission tips

- Match `PUID`/`PGID` across InfiniDysk, rclone (`--uid`/`--gid`), and *Arr.
- Keep TLS in front of any URL Emby/Jellyfin fetches remotely.
- After strategy changes, use Maintenance tasks to recreate STRM files or convert to symlinks.

[Import strategies](import-strategies.md) · [Infinite library](../use-cases/infinite-library-arr.md)
