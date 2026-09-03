# Mounting WebDAV

Symlink imports need the InfiniDysk WebDAV tree on the host filesystem. Use rclone (sidecar or host mount).

The guided [Setup Guide](../getting-started/setup-guide.md) shows these sidecar
flags, configures RC notifications, tests the connection, and disables InfiniDysk
Segment Cache for Symlink/Plex libraries.

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
url = http://nzbdav:8080/
vendor = other
user = your-webdav-user
pass = your-obscured-password
```

!!! important

  The rclone sidecar is a backend service and shares the Compose network with
  InfiniDysk. Point it directly at the backend on port `8080`. Sending WebDAV
  traffic through the frontend on port `3000` adds proxy overhead and reduces
  streaming performance. Use the frontend URL only when an rclone client cannot
  access the backend service over the network.

```bash
chmod 600 rclone.conf
```

!!! note

    Rclone's obscured password is not strong encryption — protect the file.

## Sidecar Compose service

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
        --vfs-cache-max-size=20G
        --vfs-cache-max-age=24h
        --buffer-size=0M
        --vfs-read-ahead=512M
        --dir-cache-time=20s
```

```bash
docker compose up -d nzbdav_rclone
ls -la /mnt/remote/nzbdav
# Expect: .ids, completed-symlinks, content, nzbs
```

## Flag cheat sheet

| Flag | Why |
|------|-----|
| `--links` | Turn `*.rclonelink` into real symlinks (rclone ≥ 1.70.3) |
| `--use-cookies` | Avoid re-auth on every request |
| `--vfs-cache-mode=full` | Disk-backed read cache for smooth seeks |
| `--buffer-size=0M` | Avoid double-caching with VFS |
| `--vfs-read-ahead=512M` | Buffer ahead for high-bitrate spikes |
| `--dir-cache-time=20s` | Fresh listings without RC; raise if using RC notifications |

## Optional RC notifications

Append to the mount command:

```yaml
        --rc
        --rc-addr=:5572
        --rc-user=rclone
        --rc-pass=your-rc-password
```

Then **Settings → Rclone Server**: enable notifications, host `http://nzbdav_rclone:5572`, matching credentials. Raise `--dir-cache-time` once RC works.

[Rclone settings](../configuration/rclone.md)
