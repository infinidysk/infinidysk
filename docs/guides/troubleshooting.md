# Troubleshooting

## Container unhealthy / won't start

- Check `docker logs nzbdav` for migration or backend health failures.
- Ensure `CONFIG_PATH` (`/config`) exists as a directory and is writable by `PUID`/`PGID`. Startup now fails before migrations with a message that names the path and expected `PUID`/`PGID` instead of a later SQLite/EF error.
- `/config/session.key` is the frontend cookie-signing secret and is mode `0600`. It must be owned by `PUID`/`PGID`; supervisors that chown only selected config files must include it. Fix that file directly rather than recursively chowning `blobs/`.
- A `/config` path inside the image is not proof a persistent volume is mounted. Confirm the Compose `volumes:` mapping on the host.
- Frontend `/healthz` should pass during long migrations; backend `/health` must eventually succeed.

## Locked out of the web UI

If you forgot the administrator username or password, reset the local admin account
with the `RESET_ADMIN_PASSWORD` environment variable:

1. Add `RESET_ADMIN_PASSWORD: "true"` to your Compose `environment` (or pass
   `-e RESET_ADMIN_PASSWORD=true` to `docker run`).
2. Restart the container.
3. Visit the UI — you will land on the onboarding page to set new credentials.
4. **Remove** `RESET_ADMIN_PASSWORD` from your environment.
5. Restart again. If you skip this step, the next restart deletes the admin
   account again.

While `RESET_ADMIN_PASSWORD` remains set, the UI shows a persistent warning banner
and the backend logs a matching warning on every startup.

!!! danger "Security"

    Anyone who can reach the UI while no admin account exists can create the new
    administrator account. Re-register promptly after the reset and remove the
    variable before the next restart.

### Manual reset (without restarting)

If you have shell access to `/config` and prefer not to restart:

```bash
sqlite3 "${CONFIG_PATH:-/config}/db.sqlite" "DELETE FROM Accounts WHERE Type = 1;"
```

Then visit the UI and complete onboarding. Queue, history, settings, and WebDAV
credentials are untouched.

## Sign-in fails with "The sign-in request could not be verified"

The login form posts to the same origin the page was loaded from. The request is rejected with
this message when the browser's `Origin` header does not match the host InfiniDysk believes it
is serving, and the frontend logs a throttled
`Action request origin rejected. Request URL: …, Origin: …` warning.

- **Behind a reverse proxy:** the container sees its internal `Host` (for example
  `nzbdav:3000`) while the browser sends the public origin. Either enable
  **Settings → General → Trust reverse-proxy headers** (or set `TRUST_PROXY=1`) and have the
  proxy send `X-Forwarded-Host` and `X-Forwarded-Proto`, or set **Base URL** to the public
  HTTPS address.
- **Direct access (no proxy):** Base URL and Trust reverse-proxy headers do not restrict direct
  sign-in; `http://<lan-ip>:3000` keeps working alongside the proxied address. If direct sign-in
  still fails, something between the browser and the container is injecting `X-Forwarded-*`
  headers (tunnels, NAS app portals, ingress controllers). Route those connections through the
  proxy that sets correct headers, or disable proxy trust for them.
- **Cross-site requests:** a form submitted from a different origin is rejected on purpose
  (CSRF protection).

## Streaming readiness (`/ready`) [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since }

The backend readiness endpoint reports whether InfiniDysk can make progress on new streams. It returns
`503 Service Unavailable` when Article RAM remains at least 90% leased with no active reads for 30
seconds. A high Article RAM value while reads are active is normal backpressure and remains ready.

`/ready` is separate from the cheap liveness endpoints (`/health` on the backend and `/healthz` on
the frontend). The default container healthcheck stays on `/healthz`, so temporary streaming load
does not trigger restarts. To opt into readiness for routing or monitoring, probe the backend port:

```yaml
healthcheck:
  test: ["CMD-SHELL", "curl -fsSL http://localhost:8080/ready > /dev/null || exit 1"]
  interval: 30s
  timeout: 5s
  retries: 3
  start_period: 60s
```

Use `/ready` as a restart trigger only if restarting a streaming-wedged container is the intended
policy. This check detects a stuck in-flight article budget; it does not test provider connectivity.

## Queue coordinator liveness (`/health`) [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

The backend `/health` endpoint reports `503 Service Unavailable` if the queue coordinator fails or
exits unexpectedly. The backend also exits with a nonzero code, allowing the documented Compose
`restart: unless-stopped` policy to restart the container automatically. Previously, the SAB API and
health endpoint could continue responding while queued items no longer progressed.

`/ready` continues to report streaming admission readiness. The default frontend `/healthz` remains
a lightweight process endpoint; queue-coordinator recovery does not depend on a healthcheck watcher
because the backend process exits on failure.

## WebDAV or playback fails

- Confirm WebDAV username/password.
- Behind a proxy: TLS, `/ws` Upgrade, `SECURE_COOKIES`, Base URL / `TRUST_PROXY`.
- Overview **Active Reads**: unexpected traffic → rclone VFS or media-server scans.
- Try disabling segment cache or adjusting Max Download Connections — [WebDAV](../configuration/webdav.md).

## Activity totals [since 1.4.3](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.3){ .nzbdav-since }

The Overview Activity summary uses the selected time window:

- **Successful reads** counts article retrievals reported successful, including segment-cache hits. It excludes recorded misses and errors, but is not a count of unique articles, completed files, or successful playback sessions.
- **Peak download** is the highest 1-second Usenet download rate sampled in the window [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }. The sampler runs once per second and stores only the per-minute and per-hour maximum, so the value does not shrink when you widen the time range. History recorded before the sampler existed falls back to the highest bucket average (downloaded bytes divided by bucket duration). **N/A** means no chart data. This metric works with or without segment caching, including rclone installations.
- **Errors** counts attempt errors other than provider misses. A retry or fallback can still recover the request.
- **Served** counts bytes served by client read sessions ending in the window.

The client/app chart lines and legend count **attempts**, including recorded misses and errors. Their totals therefore need not match Successful reads. Historical availability probes contribute failures but not successful checks, so these totals must not be used to calculate an availability rate.

**Provider miss attempts** remain in Error breakdown and bucket details. Retries and multiple providers can produce several misses for one article that is eventually retrieved. Negative-cache skips do not add misses. Unexpected BODY responses are recorded as protocol errors going forward; existing history is not rewritten.

## Provider throughput [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The Overview provider table shows **Peak** and **Active avg** in decimal MB/s, combining
all connections for each provider. Both use raw NNTP BODY byte-counter differences and
actual elapsed time, sampled approximately once per second:

- **Peak** is the highest observed sampled rate in the selected window. The sparkline
  and solid detail-chart line show the peak in each chart interval.
- **Active avg** divides sampled bytes by elapsed time in samples with transferred bytes.
  Wholly idle samples are excluded; pauses within a nonempty sample still count. The
  dashed detail-chart line shows this weighted average in each chart interval.

For example, ten seconds at 100 MB/s followed by fifty idle seconds produces approximately
100 MB/s for both values, not the 16.7 MB/s whole-minute average. This is observed workload
throughput, not a benchmark of a provider's maximum capacity. Individual provider peaks
may occur at different times, so adding them need not equal the overall download peak.

Older history has no sampled provider rates and displays an unavailable value rather than
an estimate. Windows spanning an upgrade use recorded samples only. Empty chart intervals
remain gaps. Samples belong to the interval containing their endpoint; minute and hourly
rollups preserve maxima and weighted active totals, and pruned hourly totals remain in the
all-time summary. The expanded all-time chart covers retained history only. Persisted rates
survive restart, but unflushed samples can be lost on shutdown or crash; downtime is not
counted as active time. Back up `/config` before upgrading: an additive metrics migration
stores these new measurements without rewriting existing history.

## Playback slowed but nothing failed

When streams buffer without hard errors, read support-pack latency phases first
(`metrics/recent.json` → `latency24Hours`):

- High `response` with low `pool-wait` / `permit-wait` → provider/server latency.
- High provider `pool-wait` → that provider's connections are saturated or churning.
- High streaming/queue `permit-wait` → that workload's configured connection cap is saturated.
- High stream-trace `consumerWaitMs` with low values in all three phases → prefetch
  geometry or consumer pacing — compare with `bodyDrainMs` on RangeEnd events.

Generate a pack from **Settings → Support** — [Technical support pack](../configuration/support.md).

## *Arr won't import

- Paths must match exactly between InfiniDysk completed path and *Arr containers.
- Symlinks: rclone mount healthy? `ls` shows `completed-symlinks` and `.ids`?
- STRM: Base URL reachable from Emby/Jellyfin?
- Check Automatic Queue Management rules — [Arrs](../configuration/arrs.md).

## 403 / 405 on MKCOL, PUT or DELETE

The mount is a **read-only** virtual filesystem — `/content`, `/completed-symlinks` and `/.ids`
serve data streamed from Usenet and accept no writes. Refused writes are expected, not a fault:

- `403 Forbidden` — a client tried to create, copy, move or upload something.
- `405 Method Not Allowed` — `MKCOL` targeted a directory that already exists.

Logs show one aggregated warning per read-only path every 5 minutes (`Refused to create item under
read-only path …`), with per-attempt detail at `LOG_LEVEL=debug`. InfiniDysk cannot stop a client from
re-attempting, so fix it at the source — the warning and the access-log line both name the client IP
and User-Agent:

- Media servers (Emby/Jellyfin/Plex/Kodi): turn off saving metadata, artwork or `.nfo`/`.srt`
  sidecars **into media folders**, or scan your library rather than the InfiniDysk mount.
- *Arr: disable metadata/extra-file writing for the affected root folder.
- rclone: mount with `--read-only` so it stops probing for writability.

## `addurl` SSRF / private indexer [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }

Allow Docker DNS or LAN hosts under **Trusted local hosts** — [SABnzbd API](../features/sab-api.md).

## Why did files disappear?

See [Deletion audit](../operations/deletion-audit.md) — history retention ≠ deleting mounts; orphan cleanup and *Arr actions can remove content. History rows disappearing after import are usually the Arr or a `/completed-symlinks` folder delete, not InfiniDysk deleting the file. If Remove Orphaned Files lists imported files, check that **Library Directory** is your organized library root, not the rclone mount.

## Plex marks old episodes as newly added [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

InfiniDysk does not change WebDAV `Last-Modified` after import, and it does not issue ETags. Plex keys library items by **file path**, so an old episode showing up as *newly added* means Plex deleted its library row and then re-created it on a later scan.

The most common cause on an rclone mount of `/content` is a transient scan-time failure (container restart, rclone re-list after `vfs/forget`, lazy RAR size correction, proxy timeout) combined with Plex's **Empty trash automatically after every scan**. The path is briefly unavailable, trash collection removes the item, and the next clean scan re-adds it — triggering intro/credits analysis again.

Quick check: compare the file's mtime in the mount (`ls -l`) with Plex's *added* date. An old mtime with a new *added* date means the server never recreated the file. For the full checklist see [Plex “newly added” churn on /content mounts](../operations/plex-readd-diagnosis.md).

## Provider / missing articles

- Circuit breaker may pause a bad provider — check Usenet settings and Overview.
- Storage groups skip sibling resellers after a miss — only group identical upstream storage.
- Health/repairs can replace unhealthy library items — [Health and repairs](../operations/health-repairs.md).

## Still stuck

Generate a [technical support pack](../configuration/support.md) from **Settings → Support**,
review it for personal paths and names, then [open an issue](https://github.com/infinidysk/infinidysk/issues).
For local stream debugging, see [Contributing](../community/contributing.md).
