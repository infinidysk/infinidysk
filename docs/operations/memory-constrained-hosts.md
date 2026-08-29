# Memory-constrained hosts

InfiniDysk has a .NET backend and a Node frontend. Size the limit for both
processes, plus sockets, TLS, SQLite, and native allocations—not only the
managed .NET heap.

## Container memory limits

Prefer a real container memory limit (`docker run --memory` or the equivalent
Compose/Kubernetes setting). .NET detects a cgroup limit and uses it when it
derives its available-memory and unset streaming-budget defaults.

Set an explicit **In-flight article budget** in Settings → Streaming on small
hosts. Also cap provider connections and the streaming/queue connection
settings to the throughput the host can sustain.

## Hosts with `ulimit -v` / `RLIMIT_AS`

`ulimit -v` caps *virtual address space*, not resident memory. A process can
hit it while its RSS is comparatively small.

On 64-bit Linux, the .NET regions GC reserves virtual address space up front.
When only `DOTNET_GCHeapHardLimit` is configured, the default region range is
five times that limit. Set `DOTNET_GCRegionRange` explicitly if that reservation
does not fit alongside the rest of the process.

Environment-variable byte values for these .NET GC settings are hexadecimal:

```yaml
environment:
  # 512 MiB managed heap ceiling
  DOTNET_GCHeapHardLimit: "20000000"
  # 1 GiB regions reservation (2× the heap ceiling)
  DOTNET_GCRegionRange: "40000000"
  # Upper bound (0–9). Prefer 5–7 as a first measured canary; see below.
  DOTNET_GCConserveMemory: "9"
  # Bound worker growth separately from the GC heap.
  THREADPOOL_MAX_THREADS: "200"
```

Treat a 2× region range as a starting point, not a universal value. Workloads
that frequently make large object allocations can need a larger range; a range
that is too small trades virtual address space for more full compacting GCs.
`DOTNET_GCHeapHardLimitPercent` is usually unsuitable for a host that has only
an address-space limit, because its percentage is based on physical/cgroup
memory rather than `RLIMIT_AS`.

Set these values before the backend starts. Confirm the active values in
Settings → Support → downloaded `environment.json`: `runtime.addressSpaceLimitBytes`
is the finite `RLIMIT_AS` when Linux exposes one, and the `gc` section reports
the region range, size, hard limit, committed heap, detected heap limit,
collection identity, memory-load, and LOH-specific hard limits.

`DOTNET_GCConserveMemory` is a restart-required runtime canary, not an
InfiniDysk default. The image does not set it. A value in the 5–7 range can
reduce LOH fragmentation at the cost of more frequent collections and longer
pauses; measure playback and import latency before keeping it.

## Repeated 64 MiB `PROT_NONE` mappings

On a **glibc** host, a repeated pair of anonymous RW and `PROT_NONE` mappings
whose combined size is 64 MiB commonly identifies glibc malloc arenas. It is
not evidence that each NNTP socket has allocated 64 MiB.

After verifying that map shape, try a conservative arena cap:

```yaml
environment:
  MALLOC_ARENA_MAX: "2"
```

Fewer arenas can add allocator lock contention under highly concurrent TLS or
download workloads, so measure throughput after changing it. This tuning is
glibc-specific; the shipped Alpine image uses musl and does not use
`MALLOC_ARENA_MAX`.

## Additional safeguards

- Lower provider **Max connections** and explicit download/queue connection
  limits before reducing buffers. NNTP I/O is asynchronous; it does not create
  a dedicated OS thread per socket.
- Disable [warm connections](../features/connection-warming.md) or shorten the
  idle timeout if many pooled sockets are unnecessary.
- Lower **Streaming body batch width** and set an explicit **In-flight article
  budget** to reduce active decoded-pipe retention.
- `DOTNET_GCRetainVM=0` is already the .NET default; setting it does not shrink
  the initial regions-GC reservation.

See [.NET runtime GC configuration](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector)
for the complete runtime-setting semantics.
