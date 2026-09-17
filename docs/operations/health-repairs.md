# Health and repairs

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

Use **Re-check action needed** [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }
in the Health history header to queue another check for every live file whose latest result still
requires action. Each file is queued once, including files still present in SAB history; older
**Action needed** events that were superseded by a successful check are ignored. Existing history
rows remain available as an audit trail. Re-checks pause while downloads are processing and follow
the configured health-check and repair schedules.

## Manual checks

Use the Health UI / repairs flows in the app to inspect failures. Known transport issues should appear as clear warnings in logs rather than opaque crashes — see [Logs](logs-crash-dumps.md).

## Re-running health checks [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

**Settings → Maintenance → Re-run Library Health Checks** queues a fresh background health-check
pass over every video, audio, and archive file in the library, including files still present in
SAB history — no history rows are deleted, and existing health-check results are kept. Checks run
a few files at a time, pause while the download queue is processing, and can generate significant
Usenet (STAT) traffic on large libraries. Track progress on the **Health** page.
