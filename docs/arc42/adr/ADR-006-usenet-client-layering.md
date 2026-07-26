# ADR-006: Layered decorator stack over an external native NNTP/yEnc client

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: QS-1 (seek latency), QS-3 (concurrent streams), QS-6 (provider failover), QS-4 (footprint)

## Context

Usenet clients need connection pooling per provider, multi-provider failover, per-provider circuit
breaking, and interactive-vs-background prioritization — none of which a raw NNTP client provides,
and all of which matter for a resilient, performant streaming experience on unreliable third-party
provider infrastructure.

## Decision

Delegate the raw NNTP protocol and yEnc decode to `UsenetSharp` (an external NuGet package, same
`nzbdav-dev` org), which itself depends on `RapidYencSharp` — a P/Invoke binding to the native,
SIMD-accelerated (SSE2/AVX2/NEON) `rapidyenc` C library. Build every cross-cutting concern as a
decorator implementing this project's own `INntpClient` interface on top:
`UsenetStreamingClient` → `DownloadingNntpClient` → `MultiProviderNntpClient` →
`MultiConnectionNntpClient` → `ConnectionPool` → `BaseNntpClient` (the `UsenetSharp` adapter).
`ArticleCachingNntpClient` is a separate decorator, scoped only to Queue ingestion.

**This corrects an assumption in the original brief for this analysis**: the low-level protocol/
decode layer is *not* hand-rolled. Only the decorator layering above it is original to this
project.

## Consequences

- **Positive**: each concern is independently composable/testable in principle (in practice, no
  tests exist for any of it — see §11); the yEnc decode hot path is already at native/SIMD
  performance, so the classic "rewrite the hot path in Rust/C for speed" optimization is already
  banked.
- **Negative**: every new cross-cutting concern (throttling, stats, in the fork's case) is another
  decorator layer, and priority/cancellation must be threaded through correctly at each one — two
  independent priority mechanisms already don't compose transparently (§8.3, §11). `ConnectionPool`/
  `ConnectionLock`, the connection-lifecycle code every stream and queue download depends on, is
  explicitly code-comment-marked as ChatGPT-authored with zero test coverage.

## Alternatives considered

| Alternative | Verdict |
|---|---|
| Replace the hand-rolled decorator stack with an existing full-featured .NET Usenet client library | No such library was found that provides connection pooling + multi-provider failover + circuit breaking + yEnc decode as one package; `UsenetSharp` (already in use) is this project's own foundational library. Not a real alternative — the realistic option space is entirely about the decorators on top, not the raw client. |
| Rewrite yEnc/AES decode in Rust/C via P/Invoke for a "lower-level language" win | **Already done for yEnc** (native SIMD via `RapidYencSharp`) — no further gain available without profiling data showing it's still a bottleneck. `AesDecoderStream` is a managed CBC decoder over .NET's `Aes.CreateDecryptor`, itself backed by OS-native crypto on most platforms — already effectively native at the primitive level. A custom rewrite would mostly reimplement existing buffer-management logic for uncertain gain, at real cost to QS-7 (adds a native toolchain/cross-compilation dependency to the build). **Not recommended without profiling evidence.** |
| Pre-warm/keep N idle connections per provider instead of pure on-demand pooling | See §11 — a genuine, low-cost QS-1/QS-3 improvement, not a rejection of this ADR's structure, just an addition to it. |
| Latency-based (not just failure-count-based) circuit breaking | See §11 — same, an addition rather than a structural change. |

**Recommendation**: keep the layered decorator structure; invest in test coverage for
`ConnectionPool`/`ConnectionLock` before adding further decorators, and pursue the two targeted
additions above (connection pre-warming, latency-aware circuit breaking) rather than any
lower-level-language rewrite.
