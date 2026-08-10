# Queue

Queue settings control how many NZBs can wait and process at once, plus the
provider connections available to active imports.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`queue.worker-count` → `NZBDAV_CONFIG__QUEUE__WORKER_COUNT`).

## Processing capacity

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Concurrent Queue Downloads [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since } | `queue.worker-count` | `1` | Process 1–10 NZBs concurrently; the oldest active item is preferred |
| Queue Download Connections | `usenet.max-queue-connections` | blank = all | Provider connections shared by queue workers and background health checks |

Adding workers does not add provider capacity. Additional workers use spare
connections from the same queue budget, so raising concurrency primarily lets
independent jobs make progress while the oldest item retains preferred access.

The headless-only `usenet.max-queue-connections-preset` supports
`low`/`medium`/`high`/`max` (25/50/75/100% of pooled provider connections).
An explicit `usenet.max-queue-connections` value takes precedence.

Playback has a separate connection budget and priority policy under
[Streaming](streaming.md).

## Queue admission

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Maximum queued jobs [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since } | `queue.max-items` | `0` (unlimited) | Reject new SAB submissions at this queue depth |
| Resume threshold [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since } | `queue.resume-threshold` | `0` | Resume admission at or below this depth |

At the maximum, InfiniDysk returns the standard SAB-compatible
`{"status": false, "error": "..."}` response without storing the NZB. Sonarr
and Radarr treat this as a temporarily unavailable download client and keep
automatic-search releases pending for a later retry.

The resume threshold adds hysteresis after the maximum is reached. Set it to
`0` to resume as soon as the queue drops below the maximum. Duplicate
submissions that replace an existing queue item remain allowed because they do
not increase queue depth.

## Stuck-item watchdog [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }

Each active queue worker runs a progress watchdog. If `ProgressPercentage` does not
increase for `QUEUE_ITEM_STUCK_MINUTES` (default **5**), InfiniDysk pauses the item
(`PauseUntil` ≈ 15–20 minutes with jitter) and cancels the worker so the queue can
move on. The item is **not** failed into history — it retries after the pause
expires.

Tune the stall budget with `QUEUE_ITEM_STUCK_MINUTES` when long phases legitimately
hold progress (large archives, full article-existence health checks).

[SABnzbd settings](sabnzbd.md) · [Streaming settings](streaming.md)
