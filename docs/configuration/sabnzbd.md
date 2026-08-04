# SABnzbd

SABnzbd-compatible download client API used by Radarr/Sonarr. See also [API compatibility](../features/sab-api.md).

!!! tip "Headless ENV"

    Map any config key in the table to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`api.categories` → `NZBDAV_CONFIG__API__CATEGORIES`).

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| API Key | `api.key` | from `FRONTEND_BACKEND_API_KEY` if unset | *Arr download client auth |
| Categories | `api.categories` | env/`audio,software,tv,movies` | Letters/numbers/dashes |
| Manual Upload Category | `api.manual-category` | `uncategorized` | Queue page uploads |
| Import Strategy | `api.import-strategy` | `symlinks` | Symlinks (Plex) / STRM (Emby/Jellyfin) |
| Rclone Mount Directory | `rclone.mount-dir` | env `MOUNT_DIR` or `/mnt/nzbdav` | When symlinks |
| Completed Downloads Dir | `api.completed-downloads-dir` | backend default under `/data` | When STRM |
| Base URL | `general.base-url` | `http://localhost:3000` | STRM / adapter absolute URLs |
| Ignored Files | `api.download-file-blocklist` | `*.nfo, *.par2, …` | Glob blocklist for mounts (`*` and `?`) |
| Filter sample videos [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since } | `api.sample-filter-enabled` | on | Discard videos with whole-word `sample`/`samples` under 20% of the largest video in the NZB |
| Behavior for Duplicate NZBs | `api.duplicate-nzb-behavior` | `increment` | increment / mark-failed |
| User Agent | `api.user-agent` | env/default | `addurl` NZB fetch |
| Trusted local hosts [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since } | `api.addurl-trusted-hosts` | env `TRUSTED_INTERNAL_HOSTS` | SSRF allowlist for private addurl |
| Fail downloads without video | `api.ensure-importable-video` | on | Reject non-video NZBs |
| Fail when non-video missing articles | inverse of `api.skip-non-video-on-missing-articles` | skip non-video by default | |
| Article health check categories | `api.ensure-article-existence-categories` | empty (off) | Per-category; may be slow |
| Article health check mode | `api.article-existence-check-mode` | `full` | Full or per-file sampled verification |
| Maximum queued jobs | `queue.max-items` | `0` (unlimited) | Reject new submissions at this queue depth |
| Queue resume threshold | `queue.resume-threshold` | `0` (same as maximum) | Resume admission at or below this depth |
| Always send full History | `api.ignore-history-limit` | on | Ignore client history limit |
| Save backup copies of incoming NZBs | `api.nzb-backup-enabled` | off | On-disk `*.nzb` copies |
| Backup location | `api.nzb-backup-location` | — | By category |
| Keep NZB backups (days) | `api.nzb-backup-retention-days` | `30` | `0` = forever |

[Import strategies](../guides/import-strategies.md)

## Queue admission control [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

Set `queue.max-items` to prevent a large automatic *Arr search from adding an
unbounded number of NZBs at once. At the limit, InfiniDysk returns the standard
SAB-compatible `{"status": false, "error": "..."}` response without storing the
NZB. Sonarr and Radarr treat this as a temporarily unavailable download client
and keep automatic-search releases pending for a later retry.

`queue.resume-threshold` adds hysteresis: after the queue reaches the maximum,
new submissions remain blocked until queue depth falls to this value. Set it to
`0` to resume as soon as the queue falls below the maximum. Duplicate
submissions that replace an already queued item remain allowed because they do
not increase queue depth.

For reference, see the
[SABnzbd API response format](https://sabnzbd.org/wiki/advanced/api) and
[Sonarr's SABnzbd response handling](https://github.com/Sonarr/Sonarr/blob/develop/src/NzbDrone.Core/Download/Clients/Sabnzbd/SabnzbdProxy.cs).

## Sampled article checks [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

Categories selected under `api.ensure-article-existence-categories` use a full
article check by default, preserving existing behavior. Set
`api.article-existence-check-mode` to `sampled` to check the first and last
segments plus an evenly spaced selection of middle segments in each important
file. Sampling is applied per file so every file's tail is covered; this catches
common truncated or partially removed releases without a full STAT sweep.

Small files (currently up to roughly 8,000 segments) are still checked in full.
The sampled mode uses the same standard-depth selection as background health
checks. It is a screen rather than a guarantee: urgent repair remains the
backstop if an article disappears later or STAT succeeds while BODY fails.
