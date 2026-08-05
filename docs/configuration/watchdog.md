# Watchdog

Playback failover, stall failover, and size-variant retention when a release cannot be served.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`play.watchdog-enabled` → `NZBDAV_CONFIG__PLAY__WATCHDOG_ENABLED`).

## Failover

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable failover watchdog | `play.watchdog-enabled` | on | Off = single release (legacy) |
| Total budget (seconds) | `play.total-budget-seconds` | `30` | Hard ceiling 3–180 |
| Hedge delay (seconds) | `play.hedge-delay-seconds` | `3` | Start backups if primary is slow |
| Parallel candidates per batch | `play.max-candidates` | `3` | 1–10 |
| Total candidates per request | `play.max-attempts` | `10` | 1–200 |
| Verify mode | `play.verify-mode` | `none` | `stat` / `body` / `none` |
| Negative-cache TTL (minutes) | `play.candidate-negative-cache-minutes` | `5` | Skip recently failed |
| Search link lifetime (hours) [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since } | `play.resolution-cache-ttl-hours` | `168` | Play links in search results (env fallback) |
| Prefer releases with subtitles | `play.prefer-subtitles` | on | Reorder only |

## Stall failover

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable stall failover | `grab.stall-failover-enabled` | on | Requires watchdog on |
| Stall window (seconds) | `grab.stall-failover-window-seconds` | `2` | No progress → set aside |
| Per-candidate ceiling (seconds) | `grab.stall-failover-ceiling-seconds` | `5` | Max time before moving on |

## Variants

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Mode | `variants.mode` | `off` | off / smart / collect-all |
| Size tolerance (%) | `variants.tolerance-pct` | `25` | smart only |
| Max copies per group | `variants.max-per-group` | `3` | `0` = unlimited |
| Selection strategy | `variants.replay-strategy` | `closest-to-click` | closest / largest / smallest |
| Fallback on fetch failure | `variants.fallback-on-failure` | on | Use closest existing |
| Eviction strategy | `variants.eviction-strategy` | `lru` | lru / size / never |
| Active-use grace (seconds) | `variants.eviction-active-grace-seconds` | `60` | Skip eviction if recently used |

[Warden, Watchdog, Preflight](../features/warden-watchdog-preflight.md)
