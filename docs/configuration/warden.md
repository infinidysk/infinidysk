# Warden

Portable dead-release fingerprint list: filter search results, sync remote sources, optional GitHub backup.

!!! note "Headless ENV scope"

    Scalar Settings below (`warden.hide-dead`, `warden.quorum`, `warden.backbone-scope`) can use
    [`NZBDAV_CONFIG__...`](headless.md). Warden **sources**, GitHub backup state, and `warden.db`
    remain a separate domain and are **not** driven by the ENV overlay.

## Persisted settings

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Filter out anything on the list | `warden.hide-dead` | on | Hide matching search hits (fallback if all match) |
| Agreement needed for shared lists | `warden.quorum` | `2` | Quorum for corroborate sources |
| Only filter when the provider matches | `warden.backbone-scope` | on | Scope remote/imported fingerprints |

## Operational UI

Sources (local / remote / imported): trust full/corroborate/observe, enable, refresh hours, import/export/bundle, clear/remove.

### Scan history [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

**Scan history** turns imports that already failed into fingerprints for your own list, so an
install that has been grabbing through Sonarr/Radarr starts with the verdicts it has already paid
for instead of an empty list. The button reports what it found first; nothing is written until you
confirm.

Only failures whose articles were gone (missing segments, missing rar volumes) count. Imports that
failed for any other reason — broken archive headers, unsupported compression, no media files,
encryption, CRC mismatches — arrived intact and say nothing about the release, so they are skipped
rather than hiding healthy releases from future searches. Items whose nzb is no longer stored, or
whose nzb carries neither a poster nor a post date, cannot be fingerprinted and are counted as
skipped.

`POST /api/warden-import-history` does the same thing; it dry-runs unless called with
`?dryRun=false`.

**Backup to GitHub:** repo, fine-grained PAT, path, branch, scope, interval, auto backup, restore. Fingerprints only — keep backup repos private for personal lists. Restore replaces the **local** list only.

!!! warning

    Merge-into-my-list import cannot be un-merged.
