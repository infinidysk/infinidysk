# WebDAV filesystem

InfiniDysk exposes NZB contents as a browsable tree over WebDAV and the **Files** UI.

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

## Files administration [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

**Files** opens the canonical `/content` hierarchy. **Tree** expands folders in place;
**List** shows matching files throughout the current scope. Expansion does not change the
URL or clear other branches, filters, selection, or an open player. Use the breadcrumb or
**Use as scope** to change scope explicitly. Existing `/explore/content/...` links still
open that directory; **WebDAV system views** retains access to `/nzbs`,
`/completed-symlinks`, and `/.ids` using their existing directory browser.

Search and metadata filters apply to the whole scope before pagination, including unopened
folders. Both modes share health, stored schedule, library, category, indexer, storage kind,
repair outcome, original NZB availability, size, and date filters. Search matches file names,
paths, job names, and original NZB names. SQLite folds ASCII case in search; non-ASCII
characters remain case-sensitive. Blank filters are unrestricted; **Unknown** is a distinct
state. Each branch or list page contains at most 100 rows in the UI.

### Reading file metadata

- **Health** comes from the latest retained check for that exact file. Pending or urgent
	repairs take precedence. **Unknown** means there is no retained result, even if a last-check
	timestamp remains after history cleanup. Requeueing a degraded file does not make it healthy.
- **Next scan** distinguishes checking, disabled, repair pending, queued, due, and scheduled
	work. Queued sentinels are never shown as dates. **No scheduled date** filters the stored
	null date, so it can include unsupported sidecars. A due date is eligibility, not a promised
	start time: [health windows](../configuration/repairs.md), queue pressure, and workers can
	defer execution.
- **Added age** uses the existing server-local added timestamp, serialized with an offset.
	**Posted age** uses the release date and remains unknown when that date is absent. They are
	not interchangeable. Historical added dates cannot recover a previous server timezone;
	negative displayed ages are clamped to zero.
- The focused row's details include canonical path and ID, storage kind (NZB, RAR, or
	multipart), health outcome and message, job, original NZB name, category, indexer, and
	library link paths. **Last played (release)** belongs to the linked release history,
	not an individual file. Deleting history does not hide a live file.
- **Library** membership is a completed scan of recognized symlinks or STRM files in the
	configured [Library Directory](../configuration/repairs.md), matched by exact file ID.
	It is refreshed periodically and is not a live Arr inventory. Pending or failed scans
	mean **Unknown**, never **Not in library**. No configured root means **Not configured**.
	The scan timestamp or failure is shown with the file count. Link details contain filesystem
	paths, not signed STRM target URLs.

### File actions

**Recheck** queues only the selected supported file and preserves its observed health and
repair history. It respects disabled repairs, work windows, queue pressure, and worker
concurrency. Repeating a request for an already queued or running file does not start duplicate
work. WebDAV enforce-read-only blocks removal, not an administrator's recheck.

**Play** uses the existing built-in player when the media type is supported. **Download**
streams the file through its signed URL. **Export NZB** uses the retained original NZB
identity and filename; an unavailable original is not reconstructed. Media inspection remains
on demand in the player.

**Search in Arr** is a confirmed, non-destructive request to enabled
[Radarr/Sonarr instances](../configuration/arrs.md) that own the file. It requires a recognized
link in the configured Library Directory and matching ownership paths; a download ID alone is
not enough. Every enabled instance must be checked before any search is requested. InfiniDysk
does not remove or blocklist the current file, alter monitoring or quality rules, or spend the
automatic replacement-search budget. Arr's quality, cutoff, and monitoring decisions still
apply, so a search receipt does not guarantee a replacement download. Multiple owners can
produce partial results. **Unconfirmed** means the command might have arrived; check Arr
before manually trying again. These requests are not automatically retried.

Read-only frontend accounts can browse, play, download, and export but cannot recheck, search,
preview removal, or remove. Disabled action tooltips explain the restriction. Mobile layouts
retain every filter and action through toolbar wrapping and horizontal table scrolling.

No new settings or setup-wizard steps are required. Existing Library Directory, Arr instances,
Repairs, and WebDAV settings govern these actions. This feature introduces no database
migration, data conversion, or feature-specific backup requirement.

## Deleting from Files [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }

From **Files**, you can delete individual files or entire release folders under `/content/{category}/{release}/…`. Removal requires successful impact previews before confirmation. The dialog shows file, folder, byte, and linked-history totals.

Selecting a directory includes its current collapsed, hidden, and filtered-out descendants.
Overlapping parent/child selections are reduced to their top-level targets. Counts are advisory
at preview time: the dialog does not lock a subtree against new arrivals. Bulk removal runs
sequentially, is not atomic across directories, and retains individual failures for review
without retrying already removed targets. The selected file ID is checked again, so a different
file appearing at the same path cannot be removed using a stale selection.

Protected roots and category folders, matching active downloads, and **Enforce Read-Only**
continue to prevent removal.

Deletion is limited to mounted content under `/content`. Use the **Queue** page to remove NZBs from `/nzbs`, and the **History** page to clear `completed-symlinks` entries. Internal paths such as `/.ids` cannot be deleted from Files.

When the last file referencing a history entry is removed, the history row is pruned automatically so external SAB clients do not keep pointing at deleted mounts.
