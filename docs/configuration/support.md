# Technical support pack [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

**Settings → Support** generates a ZIP you can provide to trusted InfiniDysk support
when troubleshooting an issue. The archive is streamed to your browser and is
not retained by InfiniDysk.

## Included

- Recent backend logs from the in-memory log buffer
- Redacted active Settings and runtime/build information
- Aggregate provider throughput, outage, failover, and consumption metrics
- Historical latency phase histograms (`metrics/recent.json` → `latency24Hours`)

Backend logs are memory-only and are cleared when InfiniDysk restarts. Frontend and
container logs are not included.

## Latency phases [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

`latency24Hours` projects one-minute histograms into five-minute buckets for:

| Phase | Meaning |
|-------|---------|
| `response` | Successful NNTP response availability after a provider connection is acquired. Excludes body drain. |
| `pool-wait` | Wait to acquire a connection from the named provider pool. |
| `permit-wait` | Top-level workload connection-budget wait; no provider is selected yet. |

Percentiles are **bucket upper bounds**, not exact sample percentiles. Only
successful responses are counted — misses, errors, and cancellations stay in
existing status metrics. Body-drain time is never folded into `response`.

## Health and playback diagnostics [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

`environment.json` includes `healthChecks`: active background workers and the last
32 finished attempts, including file IDs and names, phase, elapsed time, progress,
and the time of the last progress report. Process-lifetime started and finished
counts help show whether work is advancing even after the debug log buffer wraps.
Repeated file IDs in the recent history can reveal repeated checks. This history
is bounded and resets on restart. `Finished` means the worker returned, not that
the media was healthy; consult health-result history for the verdict.

With stream tracing enabled, `stream-traces/events.jsonl` includes friendly
filenames on `RangeOpen` events, so an opaque `/.ids/` URL can be matched to a movie.
Range-open events also export accumulated stall counters before a request ends.
They reflect completed timing observations; a currently blocked operation may not
have reported its duration yet. Do not sum range-open and range-end totals for the
same generation. A missing range-end event can also mean tracing stopped or the
capture was truncated, rather than proving a request is still active.

## Privacy

The pack redacts passwords, API keys, tokens, URL credentials, sensitive URL
parameters, authorization values, and IP addresses. It does **not** anonymize
file names, filesystem paths, account usernames, DNS names, or non-secret URL
paths. Review the ZIP before sharing it.

The pack never includes databases, database backups, NZBs, blobs, environment
files, session or API-key files, crash dumps, or segment-cache data. Opt-in stream
traces are included while tracing is enabled or a stopped capture is retained.
