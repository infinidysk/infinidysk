# Backend benchmarks

BenchmarkDotNet timing runs stay manual: they are sensitive to runner
contention and hardware, and timing comparisons never block pull requests.
Deterministic transport and SAB API fields from the report harnesses **are**
compared in CI (see `.github/workflows/ci.yml` and
`.github/workflows/performance.yml`).

Run BenchmarkDotNet from the repository root:

```bash
dotnet run --project backend.Benchmarks -c Release
```

Use the same machine and runtime when comparing BenchmarkDotNet results across
UsenetSharp or streaming changes.

## NNTP decoded BODY (`NntpDecodedBodyBenchmarks`)

This measures the playback decode path in
`UsenetSharp.Clients.NntpYencBodyDecoder`: `NntpLineReader` buffering, NNTP/yEnc
framing, rapidyenc, optional CRC, `PipeWriter` backpressure, and a concurrent
consumer. It does **not** include TLS, providers, archives, or WebDAV.

`YencDecodeBenchmarks.DecodeYencSegment` only exercises `YencStream` and is not
evidence for decoded BODY changes.

```bash
dotnet run --project backend.Benchmarks/NzbWebDAV.Benchmarks.csproj -c Release -- \
  --filter "*NntpDecodedBodyBenchmarks*"
```

The corpus is a fixed-seed payload encoded with yEnc line size 128, NNTP
dot-stuffed, with a single-part `=ybegin` / `=yend` / `.` wrapper. Parameters
are decoded size (4 MiB and 32 MiB) and `YencCrcValidationMode` (`Off` or
`Require`). Compare mean time, decoded MiB/s, and allocations only on the same
machine and runtime. Timing stays manual and is not a PR gate.

On macOS, set `RAPIDYENC_LIBRARY_PATH` to the host `librapidyenc.dylib` (see
`scripts/run-backend.sh`).

## Tool decision

Issue [#854](https://github.com/infinidysk/infinidysk/issues/854) asked to
choose k6, wrk, or a custom .NET client. InfiniDysk uses a **custom in-process
.NET harness** (`--streaming-report` and `--sab-api-report`) because the
deterministic transport fake (`BenchmarkNntpClient`) and pre-seeded SQLite SAB
corpus have to be wired inside the process. A live-socket load tool cannot
see those counters or keep results independent of the network.

Range-probe and tail-probe are the deterministic stand-in for ffprobe's access
pattern (open, header read, tail seek). Full HTTP WebDAV GET percentiles and
SAB `addfile` ingest are out of scope here.

## Repeatable streaming report

```bash
dotnet run --project backend.Benchmarks -c Release -- --streaming-report
dotnet run --project backend.Benchmarks -c Release -- --streaming-report --json /tmp/streaming.json
```

It uses generated in-memory segments (`Random(1025)`, 12 × 256 KiB) and the
local segment cache, so it makes no provider connections. The report verifies
payload fidelity while recording cold sequential transport bytes/requests,
first-byte latency, range and tail probes, warm cache re-reads, seeks, and a
zero-filled dead-article read. Compare throughput and latency fields only on
the same machine and runtime, or against the committed envelopes; transport
fields remain deterministic across runs.

## SAB API report

```bash
dotnet run --project backend.Benchmarks -c Release -- --sab-api-report --json /tmp/sab-api.json
```

Directly invokes `GetQueue` / `GetHistory` against a migrated temp SQLite
database (same setup as the SAB limit-zero tests) with a fixed 50-queue /
500-history corpus. Deterministic fields are `rowsReturned`, `totalCount`, and
`dbCommands` (EF command count).

## Regression layers

1. **PR-blocking (no clocks):** xUnit exact-count coverage plus both reports
   compared with `scripts/check-performance-baseline.py --deterministic-only`
   against `backend.Benchmarks/Baselines/*.json`. An intentional
   transport-contract or query-shape change must update the baseline JSON in
   the same PR.
2. **Scheduled envelopes:** `.github/workflows/performance.yml` runs each
   report 3× on a cron / `workflow_dispatch` and fails when the median misses
   a floored 3× envelope. Dispatch the workflow with `rebaseline: true` to
   write new baselines and open (never merge) a PR. Locally:

```bash
python3 scripts/check-performance-baseline.py \
  --candidates /tmp/streaming.json \
  --write-baseline backend.Benchmarks/Baselines/streaming-baseline.json
```

`GITHUB_TOKEN`-created re-baseline PRs do not trigger `pull_request` CI;
close/reopen or push to run checks.

## Scenario → meaning

A count change is a transport or query-shape contract change. Update the
matching constant in
`tests/NzbWebDAV.Tests/Streams/RepeatableStreamingBenchmarkCoverageTests.cs`
and/or the committed baseline JSON.

| Scenario | Field going up usually means |
| --- | --- |
| `cold-sequential` `transportRequests` | extra BODY/ARTICLE traffic per byte (lost batching or smaller segments) |
| `cache-prime` `transportRequests` | cache prime is no longer one request per fixture segment |
| `warm-reread` `transportRequests` | segment cache miss on a path that should be warm |
| `range-probe` `transportRequests` | read-ahead widened (or cache skipped) for a mid-file probe |
| `tail-probe` `transportRequests` | tail/header-style probe is fetching extra segments |
| `seeks` `transportRequests` | seek amplification (more articles per scrub) |
| `dead-article` `transportRequests` / `transportBytes` | extra work around a missing article, or a gap that is no longer zero-filled |
| SAB `rowsReturned` / `totalCount` | pagination or filter contract change |
| SAB `dbCommands` | extra round-trips (often N+1) for the same page |
