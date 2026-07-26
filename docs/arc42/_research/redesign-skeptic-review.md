# Redesign Skeptic Review

**Role of this document**: an independent stress test of the four in-flight redesign proposals
(Rust backend, Java+GraalVM backend, htmx+Web-Components frontend, testing strategy), written
without access to their text. The brief for this analysis was explicit that "not recommended
currently" was rejected as a conclusion delivered with too little engagement — this document does
not re-assert that conclusion. It separates the parts of the earlier caution that are still true
from the parts that were just excessive hedging, adds new evidence the earlier pass didn't have,
and ends with a concrete, sequenced recommendation.

All git data below is directly reproducible; commands are given so any claim can be checked, not
taken on faith.

---

## 1. The realistic failure mode: does NzbDav fit the "stalled rewrite" pattern?

The "rewrite in $LANGUAGE" GitHub-issue graveyard is a real, well-documented pattern across infra
projects — the common shape is: an ambitious rewrite branch is started with energy, the original
maintainer(s) keep fixing bugs and shipping features on the old version because real users are still
filing real issues against it, the rewrite's scope creeps as edge cases inherited "for free" by the
old version turn out to need re-discovering one by one, and the rewrite either stalls indefinitely
or ships years late missing features the old version had gained in the meantime. It is not a
Rust-specific or Java-specific phenomenon — it happens to Go rewrites, TypeScript rewrites, anything
where "start over" competes against "the old thing still works and people depend on it."

**Does NzbDav's situation make it more or less likely to follow this pattern?** Two structural facts
push in *opposite* directions and both need to be named plainly:

- **More likely to stall**: single/solo maintainer, no CI test gate, no dedicated QA, done alongside
  whatever else occupies the maintainer's time (this is a homelab tool, not someone's job). A stalled
  rewrite here has nobody else to pick up the slack — there's no team to reassign the old version's
  maintenance to while one person is heads-down on the new one. That's the single sharpest version of
  this risk: **on a solo project, "maintain both" is not actually possible** the way it nominally is on
  a staffed team, because there's exactly one person's time to split.
- **Less likely to stall, for a specific reason not available to most rewrite-graveyard projects**:
  see §2 below — the thing a rewrite would normally be racing against (a firehose of upstream
  commits it needs to keep re-porting) has largely stopped moving. A rewrite here isn't racing a
  moving target the way "rewrite curl in Rust" would be.

Net: the classic failure mode (old version outpaces the rewrite) is *less* of a live threat than
usual, precisely because upstream isn't shipping much right now (§2). But the *solo-maintainer*
version of the failure mode (attention gets split, the old version bit-rots for reasons unrelated to
upstream pace — e.g. dependency CVEs, Docker base image EOL, user-reported bugs in the current
.NET/React version going unanswered while effort goes to the rewrite) is real and isn't addressed by
anything in this document unless it's explicitly planned around (§6).

---

## 2. What "96% inherited, 415 vs ~12 commits" actually implies for a rewrite's timeline — verified against real upstream activity

This is the single most consequential correction this review makes to the existing docs, so the
evidence is laid out in full.

**The existing analysis's own git commands only look at this repo's local history**, which shows
sync cadence with upstream, not upstream's actual current activity. `git remote -v` confirms `origin`
is `habenspass/nzbdav` (the fork itself) — there is no upstream remote configured locally to diff
against. So the right check is querying the real upstream repo directly, the same way the existing
ADR-009 verification in `09-architecture-decisions.md` did (`gh api repos/nzbdav-dev/nzbdav/commits/<sha>`):

```
$ gh api 'repos/nzbdav-dev/nzbdav/commits/main' --jq '.sha[0:10], .commit.author.date'
794948be29
2026-05-27T23:07:00Z

$ gh api 'repos/nzbdav-dev/nzbdav' --jq '{archived, pushed_at, open_issues_count, stargazers_count, forks_count}'
{"archived": false, "pushed_at": "2026-07-01T16:39:13Z", "open_issues_count": 144, "stargazers_count": 1121, "forks_count": 113}
```

**Upstream `main`'s newest commit, as of today (2026-07-26), is dated 2026-05-27** — a small
logging fix (`#441`, external contributor `loambit`). That's exactly two months of zero merges to
upstream `main`, on a project with 1,121 stars, 113 forks, and 144 open issues (i.e., not a dead or
unwatched repo by any surface metric — `pushed_at` is recent because dependency-bump branches keep
getting pushed by Dependabot, not because `main` is moving).

It gets more specific than "quiet": upstream's own open/closed PR queue shows the quiet is not
"nothing is being submitted," it's **submissions are piling up unmerged or being closed without
merging**:

```
$ gh api 'repos/nzbdav-dev/nzbdav/pulls?state=open&per_page=20' --jq '.[] | "\(.number) \(.created_at[0:10]) \(.title)"'
471 2026-07-01  fix(deps): Bump the dotnet group with 3 updates
465 2026-06-23  Pipeline STAT article-health checks (on-add + background)
437 2026-05-18  feat: native URL_BASE support for sub-path hosting
421 2026-05-05  feat: add Lidarr support
420 2026-05-05  feat: add LISTEN_ADDRESS env var to configure bind interface
...(plus ~14 more open dependency-bump PRs from May–July, all unmerged)

$ gh api 'repos/nzbdav-dev/nzbdav/pulls?state=closed&per_page=10&sort=updated&direction=desc' --jq '.[] | "\(.number) merged=\(.merged_at) \(.title)"'
478 merged=null  fix: dispose leaked socket on failed auth, gate concurrent circuit-breaker probes   (opened 2026-07-22, closed 2026-07-24 — 2 days, unmerged)
444 merged=null  fix(nntp): add configurable connection idle timeout + cap prefetch at HTTP Range end
441 merged=2026-05-27  fix(nntp): tag provider name in connection-lock and command-error logs          (the last real merge)
439 merged=null  fix(api): Treat history delete as idempotent when row is already gone
473 merged=null  Usenet stream / download speed feature
399 merged=null  feat(nntp): playback streaming timeout fast-fail and retry logic                     (opened 2026-04-10, closed 2026-07-13 — sat 3 months, then closed unmerged)
```

Real feature PRs (Lidarr support, sub-path hosting, article-health-check pipeline — none of them
trivial) have sat open for one to three months with no merge decision either way. A fix PR was
closed within two days of being opened without merging. This is a legible, falsifiable pattern of
**upstream maintainer bandwidth dropping sharply starting around late May 2026**, independent of and
earlier than this fork's own July commit burst (which, per the earlier git-log evidence, is entirely
`habenspass`-authored — the fork's own maintainer picking up nearly all recent activity on both
sides).

**Why this matters for the rewrite timeline math the brief asked for**: the existing docs' framing
("415 vs ~12 commits, and a rewrite forfeits pulling future upstream fixes") implicitly assumes
upstream is a moving target a rewrite would fall behind. **That assumption no longer holds as
strongly as the historical commit count suggests.** If upstream merges roughly zero commits/month
going forward (extrapolating the last two months, not a certainty but the best available signal),
a 6-month rewrite doesn't forfeit "6 months × upstream's historical ~30-40 commits/month" — it
forfeits close to nothing, because there's increasingly little to forfeit. **This is exactly the
condition §9.3 of the existing analysis already named as the trigger for revisiting the
recommendation**: *"if the fork maintainer decides to permanently diverge from upstream regardless
of language (e.g., upstream becomes unmaintained...), a full rewrite becomes a first-class option
again."* The evidence above is a real, dated signal that this condition may already be firing, not a
hypothetical.

**Caveats, stated plainly so this isn't overclaimed**: two months of quiet plus a slow PR queue is
suggestive, not proof of abandonment — maintainers take breaks, have life events, or batch-merge
after a lull. It does not establish that upstream is *permanently* dead. It does establish that the
"lose the upstream firehose" cost the original docs priced in is, right now, closer to zero than to
"415 commits worth of stuff." **If this analysis is acted on, the single highest-value cheap check
before committing to any rewrite is: watch upstream for another 4-6 weeks, and/or the fork maintainer
should directly ask upstream (issue/discussion) about project status** — this is a two-minute
action that meaningfully de-risks the biggest number in this whole calculus, and costs nothing to
do in parallel with any other work.

---

## 3. Ecosystem-maturity honesty check (verified, not recalled)

### Rust: is there a real WebDAV foundation, or from-scratch?

Verified via search: **`dav-server`** (crates.io, a maintained fork-of-a-fork lineage via
`webdav-handler` → `dav-server-rs` → `dav-server`, 18+ contributors) is real and passes the WebDAV
Litmus Test's basic/copymove/props/locks/http suites — i.e., baseline RFC4918 compliance is not a
from-scratch problem in Rust.

**What that maturity claim does *not* cover**, and this is the load-bearing nuance: RFC4918
compliance tests check protocol correctness (PROPFIND, locking, COPY/MOVE semantics) — they say
nothing about the thing this specific app's WebDAV layer actually earns its keep on: custom
range/seek handling for multi-gigabyte files backed by a non-filesystem store
(`GetAndHeadHandlerPatch`, `backend/WebDav/Base/GetAndHeadHandlerPatch.cs` — computes
`Content-Range`/206-vs-200 and does manual buffered range copies over a *composed* `Stream` chain,
not a real file). A generic Rust WebDAV crate gives you the protocol scaffolding; the actual
hard part of this project — seekable streaming over synthetic multi-segment/RAR/AES-wrapped content
— is unique application logic that has to be built again in any language, crate or no crate. So: "is
there a usable WebDAV crate" — yes. "Does that crate materially de-risk *this app's* hardest problem"
— no, that problem is bespoke either way.

### Java + GraalVM: how real is the native-image reflection tax?

Verified via search: GraalVM's reachability-metadata mechanism is real and is exactly the tax
described — native-image does closed-world, build-time analysis and anything reached only through
reflection, dynamic proxies, or resource loading at runtime (the bread and butter of JPA/Hibernate-style
ORMs, and of ASP.NET/EF-Core-equivalent frameworks generally) has to be explicitly declared via
metadata or it silently fails at runtime, not at build time. Oracle/GraalVM ship a shared
"reachability metadata repository" specifically because enough popular libraries need this that a
central registry of pre-written config was worth building — which is itself evidence the tax is
real and common enough to need infrastructure, not a rare edge case.

**Practical implication for this project specifically**: this doesn't block a Java+GraalVM rewrite,
but it means "compile the equivalent of `DavDatabaseContext`/EF Core's LINQ-to-SQL translation and
30+ migrations' worth of entity mapping to a native image" is not a mechanical, low-risk step — it's
a real integration task requiring either (a) staying on the JVM (skip native-image, lose the
"single small static binary" QS-4 win that's presumably the whole point of choosing GraalVM) or (b)
budgeting real time for reachability-metadata authoring/testing for whatever ORM is chosen, ideally
one already covered by the shared metadata repo rather than a hand-rolled one. This is a real,
non-trivial line item that a Java+GraalVM proposal needs to price in explicitly, not wave away as
"it's just a build flag."

### htmx + Web Components: does it actually avoid "React, but worse and hand-rolled"?

Not separately re-verified via search (this is a design-pattern judgment, not a factual lookup), but
grounded directly in what `docs/arc42/_research/frontend.md`'s findings (cited in
`09-architecture-decisions.md` D28-D33) already established about *this app's* frontend: real-time
queue/history/connection-count state pushed over one relayed WebSocket (D32), and a file-browser
navigation UI. Both are genuine client-side state problems, not just server-rendered pages with a
sprinkle of interactivity — which is exactly the category where htmx's core model (server returns
HTML fragments, swap into DOM) has to reach for something extra:

- Live WebSocket-pushed queue/progress updates are htmx's actual documented strength via its SSE/WS
  extensions (server pushes a fragment, htmx swaps it in) — this part is not a stretch.
- Web Components carry client-side state (e.g., a file browser's expand/collapse tree state,
  in-flight upload progress bars, optimistic UI while a queue action is pending) in **hand-rolled
  component internals with no shared state-management convention** — every team that has tried
  "htmx + web components for a genuinely stateful admin UI" ends up re-inventing some of what
  React/a framework store gives for free (component-local state, cross-component reactivity), just
  without the tooling, TypeScript-typed props, or ecosystem of solved patterns. This is the real
  version of "React, but worse and hand-rolled" — not a strawman: it shows up specifically once a
  UI needs cross-component reactive state, and this app's queue view is exactly that (multiple
  concurrent queue items, each independently updating, feeding a shared history list).

The honest framing: htmx+Web-Components is a **strong fit for the parts of this UI that are mostly
document-like (settings forms, config pages, static file browsing)** and a **genuine, not-fully-solved
challenge for the parts that are live-dashboard-like (queue/progress view)** — which is precisely the
part of the UI this project's own quality goals care most about. A proposal that picks this path
should say explicitly which islands of the UI get a heavier client-side treatment (even "keep a
small amount of vanilla JS/Alpine.js/a tiny signal library just for the queue view, htmx for
everything else") rather than presenting it as a uniform, risk-free swap.

---

## 4. Single-maintainer bus-factor question

The brief is right to flag this as evidence-free territory, and the earlier analysis should not
guess. What's actually verifiable from git history: **the fork maintainer (`habenspass`) has
demonstrated real, sustained productivity in both C# (per-provider usage stats, bandwidth
throttle/reserve split, prefetch cache backend logic) and TypeScript/React (the corresponding
frontend settings/usage UI for the same features) across the current stack, spanning several
months of commits** (`git shortlog`, `git log --author=habenspass`). There is **zero evidence either
way** — not negative evidence, genuinely absent evidence — about fluency in Rust or Java/GraalVM
specifically. Flagging it as unknown, per the brief's own instruction, rather than assuming
competence or incompetence either direction.

What *can* be stated without guessing at language fluency: switching the implementation language is
a bus-factor-neutral-to-negative move **unless** the maintainer already has meaningful Rust/Java
experience elsewhere, because:
- It resets "time to productive" for exactly one person, on a codebase where that one person is
  currently the only committer for the domain-critical parts (queue/database/webdav — see
  `core-domain.md`'s fork-status recap: 79/54/41 commits per folder are upstream, 1-2 are
  fork-authored).
- It does **not** currently increase the number of people who could contribute — this is not a team
  moving to a language more of its members know; it's a solo maintainer's own tooling choice.
- The genuine, not-hypothetical bus-factor lever available today is different: **more of this
  project's contributor pool (Sonarr/Radarr-adjacent homelab tinkerers, judging by the existing
  external PRs — Anthony Hoivik, David Young, loambit, Root-Core, Evan) is more likely to already
  know C#/TypeScript than Rust or Java+GraalVM specifically**, simply because that's what the
  existing 415+ commits of prior art, existing docs, and existing PR history are already in. Any
  language switch has to weigh a real, if unquantified, risk of shrinking the pool of people who
  could plausibly send a PR at all — not just the maintainer's own ramp-up cost.

Net: this isn't a knockout argument against either rewrite, but it's a real cost that should be
named as a cost, not waved past — "bus factor" for this project today is really "one maintainer plus
an occasional external PR in a shared, common stack"; a language switch trades a known, currently-
functioning version of that arrangement for an unknown one.

---

## 5. Separating what the original caution got right from what was excessive

Going through `docs/arc42/11-risks-and-technical-debt.md` §11.4's rejection table and
`09-architecture-decisions.md` §9.3 item by item, marked **UPHELD** (still true, keep it),
**REVISED** (partially true, needs updating with new evidence from this review), or **WITHDRAWN**
(the tone/conclusion doesn't survive scrutiny as stated):

| Original claim | Verdict | Why |
|---|---|---|
| "Zero test coverage to validate behavioral parity" for RAR/7z/PAR2 deobfuscation heuristics | **UPHELD** | Directly verified in `core-domain.md` — no backend test project exists anywhere in this repo. This is a fact, not a hypothesis, and it's the single most legitimate reason to sequence a rewrite behind a characterization-testing effort (§6). |
| "Forfeits upstream mergeability, a real recurring cost" | **REVISED** | The *mechanism* is real (diverging architecture makes future upstream diffs unmergeable) but the *size* of the cost was overstated by treating 415 historical commits as a proxy for future cadence. §2's evidence shows upstream has merged ~1 commit in the last 2 months and has a growing backlog of stale/closed-unmerged PRs. The forfeited stream is, right now, much smaller than "415 commits worth." This is the single biggest correction this review makes. |
| "The actual hot path (yEnc decode) is already native/SIMD... the biggest classic rewrite-for-speed win is already banked" | **UPHELD** | Independently confirmed by the usenet-streaming research pass (`UsenetSharp`/`RapidYencSharp`, P/Invoke to native `rapidyenc`). A rewrite's realistic QS-4 win is container size / idle RAM, not raw throughput — true regardless of how the earlier draft's tone landed. |
| "Payoff... is likely real but modest" (container size, idle RAM) | **UPHELD**, with the caveat that "modest" is doing a lot of work — no measurement exists (OQ-6/OQ-7 in `11-risks-and-technical-debt.md` are still open). Neither this review nor the earlier one has a real number; both are reasoning from architecture, not profiling. |
| Rust/Go/Java ecosystem maturity as a blanket reason not to rewrite | **REVISED** | §3 shows the WebDAV-protocol-scaffolding part is more mature than a skeptical read might assume (Rust has a real, tested crate). The genuinely hard, unsolved-by-any-crate part is this app's bespoke seekable-streaming logic — that's a constant-cost item in any language, not a Rust-specific or Java-specific tax. The earlier framing implied "ecosystem gaps" broadly; the real gap is narrower and language-independent. |
| Frontend rewrite "recommended only if the SSR-off experiment first demonstrates a real QS-4 problem" | **UPHELD as a sequencing point, WITHDRAWN as a blocker** | The SSR-off experiment (P1-10) is cheap and worth doing regardless — but per §3 above, the more decisive question for htmx+Web-Components specifically isn't "does SSR cost matter," it's "does this UI's live-queue state fit htmx's model" — a design-fit question, not a QS-4 measurement, and one this review answers partially in §3 without needing the experiment first. |
| "No decision is off the table" was not actually honored by defaulting every §9.3/§11.4 entry to a rejection with the same boilerplate cost-benefit shape | **the owner's complaint, and it's fair** | Re-reading §9.3 and §11.4 together, every alternative gets essentially the same treatment (INHERITED-ness cited, cost asserted, "not recommended" concluded) regardless of how different Rust/Java/frontend/modularization actually are as risks. A Rust backend rewrite and a "split into Nx projects" modularization change are not remotely the same size of decision, but both get resolved the same way. That pattern — not any single factual claim — is what justified the owner's pushback, and this review's job was to re-derive the specifics rather than the tone. |

---

## 6. Constructive counter-proposal: the highest-value subset of "go do it"

If a full simultaneous bet (rewrite the backend *and* the frontend, in a new language, in one push)
is the too-risky version, here is a concrete, sequenced alternative that captures most of the stated
goal (get out from under a fork of inherited code, in whatever language serves the project best
long-term) at a fraction of the risk of doing everything at once — and gives each step a real,
falsifiable stopping/go condition rather than being a vague "start small" gesture.

**Step 0 (days, not weeks) — resolve the upstream-status uncertainty this review surfaced.** Before
anything else: post on the upstream repo (issue or discussion) asking about maintenance status, and/or
just watch for 4-6 weeks. This one action directly changes the size of the biggest number in this
whole document (§2). If upstream resumes normal cadence, the mergeability cost argument regains its
original force and every rewrite proposal should be read more conservatively. If it stays quiet,
the fork is already, functionally, the primary living version of this project, and every argument
against diverging from upstream gets correspondingly weaker.

**Step 1 (low risk, do regardless of what else happens) — the testing-strategy proposal's
characterization tests, targeted narrowly, not as generic coverage.** Not "add tests" in general —
specifically: golden-file/characterization tests over the deobfuscation pipeline's name-reconciliation
heuristic (`GetFileInfosStep.GetFilenamePriority`/`IsCloseToYencodedSize`) and the RAR
aggregator's volume/part-order reconciliation (`RarAggregator.ValidateVolumes`/part-number-delta
logic), captured against a handful of real, messy, obfuscated-release NZBs. This is the exact
asymmetry that should drive sequencing everywhere else in this document: a bug in this logic doesn't
crash anything or throw a visible error — per `core-domain.md`'s own weak-points section, it "can
silently produce a corrupted/incomplete playable file... that only surfaces as playback artifacts."
That failure mode is invisible without characterization tests and is the *specific* reason a backend
rewrite is riskier than a frontend one — not "the backend has more lines of code," but "the backend's
bugs are silent and destructive, the frontend's are visible and recoverable." **No backend rewrite,
in any language, should start before this exists** — it's the regression net that makes "did the
rewrite preserve behavior" answerable at all, and it's valuable even if no rewrite ever happens.

**Step 2 (the actual highest-value/lowest-risk "go do it") — the frontend rewrite, and specifically
starting with the parts of the UI that are document-like, not the live-queue view.** This is lower
risk than the backend rewrite for reasons independent of raw size:
- It cannot touch the 96%-inherited domain logic (Queue/Database/WebDav/Par2Recovery) at all — a
  frontend rewrite has zero interaction with the exact code this document worries most about
  regressing silently.
- Frontend bugs are visible (a broken page renders wrong, a form doesn't submit) and non-destructive
  (no user's Usenet download or media library is at risk from a frontend bug the way a RAR
  part-ordering bug risks a corrupted stream) — the opposite of the backend's failure-mode asymmetry
  from Step 1.
- Per §9's D33, this fork's own commits have never touched `server.ts`/`app.ts`/
  `websocket.server.ts`/`auth-middleware.server.ts`/`routes.ts` — meaning there's no existing
  fork-specific frontend work to preserve or re-port, unlike the backend where fork-specific features
  (prefetch caching, bandwidth throttling, usage stats) are threaded through inherited files and
  would need re-porting into a rewritten version regardless of language.
- Concretely, per §3: use htmx+Web-Components (or plain SSR'd fragments, or a lighter framework) for
  settings/config/file-browser pages first, and treat the live queue/progress view as a separate,
  harder sub-problem to solve last (possibly keeping a small amount of reactive JS there even if the
  rest of the UI goes htmx) rather than committing to one uniform approach for the whole UI on day one.

**Step 3 (the actual full bet, gated behind Steps 0-2, not attempted first) — a backend language
rewrite (Rust or Java+GraalVM), only if Step 0 shows upstream divergence is already the reality (not
a hypothetical), and only with Step 1's characterization tests as the acceptance gate for "does the
rewrite behave the same as the original."** Between Rust and Java+GraalVM specifically: this review
did not find a decisive language-choice argument in either direction from the ecosystem-maturity
check in §3 (both have real, if partial, foundations to build on; both leave the actual hard,
bespoke seekable-streaming logic as new work regardless of language) — that choice should be made on
the maintainer's own judgment/preference/existing familiarity (still unverified per §4), not on a
crate-availability argument, since neither language's ecosystem gap is the blocking factor.

**What this sequencing deliberately avoids**: attempting the frontend and backend rewrites
simultaneously, which reintroduces the solo-maintainer version of the classic stalled-rewrite failure
mode from §1 (one person, two simultaneous large unfinished efforts, the working version's own
maintenance gets starved of attention regardless of upstream's pace). Sequential, with Step 2 done and
shipped as a real, working improvement before Step 3 begins, keeps at most one large rewrite in
flight at a time.

---

## Summary

The earlier "not recommended currently" conclusion was procedurally fair to push back on — it
applied uniform "not recommended" boilerplate to genuinely different-sized decisions — but several
of the underlying facts it cited were and remain true (zero test coverage, yEnc already native, the
mechanism of lost mergeability). This review's own new evidence is that **the upstream project this
fork would diverge from has itself gone nearly silent for the last two months, with a visible backlog
of unmerged/closed-without-merging PRs** — which materially shrinks the "lost mergeability" cost
below what the historical 415-commit count implies, and is close to the exact condition the existing
docs already named as the trigger for revisiting the rewrite question. The recommendation is not
"don't rewrite" and not "rewrite everything now" — it's the sequenced path in §6: confirm upstream's
real status (days), write characterization tests over the two silently-fragile heuristics regardless
of what else happens (the actual prerequisite for any backend rewrite to be verifiable at all), ship
a frontend rewrite first since it's genuinely lower-risk on structural grounds (not just smaller),
and treat a full backend language rewrite as the real, biggest bet — gated behind the first two, not
attempted as the opening move.
