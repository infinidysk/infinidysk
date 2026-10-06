# Performance experiment ledger

Every streaming, CPU, memory, or native-library performance idea that was
measured, with its verdict. **Check this before proposing or re-running an
idea.** Add a row whenever an experiment finishes, including rejected and
inconclusive ones. Retry a rejected idea only when the "Revisit if" condition
holds, and record the new result here.

Corpus entries are described generically; never write media or release titles
here (see `AGENTS.md`).

Verdicts: **Shipped** (merged), **Win** (PR open), **Rejected**,
**Inapplicable** (code path does not fire), **Inconclusive**.

## Measurement context (2026-10)

- Viren070 `nzb-streaming-benchmarks` Docker runs on Apple M4 (linux/arm64),
  20 connections: ~19-21 CPU-s per delivered GB; sequential 40-120 MB/s,
  provider-bound. Run-to-run variance is ~±15%; compare only ABBA-ordered arms
  within one session.
- Native rapidyenc on the same host (clang and Alpine GCC 14 agree):
  NEON decode 10-12 GB/s on random payloads at ≥4 KiB chunks (0.09-0.10 s/GiB),
  ARM CRC32 11.5 GB/s (0.09 s/GiB).
- Managed `NntpDecodedBodyBenchmarks` (4 MiB body): 0.25 s/GiB with CRC off,
  0.37 s/GiB with CRC required. The whole decode path is therefore <2% of
  streaming CPU; one core decodes ~2.8 GB/s. Native/decoder work cannot move
  throughput, p05, or stalls while streaming stays provider-bound.

## Streaming and scheduling

| Idea | Verdict | Evidence | Revisit if |
|------|---------|----------|------------|
| Incremental article delivery at every demand article (E1) | Win (#1638) | Max stall 4-25× lower; throughput neutral or better | — |
| Expand read-ahead early after useful delivery (E2) | Rejected | Viren ABBA showed no repeatable win | — |
| Demand-first assignment of unissued work to ready capacity (E3) | Rejected | Producer/admission wait already ~0; waits are body-drain dominated | Diagnostics show admission wait |
| Resolve near-EOF RAR reads from the exact end (E4) | Inapplicable | RAR fixtures open eagerly; near-EOF suffix resolution never fires | Lazy RAR open path changes |
| Hedge demand requests receiving no bytes (E5) | Rejected | Viren ABBA showed no repeatable win | — |
| Archive background work yields to live reads (E6) | Rejected | Viren ABBA showed no repeatable win | — |
| Non-pipelined BODY for demand reads | Rejected | Helps only the direct large-article entry | — |
| Read-ahead ramp starting at 1 segment per stripe | Rejected | Starved RTT at 20 connections (64 MiB 927 → 1053-1679 ms) | — |
| Read-ahead ramp `max(8, min(stripes*width, window/2))` | Shipped (#1618) | Kept speed; still ramps at 40 connections | — |

## CPU, GC, and memory

| Idea | Verdict | Evidence | Revisit if |
|------|---------|----------|------------|
| `ThreadPool UnfairSemaphoreSpinLimit=0` | Shipped (#1614) | CPU/GiB 10.6 → 7.6 bulk, 8.7 → 5.8 at 4 streams (Linux Docker arm64) | — |
| Idle trim of segment buffer pool | Shipped (#1617) | Releases idle pool ~40-45 s after streams stop | — |
| Cap `GCgen0MaxBudget` | Rejected | Barely changes server-GC gen0 count under DATAS; costs CPU | — |
| Workstation GC | Rejected | ~255 MiB lower after-stream RSS but mixed throughput at 4 streams; memory is lowest priority | — |

## Native yEnc / CRC (rapidyenc, UsenetSharp decoder)

Tested 2026-10-05 against main `c7d0da78`, rapidyenc `81b6ed3`.

| Idea | Verdict | Evidence | Revisit if |
|------|---------|----------|------------|
| N0: disable CRC validation for playback | Rejected | Viren ABBA (2 rounds, 4 entries): CPU/GB 19.4 → 18.4, no consistent p05/stall/MB/s change. Measured CRC cost (~0.13 s/GiB) is ≤0.7% of CPU. Also drops detection of same-length corrupted articles (policy change) | Streaming becomes CPU-bound |
| N1: combined native decode+CRC entry point | Rejected | Transitions ≈ 25 ns × ~2 calls per chunk ≈ 13 ms/GiB even at 4 KiB chunks (<0.1% CPU) | — |
| N2: native incremental scanning of the data region | Rejected | Managed framing + pipe overhead ≤0.15 s/GiB (<1% CPU); high protocol-parsing risk | Profiling shows line scanning as a hot spot |
| N3: 3 independent `__crc32d` chains + `crc_combine` on ARM64 | Rejected | Correct; 2-3× faster CRC at ≥4 KiB (22-35 vs 11.5 GB/s), equal at ≤1 KiB. Saves ≤0.06 s/GiB (~0.3% CPU). Upstream notes a 2-chain variant slowed Cortex A53; untested on user SBC cores | Streaming becomes CPU-bound on ARM, with SBC hardware to verify |
| N4: sparse-mask NEON compaction fast paths | Rejected | Even escape-free payloads only reach ~20 GB/s (saves ≤0.05 s/GiB, <0.3% CPU); real payloads behave like random data | — |

The scratch native chunk-size benchmark (decode / CRC / decode+CRC / 3-chain CRC
over 256 B-768 KB chunks and random/sparse/dense escapes) was not committed;
extend `libs/rapidyenc/tool/bench.cc` if a native idea needs re-measuring.
