# Redesign Proposal: Rust Backend Rewrite

**Status**: input to architecture brainstorm, not a decision. Written to be actually implementable
from, per instruction — where costs are real they're stated plainly, but this document does not
pre-emptively conclude "not recommended." §9.3 of `09-architecture-decisions.md` already took the
conservative "no, not now" position; the project owner explicitly rejected that framing and asked
for a serious proposal. This is that proposal.

Confidence discipline used throughout: every crate name is one I have real (if not always current)
knowledge of; anything I'm not confident is still accurately named/maintained is marked
**(verify)**. Every specific number is marked **(hypothesis)** unless grounded in something citable.

---

## 1. Target architecture

### 1.1 Async runtime: tokio

Not a close call. tokio is the de facto standard async runtime for network-I/O-bound Rust services,
has first-class `Semaphore`, `mpsc`/`oneshot` channels, `select!`, and `AsyncRead`/`AsyncWrite`
traits that map directly onto patterns already in this codebase: `PrioritizedSemaphore`'s two-queue
odds-based gate is a `tokio::sync::Semaphore` plus a small custom waiter-priority wrapper (or the
`tokio-util` priority-queue patterns); `MultiSegmentStream`'s bounded read-ahead channel is a
`tokio::sync::mpsc::channel(capacity)` almost verbatim. There is no serious alternative runtime for
this workload (async-std is effectively dormant; smol is niche) — tokio it is.

### 1.2 Web framework: axum over actix-web

**Recommendation: axum.** Both are viable; axum wins on fit for this specific system:

- axum is built directly on `hyper` + `tower`, the same stack tokio's own maintainers steward. It
  has no actor-runtime layer to reconcile with tokio (actix-web historically ran its own actor
  system under the hood, `actix-rt`, though modern actix-web has converged heavily on tokio too —
  the distinction matters less than it used to, but axum's tower-middleware model composes more
  directly with hand-rolled protocol code like a custom WebDAV method dispatcher).
- `tower::Service` middleware (auth, the `x-api-key` check, request logging) is exactly the shape
  needed to replicate the two REST families here (`Api/Controllers`-equivalent and
  `Api/SabControllers`-equivalent as two `Router`s merged with different middleware stacks) — this
  maps cleanly onto D17 (two independent error-handling shapes kept deliberately separate for
  SABnzbd protocol authenticity).
- WebDAV is not a native HTTP method set in either framework — both require intercepting
  `PROPFIND`/`MKCOL`/`LOCK`/etc. Axum's `MethodFilter`/raw-method routing and body-streaming primitives
  (`axum::body::Body` wraps a `Stream` directly, so `AsyncRead`-composed streams plug straight into a
  response body — the direct analog of what `GetAndHeadHandlerPatch` does with `CopyToAsync`) are
  a slightly better fit for "return an arbitrary composed byte stream with correct
  `Content-Range`/`Content-Length` semantics" than actix-web's actor-oriented `HttpResponse` builder,
  though both can do it.

This is a judgment call between two mature, actively maintained frameworks, not a maturity gap.

### 1.3 WebDAV layer

**Honest maturity assessment**: there is no WebDAV crate with anywhere near NWebDav.Server's
adoption or feature completeness. The most relevant prior art I have real knowledge of:

- **`dav-server`** (formerly `webdav-handler-rs`, crates.io) — an async, hyper-integrated WebDAV
  *protocol* implementation (PROPFIND/PROPPATCH/MKCOL/COPY/MOVE/LOCK, WebDAV XML property handling)
  built around a `DavFileSystem` trait you implement — this is structurally the same shape as
  NWebDav's `IStore`/`IStoreCollection`/`IStoreItem` abstraction that `DatabaseStore` already
  implements today. **(verify — I'm reasonably but not certainly confident this crate's current name
  and maintenance status are accurate; check crates.io before committing effort here.)** If it's as
  I remember it, `DatabaseStore`/`DatabaseStoreCollection`/`BaseStoreStreamFile`'s logic (path
  resolution over SQLite, `SubType`-dispatch to typed wrappers, range-aware stream serving) ports
  almost directly onto its `DavFileSystem`/`DavFile`/`DavDirectory` traits — same shape, different
  trait names.
- If that crate turns out unmaintained or too limited (e.g., no partial-content/range support, which
  is the one feature this project cannot compromise on), **the fallback is a custom WebDAV layer on
  raw axum**: intercept the handful of WebDAV verbs actually exercised here (this codebase only
  needs GET/HEAD/PROPFIND/PUT/DELETE/MOVE/OPTIONS per `WebDav/Requests/*` — it does not need a
  general-purpose WebDAV server, it needs *exactly* the subset Sonarr/Radarr/rclone/media-players
  exercise). This is a bounded, well-specified problem (WebDAV's core RFC 4918 XML property format
  for PROPFIND responses is the only non-trivial part) — realistically a few thousand LOC, similar
  in scope to what `NWebDav.Server` itself is (a small, focused library, currently at
  `0.2.0-beta.2` per this repo's own `.csproj` — **worth noting NWebDav itself is pre-1.0**, so the
  "mature WebDAV library" bar this project currently clears is lower than it might appear).

**Net assessment**: this is the least certain part of the whole proposal from a "does a ready-made
crate exist" standpoint, but the fallback (hand-rolled, RFC-4918-subset WebDAV on axum) is
well-scoped and not qualitatively harder than what upstream already maintains today in C#.

### 1.4 SQLite + migrations: sqlx

**Recommendation: sqlx over sea-orm or diesel.**

- **sqlx** — async-native (built for tokio from the ground up, unlike diesel which is sync-first
  with `diesel-async` bolted on), compile-time-checked queries via macros, and — critically —
  the core-domain research finding that `DatabaseStoreCollection`'s hot path
  (`GetDirectoryChildAsync`, a `WHERE ParentId = ? AND Name = ?` indexed lookup) is *already* about
  as close to raw SQL as EF Core gets (`core-domain.md` §5, alternatives table: "the ORM overhead is
  likely marginal") argues for **not** reaching for a full ORM's abstraction cost when the actual
  query surface is this simple. sqlx's `migrate!` macro embeds a directory of plain `.sql` files and
  applies them in order at startup — this is a closer mechanical match to "71 EF Core migrations"
  than it first sounds: **EF Core migrations are fundamentally DDL** (`CREATE TABLE`/`ALTER TABLE`
  statements, generated or hand-written) — translating 71 of them to 71 ordered `.sql` files is
  tedious but genuinely mechanical, not a redesign. The risk is re-deriving the *final* schema state
  correctly (you'd realistically want to collapse to one "current schema" migration plus a
  hand-verified diff against the live EF-generated SQLite file, rather than literally replaying 71
  migrations' historical DDL quirks).
- **sea-orm** (built on sqlx, adds an EF-Core-like entity/ActiveRecord layer with its own migration
  DSL) is the closer *ergonomic* analog to what this codebase's `DavDatabaseContext`/`DbSet<T>`
  pattern looks like today, and would ease the mechanical port of entity *shapes* (`DavItem`,
  `QueueItem`, `HistoryItem` as generated entities) at the cost of reintroducing ORM overhead on the
  exact hot path the research already flagged as not needing it. **Reasonable alternative if the
  team weights "faster mechanical port of 70+ migrations and entity models" over "leanest possible
  hot-path query cost"** — I'd default to sqlx but this is a legitimate judgment call, not a clear
  winner either way.
- **diesel**: ruled out primarily on the sync-first architecture mismatch with a tokio-everything
  codebase; `diesel-async` exists but is a secondary, less-traveled path relative to sqlx's native
  async design.

### 1.5 NNTP client stack — the part that maps best onto Rust

This is the strongest section of the case for Rust, and it's worth being precise about *why*,
because the answer is not "faster NNTP" — the protocol itself is trivial (a handful of ASCII
commands: `AUTHINFO`, `GROUP`, `STAT`, `HEAD`, `BODY`, `ARTICLE`, `DATE`). No mature, actively
maintained pure-Rust NNTP client crate is known to me with confidence — **this layer needs to be
hand-written**, same as `BaseNntpClient`/`UsenetSharp` had to be hand-written in the C# ecosystem
too (there was no off-the-shelf .NET NNTP client either — `usenet-streaming.md` §5 confirms "no
existing full-stack alternative was found" even for .NET). This is not a Rust-specific gap; it's
because Usenet client libraries generally don't exist in mainstream package ecosystems.

**What Rust actually buys here** is the *layering above* the raw protocol — exactly the part that
*is* this repo's own code today (D20: "hand-rolled decorator layering... is INHERITED structure"):

- **Connection pooling** (`ConnectionPool<T>`, currently explicitly ChatGPT-authored with zero
  tests, D-flagged as a real risk in `usenet-streaming.md` §4) maps onto either a hand-rolled
  `tokio::sync::Semaphore` + `VecDeque` idle-stack (a very small amount of code once you're not
  fighting the language for async primitives) or an existing generic pool crate: **`deadpool`** or
  **`bb8`** (both real, actively maintained, widely used generic async object-pool crates I'm
  confident about) — either would replace the hand-rolled, untested `ConnectionPool`/`ConnectionLock`
  with a library that has its own test suite for exactly the concurrent acquire/return/dispose race
  conditions flagged as a risk today. This is a genuine, concrete correctness win, not just a
  rewrite-for-its-own-sake one.
- **Multi-provider failover + circuit breaking** (`MultiProviderNntpClient`,
  `ProviderCircuitBreaker`) is pure business logic — a `Vec<Provider>` ordered/filtered by health
  state, no framework dependency at all. Translates near-verbatim.
- **Priority propagation** is the single cleanest structural win available in this whole rewrite.
  Today's `CancellationTokenContext` (D24) is an admitted workaround: a **static, global,
  `ConcurrentDictionary` keyed by `(CancellationToken, Type)`** used as ambient context because
  `AsyncLocal<T>` doesn't survive a detached `Task.Run` continuation (`MultiSegmentStream`'s
  background read-ahead pipeline). This is flagged in the existing research as a real leak risk if
  any call site forgets to dispose its context handle. **In Rust with tokio, this doesn't need a
  workaround at all**: spawn the read-ahead pipeline with `tokio::spawn` and pass the priority as an
  explicit, owned value captured in the spawned future's closure (or as an explicit parameter on
  every `INntpClient`-equivalent trait method) — Rust's ownership model makes "this value must be
  explicitly threaded to where it's needed, including across an async task boundary" the *natural*
  way to write the code, not an opt-in discipline enforced by a static dictionary and a `Dispose`
  contract. This directly retires D24's flagged risk as a structural side effect of the language,
  not an extra feature to build.
- **Bandwidth throttling / per-provider stats** (`TokenBucket`, `ProviderCountingYencStream`) are
  simple `AsyncRead` wrapper types — same "wrap the stream, override read" pattern already used in
  C#, ports directly onto Rust's `tokio::io::AsyncRead` trait.

**yEnc decode — already solved, this is the headline finding to preserve**: `usenet-streaming.md` §0
established that yEnc decode is **already native C** (`rapidyenc`, SIMD SSE2/AVX2/NEON) via the
`RapidYencSharp` P/Invoke binding, itself a dependency of `UsenetSharp`. Rust can FFI into the
*exact same* `rapidyenc` native library via `bindgen` generating raw bindings, wrapped in a
hand-written `rapidyenc-sys` crate (this is the standard Rust pattern for wrapping a C library — a
`-sys` crate holds the raw `bindgen`-generated FFI surface, a safe wrapper crate sits on top). I am
not aware of a pre-existing `rapidyenc-sys` crate on crates.io **(verify)** — this would need to be
written, but it is genuinely small (a handful of `extern "C"` function signatures over a header
file) and — this is the important point — **it means Rust does not need to reimplement or
re-validate yEnc decode correctness at all**. The same battle-tested native binary that decodes
yEnc today would decode it under Rust too. This is the one area where "rewrite in Rust for
performance" is provably *not* rewriting the actual hot loop — it's rewriting everything around an
already-optimal hot loop.

### 1.6 RAR / 7z / PAR2 parsing

Be precise about what's actually needed, because it's less than "a RAR/7z decompression library":

- **7z**: `SevenZipProcessor` already **hard-rejects** compressed or solid+encrypted 7z
  (`Unsupported7zCompressionMethodException`) — only store-mode (uncompressed) 7z is supported. This
  means the C# code today only needs 7z's *central directory / header parsing*, not real
  decompression. A pure-Rust crate I have some (moderate, not high) confidence exists is
  `sevenz-rust`/`sevenz-rust2` **(verify)** — even if unsuitable or unmaintained, the actual scope
  needed (parse 7z headers to get entry names + byte offsets within store-mode archives) is a small,
  well-specified binary-format-parsing task, not a decompression engine.
- **RAR**: today's `RarProcessor` uses `SharpCompress` (a general .NET archive library) but only for
  **live header parsing** — it never decompresses RAR contents either; it emits byte ranges *inside
  the still-undownloaded archive* for the WebDAV layer to later stream directly from Usenet. Scene
  releases store video in RAR without additional compression (video is already compressed), so this
  project's actual RAR need has always been "parse RAR4/RAR5 volume/file headers, resolve part
  ordering" — never real Huffman/PPMd decompression. I am **not confident** a mature, actively
  maintained pure-Rust RAR5-header-parsing crate exists (the `unrar` crate, where it exists, is
  typically an FFI wrapper around the proprietary `unrar` library, which raises its own
  licensing/distribution questions for a rewrite explicitly motivated by owning the whole stack).
  **Honest conclusion: this likely needs a hand-written RAR4/RAR5 header parser** — bounded scope
  (volume headers, file headers, the part-number-from-header-vs-filename reconciliation heuristic
  already in `RarAggregator.ValidateVolumes`/`ValidatePartNumbers`), realistically low-thousands of
  LOC given it mirrors what the C# code already narrowly does on top of SharpCompress rather than
  using SharpCompress's full feature surface. This is a real, non-trivial cost, but it is *the same
  shape of cost* that would exist rewriting in Go or any other language — it is not Rust-specific.
- **PAR2**: trivial to port either way. `Par2Recovery` is genuinely small (packet-header framing +
  a single `FileDesc` packet type, ~3 commits' worth of code, `core-domain.md` §1.3) — no repair
  engine exists to reimplement (D6, deliberately descriptor-only). This is a few hundred lines of
  binary parsing, directly and mechanically portable.

### 1.7 Queue pipeline / deobfuscation / aggregation — business logic, not framework

The deobfuscation heuristics (`GetFilenamePriority`, `IsCloseToYencodedSize` size-tolerance
matching, PAR2-vs-NZB-subject-vs-yEnc-header name reconciliation) and the aggregation logic
(`RarAggregator`'s part-ordering delta reconciliation, `RenameDuplicatesPostProcessor`'s
`(ParentId, Name)`-uniqueness disambiguation) are **plain algorithms over POCOs** — no ASP.NET/EF
dependency in the core logic itself, only at the I/O edges (fetching articles, writing DB rows).
This is genuinely one of the more directly-portable parts of the system *in terms of logic
translation* — the risk here isn't "does Rust support this," it's the migration-risk point covered
in §5 below: these heuristics were tuned against years of real-world obfuscated-release naming
conventions with zero test coverage anywhere to pin down what "correct" currently means, so a
line-by-line port is really "translate this exact logic including its accumulated edge-case
handling," not "redesign it," and getting that translation subtly wrong is invisible until a user's
specific release hits the untested edge.

---

## 2. Mechanical port vs. genuine redesign — summary table

| Component | Effort class | Why |
|---|---|---|
| PAR2 packet parsing | **Mechanical** | Small, self-contained, no external deps either language |
| NZB XML parsing | **Mechanical** | Streaming XML parse (`quick-xml` crate, real and mature, is a direct analog to `System.Xml.XmlReader`'s forward-only mode) |
| Deobfuscation heuristics (steps 1–3) | **Mechanical, but high-stakes** | Pure logic, directly translatable — but zero tests to verify the translation is byte-for-byte behaviorally identical |
| Aggregation / part-reconciliation logic | **Mechanical, but high-stakes** | Same caveat as above |
| DB schema (71 EF migrations) | **Mechanical (tedious)** | DDL translates to DDL; realistic approach is "derive current schema, hand-verify against live DB," not literally replay 71 migrations |
| WebDAV protocol surface | **Partial redesign** | No off-the-shelf trait-for-trait equivalent to NWebDav confirmed; likely hand-rolled subset (§1.3) |
| RAR header parsing | **Redesign (bounded)** | No confirmed mature pure-Rust crate for the exact narrow slice needed; hand-written, but bounded in scope |
| NNTP protocol + pooling/failover/circuit-breaker | **Redesign, and the best-fit one** | No off-the-shelf crate (true in C# too), but tokio primitives are a *better* fit than the workarounds the current ambient-context/ChatGPT-authored pool needed |
| yEnc decode | **Not needed at all** | FFI straight into the same native `rapidyenc` binary already in use |
| SABnzbd API wire shape | **Fixed contract, not portable business logic** | Must reimplement exactly, cannot redesign (Sonarr/Radarr parse it) — mechanical in the sense that the target shape is externally dictated, not a design decision |

---

## 3. Migration strategy

### 3.1 Big-bang is not the only realistic path — a strangler-fig cut point exists, with one real complication

The building-block view confirms `DavFileStreamFactory` is the single seam already shared between
the WebDAV read path and the fork's own prefetch-cache warmer (`05-building-block-view.md` §5.2.4) —
i.e., upstream itself already treats "read the virtual filesystem and serve bytes" as a distinct
concern from "ingest and write the virtual filesystem." That seam is exactly where a strangler-fig
cut is most natural:

**Phase 1 candidate: a second process, in the same container, serving WebDAV + streaming reads,
written in Rust, reading the *same* SQLite file and blob-store directory the existing .NET ingestion
pipeline continues to write.**

- SQLite in WAL mode supports multiple reader connections concurrently with a single writer — the
  .NET `QueueManager`/ingestion pipeline remains the sole writer (as it already effectively is today:
  writes only happen in `MarkQueueItemCompleted`'s single `SaveChangesAsync` per queue item, an
  infrequent-relative-to-reads event), while the new Rust WebDAV service opens the same file
  read-only. This is a legitimate, well-understood SQLite deployment pattern, not a stretch.
- The frontend's Express proxy (`server/app.ts`) already treats `/view`, `/content`,
  `/completed-symlinks`, PROPFIND/OPTIONS as one routable prefix group pointed at `$BACKEND_URL` —
  repointing that specific prefix group to a second internal port (the new Rust service) instead of
  the existing Kestrel host is a small, contained change entirely on the frontend side, requiring
  zero frontend rewrite (D33 already established the frontend is architecturally untouched by this
  fork to date; this would be the first surgical exception, and a narrow one).
- **The real complication, stated honestly**: the blob store is not a neutral format. It's
  zstd-compressed (`ZstdSharp.Port`, wrapping the *standard* zstd reference algorithm — this part is
  free and cross-language, the `zstd` Rust crate binds the same reference C library) containing
  **MemoryPack**-serialized payloads. MemoryPack is a C#-specific fast binary serializer with its own
  wire format — there is no existing Rust decoder for it. This is not a blocker, but it is real work:
  the Rust side would need to replicate MemoryPack's binary layout for exactly the three fixed shapes
  actually used (`DavNzbFile`, `DavRarFile`, `DavMultipartFile` — per `core-domain.md` §1.2, these are
  simple field-based POCOs, not polymorphic/dynamic types), which is a bounded reverse-engineering
  task (documenting and replicating one serializer's layout for three known struct shapes) rather
  than "implement general MemoryPack support." **This is the single most concrete, must-solve-first
  technical unknown in the whole strangler-fig plan** — it should be a short, dedicated spike
  (write a Rust decoder for one real blob file written by the existing C# code, byte-for-byte, before
  committing to the rest of Phase 1) rather than an assumption carried into a larger build.
- **Alternative that sidesteps the MemoryPack problem entirely**: don't go through the blob store at
  all from Rust — instead expose a small internal gRPC/HTTP API from the *existing* .NET process that
  the Rust WebDAV service calls to resolve "give me the segment list + metadata for `DavItem` X,"
  keeping MemoryPack encode/decode entirely inside the process that already understands it. This
  trades "reimplement MemoryPack's wire format" for "one more internal network hop per file open" —
  given file opens are far less frequent than the byte-range reads that follow (one open, then many
  seeks/reads per playback session), this hop cost is likely negligible relative to the NNTP
  round-trips already dominating the read path. **This is probably the lower-risk Phase 1 design**:
  Rust owns WebDAV protocol handling, NNTP client stack, and stream composition/decode (the parts
  where Rust's benefits are most concrete — see §4); the existing .NET process remains the sole
  reader/writer of SQLite and the blob store, exposed to the Rust process over a narrow internal API.
  This also means the Rust service holds **no direct DB dependency at all** in Phase 1, deferring the
  sqlx-vs-sea-orm decision until (if) a later phase actually moves persistence itself.

### 3.2 What stays big-bang-only

Queue/ingestion (deobfuscation, file processors, aggregators, post-processors) is much harder to
strangle incrementally: it's a single serial pipeline (D4) whose output is exactly the DavItem/blob
rows the read path consumes, and it's the most upstream-coupled, most heuristic-heavy, least
test-covered part of the whole system (§2 above, §5 below). A partial port here (e.g., "RAR
processing in Rust, everything else in C#") would need to cross the process boundary *per file
within a single release*, which is a much chattier, harder-to-get-right boundary than "one WebDAV
read path talks to one ingestion process." **Realistic recommendation: if a full rewrite is pursued,
treat Queue/ingestion as a single big-bang unit, not something to strangle file-processor-by-file-processor.**

### 3.3 A legitimate "stop here" outcome

Because Phase 1 (WebDAV + streaming + NNTP stack in Rust) targets exactly the two quality scenarios
(QS-1 seek latency, QS-3 streaming-shouldn't-stall-behind-ingestion) where Rust's no-GC,
explicit-ownership properties matter most, and Queue/ingestion's own app-overhead is already
dominated by Usenet download time rather than app CPU (D5's finding), **a permanent hybrid — Rust
read/streaming service + .NET ingestion pipeline, forever — is a legitimate architectural end state,
not just a migration waypoint.** This should be evaluated on its own merits after Phase 1 ships,
rather than assumed to be a stepping stone to a full rewrite.

---

## 4. Concrete performance argument — precise, not oversold

The honest headline: **the single biggest classic "rewrite in Rust for speed" win — the yEnc decode
hot loop — is already banked today**, via `RapidYencSharp`/`rapidyenc` native SIMD code. Anyone
pitching this rewrite on "yEnc will be faster in Rust" is wrong; it won't be, because it's already
native C on both sides of this decision. The real argument has to be made elsewhere:

- **Container image size (real, bounded)**: today's single image bundles an ASP.NET runtime layer
  *and* a Node runtime (for the frontend) in one container (D34). A Rust backend removes the ASP.NET
  layer specifically — a static or near-static Rust binary (`musl` target) replacing
  `mcr.microsoft.com/dotnet/aspnet:10.0`-based layers. The Node/frontend layer is untouched and
  remains the larger of the two runtime layers in most such images **(hypothesis on relative size —
  no measurement taken)**. Real saving, bounded by "the smaller of the two runtimes goes away," not
  "the image gets thin."
- **Idle memory footprint (real, magnitude uncertain)**: .NET's Server GC reserves heap segments
  somewhat ahead of actual live-object size, and a Kestrel host has non-trivial baseline managed
  memory even idle. Rust's footprint is scoped essentially to what's actually allocated (no GC
  headroom reservation). Directionally real; exact MB delta is **(hypothesis)** — no profiling data
  exists for the current app's idle footprint (this repo has zero profiling infrastructure per every
  research pass's "no tests, no profiler runs" refrain).
- **No GC pauses (real mechanism, uncertain magnitude for *this* workload)**: this matters for tail
  latency (P99 seek time), not throughput. But — important honesty check — `usenet-streaming.md` §4
  explicitly flags that **nobody has profiled whether GC pressure is even a measurable problem
  today** ("Per-article/per-segment buffer allocations... haven't been profiled for GC pressure under
  several concurrent 20GB-file streams" — flagged as an open, unverified hypothesis, not a confirmed
  bottleneck). .NET's modern server GC (net10.0, concurrent/background collection) is genuinely good
  for I/O-bound workloads like this one. **The honest claim is "Rust removes a class of tail-latency
  risk that has not been shown to be a problem," not "Rust fixes a measured problem."**
- **Startup time (real, low-stakes)**: a Rust binary starts near-instantly vs. .NET JIT/ReadyToRun
  warmup. For a long-running homelab server (restarted on updates, not per-request), this is a real
  but low-frequency-impact win.
- **Structural correctness win, not raw speed (real, and probably the most defensible concrete
  claim in this whole section)**: §1.5 above — retiring the `CancellationTokenContext` static
  ambient-dictionary workaround (D24, an explicitly flagged fragility/leak risk) in favor of
  ownership-typed explicit parameter passing, and replacing the explicitly-ChatGPT-authored,
  zero-test `ConnectionPool`/`ConnectionLock` (flagged in `usenet-streaming.md` §4 as the connection
  lifecycle code every stream and queue download depends on, with no tests) with a mature pooling
  crate (`deadpool`/`bb8`) that has its own test coverage for exactly the concurrent-acquire/dispose
  races flagged as a risk today. This is a genuine reliability argument independent of raw
  performance, and arguably stronger than the memory/GC arguments above.

**What this section deliberately does not claim**: that Rust makes streaming *faster* in a way a
user would notice in isolation. The bottleneck in every runtime scenario traced in
`06-runtime-view.md` is Usenet network round-trips (connection acquisition, BODY commands, provider
round-trip time) — not application-language overhead. Both .NET and Rust are more than fast enough
at the actual CPU work involved (yEnc decode already native either way, AES-CBC decode backed by
OS-native crypto primitives either way per `usenet-streaming.md` §5's own note on `AesDecoderStream`).
The case for Rust here is container footprint + structural reliability wins on a fragile,
under-tested concurrency layer, not raw throughput.

---

## 5. Effort and risk estimate

For a solo/small-team open-source maintainer (not a funded team), rough estimates:

- **Phase 1 only (WebDAV + streaming + NNTP client stack in Rust, hybrid with existing .NET
  ingestion)**: **(hypothesis)** roughly 3–6 months of sustained part-time effort, dominated by (a)
  the MemoryPack-compatibility spike or internal-API design (§3.1), (b) hand-writing the NNTP
  protocol layer + connection pooling (bounded, but zero prior art to lean on), (c) manually
  behavior-testing every WebDAV verb/range-request edge case Sonarr/Radarr/rclone/media-players
  exercise today, given there is no existing test suite to diff against (a peer effort in this
  brainstorm is specifically proposing a characterization-test strategy to de-risk exactly this —
  strongly recommended as a prerequisite, not a parallel nice-to-have).
- **Full rewrite (Phase 1 + Queue/ingestion + REST API surfaces)**: **(hypothesis)** 12–18+ months
  part-time, given the deobfuscation/RAR-reconciliation heuristics represent years of accumulated
  real-world edge-case handling with zero automated regression coverage to validate a translation
  against.

**The single biggest risk to a project of this shape attempting this**: **not** the engineering
difficulty of any individual crate/framework choice above — it's **silent correctness regression in
the untested, heuristic-heavy deobfuscation and RAR/7z part-reconciliation logic** (§1.7, §2).
These heuristics were tuned reactively against real users' real obfuscated-release naming
conventions over years of upstream history, with no test suite anywhere to pin down "what does
correct even mean" before starting a translation. A subtly-wrong port doesn't crash or error — it
silently produces a wrong-but-plausible filename, a misordered RAR part, or a wrong byte range
that only surfaces as a garbled video or a mis-imported episode weeks later, reported by a user
against a release the maintainer has never seen. For a solo maintainer without a large user base to
absorb and report that class of bug quickly, this is the risk that turns "a rewrite took twice as
long as planned" into "a rewrite quietly eroded the thing the project is actually good at" — and
it's precisely why the characterization-test proposal from the peer testing-strategy agent should be
treated as a hard prerequisite to Phase 1, not an optional parallel workstream.

---

## 6. Effect on the fork's relationship with upstream (`nzbdav-dev`)

State this plainly, because §9.3's prior pass understated how binary this is: **a Rust rewrite of
any component currently tagged INHERITED in `09-architecture-decisions.md` permanently forfeits the
ability to `git merge`/cherry-pick upstream C# changes into that component, full stop.** This is not
a matter of degree — once `backend/Queue`, `backend/WebDav`, or `backend/Clients/Usenet` exists only
as Rust, there is no longer a shared C# file for `git cherry-pick` to apply upstream's future commits
onto. Concretely, given the fork-vs-upstream commit tallies already established in this document's
sibling research (`Queue/`: 79 upstream commits vs. 2 fork commits; `WebDav/`: 41 vs. 2;
`Clients/Usenet`-adjacent: majority upstream with fork-specific decorator additions), **rewriting any
of these means the fork takes on 100% of the maintenance burden for everything upstream would
otherwise have kept fixing for free** — every future upstream bugfix to a RAR-naming edge case, a
circuit-breaker tuning improvement, a new Sonarr/Radarr-compatibility quirk, a security fix to the
`DISABLE_WEBDAV_AUTH` bypass (D15, already flagged as needing reconsideration) — all of it would need
to be **manually re-discovered and re-implemented in Rust** by reading upstream's C# diffs and
translating them, forever, with no tooling assistance. This is a permanent, compounding cost, not a
one-time migration tax.

**Practical implication if pursued**: this should be treated as an explicit, stated decision to
permanently diverge from upstream's C# codebase as a strategic choice about the fork's future — not
a decision anyone should back into by accretion (the exact caution `09-architecture-decisions.md`
§9.3 already raised, and the one part of that prior pass's reasoning this document does not
walk back). If a hybrid architecture (§3.3) is the end state — Rust read path, .NET ingestion
pipeline — then only the Rust-rewritten components lose upstream mergeability; the ingestion
pipeline (the larger of the two by commit volume) would continue to receive upstream fixes normally.
This is a real argument in favor of the hybrid end state being evaluated as a destination in its own
right, not just a stepping stone: it bounds the upstream-divergence cost to exactly the components
being rewritten, rather than accepting it project-wide.

---

## Summary of recommendations, if this is pursued

1. **tokio** (uncontested), **axum** (judgment call, axum's tower/hyper alignment is a marginally
   better fit than actix-web here).
2. **sqlx** for any component that touches SQLite directly, if/when persistence itself moves — but
   see point 4, it may not need to move at all in Phase 1.
3. WebDAV: attempt `dav-server` **(verify)** first; fall back to a hand-rolled RFC-4918-subset
   layer on axum, scoped to only the verbs this project actually exercises.
4. **Start with a strangler-fig Phase 1**: Rust owns WebDAV protocol + NNTP client stack + stream
   composition; keep persistence and ingestion in .NET, with the Rust service either reverse-engineering
   the fixed-shape MemoryPack blob format (spike this first, before anything else) or — likely lower
   risk — calling back into the .NET process over a narrow internal API for DavItem/segment metadata.
5. Do not attempt Queue/ingestion in Rust until/unless a characterization-test suite exists to
   validate the translation of the deobfuscation/RAR-reconciliation heuristics — this is the
   single highest-risk piece of the entire proposal.
6. Treat "permanent hybrid, never finish the rewrite" as a legitimate, explicitly-evaluated end
   state, not a failure mode — it bounds both the effort and the upstream-divergence cost to the
   component (WebDAV/streaming) where Rust's actual technical advantages are most concrete.
