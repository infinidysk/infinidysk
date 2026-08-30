# Watchtower (settings)

Pre-resolve list titles to healthy releases. Feature overview: [Watchtower](../features/watchtower.md).

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`watchtower.enabled` → `NZBDAV_CONFIG__WATCHTOWER__ENABLED`).

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable Watchtower | `watchtower.enabled` | off | Master switch |
| Search profile | `watchtower.profile-token` | first profile | Which profile's indexers |
| Release selection | `watchtower.ranking` | `watchdog` | Match watchdog / largest healthy |
| Series scope | `watchtower.series-scope` | `latest-season` | latest / first / all-aired / recent / off |
| Recent episode count | `watchtower.series-recent-count` | `3` | When scope=recent |
| Prefer season bundles | `watchtower.season-bundles` | on | Finished seasons as one release |
| Fall back to episodes | `watchtower.season-bundle-fallback` | off | When no healthy bundle |
| Fallback scope / recent / max episodes | `watchtower.season-bundle-fallback-*` | see defaults | Bound episode fallback |
| Max items per series | `watchtower.series-max-episodes` | `50` | `0` = unlimited |
| When over the cap, keep | `watchtower.series-cap-keep` | `newest` | newest / oldest |
| Junk floor (GB) | `watchtower.size-floor-bytes` | 0.5 GB | Drop tiny releases |
| Bandwidth ceiling (GB) | `watchtower.size-ceiling-bytes` | none | Empty/0 = none |
| Minimum grabs | `watchtower.min-grabs` | `0` | Fake filter |
| Active warm-set cap | `watchtower.active-set-cap` | `100` | Standing ready items |
| Auto throughput | `watchtower.auto-throughput` | off | Match indexer limits; ignore daily budget |
| Daily resolve budget | `watchtower.daily-resolve-budget` | `60` | Soft new-resolves/day |
| Shortlist depth | `watchtower.shortlist-depth` | `2` | Winner + backups |
| Grab cap per resolve | `watchtower.grab-cap-per-resolve` | `3` | Max NZB fetches/pass |
| Verify sample count | `watchtower.verify-sample-count` | `3` | STAT segments |
| Verify timeout (seconds) | `watchtower.verify-timeout-seconds` | `10` | Per-segment |
| Re-check interval | `watchtower.keepfresh-base-seconds` | 6h | Usenet-only re-verify |
| Max re-check interval | `watchtower.keepfresh-max-seconds` | 7d | Backoff cap |
| Dead-item retry | `watchtower.unavailable-retry-seconds` | 6h | Re-search cadence |
| List sync interval | `watchtower.sync-interval-seconds` | 1h | Remote list refresh |
| Max list-source response (bytes) [since 1.2.8](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.8){ .nzbdav-since } | `watchtower.list-source-max-response-bytes` | `8388608` (8 MiB) | Reject oversized Stremio/list bodies before parse; counts HTTP-client bytes (no automatic decompression). Max `16777216` (16 MiB). |
| Verbose activity logging | `watchtower.verbose-logging` | off | Chatty Logs output |

`watchtower.resolve-concurrency` (default `3`) exists in the backend but is not exposed in this Settings tab.
