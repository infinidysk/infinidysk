# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Self-hosters running a Sonarr/Radarr plus Plex/Jellyfin stack in Docker, often upgrading from infinidysk 1.x. They open the UI to add providers and Arr instances, watch imports and playback, and troubleshoot when a read stalls or an import fails. Most visits are short check-ins; long visits happen while debugging.

## Product Purpose

infinidysk is a Usenet-backed virtual filesystem. It takes NZBs from Arr instances through a SABnzbd-compatible API and serves the media through a native FUSE mount and WebDAV, streaming from Usenet providers instead of downloading files to disk. Success is a library that plays like local files, imports that the Arrs accept without special handling, and problems that the user can find and fix from the UI.

## Positioning

- No download: the library streams straight from Usenet providers.
- Built-in FUSE mount in one Rust binary; no rclone sidecar.
- Drop-in SABnzbd replacement: Arrs add it as a SABnzbd download client, no plugin.
- Health checks and PAR2 repair keep files playable over time.
- Playback performance comes first, ahead of memory usage.

## Operating Context

- Runs as a Linux x64/arm64 Docker container; the UI, SABnzbd API and WebDAV share one port.
- Arr instances, media servers and other containers read the mount at the same host path.
- Users reach the UI from desktop and phone browsers, often on a home network or behind a reverse proxy.
- Troubleshooting relies on the Logs page, playback traces with secret-free exports, and the 1.x import report.

## Capabilities and Constraints

- Areas: Overview, Queue (imports and history), Search, Explorer (library tree and media analysis), Health (checks, repair, schedule), Stats, Traces, System (logs), Settings (providers, Arr instances, indexers, library, mount, performance, network).
- SABnzbd API (`/api?mode=…`) and WebDAV paths (`/view`, `/content`, `/nzbs`, `/.ids`, `/completed-symlinks`) must behave exactly like 1.x; everything else is new in 2.0.
- 2.0 is an alpha for early testers. Not yet available: global bandwidth limit, global connection cap.
- Terminology: "unowned" and "unlinked" files, never "orphan". Providers come from a managed catalog and share a backbone; there is no storage-group setting. Provider priority levels are pools; cascade routing is always on.
- Arr configuration is one list of instances, each with a type profile; never per-Arr lists.
- Data sizes and speeds use decimal units (KB, MB, GB, TB; MB/s), never MiB/GiB or bits. SABnzbd API fields keep SABnzbd's meaning.

## Brand Commitments

- Name: infinidysk, written lowercase everywhere (UI, docs, code, commits, PRs), even at the start of a sentence.
- Voice: plain, direct, and specific; say what happened and what to do next.
- Never write movie, show, episode, album, book or release names anywhere; describe files generically (for example "stored RAR5 episode").
- Never name competing or upstream-fork projects.

## Evidence on Hand

- README.md: install, upgrade from 1.x, rclone migration and troubleshooting guidance.
- infinidysk-lab:docs/ (private): health, import, maintenance, restart-behavior and STRM policies.
- crates/bench: benchmark scenarios matched to 1.x baselines; no published performance figures yet.
- No testimonials, user counts, case studies or press exist. Do not invent them.

## Product Principles

1. SABnzbd is the truthstone: if SABnzbd accepts an NZB, so does infinidysk.
2. Playback first: never trade throughput or prefetch for lower memory.
3. Self-explaining failures: a known failure shows one clear reason and next step, not a stack dump.
4. Opinionated defaults over toggles: managed provider catalog, always-on cascade routing, one global cache layer.
5. Safe upgrades: 1.x data is read-only, imports are reported, and old formats retire on a stated schedule.

## Accessibility & Inclusion

Every page must stay usable on a 360 px wide phone screen.
