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
| Tolerate a few missing articles in video files [since 0.9.2](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.2){ .nzbdav-since } | `repair.healthcheck-lenient` | off | Marks lightly damaged video degraded instead of repairing it |
| Repair After Streaming Failures | `repair.auto-remove-after-failures` | `0` | Consecutive streaming failures before urgent repair; `0` = immediate repair |
| Auto-remove unlinked files only | `repair.auto-remove-unlinked-only` | on | At the threshold, linked items use *Arr remove-and-search instead of force-delete |
| Library Directory | `media.library-dir` | empty | Organized media path in container |

`repair.healthcheck-lenient` changes what a health check does when it confirms a missing article.
With it off, the first miss fails the file and starts a repair. With it on, an `.mp4`, `.m4v`,
`.mov`, `.mkv` or `.webm` file may be missing up to 2% of its articles, and never more than 64,
and is recorded as **degraded** rather than repaired. Players skip past gaps that small, so the
file stays watchable and *Arr is not asked to fetch a replacement. Past either limit the file fails
and repairs exactly as when the setting is off.

Only a file posted on its own qualifies. A video packed into a rar or 7z set does not, even though
NzbDAV lists it under the inner file's name, because a zero-filled gap breaks the extraction rather
than the picture. Nor does a video split across `.001` parts, which is safe in principle but shares
its storage shape with archive members closely enough that the two cannot be told apart reliably for
files imported by earlier versions. Disc images, playlists and other containers stay out as well.

Missing articles that sit next to each other are treated separately from the totals above. Three in
a row fails the file, because that is the point at which playback stops filling the gap with silence
and drops the stream, and a file that passed the check and then broke on playback would be the worst
of both. Whether a run is visible depends on the check finding both of its neighbours: at
**Complete** depth, or for files small enough to be checked in full, every run is seen. A shallower
check on a large file reads scattered articles rather than adjacent ones, so a run in the middle of
such a file can pass unnoticed.

The totals count against the whole file, not the subset a sampled check reads, so a shallow depth on
a large file can pass one carrying more missing articles than the numbers above name.

`repair.auto-remove-after-failures` applies only to streaming-triggered failures such as missing
articles and corrupt archives. With a value greater than `0`, NzbDAV waits for that many
consecutive failures before it starts an urgent repair. At the threshold, linked library items
trigger *Arr remove-and-search when **Auto-remove unlinked files only** is enabled; unlinked files
are removed. Disable that option to force-delete linked items at the threshold.

Successful full-file playback and a clean background health check reset the in-memory failure
count; a degraded result leaves it in place. The count resets when NzbDAV restarts, so it is
intentionally not a durable replacement for health checks.

[Health and repairs](../operations/health-repairs.md)
