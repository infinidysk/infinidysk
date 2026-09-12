# Rclone

InfiniDysk's WebDAV tree has to appear as a folder on your host before Sonarr, Radarr, Plex, Emby, or Jellyfin can use it. Two ways to get one:

| Mode | What runs rclone | Use it when |
|------|------------------|-------------|
| **Built-in** [since 1.4.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.0){ .nzbdav-since } | InfiniDysk, inside its own container | You want one container, and settings and credentials managed in one place |
| **External** | Your own rclone container | You already run a tuned rclone, or you want mounting independent of InfiniDysk |

Both are configured under **Settings → Rclone**. A new installation is offered built-in mode during setup and starts there unless you choose your own container. An existing installation stays on external mode until you turn built-in on yourself, so it keeps working exactly as it did.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`rclone.builtin.enabled` → `NZBDAV_CONFIG__RCLONE__BUILTIN__ENABLED`).

## Built-in mount [since 1.4.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.0){ .nzbdav-since }

InfiniDysk runs an `rclone rcd` daemon itself and manages mounts through it. You do not write an `rclone.conf`, and the WebDAV credentials are configured once.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Run rclone inside InfiniDysk | `rclone.builtin.enabled` | off | Starts and supervises the built-in rclone daemon |
| Mounts | `rclone.builtin.mounts` | empty | JSON array of mount definitions, edited from the UI |
| RC port | `rclone.builtin.rc-port` | `5572` | Loopback port the daemon listens on. Never published outside the container |
| Cache directory | `rclone.builtin.cache-dir` | `<CONFIG_PATH>/rclone/cache` | Where the VFS cache is written. Must be an absolute path outside every mount point. Changing it restarts the rclone daemon |

### Required Docker settings

The image ships rclone and `fuse3`, but Docker still has to let the container use FUSE. Without these, mounting cannot work no matter how the settings are filled in — InfiniDysk detects this and tells you which flag is missing rather than failing silently.

```yaml
  nzbdav:
    image: ghcr.io/infinidysk/infinidysk:latest
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

!!! important "`rshared` is what makes the mount usable"

    A mount created inside a container is invisible to other containers unless the
    parent bind uses `rshared`. Without it the mount exists only inside InfiniDysk,
    and Plex, Sonarr, and Radarr see an empty folder. This is the single most
    common reason a correct-looking built-in mount appears not to work.

### Mount settings

Each mount has:

| Field | Meaning |
|-------|---------|
| Folder on this host | Where the WebDAV tree appears, for example `/mnt/remote/infinidysk` |
| Folder inside WebDAV | Which part of the tree to expose. `/` is all of it |
| Cache mode | `full` for smooth seeking, `writes`, `minimal`, or `off` |
| Mount this folder | Turn one mount off without deleting its settings |
| Follow symlinks | Turns `*.rclonelink` files into real symlinks. Required for symlink imports |
| Read ahead | Extra data buffered ahead of the read position. Buffered **on disk** in `full` cache mode, so it spends the same budget as the cache. Accepts `512M`-style values |
| Keep cached data for | How long cached file data is kept, in hours |

### How much disk the cache uses

rclone's own `--vfs-cache-max-size` default is "off", which means a `full` cache
mode mount grows until the filesystem is full. InfiniDysk does not ship that
default. The ceiling is derived at mount time from the free space on the cache
directory's volume:

- half of the free space, capped at 20 GB;
- never less than 256 MB, so playback still works on a nearly full disk;
- and when the cache shares a volume with the databases — which the default
  directory does — 5 GB is reserved so a full cache cannot take the database
  down with it.

**Settings → Rclone** shows the directory, its free space, and the ceiling in
force. To override the automatic figure, set `VfsCacheMaxSizeBytes` on the mount
object itself — there is no separate environment variable for it, because mounts
are stored as one JSON array:

```text
NZBDAV_CONFIG__RCLONE__BUILTIN__MOUNTS=[{"Id":"library","MountPoint":"/mnt/remote/infinidysk","VfsCacheMaxSizeBytes":21474836480}]
```

An explicit value is always used as given.

Two further per-mount settings exist in `rclone.builtin.mounts` but are not
surfaced in the UI: `AllowOther`, because turning it off stops your media server
reading the mount, which is the only reason the mount exists; and
`DirCacheTime`, because the tradeoff between listing freshness and RC cache
invalidation is not settled yet. Both round-trip, so a value set through
`NZBDAV_CONFIG__` is never dropped.

**Apply now** mounts and unmounts to match the settings without waiting for the next supervisor cycle. **Remount** unmounts one mount and lets it be recreated, which recovers a wedged mount without restarting the container.

### Mounts left by a hard shutdown

A FUSE mount outlives the process that created it. A container that is killed
rather than stopped — `docker kill`, the OOM killer, a crash — leaves its mount
in the kernel's table, and rclone then refuses to mount over it with *directory
already mounted*.

InfiniDysk handles both ends of this. On shutdown it releases its mounts over
rclone's remote-control API before stopping the process, rather than relying on
rclone reacting to a signal in time. On startup, and after an unexpected daemon
exit, it clears a mount left on one of its own configured mount points — but
only when the kernel reports a `fuse` filesystem mounted exactly there **and**
that mount no longer answers, which is what a mount whose daemon has died looks
like (`ENOTCONN`, *transport endpoint is not connected*).

A mount that still responds is never removed. It is reported instead, because
something is serving it — usually an external rclone container that has not been
stopped yet — and taking it down would break a library mid-playback.

The mount point cannot be inside `CONFIG_PATH`. Mounting there would put a FUSE filesystem underneath the databases InfiniDysk is writing to, and a stalled backend would then stall its own mount.

### Moving from an rclone container

See the [migration runbook](../guides/mounting-webdav.md#moving-from-a-sidecar-to-the-built-in-mount). InfiniDysk reads the mount settings your rclone is actually running with — path, cache mode, tuning, and `--links` — and keeps the mount path identical so existing symlinks keep resolving.

The WebDAV password is **not** copied across. Enter it once under **Connect to your library** after applying the import.

Pasting an `rclone mount ...` command works when the sidecar has no remote control enabled. Flags the built-in mount does not model are reported in the preview rather than silently kept, and a command where an unrecognised flag's value cannot be told apart from the mount point is rejected instead of guessed at — rewrite those flags in `--flag=value` form.

## External rclone server

Notify rclone RC when WebDAV files change (useful with high `dir-cache-time`).

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Rclone RC notifications | `rclone.rc-enabled` | off | Auto `vfs/forget` on add/remove |
| Rclone Server Host | `rclone.host` | empty | e.g. `http://nzbdav_rclone:5572` |
| Rclone Server User | `rclone.user` | empty | Optional |
| Rclone Server Password | `rclone.pass` | empty | Optional |

**Test Conn** (next to the host field) validates RC reachability, credentials, and a successful
API response (`POST core/version`). It works with an already-saved (masked) password — you do
not need to re-enter it after reload. Failures show a reason (authentication, HTTP status, or
network error). Password may be left empty when the RC server has no auth.

Turning on built-in mode does not change any of these values, so switching back is just turning it off again.

Mount directory for symlink imports is configured on the [SABnzbd](sabnzbd.md) tab (`rclone.mount-dir`).

[Mounting WebDAV](../guides/mounting-webdav.md)
