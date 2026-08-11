# Getting started

!!! note "Docs track latest"

    These pages describe the **latest** InfiniDysk release. Features and settings marked with a [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since } pill (or similar) need at least that version — check your image tag or **Settings → About** before following a new workflow.

Choose a path based on where you are:

<div class="grid cards" markdown>

-   :material-docker:{ .lg .middle } __Install with Docker__

    ---

    Persistent Compose deploy, health checks, reverse-proxy notes.

    [:octicons-arrow-right-24: Docker](docker.md)

-   :material-swap-horizontal:{ .lg .middle } __Migrate from another build__

    ---

    Official path from nzbdav-dev `v0.6.4`, plus community forks (Pukabyte, NzbDavEx).

    [:octicons-arrow-right-24: Migration](migration.md)

-   :material-account-cog:{ .lg .middle } __First run__

    ---

    Admin account, Usenet provider, WebDAV credentials, import strategy.

    [:octicons-arrow-right-24: First run](first-run.md)

-   :material-sync:{ .lg .middle } __Connect *Arr__

    ---

    Add InfiniDysk as a SABnzbd download client in Sonarr and Radarr.

    [:octicons-arrow-right-24: Connect Radarr/Sonarr](connect-arr.md)

-   :material-map:{ .lg .middle } __Understand the flow__

    ---

    How queue → mount → stream works with media servers.

    [:octicons-arrow-right-24: Architecture](../guides/architecture.md)

</div>

## Prerequisites

1. **Usenet provider** — NNTP host, credentials, and connection allowance.
2. **Indexers** (optional for first play) — Newznab-compatible sources for search and *Arr.
3. **Docker** — Engine + Compose v2. Symlink imports also need Linux FUSE (`/dev/fuse`).

## Setup and hosting options

| Option | Who it’s for | Start here |
|--------|----------------|------------|
| **Self-hosted Docker** | You run Compose (or Unraid/etc.) and wire *Arr / rclone yourself | [Docker](docker.md) |
| **[DUMB](https://dumbarr.com/)** (Debrid Unlimited Media Bridge) | You want InfiniDysk as a **fully supported core module** with guided onboarding and Arr wiring | [InfiniDysk on dumbarr.com](https://dumbarr.com/services/core/nzbdav/) |

DUMB treats InfiniDysk as a first-class Usenet WebDAV / SABnzbd-compatible workflow service (`core_service: nzbdav`), including automatic Arr download-client and symlink-path integration when you select it during onboarding. Prefer DUMB’s own docs for stack-specific paths and ports; use this site for InfiniDysk Settings, features, and troubleshooting.

## Release channels and tags

Docker images and [prebuilt Linux archives](prebuilt-archives.md) follow the same release train. Choose a tag based on how much change you want to absorb:

| Tag | Definition |
|-----|------------|
| `latest` | The current stable release — the same definition as any project. Recommended for most deployments. |
| `lts` | A curated stable pointer that generally trails `latest` by a few minor/patch releases, for operators who prefer the front lines to do the testing. Docker image tag only; promoted manually, not on every release. |
| `rc` | Release candidate for the upcoming version, promoted from `dev` builds after basic regression testing. Preferred for all but the most daring testers. |
| `dev` | Work-in-progress snapshots deployed to the maintainer’s non-production instance to validate basic functionality. Expect a noisy tag: frequent, sometimes non-functional builds, with no per-build version numbers. Testing only. |

For reproducible deployments, pin an exact version tag (`v1.2.3`) or a rolling minor/major alias (`v1.2`, `v1`). Versioned `v*-rc.*` pre-releases and image tags are removed when the stable release ships; the rolling `rc` and `dev` tags persist.

!!! note "Building from source"

    Cloning the repository and running from source remains possible — see [Contributing](../community/contributing.md) — but it is a development workflow, not a supported deployment method. Releases are tested as the published Docker image and prebuilt Linux archives; source builds are not part of release testing.

## After install

| Goal | Next |
|------|------|
| Plex + symlinks | [Import strategies](../guides/import-strategies.md) → [Mounting WebDAV](../guides/mounting-webdav.md) |
| Emby/Jellyfin + STRM | [Import strategies](../guides/import-strategies.md) → [Media servers](../guides/media-servers.md) |
| Stremio on demand | [Stremio](../guides/stremio.md) |
| Tune Settings | [Configuration](../configuration/index.md) |
