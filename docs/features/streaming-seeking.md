# Streaming and seeking

Players issue HTTP range requests; InfiniDysk fetches the corresponding Usenet articles and serves bytes on demand.

## Performance levers

| Area | Settings |
|------|----------|
| Connection budget | **WebDAV → Max Download Connections**, per-stream presets, streaming priority vs queue |
| Latency | Segment timeout/retries, article buffer size, idle connection timeout |
| Throughput | [NNTP pipelining](nntp-pipelining.md) (queue + WebDAV toggles), provider connection counts |
| Local smoothing | Optional segment cache; rclone VFS cache for symlink mounts |

Tune methodically — [First run](../getting-started/first-run.md) and speed notes in the old setup guide live under WebDAV: raise connections until speed plateaus without pegging CPU.

## Playback automation

[Watchdog](warden-watchdog-preflight.md) retries alternate releases when playback fails. [Preflight](warden-watchdog-preflight.md) can warm candidates ahead of a click. [Watchtower](watchtower.md) resolves list titles before you need them.
