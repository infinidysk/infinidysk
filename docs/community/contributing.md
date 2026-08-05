# Contributing

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

`run-backend.sh` defaults `LOG_LEVEL=Debug` and `LOG_BUFFER_SIZE=2000` for local debugging. Docker leaves these unset. Stream tracing is off by default — toggle it from Settings → Support, or export `STREAM_TRACE_EVENTS=20000` for an always-on capture.

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
