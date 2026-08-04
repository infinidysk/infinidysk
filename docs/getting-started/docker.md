# Docker

InfiniDysk ships as a single multi-arch image: `ghcr.io/nzbdav/nzbdav`. The container runs the frontend (public port `3000`) and backend (internal `8080`).

!!! tip "Hosted with DUMB"

    Prefer a batteries-included Arr stack? InfiniDysk is a **fully supported core module** in [Debrid Unlimited Media Bridge (DUMB)](https://dumbarr.com/) — see the [InfiniDysk service guide](https://dumbarr.com/services/core/nzbdav/) and [setup options](index.md#setup-and-hosting-options).

!!! tip "IPv6-only hosts"

    `ghcr.io` is not reachable over IPv6. Use the Docker Hub mirror `nzbdav/nzbdav` instead.

## Persistent Compose

```yaml
services:
  nzbdav:
    image: ghcr.io/nzbdav/nzbdav:latest
    container_name: nzbdav
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "curl -fsSL http://localhost:3000/healthz > /dev/null || exit 1"]
      interval: 30s
      retries: 3
      start_period: 60s
      timeout: 5s
    ports:
      - "3000:3000"
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: America/New_York
    volumes:
      - ./config:/config
      - /mnt:/mnt
```

```bash
docker compose up -d
```

Set `PUID`/`PGID` from `id` on the host. Map `/mnt` (or your media paths) so completed downloads and library folders share paths with *Arr and media servers.

!!! tip "Headless Settings [since 0.9.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0){ .nzbdav-since }"

    Prefer infrastructure-as-code for Usenet, WebDAV, *Arr, and other Settings? Use authoritative
    [`NZBDAV_CONFIG__...`](../configuration/headless.md) variables — see the
    [fully hydrated Compose example](../configuration/headless.md#fully-hydrated-compose-example).
    Process variables on this page (`PUID`, `TZ`, …) stay separate.

## Change the published port

Compose port mappings use `HOST_PORT:CONTAINER_PORT`. To open InfiniDysk on port
`3001` without changing the ports inside the container:

```yaml
ports:
  - "3001:3000"
```

Open `http://your-server:3001`. Keep the container-side port at `3000`, including
the Compose healthcheck at `http://localhost:3000/healthz`. A mapping such as
`3001:3001` does not work unless the frontend is separately configured to listen
on `3001`.

!!! tip

    For ordinary bridge-networked Docker and Compose deployments, changing the
    host side of the port mapping is the recommended approach. Do not set `PORT`
    just to avoid a host port conflict.

### Host networking and internal port conflicts

With `network_mode: host`, there is no port mapping layer. Set `PORT` if the
frontend's port conflicts with another host service:

```yaml
network_mode: host
environment:
  PORT: "3001"
```

The backend remains on `8080`. If that port also conflicts, its listen URL and
the frontend's backend URL must change together:

```yaml
network_mode: host
environment:
  PORT: "3001"
  ASPNETCORE_URLS: "http://127.0.0.1:8081"
  BACKEND_URL: "http://127.0.0.1:8081"
```

Update the Compose healthcheck to use the effective frontend port
(`http://localhost:3001/healthz`) and update **Settings → General → Base URL**
when generated links should use the new public port.

!!! note "DUMB deployments"

    DUMB builds and launches InfiniDysk directly rather than using this image's
    entrypoint. Change DUMB's `nzbdav.frontend_port` and
    `nzbdav.backend_port` settings instead; DUMB supplies the corresponding
    process environment automatically. See the
    [DUMB InfiniDysk service guide](https://dumbarr.com/services/core/nzbdav/#configuration-in-dumb_configjson).

## What the image does

1. Starts the frontend so maintenance progress can be shown.
2. Runs database migration / restore swap (`--db-migration`).
3. Starts the backend and waits for `/health` before considering startup complete.

Long one-time maintenance does not mark the Compose healthcheck unhealthy — it targets frontend `/healthz`.

## Reverse proxy

!!! warning "TLS and WebSockets"

    - Prefer HTTPS in front of port `3000`. Do not expose plain HTTP to the internet.
    - Bind `127.0.0.1:3000:3000` when the proxy is on the Docker host.
    - Allow HTTP Upgrade on **same-origin** `/ws` (Overview/Queue live updates).
    - Set `SECURE_COOKIES=true` when the UI is HTTPS-only.
    - Set **Base URL** in Settings (or `TRUST_PROXY=1` so forwarded headers rewrite correctly) for STRM/adapter absolute URLs.
    - For `addurl` to Docker-internal indexers, configure [Trusted local hosts](../configuration/sabnzbd.md) or `TRUSTED_INTERNAL_HOSTS` [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since }.

## Optional environment

| Variable | Purpose |
|----------|---------|
| `TRUST_PROXY=1` | Honor proxy `X-Forwarded-*` when rewriting scheme/host |
| `TRUSTED_PROXY_CIDRS` | Widen backend proxy trust (split-container) |
| `TRUSTED_INTERNAL_HOSTS` [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since } | Allowlist for private `addurl` targets |
| `SESSION_KEY` | Stable session secret (else persisted under `/config`) |
| `THREADPOOL_MIN_THREADS` / `THREADPOOL_MAX_THREADS` | Memory-constrained hosts |

Full list: [Environment variables](../configuration/environment-variables.md).

## Next

[First run](first-run.md) — admin account, providers, WebDAV, import strategy.
