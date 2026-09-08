# Mounting WebDAV

Symlink imports need the InfiniDysk WebDAV tree on the host filesystem. Either let
InfiniDysk run rclone itself, or run rclone yourself as a sidecar or on the host.

- **[Built-in mount](#built-in-mount)** [since 1.4.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.0){ .nzbdav-since } — one container, settings in the admin UI.
- **[Sidecar](#sidecar-rclone)** — your own rclone container, tuned by you.
- **[Moving from a sidecar](#moving-from-a-sidecar-to-the-built-in-mount)** — import an existing setup.

## Built-in mount

Enable it under **Settings → Rclone**. InfiniDysk starts its own rclone daemon and
mounts through it, so there is no second container and no `rclone.conf` to write.

The container needs FUSE access and shared bind propagation:

```yaml
  nzbdav:
    image: ghcr.io/infinidysk/infinidysk:latest
    container_name: nzbdav
    restart: unless-stopped
    ports:
      - "3000:3000"
    devices:
      - /dev/fuse:/dev/fuse:rwm
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    volumes:
      - ./config:/config
      - /mnt:/mnt:rshared
```

!!! important "`rshared` decides whether anything else can see the mount"

    A mount made inside a container does not appear in other containers unless the
    parent bind is `rshared`. Without it Plex, Sonarr, and Radarr see an empty
    folder while InfiniDysk reports the mount as healthy — because from inside the
    container, it is.

Then add a mount in **Settings → Rclone**, set the folder on the host, and choose
**Apply now**. If the container is missing a FUSE flag, the page says which one
instead of failing quietly.

## Moving from a sidecar to the built-in mount

The mount path must not change. Your library's symlinks resolve through it, and
`rclone.mount-dir` is derived from it, so a different path breaks every imported
file. The import keeps the path for you; do not edit it during the move.

1. **Preview.** In **Settings → Rclone**, turn on *Run rclone inside InfiniDysk*,
   then choose **Read my rclone server**. InfiniDysk reads the mounts and the VFS
   settings your rclone is actually running with. Nothing is written yet, and the
   sidecar is not modified.

    If your sidecar has no `--rc` flags, expand *My rclone has no remote control
    enabled* and paste the `rclone mount ...` command instead. Flags InfiniDysk
    does not manage are listed back to you as not carried across. A command where
    an unrecognised flag's value cannot be told apart from the mount point is
    rejected rather than guessed at — rewrite those flags as `--flag=value`.

2. **Review.** Check the folder path, cache mode, and any warnings. Do not save
   yet.

3. **Stop the sidecar.** Two rclone instances cannot serve the same mount point,
   and saving in the next step makes InfiniDysk mount straight away.

    ```bash
    docker compose stop nzbdav_rclone
    ```

4. **Save.** Choose **Use these settings**, then save. InfiniDysk applies the
   mount list as soon as it is saved.

5. **Enter the WebDAV password.** The import does not copy it. Under *Connect to
   your library*, enter the password from **Settings → WebDAV** once. It is
   stored only in rclone's own config, obscured.

6. **Confirm.** The mount should report *Mounted*. **Apply now** re-runs the
   reconcile if it does not.

7. **Verify** before deleting anything:

    ```bash
    ls -la /mnt/remote/nzbdav
    # Expect: .ids, completed-symlinks, content, nzbs
    ```

    Check that a few existing items still play in your media server, since those
    are the files whose symlinks point through this path.

8. **Clean up.** Once verified, remove the `nzbdav_rclone` service from your
   compose file.

**To roll back at any point:** turn off *Run rclone inside InfiniDysk*, then start
the sidecar again. The import never modified it, and your external Rclone Server
settings were never overwritten.

## Sidecar rclone

The guided [Setup Guide](../getting-started/setup-guide.md) shows these sidecar
flags, configures RC notifications, tests the connection, and disables InfiniDysk
Segment Cache for Symlink/Plex libraries.

### Prepare the mount point

```bash
sudo mkdir -p /mnt/remote/nzbdav
sudo chown -R $(id -u):$(id -g) /mnt/remote/nzbdav
```

### Rclone config

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
  access the backend service over the network. Do not publish backend port `8080`
  to an untrusted network; browser and admin traffic should continue to use port
  `3000`.

```bash
chmod 600 rclone.conf
```

!!! note

    Rclone's obscured password is not strong encryption — protect the file.

#### Frontend proxy warning [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

If the frontend detects an rclone client on port `3000`, it emits an operator warning
at most once every 30 minutes and shows a non-dismissible warning in the admin UI.
Continued proxied traffic keeps the warning active. After rclone is moved to port `8080`,
the warning clears once the 30-minute observation window expires. An open admin tab
refreshes this status once per minute.

### Sidecar Compose service

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

### Flag cheat sheet

| Flag | Why |
|------|-----|
| `--links` | Turn `*.rclonelink` into real symlinks (rclone ≥ 1.70.3) |
| `--use-cookies` | Avoid re-auth on every request |
| `--vfs-cache-mode=full` | Disk-backed read cache for smooth seeks |
| `--buffer-size=0M` | Avoid double-caching with VFS |
| `--vfs-read-ahead=512M` | Buffer ahead for high-bitrate spikes |
| `--dir-cache-time=20s` | Fresh listings without RC; raise if using RC notifications |

### Optional RC notifications

Append to the mount command:

```yaml
        --rc
        --rc-addr=:5572
        --rc-user=rclone
        --rc-pass=your-rc-password
```

Then **Settings → Rclone Server**: enable notifications, host `http://nzbdav_rclone:5572`, matching credentials. Raise `--dir-cache-time` once RC works.

[Rclone settings](../configuration/rclone.md)
