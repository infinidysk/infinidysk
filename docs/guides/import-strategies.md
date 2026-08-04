# Import strategies

Choose how Radarr/Sonarr import completed jobs. Set this under **Settings → SABnzbd → Import Strategy**.

=== "Symlinks — Plex"

    Best when the media server needs real filesystem entries (classic Plex libraries).

    1. Set **Rclone Mount Directory** to the host path of the WebDAV mount (e.g. `/mnt/remote/nzbdav`).
    2. Run an [rclone sidecar](mounting-webdav.md) with `--links` so `*.rclonelink` files become symlinks into `.ids`.
    3. Point *Arr root folders at paths that see those symlinks.

    A bounded rclone VFS cache can smooth seeking without storing full media forever.

=== "STRM — Emby/Jellyfin"

    Best when the media server can play `.strm` URLs.

    1. Set **Completed Downloads Dir** to a path shared with *Arr (e.g. `/mnt/completed-downloads`).
    2. Set **Base URL** to an InfiniDysk URL the media server can reach (HTTPS recommended).
    3. Skip the rclone FUSE mount — no `/dev/fuse` required.

    STRM files contain authenticated streaming URLs; keep Base URL and WebDAV credentials correct.

## Path consistency

The completed path InfiniDysk reports must appear **at the same absolute path** inside Radarr/Sonarr containers. Map host volumes identically.

## Switching strategies

Maintenance tools can convert STRM → symlinks or recreate STRM sidecars — see [Maintenance](../configuration/maintenance.md). Back up first.

## Related

[Mounting WebDAV](mounting-webdav.md) · [Media servers](media-servers.md) · [SABnzbd settings](../configuration/sabnzbd.md)
