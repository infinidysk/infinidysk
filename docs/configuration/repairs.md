# Repairs

Background health monitoring, PAR2 reconstruction, and replacement of unhealthy library items.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`repair.enable` → `NZBDAV_CONFIG__REPAIR__ENABLE`). A Library Directory and configured
    [*Arr instances](arrs.md) are optional; they are only needed to replace linked library items.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Background Repairs [since 1.2.5](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.5){ .nzbdav-since } | `repair.enable` | off | Enables health checks, PAR2, and damage tolerance; Library Directory + *Arr are only needed for linked-item replacement |
| Health Check Concurrency [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `repair.healthcheck-concurrency` | `50` | Aggregate NNTP verification-connection limit shared by background checks and queue article validation (1–200, capped by pooled provider capacity) |
| Health Check Workers [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `repair.healthcheck-workers` | `1` | Library files checked at once (1–8). All workers share Health Check Concurrency; this setting never multiplies it |
| Health Check Depth | `repair.healthcheck-depth` | `standard` | standard / enhanced / deep / complete |
| Check older releases less thoroughly [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since } | `repair.healthcheck-aging` | off | Aging taper |
| Repair After Streaming Failures | `repair.auto-remove-after-failures` | `0` | Consecutive streaming failures before urgent repair; `0` = immediate repair |
| Auto-remove unlinked files only | `repair.auto-remove-unlinked-only` | on | At the threshold, linked items are removed and blocklisted through *Arr instead of force-deleted |
| Degraded damage tolerance [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since } | `repair.degraded-tolerance-enabled` | on | Keep slightly damaged videos playable instead of replacing the release |
| Track corrupt articles during playback [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since } | `repair.corruption-tracking-enabled` | on | Record streaming-confirmed corrupt articles, include them in health classification, and skip the retry storm on later reads |
| Max consecutive missing segments | `repair.degraded-max-consecutive-missing` | `2` | Longest tolerable run of adjacent holes (1–2) |
| Max total missing segments | `repair.degraded-max-total-missing` | `5` | Total tolerable holes per file (1–1000) |
| Max missing data (% of file) | `repair.degraded-max-missing-byte-percent` | `1.0` | Tolerable hole share of file bytes (0.01–50) |
| Library Directory | `media.library-dir` | empty | Organized library root in the container — parent of your Arr root folders. Never the rclone mount or `/completed-symlinks` |

## Parallel health checks

`repair.healthcheck-workers` controls how many library files can make progress at once.
`repair.healthcheck-concurrency` remains the connection-pressure control, but is now shared by
every background worker and queue article-existence validation. For example, four workers with a
50-connection limit share those 50 admissions rather than receiving 50 each.

Existing numeric `repair.healthcheck-concurrency` values remain valid during upgrades, including
headless values outside the current effective range. InfiniDysk safely limits them at runtime to at
least one, at most 200, and no more than the total pooled provider capacity. Existing Compose files
therefore do not need to be changed before starting the new version.

New background checks continue to wait while queue items are active. If queue work starts while a
health check is already running, both paths share the aggregate admission limit and queue validation
receives released capacity before waiting background verification. Repair and urgent-repair behavior
inside each file check is otherwise unchanged.

Provider auto-tune pauses new health checks and waits for active checks to release their verification
connections before opening benchmark connections, so parallel workers cannot skew the measured limit.

## Re-check after provider changes [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

Changing Usenet providers can affect which library files are available. After saving provider changes,
InfiniDysk offers to queue your library for a health re-check. This requires Background Repairs to be
enabled; urgent repairs already queued from streaming failures keep their priority.

!!! note "Streaming failure repair requires Background Repairs"

    **Repair After Streaming Failures** (`repair.auto-remove-after-failures`) only takes effect when
    **Enable Background Repairs** (`repair.enable`) is on. A Library Directory and \*Arr are only
    needed to replace linked library items. Without them, PAR2 can still reconstruct the file and
    threshold-based removal can delete unlinked items.

`repair.auto-remove-after-failures` applies only to streaming-triggered failures such as missing
articles, corrupt archives, and seeks that find missing or truncated article data. With a value
greater than `0`, InfiniDysk waits for that many consecutive failures before it starts an urgent
repair. At the threshold, linked library items are removed and their original downloads are marked
failed in \*Arr when **Auto-remove unlinked files only** is enabled. \*Arr blocklists those releases
and applies its configured failed-download redownload policy. Unlinked files are removed. Disable
that option to force-delete linked items at the threshold. With the default value `0`, failed
unlinked files are kept and surfaced as **Action needed**; set a value greater than `0` to
auto-remove them after repeated failures.

Successful full-file playback and a successful background health check reset the in-memory failure
count. The count resets when InfiniDysk restarts, so it is intentionally not a durable replacement for
health checks.

## Degraded damage tolerance [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

Health checks of plain video files no longer treat every missing Usenet segment as fatal.
When a check covers **every** segment of an eligible file (files up to 8000 segments at any
depth, or any file at **Complete** depth), InfiniDysk sweeps up all confirmed misses and
classifies the damage instead of aborting on the first one. With `repair.healthcheck-aging`
enabled, releases old enough to be sampled are not classified.

- **Healthy** — no confirmed holes. A segment whose primary article is gone but that is still
  fetchable through a fallback Message-Id counts as servable, not a hole.
- **Degraded** — holes within all three caps (longest consecutive run, total count, and share
  of the file's bytes) in a container that tolerant decoders can resync past. The file stays
  mounted, playback zero-fills the gaps, and **no Arr repair is triggered**. The confirmed
  holes are recorded on the item so the status survives restarts; later playback fills them
  without sending a provider BODY request. A local PAR2 patch or segment-cache entry still wins
  over a gap fill, and the next full health sweep detects a provider-side recovery.
- **Failed** — over any cap, any hole in an unsafe layout, or an unrecognized container.
  These take the normal repair path (PAR2 reconstruction first when enabled, then Arr
  remove-and-replace when a linked library item has an enabled Arr instance). Without an Arr
  replacement path, the file remains mounted as **Action needed**.

Eligible containers: `.mkv`, `.mk3d`, `.webm`, `.ts`, `.m2ts` (resync at cluster/packet
boundaries) and `.mp4`/`.m4v`/`.mov`, whose layout is probed once from a bounded read of the
file head: fast-start and fragmented MP4 can tolerate mid-stream holes, while **moov-at-end
MP4 is fatal on any segment loss** because the moov atom lives in the file tail — holes
overlapping the moov atom region at the start of a fast-start MP4 are also fatal. Offset-
sensitive formats (`.avi`, …) and non-payload files are never classified; they keep the
legacy abort-on-first-miss behavior. A missing first segment is always fatal.

Degraded verdicts compose with the rest of the repair pipeline:

- **PAR2 first.** When PAR2 gap repair (`repair.par2-enabled`) is enabled and preferred,
  reconstruction is attempted with the full hole list before any verdict; success records a
  healthy, PAR2-repaired result and clears any recorded holes.
- **Rechecks can escalate or recover.** Degraded files stay on the normal age-doubling
  recheck schedule. If damage grows past a cap, the next check fails the file and repair
  proceeds; if the missing articles reappear (provider-side restoration), the record clears
  itself and the file returns to healthy.
- **Streaming failures still count.** A degraded verdict does not reset the consecutive
  streaming-failure counter, so genuinely unplayable files still escalate toward
  `repair.auto-remove-after-failures`.

Degraded files appear on the [Health page](../operations/health-repairs.md) with a warning
badge, a dedicated history filter, and an overview stat card.

## Realtime corruption detection [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

A corrupt-but-present article (right size, STAT succeeds, yEnc CRC fails) used to play as
silent garbage. InfiniDysk now detects that on the playback path:

1. **Detect** — yEnc CRC failures surface as corrupt articles, including trailer-CRC
   corruption on exact-size segments in the unbuffered first-segment reader.
2. **Re-fetch / failover** — the reader retries across providers, then sibling donor
   Message-Ids, then gap-fills a known-length hole so later bytes stay aligned. There is
   **no synchronous PAR2** on the read path (reconstruction stays in the background).
3. **Record** — persistently corrupt segment IDs are stored on the file payload when
   `repair.corruption-tracking-enabled` is on (the default whenever Background Repairs is
   on). Later reads of those IDs probe once instead of repeating the retry storm.
4. **Classify** — full-coverage health sweeps union remaining recorded corruption with
   STAT holes, so a present-but-corrupt file is no longer reported Healthy.
5. **Escalate** — when playback actually breaks, the same streaming-failure path used for
   missing articles runs: PAR2-first when enabled, then *Arr remove-and-blocklist for linked
   library items with an enabled Arr instance.

Disable **Track corrupt articles during playback** if you need the previous retry-only
behavior. Playback-breaking corruption still schedules repair whenever Background Repairs
is on.

[Health and repairs](../operations/health-repairs.md)

## Replacement-loop protection [since 0.9.4](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.4){ .nzbdav-since }

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
