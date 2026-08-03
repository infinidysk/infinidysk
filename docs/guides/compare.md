# Compare NzbDAV to alternatives

This is an honest orientation for people choosing how to get Usenet content into a media library or player. Categories evolve quickly — verify current capabilities in each project's docs before you migrate.

NzbDAV is intended for **legally obtained or public domain** content only.

## At a glance

| | **NzbDAV** | **[AltMount](https://github.com/javi11/altmount)** | **SABnzbd / NZBGet** (classic) |
|--|------------|---------------------------------------------------|--------------------------------|
| Model | Stream on demand via WebDAV | Stream on demand via WebDAV | Download to disk, then play |
| Local media storage | Segment maps / blobs — not full files | Metadata-oriented; streams from Usenet | Full release on disk |
| *Arr download client | SABnzbd-compatible API | SABnzbd-compatible API | Native |
| Import styles | Symlinks (rclone) **or** STRM | WebDAV + rclone / built-in mount | Move/copy/hardlink from complete folder |
| Stack | .NET + React admin UI | Go (+ built-in rclone/FUSE options) | Python / C++ download clients |
| Best when | You want a deep ops UI, automation (Watchtower/Warden/Watchdog), STRM without FUSE, owned streaming libraries | You want a lean Go binary, built-in mount/rclone, built-in Stremio addon | You want offline PAR2 repair, portable files, and the traditional Usenet workflow |

Upstream [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) is the project this tree forked from. NzbDAV continues that model while owning the UsenetSharp / RapidYenc / SharpCompress stack in-tree. Treat community forks such as [Pukabyte/nzbdav](https://github.com/Pukabyte/nzbdav) and [NzbDavEx](https://github.com/qooode/nzbdavex) as siblings in the same idea-space — and see [Migration paths](../getting-started/migration.md) if you are moving an existing `/config` to this build.

## Feature comparison

Capabilities below reflect publicly documented intent. “Partial” means available with extra tooling or a subset of the classic experience.

| Capability | NzbDAV | AltMount | Classic SAB/NZBGet |
|------------|:------:|:--------:|:------------------:|
| WebDAV virtual filesystem | Yes | Yes | No (local disk) |
| HTTP range seek / scrub | Yes | Yes | After download |
| RAR / 7z streaming | Yes | Yes | After extract |
| Passworded archives | Yes | Yes | After extract |
| Multi-provider + failover | Yes | Yes | Via SAB servers / scripts |
| SABnzbd API for *Arr | Yes (subset) | Yes (subset) | Full SAB surface |
| Plex-style symlink library | Yes (rclone sidecar) | Yes (rclone / built-in mount) | Native paths |
| Emby/Jellyfin STRM without FUSE | Yes | Partial / different path | N/A |
| Built-in FUSE / OS mount | Via rclone sidecar | Built-in options | N/A |
| Built-in Stremio addon | Via AIOStreams / adapters | Built-in | No |
| Indexer search + profiles | Yes | Via Prowlarr / addon flows | Via *Arr / Prowlarr |
| Proactive list warm (Watchtower) | Yes | — | — |
| Dead-release ledger (Warden) | Yes | — | — |
| Playback failover (Watchdog) | Yes | Health/repair oriented | Re-grab via *Arr |
| Live ops dashboard / logs UI | Yes | Web UI | SAB/NZBGet UI |
| Full PAR2 repair offline | Limited (health/repair paths) | Health monitoring | Strength of classic clients |
| Disk footprint for “library” | Minimal | Minimal | Large |

## Who should pick what?

### *Arr + Plex “infinite library”

**NzbDAV or AltMount** both fit. Prefer **NzbDAV** if you want STRM for Emby/Jellyfin without FUSE, richer queue automation (stuck-item rules, Watchtower/Warden), or the admin Overview/queue tooling in this project. Prefer **AltMount** if you want fewer moving parts around mounting (built-in rclone/FUSE) and a Go single-service feel.

Classic **SABnzbd** still wins if you need bulletproof offline PAR2 repair and are fine storing full media.

### Emby / Jellyfin

**NzbDAV**’s STRM import strategy is aimed at this audience. AltMount and rclone mounts also work if you prefer a filesystem-shaped library. Classic SAB is fine if you keep completed files locally.

### Stremio / on-demand play

**AltMount** documents a built-in Stremio addon with Prowlarr search. **NzbDAV** pairs with [AIOStreams](stremio.md) and optional Newznab/Addon/JSON [search profiles](../configuration/profiles.md). Classic SAB is a poor fit for pure on-demand streaming.

### Operators who already live in SABnzbd

Stay on **SABnzbd/NZBGet** until you outgrow disk or want instant play. Migrating to NzbDAV or AltMount means accepting a streaming reliability model (providers, retention, health checks) instead of “file is complete on disk.”

### Contributors / stack ownership

**NzbDAV** maintains the .NET Usenet streaming libraries used at runtime. That matters if you care about upstream decode/connection fixes landing with the app. AltMount’s Go stack is independently maintained — a healthy alternative ecosystem, not a drop-in of the same code.

## Honest caveats (all streamers)

- Streaming depends on provider retention, article availability, and your connection budget. Missing articles are a product class problem for NzbDAV and AltMount alike.
- WebDAV + media servers need careful reverse-proxy, auth, and path design. Misconfigured mounts look like “random” stalls or library thrash.
- Neither streaming server replaces the legal requirement to only access content you are entitled to use.

## Related reading

- [Architecture](architecture.md) — how NzbDAV fits *Arr and players
- [Import strategies](import-strategies.md) — symlink vs STRM
- [Getting started](../getting-started/index.md)

External docs: [AltMount](https://javi11.github.io/altmount/) · [SABnzbd](https://sabnzbd.org/)
