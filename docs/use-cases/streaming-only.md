# Streaming only

Use InfiniDysk without building a traditional *Arr library.

## Patterns

- **Explore / Queue** — manual NZB upload, play from Explore or copy `/view` links.
- **Stremio + AIOStreams** — [Stremio guide](../guides/stremio.md).
- **Search profiles** — Addon or JSON adapters for on-demand clients — [Indexer search](../features/indexer-search.md).
- **Watchtower** — keep a list warm without importing to Plex — [Watchtower](../features/watchtower.md).

## Minimal settings

1. Usenet providers + WebDAV password.
2. Indexers (if searching).
3. Watchdog on for playback failover.
4. Skip rclone unless you want a local FUSE mount for VLC/etc.

## Repairs without *Arr [since 1.2.5](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.5){ .nzbdav-since }

Enable **Background Repairs** to run health checks, reconstruct recoverable gaps from PAR2 parity,
and keep slightly damaged video files playable. A Library Directory and *Arr are only needed to
replace linked library items. To automatically remove broken unlinked items after playback failures,
set **Repair After Streaming Failures** above `0`; its default (`0`) keeps the item and marks it
**Action needed**.

Secure the UI and WebDAV the same way as any other deploy — TLS, strong passwords, no open port `3000` on the internet.
