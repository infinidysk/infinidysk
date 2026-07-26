# 9. Architecture Decisions

## 9.1 Methodology

Every decision below is tagged by checking `git log --format='%an' -- <path>` against the known
split: **415 commits are `nzbdav-dev`/upstream contributors** (`nzbdav-dev`, `David Young`,
`Root-Core`, `Anthony Hoivik`, `Evan`, and others merged via PR), versus a handful of
**fork-specific commits by `habenspass`** (and one by "Claude", where this fork used Claude Code to
author a feature). A decision tagged **INHERITED** was made by upstream and is not this fork's
choice to unilaterally reverse without pricing in lost upstream mergeability (§2.2). A decision
tagged **FORK-SPECIFIC** was made in this fork and is fully this fork's to revisit.

**Verify, don't infer from author name alone**: a contributor's name is not sufficient evidence of
fork-vs-upstream status by itself — a commit by an external contributor can still be merged
*upstream* (as `David Young`'s and `Root-Core`'s are). D15/[ADR-009](adr/ADR-009-webdav-auth-bypass.md)
was initially mis-tagged FORK-SPECIFIC on exactly this mistake and was corrected after confirming
against the upstream repo directly (`GET api.github.com/repos/nzbdav-dev/nzbdav/commits/<sha>`).
Any tag in the inventory below not independently re-verified this way should be read as "author-name
inference," not a hard guarantee.

The ten most consequential decisions are expanded as individual ADRs in [`adr/`](adr/), each
including alternatives actually considered and their cost against the quality scenarios in §10.
The full inventory below is the complete list found across all five research passes plus this
document's own system-level analysis (§9.3).

## 9.2 Full decision inventory

### Persistence & core domain

| # | Decision | Tag | Rationale |
|---|---|---|---|
| D1 | SQLite as sole datastore for the virtual filesystem tree | INHERITED | Single-file, zero-ops, trivially backed-up; fits single-container deployment (QS-7) perfectly. See [ADR-001](adr/ADR-001-persistence-model.md). |
| D2 | Blob-store split (flat zstd+MemoryPack files) for per-segment metadata, SQLite JSON columns kept as legacy read-fallback | INHERITED | Keeps the SQLite file and its WAL small regardless of segment count (a remux can exceed 5,000 segments as JSON row values). Itself a completed migration away from an earlier INHERITED design. See [ADR-001](adr/ADR-001-persistence-model.md). |
| D3 | Denormalized materialized `Path` on `DavItem` | INHERITED | O(1) path reads everywhere (WebDAV, `.strm` generation, rclone-forget), at the cost of needing rename/move cascade logic (unverified for directory moves — §11). |
| D4 | Serial, one-item-at-a-time queue processing across releases; bounded concurrency only within one release | INHERITED | See [ADR-002](adr/ADR-002-serial-queue-processing.md). |
| D5 | Deobfuscation via first-segment + PAR2-descriptor sniffing before downloading whole files | INHERITED | Makes filename/identification work possible on a tiny fraction of total bytes — the concrete mechanism keeping QS-2 app-overhead low. |
| D6 | PAR2 descriptor-only parsing, no repair/reconstruction capability | INHERITED | Cheaper to build than a full Reed-Solomon engine; "fail and let the Arr re-grab" substitutes for repair. |
| D7 | Single shared ingestion code path for SABnzbd-API and WebDAV watch-folder uploads | INHERITED | `DatabaseStoreCategoryWatchFolder` calls `AddFileController` directly — avoids duplicating queueing/validation logic. |
| D8 | Manual migration gate (`--db-migration`), no auto-migrate-on-boot; one hardcoded hard-stop (`BlockUpgradesToV06X`) for a specific breaking migration | INHERITED | See [ADR-010](adr/ADR-010-migration-gate.md). |
| D9 | Prefetch-cache short-circuit ahead of the Usenet stream path (Jellyfin webhook-driven) | FORK-SPECIFIC | Serves a fully-local file for predicted-next-episode playback, bypassing Usenet entirely for that case. |
| D10 | Sample-file rejection logic | FORK-SPECIFIC | Narrow, additive UX tweak. |

### API, auth & backend services

| # | Decision | Tag | Rationale |
|---|---|---|---|
| D11 | Single static shared secret (`FRONTEND_BACKEND_API_KEY`) as the frontend/backend trust boundary | INHERITED | See [ADR-005](adr/ADR-005-auth-trust-boundary.md). |
| D12 | SABnzbd-API-compatibility as the Sonarr/Radarr integration strategy | INHERITED | See [ADR-004](adr/ADR-004-sabnzbd-compatibility.md). |
| D13 | Rotatable secondary API key (`api.key`) layered on top of the immutable frontend key | INHERITED | Lets a user rotate the Sonarr/Radarr-facing key from the UI without redeploying — but doubles the SAB surface's valid-credential set. |
| D14 | Path-scoped HMAC-style download keys instead of reusing the raw API key in stream URLs | INHERITED | Sound hygiene: `.strm` files/media-player links never carry the shared secret. |
| D15 | `DISABLE_WEBDAV_AUTH` full auth bypass for reverse-proxy setups | INHERITED (external contributor, merged upstream — see [ADR-009](adr/ADR-009-webdav-auth-bypass.md) for the verification) | Flagged for reconsideration despite being inherited, not settled design. |
| D16 | Jellyfin webhook token kept deliberately separate from `api.key` | FORK-SPECIFIC | Good hygiene, explicit in-code rationale. |
| D17 | Two independent REST error-handling shapes (`BaseApiController` vs `SabApiController`) instead of one shared mapper | INHERITED | Keeps the SAB surface protocol-authentic for Sonarr/Radarr's parser. |
| D18 | All background/periodic work modeled as `IHostedService` `BackgroundService`s with a uniform retry-loop shape | INHERITED convention, extended by fork | Zero extra dependencies; matches CLAUDE.md's own stated convention. |

### Usenet client stack & streaming

| # | Decision | Tag | Rationale |
|---|---|---|---|
| D19 | Raw NNTP protocol + yEnc decode delegated to external `UsenetSharp`/`RapidYencSharp` (native SIMD) | INHERITED | See [ADR-006](adr/ADR-006-usenet-client-layering.md) — corrects the "hand-rolled" framing; only the layers above this are original. |
| D20 | Hand-rolled decorator layering (pool, failover, circuit-breaker, priority, throttling) on top of the external client | INHERITED structure, FORK-SPECIFIC decorators added | Each concern implements the same `INntpClient` interface — composable, but every new concern is another layer priority/cancellation must thread through correctly. |
| D21 | Per-provider `ConnectionPool`, purely on-demand (no pre-warming) | INHERITED | Simple; costs a synchronous TCP+TLS+AUTHINFO round trip on the first request after any idle period. |
| D22 | `ArticleCachingNntpClient` scoped only to Queue ingestion, never used on the streaming/seek path | INHERITED | See §6.2 — this is the single most actionable QS-1 finding in the document. |
| D23 | `PrioritizedSemaphore` (odds-based, two-queue) instead of separate pools per priority class or OS-level QoS | INHERITED (pool gate), FORK-SPECIFIC reuse (download semaphore, bandwidth throttle) | An in-process, config-tunable scheme is the pragmatic choice without root/NET_ADMIN for OS-level QoS. |
| D24 | `CancellationToken`-keyed static ambient context for priority propagation across a detached background task | INHERITED | Unusual (vs. `AsyncLocal`) but necessary given the detached-task lifecycle; carries a leak risk if a caller fails to dispose (§11). |
| D25 | Bandwidth throttling (`TokenBucket`) and per-provider usage stats as additional stream decorators | FORK-SPECIFIC | Follows the exact composition pattern already established upstream — low risk, high consistency. |
| D26 | Custom `Stream` composition per request instead of one monolithic stream class | INHERITED | Lets RAR/multipart/AES/plain cases share fetch primitives while composing only the transform each needs. |
| D27 | Failure-count-only circuit breaking (3 consecutive failures), not latency-aware | INHERITED | Simple, static thresholds; misses "technically succeeding but slow" providers (§11). |

### Frontend

| # | Decision | Tag | Rationale |
|---|---|---|---|
| D28 | SSR via React Router 7 for what is largely an authenticated internal admin/queue UI | INHERITED | See [ADR-007](adr/ADR-007-frontend-ssr-and-proxy.md). |
| D29 | Hand-rolled Express server (proxy + auth + websocket relay + SSR in one process) instead of the stock React Router server or a separate reverse proxy | INHERITED | See [ADR-007](adr/ADR-007-frontend-ssr-and-proxy.md). |
| D30 | Session cookie auth for the browser UI, separate sha256 capability-token scheme for streamed media links | INHERITED | Two mechanisms coexist because external players can't participate in cookie-based session auth. |
| D31 | API key injection centralized in the proxy layer, but duplicated in `backend-client.server.ts` for loader-initiated calls | INHERITED | Works, but is genuinely two code paths for the same concern (§11). |
| D32 | Real-time queue/history/connection-count via one relayed WebSocket rather than polling | INHERITED | Evolved feature, not a day-one design (iterative git history). |
| D33 | All fork-specific frontend work is feature/settings UI only — zero changes to `server.ts`/`app.ts`/`websocket.server.ts`/`auth-middleware.server.ts`/`routes.ts` | FORK-SPECIFIC (absence of change, confirmed) | Frontend architecture is entirely untouched by this fork to date. |

### Deployment

| # | Decision | Tag | Rationale |
|---|---|---|---|
| D34 | Single combined Docker image bundling both language runtimes | INHERITED | See [ADR-003](adr/ADR-003-single-container-deployment.md). |
| D35 | Hand-rolled shell-script process supervision (`entrypoint.sh`), no s6-overlay/supervisord/tini | INHERITED | See [ADR-008](adr/ADR-008-process-supervision.md). |
| D36 | Backend does not start serving until migration completes; frontend does not start until backend reports healthy | INHERITED | Deliberate ordering — avoids the frontend proxy hitting a not-yet-live backend, and avoids serving traffic during a migration. |
| D37 | PUID/PGID runtime user remapping via `su-exec` + dynamic `useradd`/`groupadd` | INHERITED | Standard self-hosted-container convention (linuxserver.io-style) for bind-mount permission compatibility. |
| D38 | Branch-per-push Docker publishing + date-versioned pre-release + release-please semver releases | INHERITED | Every branch gets a pullable image; no CI correctness gate precedes any of these publishes. |
| D39 | No CI test/lint gate before image push | INHERITED (repo-wide characteristic) | Consistent with "no backend test project, no frontend test suite" per CLAUDE.md. |

## 9.3 System-level decisions (not owned by any single subsystem)

The user's brief explicitly put **programming language**, **frameworks**, and **modularization**
on the table system-wide, not just per-subsystem. This subsection is this document's own analysis
— it wasn't assigned to a research agent because no single file scope owns "should this whole
project be a different language."

### Should the backend be rewritten in a different language (Rust, Go, or unified into the frontend's TypeScript)?

**Recommendation: no, not currently.** The case for each alternative:

- **Go**: goroutines are a natural fit for the NNTP-heavy concurrent I/O workload; a single static
  binary would eliminate the ASP.NET Alpine runtime layer entirely (real QS-4 win); `golang.org/x/net/webdav`
  exists as a WebDAV server foundation; cgo bindings to `rapidyenc` (the same native decoder already
  in use) are feasible.
- **Rust**: similar QS-4 footprint benefits, memory safety, `tokio` is an excellent fit for this
  workload; steeper learning curve; WebDAV/EF-Core-equivalent crates are less mature than Go's.
- **Unifying into one Node/TypeScript runtime**: would eliminate "two runtimes in one container"
  outright (the single biggest QS-4/QS-7 lever available), reusing the frontend's existing language
  — but Node is weaker for the CPU-bound work (RAR/7z parsing, AES decode) unless delegated to native
  addons, which still need a yEnc decoder binding analogous to what C# already has via
  `RapidYencSharp`.

**Why none of these are recommended right now**: the backend (`backend/Queue`, `backend/Database`,
`backend/WebDav`, `backend/Par2Recovery`) is **96%+ INHERITED** — 30+ EF Core migrations, RAR/7z/PAR2
deobfuscation heuristics tuned against real-world obfuscated-release naming conventions, and the
completed SQLite→blob-store migration would all need re-implementing from scratch, **with no test
suite anywhere in this repo to validate behavioral parity against.** That combination — huge
inherited surface area, zero regression safety net — makes a language rewrite the single most
expensive and highest-risk item anywhere in this document, for a payoff (smaller container image,
somewhat lower idle RAM) that the research agents' own hypothesis-labeled findings suggest is likely
real but modest, since the actual hot path (yEnc decode) is **already** native/SIMD via
`RapidYencSharp` — the biggest classic "rewrite it in Rust for speed" win is already banked. It also
forfeits the ability to pull upstream's ongoing fixes/features entirely, the same cost noted for
every INHERITED-domain alternative in this document (§2.2).

**When this calculus would change**: if the fork maintainer decides to permanently diverge from
upstream regardless of language (e.g., upstream becomes unmaintained, or the fork's own feature set
outgrows what's practical to keep merging), a full rewrite becomes a first-class option again — but
that's a strategic call about the fork's relationship to upstream, not a performance decision, and
should be made explicitly rather than by accretion.

### Frontend framework choice

Covered in depth in `_research/frontend.md` and [ADR-007](adr/ADR-007-frontend-ssr-and-proxy.md).
Summary: SSR is **not** actually in the streaming hot path (file links are plain `<a href>` tags that
bypass React Router loaders entirely), which weakens the case for ripping it out — but flipping
`ssr:false` (already anticipated by an inline code comment) is a nearly-free experiment to test real
QS-4 footprint gains before committing to any bigger frontend change. A full framework swap
(SvelteKit, plain SPA, htmx-served-from-the-backend) is technically feasible but is the single
largest frontend rewrite discussed in this document, for a UI that is entirely behind auth with no
SEO/first-paint requirement — recommended only if the SSR-off experiment first demonstrates a real
QS-4 problem worth solving.

### Modularization

The backend is currently a single project, layered by technical concern (`Api`, `Auth`, `Clients`,
`Database`, `Queue`, `Streams`, `WebDav`) rather than split into independently-versioned/deployable
modules or projects. **Recommendation: keep it this way.** A "modular monolith" split into separate
internal projects (e.g., extracting the Usenet client stack or Queue engine into their own
assemblies) would add real build/dependency-graph complexity for a benefit — independent
deployability, independent versioning, enforced module boundaries — that has **no payoff at this
deployment scale**: there is exactly one deployable artifact (§2, §7), so there is no second consumer
of an extracted module to justify the boundary cost. The layered-by-concern structure already maps
cleanly to the domain (confirmed by how cleanly the five research agents' scopes divided along it)
and doesn't need microservice-style decomposition, which would actively work against QS-4/QS-7 (more
processes, more inter-process overhead) for a single-container homelab target.
