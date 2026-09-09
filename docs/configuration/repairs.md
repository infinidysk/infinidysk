# Repairs

Background health monitoring, PAR2 reconstruction, and replacement of unhealthy library items.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`repair.enable` → `NZBDAV_CONFIG__REPAIR__ENABLE`). A Library Directory and configured
    [*Arr instances](arrs.md) are optional; they are only needed to replace linked library items.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Background Repairs [since 1.2.5](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.5){ .nzbdav-since } | `repair.enable` | on [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | Enables health checks, PAR2, and damage tolerance; Library Directory + *Arr are only needed for linked-item replacement. An explicit `false` still disables them. Existing installs that already saved `false` stay off. |
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
| Health-check schedule [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `repair.healthcheck-schedule` | empty (always on) | JSON weekly windows for **new** routine health checks |
| Repair quiet hours [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `repair.action-schedule` | empty (always on) | JSON weekly windows for starting repairs |
| Library Directory | `media.library-dir` | empty | Organized library root in the container — parent of your Arr root folders. Never the rclone mount or `/completed-symlinks` |

## PAR2 gap repair [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

PAR2 can reconstruct missing or corrupt Usenet articles using parity in the retained NZB.
It does not require a Library Directory or an *Arr instance; that prerequisite was removed
in 1.2.5. Both Background Repairs and PAR2 gap repair must be enabled.

| Control | Config key | Default | Effective range or behavior |
|---------|------------|---------|-----------------------------|
| PAR2 gap repair | `repair.par2-enabled` | on | Background Repairs is the master switch |
| Prefer PAR2 over *Arr replacement | `repair.par2-preferred-over-arr` | on | Try parity first; without an *Arr replacement path, PAR2 is still attempted |
| Maximum missing slices | `repair.par2-max-missing-slices` | `8` | 1-64; applies to the combined unavailable slices in the recovery set, including damage found during source checks |
| Maximum release size (GiB) | `repair.par2-max-release-gb` | `16` | 1-200; total recoverable source-volume bytes, checked before content reads |
| Maximum repair memory (MiB) | `repair.par2-max-memory-mb` | `256` | 64-2048; conservative repair-owned working-memory budget, not total process RSS |
| Maximum patch store (GiB) | `repair.par2-max-patch-gb` | `4` | 1-100; cataloged patch-body capacity; restart to apply a changed store capacity |
| PAR2 fetch concurrency | `repair.par2-fetch-concurrency` | `2` | 1-8; upper bound for repair fetches, not a promise to parallelize every phase |
| Failure cooldown (hours) | `repair.par2-failure-cooldown-hours` | `6` | 1-168; failed/infeasible attempts are deferred, with at most three provider-repair attempts per job |

Health checks and attributed streaming failures can request repair. Playback gap/corruption
reports enqueue background work; playback never waits for reconstruction. Until a patch is
available, reads retain their existing safe gap-fill or failure behavior. A gap fill does not
guarantee that an archive or media player can continue playback.

Repair checks PAR2 packet hashes and source-slice checksums, reconstructs unavailable slices,
and verifies every affected volume's whole-file MD5 before publishing any segment patches.
Only validated posted bytes are stored. Sources and parity are read through Usenet without
downloading or extracting a whole release to disk. One repair owner runs at a time across
health and playback requests; queued jobs wait without multiplying the memory budget.

A health check or playback report that finds damage while another repair already owns that
slot waits only briefly, then defers the file for another attempt in 15 minutes and releases
its health-check worker. Deferred files keep their place in the repair queue and are never
sent to *Arr replacement on that basis, because a busy repair slot says nothing about whether
parity can fix the file. Without this, a single unrepairable release could hold every
health-check worker and stop the library scan entirely.

The budget includes NZB/PAR2 metadata, source windows, reconstruction buffers, and staged
patches. Discovery also limits metadata candidates to 128, metadata scanning to 512 MiB,
and unnamed magic probes to 64 candidates with at most 64 bytes per probe. Recovery scanning
has a separate 512 MiB limit, including foreign packets. Source matching permits at most
100,000 candidate/descriptor comparisons per job. Identity probing shares a 512 MiB work
budget (article reads and bytes hashed) and 100,000-request limit across all candidates;
recent article bytes are reused while proving adjacent slices. These limits do not restrict
identity proofs to the first few slice positions. A recovery set must contain 1-32,768 input
slices. An unsupported layout, ambiguous identity, exhausted limit, or insufficient parity
produces a clear infeasible reason rather than guessed bytes.

## RAR and multipart repair [since 1.4.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.0){ .nzbdav-since }

Targeted repair supports current multipart items and legacy RAR items, including encrypted
posted volumes and unresolved lazy parts when their article geometry can be established.
PAR2 protects the **posted archive-volume bytes**, not the extracted movie or its decrypted
contents. Repair does not invoke archive extraction, decryption, or lazy member resolution.

Reported article IDs must belong uniquely to the mounted item's retained NZB. Filenames only
prioritize candidates: exact length plus a prefix hash or intact PAR2-verified slices establish
volume identity. Trusted persisted subsequences and consistent yEnc headers establish exact
article boundaries. NZB byte counts alone are not trusted; adjacent unavailable articles with
no exact boundaries can make repair infeasible.

One attempt uses **one recovery set** covering all reported volumes. It checks every recoverable
source for additional damage and reconstructs the union once. Reports spanning independent
sets are not combined. Multipart repair requires specific article IDs; full-file verify-all
and durable multipart degraded/corrupt indexes are not supported. Existing plain-NZB damage
tracking and verify-all behavior remain available.

See [PAR2 storage and diagnostics](../operations/health-repairs.md#par2-storage-and-diagnostics)
for restart persistence, storage cleanup, and progress reporting.

## Health-check and repair windows [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

Same JSON shape as [download schedule](queue.md#download-schedule-since-130): `{ "Enabled": true, "Windows": [{ "Days": [1,2,3,4,5], "StartMinute": 0, "EndMinute": 420 }] }`. Empty or disabled is unrestricted. Times use the host local timezone (container `TZ`).

Work already in progress finishes if a window closes. Closed health-check windows still admit urgent and already-deferred repairs. Closed repair windows defer confirmed damage until the next open repair window instead of reconstructing or replacing immediately. **Run all checks now** on the Health page opens checks (not repairs) until the due queue is empty, then the schedule resumes. Playback is never gated.

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

For linked files downloaded before Arr provenance tracking was available, InfiniDysk first tries
to recover the original download from exact Arr import history, then falls back to the retained SAB
`nzo_id`. Arr must still confirm grabbed history for that ID before InfiniDysk removes the media,
marks the download failed, or requests a replacement. If that history has expired, the file remains
untouched and is surfaced as **Action needed** instead of risking the wrong release.

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
  found definitively missing while downloading or streaming). Successful **Remove, Blocklist** and
  **Remove, Blocklist, Search** [queue-rule actions](arrs.md) record
  the same in-process evidence. A re-grabbed NZB containing any remembered article fails while
  still in the download queue, before import or Usenet article requests. The bounded memory resets
  on restart and entries can be evicted; a loop that survives a restart is stopped again after one
  extra cycle.
- **Per-file repair rate limit.** After repair has removed 3 downloads for the same library file
  (the same episode or movie file path — not the whole series or folder) within 6 hours, further
  repairs for that file are deferred for a day and surfaced as **Action needed** in the health
  screen instead of triggering another replacement.

[Health and repairs](../operations/health-repairs.md)
