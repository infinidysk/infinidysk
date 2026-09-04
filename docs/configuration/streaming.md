# Streaming

Streaming settings tune how WebDAV playback uses provider connections, memory,
caching, and retries. WebDAV credentials and filesystem behavior remain under
[WebDAV](webdav.md); queue capacity is under [Queue](queue.md).

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`usenet.streaming-priority` → `NZBDAV_CONFIG__USENET__STREAMING_PRIORITY`).

## Connection allocation

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Max Download Connections | `usenet.max-download-connections` | `0` (auto = pool) | Streaming connection budget |
| Apply limit per stream | `usenet.max-download-connections-per-stream` | off | Give each concurrent stream its own budget |
| Per-stream performance | `usenet.max-download-connections-per-stream-preset` | `high` | low/medium/high/max = 25/50/75/100% |
| Streaming Priority (vs Queue) [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `usenet.streaming-priority` | `80` | Favor playback when streaming and queue imports overlap |

Provider limits still cap total connections. Spare capacity is not held idle
for playback; the priority setting only affects admission when a provider pool
is saturated.

!!! tip "Speed tuning"

    Raise **Max Download Connections** until throughput plateaus without
    pegging CPU. Baseline with a host speed test, then time a `/view` download
    from inside the container against the backend.

## Streaming performance

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Segment Cache | `usenet.segment-cache.enabled` | off (new installs) | Cache decoded segments on disk; restart required |
| Cache path | `usenet.segment-cache.path` | `/config/segment-cache` | Segment-cache directory |
| Maximum size (GB) | `usenet.segment-cache.max-gb` | `10` | Segment-cache size limit |
| Cache write-behind (MiB) [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `usenet.segment-cache.write-behind-mb` | `0` | Advanced, restart-required RAM budget for asynchronous cache writes. `0` keeps inline writes; nonzero values are 16–1024 MiB |
| Streaming Segment Timeout | `usenet.streaming-segment-timeout-seconds` | `8` | Per-segment deadline, 2–40 seconds |
| Streaming Read Timeout [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `usenet.streaming-read-timeout-seconds` | `30` | Initial 5–120 second wait to open a GET/range |
| Streaming Write Timeout | `usenet.streaming-write-timeout-seconds` | `60` | Per-write deadline, 0–600 seconds (0 disables); also cancels a stream that transfers less than 64 KB per timeout window while other streams wait on Article RAM |
| Streaming Segment Retries | `usenet.streaming-segment-retries` | `3` | Fresh-connection retries after timeout, 0–5 |
| Article Buffer Size | `usenet.article-buffer-size` | `40` | Articles buffered ahead per stream |
| In-flight article budget (MiB) [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `usenet.in-flight-article-budget-mb` | auto | Host-wide decoded-byte cap. Auto uses 25% of the detected managed-heap ceiling, clamped to 64–8192 MiB; explicit values also range from 64–8192 MiB |
| Global Usenet bandwidth limit (Mbit/s) [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `usenet.bandwidth-limit-mbps` | unlimited | Process-wide cap on live provider BODY/ARTICLE payload ingress for queue and WebDAV. Empty or `0` is unlimited. 1 Mbit/s = 125,000 bytes/s. Provider speed tests, cache hits, and LAN delivery are never limited. Takes effect immediately. A cap cannot raise a latency-limited workload. |
| Idle connection timeout | `usenet.idle-connection-timeout-seconds` | `60` | Close unused connections after 15–300 seconds; also sets the [connection warming](../features/connection-warming.md) sweep and keepalive cadence |
| Read-start warm-up [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `usenet.read-start-warmup.enabled` | on | Expand pooled-provider connections in parallel when a long buffered WebDAV read starts; captured when each stream opens |
| NNTP response timeout [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `usenet.nntp-read-timeout-seconds` | `30` | Stalled-read inactivity deadline for BODY, ARTICLE, STAT, authentication, and other NNTP responses, 5–120 seconds. This is not a total transfer deadline. Streaming segment/read budgets and the 15-second connect/auth ceiling can expire first. Takes effect on the next provider-pool rebuild or restart. |
| Replacement reconnect spacing [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | `usenet.reconnect-delay-milliseconds` | `500` | Minimum spacing between replacement handshakes after a poisoned connection is closed, 0–5000 milliseconds. Zero disables ordinary replacement spacing; TCP/TLS/AUTHINFO factory failures still back off from a 500ms floor, doubling up to 60 seconds. Takes effect on the next provider-pool rebuild or restart. |
| Batched article downloads | `usenet.pipelined-body-requests` | on | Fetch WebDAV BODY requests in small batches |
| Streaming batch width [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since } | `usenet.streaming-body-batch-width` | `4` | Maximum articles per BODY batch (1–8) |
| Container-aware gap fill [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since } | `usenet.container-aware-fill` | on | MPEG-TS null-packet fill for confirmed gaps |

The NNTP stalled-read timeout, streaming segment timeout, streaming read budget, and idle
connection timeout are separate deadlines. Connect and AUTHINFO use the earliest of caller
cancellation, the 15-second connect/auth ceiling, and the configured NNTP stalled-read
timeout. Saving the NNTP timeout or reconnect spacing does not rebuild live pools; new
values apply on the next provider-config save or process restart.

### Segment-cache storage

Segment Cache is **off by default on new installs**
[since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }.
Installs upgraded from an earlier release that never changed the setting keep the cache
**on**: the upgrade pins the previous effective value so behaviour does not change
silently. Review it under **Settings → Streaming** and turn it off if you do not need it.

When enabled, the cache can improve repeated reads and seeks, but it also writes decoded
segments to the cache path. InfiniDysk does not automatically
classify that storage, so verify that `/config/segment-cache` (or your configured
path) is local SSD/NVMe or other storage that can safely absorb the extra writes.
Disable the cache for slow disks, network mounts, or flash storage with limited
write endurance; alternatively, point **Cache path** at suitable local storage.

When Segment Cache is **disabled** at startup, InfiniDysk purges any leftover cache
files from the cache path in the background so a previously enabled cache does not
keep occupying disk. Only files matching the cache layout are removed; unrelated
files placed in that directory are left alone.
[since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

### Segment-cache write-behind

With the default value `0`, each decoded segment is written to storage before
the same bytes continue through the playback drain. On slower bind mounts,
overlay filesystems, or network storage, that can apply disk backpressure to
the Usenet socket.

Set `usenet.segment-cache.write-behind-mb` to 16–1024 to copy completed segment
bodies into bounded pooled memory and publish them from one FIFO background
writer. This budget is additional to the in-flight article budget. The writer
also caps queued plus active jobs at 256. When either limit is full, the new
cache write is skipped rather than delaying playback.

Write-behind makes cache publication asynchronous. An immediate second read can
miss and fetch the segment again until the writer publishes both body and header.
Disk failures remain best-effort and do not fail playback. The Support memory
snapshot reports the configured budget, reserved and peak physical buffer bytes,
queued/active jobs, and capacity skips.

The setting is advanced-only and has no Settings control. Configure
`NZBDAV_CONFIG__USENET__SEGMENT_CACHE__WRITE_BEHIND_MB`, then restart the
container. Set it back to `0` and restart to restore inline writes.

### Segment Cache and rclone read-ahead

Symlink libraries stream through an rclone mount. When that mount runs with
`--vfs-read-ahead`, rclone already buffers ahead of playback, and Segment Cache only
adds disk writes without improving seeks. Keep one or the other: the
[Setup Guide](../getting-started/setup-guide.md) disables Segment Cache for Symlinks
and recommends the bounded rclone VFS cache instead.

InfiniDysk cannot always inspect the mount, so the Streaming tab shows a dismissable
warning whenever Segment Cache is on for a Symlinks library. Dismissal is remembered
per browser. When the [rclone RC connection test](rclone.md) can read VFS statistics
and confirms read-ahead is enabled, the Rclone tab shows a definitive warning instead.
Re-run the Setup Guide from the main navigation to review this and other recommended
settings, even on an existing installation.

## Article buffer and adaptive prefetch

`usenet.article-buffer-size` bounds how many decoded articles a stream may keep
ahead of the consumer. The in-flight article budget separately caps decoded
bytes across all concurrent streams.

When **Batched article downloads** is on, WebDAV BODY requests start in
batches of up to the configured streaming batch width (default four articles
on one connection). If playback starves waiting for the next segment,
InfiniDysk narrows that batch width (`4 → 2 → 1`) so more connections can
work in parallel. The width recovers gradually when the consumer remains
ahead, but never above the configured maximum.

The segment task window and prefetch byte ceiling are computed once at stream
construction from the initial batch width and article buffer. Adaptive
narrowing does not shrink those ceilings — only future batch sizes. Leave the
width at the default unless you have measured a benefit; wide settings can
starve other concurrent streams via the shared in-flight article budget.

## Container-aware gap fill

After every provider and fallback Message-ID confirms an article is missing or
corrupt, InfiniDysk normally emits the same number of zero bytes to preserve
later file offsets. For direct MPEG-TS files (`.ts`, `.m2ts`, `.mts`),
container-aware gap fill emits packet-aligned null packets instead when exact
segment offsets are available.

This can help compatible players resynchronize sooner, but cannot restore
missing audio or video. Matroska, MP4/MOV, archive-backed files, and transient
transport failures retain their existing behavior.

## Shared streams for concurrent readers [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

Players often issue a probe GET and a playback GET for the same file at the
same time. Shared streams join those overlapping requests onto one Usenet
fetch when their byte offsets are close enough, instead of opening a private
stream per request.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Share one stream across concurrent readers | `usenet.shared-streams.enabled` | on | Master switch; off restores a private stream per request without a restart |
| Max shared streams | `usenet.shared-streams.max-entries` | `4` | Global cap on live shared streams (1–32) |
| Max regions per file | `usenet.shared-streams.max-entries-per-file` | `3` | Separate streams for far-apart offsets of the same file (1–8) |
| Ring size (MiB) | `usenet.shared-streams.ring-mb` | `32` | Recently fetched bytes late joiners can read without refetching (4–256) |
| Grace period (seconds) | `usenet.shared-streams.grace-seconds` | `10` | Keep a stream warm after the last reader disconnects (0–60) |
| Small-range skip (MiB) | `usenet.shared-streams.small-range-max-mb` | `16` | Closed ranges at or below this size stay private unless they already overlap a live stream (1–256) |

HEAD requests never join a shared stream. Unsatisfiable ranges still return
416 before any stream is opened. A declined attach uses today's private-stream
path unchanged.

Ring bytes are rented from `ArrayPool` as needed and returned when readers
catch up or the entry is torn down. Support packs and Prometheus report:

- `sharedStreamRingConfiguredMaxBytes` — max-entries × ring size (128 MiB at
  the defaults). This is the theoretical ceiling under sustained maximal
  divergence on every slot at once.
- `sharedStreamRingLogicalBytes` — decoded bytes currently sitting in ring
  chunks (reader divergence plus about 4 MiB of lead data in the steady state).
- `sharedStreamRingRetainedBytes` / `…Peak` — actual `ArrayPool` array
  capacity currently (and ever) rented by those chunks. Bucket sizes are
  typically larger than the requested chunk length.
- `sharedStreamPumpScratchRentedBytes` — the pump's separate scratch buffer,
  counted in its own category.

Returning an array to `ArrayPool` does **not** give those pages back to the
OS; process working set can stay elevated after the counters drop. Ring bytes
are **not** leased from the in-flight article budget. A dedicated retention
cap will be added only if field support packs show shared-ring rented
capacity contributing to memory-pressure events or occupying a large fraction
of the container limit.

Disable shared streams if you need a guaranteed private prefetch window per
client, or if a support pack shows ring retention competing with Article RAM
on a small host.

## Capturing a buffering support pack

1. Use `LOG_LEVEL=INFO` so routine debug activity does not evict streaming events.
2. Enable **Developer stream tracing** under **Settings → Support**.
3. Reproduce the stall by playing the file from Files so the read goes directly through `/view`.
4. Download the support pack immediately after reproducing the problem.

[Streaming and seeking](../features/streaming-seeking.md) ·
[NNTP pipelining](../features/nntp-pipelining.md) ·
[Logs and crash dumps](../operations/logs-crash-dumps.md)
