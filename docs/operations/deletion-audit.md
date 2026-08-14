# Deletion audit

Mounted content under `/content` can vanish for several independent reasons. “Files disappeared by themselves” usually conflates these paths:

| Cause | Trigger | What happens |
|-------|---------|--------------|
| History delete with delete-files | Admin UI, or `del_completed_files=1` | Mounted items for that history row are deleted |
| Cascading child sweep | Deleted directory | Children removed in background |
| Health repair | Repairs + missing articles | Orphans/blocklisted files are deleted; linked releases are removed and blocklisted through *Arr |
| Remove Orphaned Files | Manual/scheduled | Deletes files with **no** library symlink/STRM and **no** history link (safety abort if too few links) |
| History retention / Prune Completed History | Retention days > 0, or Maintenance task | Prunes history with `deleteFiles: false` — mounts stay, lose history link; unlinked mounts can then be removed by Remove Orphaned Files |
| SAB/Arr history delete without delete-files | Arr Remove Completed, queue rules with `removeFromClient=true`, or `/completed-symlinks` folder DELETE | History row gone; mounts stay. Logged as `history-remove source=… deleteFiles=false` |
| Manual delete | WebDAV DELETE / admin API | Explicit user action |
| Blocklist filter | Download file blocklist | Matching files never appear in `/content` |
| Non-persistent `/config` | Volume missing/wiped | Empty DB on restart — entire library looks gone |

## Grep the audit log

Every DavItem deletion emits a structured Serilog line. Search for:

```text
dav-delete
```

Examples:

```text
dav-delete source=history-cleanup ... reason=DeleteMountedFiles=true ...
dav-delete source=dav-cleanup ... reason=cascading child sweep ...
dav-delete source=health-repair ... reason=missing articles; orphaned ...
dav-delete source=remove-orphaned ... reason=no library symlink/strm link
dav-delete source=webdav-delete ... reason=client DELETE on UsenetFile
dav-delete source=blocklist-filter ... reason=filename matches blocklist pattern ...
history-remove source=sab-history-delete count=1 deleteFiles=false ...
```

Large history deletes may also emit `dav-delete-bulk`.

## Persist `/config`

Always map a durable host directory to `/config`. Without it, every recreate looks like a total wipe.
