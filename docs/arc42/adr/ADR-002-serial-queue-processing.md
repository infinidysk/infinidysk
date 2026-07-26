# ADR-002: Serial (one-item-at-a-time) queue processing across releases

**Status**: Accepted (INHERITED), **flagged for reconsideration** — see optimization candidate in §11
**Quality scenarios affected**: QS-2 (ingestion latency), QS-3/QS-4 (indirectly, via resource contention)

## Context

Multiple NZBs can be queued faster than they can be processed (e.g. a Sonarr RSS sync queuing
several episodes at once). Usenet download bandwidth is normally the bottleneck resource on a
homelab uplink, not CPU — a single release can often saturate the available bandwidth alone.

## Decision

`QueueManager`'s loop (`QueueManager.cs:78`) processes **exactly one `QueueItem` at a time**,
end-to-end (deobfuscation → file processing → aggregation → post-processing), before pulling the
next. Within one item, file-level work *is* parallelized (bounded concurrency =
`maxDownloadConnections + 5`) — the seriality is strictly cross-item.

## Consequences

- **Positive**: sidesteps a whole class of concurrent-SQLite-write and shared-connection-pool
  contention problems for comparatively little throughput gain in the common single-release case;
  simple cancellation/locking semantics (one `SemaphoreSlim(1,1)` guards the single in-flight item).
- **Negative**: a second release doesn't even start downloading until the first fully completes,
  including all post-processing — directly threatens QS-2 under burst ingestion. Compounded by no
  observed retry cap on `IsRetryableDownloadException()` items, which can retry every minute
  indefinitely at the front of the priority queue, head-of-line-blocking same-or-lower-priority items
  behind it.

## Alternatives considered

| Alternative | QS-7 | QS impact | Migration cost |
|---|---|---|---|
| **Bounded-parallel queue processing** (process 2-3 items concurrently) | Fully compatible — just more concurrent connections against the same provider pool | Directly helps QS-2 for burst ingestion; risks QS-3/QS-4 if the bound isn't tied to `GetMaxDownloadConnections()` | **Medium** — reworks `QueueManager`'s core loop and its cancellation/lock semantics, but `QueueManager.cs` has comparatively little fork-specific history to conflict with, making this one of the cheaper "diverge from upstream" options in the whole document |
| Keep serial, add a bounded retry count instead | No change | Prevents indefinite head-of-line blocking without touching concurrency semantics at all | **Low** — additive counter + one comparison in an existing catch block |

**Recommendation**: the retry-cap fix is unambiguously worth doing regardless (low cost, closes a
real gap). Bounded-parallel processing is the highest-leverage *ingestion*-side optimization in this
document if the maintainer is willing to accept medium effort/risk for a real QS-2 win — see §11.
