# infinidysk-rs

Rust rewrite of InfiniDysk: a Usenet-backed virtual filesystem that imports NZBs through a SABnzbd-compatible API and serves media over a native FUSE mount and WebDAV, streaming directly from Usenet providers.

This is the 2.0 alpha, for early testers. Expect bugs, and keep your 1.x install around until you are happy. Contributors: see [AGENTS.md](AGENTS.md).

## Image

`ghcr.io/infinidysk/infinidysk-next`, Linux x64 and arm64.

| Tag | What it is |
|-----|------------|
| `v2.0.0-alpha.N` | An alpha release. Pin one of these if you want to stay put. |
| `edge` | Every change on `main`, untested. May be broken. |
| `sha-<commit>` | One exact build, for reproducing a bug. |

There is no `latest` tag on purpose, so nothing upgrades onto the alpha by accident. When 2.0 ships it moves back to the main `infinidysk` image and this one stops updating.

## Quick start

```yaml
services:
  infinidysk:
    image: ghcr.io/infinidysk/infinidysk-next:v2.0.0-alpha.1
    container_name: infinidysk
    restart: unless-stopped
    environment:
      PUID: 1000
      PGID: 1000
      TZ: Etc/UTC # your time zone, e.g. Europe/Oslo; the maintenance window follows it
    ports:
      - "3000:3000"
    volumes:
      - ./config:/config
      - /mnt:/mnt:rshared
      - /sys/devices/virtual/bdi:/sys/devices/virtual/bdi:rw
    devices:
      - /dev/fuse:/dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
```

Open `http://<host>:3000`, create the admin account, then add your providers. Do this before exposing the port to an untrusted network: until an admin exists, anyone who reaches the page can create one.

The UI, the SABnzbd API and WebDAV all share port 3000. The library is mounted at `/mnt/infinidysk` by default (Settings, or `INFINIDYSK__MOUNT__DIR`). Containers that read it (Sonarr, Radarr, Plex, …) need the same host path, for example `/mnt:/mnt:rslave`.

In Sonarr/Radarr, add a SABnzbd download client pointing at `infinidysk:3000` with the SAB API key from Settings.

### Health check

The image has a built-in health check, so you do not need a `healthcheck:` block. Containers that read the mount can wait for it:

```yaml
  plex:
    depends_on:
      infinidysk:
        condition: service_healthy
```

Healthy means the server answers, the database works, the import worker runs, and the FUSE mount is mounted and answers. Without `/dev/fuse` the mount is not checked. A normal start turns healthy within seconds. The first start after an upgrade from 1.x stays `starting` until the import is done; if that takes longer than the 10-minute start period, the container shows `unhealthy` until the import ends, so run `docker compose up -d` again once it has.

`GET /healthz` answers without sign-in: 200 when healthy, 503 while starting or unhealthy, with a small JSON body of check results and no other detail. Reasons for a failing check go to the log. `infinidysk healthcheck` calls it on the port from `INFINIDYSK_LISTEN` and exits 0 or 1.

## Upgrading from 1.x

The upgrade reads your 1.x config and imports it into a new database: providers, Arr instances, categories, SAB API key, WebDAV and admin logins, the mount dir, the library and its item ids, history and the queue. Your 1.x files are opened read-only and never changed. Still, test on a copy:

1. Stop 1.x so the copy is consistent, then copy its config directory:

   ```sh
   docker compose stop <1.x service>
   cp -a ./config ./config-2.0
   ```

2. Optional dry run. This prints what will be carried over, what is not, and why, without importing anything:

   ```sh
   docker run --rm -v ./config-2.0:/config ghcr.io/infinidysk/infinidysk-next:v2.0.0-alpha.1 import-report
   ```

3. Set `TZ` on 2.0 to the same zone 1.x ran in. 1.x stored local times, so a different zone shifts every file date, and media servers may then re-read your whole library.
4. Start 2.0 with `./config-2.0:/config` (compose above). The import runs on first start; the UI shows progress and the full report. Sign in with your 1.x admin login.
5. If the container name or port changed, update the SABnzbd download client in each Arr. The API key is the same.

Do not run 1.x and 2.0 at the same time: both would use your provider connections. To roll back, stop 2.0 and start 1.x on the original `./config`. Anything imported or added while on 2.0 is not carried back.

### Moving off rclone

2.0 mounts the library itself over FUSE, so the rclone sidecar is no longer needed. Most 1.x installs used rclone, so this is the part to get right: **your library symlinks only keep working if the 2.0 mount sits at exactly the path rclone mounted.**

1. Check your 1.x mount dir (Settings > Rclone Server, for example `/mnt/remote/infinidysk`). The upgrade carries it over as the 2.0 mount dir; confirm it in the import report. Your symlinks point into it (`<mount dir>/.ids/...`).
2. Stop and remove the rclone service from your compose file, and make sure nothing is left mounted there:

   ```sh
   docker compose rm -sf <rclone service>
   mount | grep /mnt/remote/infinidysk    # should print nothing
   sudo umount -l /mnt/remote/infinidysk  # only if it still does
   ```

3. Give the infinidysk container what the rclone container had: `/dev/fuse`, `SYS_ADMIN`, `apparmor:unconfined`, and the parent directory with `:rshared` (as in the quick start). The mount dir must be visible at the same path inside the container, so if rclone used `/mnt:/mnt:rshared`, use the same bind here.
4. Start 2.0 and check from the host and from an Arr container:

   ```sh
   ls /mnt/remote/infinidysk   # expect .ids, completed-symlinks, content, nzbs
   ```

   Then open one existing library symlink and confirm it plays.

If your rclone mount used `--no-modtime`, every file on it showed a 1970 date and your media server stored that. 2.0 shows real dates by default, which makes Plex and similar servers re-read every file on their next scan. Before that scan, turn on Settings > Mount > Disable file timestamps and restart.

To move the mount to a new path later, change it under Settings > Mount after setting your library folders under Settings > Library. After the restart, 2.0 rewrites the symlinks in those folders. That needs the library folders bound into the infinidysk container at the same paths your Arrs use.

The rclone settings (RC notifications, cache flags) are not carried over; the built-in mount does not need them.

If you cannot give the container FUSE (no `/dev/fuse`), 2.0 logs a warning and runs WebDAV-only. You can keep rclone in that case, pointed at `http://infinidysk:3000/` (1.x used port 8080). 2.0 does not send rclone refresh notifications, so keep `--dir-cache-time` short.

## Not in 2.0 yet

These 1.x features are missing in the alpha. The import report lists settings it could not carry over.

- Global download bandwidth limit
- Global connection cap across providers (per-provider connection counts do carry over)
- Rclone RC notifications (not needed with the built-in mount)
- Stremio and addon routes, search, indexers and Prowlarr
- Backup and restore

## Reporting problems

Include:

- The image tag (or `sha-` tag) you run.
- The log, from `/config/logs/infinidysk.log` or the Logs page.
- For playback problems: start a trace on the Traces page, reproduce, and attach the trace export. Exports have secrets removed.
- For upgrade problems: the output of `import-report` (above).

Never post provider passwords or API keys. Release names are not needed; describe the file instead (for example "stored RAR5 episode").

## Configuration

Settings live in the database and are edited in the UI. Any setting can also be forced with an environment variable, `INFINIDYSK__<SECTION>__<KEY>` (for example `INFINIDYSK__MOUNT__DIR=/mnt/remote/infinidysk`); forced values are not written back.

| Variable | Default | |
|----------|---------|---|
| `INFINIDYSK_LISTEN` | `0.0.0.0:3000` | UI, SABnzbd API, WebDAV and `/healthz` |
| `INFINIDYSK_DATABASE_URL` | `sqlite:/config/infinidysk.sqlite` | Use `postgres://…` for PostgreSQL |
| `PUID` / `PGID` | `1000` | Owner shown on files in the mount |
| `INFINIDYSK_LOG_DIR` | `/config/logs` | |
| `RUST_LOG` | `info` | Log filter |

## Media analysis

The Explorer lists each media file's container, codecs, resolution and audio tracks, read by `ffprobe`. The Docker image includes it; elsewhere, install FFmpeg or point `INFINIDYSK_FFPROBE` at the binary. Without it, analysis is off and everything else works.

## Who is reading

Overview and Traces show who opened each read. This is best effort, for troubleshooting only.

- **WebDAV** shows the user agent and the address that connected. Behind a reverse proxy, add the proxy under Settings > System > Network > Trusted proxies so the client from `X-Forwarded-For` is shown instead. Add only proxies you run; any other client can fake the header.
- **FUSE** shows the process name, pid, and uid/gid of the reader. The kernel reports the reader as seen from the container that mounted. A reader in another container, or on the host, shows as `pid unavailable` with only its uid/gid, even when the mount is shared to it with `:rshared`. Mount propagation does not share process visibility. To see those process names, run infinidysk with `pid: host`. This lets the container see every process on the host, so only do it if you accept that.
- For a snapshot of open files on the host, run `sudo lsof +f -- <mount dir>` or `sudo fuser -vm <mount dir>`. These show which processes hold files open right now, not which ones are reading.

## FUSE in Docker

- `/dev/fuse`, `SYS_ADMIN` and `apparmor:unconfined` let the container mount. Without `/dev/fuse` it logs a warning and serves WebDAV only.
- `:rshared` on the parent directory makes the mount visible on the host and in other containers.
- The `/sys/devices/virtual/bdi` volume lets the Settings > Performance preset set the kernel's readahead. Docker mounts `/sys` read-only, so without it readahead stays at the kernel's 128 KB.

On shutdown the mount is removed. After a crash the next start clears the dead mount; see [infinidysk-lab:docs/restart-behavior.md](https://github.com/infinidysk/infinidysk-lab/blob/main/docs/restart-behavior.md).

## Build

Requires [rustup](https://rustup.rs); the toolchain is pinned in `rust-toolchain.toml`.

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
```

## License

MIT
