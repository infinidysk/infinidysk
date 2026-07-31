# Repairs

Background health monitoring and replacement of unhealthy library items.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`repair.enable` → `NZBDAV_CONFIG__REPAIR__ENABLE`). Enabling repairs via ENV
    also needs `media.library-dir` and *Arr instances.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Background Repairs | `repair.enable` | off | Needs library dir + *Arr |
| Health Check Concurrency [since 0.9.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0){ .nzbdav-since } | `repair.healthcheck-concurrency` | `50` | Worker ceiling for concurrent STAT checks; capped by the provider pool. Actual contention with playback is governed by provider-pool admission and **Streaming Priority** |
| Health Check Depth | `repair.healthcheck-depth` | `standard` | standard / enhanced / deep / complete |
| Check older releases less thoroughly [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since } | `repair.healthcheck-aging` | off | Aging taper |
| Repair After Streaming Failures | `repair.auto-remove-after-failures` | `0` | Consecutive streaming failures before urgent repair; `0` = immediate repair |
| Auto-remove unlinked files only | `repair.auto-remove-unlinked-only` | on | At the threshold, linked items are removed and blocklisted through *Arr instead of force-deleted |
| Library Directory | `media.library-dir` | empty | Organized media path in container |

`repair.auto-remove-after-failures` applies only to streaming-triggered failures such as missing
articles and corrupt archives. With a value greater than `0`, NzbDAV waits for that many
consecutive failures before it starts an urgent repair. At the threshold, linked library items are
removed and their original downloads are marked failed in *Arr when **Auto-remove unlinked files
only** is enabled. *Arr blocklists those releases and applies its configured failed-download
redownload policy. Unlinked files are removed. Disable that option to force-delete linked items at
the threshold.

Successful full-file playback and a successful background health check reset the in-memory failure
count. The count resets when NzbDAV restarts, so it is intentionally not a durable replacement for
health checks.

## Replacement-loop protection [since 0.9.4](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.4){ .nzbdav-since }

When *Arr imports a download instantly (for example over an rclone mount), a broken release can
import successfully before any health check runs. Marking an already-imported download failed does
not reliably blocklist it, so *Arr could re-grab the identical release and loop. Two safeguards
break that cycle:

- **Fail re-grabs before import.** Releases rejected by repair are remembered: when repair removes
  a broken download and marks it failed, the release's article ids are recorded (as are articles
  found definitively missing while downloading or streaming). A re-grabbed NZB containing any of
  them fails within milliseconds while still in the download queue. *Arr sees a failed download
  before import, blocklists the release, and moves on to a different one. The memory is in-process
  and resets on restart; a loop that survives a restart is stopped again after one extra cycle.
- **Per-file repair rate limit.** After repair has removed 3 downloads for the same library file
  (the same episode or movie file path — not the whole series or folder) within 6 hours, further
  repairs for that file are deferred for a day and surfaced as **Action needed** in the health
  screen instead of triggering another replacement.

[Health and repairs](../operations/health-repairs.md)
