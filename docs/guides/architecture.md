---
description: "How InfiniDysk streams media from Usenet providers through a virtual WebDAV filesystem without downloading files to disk."
---

# Architecture

InfiniDysk sits between download clients / search and Usenet providers, exposing a virtual filesystem and SAB-compatible API.

```mermaid
flowchart TB
  subgraph clients [Clients]
    Arr[Sonarr_Radarr]
    Browser[Admin_UI]
    Player[Plex_Jellyfin_rclone]
    Stremio[Stremio_AIOStreams]
  end
  subgraph nzbdav [InfiniDysk]
    SAB[SAB_API]
    Queue[Queue_processor]
    WebDAV[WebDAV_store]
    UI[Frontend_proxy]
  end
  subgraph usenet [Usenet]
    NNTP[NNTP_providers]
  end
  Arr -->|addfile_addurl| SAB
  SAB --> Queue
  Queue --> WebDAV
  Browser --> UI
  UI --> SAB
  UI --> WebDAV
  Player -->|HTTP_range| WebDAV
  WebDAV --> NNTP
  Stremio -->|API_WebDAV| UI
```

## Two common flows

### Automation (*Arr + media server)

1. Radarr/Sonarr sends an NZB to InfiniDysk as a SABnzbd download client.
2. InfiniDysk mounts the release on WebDAV without downloading the full file.
3. Import artifact:
   - **Symlinks** — entries under `completed-symlinks`; rclone turns them into filesystem links into `.ids`.
   - **STRM** — small `.strm` files with authenticated streaming URLs.
4. *Arr imports into the library; the media server reads through the link/URL → InfiniDysk → Usenet.

### On-demand (Stremio)

1. AIOStreams finds a release via Newznab.
2. NZB is mounted through InfiniDysk's API.
3. Playback URL (often proxied by AIOStreams) streams from WebDAV.

## Processes and ports

| Process | Default | Role |
|---------|---------|------|
| Frontend | `:3000` | Admin UI, auth, proxy for WebDAV + `/api` + `/ws` |
| Backend | `:8080` (internal) | WebDAV, queue, SAB API, SQLite under `CONFIG_PATH` |

Persistent state lives under `/config` (DB, settings, blobs, backups).

## Related

[Import strategies](import-strategies.md) · [Features overview](../features/index.md) · [Environment variables](../configuration/environment-variables.md) · [Code boundaries](../decisions/0001-code-boundaries.md)
