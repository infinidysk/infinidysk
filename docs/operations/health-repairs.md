# Health and repairs

## Attention summary [since 1.4.3](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.3){ .nzbdav-since }

**Overview → Needs attention** highlights files requiring action, open or recovering provider
circuits, and degraded or offline Arr integrations. Expand an Arr warning to identify the
affected instance and its reason. Queue warnings or errors come from the Arr app's queue;
inspect **Activity → Queue** in that app. They do not mean the instance is offline.
Connection settings remain available separately for unreachable instances.
Unavailable status is shown separately
from a successful check with no problems. The health count refreshes every minute while
the page is visible.

The **Health** page puts action-needed files first. Expand **Diagnostic details** to read
the complete recovery message with touch, mouse, or keyboard. Small nonzero outcome
percentages are preserved instead of rounding down to zero.

## Background repairs

**Settings → Repairs** monitors mounted media, reconstructs missing segments from PAR2 parity, and
can trigger *Arr replacements for unhealthy linked library items.

**Enable Background Repairs** is on by default [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }. Turn it off to stop
background health checks, PAR2 work, and limited damage-tolerance handling. To automatically
replace linked library items, also configure:

- **Library Directory** visible inside the container — the organized library root (parent of Arr root folders), never the rclone mount or `/completed-symlinks`
- At least one configured [Radarr/Sonarr instance](../configuration/arrs.md)

Tune concurrency, health-check depth, aging [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }, and streaming-failure thresholds — [Repairs settings](../configuration/repairs.md).

For streaming-triggered failures, **Repair After Streaming Failures** can require consecutive
failures before InfiniDysk starts a repair or asks *Arr to find a replacement. A successful full-file
playback or background health check resets that count. The counter is in memory, so it also resets
when InfiniDysk restarts.

Corrupt-but-present articles (CRC failures on otherwise complete files) now follow the same
escalation path when playback breaks. Confirmed corrupt segments are recorded and included in
full-coverage health classification so those files are not reported healthy — see
[Realtime corruption detection](../configuration/repairs.md).

A segment counts as missing only when every eligible provider source is conclusively unavailable;
cached negative results and storage-group sibling evidence can establish that without probing every
provider. If a walk would otherwise conclude that the segment is missing but any provider timed out
or failed to connect, the file is marked *Action needed* and rechecked later instead of being
repaired or blocklisted.

### When files are rechecked

After a successful health check, InfiniDysk schedules the next routine check after an
interval equal to the release's current age, measured from its release date rather than
when it was imported into InfiniDysk.

| Release age when checked | Next routine check due |
|--------------------------|------------------------|
| 1 week | In 1 week |
| 2 weeks | In 2 weeks |
| 1 year | In 1 year |

For example, a one-week-old release is checked again in one week. It is then two weeks
old, so the following check is scheduled two weeks later. Older releases are therefore
checked less frequently. Degraded files use this same age-based schedule.

The minimum interval is **one hour**, with no maximum interval. Missing or future release
dates use the one-hour minimum. Failed checks and repairs use separate retry rules, and
[health-check windows](../configuration/repairs.md#health-check-and-repair-windows-since-130)
may delay when a due routine check actually runs.

This schedule is independent of **Check older releases less thoroughly**, which changes
how many segments are checked, not how often checks occur.

## Health-check retention

Health result rows prune by age (**Maintenance** retention or `DATABASE_HEALTHCHECK_RETENTION_DAYS`). Reset counters from Maintenance when needed.

## PAR2 storage and diagnostics

PAR2 repairs persist decoded article bodies and their volume-relative yEnc headers under
`CONFIG_PATH/repair-segments` (normally `/config/repair-segments`). Startup reloads complete
body/header pairs into the patch catalog. Patched BODY reads work without the original
provider, and patched articles satisfy STAT health checks without querying that provider.
The store does not invent NNTP HEAD or Date metadata.

The patch store is separate from the ordinary segment cache. Its least-recently-used body
capacity defaults to 4 GiB. A successful repair publication retains the entire new batch
and evicts older entries if necessary. Staging temporary files and already-open evicted
bodies can temporarily consume additional disk space beyond the cataloged-body limit.
Every visible patch is a complete validated entry; publication is not a filesystem batch
transaction. An I/O failure can leave a validated subset of a batch visible while the
job records failure.

Include `repair-segments` in `/config` backups to preserve repairs. Removing it discards the
only available copy of an article when its provider copy is gone. For deliberate cleanup:

1. Stop the container so neither playback nor repair is using the store.
2. Back up `/config`; move or remove only the `repair-segments` directory if discarding all
	patches is intended. Do not remove `blobs` or the database.
3. Restart InfiniDysk. The catalog is rebuilt; missing articles must be fetched or repaired
	again, and may no longer be recoverable.

Do not edit individual body/header files while the server runs. Lookup validates that both
files exist, the header parses, and the body length agrees. This structural check does not
detect arbitrary same-length corruption introduced by external disk edits.

### Progress and failures

Health-triggered repairs record their result in Health history. Playback-triggered jobs also
appear in repair logs, Prometheus, and the support pack, but do not create a health-history
row merely because a playback report was queued. The support pack's `par2Repair` data includes
the active path/phase, consumed bytes, estimated memory, retained source window, recent jobs,
and admission state (limit 1, active owner, waiters, cumulative/latest wait).

Admission metrics [since 1.4.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.0){ .nzbdav-since }:

- `nzbdav_par2_repair_active`: admitted owners, 0 or 1.
- `nzbdav_par2_repair_admission_waiters`: owners waiting to start; callers sharing the same work do not add waiters.
- `nzbdav_par2_repair_admission_wait_seconds`: admission-wait histogram.

Existing PAR2 metrics:

- `nzbdav_par2_repair_jobs_total{state}` and `nzbdav_par2_repair_duration_seconds`: job transitions and duration.
- `nzbdav_par2_repair_bytes_read_total`, `nzbdav_par2_repair_slices_reconstructed_total`, and `nzbdav_par2_repair_segments_committed_total`: successful patch-repair work.
- `nzbdav_par2_validation_failures_total{gate}`: packet, slice, or file validation failures.
- `nzbdav_par2_patch_store_bytes`, `nzbdav_par2_patch_hits_total`, and `nzbdav_par2_patch_evictions_total`: patch-store usage.

Repair byte accounting measures decoded bytes consumed by repair, not all NNTP wire traffic
or process memory. Source passes are sequential and may reread healthy data to keep memory
bounded. See [PAR2 settings](../configuration/repairs.md#par2-gap-repair-since-120) for limits.

Known unavailable/corrupt parity and infeasible repairs produce single-line warnings with a
reason. Missing or unreadable local streaming payloads instead require operator action and
a daily, eventually weekly, health recheck. They do not trigger *Arr deletion/blocklisting
or a provider-repair cooldown. Restore matching metadata/blobs before retrying.

## Repair history [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

The **Health** page lists items that Background Repairs automatically deleted or repaired, with the
time and reason for the action. New rows retain the original NZB filename and release name so you
can locate a replacement; rows created before this feature show the affected WebDAV path instead.

The list follows Health-check retention and is cleared with the Health-check statistics reset in
**Settings → Maintenance**. It records automatic health actions only — deleting items manually or
through the API does not add a repair-history row.

History does not include **Action needed** records, including under the **Degraded** filter.
These represent unresolved work rather than completed actions.

## Needs attention [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The **Needs attention** section appears above the health schedule. It shows the latest unresolved
check for each existing file, with its NZB identity and failure reason. Resolved failures, deleted
files, duplicate checks, and files already queued for repair are excluded. It has separate paging
from history and refreshes when health-check results arrive.

Use a file's **Re-check** button to retry that item, or **Re-check action needed** to queue all
eligible files, including files on other pages. Retry controls require Background Repairs to be
enabled and are unavailable to read-only users. A retry queues another check; it does not guarantee
repair. Files leave this list while queued and return if the next check still requires action.
Re-checks pause while downloads are processing and follow the configured health-check and repair
schedules. Existing check records remain stored until health-check retention removes them.

### Manual resolution [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

Each attention row also offers **Search indexers** and **Delete file** to users with write access,
even when Background Repairs is disabled:

- **Search indexers** opens search with the release/NZB name prefilled. Adjust the query to find
	another release. Mounting a result does not request a Radarr/Sonarr import, blocklist the old
	release, or resolve the old attention item. For Arr-managed media, request the replacement in
	Radarr/Sonarr instead.
- **Delete file** previews eligibility and requires confirmation before permanently removing
	the selected WebDAV file. It does not fetch a replacement or remove imported symlinks/STRMs;
	those links may stop playing. Use it for confirmed unused files, or after arranging a replacement.
	WebDAV read-only, protected-item, and in-progress-download restrictions still apply.

A missing-library-link reason does not necessarily mean the configured paths are wrong. The file
may never have been imported or may have been superseded. Re-check alone cannot resolve a persistently
missing link. Verify the movie/episode's current file in Arr before deleting the old WebDAV file.

## Manual checks

Use the Health UI / repairs flows in the app to inspect failures. Known transport issues should appear as clear warnings in logs rather than opaque crashes — see [Logs](logs-crash-dumps.md).

## Re-running health checks [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

**Settings → Maintenance → Re-run Library Health Checks** queues a fresh background health-check
pass over every video, audio, and archive file in the library, including files still present in
SAB history — no history rows are deleted, and existing health-check results are kept. Checks run
a few files at a time, pause while the download queue is processing, and can generate significant
Usenet (STAT) traffic on large libraries. Track progress on the **Health** page.
