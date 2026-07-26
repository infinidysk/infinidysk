# Redesign Proposal: NzbDav Backend in Java + GraalVM Native Image

**Status**: Brainstorm input, not a decision. Companion to a parallel Rust rewrite proposal; both are
inputs to a human-synthesized architecture decision. Ground truth for the current system is
`docs/arc42/05-building-block-view.md`, `06-runtime-view.md`, `09-architecture-decisions.md` §9.3,
and the `_research/*.md` deep dives — this document assumes that context and cites back to it rather
than re-deriving it.

Every specific number in this document not backed by a citation to an existing measurement in this
repo (there are none — QS-4/QS-5 have no baseline, per `10-quality-requirements.md`) is tagged
**(hypothesis)**. Where I'm not confident a library or GraalVM feature works as described, I say so
explicitly rather than asserting it.

---

## 1. Target architecture

### 1.1 Framework: Quarkus, not Micronaut, not plain Spring Boot

**Recommendation: Quarkus.** All three have native-image stories; the differentiator is how much of
the native-image tax is already paid by the framework itself versus left for this project to
discover the hard way.

- **Quarkus** was designed native-image-first from day one (RedHat/IBM-backed, first released
  2019 specifically to answer GraalVM). Its extension model does reflection-config generation,
  build-time bean wiring (no runtime classpath scanning), and build-time resource/config
  substitution automatically for every first-party extension (RESTEasy Reactive, Hibernate
  ORM/Panache, Flyway, the Vert.x core it's built on). This matters concretely: the #1 native-image
  failure mode is a library doing runtime reflection or dynamic proxying that `native-image`'s
  static analysis can't see ahead-of-time-config for, and Quarkus extensions exist specifically to
  pre-solve that for the libraries in its ecosystem. Building a `dotnet publish`-equivalent
  (`quarkus build --native` or `-Dquarkus.native.enabled=true`) is a first-class, CI-tested path
  upstream, not an afterthought.
- **Micronaut** is architecturally similar (compile-time DI via annotation processing instead of
  runtime reflection, also native-image-first), and Micronaut Data in particular is arguably
  *more* native-image-friendly than Hibernate for simple schemas because it generates SQL at
  compile time with zero runtime reflection or proxying. It's a legitimate second choice. I'm
  picking Quarkus over it mainly for ecosystem breadth (more extensions, larger community, Red
  Hat's continued investment specifically in the GraalVM story) and because Quarkus's "run on
  virtual threads with plain blocking code" story (§1.1.3 below) is more mature and better
  documented today. This is a close call, not a landslide — if the fork maintainer already knows
  Micronaut better, that's a legitimate reason to pick it instead.
- **Plain Spring Boot** (Spring Boot 3+ with Spring AOT / native hints) does support native-image
  now, but it's retrofitted onto a 15+-year-old runtime-reflection-heavy framework (classpath
  scanning, dynamic proxies for `@Transactional`/AOP, lazy Hibernate proxies). Native builds are
  reported as slower and rougher than Quarkus/Micronaut's, and more of the "why won't this compile
  to native" debugging falls on the app author rather than being pre-solved upstream. Not
  recommended for a project with zero test suite to catch native-image-only failures.

**Native-image's real rough edges, stated plainly** (these apply regardless of which framework is
picked, and are the actual risk surface of this whole proposal):
- **Reflection**: anything using unregistered runtime reflection fails at native build time (best
  case) or silently misbehaves at runtime (worst case, e.g. a `ClassNotFoundException` on an app
  path only exercised in production, since there's no test suite to catch it in CI). Every
  third-party library pulled in (SQLite JDBC driver, an archive-parsing library, anything doing
  JSON/serialization via reflection) needs either built-in GraalVM reachability metadata, a
  Quarkus extension, or hand-written `reflect-config.json` entries discovered by trial and error.
- **Dynamic class loading / classpath scanning** (Class.forName with a computed string, dynamic
  proxies, most classic Java ORM/DI magic) doesn't work at all in native-image without static
  reachability metadata — this is precisely why Hibernate/Spring needed years of retrofit work and
  why compile-time-DI frameworks (Quarkus, Micronaut) exist.
- **Some libraries just don't support native-image at all** or only partially — this is the
  concrete reason §1.2/§1.4/§1.5 below spend real space on a per-library native-image
  compatibility assessment instead of just picking libraries by feature fit.
- **Build cost**: `native-image` compilation is CPU- and memory-hungry (whole-program static
  analysis) and meaningfully slower than a normal Java/Maven build — commonly **minutes, not
  seconds** (hypothesis: for an app this size, likely 2-8 minutes per native build, vs. Quarkus's
  own JVM-mode build being comparable to any other Java build). This affects the Docker build
  pipeline (`docs/arc42` D34/D38: this repo already builds Docker images on every branch push) —
  a native build adds real CI minutes to every push, unlike the current `dotnet publish`.

### 1.2 WebDAV server: recommend a thin hand-rolled layer over Quarkus/Vert.x routes, not Jackrabbit or Milton

This is the sharpest edge of the whole proposal, and worth being direct about: **neither mature
Java WebDAV library is a clean fit, and I don't recommend adopting either as the WebDAV
foundation.**

- **Apache Jackrabbit's WebDAV module** (`jackrabbit-webdav`) is real, production-used code (it
  backs the WebDAV layer of Jackrabbit's JCR content repository, itself used by things like Adobe
  AEM), but it's built around JCR semantics and servlet-API abstractions from the mid-2000s, uses
  JAXB/DOM4J for XML (a real reflection surface for native-image — JAXB in particular is
  notoriously reflection-heavy and has a poor native-image track record without substantial
  vendor-supplied reachability metadata, which Jackrabbit doesn't ship). Retrofitting it onto a
  `DavItem`-tree-backed-by-SQLite model (nothing like JCR) would mean fighting the library's own
  content-model assumptions as much as using them.
- **Milton (`io.milton`)** is a more modern, more directly WebDAV-shaped library (used commercially
  for CalDAV/CardDAV/WebDAV servers), with a cleaner `Resource`/`CollectionResource` interface that
  maps more naturally onto `DavItem`. It's the better of the two candidates. But it's still a
  general-purpose library carrying LOCK/PROPPATCH/ACL/versioning machinery NzbDav doesn't need, it
  has no published native-image compatibility statement or track record I'm aware of, and its
  commercial-support model (a free "Milton2" open-source core plus a paid enterprise tier) is worth
  the fork maintainer's own diligence before depending on it long-term.
- **The actually-relevant precedent is in this repo already**: the current C# backend uses
  `NWebDav.Server`, itself a fairly minimal library, and *still* has to replace its stock GET/HEAD
  handler entirely (`GetAndHeadHandlerPatch`, `backend/WebDav/Base/GetAndHeadHandlerPatch.cs`) to
  get correct range/seek behavior — because generic WebDAV libraries are built for filesystem-like
  backing stores, not a segment-fetching virtual stream. NzbDav's actual required WebDAV surface is
  narrow: `PROPFIND`, `GET`/`HEAD` with `Range`, `PUT` (watch-folder NZB drop), `MKCOL`, `DELETE`,
  `MOVE`/`COPY` (§5.2.4/§1.5 of `05-building-block-view.md`/`core-domain.md`) — no locking, no
  versioning, no ACL, no DeltaV.

**Recommendation**: implement a minimal WebDAV method dispatcher directly on Quarkus's Vert.x-based
HTTP layer (plain `@Route`/reactive routes, or RESTEasy Reactive with a custom method-not-standard
HTTP-verb workaround for `PROPFIND`/`MKCOL`/etc., which aren't in the standard JAX-RS verb set and
need router-level handling either way). This is *more* work up front (there's no library doing
XML PROPFIND-response generation for you) but it avoids adopting a large reflection-heavy
dependency of uncertain native-image status for a surface this narrow, and it mirrors what the
current C# implementation already effectively does (a generic library, immediately special-cased).
Concretely: hand-write PROPFIND multistatus XML generation (a well-documented, stable wire format;
low ongoing maintenance once written) and hand-roll the range-request handler (`Content-Range`,
206/200 status, `Stream`-equivalent seek) directly against `DavItem`, exactly mirroring
`GetAndHeadHandlerPatch`'s existing logic since that logic already had to be purpose-built once.

### 1.3 SQLite + migrations: recommend jOOQ + Flyway over Hibernate/Panache

- **SQLite driver**: `org.xerial:sqlite-jdbc` is the standard choice — it's a JNI wrapper shipping
  precompiled native `.so`/`.dll` per platform, extracted from the jar at runtime. This is itself a
  **native-image risk worth flagging directly**: native-image needs to know to bundle/extract that
  native library and JNI needs to resolve correctly inside a statically-linked native binary.
  Community reports of getting `sqlite-jdbc` working under `native-image` exist, but it is not an
  official, vendor-tested "just works" path the way, say, the Postgres JDBC driver's reflection
  metadata is maintained by Quarkus. **This is a concrete spike item, not an assumption** — verify
  `sqlite-jdbc` under a real native-image build early, before committing further design around it.
  A fallback if it doesn't work cleanly: `org.xerial`'s alternative pure-Java SQLite
  implementations are far less mature/performant; the more realistic fallback would be bundling a
  small custom JNI shim around SQLite's C amalgamation directly (SQLite ships as ~1 C file — this
  is a small, very well-trodden native-image pattern, and NzbDav would be linking a battle-tested
  C library either way).
- **ORM/query layer**: recommend **jOOQ** (with generated typesafe SQL DSL classes from the
  schema) over **Hibernate ORM with Panache** (Quarkus's flagship ORM story). This is a deliberate
  choice against the "obvious" Quarkus-native answer, for reasons specific to this codebase:
  - Hibernate's native-image support is real and Quarkus has invested heavily in it (build-time
    bytecode enhancement instead of runtime, static metamodel generation) — it's not a bad choice
    mechanically.
  - But this schema **already went out of its way to avoid ORM magic**: the blob-store migration
    (D2/D3 in `09-architecture-decisions.md`, the single most consequential finding of the
    core-domain research pass) deliberately moved per-segment metadata *out* of EF Core's JSON
    columns and into flat compressed files specifically to keep SQLite/EF overhead small and
    predictable. Re-introducing a full object-relational mapper's lazy-loading/proxy/dirty-checking
    machinery on top of a schema this simple, on a rewrite target that already has zero regression
    tests, adds a whole category of "why did this query behave differently than the raw SQL I
    expected" risk for no clear benefit — the actual queries here (`(ParentId, Name)` lookups,
    `GetTopQueueItem`'s priority-ordered scan, one big `SaveChangesAsync` per queue item) are
    simple enough that hand-written SQL via jOOQ's typesafe builder is not meaningfully more work
    than mapping them through an ORM, and is far easier to reason about byte-for-byte against the
    existing EF Core LINQ queries when porting for parity.
  - jOOQ's code-generation model (SQL schema → generated Java classes at build time) has
    essentially no runtime reflection surface, which is the safest possible position to be in for
    native-image — there's very little to get wrong.
  - **Migrations**: Flyway (plain versioned `.sql` files) instead of Hibernate's schema-generation
    tooling — this is a closer structural match to the 30+ existing EF Core migrations (each one
    is, at bottom, a set of SQL DDL statements) and makes translating them mechanical: one Flyway
    `.sql` file per existing EF Core migration, in order, rather than reverse-engineering an
    entity-model diff. Flyway has broad native-image precedent through Quarkus's own Flyway
    extension.

### 1.4 The NNTP client stack: protocol, concurrency, and the FFI question

**The yEnc decode FFI question is the single biggest open technical risk in this entire proposal,
and I want to be honest that I do not have high confidence in the answer.**

- **What's not in question**: `rapidyenc` is a small, native C library (SIMD SSE2/AVX2/NEON yEnc
  encode/decode) that NzbDav already depends on indirectly via `RapidYencSharp`
  (`docs/arc42/_research/usenet-streaming.md` §0). A Java rewrite would call the *same* C library
  directly — there's no need to reimplement yEnc decode in Java, and doing so would be a strictly
  worse idea than calling the existing native code (this mirrors the C# proposal's own correct
  framing: the hot path is already native/SIMD, so a language rewrite banks nothing new here unless
  it can call that same C code cleanly).
- **JNI**: long-established, well-supported under GraalVM native-image — this is genuinely
  low-risk. `rapidyenc`'s public C API is small (init/feed/decode-style calls per the existing
  .NET binding's shape), so a hand-written JNI shim (a few hundred lines of glue: `javac -h` header
  generation, a thin `.c` bridge file, linked at native-image build time) is a contained,
  well-understood task. **Recommend JNI as the primary, safe path.**
- **Panama / the Foreign Function & Memory API** (stable since JDK 22) would let Java call
  `rapidyenc` without writing any C glue at all — a real ergonomic win over JNI if it works cleanly
  under `native-image`. GraalVM has been actively adding FFM support in native-image, and I believe
  meaningful support exists in recent GraalVM releases, but I do **not** have confident, current
  knowledge of exactly how complete/stable that support is at whatever GraalVM version would be
  targeted, and I'm flagging that explicitly rather than asserting it works. **Treat this as a
  1-2 day spike before committing**: write a minimal FFM downcall to a trivial native function under
  `native-image`, on the specific GraalVM version being targeted, before designing the rest of the
  NNTP layer around it. If the spike fails or is flaky, fall back to JNI — the shim work is small
  either way given rapidyenc's narrow API surface.
- **JNA/JNR-FFI are explicitly not recommended** — both rely on dynamic proxying/reflection deep in
  their implementation, which is exactly the pattern native-image struggles with; there are
  long-standing community reports of JNA being difficult-to-impossible under native-image without
  significant extra configuration. Prefer hand-written JNI over either.

**Concurrency model — recommend virtual threads with blocking-style code over a Vert.x-reactive
rewrite.** This is a deliberate choice against Quarkus's own most idiomatic native model, and it's
the single highest-leverage decision for keeping this rewrite's *translation* risk (not just its
*language* risk) bounded:
- Quarkus is built on Vert.x, and the framework's most idiomatic native-image path is fully
  reactive (Mutiny `Uni`/`Multi`, non-blocking event-loop code). This is a legitimate, very mature
  choice for I/O-bound workloads like this one — but it is a large *structural* rewrite of the
  existing decorator-stack pattern (`docs/arc42/_research/usenet-streaming.md` §1), which today is
  built from linear `async`/`await` `Task`-returning methods layered as decorators
  (`WrappingNntpClient` base, `MultiConnectionNntpClient` → `ConnectionPool` → etc.). Translating
  that directly into reactive combinator chains (`.chain()`, `.onFailure().retry()`, callback-style
  composition) is a genuine redesign, not a mechanical port, and is exactly the kind of thing that's
  hardest to get right with zero tests to catch a subtly-wrong retry/cancellation-propagation
  translation.
- **Virtual threads (Project Loom, stable since JDK 21, and supported under GraalVM native-image
  since around GraalVM for JDK 21/22)** let you write plain, synchronous, blocking-style Java —
  blocking socket reads, blocking JDBC calls, a normal `try`/`finally`/`Thread.sleep` — while still
  getting massive concurrency, because each virtual thread is cheap and the runtime parks it during
  blocking I/O instead of pinning an OS thread. Quarkus explicitly supports this today via
  `@RunOnVirtualThread`. Structurally, one blocking virtual-thread-per-request method maps much
  more directly onto the existing C# `async Task<T>` decorator methods (rename `await X()` calls to
  plain blocking calls, keep the same layering, same `try`/`finally`/`using`-equivalent structure)
  than a reactive rewrite does — lower translation risk for a solo maintainer with no test suite to
  catch behavioral drift.
- The tradeoff: virtual threads under native-image are a newer feature than JNI/reflection basics,
  and — like the FFM question above — deserve a small early spike to confirm they behave as
  expected (in particular: that blocking I/O inside a virtual thread correctly parks rather than
  pinning the carrier thread, which historically had edge cases around synchronized blocks and some
  native calls). Given the choice is "moderately-new-but-structurally-simpler" vs.
  "well-established-but-structurally-riskier," I lean toward virtual threads given the zero-test-
  suite constraint, but this is closer than the JNI-vs-Panama call above.
- Either way, the actual protocol layer (connect/AUTHINFO/STAT/HEAD/BODY/ARTICLE, per-provider
  connection pooling, multi-provider failover, circuit breaking) has no Java library candidate any
  more than C# did (the usenet-streaming research explicitly found no full-stack .NET alternative
  either) — this is hand-written regardless of language, just as it is today. The decorator-stack
  *shape* (each concern as a composable wrapper implementing one client interface) translates
  cleanly to Java interfaces/composition; nothing about that pattern is C#-specific.

### 1.5 RAR/7z/PAR2 parsing

- **7z**: **Apache Commons Compress** (`org.apache.commons:commons-compress`) has a mature
  `SevenZFile` reader, is pure Java (no native dependency), widely used, and — critically — NzbDav
  only ever needs **uncompressed (store-mode) 7z** support today (`SevenZipProcessor` hard-rejects
  anything else, `docs/arc42/_research/core-domain.md` §1.1). That's the simplest possible case for
  any 7z reader to support, which meaningfully de-risks this specific library choice. Commons
  Compress has no obvious heavy reflection surface I'm aware of and is a strong candidate to be
  native-image-clean, though — consistent with this whole document's honesty standard — that should
  still be spiked/verified rather than assumed.
- **RAR**: this is a genuine gap, and worth stating plainly rather than papering over. `junrar` is
  the closest existing Java RAR library, but it's aging/lightly maintained and I'm not confident it
  exposes the specific capability NzbDav actually needs: parsing RAR volume headers to get
  byte-offsets of files *inside a still-downloading, not-yet-complete archive* (`RarProcessor`,
  `RarUtil.GetRarHeadersAsync` today), not extracting from a complete local file, which is the
  use case most RAR libraries (including junrar) are built around. Practically, this likely means
  hand-writing a minimal RAR4/RAR5 header parser in Java — which is a smaller lift than it sounds,
  because NzbDav's own `RarAggregator` already does substantial custom reconciliation logic on top
  of whatever headers SharpCompress hands it (part-number-vs-filename delta reconciliation,
  volume-consistency validation, `core-domain.md` §1.1/§3) — meaning a meaningful fraction of the
  "RAR support" needed here was always going to be custom application logic regardless of which
  library parses the raw headers. This is real, non-mechanical effort either way — flag it as a
  concrete unknown-sized work item, not a solved problem.
- **PAR2**: no library needed in either language — the current implementation is ~3 commits of
  packet-header framing plus one packet type (`Par2Recovery/Par2.cs`, `core-domain.md` §1.3, §5.2.3
  of `05-building-block-view.md`). This ports near-mechanically: it's simple binary parsing over a
  fixed format, translatable in well under a day regardless of language.

### 1.6 Queue pipeline / deobfuscation / aggregation business logic — translation estimate

This is the largest single body of code (`backend/Queue/**`, 79+ commits, `core-domain.md`
"Fork status recap") and the least mechanical to port, because it's not protocol/IO code — it's
accumulated heuristics tuned against real-world obfuscated-release naming conventions with no
formal spec: filename-priority scoring across three name sources (`GetFileInfosStep`), size-
tolerance-band cross-validation (95-100%), RAR part-number delta reconciliation, the specific order
of post-processors, the three-way retry/failure classification. None of this is Java-specific or
C#-specific — it's pure business logic that has to be read, understood, and faithfully
re-implemented statement-by-statement, then validated against real obfuscated releases by hand
since there's no automated fixture set. **(hypothesis, effort estimate)**: this is plausibly
40-60% of the total rewrite effort by engineering time, independent of which target language is
chosen — it's the same-sized problem for Java or Rust. Nothing about Java specifically makes this
easier or harder than the current C# is; the risk here is the absence of tests, not the language.

---

## 2. What ports mechanically vs. needs redesign

**Mechanical (low design risk, translation-dominated)**:
- PAR2 packet parsing (§1.5).
- NZB XML parsing (`NzbDocument.LoadAsync` — Java's `javax.xml.stream.XMLStreamReader` is a direct
  streaming-XML-reader equivalent, no design change).
- The decorator-stack *shape* for the Usenet client (interfaces + composition translate directly;
  see §1.4's caveat that the *async model* underneath needs a real decision, not the shape itself).
- SQL schema and migrations (Flyway `.sql` files map ~1:1 from EF Core migrations' DDL).
- The PROPFIND/range-GET WebDAV surface, once hand-rolled once (§1.2) — narrow, stable wire format.
- Blob store (flat zstd+serialized files, GUID-keyed, sharded directory layout) — this is a
  filesystem convention, not a framework dependency; Java has zstd bindings (`com.github.luben:
  zstd-jni`, itself JNI-based — same native-image caveat class as sqlite-jdbc, spike it) and a
  choice of binary serialization (Protobuf, or a hand-rolled format matching MemoryPack's
  simplicity) to replace MemoryPack.

**Needs real redesign, not just translation**:
- The async/concurrency model for the Usenet stack (§1.4) — blocking-virtual-threads vs. reactive
  is a first-class design decision, not a mechanical port either way.
- The WebDAV layer's foundation (§1.2) — there's no drop-in equivalent to NWebDav to wrap; this is
  new design work, informed by (but not copied from) the existing `GetAndHeadHandlerPatch`
  approach.
- The FFI boundary to `rapidyenc` (§1.4) — genuinely new code, gated on a spike.
- Anything Hibernate/EF-Core-idiomatic in the current code that assumes an ORM's change-tracking
  (`MarkQueueItemCompleted`'s single `SaveChangesAsync` closure, `core-domain.md` §1.1) needs
  restating as explicit transaction boundaries under jOOQ (a `DSLContext.transaction { ... }`
  block) — mechanically similar in spirit, but every implicit EF change-tracker behavior needs to
  become an explicit statement.

---

## 3. Migration strategy: big-bang-at-cutover, not a live in-process strangler-fig

A classic strangler-fig pattern (route some paths to the new service, others to the old, shrink the
old service's surface over time) is awkward here for a structural reason specific to this app: **the
sole datastore is a single SQLite file, single-writer by design (D1, `09-architecture-decisions.md`)**.
Running the existing C# backend and a new Java backend as two live, simultaneously-writing processes
against the same SQLite file is a correctness risk, not just an operational inconvenience — EF Core
and jOOQ would both need to agree on migration ownership, WAL-mode concurrent-writer semantics, and
neither codebase's transaction boundaries were designed with a concurrent second writer in mind.
Splitting by *route* (e.g., WebDAV reads on the new backend, Queue writes on the old) doesn't avoid
this either, since WebDAV reads still touch the same SQLite file the Queue writes to.

**Recommended shape instead**:
1. Build the full Java backend as an independent binary that reads/writes the **same** SQLite
   schema and blob-store layout as the C# backend (schema-compatible by construction, via the
   Flyway-from-EF-migrations port in §1.3) — but run it only against a copied/staging `/config`
   directory during development, never pointed at a live instance sharing the C# backend's file.
2. Before any cutover, build the missing regression safety net this project has never had, for
   *both* implementations at once: record a fixture set of real (or synthetic, obfuscated-in-the-
   same-style) NZBs plus their expected `DavItem` tree/aggregation output, and a fixture set of
   range-read requests plus expected byte output. This directly addresses the same "zero test
   suite" risk `09-architecture-decisions.md` §9.3 already flagged as the single most expensive
   thing about any language rewrite — building it once, usable to validate parity for whichever
   rewrite (Java or Rust) is chosen, is worth doing regardless of which proposal is picked.
3. Validate the Java backend against that fixture set until parity is demonstrated, then cut over
   the Docker image's backend process **as a single deployment event** (big-bang at the
   container/binary level), keeping the C# build path alive on a branch for a defined rollback
   window rather than deleting it immediately.
4. This is not a "no incremental value" plan — steps 1-2 can proceed subsystem-by-subsystem
   (Usenet client stack first, since it's the most self-contained and highest-value to validate
   early per the yEnc-FFI spike in §1.4; WebDAV/Queue next) even though the final cutover itself is
   a single event rather than a gradual traffic shift.

---

## 4. Performance argument

**The framing that matters most here, stated up front**: the current backend is **.NET, not a slow
interpreted runtime** — ASP.NET Core already starts in low hundreds of milliseconds and has a
reasonably disciplined memory footprint. This changes the shape of the performance argument
compared to a hypothetical "Java vs. a scripting language" pitch: **Java+GraalVM native-image's
realistic goal here is closing the gap back to parity with what .NET already delivers**, not
unlocking large new headroom .NET couldn't reach. That's a materially more modest pitch than what
a from-scratch-native language (Rust) can honestly claim (§6), and it should be evaluated as such.

- **Startup time**: GraalVM native-image apps commonly start in the tens of milliseconds for simple
  services, vs. a *plain* (non-native) JVM's hundreds of milliseconds to seconds of class-loading
  and JIT warm-up (hypothesis — Quarkus's own published figures for simple REST services are in
  this range, but not measured against this specific codebase's actual dependency graph, which
  includes a DB connection pool, HTTP server, and eventually a hand-rolled WebDAV layer — all of
  which add real, if small, native-image startup cost beyond a "hello world" benchmark). Directly
  relevant to **QS-5** (startup/recovery) — but since the *current* .NET backend's startup time has
  never been measured in this repo either (no QS-5 baseline exists per §10.2), the honest framing
  is "native-image should be competitive with or modestly better than the current .NET startup,"
  not "eliminates a slow-startup problem," because there's no evidence the current startup is
  actually a problem worth solving.
- **Memory footprint**: native-image binaries typically report dramatically lower idle RSS than a
  running (non-native) JVM — commonly cited as closer to Go/Rust territory than to a warmed-up JVM
  (hypothesis, same caveat: no measurement exists against this codebase, and no baseline exists for
  the *current* .NET backend's footprint either — `10-quality-requirements.md` QS-4 explicitly has
  "Target TBD, pending measurement," a placeholder, not a validated number). This directly targets
  **QS-4**, but again: the honest comparison is Java-native-image vs. **.NET's current footprint**,
  which is plausibly already reasonably small for a single-container homelab target, not vs. a
  hypothetical bloated JVM baseline that isn't actually this project's status quo.
- **Peak throughput / warmed-JIT tradeoff, stated honestly**: `native-image` compiles ahead-of-time
  and forgoes some of a running JVM's later-stage profile-guided JIT optimizations (HotSpot's C2
  tier), so for a long-running, steady-state, hot-loop-heavy workload, a fully warmed JVM can
  outperform a native-image binary at sustained peak throughput (hypothesis — this is a
  well-documented general characteristic of AOT vs. JIT compilation, not something specific to this
  codebase). GraalVM has been closing this gap via **PGO (profile-guided optimization for
  native-image)**, using representative training runs at build time — I believe this capability has
  moved from an Oracle-GraalVM-only feature toward broader availability in unified GraalVM
  distributions in recent releases, but I'm **not fully certain of current licensing/availability
  status** for whichever GraalVM edition/version would actually be targeted — verify this before
  relying on PGO as part of the performance case. For NzbDav's actual workload (I/O-bound NNTP
  streaming, not CPU-bound hot loops — the one genuinely CPU-heavy piece, yEnc decode, is already
  native C via `rapidyenc` regardless of host language per §1.4) this JIT-vs-AOT peak-throughput gap
  is plausibly less consequential than it would be for a CPU-bound service, since the hot loop isn't
  running in managed code either way.
- **Build cost, honestly stated as a real cost, not just a footnote**: native-image compilation is
  materially slower than a normal build (§1.1) — this is a genuine, ongoing tax paid on every CI run
  and every local iteration cycle, not a one-time migration cost. For a solo/small-team maintainer
  doing frequent iterative changes, this is a real day-to-day developer-experience regression
  relative to both the current fast `dotnet build` inner loop and a hypothetical Rust
  `cargo build` incremental loop (native-image's whole-program analysis doesn't have as mature an
  incremental-rebuild story as either).

---

## 5. Effort and risk estimate

**Effort**: large — realistically several months of solo/small-team engineering time, not weeks.
The floor is set by §1.6 (business-logic translation, ~40-60% of effort, hypothesis) plus the two
genuinely-new subsystems this proposal requires building from scratch that don't exist as reusable
libraries in Java any more than they did in C# (a hand-rolled WebDAV layer, §1.2; the NNTP protocol/
pooling/failover/circuit-breaker stack, §1.4) plus the FFI bridge to `rapidyenc` (§1.4) plus a
RAR header parser of uncertain-but-real size (§1.5) — this is not a "swap frameworks, keep the
logic" rewrite, it's closer to a ground-up reimplementation informed by reading the existing
C# code as a spec.

**Biggest single risk**: **silent byte-level content-correctness regressions in the
deobfuscation/aggregation heuristics, with no test suite to catch them**, compounded specifically
by native-image's own failure mode of *working differently in native mode than in a JVM dev-mode
smoke test* — i.e., there are two independent ways for this rewrite to look fine during development
and then misbehave in production: (a) a heuristic translated slightly wrong (the generic
cross-language rewrite risk `09-architecture-decisions.md` §9.3 already names as this project's
single most expensive risk category), and (b) a reflection/dynamic-dispatch edge case that only
manifests once compiled to a native binary, which a maintainer might not even think to re-test after
verifying correctness in ordinary JVM mode during day-to-day development. **This compounding is
specific to the GraalVM native-image path** and doesn't apply to the same degree to a Rust rewrite
(which has no "does this behave differently once compiled" split — a Rust binary behaves the same
whether you build it for dev or release, modulo debug-assertions). Recommend the fixture-based
regression suite from §3 be built and run **against the actual native-image binary**, not just in
JVM dev mode, specifically because of this failure class.

---

## 6. Direct comparison to a Rust rewrite

Where **Java+GraalVM is plausibly the safer choice**:
- **Ecosystem maturity as a fallback net, even where imperfect**: Jackrabbit and Milton exist as
  real, if imperfect, WebDAV libraries with production usage history (§1.2) — Rust's WebDAV crate
  ecosystem (e.g. `webdav-handler`) is smaller and less battle-tested; my honest expectation is Rust
  and Java land in a similar place here (both likely end up hand-rolling on top of a generic HTTP
  layer, `hyper`/`axum` vs. Vert.x), but Java at least has a "adopt an imperfect but real library"
  escape hatch that Rust's ecosystem doesn't offer as strongly.
- **7z/RAR parsing**: Apache Commons Compress is a genuinely mature, widely-used pure-Java library
  for the store-mode-only 7z case NzbDav needs (§1.5) — I'd expect this to be a cleaner win than
  whatever Rust crate the peer proposal picked for 7z, purely on ecosystem-maturity grounds (Commons
  Compress has a long production track record; Rust archive crates are generally younger and less
  battle-tested for edge-case-heavy real-world archive formats). RAR is a wash — likely hand-rolled
  in both ecosystems either way.
- **Contributor pool / hiring for an OSS project**: Java has a vastly larger pool of engineers who
  could plausibly review or contribute to this codebase than Rust does, and mature, widely-known
  tooling (debuggers, profilers, IDEs with excellent Java support) — a real advantage for an
  open-source project hoping for outside contributions, though tempered by the observation that a
  meaningful slice of the self-hosted/homelab-tooling community has been gravitating toward Go/Rust
  specifically for native-binary, low-footprint distribution — so the *stylistically* closest peer
  projects to NzbDav's actual audience may skew Rust/Go-friendly even if the absolute talent pool
  skews Java.
- **ORM/persistence tooling breadth**: Java's SQL/persistence ecosystem (jOOQ, Flyway, Hibernate,
  Micronaut Data) is broader and more mature than Rust's (`sqlx`, `diesel`, `sea-orm` are all solid
  but younger, with less of a "boring, extremely well-trodden" feel than jOOQ+Flyway).

Where **Rust plausibly wins outright**:
- **No FFI/native-image-compatibility tax, at all**: this is the sharpest, most structural
  difference between the two proposals. Rust compiles to native code directly — calling
  `rapidyenc`'s C API is a completely standard, low-risk `extern "C"` FFI binding with no separate
  "does this work under a subsequent AOT-compilation pass" question, because there is no subsequent
  pass. The single biggest open technical risk in this entire Java proposal (§1.4's
  Panama/JNI-under-native-image question, and to a lesser extent sqlite-jdbc/zstd-jni's own
  native-image behavior, §1.3/§2) simply doesn't exist for Rust. This is a genuine, structural
  advantage, not a close call.
- **Tighter memory control**: no garbage collector at all (vs. GraalVM native-image, which still
  runs a GC — a different, often more predictable one than HotSpot's default, but still a GC) —
  relevant to QS-4's footprint goal and to avoiding GC-pause-related latency spikes on the streaming
  hot path (a scenario the usenet-streaming research already flagged as unprofiled even in the
  current C# implementation, `usenet-streaming.md` §4 QS-4 item on GC pressure — a concern that
  transfers to a JVM/native-image target but is structurally absent in Rust).
- **Async ecosystem maturity for this exact workload**: `tokio` is an extremely mature,
  purpose-built async runtime for I/O-bound concurrent workloads like this one, arguably more
  battle-tested for this specific shape of problem (many-connection, many-provider, priority-aware
  I/O multiplexing) than either Quarkus/Vert.x's reactive model or the virtual-threads path I'm
  recommending in §1.4 — though virtual threads' whole selling point is sidestepping needing
  tokio-style async ceremony in the first place, so this is less of a gap than it would be if I were
  recommending full Vert.x-reactive Java instead.
- **Build/dev-loop speed**: Rust's `cargo build` incremental compilation, while not fast in absolute
  terms, doesn't carry native-image's whole-program-analysis native-build tax on every native
  artifact — every Rust build already produces the deployable native binary, there's no separate
  "now AOT-compile the AOT-compiled-enough JVM app" step the way there is going from Quarkus
  JVM-mode to Quarkus native-image mode.

**Bottom line on the comparison**: Java+GraalVM's case rests on "we get most of what a from-scratch
native language offers, while staying in a more familiar, more mature general ecosystem" — which is
true, but every one of the specific mechanisms that closes that gap (native-image's reflection
handling, FFM/JNI-under-native-image, PGO availability, sqlite-jdbc/zstd-jni native-image behavior)
is a real, unverified technical dependency that Rust simply doesn't need to have opinions about,
because Rust *is* native, full stop. That asymmetry is the single most important thing for the human
synthesizer to weigh — Java+GraalVM is not a bad proposal, but it is a proposal with meaningfully
more open technical risk *specific to the native-image compilation step itself*, layered on top of
the risk both proposals share (translating undertested business logic to a new language with zero
regression coverage, §5).

---

## 7. Effect on the fork's relationship with upstream

Same structural cost as any language rewrite, stated in `09-architecture-decisions.md` §9.3 and
worth restating precisely rather than softening: **this forfeits the ability to pull `nzbdav-dev`
upstream's ongoing C# fixes/features entirely, permanently, for every module touched.** Given
`backend/Queue`, `backend/Database`, `backend/WebDav`, and `backend/Par2Recovery` are collectively
96%+ INHERITED (§9.3's own figure) — meaning nearly all of the ongoing upstream commit activity in
this repo's history lands in exactly the code this proposal replaces — a Java rewrite doesn't just
lose *future* mergeability, it means every upstream bugfix or feature landing in those folders from
this point forward would need to be manually re-read and re-translated into the Java codebase by
hand, with no tooling (not even `git cherry-pick`-with-conflicts, since the files won't correspond
line-for-line, or file-for-file, at all) to make that cheaper. This is identical in kind to the cost
already priced into the existing "no, not currently" recommendation against a Go/Rust/Node rewrite
in §9.3 — this proposal doesn't change that calculus, it's simply evaluating one candidate language
within a decision the project owner has explicitly reopened ("Want Java with GraalVM for better
performance? DO IT!"). If the fork maintainer has already decided to permanently diverge from
upstream regardless of language — which is the precondition §9.3 itself names for revisiting this
question — then this cost is already sunk by that decision and shouldn't be re-litigated per
language choice; if that decision hasn't actually been made yet, it's the one this whole document
implicitly assumes as a given rather than argues for.
