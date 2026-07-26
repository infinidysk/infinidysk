# 11. Risks and Technical Debt

This section follows [aim42](https://aim42.github.io)'s Analyze → Evaluate → Improve structure: the
risks below were surfaced during the Building Block/Runtime analysis (§5–§8) and the five parallel
research passes; they're evaluated here against the quality scenarios in §10 and turned into a
ranked, actionable improvement backlog. Every item is independently additive — none require
reversing an ADR in §9 unless explicitly noted.

**A note on evidence**: no test suite, benchmark, or profiler output exists anywhere in this repo.
Every item below that depends on a performance claim is marked `(hypothesis)` with the experiment
that would confirm it, per the standing instruction in §10.3. Treat unmarked items as structural/
static-analysis findings (e.g., "this code path has no test coverage" is directly verifiable, not a
hypothesis).

## 11.1 Priority 1 — quick wins (small effort, clear payoff, low risk)

| # | Item | QS improved | Effort | Risk | Source |
|---|---|---|---|---|---|
| P1-1 | **Cache yEnc headers (not full article bodies) per `NzbFileStream`**, so repeated seeks into the same open file skip re-probing already-visited segments | QS-1 | S | Low | usenet-streaming — the single highest-leverage seek-latency fix in this document |
| P1-2 | **Add a bounded retry count for `IsRetryableDownloadException()` items**, converting to a hard failure after N attempts instead of retrying forever | QS-2, QS-5 | S | Low | core-domain |
| P1-3 | **Add a Docker `HEALTHCHECK`** targeting the frontend's exposed port | QS-8 | S | Low | deployment — see [ADR-008](adr/ADR-008-process-supervision.md) |
| P1-4 | **Add real readiness checks to `/health`** (DB reachable, ≥1 Usenet provider reachable), distinct from the current bare liveness ping | QS-5 | S | Low | api-auth |
| P1-5 | **Harden `DISABLE_WEBDAV_AUTH`** with a second signal (trusted-proxy header secret or loopback/XFF check) instead of a bare boolean | security (adjacent to QS-7) | S-M | Low | api-auth — see [ADR-009](adr/ADR-009-webdav-auth-bypass.md), the most significant security finding in this document |
| P1-6 | **De-duplicate the proxy path list** (`/api`, `/view`, `/.ids`, `/nzbs`, `/content`, `/completed-symlinks`) into one shared constant used by both `server.ts`'s compression filter and `server/app.ts`'s routing | QS-1 (prevents silently breaking seeking/auth for a new path) | S | None | frontend |
| P1-7 | **Add `.dockerignore`** (`bin/`, `obj/`, `node_modules/`, `build/`, `dist-node/`, `.git/`) | dev-loop speed only | S | None | deployment |
| P1-8 | **Constant-time API key comparison** (`CryptographicOperations.FixedTimeEquals`) instead of `==` | security | S | None | api-auth — see [ADR-005](adr/ADR-005-auth-trust-boundary.md) |
| P1-9 | **Clarify or fix `BaseTask`'s process-wide static mutual-exclusion semaphore** — confirm whether "only one of RemoveSampleFilesTask/RemoveUnlinkedFilesTask/StrmToSymlinksTask can run at a time, system-wide" is intentional; document it or make it per-type | none directly; prevents a confusing silent no-op | S | Low | api-auth |
| P1-10 | **Run the `ssr:false` experiment** (flag already exists, anticipated by an inline comment) under `docker stats` idle and loaded, to get a real QS-4 number before deciding on any bigger frontend change | informs QS-4 decision-making | S | None (measurement only, reversible) | frontend |
| P1-11 | **Confirm whether `RemoveOrphanedFilesSchedulerService` already reconciles blob-store files against `DavItem`/`QueueItem`/`NzbBlobId` references** before treating orphaned-blob cleanup as a gap | QS-8 (closes a narrow disk-leak risk, if not already closed) | S (verification) | None | core-domain open question |

## 11.2 Priority 2 — real payoff, medium effort or medium risk

| # | Item | QS improved | Effort | Risk | Source |
|---|---|---|---|---|---|
| P2-1 | **Bounded-parallel queue processing** (2-3 items concurrently instead of strictly serial), capped relative to `GetMaxDownloadConnections()` | QS-2, indirectly QS-3 | M | Medium — reworks `QueueManager`'s cancellation/locking semantics; comparatively low upstream-merge-conflict risk since that file has little fork-specific history | core-domain — see [ADR-002](adr/ADR-002-serial-queue-processing.md) |
| P2-2 | **Add an optional idle-connection floor (keep-warm) per provider**, opt-in, defaulting to today's on-demand behavior | QS-1, QS-3 | S-M | Low-Medium — must respect provider max-connections and not fight the existing idle-sweeper | usenet-streaming |
| P2-3 | **Introduce a minimal process supervisor** (s6-overlay or tini+supervisord) for independent backend/frontend crash-restart and real zombie reaping | QS-8 | M | Medium — must re-derive the existing ordered-startup + signal-forwarding + PUID/PGID logic; no CI test gate would catch a regression here, needs manual restart/signal testing | deployment — see [ADR-008](adr/ADR-008-process-supervision.md) |
| P2-4 | **Add unit tests for `ConnectionPool`/`ConnectionLock`** covering concurrent acquire/return/replace/dispose races (explicitly ChatGPT-authored, zero test coverage today, and every stream/queue download depends on it) | QS-3, QS-4, QS-8 | S-M | Low (test-only; may surface latent bugs to then fix) | usenet-streaming |
| P2-5 | **In-process LRU cache for `DatabaseStoreCollection`/watch-folder child lookups**, invalidated on the same writes that already call `RcloneVfsForget` | QS-3, QS-4 | S-M | Low | core-domain |
| P2-6 | **Add latency-aware signal to `ProviderCircuitBreaker`** (trip on sustained high command latency, not just consecutive hard failures) | QS-1, QS-3, QS-6 | M | Medium — tuning false-positive/negative thresholds with no load-test harness to validate against | usenet-streaming |
| P2-7 | **Unify the three parallel error-response code paths** (`ExceptionMiddleware`, `BaseApiController`, `SabApiController`) behind one shared exception→status mapper, keeping the SAB surface's response *shape* unchanged | maintainability | M | Medium — regression risk on the SAB surface specifically, since Sonarr/Radarr parse it strictly | api-auth |
| P2-8 | **Wire `npm run typecheck` into CI as a required check** on `branch.yml`/`pre-release.yml` (the script already exists; today it's a manual, optional pre-PR step) | correctness gate before any image ships | S | Low | deployment (weak point #7) — this document's own addition; the script is already written, only CI wiring is missing |
| P2-9 | **Document/measure the `articleBufferSize` (read-ahead depth) default and its QS-1-vs-QS-4 tradeoff**, and confirm it's exposed/tunable for operators | QS-1, QS-3, QS-4 (via informed tuning) | S | Low | usenet-streaming open question |
| P2-10 | **De-duplicate `backend-client.server.ts`'s repeated header/error-handling boilerplate** into one `fetchJson` helper | maintainability | S | Low | frontend |

## 11.3 Priority 3 — larger or lower-priority items

| # | Item | QS improved | Effort | Risk | Source |
|---|---|---|---|---|---|
| P3-1 | Final integrity check comparing the assembled file's byte count/hash against the PAR2-declared value before marking a queue item complete | confidence adjacent to QS-1/QS-3 | M | Low | core-domain |
| P3-2 | A real PAR2 repair/reconstruction engine (currently descriptor-only — missing articles can only be detected, never reconstructed) | reliability adjacent to QS-6 | L (from-scratch FEC implementation) | — | core-domain |
| P3-3 | Per-client API keys (one per Sonarr/Radarr/frontend instead of one shared secret) | blast-radius containment | M | Low | api-auth — see [ADR-005](adr/ADR-005-auth-trust-boundary.md); only worth it if multi-arr-instance usage is observed |
| P3-4 | Prototype a reverse proxy (Caddy/nginx) as an additional supervised process *inside* the existing single container, replacing (not duplicating) Express's route-matching | QS-1, QS-3 (modest, unverified ceiling) | M | Medium | frontend — see [ADR-007](adr/ADR-007-frontend-ssr-and-proxy.md); do the `ssr:false` experiment (P1-10) first |
| P3-5 | Slimmer/distroless base image for the final Docker stage | QS-4 | M-H | Medium — breaks `entrypoint.sh`'s `su-exec`/shell-based PUID remap, needs a redesigned non-root strategy | deployment — see [ADR-003](adr/ADR-003-single-container-deployment.md) |
| P3-6 | GHCR branch-tag pruning workflow (unbounded registry growth from `branch.yml`) | operational hygiene | S | Low | deployment |
| P3-7 | Wire up or remove the currently-inert `QueueItem.PostProcessingOption` field (accepted from Sonarr/Radarr, stored, never consulted) | correctness/expectations cleanup | S | Low | core-domain |

## 11.4 Explicitly considered and rejected (to prevent re-litigating)

| Option | Why rejected |
|---|---|
| Separate backend/frontend containers via docker-compose | Directly breaks QS-7's single-`docker run` value proposition; a breaking change for the entire existing user base. See [ADR-003](adr/ADR-003-single-container-deployment.md). |
| mTLS or OAuth2/OIDC for the frontend↔backend boundary | Over-engineered for a same-host, same-container, single-operator trust boundary; OAuth2/OIDC actively contradicts QS-7 by requiring an external IdP. See [ADR-005](adr/ADR-005-auth-trust-boundary.md). |
| Auto-migrate-on-boot instead of the explicit `--db-migration` gate | Weakens a deliberate safety property (migration failing mid-boot while also trying to serve traffic) for a marginal QS-5 gain. See [ADR-010](adr/ADR-010-migration-gate.md). |
| Embedded Postgres or LiteDB instead of SQLite+blob-store | No real win while queue processing stays serial (SQLite's single-writer model isn't today's bottleneck); high migration cost against 30+ inherited EF migrations. See [ADR-001](adr/ADR-001-persistence-model.md). |
| Rust/C rewrite of yEnc or AES decode for a "native speed" win | yEnc decode is already native/SIMD via `RapidYencSharp`; AES decode already sits on .NET's OS-backed crypto primitives. No profiling evidence of a bottleneck here to justify the QS-7 cost of a new native toolchain. See [ADR-006](adr/ADR-006-usenet-client-layering.md). |
| Whole-system rewrite in Rust/Go/unified TypeScript | Backend is 96%+ inherited with zero test suite to validate behavioral parity; forfeits upstream mergeability entirely for a footprint gain the research agents' own hypotheses suggest is real but modest, since the actual hot path is already native. See §9.3. Revisit only as a deliberate strategic fork-divergence decision, not a performance optimization. |
| Full frontend framework rewrite (SvelteKit / SPA / htmx-from-backend) before measuring anything | SSR isn't actually in the streaming hot path, so the strongest performance argument for it doesn't hold up; run the cheap `ssr:false` experiment (P1-10) first. See [ADR-007](adr/ADR-007-frontend-ssr-and-proxy.md). |
| Splitting the backend into separately-versioned modules/projects | No second consumer of an extracted module exists at this deployment scale (one deployable artifact); adds build/dependency-graph complexity for no payoff. See §9.3. |

## 11.5 Open questions / unresolved

Each research pass ended with explicit open questions it couldn't resolve within its assigned file
scope. These are carried forward here verbatim rather than dropped, per arc42's own guidance that
"we don't know X, and it matters" is exactly what this section is for.

| # | Open question | Why it matters | Owner-in-waiting |
|---|---|---|---|
| OQ-1 | **Does `DavItem.Path` cascade-update for all descendants when a *directory* (not a single file) is moved/renamed via the WebDAV `MoveItemRequest`/`CopyRequest` handlers?** Not read in depth by the core-domain pass (time-boxed to the ingestion pipeline). | If it doesn't cascade, a directory move silently desyncs the denormalized `Path` from the real `ParentId` chain — everything that trusts `Path` directly (`.strm` generation, rclone-forget, §5.2.2) would then be wrong for every descendant, with no visible error. This is a correctness risk referenced from §5.2.2 and directly relevant to ADR-001. | Whoever next reads `backend/WebDav/Requests/MoveItemRequest.cs` / `CopyRequest.cs` |
| OQ-2 | **What does `UsenetSharp.YencStream.Dispose()` actually do when a consumer reads only the yEnc header and abandons the rest of a BODY response** — as every interpolation-search seek probe does (§6.2)? Does the connection get fully drained and returned to the pool, or forced into replacement (an extra reconnect per probe)? | This is a **prerequisite to sizing P1-1** (yEnc header caching), this document's #1 recommendation: if abandoning the response forces a reconnect, each *uncached* seek probe today already costs a full new connection, which changes both the urgency and the expected win of caching headers. Unverified because it requires inspecting the external `UsenetSharp` package's internals, out of scope for the usenet-streaming pass. | Whoever profiles or reads `UsenetSharp` source before implementing P1-1 |
| OQ-3 | **Can `ProviderCircuitBreaker` trips cause `HealthCheckService`'s `CheckAllSegmentsAsync`-style re-verification to report false "missing article" verdicts**, triggering unnecessary Arr remove-and-search cycles? | Potentially user-visible: a release could get needlessly deleted and re-searched because a *temporarily* circuit-broken provider looked like a permanently missing article, not because anything is actually wrong with the release. | Whoever next reads `HealthCheckService` alongside the Usenet client stack together |
| OQ-4 | **Can `HealthCheckService`'s background re-verification loop run concurrently with a queue item that's touching the same `DavItem` rows**, and if so, what (if anything) prevents a race? | Both are documented as separate hosted services/loops in this codebase; no concurrency control between them was confirmed in either the core-domain or api-auth passes. | Whoever next reads both `HealthCheckService` and `QueueItemProcessor` together |
| OQ-5 | **Does the `downloadKey` capability token (`SHA256(path + apiKey)`, §8.1) have any expiry or per-user scoping on the backend**, or is it valid indefinitely once generated? | An unbounded capability token embedded in a URL is a standing risk independent of any performance concern — any leak (browser history, referrer header, shared link) grants indefinite access to that file. Flagged by the frontend pass; the backend-side verification code (`GetWebdavItemRequest.VerifyDownloadKey`) wasn't traced for expiry logic specifically. | Whoever next reads `GetWebdavItemRequest.cs`'s verification path with expiry in mind |
| OQ-6 | Actual Docker image size and per-runtime layer breakdown *(hypothesis, unmeasured)* | Needed to turn ADR-003/P3-5's QS-4 discussion from a shape-of-the-risk statement into a number that can be tracked over time. | `docker build . && docker history` / `dive`, once |
| OQ-7 | Wall-clock container restart/recovery time after a crash *(hypothesis, unmeasured)* | Needed to put a real number on QS-5's "short bounded time" target (§10) instead of leaving it qualitative. | Kill the container mid-queue-item and time recovery, per the QS-5 confirming experiment in §10.2 |

## 11.6 Summary: the two highest-leverage moves in this entire document

If only two items from this backlog are acted on, per the research agents' converging analysis:

1. **P1-1 (yEnc header caching)** — the most concrete, narrowly-scoped fix directly addressing the
   user's "as performant as possible" brief, specifically for QS-1 (seek latency), which is this
   product's core value proposition.
2. **P1-5 / ADR-009 (harden `DISABLE_WEBDAV_AUTH`)** — not a performance item, but the single
   highest-severity finding across all five research passes: an externally-contributed, upstream-merged
   change to security-relevant code (its own commit message admits it was "vibe-coded"), with a real
   (if configuration-dependent) exposure window. Being INHERITED rather than fork-specific (see
   ADR-009) doesn't reduce the risk — it just means fixing it locally is a deliberate small
   divergence from upstream, and it's worth raising with the upstream project directly.
