# Migrate from Altmount [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

!!! warning "Experimental"

    The AltMount migration wizard is **experimental**. Keep AltMount available until you have verified playback from NzbDAV, back up `/config` before you start, and expect rough edges. The migration ledger (`usenet-migration.db`) is disposable and is not included in Backup & Restore.

The guided migration under **Settings → System → Migration (experimental)** imports an existing Altmount library by rebuilding each release's NZB and submitting it through NzbDAV's normal queue. It does not modify Altmount metadata or store files.

Both legacy raw-protobuf metadata and Altmount v3-prefixed `.meta` formats are supported, including v3 stores. The wizard follows each file's recorded `store_ref`; **Altmount Store Root** can remap its `.nzbs/...` suffix when the library was copied from another host or mounted at a different container path. Pre-v0.3.0 (v1) releases without a store are migrated from the original NZB when it still exists on disk (including `.nzb.gz` under `.nzbs/`).

WARNING: "Back up and keep Altmount data available"


    Back up both applications before beginning. Mount the Altmount metadata, config, and store paths read-only when possible, and keep the old service available until migrated releases play correctly from NzbDAV. The optional symlink step needs write access to the media library and a writable backup directory.

## Before you start

Altmount does **not** have to be running for this process.

- Configure and test the Usenet providers NzbDAV will use.
- Create the destination NzbDAV categories expected by Radarr and Sonarr.
- Mount **exactly one** new path into the NzbDAV container: the AltMount data directory, read-only (for example `- /host/path/altmount:/altmount:ro`). Basic mode automatically detects this recommended `/altmount` layout. Values entered in the wizard are **container paths**, not host paths.
- **No rclone configuration changes** are required for any part of the migration.
- Locate the metadata tree containing `.meta` files and the store tree containing `.nzbs/*.nzbz` files (often under the same AltMount data mount).
- Keep Altmount's `config.yaml` available if you want its SABnzbd category list discovered automatically.
- The optional Links step needs the media library mounted **only** for in-container Apply. Prefer downloading the shell script to rewrite links on the host when you do not want a library volume in NzbDAV. Symlink backup archives default to `/config/migration-backups` (no extra volume).

Example compose fragment for the one-mount default:

```yaml
services:
  nzbdav:
    volumes:
      - /host/path/config:/config
      - /host/path/altmount:/altmount:ro
      # Optional — only for in-container Step 6 Apply:
      # - /host/path/media:/data/media
```

## Run the wizard

1. **Connect** — Basic mode checks `/altmount` automatically and resolves `/altmount/metadata`, `/altmount/config.yaml`, and `/altmount` as the store root. If you mounted the same directory somewhere else, enter that one container path and select **Detect paths**. Use **Advanced mode** when metadata, `config.yaml`, and the store tree are mounted separately or do not follow that layout. Advanced mode also exposes **Submit Workers** and **Max Queue Depth**; keep Submit Workers at `1` unless you have a specific reason to increase it.
2. **Categories** — map every discovered Altmount category to an existing NzbDAV category, or exclude it.
3. **Scan** — NzbDAV groups metadata by store, verifies the referenced `.nzbz` data, estimates fetch cost, and checks whether the release is already present.
4. **Review** — inspect red/amber findings, exclusions, filename changes, and collisions. Blocking collisions must be resolved before the run can start.
5. **Run** — the wizard reconstructs NZBs and submits them through NzbDAV's normal import pipeline. Progress survives restarts. Pause or cancel stops before the next submission; an individual submission already crossing the queue boundary is allowed to finish and is recorded safely.
6. **Links (optional)** — build a dry-run plan for library symlinks that still target Altmount. Review every status before applying. NzbDAV writes a restore archive first, changes symlinks only, and leaves real files, unmatched links, and drifted links untouched. If you want a clean break from Altmount, you can separately remove links classified as `orphan`; that action writes its own restore archive first.

The wizard can be reset after work reaches a non-active state. Resetting clears the current scan and plan but retains completed migration mappings so later symlink scans can identify releases imported in earlier runs.

## Symlink safety and restore

Set **Library Root** to the media library containing the links. **Backup Directory** defaults to `/config/migration-backups` (do not place it on the AltMount mount you are about to decommission). Apply and restore are confined to the Library Root and reject symlinked or reparse-point parent directories. If a link target changes after planning, the drift guard leaves it untouched.

Pause *Arr import automation while Apply or orphan removal runs — a rename race with Sonarr/Radarr can leave a drifted link. Restored (and rewritten) symlinks are owned by the NzbDAV process user.

You can download a **shell script** of rewrite rows to run on the host (or any container where the library paths are visible). That path does not update the wizard status table; in-container Apply with the restore archive remains the recommended path when the library is mounted.

The dry-run plan classifies every symlink found during the library walk:

| Status | Meaning |
|--------|---------|
| `rewrite` | Points to Altmount and has a verified NzbDAV replacement. |
| `orphan` | Points to Altmount, but no safe NzbDAV match was found. |
| `unreadable` | The link was found, but its target could not be read or classified. It remains unchanged and may still point at Altmount. Review directory permissions and filenames that are not valid UTF-8, and check whether another process changed the entry during the scan. Applying other rewrites requires explicit acknowledgement of these gaps. |
| `already-nzbdav` | Already points to NzbDAV and needs no change. |
| `not-altmount` | Does not correlate to scanned Altmount content and is left unchanged. |
| `applied` | Successfully repointed to NzbDAV. |
| `failed` | A verified rewrite was attempted but could not be completed. |
| `removed` | An orphaned Altmount symlink was removed after its original target was backed up. |

Use **Restore Symlinks** to select a generated archive. Rewrite restores verify that each link still points to the recorded NzbDAV replacement; orphan-removal restores require the path to remain absent. Both restore the original Altmount target without overwriting changed library entries.

### Remove orphaned Altmount links

After reviewing the plan, **Remove orphaned links** can delete only the library symlink entries classified as `orphan`. It does not delete the files those links point to, remove Altmount or NzbDAV data, touch real files, or act on `unreadable` and unrelated links. Each link target is checked again immediately before removal, and a separately labelled orphan-removal archive is written and verified before the first link is deleted.

This cleanup does **not** ask Sonarr or Radarr to search for replacements. After removing orphaned links, run **Refresh & Scan** for each affected series or movie (or trigger the corresponding refresh job) so the Arr application detects the deleted paths and marks those files as missing. You can then initiate or schedule re-grabs according to your Arr configuration.

Orphan-removal archives appear in the same **Restore Symlinks** control as rewrite archives. Restoring one recreates an absent link with its original Altmount target, but never overwrites a real file, directory, or differently targeted symlink that now occupies that path.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Basic mode cannot detect Altmount | Confirm the entered container directory contains `metadata/` and `config.yaml`. If the paths are split or `config.yaml` is unavailable, use Advanced mode. |
| No categories discovered | Supply Altmount's `config.yaml`, or continue to Scan so categories can be discovered from stores. |
| `store_missing` | Mount the `.nzbs` tree and set **Altmount Store Root** to the directory that contains `.nzbs`. |
| A release is already migrated | It is not resubmitted; retained provenance can still be used for symlink matching. |
| Start migration is disabled | Resolve blocking collisions, map or exclude every category, and successfully refresh the review tables. |
| Reset is disabled | Cancel active work and wait for the session to reach a non-active state. A paused run is still active. |
| A symlink is `orphan`, `unreadable`, or `failed` | Review its match/target details. The wizard will not guess or overwrite a link that cannot be verified safely. |
| Removed links are still shown as present in Sonarr or Radarr | Run the relevant **Refresh & Scan** or refresh job so the Arr detects the missing paths before starting re-grabs. |
| Many releases show `evicted` or `failed` after restoring a database backup | The migration ledger is ahead of the restored main database. Use Reset (or Forget migration data), then re-scan; releases that already imported are re-detected and not resubmitted. |

## Extending to other sources

The wizard core — session state machine, runs, provenance, submission lifecycle, claim recovery, reconciler, and history cleaner — is source-neutral. Adding another downloader (for example Decypharr) needs:

1. A scan runner and metadata/store readers for that source's on-disk layout.
2. A correlation provider that maps library symlinks to imported DavItems (today that seam is `SymlinkPlanner.BuildCorrelationIndexAsync`).
3. Distinct restore-archive prefixes so Step 6 backups do not collide across sources.

Decypharr is torrent/debrid-first with its own usenet mode, so a full import path needs its own analysis. The symlink retargeting engine (matcher, ops, rewriter, restore, walker) already applies to any mount-to-mount move once correlation is supplied.

## Related

[Migration paths](../getting-started/migration.md) · [Backups and upgrades](backups-upgrades.md) · [SABnzbd settings](../configuration/sabnzbd.md)
