# ADR-001: SQLite + flat-file blob store as the persistence model

**Status**: Accepted (INHERITED — upstream `nzbdav-dev`, including a completed internal migration)
**Quality scenarios affected**: QS-7 (single-command deployability), QS-4 (resource footprint), QS-8 (crash-safety)

## Context

The entire virtual filesystem tree, queue, and history need to be persisted, updateable
transactionally, and queryable by path — while the deployment target is a single Docker container
with no external database service (§2, §10 QS-7). Per-file metadata can be large: a multi-GB remux
NZB can have 5,000+ segments, and early history shows this was originally stored as JSON in SQLite
row values via `DavNzbFiles`/`DavRarFiles`/`DavMultipartFiles` tables.

## Decision

Use SQLite (via EF Core) as the structural datastore (`DavItem` tree, `QueueItem`/`HistoryItem`
rows), but store the potentially-large per-segment metadata in a **separate flat-file blob store**
(`BlobStore.cs`: zstd-compressed, MemoryPack-serialized files under a 2-level sharded directory,
keyed only by a GUID pointer kept in SQLite). The former JSON-column tables are kept as a read
fallback, actively drained by a dedicated `UsenetFileToBlobstoreMigrationService` on every startup
until empty — this is itself a completed, deliberate migration (dated 2026-01-19) away from the
earlier all-SQLite design.

## Consequences

- **Positive**: SQLite file stays small and its write-ahead-log churn low regardless of segment
  count; the whole datastore is one file, trivially backed up by copying it (which
  `BlockUpgradesToV06X`, ADR-010, explicitly leans on for a breaking-migration safety story).
- **Negative**: two independent storage engines (SQLite + flat files) must be kept consistent.
  Blob files are written just before the SQLite transaction and rolled back on any exception the
  process is alive to catch — a hard kill between those two steps can orphan blob files (disk-space
  leak, not filesystem corruption; see §11).

## Alternatives considered (this document's analysis, scored against §10)

| Alternative | QS-7 | QS impact | Verdict |
|---|---|---|---|
| Embedded Postgres | Still single-container, but adds a second in-container process to supervise | Would help write-concurrency, but today's serial queue processing (ADR-002) means SQLite's single-writer model isn't the actual bottleneck | Not worth it unless serial queue processing is also replaced |
| LiteDB or a custom flat-file index instead of the SQLite+blob hybrid | Comparable deployability | Unclear win — the hybrid already gets "small metadata store + flat blobs"; reinventing indexing/transactions SQLite gives for free | Not recommended |
| EF Core → Dapper/raw SQL for the hot path (`GetDirectoryChildAsync`) | No change | Marginal expected gain — the query is already a simple indexed `WHERE ParentId = ? AND Name = ?` (hypothesis, no profiler run exists) | Could be done surgically and cheaply if profiling ever confirms a real cost |

Migration cost if reversed: **high** — `Database/` is ~96%+ inherited with 30+ migrations; replacing
the datastore abandons upstream schema/migration mergeability entirely.
