# Backend Language/Runtime Alternatives Survey

This is a comparative survey, not an implementation proposal. It widens the field beyond the two
languages the project owner named as examples (Rust, Java+GraalVM — each covered in a separate deep
dive) to other genuinely credible backend candidates for this specific workload: a single-container,
mostly-I/O-bound-but-occasionally-CPU-bound Usenet-streaming WebDAV server with a hand-rolled
connection-pool/failover/circuit-breaker layer, SQLite persistence, and archive (RAR/7z) parsing —
judged against the QS-1..QS-8 scenarios in [10-quality-requirements.md](../10-quality-requirements.md),
with QS-1 (seek latency) and QS-4 (resource footprint) weighted most heavily since the brief is a
homelab single-container deployment where "as performant as possible" is dominated by those two.

Context established elsewhere in this analysis that matters for every candidate below: the raw
NNTP protocol and yEnc decode are **already** delegated to a native, SIMD-accelerated C library
(`rapidyenc`, via `RapidYencSharp`'s P/Invoke binding — see
[usenet-streaming.md §0](usenet-streaming.md)) — so no candidate needs to reimplement yEnc decode;
the only question per candidate is how cheaply/safely it can call into that same native library.

## 1. Go

**Fit for this workload.** Goroutines + channels are an unusually close conceptual match to what
this codebase already hand-builds in C#: `MultiSegmentStream`'s detached background read-ahead task
feeding a bounded channel is, almost line-for-line, the idiomatic Go pattern (a goroutine writing to
a `chan`). `PrioritizedSemaphore`, `ConnectionPool`, and `ProviderCircuitBreaker` — all flagged
elsewhere in this analysis as untested, partly ChatGPT-authored, hand-rolled primitives — are exactly
the kind of thing Go's ecosystem has mature, battle-tested off-the-shelf answers for
(`golang.org/x/sync/semaphore`, `golang.org/x/time/rate`, well-known circuit-breaker packages). The
ambient-priority-via-`CancellationToken`-keyed-dictionary hack this codebase uses to propagate
interactive-vs-background priority into a detached background task would, in Go, just be an explicit
value passed down the call chain or carried on `context.Context` — arguably a more natural fit given
Go's context-propagation idiom is designed exactly for "cancellation/deadline/value needs to reach a
child goroutine, including ones outliving the original call."

**Ecosystem.** `golang.org/x/net/webdav` is a real, maintained, actually-used WebDAV server package
(unlike most other candidates here) — it still requires a custom `webdav.FileSystem` implementation
(comparable DIY effort to the existing `DatabaseStore`/`IStore` implementation over NWebDav.Server,
not a shortcut around that work, but a solid foundation rather than nothing). SQLite: either
`mattn/go-sqlite3` (cgo-based, mature) or `modernc.org/sqlite` (pure Go transpile of SQLite's C
source, no cgo at all) — meaning the DB access path doesn't need cgo, only the yEnc FFI does. EF
Core's migrations/tracked-entity convenience has no direct equivalent; expect hand-rolled SQL or a
lighter query builder plus `golang-migrate`, a real loss of convenience versus EF Core, not a
blocker. Archive parsing: `nwaples/rardecode` (RAR) and `bodgit/sevenzip` (7z) are real, maintained,
pure-Go libraries, but far less battle-tested than SharpCompress; the live-streaming-header-while-
still-downloading trick `RarProcessor` relies on would need to be re-verified against
`rardecode`'s API. FFI to `rapidyenc`: cgo — real, known costs (per-call overhead higher than native
Rust FFI, complicates cross-compilation since a C toolchain is needed per target unless using the
`zig cc` trick, slows builds) but the calls here are per-segment (~700KB), not per-byte, so cgo's
per-call overhead is irrelevant at this granularity — very workable in practice.

**QS-4 fit:** likely the simplest footprint story of any candidate surveyed anywhere in this
analysis — a single static binary, no runtime layer in the container at all (not even Rust's need
for glibc/musl consideration is simpler), small idle RSS, GC present but tunable
(`GOGC`/`GOMEMLIMIT`) and not a concern at this workload's scale.
**QS-1 fit:** goroutine-per-read-ahead-task + channel is a direct, idiomatic match for the existing
read-ahead/priority-queue design; `context.Context` deadlines map cleanly onto range-read timeouts.

**Verdict:** the "boring, proven, gets it done" choice — and for a solo-maintainer OSS project, boring
might genuinely be the right answer independent of raw performance: Go's standard-library WebDAV
package, mature semaphore/rate-limiter packages, and huge ecosystem for exactly this class of
networked service make it the lowest-risk rewrite target on this list, at the honest cost of losing
some of EF Core's and SharpCompress's polish.

## 2. Kotlin (JVM+coroutines / native-image, vs. Kotlin/Native)

These are two very different bets and must not be conflated.

**Kotlin/JVM + coroutines, optionally GraalVM native-image compiled.** Coroutines' structured
concurrency and `Flow`/`Channel` types are a close ergonomic match to what this codebase does with
`Task`/`Channel<T>` today — arguably nicer than either raw Java virtual threads or reactive streams
for this kind of pipeline-heavy, backpressure-sensitive code, because coroutine context elements
propagate automatically through child coroutines (including ones launched in their own scope), which
directly solves the exact problem this codebase currently solves with the `CancellationToken`-keyed
`ConcurrentDictionary` ambient-priority hack (`CancellationTokenContext.cs`) — no bespoke plumbing
needed for "priority must survive a detached background download loop." Layered on top, GraalVM
native-image compilation carries the *same* tradeoffs the peer Java+GraalVM proposal covers in depth
(reflection/closed-world restrictions, needs reachability metadata for anything dynamic), with
Kotlin's null-safety and more concise data classes reducing a class of NPE-shaped bugs for the same
capability — a syntax/ergonomics win layered on an unchanged technical foundation, not a new
capability Java lacks.

**Ecosystem.** WebDAV: no Kotlin-specific library exists; would draw on the same JVM libraries the
Java proposal would (e.g. Milton, or a hand-rolled servlet-based implementation) — no better or worse
than the Java path. SQLite: `sqlite-jdbc` (JNI-based, mature, widely deployed), works under
native-image with the right configuration. NNTP: no purpose-built async NNTP library in the JVM
ecosystem for either language — build on Netty (mature, excellent fit for connection-pooled,
prioritized binary protocols). Archive parsing: Apache Commons Compress + `junrar`, same as the Java
proposal, no Kotlin-specific win. FFI to `rapidyenc`: Project Panama (`java.lang.foreign`, stable
since JDK 22) gives genuinely modern, low-boilerplate native FFI — comparable ergonomics to cgo or
Rust FFI for calling a native SIMD library, though Panama's support under GraalVM native-image
specifically (vs. plain JIT mode) needs verification against whatever JDK/GraalVM version gets
pinned; it has historically lagged JIT-mode maturity.

**Kotlin/Native, separately:** a much higher-risk bet — its own LLVM-based backend that does not run
JVM bytecode, meaning essentially none of the JVM ecosystem above (SQLite driver, archive libraries,
WebDAV framework, Netty) is usable as-is. Adopting Kotlin/Native would mean rebuilding almost
everything the JVM ecosystem provides from a much smaller native-Kotlin library pool — converging
toward a worse-resourced version of the Rust proposal rather than a genuine alternative. Should be
set aside for this project.

**QS-4/QS-1 fit:** same profile as the Java+GraalVM proposal for the native-image path (see that
document for footprint/startup specifics); coroutines are at least as good a concurrency-model fit
for QS-1/QS-3 as Java virtual threads, arguably better for this codebase's specific channel-heavy
pipeline shape.

**Verdict:** Kotlin/JVM+native-image is a legitimate, arguably strictly nicer refinement of the
Java+GraalVM proposal for this codebase's specific concurrency patterns, at the cost of a smaller
(though still large) contributor/hiring pool than plain Java. Kotlin/Native is not a serious
contender today and shouldn't be confused with the JVM-mode option above.

## 3. Elixir/Erlang (BEAM)

**Fit for this workload.** This is the most conceptually elegant match on this list for the project's
actual pain points as documented elsewhere in this analysis: `ConnectionPool`/`ConnectionLock`
(explicitly ChatGPT-authored, untested), and `ProviderCircuitBreaker`'s hand-rolled
consecutive-failure-counting/cooldown-doubling logic are, on BEAM, close to what the runtime gives
you for free — isolated lightweight processes, "let it crash" supervision trees, and per-provider
fault isolation (one provider's connection process crashing doesn't touch any other provider's) as
language/runtime primitives rather than application code a solo maintainer has to write, and — since
there is no backend test project in this repo today — debug without a safety net. The
`PrioritizedSemaphore`/ambient-priority-context machinery this codebase hand-rolls to get
interactive-vs-background prioritization across a detached background task has reasonably direct
BEAM analogues (message priority, process priority hints, or simply structuring the supervision tree
so interactive request-handling processes are scheduled preferentially).

**Honest ecosystem gaps.** WebDAV server support in Elixir is thin to nonexistent — there is no
equivalent of Go's `x/net/webdav` or even a widely-used third-party package; PROPFIND/OPTIONS/range-
GET handling would need to be built from scratch on Plug/Cowboy. This is probably the single largest
ecosystem gap of any candidate surveyed — every other candidate here gets at least a partial WebDAV
foundation for free or cheap; Elixir gets none. NIFs (native function calls) for `rapidyenc` are a
well-trodden pattern, but a real integration cost specific to BEAM: a long-running or misbehaving NIF
call can block or crash the scheduler thread it runs on, which cuts directly against BEAM's core
"isolated crash domains" pitch — doing this safely requires dirty schedulers and careful attention to
call duration, adding complexity exactly where Go/Rust/Kotlin/C++/Zig treat native FFI as close to
free. Raw byte-pumping throughput: BEAM's binary handling (reference-counted binaries, no-copy
slicing) is reasonable for this if done carefully, but BEAM is not the runtime typically chosen to
maximize raw single-stream throughput — it wins on concurrent-many-small-things, not
few-large-fast-things. For QS-1 (time to first byte on one seek) BEAM's low-jitter preemptive
scheduling is fine; for QS-3 at genuinely high sustained per-stream throughput it's the weakest
story among the credible candidates here, though at this project's realistic scale (a handful of
concurrent home-lab streams, network-bound regardless) that weakness is unlikely to actually bind.

**QS-4 fit:** the BEAM VM has a real memory floor (tens of MB minimum), higher than a static Go/Rust/
Zig binary but broadly comparable to or better than a JVM; ships as a self-contained release with no
external runtime to provision, so it doesn't violate QS-7.

**Verdict:** the most philosophically satisfying match for this codebase's existing hand-rolled
resilience layer, undercut by the one gap that matters most for a WebDAV server — there is
essentially no WebDAV ecosystem to build on, meaning this candidate pays for an entire protocol
server from scratch that every other candidate gets partially or fully for free. A fascinating
alternate-universe pick, not the pragmatic one.

## 4. C++

**Fit for this workload.** Technically capable of everything here — manual control over allocation
and threading, zero runtime overhead, and the cheapest possible FFI story for `rapidyenc` since it's
already a C library and a C++ caller pays literally zero marshaling cost.

**Honest read: probably the worst fit for this specific project**, not because C++ is incapable but
because of who has to maintain the result. This is a solo/small-contributor OSS project with **zero**
backend test coverage today and no CI gate beyond a Docker image build (see CLAUDE.md and
[11-risks-and-technical-debt.md](../11-risks-and-technical-debt.md)). Introducing manual memory
management into a project with no automated safety net to catch use-after-free/double-free/buffer-
overrun-class bugs is a straight regression from every other candidate surveyed anywhere in this
analysis, .NET included — those bug classes simply don't exist in the current codebase and would be
newly introduced. The WebDAV/HTTP ecosystem in C++ is fragmented: no standard WebDAV layer exists at
all, and even HTTP serving means committing to a heavier third-party framework (Boost.Beast, Pistache)
with less standardization and community convention than any other candidate here — every protocol
detail NWebDav.Server currently provides would need re-implementing or wrapping around an unfamiliar
library.

**Ecosystem.** SQLite: zero friction (SQLite is C; this is the one place C++ has a genuine, real
edge). Archive parsing: mature and strong (7-Zip's own C++ source tree, `libarchive`). FFI to
`rapidyenc`: the cheapest of any candidate on this list, a direct, zero-overhead C call.

**QS-4 fit:** can plausibly be the smallest footprint of all candidates (no runtime, no GC) — but
that only matters if the result is correct; a smaller but occasionally-segfaulting or leaking service
serving Sonarr/Radarr/Jellyfin isn't a real QS-4 win.

**Verdict:** a clear "probably not." Every genuine advantage it has (cheapest FFI, tiny footprint) is
available at far lower maintenance risk via Rust or Go; the memory-safety regression for a
zero-test-coverage solo-maintainer project is disqualifying on its own, independent of any
performance argument.

## 5. Zig

**Fit for this workload.** A "safer C" with comptime metaprogramming, explicit allocators, and no
hidden control flow — a more reviewable, honest memory model than C++ for whatever part of a project
like this sits closest to the metal. But that's precisely the caveat: this project's closest-to-metal
part, yEnc decode, is *already* fully delegated to the native `rapidyenc` library, so Zig's core
selling point over plain C is already moot for the one place raw performance matters most in this
codebase.

**Maturity.** Pre-1.0 (still in the 0.1x release series), with a track record of breaking language
and standard-library changes between minor versions — real adoption churn risk independent of the
language's technical merits. No mature async HTTP or WebDAV framework exists; Zig's own async story
has itself been reworked/removed more than once in the language's history. Adopting Zig today would
mean building HTTP parsing, WebDAV semantics, connection pooling, and likely SQLite bindings almost
entirely from scratch or on immature third-party libraries — a materially bigger bet than any other
candidate surveyed here.

**FFI to `rapidyenc`:** genuinely excellent — `@cImport` makes calling a C header nearly frictionless
— but Go, Rust, and C++ all offer comparably good FFI for this same narrow task, so it isn't a
differentiator large enough to offset everything else.

**QS-4 fit:** would likely produce the smallest possible static-binary footprint of any candidate
here (no GC, minimal runtime), a genuine theoretical strength.

**Verdict:** watch this space, not ready for a project like this today. The ecosystem gaps (no mature
WebDAV/HTTP framework, pre-1.0 churn risk) are disqualifying on their own regardless of the language's
technical merits, and its signature strength is moot here since the metal-adjacent work is already
delegated to native code.

## 6. Staying on .NET, modernized: Native AOT

The owner's actual complaint was that the *analysis* was too conservative, not necessarily that .NET
itself is categorically wrong — so the highest-value item in this whole survey may be the one that
changes nothing about the language.

**What it is.** `dotnet publish -r <rid> -p:PublishAot=true` (Native AOT, shipped since .NET 7,
matured further through .NET 8–10) compiles the app to a single self-contained native binary: no JIT,
no separate CLR runtime layer in the container, startup in milliseconds instead of the JIT-hosted
CLR's ~100s of ms, and meaningfully lower idle RSS — directly attacking QS-4 (footprint) and QS-5
(startup/recovery) without touching a single line of the 96%-inherited business logic, the queue
pipeline, the stream composition, or any contributor's existing skill set. This is the only candidate
on this entire list — including Rust and Java+GraalVM — that requires *zero* rewrite of working code.

**Constraints, honestly stated.** Native AOT disallows/restricts unbounded runtime reflection,
`Reflection.Emit`, and dynamic `Type` loading — the same closed-world discipline the peer
Java+GraalVM proposal has to account for. EF Core has official, if still-maturing, Native AOT support
(EF Core 8/9+) but requires precompiled models (`dotnet ef dbcontext optimize`) and precludes some
dynamic-LINQ patterns — this codebase's `DavItem` tree queries and EF configuration would need a
concrete audit for AOT-incompatible patterns (dynamic includes, reflection-based mapping), a bounded,
answerable task rather than a rewrite. ASP.NET Core Minimal APIs and Kestrel are AOT-supported
directly; the one genuinely open question is whether `NWebDav.Server` — a small, low-activity
third-party dependency — is itself AOT-compatible, since WebDAV method routing and PROPFIND XML
(de)serialization are exactly the kind of thing that tends to lean on reflection internally. That
single unknown is what actually determines whether this is a multi-week spike or a multi-month
project, and it's worth resolving with a short, concrete experiment before committing to anything
else in this survey.

**FFI to `rapidyenc`.** Fully AOT-compatible today with zero change required — P/Invoke has always
been Native-AOT-friendly, and this codebase already calls `rapidyenc` through exactly this mechanism
via `RapidYencSharp`. This is a pure carry-over win, uniquely available to this option alone among
all candidates surveyed.

**QS-1/QS-4 fit.** The concurrency model doesn't change at all — same `Task`/`async`, same
`Channel<T>`, same `PrioritizedSemaphore`/`ConnectionPool` code, untouched — so QS-1 seek latency is
essentially unaffected either way, since it's dominated by network round trips, not JIT/CLR overhead.
QS-4 footprint improves meaningfully (lower idle RSS, much faster cold start) but won't reach the same
floor as a genuinely runtime-free static binary in Go/Rust/Zig — there's still a GC and a somewhat
larger baseline RSS than those.

**Verdict:** probably the single highest-value, lowest-risk item in this entire survey — it can
plausibly capture a large fraction of the footprint/startup win every rewrite candidate promises,
while keeping the entire codebase, every contributor's existing skills, and upstream mergeability
completely intact. The one real unknown (`NWebDav.Server` + EF Core AOT compatibility) is answerable
with a days-long spike, not a rewrite bet, and arguably deserves to be attempted *before* any
full-language-rewrite proposal in this document is seriously greenlit.

## 7. Comparative ranking table

| Candidate | QS-4 footprint fit | QS-1/QS-3 concurrency-latency fit | Ecosystem maturity for this workload | Solo-maintainer risk | One-line verdict |
|---|---|---|---|---|---|
| **Go** | Excellent — static binary, no runtime layer at all | Excellent — goroutines/channels are a near-direct match for the existing read-ahead/priority design | Strong — real WebDAV package, workable cgo FFI, decent-but-less-mature archive libs, no EF-Core-equivalent | Low | Boring, proven, best risk-adjusted pick among the surveyed candidates |
| **Kotlin/JVM + native-image** | Same profile as Java+GraalVM (see that proposal) | Very good — coroutines solve the ambient-priority problem more natively than Java threads/reactive stacks | Same as Java+GraalVM (shared JVM ecosystem), plus modern Panama FFI | Medium (smaller pool than Java, still large) | A nicer-ergonomics refinement of the Java+GraalVM proposal, not a different bet |
| **Kotlin/Native** | Good in theory | Unproven for this shape of workload | Poor — can't reuse JVM libraries at all | High | Not a serious contender today |
| **Elixir/Erlang (BEAM)** | Good — no external runtime, moderate VM floor | Mixed — excellent fault-isolation/latency-jitter story, weaker raw sustained-throughput story | Poor — essentially no WebDAV ecosystem to build on | Medium-high (unfamiliar paradigm, but less code to get wrong) | Conceptually the best match for this codebase's resilience layer, undercut by having to build WebDAV from scratch |
| **C++** | Excellent in theory | Excellent in theory | Fragmented — DIY HTTP/WebDAV, strong only on SQLite/archive/FFI | Very high (manual memory management, zero tests) | Probably not — the one project shape where memory-safety regression is disqualifying |
| **Zig** | Excellent in theory | Unproven — no mature async ecosystem | Weak — pre-1.0, no mature WebDAV/HTTP framework | High (language and ecosystem churn) | Watch this space, not ready today |
| **.NET Native AOT (no rewrite)** | Good — real improvement over JIT-hosted CLR, not as low as static Go/Rust/Zig binaries | Unchanged (same async/Task model) — neutral, not a regression | Full — entire existing codebase, one open compatibility question (NWebDav.Server + EF Core AOT) | Lowest — zero rewrite | The cheapest, lowest-risk win in this whole survey; validate with a short spike before any rewrite is greenlit |
| *Rust (peer deep-dive, by reference)* | Excellent | Excellent | Weakest ecosystem depth of the "serious" tier (WebDAV, archive libs less mature than .NET's) | Medium-high (steep learning curve, but memory-safe) | See dedicated proposal |
| *Java+GraalVM (peer deep-dive, by reference)* | Good | Good | Strong (mature JVM ecosystem) minus native-image reflection friction | Medium | See dedicated proposal |
