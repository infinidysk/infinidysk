# Contributing

!!! note "Source builds are a development workflow, not a deployment method"

    Running InfiniDysk from a cloned repository is how the project is developed, but it is **not a supported deployment method** — releases are tested as the published [Docker image](../getting-started/docker.md) and [prebuilt Linux archives](../getting-started/prebuilt-archives.md). You are still welcome to run from source; it is simply not a path releases are tested against. See [Release channels and tags](../getting-started/index.md#release-channels-and-tags) for the supported channels.

## Shared environment

Frontend and backend must share:

```bash
export CONFIG_PATH=/where/to/create/database/
export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
export BACKEND_URL=http://localhost:5000
```

Optional: `THREADPOOL_MIN_THREADS`, `THREADPOOL_MAX_THREADS`.

## Preferred workflow

```bash
# Terminal 1
./scripts/run-backend.sh

# Terminal 2
cd frontend && npm install && npm run dev
```

UI: `http://localhost:5173` → proxies to backend `:5000`.

`run-backend.sh` defaults `LOG_LEVEL=Debug` and `LOG_BUFFER_SIZE=2000` for local debugging. Docker leaves these unset. The script also enables the contributor-only admin API reference locally: use `http://localhost:5000/scalar/` directly, or sign in and use `http://localhost:5173/scalar/` through the frontend. Set `ENABLE_API_DOCS=false` to disable it; Docker keeps the reference disabled unless explicitly enabled. Stream tracing is off by default — toggle it from Settings → Support, or export `STREAM_TRACE_EVENTS=20000` for an always-on capture.

## Real-provider playback

1. Add Usenet + WebDAV credentials in Settings.
2. Queue an `.nzb`, play via Explore or rclone against the **frontend** port.
3. Dump stream traces with `./scripts/dump-stream-trace.sh`.

## PR checks

```bash
cd frontend && npm run lint && npm run typecheck && npm run build && npm test
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release
```

Full details: repository [CONTRIBUTING.md](https://github.com/infinidysk/infinidysk/blob/main/CONTRIBUTING.md) and [AGENTS.md](https://github.com/infinidysk/infinidysk/blob/main/AGENTS.md).
