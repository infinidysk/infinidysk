> [!IMPORTANT]
> This fork is designed to be a drop in replacement/upgrade from `nzbdav-dev/nzbdav v0.6.4`.
>
> Early adopters are reporting **2x network throughput** capability and a **400% reduction in seek time**.
>
> `ghcr.io/nzbdav/nzbdav:latest`

<p align="center">
  <img src="docs/assets/logo.png" width="160" alt="InfiniDysk logo" />
</p>

<h1 align="center">InfiniDysk</h1>

<p align="center"><strong>The NzbDAV SuperFork</strong></p>
<p align="center">
  Mount NZBs as a virtual filesystem and stream directly from Usenet — without downloading full media files first.
</p>

> [!NOTE]
> **NzbDAV is becoming InfiniDysk.** The new identity is here first; the repository and Docker image will move later. No deployment change is needed yet. Read the [rename FAQ](https://nzbdav.com/community/renaming-to-infinidysk/).

<img width="1024" height="601" alt="InfiniDysk overview dashboard" src="docs/assets/overview.png" />

<p align="center">
  <a href="https://github.com/nzbdav/nzbdav/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/nzbdav/nzbdav" /></a>
  <a href="https://github.com/nzbdav/nzbdav/pkgs/container/nzbdav"><img alt="Docker image" src="https://img.shields.io/badge/ghcr.io-nzbdav%2Fnzbdav-blue?logo=docker&logoColor=white" /></a>
  <a href="https://github.com/nzbdav/nzbdav/actions/workflows/ci.yml"><img alt="CI status" src="https://img.shields.io/github/actions/workflow/status/nzbdav/nzbdav/ci.yml?branch=main&label=CI" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/nzbdav/nzbdav" /></a>
</p>

---

InfiniDysk is a **WebDAV server** that mounts NZB documents as a browsable virtual filesystem — without downloading full media files first. Content streams on demand, straight from your Usenet provider.

It also exposes a **SABnzbd-compatible API**, so Sonarr, Radarr, and similar tools can use it as a drop-in download client. Combined with Plex, Emby, or Jellyfin, this lets you build an effectively infinite media library without storing the full media library on your server.

Please add feature requests and issues over on our [Issue Tracker](https://github.com/nzbdav/nzbdav/issues) or join our [Discord](https://discord.gg/DAya7W6QMa) to chat with us!

> After joining, use the channel and role selector to enable **InfiniDysk - SuperFork** for release notifications and development channels.

## Why another fork?

This project is a maintained fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). We took ownership of the full Usenet streaming stack — nzbdav, UsenetSharp, RapidYencSharp, rapidyenc, and SharpCompress — so playback, connection, and decoding fixes could land in the right layer instead of waiting on a single upstream dependency chain.

Read the full story in the [about page](https://nzbdav.com/community/about/).

## Special thanks

Special thanks to the forks and contributors whose ideas we consolidated:

* [@Nzbdav-dev](https://github.com/Nzbdav-dev)
* [@Pukabyte](https://github.com/Pukabyte)
* [@elfhosted](https://github.com/elfhosted)
* [@kha-kis](https://github.com/kha-kis)
* [@mrghxst](https://github.com/mrghxst)
* [@qooode](https://github.com/qooode)
* [@dgherman](https://github.com/dgherman)
* [@loambit](http://github.com/loambit)

## Features

* 📁 **WebDAV server**
  
  - _Host your virtual filesystem over HTTP(S)_

* ☁️ **Mount NZB documents**
  
  - _Browse NZB contents instantly, no download needed_

* 📽️ **Full streaming & seeking**
  
  - _Jump to any point in your video streams_

* 🚀 **NNTP article pipelining**
  
  - _Optional pipelined article fetches for higher throughput and faster seeks_

* 🗃️ **Archive streaming**
  
  - _View, stream, and seek inside RAR and 7z archives_

* 🔓 **Password-protected archives**
  
  - _Stream encrypted content transparently_

* 🔀 **Multiple Usenet providers**
  
  - _Automatic failover with per-provider circuit breakers_

* 📊 **Live operations dashboard**
  
  - _Throughput, latency, errors, active reads, provider usage, failover saves, and indexer activity_

* 🧭 **Provider routing and limits**
  
  - _Cascade priorities, per-provider data caps, usage resets, and connection benchmarking_

* 🔎 **Built-in indexer search**
  
  - _Configure Newznab indexers, track API usage, search them manually, and mount results_

* 🚫 **Search exclude filters**
  
  - _Manual regex excludes plus auto-synced remote lists (e.g. TRaSH) with refresh status_

* 🎛️ **Search profiles and adapters**
  
  - _Expose selected indexers through token-scoped Addon, Newznab, and JSON APIs_

* 🐕 **Watchdog playback failover**
  
  - _Verify candidates, retry failed releases, and inspect each playback attempt_

* 🛡️ **Warden dead-release ledger**
  
  - _Remember unavailable releases, combine trusted remote ledgers, and import, export, or back up the data_

* 📡 **Watchtower proactive resolution**
  
  - _Keep wanted movies and episodes mapped to verified releases before playback_

* 📜 **Live log viewer**
  
  - _Filter, follow, and download backend logs from the admin UI_

* 🗂️ **WebDAV management**
  
  - _Browse, download, and delete eligible virtual filesystem items from the UI_

* 💙 **Health checks & optional repairs**
  
  - _Monitor content health and trigger replacements through Radarr/Sonarr when configured_

* 🧩 **SABnzbd-compatible API**
  
  - _Drop-in replacement for SABnzbd_

* 🙌 **Sonarr/Radarr integration**
  
  - _Import through Rclone symlinks or lightweight STRM files_

## Quick start

InfiniDysk ships as a single Docker image. Use the `latest` tag for the newest release, or `lts` for a slower, more conservative release cycle that lags one feature release behind `latest`. To try it out:

```bash
docker run --rm -it -p 3000:3000 ghcr.io/nzbdav/nzbdav:latest
```

This trial command is ephemeral: its settings are discarded when the container exits.

For a persistent setup, use Docker Compose:

```yaml
services:
  nzbdav:
    image: ghcr.io/nzbdav/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    ports:
      - "3000:3000"
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: Etc/UTC
    volumes:
      - ./config:/config
```

Then open `http://localhost:3000`, create your admin account, and head to the **Settings** page to configure your Usenet provider:

> [!IMPORTANT]
> Port `3000` serves plain HTTP. If InfiniDysk will be reachable outside your trusted network, put it behind an HTTPS reverse proxy and do not expose the container port directly to the internet. WebDAV uses Basic authentication, so TLS is essential for remote access. When the proxy runs on the Docker host, bind the port to localhost with `127.0.0.1:3000:3000`.

You'll also want to set a username and password for the WebDAV server itself.

## Documentation

Full documentation is published at [nzbdav.com](https://nzbdav.com/).

The [about page](https://nzbdav.com/community/about/) covers project heritage and the managed library ecosystem (UsenetSharp, RapidYencSharp, rapidyenc, SharpCompress).

Start with the [getting started guide](https://nzbdav.com/getting-started/) for a full production deployment:

* **Docker Compose** — persistent deployment, container health checks, and updates
* **Migration** — [official path from nzbdav-dev v0.6.4](https://nzbdav.com/getting-started/migration/) and community forks (Pukabyte, NzbDavEx)
* **Import strategies** — Rclone symlinks for Plex or STRM files for Emby/Jellyfin
* **Performance tuning** — benchmarking WebDAV connection limits
* **Integrations** — automating Radarr/Sonarr queue management and repairs
* **Stremio** — streaming Usenet on demand via AIOStreams
* **Search profiles** — token-scoped Newznab, Addon, and JSON adapter setup
* **Watchtower** — proactive wanted-list resolution in the [Watchtower guide](https://nzbdav.com/features/watchtower/)
* **Configuration** — [Settings walkthrough](https://nzbdav.com/configuration/) and [environment variables](https://nzbdav.com/configuration/environment-variables/)
* **Compare** — [InfiniDysk vs AltMount vs classic download clients](https://nzbdav.com/guides/compare/)

## Development

The project consists of a .NET backend (WebDAV, Usenet streaming, SAB API) and a React Router frontend (admin UI). See [CONTRIBUTING.md](CONTRIBUTING.md) for local development setup and [CHANGELOG.md](CHANGELOG.md) for release history. Source for the published docs lives in [`docs/`](docs/).

## License

InfiniDysk is released under the [MIT License](LICENSE).

> [!NOTE]
> InfiniDysk is intended for use with legally obtained or public domain content only. The project maintainers do not condone piracy and will not provide support for users suspected of engaging in copyright infringement.
