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
