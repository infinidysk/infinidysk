# Testing Strategy for NzbDav — De-risking Any Rewrite (and Independent of It)

**Purpose of this document**: three parallel proposals are being evaluated — Rust backend rewrite,
Java+GraalVM backend rewrite, htmx+Web Components frontend rewrite. All three run into the same wall:
per `02-constraints.md`, "there is no backend test project and no frontend test suite... `npm run
typecheck` is the only automated gate, and it isn't CI-enforced." Without a regression oracle, "did the
rewrite preserve behavior" is a matter of faith, not verification. This document proposes a concrete,
prioritized testing strategy that (a) has standalone value against the current .NET/React codebase
regardless of whether any rewrite happens, and (b) produces the specific artifact a rewrite needs to be
validated against.

This is a strategy document only — no test code is written here.

---

## 1. The golden-master / characterization-test approach

### 1.1 Core idea

Before any rewrite starts, build a **corpus-driven contract test suite** against the *current* .NET
implementation. For a representative set of synthetic NZBs, run them through the real queue pipeline
end-to-end and record two things per resulting output file:

1. **The `DavItem` tree shape** — path, `Type`/`SubType`, byte length, and (for multi-file archives)
   the full set of resulting file paths and their sizes. This is the "did we produce the right files in
   the right places" oracle.
2. **Byte-for-byte content of several streamed byte ranges per file**, captured via the actual WebDAV
   GET/Range path (`GetAndHeadHandlerPatch`) — not just "the aggregator's internal metadata," but what a
   real client would receive on the wire. At minimum, per file: bytes `[0, 64KB)`, the last `64KB` of the
   file, and **at least one range starting at a non-zero, non-block-aligned offset in the middle of the
   file** (the exact case that exercises `NzbFileStream.Seek` → `InterpolationSearch` →
   `MultiSegmentStream`/`DavMultipartFileStream`, per `usenet-streaming.md` §2b — this is the single most
   fragile and important code path to characterize, since it composes RAR/7z/AES/multipart streams and a
   bug there produces silently-wrong bytes, not a crash).

The recorded corpus (tree shape + byte-range hashes, or the ranges themselves for small test files)
becomes the **acceptance test any Rust/Java/htmx rewrite must reproduce exactly**. This is strictly more
valuable to a rewrite effort than hand-written new tests against the rewrite's own code, because it's
derived from *actual current behavior*, including whatever undocumented quirks that behavior has (e.g.
the `RarAggregator`'s "rename sole extracted file to match release folder name" UX behavior noted in
`core-domain.md` §1.1 — a new implementation could easily "fix" this as an oversight and silently break
existing users' expectations without a golden-master test catching it).

### 1.2 Corpus composition

Cover each container/processing path currently in `backend/Queue/FileProcessors/` +
`FileAggregators/`, since each is a distinct code path with its own reconciliation logic:

| Corpus case | Exercises | Priority |
|---|---|---|
| Plain file (no archive), single segment | `FileProcessor`/`FileAggregator`, the trivial baseline | Must-have |
| Plain file, multi-segment (forces `MultiSegmentStream` across ≥3 segments) | Segment stitching, interpolation-search seek with >1 probe | Must-have |
| RAR multi-volume (`.part01.rar` … `.partNN.rar`), unencrypted | `RarProcessor`, `RarAggregator.ValidateVolumes`/part-number reconciliation | Must-have |
| RAR multi-volume, password-protected | Above + `AesDecoderStream` | Must-have |
| RAR single-volume, obfuscated filename (exercises the "rename to release folder name" behavior) | The specific UX quirk in `RarAggregator.cs:52` | Must-have |
| 7z store-mode (uncompressed), multi-part (`.7z.001…`) | `SevenZipProcessor`, its `InterpolationSearch`-based entry-to-multipart-byte-range mapping | Must-have |
| 7z compressed or solid+encrypted | Confirms the rewrite also **hard-rejects** this the same way (`Unsupported7zCompressionMethodException`) — an easy thing for a rewrite to "helpfully" support differently | Should-have |
| Multipart raw `.mkv.001`/`.mkv.002`… (no container) | `MultipartMkvProcessor`/`Aggregator`, pure concatenation | Must-have |
| Obfuscated release (garbage filenames/subject lines, real names only recoverable via PAR2 descriptor) | `FetchFirstSegmentsStep` → `GetPar2FileDescriptorsStep` → `GetFileInfosStep`'s priority/tolerance reconciliation — flagged in `core-domain.md` as the most fragile heuristic in the domain | Must-have |
| Release with a missing/corrupt article (mid-file and last-file) | Retry/failure classification, `IsRetryableDownloadException`, health-check-adjacent "missing article" path | Should-have |
| Release with sample files + blocklisted filename patterns | `SampleFilePostProcessor`, `BlocklistedFilePostProcessor`, `EnsureImportableVideoValidator` | Should-have |
| Duplicate NZB / duplicate filename collision | `RenameDuplicatesPostProcessor`, `duplicate_nzb_behavior` config paths | Should-have |

### 1.3 Where the corpus comes from — avoid real copyrighted-release-shaped data

Do **not** use real scene-release NZBs, even old/public-domain-adjacent ones — release naming
conventions, PAR2 files, and obfuscation patterns are closely tied to actual copyrighted content
distribution, and shipping such files (even empty/junk payloads dressed up in real release-style
naming) in an open-source test fixtures directory is a bad look at best and a DMCA-bait fixture at
worst.

Instead, **synthesize test NZBs with real container/archive structure but throwaway content**:

1. Generate small (KB-scale) files with deterministic, license-free content — e.g. a short public-domain
   text file repeated to pad size, or simple generated PCM/PNG test patterns. Content should be
   *distinctive per byte range* (e.g. include the byte offset in the payload) so a corrupted-byte-range
   bug is immediately visible in a diff rather than masked by repetition.
2. Build real RAR/7z archives from that content using actual `rar`/`7z` CLI tools (multi-volume, with
   and without password) — this produces bit-identical real archive format headers, just with harmless
   payload. Split raw `.mkv.NNN` parts with plain byte-range concatenation of test content.
3. Generate real PAR2 index files with a PAR2 tool (e.g. `par2cmdline`) against the synthetic content —
   this exercises the actual `FileDesc` packet parsing in `Par2Recovery` against genuinely-produced PAR2
   files, not hand-rolled fixtures that might not match real encoder quirks.
4. Give files intentionally obfuscated/garbage names (random hex, no meaningful extension in the subject
   line) to exercise the deobfuscation path, since obfuscated naming itself carries no copyright
   implication — it's a structural/naming property, not content.
5. Package as `.nzb` XML pointing at a **local mock NNTP server** (see §3) that serves the synthesized
   article bodies — the corpus never needs to touch a real Usenet provider or the public internet at
   all, which also makes the golden-master suite fully offline/deterministic/CI-runnable.

This gives 100% structural fidelity (real RAR/7z/PAR2 binary formats, real obfuscation patterns) with
zero copyrighted content and zero legal ambiguity. Effort to build this generator is one-time; the
corpus can grow incrementally as new edge cases are found in production.

### 1.4 What "passes" means for a rewrite

A rewrite is validated by: feed the same synthesized `.nzb` corpus into the new implementation pointed
at the same mock NNTP server, and diff the resulting file tree + byte ranges against the recorded
golden master. Exact byte-for-byte match required for content; tree shape (paths, sizes) exact match
required; internal implementation details (DB schema, internal class names) are explicitly *not* part
of the contract — only externally observable behavior (WebDAV responses, SABnzbd API responses, history
entries) is.

---

## 2. Unit/integration test priorities for the CURRENT codebase

Independent of any rewrite decision, ranked by risk × current lack of coverage. `.NET`'s obvious choice
is **xUnit** (the modern .NET-idiomatic default, good async/`Task`-based test support, `IClassFixture`
support for shared setup like a mock NNTP server) — no reason to deviate to NUnit/MSTest here.

| Rank | Target | Why | Unit-testable in isolation? | Framework |
|---|---|---|---|---|
| 1 | **`ConnectionPool<T>`/`ConnectionLock<T>` concurrent acquire/return/dispose races** | Explicitly flagged in both `core-domain.md`/`usenet-streaming.md`: "authored by ChatGPT 3o," zero coverage, and every single stream + queue download depends on it. Highest blast radius of any untested code in the repo. | **Partially.** Pure logic (bounded pool size, LIFO idle stack, factory invocation) is unit-testable with a fake `INntpClient`/fake connection factory and no network. But the actual *race* conditions (concurrent acquire during idle-sweep, dispose-while-borrowed, replace-on-failure under concurrent load) need genuine concurrent load — spin up N tasks hammering `AcquireAsync`/`ReleaseAsync`/`ReplaceAsync` against a fake backing factory with injected artificial delay, run with `xunit`'s parallel test execution disabled for this fixture, assert invariants (pool never exceeds max size, no connection double-returned, no use-after-dispose) via `Interlocked` counters/`ConcurrentBag` observation, not literal timing assertions. This is a stress test, not a network integration test — no real NNTP server needed. | xUnit + fake `INntpClient` |
| 2 | **RAR/7z part-number reconciliation & volume-validation heuristics** (`RarAggregator.ValidateVolumes`, header-vs-filename delta reconciliation) | Silently produces a corrupted/incomplete playable file (wrong byte ranges) with no crash — the worst kind of bug, invisible until playback artifacts. | **Fully unit-testable.** Feed synthetic `StoredFileSegment` lists (constructed in-memory, no NZB/NNTP needed) with deliberately conflicting header-vs-filename part numbers, missing volumes, out-of-order volumes, and assert the aggregator either reconstructs the correct order or throws `InvalidDataException` as designed. This is pure data transformation — the highest ratio of "risk closed per test-hour spent" in the whole backlog. | xUnit, no fixtures beyond in-memory objects |
| 3 | **PAR2 filename-reconciliation priority/tolerance logic** (`GetFileInfosStep.GetFilenamePriority`, `IsCloseToYencodedSize` 95–100% tolerance band) | Directly determines whether obfuscated releases get correctly deobfuscated; a regression here silently misnames or mismatches files. | **Fully unit-testable.** Construct fake first-segment scan results + fake PAR2 `FileDesc` lists with deliberately ambiguous/colliding size claims, assert the priority scoring picks the expected name. Also pure data transformation, no I/O. | xUnit |
| 4 | **Interpolation-search seek algorithm** (`backend/Utils/InterpolationSearch.cs`) | The core mechanism behind QS-1 (seek latency); a subtle off-by-one or wrong-convergence bug silently returns wrong byte ranges rather than erroring. | **Fully unit-testable in isolation** — it operates over an abstract "segment sizes" index, no network needed. Test with uniform segment sizes (the common case), a non-uniform last segment (the documented edge case), single-segment files, and adversarial inputs (all-zero-size segments, single giant segment) to confirm it degrades to correct-but-slow rather than wrong. | xUnit |
| 5 | **SABnzbd API surface contract tests** | Sonarr/Radarr depend on *exact* response shape (`Api/SabControllers/*`: `AddFile`, `AddUrl`, `GetCategories`, `GetHistory`, `GetQueue`, `GetStatus`, `GetVersion`, `RemoveFromHistory`, `RemoveFromQueue`). An accidental field rename/type change breaks every user's Sonarr/Radarr integration silently (Arr clients don't always surface API parse failures loudly to the user). | **Mostly integration-level**, not pure unit — needs the ASP.NET Core test host (`WebApplicationFactory`) with a seeded in-memory/temp SQLite DB, issuing real HTTP requests against `mode=queue`/`mode=history`/`mode=addfile` etc. and asserting the JSON response against a captured reference shape (snapshot testing — e.g. Verify.Xunit or a hand-maintained reference JSON per mode). This is the most valuable "won't accidentally break Sonarr/Radarr" gate and doubles as partial coverage for a rewrite's SAB surface too, since the contract is language-agnostic. | xUnit + `Microsoft.AspNetCore.Mvc.Testing` |

**Split of unit-only vs. needs-a-running-instance**: ranks 2–4 are pure unit tests, zero infrastructure,
cheapest to write and highest value-per-hour — these should be written first regardless of anything
else. Rank 1 (`ConnectionPool`) needs concurrent-load stress testing but still no real network — a fake
`INntpClient` factory with configurable artificial delay/failure injection is sufficient. Rank 5 needs a
real (in-process) ASP.NET Core test host but not a real NNTP server or real DB file — SQLite in-memory
mode works.

---

## 3. End-to-end / smoke tests against a running container

Propose a small number of high-value black-box tests exercising the system as a real user or Sonarr
would, against a real running Docker container:

1. **Add NZB → verify it reaches history.** POST an nzb (via the SABnzbd `mode=addfile` endpoint, the
   same path Sonarr/Radarr use) pointing at articles served by a mock NNTP server; poll `mode=history`
   until the item appears with `status=Completed`; assert the expected file(s) exist via a WebDAV
   PROPFIND.
2. **Range-GET a streamed file → verify correct bytes.** WebDAV GET with a `Range` header at a non-zero
   offset into a file produced by step 1; assert the returned bytes exactly match the known synthetic
   content at that offset (this reuses the same "distinctive content per byte offset" trick from §1.3,
   so a wrong-seek bug is caught by a trivial byte comparison, not a fragile golden hash).
3. **Queue removal mid-processing → verify clean cancellation.** Add a large/slow synthetic NZB, remove
   it from the queue while in-flight, assert no `DavItem`/history entry is left behind (validates the
   cancellation path described in `core-domain.md` §2's failure/retry semantics).
4. **SABnzbd status/queue/version smoke** — `mode=version`, `mode=fullstatus` return well-formed
   responses on a fresh container (cheap, catches gross startup/wiring failures immediately).

### Mock NNTP server — yes, straightforward, and it should already exist for other reasons

The NNTP protocol subset actually used here (per `usenet-streaming.md` §1: connect/auth, `STAT`, `HEAD`,
`BODY`/`ARTICLE`, `DATE`) is simple line-based text protocol — a mock server that serves pre-loaded
article bodies keyed by message-ID, honors `AUTHINFO USER`/`PASS`, and optionally injects configurable
latency/failure/connection-drop behavior is a modest weekend build (a few hundred lines in any
language; Python's `asyncio` or a small .NET `TcpListener`-based harness both work fine, and building it
in .NET lets it reuse the same synthetic-corpus generator from §1.3).

This mock server is **not just useful for E2E tests — it's close to a prerequisite for meaningfully unit-
/integration-testing the Usenet client stack at all** (`MultiConnectionNntpClient`, `MultiProviderNntpClient`,
`ProviderCircuitBreaker`, `DownloadingNntpClient`'s priority/bandwidth logic all assume a real
`INntpClient` at the bottom). Recommend building it once, early, as shared test infrastructure used by:
the golden-master harness (§1), the `ConnectionPool` stress tests (§2 rank 1, for injecting artificial
connect delay/failure), the circuit-breaker tests (a natural rank-6 addition once the mock exists), and
the E2E smoke suite (§3). This is the single piece of infrastructure with the highest reuse value across
this entire strategy — build it before, not alongside, the rest.

---

## 4. CI integration

Current state (`branch.yml`): build-and-push only, all branches, no gate. `02-constraints.md` and
`11-risks-and-technical-debt.md` (P2-8) already flag `npm run typecheck` as existing-but-unwired.

Minimal proposed change:

1. **Immediately, regardless of anything else in this document**: add a `typecheck` job to a new
   `pr.yml` (or extend `branch.yml`) that runs `npm run typecheck` in `frontend/` and `dotnet build` in
   `backend/`, required to pass before merge to `main`. Zero new test infrastructure needed — this is
   pure wiring of what already exists, and it's the cheapest possible win (P2-8 in the existing
   backlog). Do this in the same week this strategy is adopted, independent of the rewrite decision.
2. **Once xUnit tests exist (§2)**: add a `dotnet test` step to the same job. Pure-unit tests (ranks 2–4)
   run in milliseconds; keep the `ConnectionPool` stress test (rank 1) and mock-NNTP-backed tests (§3) in
   the same job since none require real network access or secrets — everything is local/offline given
   the mock server.
3. **Golden-master corpus (§1)**: run as a separate, slower CI job (or nightly/on-demand rather than
   per-PR, if runtime becomes annoying) that runs the full synthetic corpus through the pipeline and
   diffs against recorded fixtures checked into the repo (or an artifact store, if fixture size is a
   concern — likely small, since synthetic content is deliberately tiny). This is the job a rewrite
   branch would also run to prove parity, so it's worth keeping fast enough to run on-demand from any
   branch (including a future `rust-backend`/`java-backend` branch), not just `main`.
4. Existing `branch.yml`/`pre-release.yml`/`release.yml` build-and-push behavior is unaffected — this is
   additive, not a replacement of the Docker publish flow.

---

## 5. Sequencing recommendation

**Build the golden-master corpus and the highest-risk unit tests first, before committing to any
rewrite. This is not a hedge — it's the only way the rewrite decision itself can be made on evidence
instead of faith.**

Reasoning:

- Every one of the three rewrite proposals (Rust, Java+GraalVM, htmx frontend) will, at some point,
  produce a "does this actually work the same as before" question that today has no answer except manual
  spot-checking. Building the oracle first means that question has an answer *from day one of the
  rewrite*, not retrofitted after weeks of new-language work when a subtle behavioral divergence has
  already been built on top of.
- The golden-master corpus and mock NNTP server (§1, §3) are needed regardless of *which* rewrite (or
  none) is chosen — they test the *contract*, not the .NET implementation specifically. This makes them
  the correct first investment under uncertainty: they retain full value even if the team ultimately
  decides not to rewrite anything (per `11-risks-and-technical-debt.md`'s own framing, a rewrite is one
  of several options, not a foregone conclusion).
- The highest-risk *unit* tests (§2 ranks 1–4) are cheap (S-M effort each, per the existing backlog's own
  effort tags) and, as a side effect of writing them, **will surface whether the current .NET
  implementation already has latent bugs** in exactly the code a rewrite would need to reproduce
  faithfully — better to find and fix (or consciously document) those in the current, small, well-
  understood codebase than to discover "wait, was that RAR reconciliation heuristic even correct in the
  original?" mid-rewrite in a new language.
- Sequencing unit tests and the golden master *before* picking a rewrite also directly informs the
  rewrite decision itself: writing rank-2/3 tests (RAR/PAR2 heuristics) forces someone to precisely
  specify logic that today exists only as inherited, lightly-commented C# — that specification is exactly
  what a Rust/Java port needs anyway. This work is not wasted if a rewrite is chosen, and not wasted if
  it isn't.
- Do **not** wait for 100% coverage before starting a rewrite exploration/prototype — that would be
  over-indexing on process. The recommendation is: build the mock NNTP server + golden-master harness +
  ranks 1-4 unit tests (§2) **first** (a matter of a few weeks solo, see §6), then let that become the
  shared acceptance bar any rewrite prototype is judged against as it develops, rather than gating all
  rewrite work until every item in this document is done.

---

## 6. Effort estimate (solo/small-team maintainer)

Effort tags follow the same S/M/L convention as `11-risks-and-technical-debt.md`.

| Component | Effort | Notes |
|---|---|---|
| Mock NNTP server (shared infra, §3) | **S-M** (3-6 days) | Simple line-protocol server; the highest-reuse item in this whole plan, build first |
| Synthetic corpus generator (§1.3: build RAR/7z/PAR2 fixtures via CLI tools + scripted content generation) | **S-M** (3-5 days) | One-time; mostly scripting around existing `rar`/`7z`/`par2cmdline` binaries |
| Golden-master harness (run corpus through pipeline, capture tree shape + byte ranges, diff logic) | **M** (1-2 weeks) | The actual test runner + fixture format + diffing; depends on the mock server and corpus existing first |
| **Golden-master total (§1 + shared §3 infra)** | **M, roughly 3-4 weeks solo** | This is the one-time investment that pays for itself on any future rewrite attempt |
| `ConnectionPool`/`ConnectionLock` stress tests (§2 rank 1) | **S-M** (2-4 days) | Needs the mock server (or a simpler fake factory with injected delay — doesn't strictly need the full NNTP mock) |
| RAR/7z reconciliation unit tests (§2 rank 2) | **S** (2-3 days) | Pure in-memory data transformation tests |
| PAR2 reconciliation unit tests (§2 rank 3) | **S** (2-3 days) | Same |
| Interpolation-search unit tests (§2 rank 4) | **S** (1-2 days) | Same, smallest surface of the four |
| SABnzbd contract tests (§2 rank 5) | **S-M** (3-5 days) | `WebApplicationFactory` setup is the main new-infrastructure cost; tests themselves are straightforward once set up |
| **Unit test total (§2, ranks 1-5)** | **S-M, roughly 2-3 weeks solo** | Independent of §1, can be done in parallel or first if preferred — no shared dependency except the mock server for rank 1 |
| CI wiring (§4 items 1-2) | **S** (1-2 days) | Mostly YAML; `typecheck` wiring (item 1) is trivially under a day and should ship immediately regardless of the rest |

**Total for a solo maintainer to reach "golden master + highest-priority unit tests, CI-gated":
roughly 6-8 weeks of part-time (evenings/weekends) effort**, or 3-4 weeks if treated as a focused
full-time push — small relative to the multi-month scope of any full backend rewrite, which is exactly
the point: this is a cheap prerequisite, not a comparable-scope alternative project.
