# URL base (sub-path hosting)

Host the app under a reverse-proxy sub-path — e.g. `https://example.com/nzbdav/` —
without any `sub_filter` response rewriting in the proxy.

The setting is the `URL_BASE` value and it has **two halves that must match**:

| Half | When | What it controls |
| --- | --- | --- |
| `--build-arg URL_BASE=/nzbdav` | Docker image build | React Router basename, Vite asset paths, and the `__URL_BASE__` constant baked into the client bundle |
| `-e URL_BASE=/nzbdav` | Container runtime | The Express mount prefix, WebSocket endpoint path, and login-redirect Locations |

React Router's `basename` is build-time only — the framework exposes no runtime
override — so the sub-path cannot be changed without rebuilding the frontend.
Images built with a `URL_BASE` build-arg default the runtime env var to the same
value, so setting it once at build time is enough.

Unset (or `/`) means root hosting and produces output identical to an image
built without the arg. The official published images are built for root hosting;
sub-path deployments build their own image:

```bash
docker build -t nzbdav:subpath --build-arg URL_BASE=/nzbdav .
docker run -d -p 3000:3000 -v ./config:/config nzbdav:subpath
```

Accepted forms are normalized: `nzbdav`, `/nzbdav`, and `/nzbdav/` all mean
`/nzbdav`. Multi-segment prefixes (`/tools/nzbdav`) work.

## What moves, what stays

- The web UI, `/api`, `/webdav` proxy paths, WebSocket (`<URL_BASE>/ws`), and
  login all move under the prefix.
- `GET /healthz` stays at the bare root **and** is served under the prefix, so
  container healthchecks keep a stable URL regardless of `URL_BASE`.
- A request to the bare root `/` redirects to `<URL_BASE>/`.
- Direct backend consumers (rclone pointed at the backend port, `NZBDAV_CONFIG__…`
  env config) are unaffected — the backend itself stays prefix-unaware.
- When using OIDC behind a sub-path, register the callback as
  `<public origin><URL_BASE>/auth/oidc/callback` (derived automatically from
  `general.base-url` when set).

## Minimal nginx example

```nginx
location ^~ /nzbdav/ {
    proxy_pass http://127.0.0.1:3000;
    proxy_http_version 1.1;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;

    # WebSocket upgrade — the live queue/health panels need this.
    proxy_set_header Upgrade           $http_upgrade;
    proxy_set_header Connection        $http_connection;

    # Long-running streams (WebDAV reads of large files).
    proxy_buffering    off;
    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
}
```

No `sub_filter`, no `proxy_redirect`, no manifest rewriting: the app emits
correctly-prefixed URLs itself.
