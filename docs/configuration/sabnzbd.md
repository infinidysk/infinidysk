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
| Manual Upload Category (upload-time picker [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }) | `api.manual-category` | `uncategorized` | Queue page uploads; default for the category picker beside the Upload NZB button |
| Import Strategy | `api.import-strategy` | `symlinks` | Symlinks (Plex) / STRM (Emby/Jellyfin) |
| Rclone Mount Directory | `rclone.mount-dir` | env `MOUNT_DIR` or `/mnt/nzbdav` | When symlinks |
| Completed Downloads Dir | `api.completed-downloads-dir` | backend default under `/data` | When STRM |
| Base URL | `general.base-url` | `http://localhost:3000` | STRM / adapter absolute URLs |
| Ignored Files | `api.download-file-blocklist` | `*.nfo, *.par2, …` | Glob blocklist for mounts (`*` and `?`) |
| Filter sample videos [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since } | `api.sample-filter-enabled` | on | Discard videos with whole-word `sample`/`samples` under 20% of the largest video in the NZB |
| Behavior for Duplicate NZBs | `api.duplicate-nzb-behavior` | `increment` | increment / mark-failed |
| Trusted local hosts [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since } | `api.addurl-trusted-hosts` | env `TRUSTED_INTERNAL_HOSTS` | SSRF allowlist for private addurl |
| Fail downloads without video or audio | `api.ensure-importable-video` | on | Reject NZBs with no media files |
| Fail when non-media missing articles | inverse of `api.skip-non-video-on-missing-articles` | skip non-media by default | Media files (video/audio) always fail on missing articles; companion files are skipped unless this is enabled |
| Article health check categories | `api.ensure-article-existence-categories` | empty (off) | Per-category; may be slow |
| Article health check mode | `api.article-existence-check-mode` | `full` | Full or per-file sampled verification |
| Always send full History | `api.ignore-history-limit` | on | Ignore client history limit |
| Save backup copies of incoming NZBs | `api.nzb-backup-enabled` | off | On-disk `*.nzb` copies |
| Backup location | `api.nzb-backup-location` | — | By category |
| Keep NZB backups (days) | `api.nzb-backup-retention-days` | `30` | `0` = forever |

Queue capacity and admission limits are configured separately under
[Queue](queue.md). The default user agent for retrieving NZBs, including
matched `addurl` requests, is configured under [Indexers](indexers.md).

## Sampled article checks [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since }

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

[Import strategies](../guides/import-strategies.md) · [Queue settings](queue.md)
