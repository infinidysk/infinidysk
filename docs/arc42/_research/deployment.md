# Deployment View — Research Notes

Scope: root `Dockerfile`, `entrypoint.sh`, `.github/workflows/**`, `CONTRIBUTING.md`, `scripts/**`, `version.txt`, migration-gate in `backend/Program.cs`.

Deployment target (fixed constraint for this whole section): **a single local Docker container on a homelab-style host** — no orchestrator, no elastic scaling, no external DB/cache/broker service. Every alternative below is scored against that.

---

## 1. Building blocks / Deployment view (arc42 §7)

### Container image: 3-stage build, 1 runtime image

`Dockerfile:1-45`:

- **Stage 1 `frontend-build`** (`node:alpine`, `--platform=$BUILDPLATFORM`): `npm install` → `npm run build` (react-router SSR build) → `npm run build:server` (compiles `server.js`/`app/` via `tsc` to `dist-node/`, per `frontend/package.json:6` `"build:server": "tsc -p tsconfig.node.json --outDir dist-node"`) → `npm prune --omit=dev` (Dockerfile:9-12).
- **Stage 2 `backend-build`** (`mcr.microsoft.com/dotnet/sdk:10.0-alpine`, also `--platform=$BUILDPLATFORM`): `dotnet restore` + `dotnet publish -c Release -r linux-musl-${TARGETARCH} -o ./publish` (Dockerfile:18-22). `TARGETARCH` is a build ARG resolved by buildx per target platform — this is how a single `docker buildx build --platform linux/amd64,linux/arm64` produces both architectures from `BUILDPLATFORM`-pinned (i.e. build-host-native, not emulated) build stages, while only the final `linux-musl-${TARGETARCH}` publish output is architecture-specific. This avoids QEMU-emulating the whole npm/dotnet build, only the final stage's base image is multi-arch.
- **Stage 3 (final/runtime)**: `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (ASP.NET *runtime*, not SDK — correctly slimmer). Installs `nodejs npm libc6-compat shadow su-exec bash curl tzdata` via apk (Dockerfile:31), then copies frontend `node_modules`, `package.json`, compiled `dist-node/server.js`, and `build/` output, plus backend's `publish/` output (Dockerfile:34-40). Single `EXPOSE 3000`. `CMD ["/entrypoint.sh"]`.

**Both runtimes (.NET + Node) are bundled into one final image** — this is the single most consequential deployment decision in the repo. It directly serves QS-7 (single `docker run`, one image, one volume) at the cost of QS-4 (image carries a full ASP.NET Alpine runtime *and* a full Node/npm runtime side by side — two language runtimes' worth of base footprint plus `frontend/node_modules` runtime deps).

No `.dockerignore` file exists at the repo root (confirmed absent). This doesn't affect final image size (multi-stage `COPY --from=` only pulls named artifacts), but it does mean the full build context (potentially including local `bin/`, `obj/`, `node_modules/`, `.git/` if present on the build host) is sent to the Docker daemon/buildx on every build — a build-time cost, not a runtime one.

### Process topology inside the running container

`entrypoint.sh` is the container's PID 1 (`CMD ["/entrypoint.sh"]`, no `tini`/`dumb-init`/exec-wrapper in front of it). It is a **hand-written POSIX-ish shell script acting as an ad hoc supervisor** — there is no s6-overlay, supervisord, or any process-supervision framework. Sequence:

1. Resolve `PUID`/`PGID` (default 1000/1000), create or reuse matching group/user via `getent`/`addgroup`/`adduser` (`entrypoint.sh:39-58`).
2. Default `BACKEND_URL`, `FRONTEND_BACKEND_API_KEY` (random if unset), `CONFIG_PATH=/config` if not provided by the user (`entrypoint.sh:61-71`).
3. `chown` `$CONFIG_PATH` to `$PUID:$PGID`; if `db.sqlite` already exists with different ownership, recursively `chown -R` the whole config dir (`entrypoint.sh:74-83`).
4. Run migration: `su-exec "$USER_NAME" ./NzbWebDAV --db-migration`, hard-exit on nonzero (`entrypoint.sh:87-93`).
5. Start backend in background via `su-exec`, capture `$BACKEND_PID` (`entrypoint.sh:96-97`).
6. Poll `$BACKEND_URL/health` (backend's ASP.NET `MapHealthChecks("/health")`, `backend/Program.cs:127`) up to `MAX_BACKEND_HEALTH_RETRIES` (default 30) × `MAX_BACKEND_HEALTH_RETRY_DELAY` (default 1s) = 30s default budget, before giving up and killing the backend + exiting 1 (`entrypoint.sh:100-118`).
7. Start frontend (`npm run start` → `node dist-node/server.js`) in background, capture `$FRONTEND_PID` (`entrypoint.sh:121-123`).
8. `wait_either` polls both PIDs every 0.5s (`entrypoint.sh:3-16`) — **not a signal-driven wait**, a busy-poll loop.
9. Whichever process exits first, the script kills the other and exits with the dead process's exit code (`entrypoint.sh:126-138`).
10. `trap terminate TERM INT` (`entrypoint.sh:30-37`) forwards SIGTERM/SIGINT from Docker to both children, waits for them, then exits 0.

Both backend and frontend run **as the same resolved `$USER_NAME`** (dropped from root via `su-exec`), not root — reasonable, PUID/PGID-aware, matches common self-hosted-app conventions (linuxserver.io-style).

### Backend graceful shutdown

`backend/Utils/SigtermUtil.cs` hooks `AssemblyLoadContext.Default.Unloading` (the .NET-idiomatic way to observe SIGTERM under `dotnet`/ASP.NET) and exposes a shared `CancellationToken` (`SigtermUtil.cs:20-38`), consumed by long-running loops such as `HealthCheckService.ExecuteAsync` (`backend/Services/HealthCheckService.cs:70-80`) to distinguish an expected shutdown-triggered `OperationCanceledException` from a real error. This is a clean, idiomatic cooperative-cancellation pattern, not a hack.

### CI: what actually gets validated before an image ships

All four workflows (`branch.yml`, `pre-release.yml`, `release.yml`, `dependabot.yml`) are **pure build-and-push pipelines** — none of them run `dotnet build`, `dotnet test`, `npm run typecheck`, or any linter before pushing to a registry. This matches CLAUDE.md's statement precisely: CI only builds/pushes Docker images. The only gate an untested change passes through before landing in a published image tag is **whether `docker build` itself succeeds** (i.e., `dotnet restore/publish` and `npm install/build` must not error) — there is no correctness gate at all, only a "does it compile/build" gate. `frontend/package.json`'s `typecheck` script (`react-router typegen && tsc -b`) exists but is **not invoked anywhere in CI** — it's a manual pre-PR step per `CONTRIBUTING.md:73-79` ("You might check types before creating a PR"), phrased as optional, not enforced.

- `branch.yml`: any push to any branch except `main`/`dependabot/**` → builds & pushes `ghcr.io/<repo>:<sanitized-branch-name>` (amd64+arm64). This is how `test_build` / ad hoc branch images get published (matches CLAUDE.md's docker build example and the recent commit `399053e Test build`).
- `pre-release.yml`: every push to `main` that isn't a release-please merge commit → pushes `:pre-release` tag to both GHCR and Docker Hub, versioned by build date (`date +%Y-%m-%d`), not semver.
- `release.yml`: on push to `main`, runs `release-please` (config `.release-please-config.json`, manifest `.release-please-manifest.json`, currently `0.6.4`); if a release was created, re-tags `latest`/`vX.x`/`vX.Y.x` git tags and pushes a versioned image to GHCR + Docker Hub under multiple tag aliases (`:alpha`, `:latest`, `:X.x`, `:X.Y.x`, `:X.Y.Z`).
- `dependabot.yml`: dependabot branches get their own isolated GHCR repo (`ghcr.io/<repo>-dependabot`) — keeps dependency-bump build artifacts out of the main image namespace.

`version.txt` (repo root, currently `0.6.4`) mirrors `.release-please-manifest.json`; `NZBDAV_VERSION` is passed as a Docker build ARG (`Dockerfile:41-42`) and baked in as an env var, surfaced presumably in the UI/health output (not traced further — out of my scope, core-domain/frontend territory).

### `scripts/`

Only one file, `scripts/jellyfin-webhook-logger.py` — a standalone dev-time HTTP listener for capturing raw Jellyfin webhook payloads to a local `.jsonl` file (`scripts/jellyfin-webhook-logger.py:1-15`). Fork-specific (`habenspass`, added alongside "Phase 1 of predictive episode-prefetch caching"), **not part of the build/deploy pipeline** — it's a local debugging aid for developing the Jellyfin-webhook prefetch feature, never invoked by Dockerfile/entrypoint/CI.

---

## 2. Key runtime interactions

### Startup sequence (cold start, e.g., first `docker run`)

```
docker run (PID 1 = entrypoint.sh)
  → resolve PUID/GID, create user/group
  → chown /config (recursive only if ownership mismatch detected)
  → su-exec user ./NzbWebDAV --db-migration   [blocking, foreground]
       → BlockUpgradesToV06X() check (backend/Program.cs:136-168)
       → EF Core Database.MigrateAsync(...)
       → optional VACUUM if configured
       → process exits (this is a one-shot CLI mode, not the server)
  → su-exec user ./NzbWebDAV &                [background, real server starts]
  → poll $BACKEND_URL/health until 200 or 30 retries × 1s exhausted
  → su-exec user npm run start &              [background, frontend starts]
  → busy-wait on both PIDs every 0.5s
```

Total startup-to-ready latency (QS-5) is bounded by: migration time (SQLite, incremental EF migrations — normally sub-second unless a large data migration like `UsenetFileToBlobstoreMigrationService` runs as a *separate* hosted service post-start, not part of `--db-migration` itself) + backend cold-start to first `/health` 200 + frontend Node startup. **No component of this path is asynchronous/overlapped** — frontend does not start until backend already reports healthy, by design (avoids the frontend's Express proxy hitting a not-yet-live backend). This is a deliberate ordering choice, not an oversight.

### Unclean shutdown / restart

- On SIGTERM (e.g., `docker stop`, or `docker restart`): `terminate()` trap fires, forwards SIGTERM to backend then frontend, blocks on `wait`, exits 0 (`entrypoint.sh:24-37`). Backend's own `SigtermUtil` cooperative-cancellation lets in-flight loops (health-check background service, queue processing) observe cancellation and exit cleanly rather than being hard-killed — *if* they finish within Docker's default grace period (10s) before SIGKILL. Long-running article downloads/health-check repairs are not obviously bounded to under 10s (hypothesis — no test/log evidence either way; would need to time a `docker stop` against an in-flight large-file health-check repair to confirm).
- If the **backend process dies unexpectedly** (crash, OOM-kill by the kernel, unhandled exception escaping `WebApplication.RunAsync`): `wait_either` detects it, entrypoint kills the frontend, and **the container exits** with the backend's exit code (`entrypoint.sh:128-138`). This means container-level restart (via Docker's `--restart` policy, e.g. `unless-stopped`) is the *only* recovery mechanism for a backend crash — there is no in-container respawn/retry of just the backend while keeping the frontend alive. Equally, if the **frontend** crashes, the *backend* also gets killed and the whole container exits — a crash in either half takes down the other, by design of this script (not a bug, but a real coupling: you cannot restart one runtime without restarting both).
- No `docker restart` verification was performed (no running container available in this research pass) — the sequence above is derived from reading the script logic, not observed; flag as **(hypothesis)** for the actual wall-clock recovery time.

---

## 3. Architectural decisions (tagged)

| Decision | Tag | Notes |
|---|---|---|
| Single combined image (backend + frontend in one container) instead of two images/services | **INHERITED** (Dockerfile structure predates fork; `git log --format='%an' -- Dockerfile` shows 8/10 commits by `nzbdav-dev`, 1 `Arya`, 1 `Root-Core` — none by `habenspass`) | Directly maximizes QS-7 (`docker run` + one volume, no compose file required) at the cost of QS-4 and QS-8 (see below). |
| SQLite as the persistence layer (file in `$CONFIG_PATH/db.sqlite`, `backend/Database/DavDatabaseContext.cs:16-17,21`) | **INHERITED** | No separate DB container/service to run/link — core enabler of QS-7. Also means DB durability = filesystem durability of the mounted `/config` volume; no independent backup/replication story beyond "back up the directory," which is explicitly what `BlockUpgradesToV06X` tells users to do before a breaking migration (`backend/Program.cs:161-163`). |
| Manual migration gate (`--db-migration` CLI arg, run once by entrypoint before the server starts) instead of migrate-on-boot inside the ASP.NET host | **INHERITED** (`Program.cs` migration-gate logic; git log shows only `nzbdav-dev`/`Claude` authorship on `Program.cs`, `habenspass` commits are feature work — episode-prefetch, provider-stats — not the gate itself) | CLAUDE.md explicitly calls this out as deliberate ("the app deliberately refuses to auto-migrate on normal startup"). Trades a small amount of QS-5 friction (extra process invocation) for safety (a botched migration can't half-apply while the server is also trying to serve traffic). |
| Explicit backward-compatibility hard-block for the 0.6.0 breaking migration (`BlockUpgradesToV06X`, `Program.cs:136-168`), requiring `UPGRADE=0.6.0` env var acknowledgement | **INHERITED** | A one-off, versioned escape hatch rather than a general mechanism — there's no generalized "confirm risky migration" framework, just this ad hoc check keyed to one specific migration name. Sets a precedent CLAUDE.md flags as noteworthy; future breaking migrations would presumably need a similar bespoke gate added by hand. |
| Branch-per-push Docker image publishing (`branch.yml`) + date-versioned pre-release (`pre-release.yml`) + release-please-driven semver release (`release.yml`) | **INHERITED**, with `habenspass` only contributing normal feature-branch pushes that flow through the existing pipeline unchanged (no commits by `habenspass` to `.github/workflows/`) | Gives every branch a pushed, pullable image (useful for testing PRs/forks without a local build) at the cost of GHCR storage growth over time (no visible pruning/retention workflow for old branch tags). |
| No CI test/lint gate before image push | **INHERITED** | Consistent with "no backend test project, no frontend test suite" per CLAUDE.md — this is a repo-wide characteristic, not something introduced by the deployment pipeline specifically. `typecheck` exists but is opt-in/manual only. |
| PUID/PGID runtime user remapping via `su-exec` + dynamic `useradd`/`groupadd` | **INHERITED** | Standard self-hosted-container convention (matches linuxserver.io idioms) for bind-mount permission compatibility on homelab NAS/hosts — directly serves the "just works on my homelab" deployability goal (QS-7-adjacent). |

---

## 4. Weak points / risks

1. **No Docker `HEALTHCHECK` instruction in the Dockerfile** (confirmed absent — grepped, zero matches). Docker itself has no way to know the container is unhealthy after startup; `docker ps` will always show the container as running (no health column) regardless of internal backend/frontend state. Orchestration tools (Docker Compose `depends_on: condition: service_healthy`, Kubernetes liveness/readiness, Watchtower-style auto-heal) cannot act on this. The only "health" signal that exists is the one-time startup poll inside `entrypoint.sh` (step 6 above) — it is never re-checked after the frontend starts. **QS-8, QS-5.**
2. **No process supervisor** — a hand-rolled `wait_either` busy-poll (0.5s interval) substitutes for s6-overlay/supervisord/tini. Consequences: (a) either process crashing kills *both* and exits the container — recovery is entirely delegated to Docker's `--restart` policy, meaning a transient frontend Node crash forces a full backend restart too (backend restart itself is cheap relative to a fresh migration-run + full reconnect cycle, but not free); (b) as PID 1 with no subreaper, orphaned grandchild processes (if any get spawned/orphaned by `npm run start` → `node`) are not reaped — likely benign in practice (few short-lived children expected) but not verified. **QS-8.**
3. **Naming collision risk for future readers**: `backend/Services/HealthCheckService.cs` is a **file-integrity checker** (verifies Usenet article availability, triggers repairs) — it has nothing to do with the ASP.NET `/health` liveness endpoint entrypoint.sh polls (`Program.cs:88,127`, a bare `AddHealthChecks()`/`MapHealthChecks("/health")` with zero registered checks, so it always returns 200 as long as the Kestrel pipeline is alive). This is a documentation/clarity risk, not a functional bug — but it means "the backend passed its health check" tells you nothing about Usenet connectivity or data integrity, only that the HTTP server is up. **QS-8** (silent — no code change needed, just worth flagging for arc42 glossary/cross-cutting concepts).
4. **Recursive `chown -R $CONFIG_PATH`** only triggers on detected UID/GID mismatch of `db.sqlite` (`entrypoint.sh:76-83`), but when it *does* trigger, on a homelab NAS with a large `/config` (e.g., containing years of cached blobs — see CLAUDE.md's `Database/` blob-cleanup services) this could add meaningful seconds-to-minutes to startup. **(hypothesis)** — untested; would need to time a `chown -R` on a representative populated `/config` directory to confirm real-world impact. **QS-5.**
5. **Two full language runtimes bundled in one image** (dotnet/aspnet:10.0-alpine + full nodejs/npm + `frontend/node_modules`) inflates image size and attack surface (two package ecosystems' worth of CVE exposure) versus a single-runtime alternative. **(hypothesis on exact size delta)** — not measured in this pass (would require `docker build` + `docker image ls` / `dive` to quantify layer sizes); flagging the shape of the risk, not a number. **QS-4.**
6. **No `.dockerignore`** — build-context bloat risk (slower `docker build` invocations, especially over remote Docker contexts) if a developer's local checkout has `bin/`/`obj/`/`node_modules/` present when they build; doesn't affect the shipped image (multi-stage `COPY --from=` is selective) but affects local dev-loop build time. **QS-5-adjacent (dev iteration speed, not container startup).**
7. **CI has zero correctness gate.** A `dotnet publish`/`npm run build` that *compiles* but is behaviorally broken (e.g., a runtime NullReferenceException on the first real request) will still build and push successfully to `:pre-release` (on every non-release main push) and even to a version tag if it lands in a release-please-batched release. The only backstop is manual `npm run typecheck` before opening a PR, which is documented as a suggestion, not enforced by any hook or required CI check.
8. **date-versioned `:pre-release` tag** (`pre-release.yml`, tag body = `date +%Y-%m-%d`) means the *Docker tag* `pre-release` is always the same string across pushes — pulling `:pre-release` gets you "whatever main built as most recently," with no way to pin to a specific prior pre-release build by tag (only by full digest). Minor reproducibility gap for anyone self-hosting off `:pre-release`.

## 5. Alternatives brainstorm

| Alternative | vs QS-7 (single `docker run`) | QS improved/hurt | Migration cost (if replacing INHERITED behavior) |
|---|---|---|---|
| Add a Docker `HEALTHCHECK` instruction pointing at the existing `/health` endpoint (and ideally a second one covering the frontend) | Neutral — doesn't change `docker run` invocation at all, purely additive | Improves QS-8 (visible unhealthy state, enables `--restart` policies keyed on health, enables Compose `depends_on: service_healthy` for anyone who *does* choose compose) | Low — one Dockerfile line (`HEALTHCHECK --interval=30s --timeout=3s CMD curl -f http://localhost:3000/... || exit 1`); needs a frontend-reachable check target since the container's public port is the frontend's 3000, not the backend's internal port |
| s6-overlay or supervisord for real process supervision (independent restart of backend vs frontend, proper signal fan-out, zombie reaping) | Still single image/single `docker run` — s6-overlay/supervisord run *inside* the same container, doesn't reintroduce a compose requirement | Improves QS-8 substantially (a Node crash no longer force-kills a healthy .NET backend and vice versa); marginal QS-5 change (added init overhead is small, tens of ms) | Medium — replaces `entrypoint.sh`'s custom logic with s6 service definitions; must reimplement the migration-gate-before-start sequencing and the backend-health-gate-before-frontend-start sequencing as s6 dependency ordering, which is exactly what those frameworks are for, but requires re-testing PUID/PGID handling and signal forwarding end-to-end |
| Separate backend + frontend containers via docker-compose (two images) | **Directly breaks QS-7** — no longer a single `docker run`; requires compose file, two build/push jobs, an explicit network/service-discovery step for `BACKEND_URL` | Could improve QS-8 (Docker natively restarts each service independently) and QS-4 (each image only carries its own runtime, no double-bundling) | High — touches CI (2 build/push jobs per workflow instead of 1), CONTRIBUTING.md, and every user's existing single-container setup; a breaking change for the existing self-hosting user base. **Recommendation: reject for this deployment target** — the homelab single-`docker run` simplicity is explicitly the product's stated value prop (CLAUDE.md, CONTRIBUTING.md's docker run example) and this tradeoff is worse across the board for that target. |
| Slimmer/distroless base for the final stage (e.g., `dotnet` chiseled/Ubuntu-chiseled image + a minimal Node runtime instead of full `nodejs npm`) | Neutral to `docker run` UX | Improves QS-4 (smaller image, smaller CVE surface: no shell, no package manager in the *final* layer) | Medium-High — chiseled images typically drop `sh`/`bash`, which `entrypoint.sh` and the `su-exec`/`getent`/`addgroup` PUID-remap logic depend on; would require rewriting the whole entrypoint approach (e.g., pre-baked non-root user instead of runtime PUID/PGID resolution), a real design change, not a drop-in swap |
| Add `.dockerignore` (`bin/`, `obj/`, `node_modules/`, `.git/`, `dist-node/`, `build/`) | Neutral | Improves local build-time iteration speed only; no runtime QS impact | Trivial — S effort, zero risk |
| Auto-migrate-on-boot inside the ASP.NET host itself (skip the separate `--db-migration` CLI invocation) instead of explicit gate | Neutral to `docker run` UX (still one command) | Would slightly improve QS-5 (one fewer process spin-up/teardown at startup) but **weakens the safety property CLAUDE.md explicitly credits** — a migration failing mid-way while the host is also trying to bind Kestrel/accept traffic is a strictly worse failure mode than today's "migration runs to completion or the container refuses to start the server at all." **Recommendation: keep current gate** — the safety story here is a deliberate, documented tradeoff (see `Program.cs`'s `BlockUpgradesToV06X`, a hand-built extra layer of the same philosophy), not an oversight to fix. |
| GHCR image retention/pruning workflow for stale branch-tagged images (`branch.yml` output) | N/A (doesn't touch `docker run`) | Not a listed QS directly, but reduces registry storage growth over time — an operational-hygiene item, not correctness | Low — a scheduled workflow calling GHCR's package-delete API for branch tags whose source branch no longer exists |

## 6. Optimization/improvement candidates (concrete, actionable)

1. **Add a Docker `HEALTHCHECK` instruction** targeting the frontend's exposed port (and ideally proxy-checking backend health through it, since the frontend already proxies `/api` to the backend per CLAUDE.md). Improves QS-8. Effort: **S**. Risk: **low** (purely additive, no behavior change to existing paths).
2. **Add a `.dockerignore`** excluding `**/bin/`, `**/obj/`, `**/node_modules/`, `frontend/build/`, `frontend/dist-node/`, `.git/`. Improves dev build-loop speed. Effort: **S**. Risk: **none**.
3. **Introduce a minimal process supervisor (s6-overlay or a small tini + supervisord setup)** to decouple backend/frontend crash-restart from each other and get real zombie reaping as PID 1. Improves QS-8. Effort: **M** (needs careful re-implementation of the existing ordered-startup + signal-forwarding + PUID/PGID logic that `entrypoint.sh` currently owns end-to-end). Risk: **medium** — this is the part of the container most homelab users depend on working exactly as-is; needs thorough manual restart/signal testing before shipping, not just a build-success check (echoing weak point #7: CI wouldn't catch a regression here).
4. **(Optional, lower priority) GHCR branch-tag pruning workflow** to bound registry growth from `branch.yml`'s per-push publishing. Effort: **S**. Risk: **low**.

None of these require abandoning the single-image/single-`docker run` model — all are additive within the existing architecture, consistent with the constraint that QS-7 is this section's dominant scenario.

---

## OPEN QUESTIONS FOR SYNTHESIS

- **Actual image size and per-runtime layer breakdown** (QS-4) was not measured — I did not build the image in this pass. Whoever synthesizes the final arc42 doc should either run `docker build . && docker history` / `dive` once, or explicitly carry forward the "(hypothesis)" tag into the final document rather than asserting a size number.
- **Wall-clock container restart/recovery time** (QS-5, QS-8) — derived purely from reading `entrypoint.sh` control flow, not observed against a running container. If the core-domain or Usenet-client agents have any operational telemetry/logs from a real deployment (e.g., from the provider-usage-stats or health-check features they're covering), that would let this move from hypothesis to measured fact.
- **How `NZBDAV_VERSION` (baked in at build time via ARG/ENV, `Dockerfile:42-43`) is surfaced to the end user** — likely a frontend "about"/settings page or an API health/status field, but that's frontend/API territory, not mine; flagging so the frontend or API-auth agent can confirm where it's consumed if relevant to their sections.
- **Whether `UsenetFileToBlobstoreMigrationService`** (a hosted service registered in `Program.cs:105`, distinct from the CLI `--db-migration` EF migration) runs long enough on first boot after an upgrade to meaningfully affect QS-5 startup/recovery time for existing installs with large libraries — this is a core-domain/database question, I only note that it exists and runs *after* the server is already accepting traffic (it's a hosted service, not part of the blocking migration gate), so it doesn't block the health-check gate in `entrypoint.sh`, but could mean the app is "up" while still doing significant background work.
