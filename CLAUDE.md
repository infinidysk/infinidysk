# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

NzbDav is a WebDAV server that mounts NZB documents as a virtual, streamable file system without pre-downloading. It exposes a SABnzbd-compatible API so it can act as a download client for Sonarr/Radarr, and can integrate with Rclone for local mounting.

The repo is two independently-run applications that must share environment configuration:

- `backend/` — a .NET 10 (ASP.NET Core) app: WebDAV server, SABnzbd-compatible API, Usenet client, queue processor.
- `frontend/` — a React Router 7 (SSR) app: UI, its own Express server, and an auth/proxy layer in front of the backend.

## Environment setup

Both apps require the same env vars (see `CONTRIBUTING.md`):

```bash
export CONFIG_PATH=/where/to/create/database/
export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
export BACKEND_URL=http://localhost:5000
```

Required tooling: `dotnet-sdk` (net10.0), `aspnet-runtime`, `nodejs`, `npm`.

## Common commands

### Backend (`backend/`, C# / .NET 10)

```bash
cd backend
dotnet publish -c Release -o ./publish   # build
mkdir -p $CONFIG_PATH && ./publish/NzbWebDAV --db-migration   # create/migrate DB (must run before first start)
./publish/NzbWebDAV                       # run
```

There is no backend test project or lint step in this repo — verify backend changes by building (`dotnet build`) and, where feasible, running the app against a real/staging Usenet+Sonarr/Radarr setup.

### Frontend (`frontend/`, TypeScript / React Router 7)

```bash
cd frontend
npm install
npm run dev         # dev server w/ HMR at http://localhost:5173, proxies to $BACKEND_URL
npm run build        # production build (react-router build)
npm run typecheck    # react-router typegen + tsc -b — run this before opening a PR
npm run start         # serve the production build (after build:server)
```

There is no frontend test suite or linter configured — `typecheck` is the only automated gate.

### Docker

```bash
docker build .                              # full stack image (see Dockerfile at repo root)
docker build -t example/nzbdav:test_build .
docker run --rm -it -v /path/to/config:/config -e PUID=1000 -e PGID=1000 -p 3333:3000 example/nzbdav:test_build
```

CI (`.github/workflows/`) only builds/pushes Docker images on branch pushes, main, and releases (via release-please) — there is no CI test or lint gate to satisfy.

## Architecture

### Backend request flow

`Program.cs` wires up two parallel HTTP surfaces on the same Kestrel host:
- **WebDAV** (`NWebDav.Server`), routed through `WebDav/DatabaseStore.cs` (implements `IStore`), which is backed entirely by the SQLite database rather than the real filesystem. `WebDav/Base/*` holds the generic NWebDav item/collection abstractions; `WebDav/DatabaseStore*.cs` are the concrete implementations (collections, symlinks, rar/multipart/nzb files, watch folders). `GetAndHeadHandlerPatch` overrides the stock GET/HEAD handlers to support ranged/streamed reads for seeking.
- **REST API**, split into two families under `Api/`:
  - `Api/Controllers/*` — the app's own UI-facing API (auth, config, health checks, arr/rclone/usenet connection tests, webdav browsing).
  - `Api/SabControllers/*` — a SABnzbd-API-compatible surface (add nzb/url, queue, history, status, categories) consumed by Sonarr/Radarr as if this were SABnzbd.

Auth: WebDAV can require HTTP Basic auth (`Auth/`); the API is authenticated via an `x-api-key` header shared with the frontend (`FRONTEND_BACKEND_API_KEY`).

### Queue / ingestion pipeline

This is the core of the backend and spans several folders under `Queue/`:
1. `QueueManager` — a singleton background loop (`HostedService`-adjacent, started in its constructor) that serially pulls the next `QueueItem` from the DB and processes it with a fresh `ArticleCachingNntpClient` scoped to that item. Supports pause/resume, cancellation, and progress broadcast over websockets.
2. `QueueItemProcessor` — orchestrates one item through, in order:
   - **Deobfuscation** (`Queue/DeobfuscationSteps/1..3`) — fetches the first NZB segment, resolves PAR2 file descriptors, and derives real file info/names for obfuscated releases.
   - **File processing** (`Queue/FileProcessors/`) — per-container-type processors (`RarProcessor`, `SevenZipProcessor`, `MultipartMkvProcessor`, plain `FileProcessor`) that turn raw NZB article data into logical files.
   - **Aggregation** (`Queue/FileAggregators/`) — mirrors the processors (`RarAggregator`, `SevenZipAggregator`, `MultipartMkvAggregator`) to assemble multi-part/segmented content into a single streamable entity, persisted as `DavItem`/`DavRarFile`/`DavMultipartFile`/`DavNzbFile` rows.
   - **Post-processing** (`Queue/PostProcessors/`) — blocklist filtering, duplicate renaming, `.strm` file creation, importable-video validation — run after the item lands in the virtual filesystem, before it's handed to Sonarr/Radarr as "complete."
3. Retryable download failures re-queue the item with a `PauseUntil` backoff instead of failing it; non-retryable failures move it to history as failed.

### Usenet client stack (`Clients/Usenet/`)

Layered NNTP client design (each wraps the previous, all implementing `INntpClient`):
`NntpClient` (raw protocol) → `MultiConnectionNntpClient` (connection pooling per provider, see `Connections/ConnectionPool.cs` + `ProviderCircuitBreaker.cs` for per-provider failure isolation) → `MultiProviderNntpClient` (fails over across configured providers) → `DownloadingNntpClient` / `ArticleCachingNntpClient` (per-queue-item article caching) → `UsenetStreamingClient` (top-level singleton used by WebDAV reads and the queue). `Concurrency/PrioritizedSemaphore.cs` prioritizes interactive streaming reads over background queue downloads on shared connections.

### Streaming (`Streams/`)

Custom `Stream` implementations compose to support seekable playback without full downloads: `MultiSegmentStream`/`UnbufferedMultiSegmentStream` stitch together NZB article segments, `CachedYencStream` decodes yEnc on the fly, `AesDecoderStream` handles password-protected archives, `SubStream`/`LimitedLengthStream`/`CombinedStream` provide range/offset composition, and `ProbingStream` sniffs content for format detection. These are composed per-request in the WebDAV file handlers to serve arbitrary byte ranges directly from Usenet.

### Database (`Database/`)

EF Core + SQLite (`DavDatabaseContext`). Migrations live in `Database/Migrations/` and must be applied via `NzbWebDAV --db-migration` before first run or after upgrading — the app deliberately refuses to auto-migrate on normal startup (see `Program.cs`'s `BlockUpgradesToV06X` for an example of a hard version-gate on a specific migration). `DavItem` and friends model the virtual filesystem tree; `QueueItem`/`HistoryItem` model the SABnzbd-style queue/history; `*CleanupItem` models drive the various cleanup `HostedService`s (blob, nzb-blob, dav, history) registered in `Program.cs`.

### Frontend

- **Routing**: filesystem-based via `@react-router/fs-routes`, declared in `app/routes.ts`. Route folders live under `app/routes/*`; each typically has a `route.tsx` plus co-located `components/`/`controllers/`. One route (`/explore/*`) is registered manually alongside the auto-discovered routes.
- **Server** (`server.ts` + `server/app.ts`): a hand-rolled Express server (not the default React Router server) that does three things before handing off to React Router SSR:
  1. Proxies WebDAV/API/media paths (`/api`, `/view`, `/.ids`, `/nzbs`, `/content`, `/completed-symlinks`, PROPFIND/OPTIONS) straight to `$BACKEND_URL` via `http-proxy-middleware`, injecting the shared `FRONTEND_BACKEND_API_KEY` as `x-api-key` for authenticated sessions that lack their own API key.
  2. Enforces session auth (`app/auth/auth-middleware.server.ts`) for all non-proxied routes.
  3. Runs a `WebSocketServer` alongside HTTP for live queue/health updates, wired to the backend's `/ws` endpoint.
  Compression is deliberately disabled for proxied/streamed paths to keep `Content-Length` intact for range-based seeking.
- **Backend access from loaders/actions**: `app/clients/backend-client.server.ts` is a thin typed wrapper over `fetch` calls to `$BACKEND_URL/api/*`, always sending the shared API key. Prefer extending this client over calling `fetch` ad hoc in routes.
- Styling: Bootstrap + react-bootstrap, plus Tailwind (`@tailwindcss/vite`) for utility classes.

### Cross-cutting notes

- The frontend and backend are two separately deployed processes in production (see root `Dockerfile`) but a single conceptual app — most features touch both a `Api/Controllers` (or `SabControllers`) endpoint and a corresponding frontend route/client method.
- `FRONTEND_BACKEND_API_KEY` is the trust boundary between them; any new backend API endpoint intended for frontend-only use should require it the same way existing controllers do.
- Long-running/background work on the backend is modeled as `IHostedService`s registered in `Program.cs`, not ad hoc timers — follow that pattern for new periodic tasks.
