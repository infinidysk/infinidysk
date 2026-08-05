<p align="center">
  <img src="docs/assets/logo.png" width="160" alt="InfiniDysk logo" />
</p>

<h1 align="center">InfiniDysk</h1>

<p align="center"><strong>The NzbDAV SuperFork</strong></p>
<p align="center">
  Stream NZBs directly from Usenet through a virtual filesystem — without downloading full media files first.
</p>

> [!NOTE]
> **NzbDAV is becoming InfiniDysk.** The documentation has moved to the new domain; the repository and Docker image will move later. No deployment change is needed yet. Read the [rename FAQ](https://www.infinidysk.com/community/renaming-to-infinidysk/).

<img width="1024" height="601" alt="InfiniDysk overview dashboard" src="docs/assets/overview.png" />

<p align="center">
  <a href="https://github.com/nzbdav/nzbdav/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/nzbdav/nzbdav" /></a>
  <a href="https://github.com/nzbdav/nzbdav/pkgs/container/nzbdav"><img alt="Docker image" src="https://img.shields.io/badge/ghcr.io-nzbdav%2Fnzbdav-blue?logo=docker&logoColor=white" /></a>
  <a href="https://github.com/nzbdav/nzbdav/actions/workflows/ci.yml"><img alt="CI status" src="https://img.shields.io/github/actions/workflow/status/nzbdav/nzbdav/ci.yml?branch=main&label=CI" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/nzbdav/nzbdav" /></a>
</p>

---

InfiniDysk combines a **WebDAV server** with a **SABnzbd-compatible API**. Sonarr, Radarr, and similar tools can use it as a drop-in download client, while Plex, Emby, Jellyfin, and other WebDAV clients stream content on demand from your Usenet providers.

> [!IMPORTANT]
> InfiniDysk is designed as a drop-in replacement and upgrade from `nzbdav-dev/nzbdav v0.6.4`. The current image is `ghcr.io/nzbdav/nzbdav:latest`; follow the [migration guide](https://www.infinidysk.com/getting-started/migration/) before switching an existing installation.

Please add feature requests and bug reports to the [issue tracker](https://github.com/nzbdav/nzbdav/issues), or join our [Discord](https://discord.gg/DAya7W6QMa) to chat with us.

> After joining, use the channel and role selector to enable **InfiniDysk - SuperFork** for release notifications and development channels.

## Features

### Streaming and providers

- **Virtual WebDAV filesystem** — Mount, browse, stream, and seek through NZB content without downloading complete media files first.
- **Archive streaming** — Read RAR and 7z archives on demand, including password-protected content.
- **Fast, resilient NNTP** — Pipeline article requests and cascade across multiple providers with automatic failover and circuit breakers.
- **Provider routing and limits** — Set provider priorities, data caps, usage resets, and connection limits, then benchmark them from the UI.
- **Container-aware gap handling** — Preserve MPEG-TS timing across missing article ranges to reduce playback desynchronization.

### Search and playback readiness

- **Built-in Newznab search** — Configure indexers, monitor API usage, search manually, and mount results.
- **Filters and adapters** — Apply regex or remote exclude lists and expose selected indexers through token-scoped Addon, Newznab, and JSON APIs.
- **Watchdog, Preflight, and Warden** — Verify candidates, warm likely results, retry failed releases, and remember known-dead releases.
- **Watchtower** — Keep wanted movies and episodes mapped to verified releases before playback begins.

### Automation and library management

- **SABnzbd-compatible queue** — Use add, queue, history, pause, resume, and speed-limit operations with configurable concurrent workers.
- **Sonarr and Radarr integration** — Import through Rclone symlinks or lightweight STRM files and optionally repair unhealthy content.
- **WebDAV management** — Browse, download, and delete eligible virtual filesystem items from the admin UI.
- **[Experimental AltMount migration](https://www.infinidysk.com/guides/altmount-migration/)** — Rebuild and import an existing AltMount library through a guided wizard that leaves its source metadata untouched.

### Operations and deployment

- **Live operations dashboard** — Track throughput, latency, errors, active reads, provider usage, failover saves, and indexer activity.
- **[Logs and diagnostics](https://www.infinidysk.com/configuration/support/)** — Filter and download logs, enable stream tracing from the UI, and generate redacted technical support packs.
- **[Backup and restore](https://www.infinidysk.com/configuration/backup/)** — Schedule database backups and download, upload, or restore them from the Settings UI.
- **[Headless configuration](https://www.infinidysk.com/configuration/headless/)** — Provision authoritative settings with `NZBDAV_CONFIG__...` environment variables.
- **[OIDC / SSO](https://www.infinidysk.com/configuration/oidc/)** — Authenticate browser sessions through standards-compliant identity providers such as Authentik, Authelia, and Keycloak.
- **Flexible hosting** — Run with Docker, prebuilt Linux archives, or DUMB, and build for reverse-proxy sub-path hosting when needed.

## Quick start

InfiniDysk ships as a single multi-architecture Docker image. Use `latest` for the newest stable release. The `lts` tag is a manually curated long-term-support pointer and may lag behind `latest`.

To try it without keeping any settings:

```bash
docker run --rm -it -p 3000:3000 ghcr.io/nzbdav/nzbdav:latest
```

This trial command is ephemeral: its settings are discarded when the container exits.

For a persistent setup, save the following as `compose.yml`:

```yaml
services:
  nzbdav:
    image: ghcr.io/nzbdav/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    healthcheck:
      test:
        - CMD-SHELL
        - curl -fsSL http://localhost:3000/healthz > /dev/null || exit 1
      interval: 30s
      retries: 3
      start_period: 60s
      timeout: 5s
    ports:
      - "3000:3000"
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: Etc/UTC
    volumes:
      - ./config:/config
```

Then run `docker compose up -d`, open `http://localhost:3000`, create your admin account, and configure your Usenet provider and WebDAV credentials under **Settings**.

> [!IMPORTANT]
> Port `3000` serves plain HTTP. If InfiniDysk will be reachable outside your trusted network, put it behind an HTTPS reverse proxy and do not expose the container port directly to the internet. WebDAV uses Basic authentication, so TLS is essential for remote access. When the proxy runs on the Docker host, bind the port to localhost with `127.0.0.1:3000:3000`.

Other supported setup paths:

- **Linux without Docker** — Follow the [prebuilt archive guide](https://www.infinidysk.com/getting-started/prebuilt-archives/) to run the x64 or ARM64 release bundle.
- **IPv6-only Docker hosts** — Use the Docker Hub mirror, `nzbdav/nzbdav`, because `ghcr.io` is not reachable over IPv6.
- **Batteries-included Arr stack** — Use InfiniDysk as a supported core module in [DUMB](https://dumbarr.com/services/core/nzbdav/).
- **Reverse-proxy sub-path** — Follow the [URL base guide](https://www.infinidysk.com/configuration/url-base/) to build an image for paths such as `/nzbdav`.

## Documentation

Full documentation is published at [www.infinidysk.com](https://www.infinidysk.com/). Start with the [getting started guide](https://www.infinidysk.com/getting-started/) for a production deployment.

- **Install and migrate** — Docker Compose, prebuilt Linux archives, first-run setup, upgrades from nzbdav-dev and community forks, and the [experimental AltMount migration](https://www.infinidysk.com/guides/altmount-migration/).
- **Configure** — Settings walkthroughs, [headless environment configuration](https://www.infinidysk.com/configuration/headless/), OIDC, backup and restore, URL base, and provider tuning.
- **Integrate** — Sonarr/Radarr automation, Rclone symlinks, STRM files, Plex, Emby, Jellyfin, and Stremio through AIOStreams.
- **Search and prepare** — Indexers, token-scoped search profiles, Watchdog, Preflight, Warden, and [Watchtower](https://www.infinidysk.com/features/watchtower/).
- **Operate and troubleshoot** — Health checks and repairs, live logs, stream tracing, support packs, and performance tuning.
- **Compare** — [InfiniDysk, AltMount, and classic download clients](https://www.infinidysk.com/guides/compare/).

## Why another fork?

This project is a maintained fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). We took ownership of the full Usenet streaming stack — NzbDAV, UsenetSharp, RapidYencSharp, rapidyenc, and SharpCompress — so playback, connection, archive, and decoding fixes can land in the right layer.

Read the full story on the [about page](https://www.infinidysk.com/community/about/).

### Special thanks

Special thanks to the forks and contributors whose ideas we consolidated:

- [@Nzbdav-dev](https://github.com/Nzbdav-dev)
- [@Pukabyte](https://github.com/Pukabyte)
- [@elfhosted](https://github.com/elfhosted)
- [@kha-kis](https://github.com/kha-kis)
- [@mrghxst](https://github.com/mrghxst)
- [@qooode](https://github.com/qooode)
- [@dgherman](https://github.com/dgherman)
- [@loambit](https://github.com/loambit)

## Development

The project consists of a .NET 10 backend (WebDAV, Usenet streaming, SAB API) and a React Router 7 frontend (admin UI). See [CONTRIBUTING.md](CONTRIBUTING.md) for local development setup and [CHANGELOG.md](CHANGELOG.md) for release history. Source for the published documentation lives in [`docs/`](docs/).

## License

InfiniDysk is released under the [MIT License](LICENSE).

> [!NOTE]
> InfiniDysk is intended for use with legally obtained or public domain content only. The project maintainers do not condone piracy and will not provide support for users suspected of engaging in copyright infringement.
