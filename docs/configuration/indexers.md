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
| Default Retrieve User-Agent | `api.user-agent` | empty → `SABnzbd/5.1.0` or `NZB_GRAB_USER_AGENT` | Fetching `.nzb` |
| Request timeout (seconds) | in instances | `30` | Per-request timeout |
| Search results per indexer | in instances | `100` | Page size |
| Max indexer response (bytes) [since 1.2.8](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.8){ .nzbdav-since } | in instances (`MaxResponseBytes`) | `4194304` (4 MiB) | Reject oversized Newznab caps/search XML before parse; counts HTTP-client bytes (no automatic decompression). Per-indexer override. Max `16777216` (16 MiB). |
| Exclude result patterns | `search.exclude-patterns` | empty | JS regex per line (case-insensitive) |
| Synced exclude URLs | `search.exclude-sync-urls` | empty | Auto-updating JSON lists |
| Refresh every (minutes) | `search.exclude-sync-refresh-minutes` | `720` | Sync interval 15–10080 |

Synced patterns take precedence; last-good cache survives temporary URL failures.

## Prowlarr pull sync [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }

InfiniDysk can pull its indexer list from one Prowlarr instance. Configure the connection in InfiniDysk under **Settings → Indexers → Prowlarr pull sync** — do **not** add InfiniDysk under Prowlarr's Apps list, and do not use InfiniDysk's SABnzbd-compatible settings for this integration.

| Setting | Config key | Environment variable | Default |
|---------|------------|----------------------|---------|
| Prowlarr URL | `prowlarr.url` | `NZBDAV_CONFIG__PROWLARR__URL` | empty |
| Prowlarr API key | `prowlarr.api-key` | `NZBDAV_CONFIG__PROWLARR__API_KEY` | empty |
| Automatically sync | `prowlarr.sync-enabled` | `NZBDAV_CONFIG__PROWLARR__SYNC_ENABLED` | `false` |
| Sync interval (minutes) | `prowlarr.sync-interval-minutes` | `NZBDAV_CONFIG__PROWLARR__SYNC_INTERVAL_MINUTES` | `60` |

Use **Test Connection** to verify the URL and API key, save the settings, then use **Sync now**. Automatic sync runs on the configured interval (5–10080 minutes). The URL may include a Prowlarr URL base such as `http://prowlarr:9696/prowlarr`; credentials, query strings, and fragments are not accepted.

Sync imports searchable Usenet indexers and points each one at Prowlarr's per-indexer Newznab proxy (`{prowlarrUrl}/{indexerId}/api`). Prowlarr owns each managed entry's name, proxy URL, API key, and enabled state. InfiniDysk preserves local tuning such as rate limits, filters, category overrides, proxy, TLS, timeout, max response, and user agents. Manually configured indexers are never changed or removed.

Entries that disappear or become unsupported in Prowlarr are removed only when they are marked as Prowlarr-managed. Search profiles are updated in the same write for managed renames and removals. If Prowlarr is unavailable or returns an invalid response, InfiniDysk keeps the complete last-good indexer configuration and reports the failure in Settings.

!!! warning "Headless ownership"

    Pull sync writes imported indexers to SQLite, so `indexers.instances` must **not** be managed with `NZBDAV_CONFIG__INDEXERS__INSTANCES`. The Prowlarr connection settings themselves may be supplied by GUI or `NZBDAV_CONFIG__PROWLARR__...`. If `profiles.instances` is environment-managed, a Prowlarr rename or removal that requires profile-reference changes is rejected rather than leaving stale references.

## Per-indexer

| Control | Effect |
|---------|--------|
| Name / URL / API Key | Newznab endpoint |
| Search / Retrieve User-Agent | Optional overrides |
| Proxy URL | Optional override |
| Timeout / search result limit / max response | Optional overrides |
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
