# Contributing

## Set up your system

The project consists of two sub projects: frontend and backend
Both share some necessary environment variables.

**Ensure that frontend and backend share the same environment configuration!**

Environment variables:

```bash
export CONFIG_PATH=/where/to/create/database/
export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
export BACKEND_URL=http://localhost:5000
```

The backend thread-pool limits can optionally be overridden with
`THREADPOOL_MIN_THREADS` and `THREADPOOL_MAX_THREADS`. When unset, they retain
the production defaults of `max(2 × processor count, 50)` minimum threads and
`max(50 × processor count, 1000)` maximum threads.

You need some packages in order to run the project:

- dotnet-sdk
- aspnet-runtime
- nodejs
- npm
- cmake and ninja (to build the rapidyenc native library from the submodule)

Example installation for Arch based systems:

```bash
sudo pacman -S dotnet-sdk aspnet-runtime nodejs npm cmake ninja
```

On macOS (Homebrew):

```bash
brew install --cask dotnet-sdk
brew install node cmake ninja
```

After cloning, initialize the rapidyenc submodule:

```bash
git submodule update --init libs/rapidyenc
```

## Preferred local workflow

Use the helper scripts so the frontend and backend share env automatically:

```bash
# Terminal 1 — backend (builds rapidyenc if needed, migrates, writes frontend/.env)
./scripts/run-backend.sh

# Terminal 2 — frontend (`predev` runs scripts/sync-dev-env.sh)
cd frontend && npm install && npm run dev
```

`npm run dev` silently runs `scripts/sync-dev-env.sh` via the `predev` hook; if the API key drifts, restart the backend with `scripts/run-backend.sh`. The manual `dotnet publish` / env-var flow below remains supported.

`scripts/run-backend.sh` defaults `LOG_LEVEL=Debug` (and `LOG_BUFFER_SIZE=2000`) when unset so local playback debugging is verbose. Docker/production leave these unset and keep Information-level logging.

Stream tracing is **opt-in** and off by default. Toggle it from **Settings → Support** for 15/30/60 minutes (no restart; it auto-expires and never survives a restart), or set `STREAM_TRACE_EVENTS` to a positive value for an always-on capture from startup. When tracing is off, no trace events are recorded and the trace APIs report `enabled: false`.

`scripts/run-backend.sh` builds the host rapidyenc native (via `scripts/build-rapidyenc.sh`) when missing and exports `RAPIDYENC_LIBRARY_PATH`. With that in place, yEnc-decoding tests run on macOS and Linux; without a native library they are skipped.

## Real-provider playback testing

Use the two-process workflow above with a real Usenet provider (credentials stay in SQLite under `CONFIG_PATH`; never commit them).

1. Start backend + frontend (`./scripts/run-backend.sh`, then `cd frontend && npm run dev`).
2. Open `http://localhost:5173` → create the admin account if needed.
3. **Settings → Usenet** — add your provider (host, port, SSL, credentials, connections). Use Test connection / Benchmark if desired.
4. **Settings → WebDAV** — set a WebDAV username/password (required for rclone and most players).
5. Drop an `.nzb` on the Queue page and wait for it to mount.
6. Play via Explore / `/view/...`, or point rclone/VLC at the **frontend** proxy (`http://localhost:5173`), not the backend port directly.

Ports: UI `5173` → proxies WebDAV + `/api` → backend `5000`.

### Dumping a stream trace

While a file is playing, Overview → **Right now** shows a truncated session id (click to copy). After seeking/scrubbing:

```bash
# Latest active/recent session (or pass an explicit session id)
./scripts/dump-stream-trace.sh
./scripts/dump-stream-trace.sh <session-id>
```

Writes JSON under `swap/` (gitignored), including the correlated range/seek/segment/zero-fill timeline and a recent logs snapshot. Drop that file into chat for analysis. Traces are in-memory only — dump before restarting the backend.

## Build / run backend

UsenetSharp, RapidYencSharp, and SharpCompress live under `libs/` and are
referenced as project references. Prefer `./scripts/run-backend.sh`, which
initializes the host rapidyenc native and env for you. Manual flow:

```bash
git submodule update --init libs/rapidyenc
./scripts/build-rapidyenc.sh   # host RID (osx-arm64, linux-x64, …)
# Point RAPIDYENC_LIBRARY_PATH at the built dylib/so under
# libs/RapidYencSharp/runtimes/<rid>/native/ (run-backend.sh does this).

cd backend

# Build (release)
dotnet publish -c Release -o ./publish

# Create database
mkdir -p $CONFIG_PATH
./publish/NzbWebDAV --db-migration

# Run backend
./publish/NzbWebDAV
```


## Build / serve frontend

Requires **Node.js 24+** (see `engines` in `frontend/package.json`).

`package.json` includes an `overrides` entry that pins `http-proxy-middleware` to the `http-proxy-node16` fork for Node compatibility. Remove it only after verifying proxy behavior against upstream `http-proxy`.

```bash
cd frontend

# Install dependencies
npm install

# Run / serve frontend with hot module replacement
npm run dev
```

## Build Docker image

### Using Docker CLI

```bash
docker build .
```

You can also tag the release, which can be used with `docker compose`:

```bash
docker build -t example/infinidysk:test_build .
```

Run the container:

```bash
docker run --rm -it \
  -v /path/to/infinidysk/config:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3333:3000 \
  example/infinidysk:test_build
```

### Using Docker Compose

```yaml
services:
  infinidysk:
    build: .
    ports:
      - 3333:3000
    volumes:
      - /path/to/infinidysk/config:/config
      - /path/to/infinidysk/data:/data
    environment:
      - PUID=1000
      - PGID=1000
```

Build and run container:

```bash
docker compose up
```

## Static analysis policy

All first-party C# projects build with the full .NET analyzer set
(`AnalysisLevel=latest-All` via the pinned `Microsoft.CodeAnalysis.NetAnalyzers`
package in the root `Directory.Build.props`). New code must not introduce
analyzer or compiler warnings.

When a rule fires, resolve it in this order:

1. **Fix it.** Most rules catch real issues (disposal, async, culture) or have
   a mechanical code fix.
2. **Per-site suppression** — `#pragma warning disable` (or a targeted
   `GlobalSuppressions.cs` entry) with a justification comment — when the rule
   misfires on intent at a specific place. Typical legit cases: ownership
   transfers the analyzer cannot see (e.g. CA2000/CA2025 around background
   tasks or response-lifetime disposal via `Response.OnCompleted`),
   format-mandated weak hashes (CA5351), user-facing opt-outs
   (CA5359/CA5398), and classification sites that must not bind the ambient
   cancellation token (CA2016).
3. **Rule-level policy** in the root `.editorconfig` (`tests/.editorconfig`
   for test-only noise) with a written justification — only for rules that are
   structural for this codebase (e.g. CA1515 "make types internal" in an
   ASP.NET app, xUnit naming conventions in tests).

Rules that must **never** be disabled rule-wide: the security rules (CA53xx) —
use per-site justification suppressions so each instance stays auditable.

Notes:

- `libs/UsenetSharp` and `libs/RapidYencSharp` have their own `root = true`
  `.editorconfig` files with a mirrored policy block — keep them in sync with
  the root policy.
- `libs/SharpCompress` and `tests/SharpCompress.Tests` are vendored upstream
  code and exempt via per-project `<NoWarn>` (justification in the csproj).
  Do not fix analyzer warnings in them; keep ports clean instead.
- Bulk `dotnet format analyzers` runs: temporarily remove the analyzer package
  from `Directory.Build.props` first — with the package referenced, the format
  workspace loads both the SDK and packaged analyzer copies and applies every
  code fix twice. Restore the package afterwards (builds are unaffected either
  way; only the format tool is).

## Contributing

Before creating a PR:

```bash
cd frontend
npm run lint
npm run typecheck
npm run build
npm test
```
