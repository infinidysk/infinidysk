---
name: infinidysk-crates
description: infinidysk crate layout, layering and streaming lifecycle invariants. Use before adding a crate, moving code between crates, adding a cross-crate dependency, or changing NNTP, stream, segment, BODY/ARTICLE, pool, permit or prefetch code.
---

# Crate layout and streaming invariants

## Crate layout

Dependencies point downward only. Parsers do no I/O. No traits just for mocking. Create a crate only when code for it exists.

| Layer | Crate | Owns |
|-------|-------|------|
| L0 pure (no tokio) | `core` | ids, byte ranges, metadata types, versioned zstd blob format |
| | `yenc` | rapidyenc FFI; the only `unsafe` crate |
| | `archive` | stored-member headers for RAR4/RAR5/7z, multipart |
| | `par2` | PAR2 parse, verify, repair |
| L1 | `nntp` | provider pool, circuit breaker, download permits, lifecycle guards |
| | `db` | sqlx, SQLite + PostgreSQL, per-backend migrations; one runtime SQL string per query (`$N` placeholders, portable SQL; PostgreSQL-only row locks such as `FOR UPDATE` run behind a backend check) |
| | `arr` | Arr HTTP client: profiles, system status, SAB download-client discovery, repair calls |
| L2 | `stream` | multi-segment seekable stream, prefetch |
| L3 | `vfs` | tree shared by WebDAV and FUSE: lookup, listing, `/.ids`, `/completed-symlinks`, read handles |
| | `queue` | NZB → archive/multipart → PAR2 → items |
| | `health` | health checks and repair |
| L4 | `webdav` | axum WebDAV |
| | `fuse` | `fuse3` mount; Linux only (`cfg(target_os = "linux")`), so macOS dev builds skip it |
| bin | `infinidysk` | lib + thin `main.rs`: config, auth, SAB API, admin API, embedded UI |
| side | `import` | one-time C# `/config` importer (deletable) |
| | `testkit` | loopback NNTP server, seed-1025 corpus (dev-dependency only) |
| | `bench` | benchmarks with C# scenario names |

## Streaming lifecycle invariants

Encode these as types and `Drop` guards, not conventions.

- Every BODY/ARTICLE completion fires **exactly once**, including cancellation, missing articles, setup failures and stream failures.
- Outcomes: `Retrieved` (success, connection reusable), `Cancelled` (drained cleanly, reusable), `NotFound` (clean server miss), `NotRetrieved` (connection must be replaced).
- Completion releases both the provider-pool lease and the global download permit. Callback panics/errors must not fault transport tasks.
- Pipelined batch responses complete in request order. Mixed cache-hit batches request only misses while preserving order.
- Fallback transfers within a batch start in response order and stay bounded; unordered permit acquisition can deadlock sequential consumers.
