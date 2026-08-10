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

Configure credentials and filesystem behavior under [WebDAV settings](../configuration/webdav.md),
and playback behavior under [Streaming settings](../configuration/streaming.md).
Mount with [rclone](../guides/mounting-webdav.md) for filesystem clients.

## Deleting from Explore [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }

From **Explore**, you can delete individual files or entire release folders under `/content/{category}/{release}/…`. The confirmation dialog shows how many files, folders, bytes, and linked history entries will be affected.

Deletion is limited to mounted content under `/content`. Use the **Queue** page to remove NZBs from `/nzbs`, and the **History** page to clear `completed-symlinks` entries. Internal paths such as `/.ids` cannot be deleted from Explore.

When the last file referencing a history entry is removed, the history row is pruned automatically so external SAB clients do not keep pointing at deleted mounts.
