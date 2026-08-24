# Import strategies

Choose how Radarr/Sonarr import completed jobs. Set the primary output under
**Settings → SABnzbd → Import behavior**.

=== "Symlinks — Plex"

    Best when the media server needs real filesystem entries (classic Plex libraries).

    1. Set **Rclone Mount Directory** to the host path of the WebDAV mount (e.g. `/mnt/remote/nzbdav`).
    2. Run an [rclone sidecar](mounting-webdav.md) with `--links` so `*.rclonelink` files become symlinks into `.ids`.
    3. Point \*Arr root folders at paths that see those symlinks.

    A bounded rclone VFS cache can smooth seeking without storing full media forever.

=== "STRM — Emby/Jellyfin"

    Best when the media server can play `.strm` URLs.

    1. Set **Completed Downloads Dir** to a path shared with \*Arr (e.g. `/mnt/completed-downloads`).
    2. Set **Base URL** to an InfiniDysk URL the media server can reach (HTTPS recommended).
    3. Skip the rclone FUSE mount — no `/dev/fuse` required.

    STRM files contain authenticated streaming URLs; keep Base URL and WebDAV credentials correct.

## Dual outputs [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

Enable both outputs when Plex needs filesystem symlinks while Emby or Jellyfin
also needs STRM files. Choose one primary \*Arr import output: InfiniDysk reports
only that output through SAB `complete_dir` and history `storage`, so one \*Arr
instance does not import the same release twice.

- Leave **Symlink Output Directory** empty to retain the virtual
  `completed-symlinks` rclone tree, or set a directory such as `/mnt/Plex` to
  write real symlinks at queue completion.
- Set **Completed Downloads Dir** to a separate writable location such as
  `/mnt/Jellyfin` for STRM sidecars.
- Generated symlinks point at `<rclone.mount-dir>/.ids/...`; the media server
  must see that rclone mount at the same absolute path.

Do not make the same \*Arr instance scan both output roots. Avoid placing a
custom symlink-output directory inside the managed Library Directory; repairs
track one discovered link per media item and overlapping trees can leave a
second link behind.

## Path consistency

The completed path InfiniDysk reports must appear **at the same absolute path** inside Radarr/Sonarr containers. Map host volumes identically.

## Switching strategies

Switching outputs affects future queue completions only; it neither deletes nor
backfills existing output files. Recreate missing STRM sidecars from
[Maintenance](../configuration/maintenance.md); conversion of existing library
symlinks to STRM remains a separate maintenance workflow.

## Related

[Mounting WebDAV](mounting-webdav.md) · [Media servers](media-servers.md) · [SABnzbd settings](../configuration/sabnzbd.md)
