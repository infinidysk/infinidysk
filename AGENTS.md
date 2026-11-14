# AGENTS.md

Rules for AI agents working on **infinidysk 2.x**, the from-scratch Rust rewrite on `main`. Write the name lowercase, `infinidysk`, even at the start of a sentence. Never "InfiniDysk".

## What this project is

A Usenet-backed virtual filesystem. It imports NZBs from Sonarr/Radarr through a **SABnzbd-compatible API** and serves the media through a **native FUSE mount** and **WebDAV**, streaming directly from Usenet providers without downloading files to disk.

Target: a single Rust binary (tokio, axum, rustls, `infinidysk-fuse`, sqlx on SQLite + PostgreSQL) with an embedded React + Vite SPA, shipped as a Linux x64/arm64 Docker image on `ubuntu:26.04` (glibc; no musl/Alpine). macOS is for development and benchmarking only. No Windows.

## Load on demand

These files are binding. Read each one only when the task needs it.

| When | Read |
|------|------|
| Adding or moving crates, adding cross-crate dependencies, or changing NNTP, stream, pool, permit or prefetch code | [`.agents/skills/infinidysk-crates/SKILL.md`](.agents/skills/infinidysk-crates/SKILL.md) |
| Writing a commit message, creating a PR or editing a PR title | [`.agents/skills/infinidysk-commits/SKILL.md`](.agents/skills/infinidysk-commits/SKILL.md) |
| Addressing CodeRabbit, Code Quality or code scanning findings | [`.agents/skills/infinidysk-review-triage/SKILL.md`](.agents/skills/infinidysk-review-triage/SKILL.md) |
| Adding or changing UI in `ui/` | [`DESIGN.md`](DESIGN.md) |
| Writing or reviewing Rust, when the `rust-skills` skill is installed | Its `SKILL.md` once per session (it is one file behind several symlinked paths), then only the relevant `rules/` files |

## The C# 1.x code on `v1` (specification)

The C# 1.x code is on [branch `v1`](https://github.com/infinidysk/infinidysk/tree/v1), checked out at `../infinidysk` when present. It is bug-fix only. The plan cites its files as `infinidysk:<path>`.

- Read C# source and tests as a **specification**. Write idiomatic Rust. No line-by-line ports.
- **Never modify** `v1` from a task on `main`. If it is not checked out, read it with `gh` (`?ref=v1`).
- Behavior that must stay **identical**: SAB API (`/api?mode=…`) and WebDAV paths/semantics (`/view`, `/content`, `/nzbs`, `/.ids`, `/completed-symlinks`). Everything else (schema, blob format, admin API, env vars, UI) is new.
- Starting points: `backend/Clients/Usenet/` (NNTP pool, circuit breaker), `backend/Streams/`, `backend/Queue/`, `backend/Api/SabControllers/`, `backend/WebDav/`, `backend/Database/Models/`, `backend.Benchmarks/`, `docs/reference/sabnzbd-api.md`, `docs/reference/rfc3977.md`.

## Companion repo and scratch space

Policies, plans, research, benchmark results and the maintainer workflow live in the private [`infinidysk/infinidysk-lab`](https://github.com/infinidysk/infinidysk-lab) at `../infinidysk-lab`, cited as `infinidysk-lab:<path>`. When it is present, read its `AGENTS.md` too. Contributors without access can ignore those references.

`scrap/` is gitignored scratch space for notes, logs and files that must not be committed.

**Keep long command output out of the context.** Send cargo, test, docker and benchmark output to a log under `scrap/<task>/` and read back only the tail or the failing lines (`tail -40`, `grep -E '^(error|warning)|FAILED|panicked'`). This applies to background commands too: their full output returns when they finish.

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

The maintainer's Rust rules are in the private [`hoivikaj/rust-skills`](https://github.com/hoivikaj/rust-skills) (see Load on demand). Contributors without access can ignore it. The project rules below are binding either way and override rust-skills where they differ.

- Workspace with crates under `crates/<short-name>` (folder `crates/nntp`, package `infinidysk-nntp`); each crate inherits `[workspace.package]`, sets `[lints] workspace = true`, and takes shared dependency versions from `[workspace.dependencies]`. The product binary is `crates/infinidysk`. Dependencies point downward only. Parsers do no I/O. No traits just for mocking.
- Release profile: `lto = "fat"`, `codegen-units = 1`, panic **unwind** (never `abort`; the streaming invariants need catchable callback panics).
- Clippy: `pedantic` baseline minus noisy lints plus selected nursery lints, all set in the root `Cargo.toml`. Prefer fixing over `#[allow]`; any `#[allow]` needs a one-line reason.
- Edition 2024, toolchain pinned in `rust-toolchain.toml`. Before committing: `cargo fmt --all --check`, `cargo clippy --workspace --all-targets --locked -- -D warnings`, `cargo test --workspace --locked`. CI also runs `cargo-deny` (`deny.toml` license allowlist + advisories) and native glibc release builds on Ubuntu 26.04 x64/arm64.
- Product version lives in `version.txt` (owned by release-please); crate versions stay `0.0.0`.
- Mark deliberate corner-cutting with a `ponytail:` comment naming the ceiling and the upgrade path.
- `unsafe` only in FFI crates (e.g. yEnc), which opt in with `#![allow(unsafe_code)]`; every block needs a `// SAFETY:` comment (enforced by clippy).
- No `unwrap` / `expect` outside tests and provably infallible cases (state the proof in the `expect` message).
- `thiserror` in library crates, `anyhow` only at the binary edge.
- `tracing` with structured fields. Never log secrets (provider passwords, API keys, WebDAV credentials).
- Encode streaming lifecycle invariants as types and `Drop` guards, not conventions (see `infinidysk-crates`).
- Tests use deterministic generated data and a loopback NNTP server. Never require live Usenet providers in CI.

### Performance

Playback performance has priority over memory usage. Never trade throughput or prefetch for lower memory without explicit approval. Benchmarks port the C# scenarios with the same corpus seed (1025) and metric names; Rust must match or beat the C# baselines before any release.

## Logging: stack dumps vs human-friendly events

Known failures (provider errors, missing articles, timeouts, auth failures, missing `/dev/fuse`, …) log as **one human-friendly warning** with a `reason` field and no backtrace. Full error chains/backtraces are for unexpected errors only (optionally `debug!` the chain for known failures). An unhandled stack dump for a known failure is a defect; a remediation plan must include the logging fix.

## Commits and PRs (required)

- [Conventional Commits](https://www.conventionalcommits.org/) for commits and PR titles: `<type>(<scope>): <short imperative description>`. release-please reads them.
- PR titles describe the change for **end users**; commit messages carry the technical detail.
- Non-behavior work (CI, tests, docs, `AGENTS.md`, skills, tooling) is `chore(<scope>)`; never bare `docs` / `ci` / `test`. Breaking changes use `!`. Never edit `CHANGELOG.md` by hand.
- Types, scopes and examples: `infinidysk-commits` (see Load on demand).

## Never write media titles

We cannot verify which media is public domain. **Never** write movie, show, episode, album, book, or release/NZB names in commits, PRs, issues, comments, code, test names, fixtures, docs, or pasted logs. Redact as `<release-name>` and describe generically ("stored RAR5 episode", "obfuscated 7z season pack").

## Never merge pull requests

Agents never merge PRs, in this repo or any infinidysk repo: no `gh pr merge`, no UI/API merge, no auto-merge. Opening the PR and reporting its URL ends the handoff. Never force-push shared branches or amend pushed commits.

## Addressing reviews

When you address CodeRabbit, Code Quality or code scanning findings, follow `infinidysk-review-triage` (see Load on demand). In short: reply to and resolve every thread, triage every open alert, and never dismiss a review just to bypass protection.
