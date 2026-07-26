# API / Auth / Cross-cutting Backend Services — Research Notes

Scope: `backend/Api/Controllers/**`, `backend/Api/SabControllers/**`, `backend/Auth/**`,
`backend/Middlewares/**`, `backend/Services/**`, `backend/Tasks/**`, `backend/Websocket/**`,
`backend/Config/**`, `backend/Program.cs`.

Tagging: **INHERITED** (authored by `nzbdav-dev` upstream) vs **FORK-SPECIFIC** (authored by
`habenspass`, `Claude`, or other fork contributors). Where a file has both, the tag notes which
parts are which.

---

## 1. Building blocks

### 1.1 Two REST API families on one Kestrel host

`Program.cs` (backend/Program.cs:83-133, INHERITED skeleton / FORK-SPECIFIC additions layered in)
wires a single ASP.NET Core host that serves three concerns side by side:

- **WebDAV protocol surface** — `NWebDav.Server`, mapped via `app.UseNWebDav()` (Program.cs:131),
  backed by `DatabaseStore` (outside this scope; documented by the core-domain agent).
- **UI-facing REST API** — `backend/Api/Controllers/*`, one controller class per endpoint,
  routed via `[Route("api/...")]` attributes, all standard ASP.NET `[ApiController]`s registered
  through `app.MapControllers()` (Program.cs:129).
- **SABnzbd-compatibility surface** — a single `SabApiController` (backend/Api/SabControllers/SabApiController.cs:25-65)
  registered at route `api` (any HTTP verb), which does its own internal dispatch by reading the
  SABnzbd `mode` query/form param (`GetController()`, SabApiController.cs:67-110) and delegating to
  one of ~11 nested `BaseController` implementations (AddFile, AddUrl, GetQueue, GetHistory,
  GetStatus, GetVersion, GetCategories, GetConfig, GetFullStatus, RemoveFromQueue,
  RemoveFromHistory). This mimics SABnzbd's single-endpoint-with-`mode`-param API shape exactly,
  which is what lets Sonarr/Radarr talk to NzbDav as if it were a real SABnzbd instance without
  any special integration code on their side.

Both controller families share one Kestrel process/thread pool with the WebDAV file-streaming
path (QS-1/QS-3 relevant — see §4).

### 1.2 Auth model — two independent secrets, no sessions

There are exactly two credential types in the backend, and they don't interact:

1. **WebDAV Basic Auth** (`backend/Auth/ServiceCollectionAuthExtensions.cs`,
   `backend/Auth/WebApplicationAuthExtensions.cs` — INHERITED) — a single hard-coded user/pass
   pair from `ConfigManager.GetWebdavUser()`/`GetWebdavPasswordHash()` (Config/ConfigManager.cs:140-154),
   validated via `NWebDav.Server.Authentication`'s `AddBasicAuthentication` with a 1-hour auth
   cookie cache (`ServiceCollectionAuthExtensions.cs:33-34`). This gates only the raw WebDAV
   protocol endpoints (PROPFIND, GET/HEAD on the DAV root), not the REST API.
   Can be fully disabled via `DISABLE_WEBDAV_AUTH=true`
   (`WebApplicationAuthExtensions.cs:16-21`) — see §3/§4, this is a **FORK-SPECIFIC** addition
   (commit `b696079`, "vibe-coded the disabling of frontend auth and webdav auth for
   authenticating proxies", authored by a third-party contributor "David Young", not
   `habenspass`).

2. **`x-api-key` / `FRONTEND_BACKEND_API_KEY`** — a single static shared secret (env var, required
   at startup via `EnvironmentUtil.GetRequiredVariable`) that gates:
   - Every `Api/Controllers/*` endpoint by default, enforced centrally in
     `BaseApiController.HandleApiRequest()` (backend/Api/Controllers/BaseApiController.cs:19-26):
     reads the key via `HttpContext.GetRequestApiKey()` and does a plain `==` string compare
     against the env var. Individual controllers opt out via `protected override bool
     RequiresAuthentication => false` (used by `JellyfinWebhookController` — see below — and
     nothing else in this scope).
   - Every SABnzbd-mode endpoint, enforced per-request in `SabApiController.BaseController.HandleRequest()`
     (SabApiController.cs:114-130), accepting **either** `FRONTEND_BACKEND_API_KEY` **or** a
     separate rotatable `ConfigManager.GetApiKey()` (`api.key` config value, falls back to the
     env var if unset — ConfigManager.cs:109-113) via `StringExtensions.IsAny` (multi-value
     compare). This second key is the one users paste into Sonarr/Radarr's "API Key" field —
     it's rotatable from the UI without redeploying the container, unlike the frontend/backend
     trust-boundary key.
   - The `/ws` WebSocket endpoint — `WebsocketManager.Authenticate()`
     (backend/Websocket/WebsocketManager.cs:69-73) requires the client to send
     `FRONTEND_BACKEND_API_KEY` as the *first text frame* within 5 seconds
     (`ReceiveAuthToken`, WebsocketManager.cs:138-154), rather than as a header/query param
     (WebSocket upgrade requests can't easily carry custom headers from a browser).
   - `HttpContextExtensions.GetRequestApiKey()` (backend/Extensions/HttpContextExtensions.cs:33-37)
     accepts the key from either the `x-api-key` header **or** an `apikey` query/form parameter —
     see §4 for the risk this creates.

   All of this is **INHERITED** except the SABnzbd controller's acceptance of the frontend key
   as an *additional* valid credential (that's inherited too, at the file level — same for
   ConfigManager's `GetApiKey()` fallback chain).

3. **Scoped per-path download keys** (`backend/Api/Controllers/GetWebdavItem/GetWebdavItemRequest.cs:41-52`,
   INHERITED) — the `view/{*path}` streaming endpoint (used by the frontend's Explore file
   browser and `.strm` files pointing at Jellyfin/media players) is **not** gated by the
   `x-api-key`/Basic-Auth mechanisms above at all; it's a plain `ControllerBase`, not
   `BaseApiController`. Instead it requires a `downloadKey` query param computed as
   `SHA256(path + apiKey)` (`GenerateDownloadKey`, GetWebdavItemRequest.cs:54-60). This is a
   deliberate and sound design: it lets a `.strm` file or a shared link embed a
   path-scoped, non-revocable-but-narrow credential in a URL (e.g. handed to a media player)
   without leaking the actual shared secret. `.ids`-prefixed paths (strm-by-id streams) use a
   *different* derivation key, `ConfigManager.GetStrmKey()` (`api.strm-key`, auto-populated by a
   migration — ConfigManager.cs:115-119), kept separate so rotating the general `api.key` doesn't
   invalidate already-generated `.strm` files.

4. **UI login accounts** (`Api/Controllers/Authenticate`, `CreateAccount`, `IsOnboarding` —
   INHERITED) — a conventional username/salted-hash account table (`Account`,
   `PasswordUtil.Hash/Verify`) used only by the frontend's own session/cookie login flow (frontend
   agent's scope covers how the session cookie is minted and enforced); the backend side here is
   just three thin CRUD-ish endpoints with no session state of its own — `AuthenticateController`
   (backend/Api/Controllers/Authenticate/AuthenticateController.cs:12-23) just returns a boolean,
   it's the frontend that turns that into a session.

5. **Jellyfin webhook token** (`ConfigManager.GetJellyfinWebhookToken()`, ConfigManager.cs:429-433
   — **FORK-SPECIFIC**, part of the predictive-prefetch feature) — a third independent shared
   secret, deliberately kept separate from `api.key` "so rotating it doesn't affect
   Sonarr/Radarr/SAB-style integrations" (comment, ConfigManager.cs:425-428). Checked as a plain
   `?apikey=` query-string compare in `JellyfinWebhookController.HandleRequest()`
   (backend/Api/Controllers/JellyfinWebhook/JellyfinWebhookController.cs:38-40), which is why the
   controller opts out of the standard `x-api-key` check (`RequiresAuthentication => false`,
   line 34).

### 1.3 Central exception handling

`ExceptionMiddleware` (backend/Middlewares/ExceptionMiddleware.cs — INHERITED) is one global
`app.UseMiddleware<ExceptionMiddleware>()` (Program.cs:125, registered *first*, before
WebSockets/health/controllers/webdav) that catches: aborted-request cancellations → HTTP 499,
`UsenetArticleNotFoundException`/`SeekPositionNotFoundException` → 404 (streaming-specific,
logs the DAV item path + seek position), and a catch-all for any other exception *while serving a
DavItem* → 500 with a structured log line. This middleware is WebDAV/streaming-oriented; the two
REST API controller bases (`BaseApiController`, `SabApiController`) each duplicate their own
try/catch → `{Status:false, Error:...}` JSON response shape instead of relying on this middleware
(BaseApiController.cs:30-54, SabApiController.cs:41-64) — i.e. there are **three parallel
exception-handling paths** for what is ultimately one host (see §4).

### 1.4 Program.cs wiring — hosted services & DI

Program.cs:89-120 registers, in order: WebDAV basic-auth (conditionally), ConfigManager,
WebsocketManager, `ProviderUsageStatsAggregator` (FORK-SPECIFIC, singleton, `LoadAsync()`'d
eagerly right after `app.Build()` at Program.cs:124 — the only service whose initial state is
force-loaded synchronously before `RunAsync()`), `UsenetStreamingClient`, `QueueManager`,
`PrefetchCacheService`+`EpisodeResolverService` (FORK-SPECIFIC), then 9 `IHostedService`s:
`HealthCheckService`, `ArrMonitoringService`, `BlobCleanupService`, `NzbBlobCleanupService`,
`HistoryCleanupService`, `DavCleanupService`, `CacheEvictionService` (FORK-SPECIFIC),
`UsenetFileToBlobstoreMigrationService`, `RemoveOrphanedFilesSchedulerService`. All hosted
services follow the same shape: a `BackgroundService.ExecuteAsync` `while
(!stoppingToken.IsCancellationRequested)` loop, catch `OperationCanceledException` guarded by
`SigtermUtil.IsSigtermTriggered()` to exit cleanly on shutdown, catch-all `Exception` → log +
`Task.Delay` backoff (typically 5-10s) before retrying — this is a consistent, load-bearing
convention across every service in this scope (BlobCleanupService.cs:44-55,
HealthCheckService.cs:81-90, CacheEvictionService.cs:34-44, etc.), and CLAUDE.md's instruction to
follow the `IHostedService` pattern for new periodic work is already exactly what's practiced
here.

---

## 2. Key runtime interactions

**Sonarr/Radarr → SAB-compatible ingestion path** (touchpoint only): `SabApiController` dispatches
`addfile`/`addurl` to `AddFileController`/`AddUrlController`, which write the raw NZB to
`BlobStore`, create a `QueueItem` row, broadcast `WebsocketTopic.QueueItemAdded`
(AddFileController.cs:91-93), and call `queueManager.AwakenQueue(...)` (line 96) to kick the
(out-of-scope) `QueueManager` loop — this is the only place this scope touches the Queue
subsystem. `queue`/`history` modes with `name=delete` route to
`RemoveFromQueueController`/`RemoveFromHistoryController`, which similarly just mutate DB rows and
broadcast `QueueItemRemoved`/`HistoryItemRemoved`.

**Frontend proxy → backend REST/WS**: every proxied frontend request arrives with `x-api-key:
$FRONTEND_BACKEND_API_KEY` injected by the frontend's Express proxy (frontend agent's scope);
backend-side this is indistinguishable from a Sonarr/Radarr request carrying the same header —
there is no way for the backend to tell "this came from our own frontend" apart from "this came
from an arr instance that happens to have the master key" (see §4, single-secret-tier risk).

**Jellyfin → prefetch pipeline** (FORK-SPECIFIC, touchpoint only):
`JellyfinWebhookController` → `EpisodeResolverService.ResolveNextEpisodeDavItemIdAsync` (Sonarr
series/episode lookup, backend/Services/EpisodeResolverService.cs:15-47) →
`PrefetchCacheService.TriggerPrefetchAsync` (backend/Services/PrefetchCacheService.cs:42-119,
detaches the actual download onto `Task.Run`, tagged as low-priority so it never contends with
active playback — comment at PrefetchCacheService.cs:131-134) → `CacheEvictionService` sweeps
every 5 minutes to enforce max-age/max-count/min-free-space retention
(backend/Services/CacheEvictionService.cs:20-56).

**Health-check/self-repair loop** (INHERITED): `HealthCheckService` continuously re-verifies
segment availability for already-downloaded `DavItem`s and, on missing articles, either deletes
blocklisted/orphaned files or calls into `ArrConfig.GetArrClients()` to trigger a fresh
Sonarr/Radarr search (backend/Services/HealthCheckService.cs:199-309) — this is the mechanism that
makes QS-6-adjacent provider failures self-heal at the *content* level (distinct from the
Usenet-client-level provider failover the core-domain/usenet agents cover).

---

## 3. Architectural decisions (tagged)

1. **Single static shared secret (`FRONTEND_BACKEND_API_KEY`) as the frontend/backend trust
   boundary, no per-client tokens** — INHERITED. Simple, zero-config, fits the single-container
   deployment (QS-7): one env var, no session store, no token issuance/refresh flow needed.
   Trade-off: same key authenticates the frontend, Sonarr, Radarr, and (via the separate
   `api.key`) arbitrary SAB-speaking clients — see §4.

2. **SABnzbd-API-compatibility as the Sonarr/Radarr integration strategy**, vs. writing a native
   download-client plugin for each — INHERITED, and clearly the correct call for this project:
   Sonarr/Radarr both already ship a generic SABnzbd client; mimicking that protocol means zero
   code changes needed on the arr side, at the cost of being bound to SABnzbd's `mode`-param
   dispatch shape and response JSON structure (`SabApiController.GetController()`, one static
   switch statement) rather than a RESTful resource design of NzbDav's own choosing.

3. **Rotatable secondary API key (`api.key`) layered on top of the immutable
   `FRONTEND_BACKEND_API_KEY`** — INHERITED (`ConfigManager.GetApiKey()`, ConfigManager.cs:109-113;
   accepted alongside the frontend key in `SabApiController.BaseController.HandleRequest()`,
   SabApiController.cs:118-122). Lets a user rotate the key they hand to Sonarr/Radarr from the
   UI without touching the container's env vars/restarting — a reasonable homelab-ergonomics
   choice, but it does mean the SAB surface has a strictly *larger* attack surface (two valid
   keys) than the plain REST API surface (one).

4. **Path-scoped HMAC-style download keys instead of reusing the raw API key in stream URLs** —
   INHERITED (`GetWebdavItemRequest.VerifyDownloadKey`, GetWebdavItemRequest.cs:41-52). Good
   security hygiene: `.strm` files and Explore-browser links that get handed to third-party media
   players never carry the actual shared secret, only a path-bound derivative.

5. **`DISABLE_WEBDAV_AUTH` full auth bypass for reverse-proxy setups** — **FORK-SPECIFIC**
   (commit `b696079`, third-party contributor, commit message literally says "vibe-coded").
   Rationale (inferred from the commit, not stated in code): users running NzbDav behind an
   authenticating reverse proxy (Authelia/Traefik forward-auth/etc.) don't want *two* logins
   stacked. But the implementation is a blunt global toggle with no compensating control (no
   trusted-proxy IP allowlist, no shared-secret header check that the proxy must inject) — see §4.

6. **Jellyfin webhook token kept separate from `api.key`** — FORK-SPECIFIC, deliberate (explicit
   comment, ConfigManager.cs:425-428) and good hygiene: an unrelated Jellyfin webhook URL leak
   doesn't compromise Sonarr/Radarr integration and vice versa.

7. **Two independent REST error-handling implementations (`BaseApiController` vs
   `SabApiController`) instead of one shared exception-mapping layer** — INHERITED. Keeps the
   SABnzbd surface's error *shape* (`SabBaseResponse`) authentically SABnzbd-like independent of
   the UI API's own `BaseApiResponse` shape, at the cost of duplicated try/catch logic (see §4).

8. **All background/periodic work modeled as `IHostedService` `BackgroundService`s with a
   uniform retry-loop shape**, rather than `Timer`/`Quartz`/cron — INHERITED convention, extended
   by every FORK-SPECIFIC service added since (`CacheEvictionService`,
   `RemoveOrphanedFilesSchedulerService`). Zero extra dependencies (fits QS-7), but see §4 for the
   `BaseTask` mutual-exclusion side effect this pattern interacts with.

---

## 4. Weak points / risks

- **[QS-7, security] `DISABLE_WEBDAV_AUTH=true` disables Basic Auth on the WebDAV surface with no
  compensating control.** There's no IP allowlist, no "trust this header from my reverse proxy"
  check — the moment the env var is set, `opts.RequireAuthentication = false` for the entire
  NWebDav pipeline (Program.cs:118-120) and `UseWebdavBasicAuthentication()` becomes a no-op
  (WebApplicationAuthExtensions.cs:23-27). If the container's WebDAV port is ever reachable
  without the reverse proxy in front of it (misconfigured port mapping, proxy restart racing
  container start, etc.), the entire virtual filesystem is unauthenticated. Log line
  (`"WebDAV authentication is DISABLED..."`, WebApplicationAuthExtensions.cs:12) is the only
  guard-rail. This is FORK-SPECIFIC and the origin commit self-describes as "vibe-coded" —
  worth a deliberate second look rather than treating it as settled upstream design.

- **[security] The shared `x-api-key`/`apikey` also accepted as a query/form parameter**
  (`HttpContextExtensions.GetRequestApiKey`, HttpContextExtensions.cs:33-37). Query-string
  secrets land in reverse-proxy access logs, browser history (if ever hit from a browser
  address bar), and `Referer` headers on outbound requests. This is how SABnzbd's own protocol
  works (Sonarr/Radarr send `apikey=` as a query param to SAB clients), so it can't be removed
  from the SAB surface without breaking compatibility — but it also silently applies to *every*
  `Api/Controllers/*` endpoint via the same helper, which don't need SABnzbd-shape compatibility.

- **[security] String equality (`==`) for API-key comparison, not constant-time.**
  `BaseApiController.cs:24`, `WebsocketManager.cs:72`, `SabApiController`'s `IsAny` chain all use
  plain string equality — theoretically timing-attackable, though practically low-severity given
  this is a homelab single-user deployment (not a multi-tenant service where an attacker gets
  many precisely-timed attempts).

- **[QS-4/QS-7, hypothesis] Single flat trust tier: one key authenticates the frontend, Sonarr,
  Radarr, *and* anything else someone points at the SAB surface.** There's no way to scope a key
  to "read-only" or "queue-management only" — compromising any one downstream consumer's stored
  key (e.g. a Radarr config backup leaking) hands over the same privileges as the frontend itself
  has. No measured incident; flagged as a design-shape observation, not a measured breach.

- **[maintainability] Three parallel error-response shapes for what's conceptually one API
  surface**: `ExceptionMiddleware` (streaming/DAV-item errors), `BaseApiController`
  (`BaseApiResponse` JSON), `SabApiController` (`SabBaseResponse` JSON, SABnzbd-shaped
  deliberately). A new endpoint author has to know which of the three conventions applies and
  why; nothing enforces it beyond controller-base-class choice.

- **[QS-4, maintainability] `BaseTask`'s mutual-exclusion semaphore is process-wide across *all*
  subclasses, not per-task-type.** `backend/Tasks/BaseTask.cs:9-10` declares `Semaphore` and
  `_runningTask` as `static` fields on the *abstract base class* — in C#, static fields on a
  non-generic base type are shared storage across every derived type. `RemoveSampleFilesTask`,
  `RemoveUnlinkedFilesTask`, and `StrmToSymlinksTask` all derive from `BaseTask` directly
  (not from a generic `BaseTask<T>`), so **only one of these three maintenance tasks can be
  "running" at a time, system-wide** — triggering `RemoveSampleFilesTask` while
  `RemoveUnlinkedFilesTask` is already mid-run makes `Execute()` return `false` immediately
  (BaseTask.cs:20-22) rather than queuing or erroring. This may well be an intentional
  "only one heavy maintenance sweep at a time" throttle (plausible given QS-4 on a homelab host),
  but nothing in the code documents that as deliberate — it reads exactly like a copy-paste
  base-class bug. Worth confirming intent before anyone "fixes" it or adds a fourth task type
  assuming independent concurrency.

- **[QS-2/QS-4, health-check completeness]** `app.MapHealthChecks("/health")`
  (Program.cs:127) is wired via bare `builder.Services.AddHealthChecks()`
  (Program.cs:88) with **zero registered health check implementations** in this scope — it's a
  liveness ping only (any registered ASP.NET Core middleware responding = "Healthy"), not a
  readiness check of DB connectivity, Usenet provider reachability, or queue-processing liveness.
  A container orchestrator's healthcheck hitting `/health` would report healthy even if every
  configured Usenet provider is down (that state is separately tracked and surfaced via
  `WebsocketTopic.UsenetConnections`/`HealthItemStatus`, but not through the HTTP health-check
  endpoint). Relevant to QS-5 (restart/recovery) if anything external (Docker healthcheck,
  Watchtower-style auto-restart) depends on `/health` reflecting real backend health.

- **[QS-1, hypothesis] No rate limiting anywhere in this scope** — the streaming (`view/{*path}`),
  REST API, and SAB surfaces have no request-rate or concurrent-connection caps at the API layer
  (any limiting/prioritization happens deeper, in the Usenet client's connection pool — out of
  this scope). On a single-container homelab deployment with one trusted key, this is a low-risk
  gap; would matter more if the deployment model ever changed to expose the API surface more
  broadly. No measurement exists; this is a hypothesis about exposure, not a measured incident.

- **[maintainability] `ProviderUsageStatsAggregator.LoadAsync()` runs synchronously between
  `app.Build()` and `app.RunAsync()`** (Program.cs:124) — the only startup-blocking async call in
  the pipeline. If this ever grows to do meaningful I/O (e.g., a larger stats backfill), it
  directly extends QS-5 (container start-to-ready time) since nothing else can serve traffic
  until it completes.

---

## 5. Alternatives brainstorm

**Auth alternatives** (all scored against QS-7 single-container deployability):

| Alternative | QS impact | Migration cost | Verdict for this deployment |
|---|---|---|---|
| Keep single static shared secret (status quo) | Neutral | None | Simple, fits homelab; already the model |
| Per-client API keys (one per Sonarr/Radarr/frontend) | Improves blast-radius containment (no QS directly, but reduces impact if one downstream leaks) | M — needs a keys table + UI to manage, `SabApiController`/`BaseApiController` key-check logic generalizes easily since they already support multi-value compare (`IsAny`) | Worth it only if the user actually runs multiple arr instances; for the common single-Sonarr+single-Radarr homelab case, low payoff for the added config-UI surface |
| mTLS between frontend and backend | Strong trust-boundary improvement | L — needs cert issuance/rotation, awkward for a `docker run` single-container deploy (QS-7), no clear win since frontend+backend already share a trust domain (same container/compose network) | Not worth it: over-engineered for a same-host trust boundary |
| OAuth2/OIDC for the REST API | N/A (no external identity provider in this deployment shape) | L — needs an IdP, session/token infra | Actively wrong fit: single-user homelab tool, adds an external dependency this project's whole design (QS-7) avoids |
| Replace `DISABLE_WEBDAV_AUTH` blanket bypass with a "trust this proxy" model (shared header secret the proxy must inject + verify, or bind WebDAV auth-skip to loopback/private-CIDR only) | Improves the QS-7-adjacent risk noted in §4 without losing the reverse-proxy use case | S-M — one new config value + one middleware check | Recommended — closes the "port exposed by accident" gap while still solving the actual problem (double-login with an authenticating proxy) |
| Constant-time key comparison (`CryptographicOperations.FixedTimeEquals`) | Closes a theoretical timing side-channel | S | Cheap, no downside; low priority given single-user threat model but free to fix |

**Sonarr/Radarr integration alternatives**: a native Sonarr/Radarr "download client" plugin (if
either project supported third-party download-client plugins) would give a cleaner
protocol/versioning story than piggybacking on SABnzbd compatibility, but neither Sonarr nor
Radarr has a stable third-party download-client plugin API — SABnzbd-compatibility is the only
practical integration point today. No actionable alternative here; INHERITED choice stands.

**Process/host topology**: separating the WebDAV surface and REST/SAB API surface into two
processes (e.g., two Kestrel hosts on different ports, or two containers) would reduce blast
radius (a runaway REST request can't directly starve WebDAV Kestrel threads or vice versa) but
directly hurts QS-7 (two things to `docker run`/monitor instead of one) and QS-4 (duplicate
thread-pool/GC overhead) for a homelab target that explicitly wants a single container. Not
recommended given the stated deployment constraint; the shared-host design is the right call here.

---

## 6. Optimization / improvement candidates

1. **Harden `DISABLE_WEBDAV_AUTH`**: require a second signal (e.g. a `TRUSTED_PROXY_HEADER_SECRET`
   env var that must match a header injected by the proxy, or restrict the bypass to
   loopback/`X-Forwarded-For`-verified requests) instead of a bare boolean. QS improved: closes
   the accidental-exposure gap noted in §4. Effort: S-M. Risk: low (additive, opt-in).

2. **Add real readiness checks to `/health`** (DB reachable, at least one Usenet provider
   reachable) via `AddHealthChecks().AddCheck(...)`, distinct from a plain liveness ping. QS
   improved: QS-5 (restart/recovery — orchestrators can now tell "started" from "actually ready").
   Effort: S. Risk: low.

3. **Clarify/fix the cross-task-type `BaseTask` mutual exclusion** (§4): either document it as
   intentional ("only one maintenance sweep runs at a time, by design") or make the
   semaphore/`_runningTask` pair per-concrete-type (e.g. a static dictionary keyed by
   `typeof(this)`, or a generic `BaseTask<TSelf>`). QS improved: none directly, but prevents a
   confusing silent no-op (`Execute()` returning `false`) from being mistaken for success by a
   future caller (e.g. a scheduled trigger silently skipping because an unrelated task happened to
   be running). Effort: S. Risk: low (behavior-preserving if documented; small if changed to
   per-type).

4. **Constant-time API key comparison.** Effort: S. Risk: none. Low priority but essentially free.

5. **Unify the three error-response code paths** (`ExceptionMiddleware`, `BaseApiController`,
   `SabApiController`) behind one shared "map exception → status code" helper, even if the two
   controller bases still project to different JSON shapes at the end. Effort: M (touches every
   controller base, needs care not to change the SAB response shape Sonarr/Radarr parse). Risk:
   medium — regression risk on the SAB surface specifically, since Sonarr/Radarr's SAB client
   parses response shape strictly.

---

## OPEN QUESTIONS FOR SYNTHESIS

- **Frontend-side proxy auth injection** (how `x-api-key` gets attached to proxied requests, and
  what protects the frontend's own session cookie) is the frontend agent's territory — this
  report only covers what the backend does once a request arrives with (or without) that header.
- **WebDAV auth vs REST API auth interaction when `DISABLE_WEBDAV_AUTH` is set together with
  the frontend's own analogous auth-disable flag** (`b696079` touched both backend WebDAV auth
  and several frontend files) — whether the two disable flags are meant to be set together,
  independently, or whether one implies a security expectation about the other, needs the
  frontend agent's view to fully assess.
- **Usenet provider circuit-breaker / connection-pool interaction with `HealthCheckService`'s
  repair loop** (does a provider being circuit-broken affect `CheckAllSegmentsAsync` outcomes,
  and could that cause spurious "unhealthy" verdicts that trigger unnecessary Arr
  remove-and-search cycles?) is best assessed by whichever agent owns `Clients/Usenet/**`.
- **`ProviderUsageStatsAggregator` internals** (it's constructed/loaded in Program.cs but its
  implementation lives under `Clients/Usenet/Statistics`, outside this scope) — the
  usenet-client-stack agent should confirm whether its `LoadAsync()` cost is negligible or worth
  revisiting per optimization candidate on startup blocking.
