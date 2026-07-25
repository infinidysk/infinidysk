# Mounting WebDAV

Symlink imports need the NzbDAV WebDAV tree on the host filesystem. Use rclone (sidecar or host mount).

## Prepare the mount point

```bash
sudo mkdir -p /mnt/remote/nzbdav
sudo chown -R $(id -u):$(id -g) /mnt/remote/nzbdav
```

## Rclone config

Obscure the WebDAV password:

```bash
docker run --rm -it rclone/rclone obscure "<your-webdav-password>"
```

`rclone.conf`:

```ini
[nzbdav]
type = webdav
url = http://nzbdav:3000/
vendor = other
user = your-webdav-user
pass = your-obscured-password
```

```bash
chmod 600 rclone.conf
```

!!! note

    Rclone's obscured password is not strong encryption — protect the file.

## Sidecar Compose service

The recommended mount enables rclone RC notifications so directory listings stay fresh without a very short `--dir-cache-time`. NzbDAV content is immutable after import: prefer a large VFS cache sized to disk and a long `--vfs-cache-max-age`. Large `--buffer-size` or `--vfs-read-ahead` values amplify media-server scan probes into multi‑hundred‑MB Usenet fetches per touched file — leave them unset (or `0`) and rely on NzbDAV's server-side read-ahead.

```yaml
  nzbdav_rclone:
    image: rclone/rclone:latest
    container_name: nzbdav_rclone
    restart: unless-stopped
    environment:
      TZ: America/New_York
    volumes:
      - /mnt:/mnt:rshared
      - ./rclone.conf:/config/rclone/rclone.conf:ro
      - ./rclone-cache:/cache
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    devices:
      - /dev/fuse:/dev/fuse:rwm
    depends_on:
      nzbdav:
        condition: service_healthy
        restart: true
    command: >
      mount nzbdav: /mnt/remote/nzbdav
        --cache-dir=/cache
        --uid=1000
        --gid=1000
        --allow-other
        --links
        --use-cookies
        --vfs-cache-mode=full
        --vfs-cache-max-size=50G
        --vfs-cache-max-age=2160h
        --buffer-size=0M
        --vfs-read-chunk-size=16M
        --vfs-read-chunk-size-limit=512M
        --no-modtime
        --no-checksum
        --dir-cache-time=1h
        --poll-interval=0
        --rc
        --rc-addr=:5572
        --rc-no-auth
```

```bash
docker compose up -d nzbdav_rclone
ls -la /mnt/remote/nzbdav
# Expect: .ids, completed-symlinks, content, nzbs
```

Then **Settings → Rclone Server**: enable notifications, host `http://nzbdav_rclone:5572`, leave User and Password empty (matches `--rc-no-auth`). Use the Test connection button to confirm NzbDAV can reach the RC endpoint — bind `--rc-addr` to `:5572` (or `0.0.0.0:5572`), not `127.0.0.1`, when NzbDAV runs in another container.

!!! tip "Sizing the VFS cache"

    Raise `--vfs-cache-max-size` to fit available disk (for example `200G`). Age-based eviction uses last access time; `--vfs-cache-max-age=off` is not valid in rclone — use a large duration such as `2160h` (~90 days) and let max-size do the real eviction.

!!! warning "Plex and remounts"

    `depends_on.restart: true` remounts rclone whenever the `nzbdav` service restarts. During that remount the mount point can briefly look empty, which Plex may treat as a library-wide delete. Disable **Empty trash automatically after every scan** in Plex, and prefer upgrading or restarting during off-hours.

## Flag cheat sheet

| Flag | Why |
|------|-----|
| `--links` | Turn `*.rclonelink` into real symlinks (rclone ≥ 1.70.3) |
| `--use-cookies` | Avoid re-auth on every request |
| `--vfs-cache-mode=full` | Disk-backed read cache for smooth seeks |
| `--buffer-size=0M` | Avoid double-caching with VFS full mode (large buffers amplify scan probes) |
| `--vfs-read-chunk-size=16M` | Initial range size for uncached reads (smaller probes before chunk growth) |
| `--vfs-read-chunk-size-limit=512M` | Cap rclone's unbounded chunk-size doubling |
| `--vfs-cache-max-age=2160h` | Immutable content — prefer size-based eviction over daily expiry (~90 days) |
| `--no-modtime` / `--no-checksum` | Skip irrelevant WebDAV metadata churn |
| `--dir-cache-time=1h` | With RC notifications, listings stay fresh via `vfs/forget`; 1h is a self-heal backstop |
| `--poll-interval=0` | Disable remote polling; RC notifications own invalidation |
| `--rc` / `--rc-addr=:5572` / `--rc-no-auth` | Let NzbDAV notify the mount on add/remove |

### Without RC notifications

If you skip RC, set `--dir-cache-time` much lower (for example `20s`) so new imports appear promptly, and omit `--poll-interval=0` (or leave rclone's default). Expect more WebDAV listing traffic during library scans.

### Optional RC authentication

For a private Docker network, `--rc-no-auth` is fine. If the RC port is reachable beyond that network, replace `--rc-no-auth` with:

```yaml
        --rc-user=rclone
        --rc-pass=your-rc-password
```

Then set the same User and Password under **Settings → Rclone Server**.

[Rclone settings](../configuration/rclone.md)

## High Overview “Served” overnight

Overview **Served** counts decoded bytes actually written to HTTP clients — not the full size of library items that were only probed. Common amplifiers with rclone mounts:

- Large `--buffer-size` or `--vfs-read-ahead` with `--vfs-cache-mode=full` (each media-server probe can download hundreds of MB — omit both; NzbDAV read-aheads server-side)
- Short `--vfs-cache-max-age` (nightly scans re-fetch previously cached probes)
- Plex / *Arr / Bazarr scans, embedded-subtitle searches, or backup tools walking the mount

See also [Media servers](media-servers.md) and [Troubleshooting](troubleshooting.md).
