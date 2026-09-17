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

## Conflicting yEnc metadata [since 1.4.2](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.2){ .nzbdav-since }

Some articles declare positive part counts or file sizes that conflict with the NZB and PAR2 metadata. When import finds a matching file prefix and complete, checksum-verified PAR2 file and slice metadata, it can use PAR2-verified reads for that file. A matching prefix selects the candidate file; it does not establish the integrity of later bytes.

Reads within the first 16 KiB verify that complete prefix against the checksum in the PAR2 file description. This keeps archive-header discovery bounded. Reads beyond that prefix buffer and verify an entire PAR2 slice using both MD5 and CRC32 before returning any bytes. This includes seeks, cached content, and physical archive volumes. Failed verification is retried against eligible providers; if verification still fails, the read fails without returning the unverified bytes. Article CRC validation and ordinary provider fallback remain enabled. Checksum-failing combinations of alternate article IDs are not exhaustively searched.

Uncached short reads can therefore download several megabytes, depending on the release's PAR2 slice size. Supported slices are at most 32 MiB. PAR2 discovery is bounded to 32 MiB, 64 articles, 2 MiB per decoded article, and 30 seconds per import. Missing, incomplete, conflicting, or unsupported PAR2 proof does not authorize relaxing the yEnc count check.

Retry failed imports, or reimport already-mounted releases, to record the new proof. Existing mounts are not rewritten automatically. Files whose first inspected header does not expose a positive metadata conflict retain the ordinary read path; later conflicting headers may still be rejected. This recovery path does not change the separate PAR2 repair-discovery workflow or verify an entire file during import. Successful import is not a full-file integrity check.
