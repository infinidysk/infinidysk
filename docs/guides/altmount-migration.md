# Migrate from Altmount [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

!!! warning "Experimental"

    The AltMount migration wizard is **experimental**. Keep AltMount available until you have verified playback from InfiniDysk, back up `/config` before you start, and expect rough edges. The migration ledger (`usenet-migration.db`) is disposable and is not included in Backup & Restore.

The guided migration under **Settings → System → Migration (experimental)** imports an existing Altmount library by rebuilding each release's NZB and submitting it through InfiniDysk's normal queue. It does not modify Altmount metadata or store files.

Both legacy raw-protobuf metadata and Altmount v3-prefixed `.meta` formats are supported, including v3 stores. The wizard follows each file's recorded `store_ref`; **Altmount Store Root** can remap its `.nzbs/...` suffix when the library was copied from another host or mounted at a different container path. Pre-v0.3.0 (v1) releases without a store are migrated from the original NZB when it still exists on disk (including `.nzb.gz` under `.nzbs/`).

!!! warning "Back up and keep Altmount data available"

    Back up both applications before beginning. Mount the Altmount metadata, config, and store paths read-only when possible, and keep the old service available until migrated releases play correctly from InfiniDysk. The optional symlink step needs write access to the media library and a writable backup directory.

## Before you start

Altmount does **not** have to be running for this process.

- Configure and test the Usenet providers InfiniDysk will use.
- Create one or more dedicated, migration-only InfiniDysk categories that Radarr and Sonarr do not monitor. Do not reuse the categories configured on their normal InfiniDysk download clients.
- Mount **exactly one** new path into the InfiniDysk container: the AltMount data directory, read-only (for example `- /host/path/altmount:/altmount:ro`). Basic mode automatically detects this recommended `/altmount` layout. Values entered in the wizard are **container paths**, not host paths.
- **No rclone configuration changes** are required for any part of the migration.
- Locate the metadata tree containing `.meta` files and the store tree containing `.nzbs/*.nzbz` files (often under the same AltMount data mount).
- Keep Altmount's `config.yaml` available if you want its SABnzbd category list discovered automatically. Basic mode requires this file to be readable, valid, and contain at least one supported category.
- The optional Links step needs the media library mounted read-write for in-container Apply, orphan removal, or restore. Prefer downloading the shell script when you only need to perform rewrites on the host and do not want a library volume in InfiniDysk. Symlink backup archives default to `/config/migration-backups` (no extra volume).

Example compose fragment for the one-mount default:

```yaml
services:
  nzbdav:
    volumes:
      - /host/path/config:/config
      - /host/path/altmount:/altmount:ro
      # Optional — for in-container Step 6 Apply, orphan removal, or restore:
      # - /host/path/media:/data/media
```

## Connect modes and detected paths

**Basic mode** is the default. When Step 1 opens, it checks a previously saved standard-layout directory or `/altmount` on a new migration. Detection verifies more than the path names: the data directory and `metadata/` directory must exist, `config.yaml` must be readable and parseable, and the config must contain at least one supported SABnzbd category. **Connect** remains disabled until those checks pass.

The single data-directory field expands as follows:

| Altmount Data Directory | Metadata Root | `config.yaml` | Store Root |
|-------------------------|---------------|---------------|------------|
| `/altmount` | `/altmount/metadata` | `/altmount/config.yaml` | `/altmount` |
| `/altmount-data/config` | `/altmount-data/config/metadata` | `/altmount-data/config/config.yaml` | `/altmount-data/config` |

Enter an absolute **container path** and select **Detect paths** when you use a custom mount. The path cannot be the container filesystem root or contain `.` or `..` navigation segments. The Store Root intentionally matches the selected data directory in Basic mode; Scan later verifies the `.nzbs/` store data referenced by the metadata.

!!! note "DUMB-managed installations"

    Category lists written by DUMB-managed Altmount installations are supported, including valid YAML lists whose `- name:` markers align with `categories:`. Map the complete Altmount data directory into the InfiniDysk container, then enter that container path in Basic mode.

Use **Advanced mode** when metadata, `config.yaml`, and the store are split across mounts or do not follow the single-directory layout. It preserves the three manual path fields from the original wizard and exposes **Submit Workers** and **Max Queue Depth**. Metadata Root is required; `config.yaml` and Store Root are optional. If you omit `config.yaml`, categories can still be discovered from the store during Scan.

## Choose dedicated migration categories

Create a new InfiniDysk category for migration imports, or one per media type/source category when you want separate rollback boundaries. Names such as `altmount-migration-movies` and `altmount-migration-tv` make their purpose clear. Map each included Altmount category to one of these dedicated categories, and exclude anything you do not intend to migrate.

!!! warning "Do not reuse an Arr-monitored category"

    Reusing a category already configured in Sonarr or Radarr is strongly discouraged. During migration, that Arr can observe and act on the wizard's queue and history entries, which may trigger imports, renames, retries, or other automation while the migration is still running. Avoiding that interference otherwise requires stopping the Arr or disabling InfiniDysk as its download client.

InfiniDysk places each imported release under `/content/<target-category>/<release>`. A migration-only category therefore also creates a clean rollback boundary: all content introduced by the migration is isolated beneath a known category directory. If migration imports share a production category with unrelated InfiniDysk content, there is no safe category-wide cleanup when reversing the migration.

## Run the wizard

1. **Connect** — use Basic mode to detect the standard single-directory layout described above, or switch to Advanced mode to enter each path manually. Keep Submit Workers at `1` unless you have a specific reason to increase it.
2. **Categories** — map every included Altmount category to a dedicated migration-only InfiniDysk category, or exclude it. Do not select a category monitored by Sonarr or Radarr.
3. **Scan** — InfiniDysk groups metadata by store, verifies the referenced `.nzbz` data, estimates fetch cost, and checks whether the release is already present.
4. **Review** — inspect red/amber findings, exclusions, filename changes, and collisions. Blocking collisions must be resolved before the run can start.
5. **Run** — the wizard reconstructs NZBs and submits them through InfiniDysk's normal import pipeline. Progress survives restarts. Pause or cancel stops before the next submission; an individual submission already crossing the queue boundary is allowed to finish and is recorded safely.
6. **Links (optional)** — build a dry-run plan for library symlinks that still target Altmount. Review every status before applying. InfiniDysk writes a restore archive first, changes symlinks only, and leaves real files, unmatched links, and drifted links untouched. If you want a clean break from Altmount, you can separately remove links classified as `orphan`; that action writes its own restore archive first.

The wizard can be reset after work reaches a non-active state. Resetting clears the current scan and plan but retains completed migration mappings so later symlink scans can identify releases imported in earlier runs.

## Symlink safety and restore

Set **Library Root** to the media library containing the links. **Backup Directory** defaults to `/config/migration-backups` (do not place it on the AltMount mount you are about to decommission). Apply and restore are confined to the Library Root and reject symlinked or reparse-point parent directories. If a link target changes after planning, the drift guard leaves it untouched.

Pause *Arr import automation while Apply, orphan removal, or restore runs — a rename race with Sonarr/Radarr can leave a drifted link. Restored (and rewritten) symlinks are owned by the InfiniDysk process user.

You can download a **shell script** of rewrite rows to run on the host (or any container where the library paths are visible). That path does not update the wizard status table; in-container Apply with the restore archive remains the recommended path when the library is mounted.

The dry-run plan classifies every symlink found during the library walk:

| Status | Meaning |
|--------|---------|
| `rewrite` | Points to Altmount and has a verified InfiniDysk replacement. |
| `orphan` | Points to Altmount, but no safe InfiniDysk match was found. |
| `unreadable` | The link was found, but its target could not be read or classified. It remains unchanged and may still point at Altmount. Review directory permissions and filenames that are not valid UTF-8, and check whether another process changed the entry during the scan. Applying other rewrites requires explicit acknowledgement of these gaps. |
| `already-nzbdav` | Already points to InfiniDysk and needs no change. |
| `not-altmount` | Does not correlate to scanned Altmount content and is left unchanged. |
| `applied` | Successfully repointed to InfiniDysk. |
| `failed` | A verified rewrite was attempted but could not be completed. |
| `removed` | An orphaned Altmount symlink was removed after its original target was backed up. |

Use **Restore Symlinks** to select a generated archive. Rewrite restores verify that each link still points to the recorded InfiniDysk replacement; orphan-removal restores require the path to remain absent. Both restore the original Altmount target without overwriting changed library entries.

### Remove orphaned Altmount links

After reviewing the plan, **Remove orphaned links** can delete only the library symlink entries classified as `orphan`. The button shows the current orphan count and requires a separate acknowledgement before removal begins. It does not delete the files those links point to, remove Altmount or InfiniDysk data, touch real files, or act on `unreadable` and unrelated links. Each link target is checked again immediately before removal, and a separately labelled orphan-removal archive is written and verified before the first link is deleted.

You can cancel an active removal. Links removed before cancellation remain recorded as `removed`, while untouched or drifted links remain available for review or retry. The verified archive intentionally covers every pre-validated candidate, so any link that was removed before cancellation remains recoverable.

This cleanup does **not** ask Sonarr or Radarr to search for replacements. After removing orphaned links, run **Refresh & Scan** for each affected series or movie (or trigger the corresponding refresh job) so the Arr application detects the deleted paths and marks those files as missing. You can then initiate or schedule re-grabs according to your Arr configuration.

Orphan-removal archives appear in the same **Restore Symlinks** control as rewrite archives. Restoring one recreates an absent link with its original Altmount target, but never overwrites a real file, directory, or differently targeted symlink that now occupies that path.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Basic mode cannot detect Altmount | Confirm the absolute container directory contains `metadata/` and a readable, valid `config.yaml` with at least one SABnzbd category. Basic mode rejects the filesystem root and paths containing `.` or `..`. If the paths are split or `config.yaml` is unavailable, use Advanced mode. |
| No categories discovered | In Basic mode, check the `sabnzbd.categories` block in `config.yaml`; inline flow-style lists such as `categories: [...]` are not supported. In Advanced mode, omit an unusable config and continue to Scan so categories can be discovered from stores. |
| `store_missing` | Mount the `.nzbs` tree and set **Altmount Store Root** to the directory that contains `.nzbs`. |
| A release is already migrated | It is not resubmitted; retained provenance can still be used for symlink matching. |
| Start migration is disabled | Resolve blocking collisions, map or exclude every category, and successfully refresh the review tables. |
| Reset is disabled | Cancel active work and wait for the session to reach a non-active state. A paused run is still active. |
| A symlink is `orphan`, `unreadable`, or `failed` | Review its match/target details. The wizard will not guess or overwrite a link that cannot be verified safely. |
| Removed links are still shown as present in Sonarr or Radarr | Run the relevant **Refresh & Scan** or refresh job so the Arr detects the missing paths before starting re-grabs. |
| Many releases show `evicted` or `failed` after restoring a database backup | The migration ledger is ahead of the restored main database. Use Reset (or Forget migration data), then re-scan; releases that already imported are re-detected and not resubmitted. |

## Reverse the migration

A complete reversal is safest when the migration used dedicated categories containing no unrelated InfiniDysk releases. Do not begin cleanup until Altmount is available again and you have the rewrite and orphan-removal archives created during Step 6.

1. **Stop migration and library automation.** Cancel or finish any active wizard operation, then pause Sonarr/Radarr imports, refresh jobs, and media-server scans. Do not let another process rename or replace library entries during restore.
2. **Make Altmount available.** Start Altmount if needed and confirm its container mounts and original link targets are accessible. Restoring links to an unavailable Altmount instance will leave the library unusable.
3. **Restore removed and rewritten links.** In Step 6, use **Restore Symlinks** for every orphan-removal archive whose links you want back, then restore the rewrite archive created before Apply. Missing orphan links are recreated with their original targets, and rewritten links are pointed back to Altmount. Restore refuses to overwrite real files, directories, or links changed since migration; resolve every reported issue before proceeding.
4. **Verify the restored library.** Inspect representative symlinks, confirm they point to Altmount again, and test playback through the original path. Do not delete the InfiniDysk copies until this verification succeeds.
5. **Delete only the isolated migration content.** Temporarily disable **Settings → WebDAV → Enforce Read-Only**, open **Explore → content → `<migration-category>`**, and delete every release folder underneath that dedicated category. Repeat for each migration-only category, then immediately re-enable **Enforce Read-Only**.
6. **Refresh the applications.** Run **Refresh & Scan** in each affected Arr and refresh the media server. Confirm the restored files are present and no migrated InfiniDysk paths remain in use before resuming automation.
7. **Remove migration bookkeeping last.** After the rollback is verified, open **Manage Migration Data** in the wizard and select **Forget all migration records**. This removes the saved migration provenance and plan but does not delete WebDAV content, SAB history, symlinks, or backup archives. Keep the archives until you are satisfied that the rollback is complete.

!!! danger "Keep deletion scoped to the migration category"

    Never delete `/content` itself, and never bulk-delete a category that also contains normal InfiniDysk releases. If an existing production category was reused, identify and remove the migrated release folders individually; the category no longer provides a safe rollback boundary. Deleting folders from Explore is recursive and cannot be undone by the symlink restore archive.

Deleting the release folders does not remove their SAB history entries. After the library and content rollback is verified, you may separately remove the migration-category history entries and remove the temporary categories from InfiniDysk settings if you no longer need them.

## Extending to other sources

The wizard core — session state machine, runs, provenance, submission lifecycle, claim recovery, reconciler, and history cleaner — is source-neutral. Adding another downloader (for example Decypharr) needs:

1. A scan runner and metadata/store readers for that source's on-disk layout.
2. A correlation provider that maps library symlinks to imported DavItems (today that seam is `SymlinkPlanner.BuildCorrelationIndexAsync`).
3. Distinct restore-archive prefixes so Step 6 backups do not collide across sources.

Decypharr is torrent/debrid-first with its own usenet mode, so a full import path needs its own analysis. The symlink retargeting engine (matcher, ops, rewriter, restore, walker) already applies to any mount-to-mount move once correlation is supplied.

## Related

[Migration paths](../getting-started/migration.md) · [Backups and upgrades](backups-upgrades.md) · [SABnzbd settings](../configuration/sabnzbd.md)
