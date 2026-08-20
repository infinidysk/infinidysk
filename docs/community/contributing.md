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

`run-backend.sh` defaults `LOG_LEVEL=Debug` and `LOG_BUFFER_SIZE=2000` for local debugging. Docker leaves these unset. The script also enables the contributor-only admin API reference locally: sign in and use `http://localhost:5173/scalar/` through the frontend. The backend's `/openapi/admin.json` endpoint requires `x-api-key` when accessed directly. Set `ENABLE_API_DOCS=false` to disable it; Docker keeps the reference disabled unless explicitly enabled.

After changing a frontend-used admin endpoint, refresh the committed contract with `./scripts/export-admin-openapi.sh` and regenerate types with `cd frontend && npm run generate:api`.

Stream tracing is off by default — toggle it from Settings → Support, or export `STREAM_TRACE_EVENTS=20000` for an always-on capture.

## Real-provider playback

1. Add Usenet + WebDAV credentials in Settings.
2. Queue an `.nzb`, play via Explore or rclone against the **frontend** port.
3. Dump stream traces with `./scripts/dump-stream-trace.sh`.

## PR checks

```bash
cd frontend
npm run lint
npm run format:check
npm run typecheck
npm run build
npm test
cd ..
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release
dotnet test tests/NzbWebDAV.ArchitectureTests/NzbWebDAV.ArchitectureTests.csproj -c Debug
```

Full details: repository [CONTRIBUTING.md](https://github.com/infinidysk/infinidysk/blob/main/CONTRIBUTING.md) and [AGENTS.md](https://github.com/infinidysk/infinidysk/blob/main/AGENTS.md).

## Performance regression gates

Pull request CI compares **deterministic** fields from the streaming and SAB API
reports (`transportRequests`, `transportBytes`, SAB `rowsReturned` /
`totalCount` / `dbCommands`) against
[`backend.Benchmarks/Baselines/`](https://github.com/infinidysk/infinidysk/blob/main/backend.Benchmarks/Baselines).
Timing never blocks a PR.

If that gate fails because you **intentionally** changed transport or query
shape, update the matching baseline JSON in the same PR (and any adjacent
count constants in
`tests/NzbWebDAV.Tests/Streams/RepeatableStreamingBenchmarkCoverageTests.cs`):

```bash
dotnet run --project backend.Benchmarks -c Release -- --streaming-report --json /tmp/streaming.json
dotnet run --project backend.Benchmarks -c Release -- --sab-api-report --json /tmp/sab-api.json
python3 scripts/check-performance-baseline.py \
  --candidates /tmp/streaming.json \
  --write-baseline backend.Benchmarks/Baselines/streaming-baseline.json
python3 scripts/check-performance-baseline.py \
  --candidates /tmp/sab-api.json \
  --write-baseline backend.Benchmarks/Baselines/sab-api-baseline.json
```

Nightly [Performance](https://github.com/infinidysk/infinidysk/actions/workflows/performance.yml)
runs check latency / throughput / CPU against floored 3× envelopes. To refresh
those envelopes: **Actions → Performance → Run workflow → rebaseline**. The
workflow opens a PR and does not merge it. PRs created with `GITHUB_TOKEN` do
not trigger CI — close/reopen (or push) to run checks.

