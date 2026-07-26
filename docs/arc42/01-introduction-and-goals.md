# 1. Introduction and Goals

## 1.1 Requirements overview

NzbDav is a WebDAV server that mounts NZB (Usenet) documents as a virtual, streamable file system
without pre-downloading their content to disk. It exposes a SABnzbd-compatible REST API so that
Sonarr/Radarr can use it as a drop-in download client, and integrates with Rclone for local mounting
and with Jellyfin/Plex-style media servers for direct streaming.

Core capabilities (from `README.md` and `CLAUDE.md`):

- Host a virtual file system over WebDAV (HTTP/HTTPS), backed by a SQLite database rather than a
  real filesystem — no local storage cost proportional to library size.
- Mount and browse NZB documents; stream and seek arbitrary byte ranges of the underlying content
  directly from a Usenet provider, including content inside RAR/7z archives and password-protected
  archives.
- Act as a SABnzbd-API-compatible download client for Sonarr/Radarr, including obfuscated-release
  deobfuscation (via PAR2 metadata) and post-processing (blocklist filtering, dedup renaming,
  `.strm` creation, import validation).
- Self-heal: detect content removed from the Usenet provider and re-fetch/repair it.
- Provide a web UI for configuration, queue/history inspection, and exploring the virtual filesystem.

## 1.2 Quality goals

The user commissioning this document gave two explicit, standing constraints that override
everything else in this analysis:

1. **Must run locally in Docker** — specifically as a single container on a homelab-style host, not
   a cluster or managed cloud environment. No alternative may implicitly assume elastic compute,
   a managed database, or a service mesh.
2. **Must be as performant as possible** — for a streaming/seeking media application, this is
   dominated by *latency to first byte* on seeks and *sustained throughput* under concurrent
   streams, not raw request count. See §10 for this turned into measurable scenarios (QS-1..QS-8) —
   "performant" is otherwise unrankable, and every optimization/alternative in this document is
   scored against those scenarios specifically to avoid producing an unranked wish list.

Ranked quality goals derived from the above and from the system's purpose:

| Rank | Quality goal | Motivation |
|------|-------------|------------|
| 1 | Low seek/start latency for streaming playback | This is the product's core value proposition — an "infinite" media library that streams at "maxed-out speeds" (README). Any regression here is user-visible immediately (buffering). |
| 2 | Resource-efficient on a single homelab host | Deployment target explicitly excludes horizontal scaling; the app competes for CPU/RAM with Jellyfin/Plex/Sonarr/Radarr on the same box. |
| 3 | Operational simplicity (single-command deploy, no external services) | Target audience is self-hosters; adding a required Postgres/Redis/broker service is a real adoption cost, not a rounding error. |
| 4 | Resilience to Usenet provider flakiness | Multi-provider failover and retry/backoff already exist in the codebase (`Clients/Usenet/`, `Queue/QueueManager`) — this is a design goal that predates this document. |
| 5 | Maintainability of a forked upstream codebase | This repo tracks `nzbdav-dev/nzbdav` upstream (415 commits) with a thin layer of fork-specific features on top (see §9). Diverging further from upstream has a real, ongoing cost: lost ability to merge upstream fixes. |

## 1.3 Stakeholders

| Role | Concern |
|------|---------|
| Self-hoster / end user | Wants Sonarr/Radarr + Jellyfin/Plex to "just work" against their Usenet provider, streaming without local storage, with minimal setup (single Docker container). |
| Fork maintainer (habenspass) | Wants to add fork-specific features (usage stats, predictive prefetch) without making it painful to keep pulling upstream fixes/features. |
| Upstream project (nzbdav-dev) | Owns the majority of the current architecture; not a direct stakeholder in this fork's decisions, but a constraint on them (see §9). |
| Sonarr/Radarr (integrating software) | Expects strict SABnzbd API compatibility — this is a hard external contract, not something this project can freely redesign. |
| Usenet providers | External NNTP servers of varying reliability/rate limits — the system must be a well-behaved, resilient client, not assume a single always-available provider. |
