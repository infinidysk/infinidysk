# Indexers

Newznab indexers/aggregators, global request defaults, and title exclude patterns.

!!! tip "Headless ENV"

    Map config keys below to `NZBDAV_CONFIG__...` with the
    [naming algorithm](headless.md#naming-algorithm)
    (`indexers.instances` → `NZBDAV_CONFIG__INDEXERS__INSTANCES`).

## Global defaults

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| HTTP(S) Proxy URL | in `indexers.instances` | empty | Used when indexer has no override |
| Default Search User-Agent | `api.search-user-agent` | empty → `nzbdav/{ver}` or `NZB_SEARCH_USER_AGENT` | Search/caps queries |
| Default Retrieve User-Agent | `api.user-agent` | empty → `nzbdav/{ver}` or `NZB_GRAB_USER_AGENT` | Fetching `.nzb` |
| Request timeout (seconds) | in instances | `30` | Per-request timeout |
| Search results per indexer | in instances | `100` | Page size |
| Exclude result patterns | `search.exclude-patterns` | empty | JS regex per line (case-insensitive) |
| Synced exclude URLs | `search.exclude-sync-urls` | empty | Auto-updating JSON lists |
| Refresh every (minutes) | `search.exclude-sync-refresh-minutes` | `720` | Sync interval 15–10080 |

Synced patterns take precedence; last-good cache survives temporary URL failures.

## Per-indexer

| Control | Effect |
|---------|--------|
| Name / URL / API Key | Newznab endpoint |
| Search / Retrieve User-Agent | Optional overrides |
| Proxy URL | Optional override |
| Skip TLS certificate verification | Accept an invalid HTTPS certificate; off by default |
| Max requests / minute | `0` = unlimited |
| API hit / download limits + reset hour | Cap usage; blank reset = rolling 24h |
| Enabled | Include in searches |
| Strict matching | Drop titles that don't match the request |
| Extra movie/TV categories | Appended to 2000/2070 or 5000/5070 |
| Ignore category filter | Omit `cat=` |
| Result filtering | Skip passworded, min grabs, grace period, age/zero-download drops, rank by grabs |

## Invalid indexer certificates [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

Leave **Skip TLS certificate verification** disabled unless a trusted HTTPS
indexer has a certificate it cannot correct. The setting keeps traffic encrypted
but accepts untrusted, expired, and hostname-mismatched certificates, exposing
API keys and NZB requests to man-in-the-middle attacks. It applies to the
indexer's API and its resolved NZB download URLs. SAB `addurl` requests inherit
the setting only when their initial URL exactly matches an enabled indexer's
configured host.

[Indexer search feature](../features/indexer-search.md)
