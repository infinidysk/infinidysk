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
| Enable Segment Cache | `usenet.segment-cache.enabled` | on | Cache decoded segments on disk; restart required |
| Cache path | `usenet.segment-cache.path` | `/config/segment-cache` | Segment-cache directory |
| Maximum size (GB) | `usenet.segment-cache.max-gb` | `10` | Segment-cache size limit |
| Streaming Segment Timeout | `usenet.streaming-segment-timeout-seconds` | `8` | Per-segment deadline, 2–40 seconds |
| Streaming Read Timeout [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `usenet.streaming-read-timeout-seconds` | `30` | Initial 5–120 second wait to open a GET/range |
| Streaming Write Timeout | `usenet.streaming-write-timeout-seconds` | `60` | Per-write deadline, 0–600 seconds (0 disables); also cancels a stream that transfers less than 64 KB per timeout window while other streams wait on Article RAM |
| Streaming Segment Retries | `usenet.streaming-segment-retries` | `3` | Fresh-connection retries after timeout, 0–5 |
| Article Buffer Size | `usenet.article-buffer-size` | `40` | Articles buffered ahead per stream |
| In-flight article budget (MiB) [since 0.8.2](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.2){ .nzbdav-since } | `usenet.in-flight-article-budget-mb` | auto | Host-wide decoded-byte cap, 64–8192 MiB |
| Idle connection timeout | `usenet.idle-connection-timeout-seconds` | `60` | Close unused connections after 15–300 seconds |
| Batched article downloads | `usenet.pipelined-body-requests` | on | Fetch WebDAV BODY requests in small batches |
| Streaming batch width [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since } | `usenet.streaming-body-batch-width` | `4` | Maximum articles per BODY batch (1–8) |
| Container-aware gap fill [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since } | `usenet.container-aware-fill` | on | Experimental MPEG-TS null-packet fill for confirmed gaps |

### Segment-cache storage

Segment Cache is **enabled by default**. It can improve repeated reads and seeks, but
also writes decoded segments to the cache path. InfiniDysk does not automatically
classify that storage, so verify that `/config/segment-cache` (or your configured
path) is local SSD/NVMe or other storage that can safely absorb the extra writes.
Disable the cache for slow disks, network mounts, or flash storage with limited
write endurance; alternatively, point **Cache path** at suitable local storage.

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

## Experimental container-aware gap fill

After every provider and fallback Message-ID confirms an article is missing or
corrupt, InfiniDysk normally emits the same number of zero bytes to preserve
later file offsets. For direct MPEG-TS files (`.ts`, `.m2ts`, `.mts`),
container-aware gap fill emits packet-aligned null packets instead when exact
segment offsets are available.

This can help compatible players resynchronize sooner, but cannot restore
missing audio or video. Matroska, MP4/MOV, archive-backed files, and transient
transport failures retain their existing behavior.

## Capturing a buffering support pack

1. Use `LOG_LEVEL=INFO` so routine debug activity does not evict streaming events.
2. Enable **Developer stream tracing** under **Settings → Support**.
3. Reproduce the stall by playing the file from Files so the read goes directly through `/view`.
4. Download the support pack immediately after reproducing the problem.

[Streaming and seeking](../features/streaming-seeking.md) ·
[NNTP pipelining](../features/nntp-pipelining.md) ·
[Logs and crash dumps](../operations/logs-crash-dumps.md)
