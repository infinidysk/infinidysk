# Backend benchmarks

The benchmarks establish a repeatable baseline for Usenet segment decoding. They
are intentionally excluded from CI because BenchmarkDotNet results are sensitive
to runner contention and hardware.

Run them from the repository root:

```bash
dotnet run --project backend.Benchmarks -c Release
```

Use the same machine and runtime when comparing results across UsenetSharp or
streaming changes.

## Repeatable streaming report

Run the deterministic local streaming harness with:

```bash
dotnet run --project backend.Benchmarks -c Release -- --streaming-report
```

It uses generated in-memory segments and the local segment cache, so it makes
no provider connections and is not intended for CI. The report verifies payload
fidelity while printing cold sequential transport bytes/requests, first-byte
latency, range and tail probes, warm cache re-reads, seeks, and a zero-filled
dead-article read. Compare throughput and latency fields only on the same
machine and runtime; transport fields remain deterministic across runs.
