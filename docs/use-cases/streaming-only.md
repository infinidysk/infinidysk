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

Secure the UI and WebDAV the same way as any other deploy — TLS, strong passwords, no open port `3000` on the internet.
