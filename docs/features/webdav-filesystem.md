# WebDAV filesystem

InfiniDysk exposes NZB contents as a browsable tree over WebDAV (and the Explore UI).

Typical top-level paths:

| Path | Role |
|------|------|
| `/content` | Mounted releases by category |
| `/nzbs` | NZB-oriented views |
| `/view/...` | Streaming/download URLs used by players and STRM |
| `completed-symlinks` | Symlink import artifacts (via rclone `--links`) |
| `.ids` | Stable id-based paths for symlink targets |

Content streams from Usenet on read — files are not fully downloaded to disk first. Blobs under `{CONFIG_PATH}/blobs/` store NZB metadata needed to remount.

Configure auth and streaming behavior under [WebDAV settings](../configuration/webdav.md). Mount with [rclone](../guides/mounting-webdav.md) for filesystem clients.
