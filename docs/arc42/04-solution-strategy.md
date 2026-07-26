# 4. Solution Strategy

This section gives the short version of *why the system is shaped the way it is*, mapped to the
quality goals in §1.2/§10. Full rationale and INHERITED-vs-FORK-SPECIFIC attribution for each
decision is in §9 (Architecture Decisions); this is the summary that ties them to quality goals.

| Quality goal | Strategic decision |
|---|---|
| Low seek/start latency (QS-1) | Content is never bulk-downloaded; instead, a layered NNTP client stack (`Clients/Usenet/`) and composable `Stream` classes (`Streams/`) resolve arbitrary byte ranges to specific Usenet articles on demand, decoding yEnc (and optionally AES) inline. Range requests map directly to article fetches rather than to a linear download-then-serve model. |
| Resource-efficient on one host (QS-4) | SQLite (in-process, file-based) instead of a client/server database; no message broker; no cache service — the whole persistence layer is one file, avoiding extra long-running processes competing for the container's RAM/CPU budget. |
| Operational simplicity, single-command deploy (QS-7) | One Docker image bundles both runtimes (.NET backend + Node frontend); SQLite removes the need to provision a database; the SABnzbd-compatible API means no Sonarr/Radarr-side plugin/configuration beyond pointing it at a URL. |
| Resilience to provider flakiness (QS-6) | A layered client (`MultiConnectionNntpClient` → `MultiProviderNntpClient` → `ArticleCachingNntpClient` → `UsenetStreamingClient`) isolates per-provider connection pooling and failure (`ProviderCircuitBreaker`) so one bad provider degrades gracefully instead of failing the whole read/download path. |
| Maintainability of a forked codebase | Fork-specific features (usage stats, predictive prefetch) are additive — new files/hooks alongside upstream structure — rather than restructuring existing upstream modules, keeping future upstream merges tractable (see §9). |
| Ingestion correctness for obfuscated/archived releases | A dedicated deobfuscation pipeline (`Queue/DeobfuscationSteps/1..3`) resolves real filenames via PAR2 metadata before per-container-type processors (`RarProcessor`, `SevenZipProcessor`, `MultipartMkvProcessor`, plain `FileProcessor`) and matching aggregators reassemble the final logical file — an explicit multi-stage pipeline rather than a single monolithic import step, so each container format's quirks are isolated. |

## Key technology choices at a glance

- **Backend: .NET 10 / ASP.NET Core** — a single process hosts both the WebDAV surface (`NWebDav.Server`)
  and the REST API surfaces on one Kestrel host.
- **Frontend: React Router 7 (SSR) + hand-rolled Express server** — the Express layer does triple duty:
  reverse-proxying WebDAV/API/media paths to the backend, enforcing session auth, and relaying a
  websocket channel — see §9 and §7 for the trade-offs this creates.
- **Database: SQLite via EF Core** — one file, no separate DB process; migrations are explicit
  (`--db-migration`), not automatic on boot.
- **Distribution: single multi-stage Docker image** — bundles both runtimes; see §7 for the internal
  process topology and §11 for the risks this specific choice carries (no process supervisor found
  in initial exploration — confirm in §7).

Where this strategy trades against "as performant as possible": on-demand, no-bulk-download streaming
is the right shape for QS-4 (resource footprint) and QS-7 (no local storage needed), but it puts
*every* seek on the critical path of a live NNTP round-trip unless something is pre-warmed/cached —
this exact tension is why §6 traces the seek path in detail and §11 evaluates prefetching/connection
pre-warming as the highest-leverage optimization candidates.
