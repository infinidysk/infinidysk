# First run

Open `http://your-server:3000` after the container is healthy.

After creating the administrator account, InfiniDysk opens the guided
**[Setup Guide](setup-guide.md)** [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }.
It configures import strategy, the recommended cache layer, rclone or STRM paths,
content ingestion, scheduled backups, and Library Directory. You may skip it and
use the manual Settings sequence below; **Setup Guide** remains available in the
main navigation.

!!! tip "Headless ConfigItems vs first-run account"

    You can pre-seed Usenet, WebDAV, *Arr, and other **Settings** values with
    [`NZBDAV_CONFIG__...`](../configuration/headless.md) before the first UI visit.
    The **admin username/password** for the web UI is still created here (or via your
    existing account) — that bootstrap is **not** part of the ENV overlay. If you lose
    those credentials later, set `RESET_ADMIN_PASSWORD=true`, restart once, re-onboard,
    then remove the variable — see [Troubleshooting → Locked out of the web UI](../guides/troubleshooting.md#locked-out-of-the-web-ui).
    Warden sources and database restore actions are also separate domains.

## 1. Create the admin account

Set username and password for the web UI. Session cookies can be hardened later with `SECURE_COOKIES=true` behind HTTPS.

## 2. Usenet (`Settings` → `Usenet`)

| Setting | Guidance |
|---------|----------|
| Host / Port | Provider NNTP endpoint (often `563` with SSL) |
| Username / Password | Provider credentials |
| Provider Connection Limit | At or below your account allowance |
| Transfer Connections | Leave blank for legacy scheduling, or use Auto-tune to set it |
| Type | **Pool Connections** for primary accounts |
| Use SSL | On for remote providers |
| Storage group | Optional — same label for resellers that share upstream storage |

Click **Test** / **Auto-tune** when available. See [Usenet settings](../configuration/usenet.md). Skip this step when providers are already supplied via [headless ENV](../configuration/headless.md).

## 3. WebDAV (`Settings` → `WebDAV`)

| Setting | Guidance |
|---------|----------|
| WebDAV User | Dedicated username (default `admin`) |
| WebDAV Password | Required for rclone, AIOStreams, and many players |
| Enforce Read-Only | Leave on unless you need deletes from clients |

See [WebDAV settings](../configuration/webdav.md).

## 4. Streaming (`Settings` → `Streaming`)

The defaults are a safe starting point. If playback is slow or stalls, tune
connection allocation, timeouts, buffering, and the segment cache here. See
[Streaming settings](../configuration/streaming.md).

## 5. Import strategy (`Settings` → `SABnzbd`)

| Strategy | Best for | What to set |
|----------|----------|-------------|
| **Symlinks — Plex** | Plex / real filesystem entries | **Rclone Mount Directory** (e.g. `/mnt/remote/nzbdav`) + [rclone sidecar](../guides/mounting-webdav.md) |
| **STRM Files — Emby/Jellyfin** | Emby/Jellyfin `.strm` playback | **Completed Downloads Dir** + **Base URL** reachable by the media server |

Copy the **API Key** from this page — *Arr download clients need it.

Queue concurrency and admission limits are under
[Settings → Queue](../configuration/queue.md).

## 6. Smoke test

1. Upload a small `.nzb` on the **Queue** page (or send one from an indexer).
2. Wait until it reaches history / mounts under Explore → `content`.
3. Open or download a video file to confirm streaming.

!!! tip "Active Reads"

    Overview **Active Reads** lists any WebDAV byte fetch. Sustained bandwidth with nobody watching often means rclone VFS thrash or media-server analysis.

## Next

- [Connect Radarr/Sonarr](connect-arr.md)
- [Import strategies](../guides/import-strategies.md)
- [Configuration reference](../configuration/index.md)
- [Headless environment configuration](../configuration/headless.md)
