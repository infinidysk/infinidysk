# AGENTS.md

Quick-start guide for AI agents working on **infinidysk 2.x**, the from-scratch Rust rewrite of infinidysk on `main`.

The name is always written lowercase: `infinidysk`, even at the start of a sentence. Never "InfiniDysk".

## What this project is

A Usenet-backed virtual filesystem. It imports NZBs from Sonarr/Radarr through a **SABnzbd-compatible API** and serves the media through a **native FUSE mount** and **WebDAV**, streaming directly from Usenet providers without downloading files to disk.

Target: a single Rust binary (tokio, axum, rustls, `fuse3`, sqlx on SQLite + PostgreSQL) with an embedded React + Vite SPA, shipped as a Linux x64/arm64 Docker image on `ubuntu:26.04` (glibc; no musl/Alpine). macOS is for development and benchmarking only. No Windows.

## The C# 1.x code on `v1` (specification)

| | |
|---|---|
| GitHub | [`infinidysk/infinidysk` branch `v1`](https://github.com/infinidysk/infinidysk/tree/v1) |
| Local checkout (if present) | `../infinidysk` (sibling of this repo, on `v1`) |
| Stack | .NET 10 backend (`backend/`, namespace `NzbWebDAV`), React Router frontend (`frontend/`), in-tree libs under `libs/` (`UsenetSharp`, `SharpCompress`, `RapidYencSharp`, `rapidyenc` submodule) |
| Status | **Bug-fix only** (1.x maintenance) |

The plan refers to its files as `infinidysk:<path>` (e.g. `infinidysk:backend/Clients/Usenet/ConnectionPool.cs`); those paths are on `v1`.

Rules for using it:

- Read C# source and tests as a **specification**. Write idiomatic Rust. No line-by-line ports.
- **Never modify** `v1` from a task on `main`. Read-only. If you need it and it is not checked out locally, read it via `gh` / the GitHub API (`?ref=v1`).
- Behavior that must stay **identical**: SAB API (`/api?mode=…`) and WebDAV paths/semantics (`/view`, `/content`, `/nzbs`, `/.ids`, `/completed-symlinks`). Everything else (schema, blob format, admin API, env vars, UI) is new.
- Useful starting points in the C# repo: `backend/Clients/Usenet/` (NNTP pool, circuit breaker, streaming client), `backend/Streams/` (multi-segment stream), `backend/Queue/` (NZB/RAR/7z/PAR2), `backend/Api/SabControllers/`, `backend/WebDav/`, `backend/Database/Models/`, `backend.Benchmarks/` (perf scenarios + baselines), `docs/reference/sabnzbd-api.md`, `docs/reference/rfc3977.md`.

## Maintainer companion repo

Decision documents (policies), plans, research, benchmark results and the maintainer workflow live in the private repo [`infinidysk/infinidysk-lab`](https://github.com/infinidysk/infinidysk-lab), checked out at `../infinidysk-lab` when you have access. Code and docs refer to its files as `infinidysk-lab:<path>` (e.g. `infinidysk-lab:docs/health-policy.md` §5.7). When it is present, read its `AGENTS.md` too; it adds to this file. Contributors without access can ignore those references.

`scrap/` is gitignored local scratch space. Put notes, logs and other files that must not be committed there.

## Scope rule

Nothing that talks to a Usenet provider ships until everything that talks to a provider is Rust. Rust v1 covers: streaming engine, queue (NZB, stored RAR4/RAR5/7z members, multipart, PAR2 verify + repair), health + repair, SAB API, symlink import, WebDAV, FUSE, SQLite + PostgreSQL, auth, and a one-time importer from C# `/config`.

**Out of v1** (do not build unless asked): on-disk segment cache, STRM, Stremio/addon routes, Watchtower, search, search profiles, indexers, Prowlarr, metrics DB, rclone RC, Arr monitoring, Preflight, Warden, transfer connections (connection management likely returns to a single shared provider pool; final design follows prior-art research).

## Project principles

- **SABnzbd is the truthstone.** When deciding how strict NZB validation, bounds and constraints are: if SABnzbd can download it, so can we. Do not reject inputs SABnzbd accepts. The one exception is streaming: compressed archive content is not playable (stored members only); detect it and fail it cleanly instead of rejecting the NZB on parse.
- **Arr config is a list of instances, not a list per Arr.** Config holds one `arr_instances` list; each instance carries a type profile (`sonarr`, `radarr`, `lidarr`, …). Arr-specific behavior lives behind the profile so new Arr types are additive. Never add `sonarr: […]` / `radarr: […]` style per-type lists.
- **Caching is global, one layer below delivery.** When caching lands, it is a single app-wide cache at the shared base layer (segment/stream level, under `vfs`) that serves FUSE, WebDAV and STRM alike. No per-delivery-method caches (no WebDAV-only segment cache, no rclone-style cache in front of WebDAV). Without rclone there is no double caching to work around, so do not build one.
- **Blobs carry their own schema version.** Every metadata blob stores a schema version inside the blob itself, so readers know which features that blob supports. New data means a new version; readers keep handling older versions (feature-gate on the stored version) instead of assuming the latest layout or rewriting old blobs.
- **Old pointer formats retire; they are not supported forever.** A change to what we write outside our database (STRM URLs, symlink targets), or a C# format we import, ships with a background rewrite, a usage counter on the old form and a retirement condition (a full rewrite pass plus a quiet period with no use). Code that reads a C# format lives behind a flag the importer sets and is deleted with the importer.
- **Providers come from a managed catalog; backbone replaces storage groups.** We ship a managed list of known providers, each profile carrying hostname, port and `backbone`. Users add a provider by picking a profile; hostname, port and backbone come from it. Providers sharing a `backbone` behave like the old storage groups, but the grouping is not user-configurable; there is no storage-group setting. The catalog lives in `providers/` (one TOML file per provider, backbones in `providers/backbones.toml`), is validated and embedded by `crates/nntp/build.rs`, and is community-maintained. Users may skip the profile and enter a custom hostname and port; a custom provider may pick a backbone from the catalog but never type a new one, and without a pick it is its own group.
- **Data and throughput use decimal bytes.** KB, MB, GB and TB are 10³, 10⁶, 10⁹ and 10¹² bytes, and speeds are shown in MB/s (bytes, not bits; 1000 MB/s ≈ 8 Gbit/s). Never show MiB/GiB or Mb/Mbit in the UI, logs or user-facing settings. API fields that carry a unit say so (`_bytes`, `_mb`, `_mb_per_s`, `_bps` for bytes/s). Exceptions: SAB API fields keep the meaning SABnzbd gives them, and internal buffer sizes in code may be powers of two.
- **Unowned and unlinked, never "orphan".** An *unowned* file is one no Arr instance imported; an *unlinked* file is one no symlink or STRM file in a library folder points to and that is no longer in the download history. Use these words in code, config, API, UI and docs; "orphan" is ambiguous and is not used (C# config keys the importer reads keep their names).
- **Cascade routing is always on; priority levels are pools.** No toggle. Each provider has a priority number; providers with the same priority form one pool, and the next level is tried only after a level misses. The UI edits this with a drag-and-drop priority editor that moves providers between levels.
- **The UI follows [`DESIGN.md`](DESIGN.md).** It is the style source of truth for `ui/` (React + Vite, shadcn/ui on Tailwind): tokens, colors, typography, components and patterns. Read it before adding or changing UI, and amend it in the same commit when the design changes. Keep every page usable on mobile.

## Explaining code and systems

When explaining code or systems to the maintainer, use Simple Technical English. Short sentences. One idea per sentence. Use the domain words that already exist in the code instead of inventing abstractions for them.

## Rust guidelines

The maintainer's Rust rules live in the private skill repo [`hoivikaj/rust-skills`](https://github.com/hoivikaj/rust-skills), checked out at `../rust-skills` (sibling of this repo). When it is present, use it when writing or reviewing Rust: read `../rust-skills/SKILL.md`, then open only the files under `../rust-skills/rules/` relevant to the code in front of you. Contributors without access can ignore it; the project rules below are binding either way.

Project rules (these override rust-skills where they differ):

- Workspace with crates under `crates/<short-name>` (folder `crates/nntp`, package `infinidysk-nntp`); each crate inherits `[workspace.package]`, sets `[lints] workspace = true`, and takes shared dependency versions from `[workspace.dependencies]`. The product binary is `crates/infinidysk`. Create a crate only when code for it exists (see Crate layout).
- Release profile: `lto = "fat"`, `codegen-units = 1`, panic **unwind** (never `abort`; the streaming invariants need catchable callback panics).
- Clippy: `pedantic` baseline minus noisy lints plus selected nursery lints, all set in the root `Cargo.toml`. Prefer fixing over `#[allow]`; any `#[allow]` needs a one-line reason.
- Edition 2024, toolchain pinned in `rust-toolchain.toml`. Before committing: `cargo fmt --all --check`, `cargo clippy --workspace --all-targets --locked -- -D warnings`, `cargo test --workspace --locked`. CI also runs `cargo-deny` (`deny.toml` license allowlist + advisories) and native glibc release builds on Ubuntu 26.04 x64/arm64.
- Product version lives in `version.txt` (owned by release-please); crate versions stay `0.0.0`.
- Mark deliberate corner-cutting with a `ponytail:` comment naming the ceiling and the upgrade path.
- `unsafe` only in FFI crates (e.g. yEnc), which opt in with `#![allow(unsafe_code)]`; every block needs a `// SAFETY:` comment (enforced by clippy).
- No `unwrap` / `expect` outside tests and provably infallible cases (state the proof in the `expect` message).
- `thiserror` in library crates, `anyhow` only at the binary edge.
- `tracing` with structured fields. Never log secrets (provider passwords, API keys, WebDAV credentials).
- Encode streaming lifecycle invariants (below) as types and `Drop` guards, not conventions.
- Tests use deterministic generated data and a loopback NNTP server. Never require live Usenet providers in CI.

### Crate layout

Dependencies point downward only. Parsers do no I/O. No traits just for mocking.

| Layer | Crate | Owns |
|-------|-------|------|
| L0 pure (no tokio) | `core` | ids, byte ranges, metadata types, versioned zstd blob format |
| | `yenc` | rapidyenc FFI; the only `unsafe` crate |
| | `archive` | stored-member headers for RAR4/RAR5/7z, multipart |
| | `par2` | PAR2 parse, verify, repair |
| L1 | `nntp` | provider pool, circuit breaker, download permits, lifecycle guards |
| | `db` | sqlx, SQLite + PostgreSQL, per-backend migrations; one runtime SQL string per query (`$N` placeholders, portable SQL; PostgreSQL-only row locks such as `FOR UPDATE` run behind a backend check) |
| | `arr` | Arr HTTP client: profiles, system status, SAB download-client discovery, repair calls |
| L2 | `stream` | multi-segment seekable stream, prefetch |
| L3 | `vfs` | tree shared by WebDAV and FUSE: lookup, listing, `/.ids`, `/completed-symlinks`, read handles |
| | `queue` | NZB → archive/multipart → PAR2 → items |
| | `health` | health checks and repair |
| L4 | `webdav` | axum WebDAV |
| | `fuse` | `fuse3` mount; Linux only (`cfg(target_os = "linux")`), so macOS dev builds skip it |
| bin | `infinidysk` | lib + thin `main.rs`: config, auth, SAB API, admin API, embedded UI |
| side | `import` | one-time C# `/config` importer (deletable) |
| | `testkit` | loopback NNTP server, seed-1025 corpus (dev-dependency only) |
| | `bench` | benchmarks with C# scenario names |

### Streaming lifecycle invariants

- Every BODY/ARTICLE completion fires **exactly once**, including cancellation, missing articles, setup failures and stream failures.
- Outcomes: `Retrieved` (success, connection reusable), `Cancelled` (drained cleanly, reusable), `NotFound` (clean server miss), `NotRetrieved` (connection must be replaced).
- Completion releases both the provider-pool lease and the global download permit. Callback panics/errors must not fault transport tasks.
- Pipelined batch responses complete in request order. Mixed cache-hit batches request only misses while preserving order.
- Fallback transfers within a batch start in response order and stay bounded; unordered permit acquisition can deadlock sequential consumers.

### Performance

Playback performance has priority over memory usage. Never trade throughput or prefetch for lower memory without explicit approval. Benchmarks port the C# scenarios with the same corpus seed (1025) and metric names; Rust must match or beat the C# baselines before any release.

## Logging: stack dumps vs human-friendly events

Known failures (provider errors, missing articles, timeouts, auth failures, missing `/dev/fuse`, …) log as **one human-friendly warning** with a `reason` field and no backtrace. Full error chains/backtraces are for unexpected errors only (optionally `debug!` the chain for known failures). An unhandled stack dump for a known failure is a defect; a remediation plan must include the logging fix.

## Commit conventions (required)

[Conventional Commits](https://www.conventionalcommits.org/), consumed by release-please:

```
<type>(<scope>): <short imperative description>
```

| Type | Use for |
|------|---------|
| `feat` | New user-visible behavior |
| `fix` | Bug fixes, regressions, compatibility fixes |
| `perf` | Performance-only improvements |
| `refactor` | Internal restructuring worth a release note (rare; prefer `chore`) |
| `build` | Packaging operators care about (otherwise `chore(docker)` / `chore(build)`) |
| `revert` | Reverts |
| `chore` | **All** non-behavior work: CI, tests only, docs, `AGENTS.md`, skills, tooling, repo hygiene |

- Do not use bare `docs` / `ci` / `test` / `tests` types; use `chore(<scope>)`. Any change that does not alter application behavior is `chore` on both the commit and the PR title, so release-please leaves it out of release notes.
- **Breaking changes** (user-visible breaks, irreversible/destructive migrations) must use `!` and/or a `BREAKING CHANGE:` footer. Routine additive migrations are plain `feat(db)` / `fix(db)` with a "back up config before upgrading" note in the PR.
- `chore(main): release …` is reserved for release-please. Never edit `CHANGELOG.md` by hand.
- Small, focused commits; one logical change each. No vague messages (`wip`, `fix bug`), no unrelated changes in one commit.

Scopes (pick the smallest accurate one; omit only for small cross-cutting changes): `nntp`, `yenc`, `stream`, `archive`, `par2`, `queue`, `sab`, `webdav`, `mount`, `db`, `import`, `api`, `auth`, `health`, `ui`, `deps`, `ci`, `docs`, `test`, `agents`, `skills`, `docker`.

Examples:

```
feat(queue): verify and repair multipart sets with PAR2
fix(webdav): return 416 for range requests past the end of a file
perf(stream): prefetch the next segments before the reader asks
feat(db)!: rewrite the item schema
chore(ci): cache the rapidyenc build
chore(agents): require conventional PR titles
```

### PR titles

PR titles use the same `<type>(<scope>): <description>` format as commits; release-please and the PR list rely on it. Never write a PR title without the type prefix. Write the description for **end users**: what is fixed or introduced from their point of view, not how. Commit messages carry the technical detail.

| Commit message (technical) | PR title (user-facing) |
|----------------------------|------------------------|
| `fix(nntp): count a dropped provider connection as one failure` | `fix(nntp): one dropped connection no longer shows up as several provider errors` |
| `fix(api): count only failures the caller sees as errors` | `fix(ui): overview errors only count failures you would notice` |
| `fix(webdav): clamp infinite-depth PROPFIND to depth 1` | `fix(webdav): prevent mounts hanging on large directory listings` |

For changes with no user-visible effect (`chore`, internal refactors) a concise technical title is fine.

## Never write media titles

We cannot verify which media is public domain. **Never** write movie, show, episode, album, book, or release/NZB names in commits, PRs, issues, comments, code, test names, fixtures, docs, or pasted logs. Redact as `<release-name>` and describe generically ("stored RAR5 episode", "obfuscated 7z season pack").

## Never merge pull requests

Agents never merge PRs, in this repo or any infinidysk repo: no `gh pr merge`, no UI/API merge, no auto-merge. Opening the PR and reporting its URL ends the handoff. Never force-push shared branches or amend pushed commits.

## Addressing CodeRabbit reviews

When you address a CodeRabbit review, reply to every comment with what you fixed (with the commit) or why you skipped or declined it. Then resolve **every** CodeRabbit thread, including the ones you skipped or declined. CodeRabbit starts a new review only after all its threads are resolved; one open thread holds the PR at "changes requested". After resolving, check that no CodeRabbit thread is still open, for example with the `reviewThreads { isResolved }` GraphQL query and `resolveReviewThread`.
