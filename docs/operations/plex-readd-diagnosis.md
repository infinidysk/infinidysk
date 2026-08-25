# Plex “newly added” churn on /content mounts

When Plex marks an old episode as *newly added* and re-runs intro/credits detection, InfiniDysk almost never changed the file’s metadata in place. WebDAV `getlastmodified` is the immutable `DavItem.CreatedAt`; there is no ETag. Plex tracks items by **file path**, so “newly added” means the path vanished and came back — either the server deleted and recreated it, or Plex removed its own library row after a failed scan and re-added it on the next clean scan.

Use the checks below in order. The first one settles most cases without touching logs.

## 1. Compare file mtime with Plex “added” date

On the host, run:

```bash
ls -l "/path/to/plex/library/Show Name/Season 01/episode.mkv"
```

If the mtime is weeks or months old but Plex says *added today*, InfiniDysk did not recreate the file. The Plex library row was deleted and re-added because a scan saw the path as unavailable — skip to section 4 below.

If the mtime is recent or the path changed (new release name, or a ` (2)` suffix), the server really did re-import the file. Check InfiniDysk history, health results, and Sonarr history for that episode.

## 2. Is background repair enabled?

Settings → Health → **Enable Background Repairs**. Default is **off**. If off, health-repair deletion is ruled out.

## 3. Look for a server-side deletion

```sql
SELECT CreatedAt, Path, Result, RepairStatus, Message
FROM HealthCheckResults
WHERE RepairStatus IN (1, 2)
ORDER BY CreatedAt DESC;
```

`RepairStatus` values: `1` = Arr remove-and-blocklist, `2` = deleted as orphan/force. Also grep container logs for:

```bash
grep "dav-delete" /var/log/docker.log  # or docker logs nzbdav 2>&1 | grep dav-delete
```

Health-repair deletes are logged as `dav-delete source=health-repair ... reason="auto-removed after repeated streaming failures"` or similar. See [Deletion audit](deletion-audit.md) for all sources.

## 4. Transient scan failure

If mtime is old and no `dav-delete` exists, the most likely cause is a scan that raced an unavailable or stale rclone view:

- container restart or auto-update while Plex was scanning
- rclone `vfs/forget` of `/content/{category}` immediately after an import forces a fresh PROPFIND
- lazy RAR size correction changing the advertised size after first stream
- frontend proxy timeout on a large season folder
- network or mount blip

Plex’s **Empty trash automatically after every scan** turns that temporary unavailability into a deleted library row; the next scan re-adds it.

## 5. Confirm the Plex trash setting

Plex Settings → Library → **Empty trash automatically after every scan** must be enabled for this symptom to appear. Disabling it prevents the re-add churn but leaves stale “unavailable” items after real upgrades.

## 6. Sonarr history

Even if you do not remember a grab, check Sonarr History for the affected series. A repair-triggered `EpisodeSearch` or a repack/proper shows up there and produces a new `/content` path.

---

If these checks point to a transient listing failure, collect a [technical support pack](../configuration/support.md) while the problem is fresh and open an issue with the `webdav.counters` section and any `dav-delete` lines.
