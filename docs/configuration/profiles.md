# Search profiles

Named profiles select which indexers to query and which output adapters to expose. Stored as `profiles.instances`
(`NZBDAV_CONFIG__PROFILES__INSTANCES` — see [headless](headless.md)).

| Control | Effect | Default |
|---------|--------|---------|
| Name | Profile label | — |
| Indexers | Leave all unchecked = every enabled indexer | all |
| JSON Search API | Vendor-neutral JSON adapter | on if unset |
| Newznab | For Prowlarr/Sonarr/Radarr | on if unset |
| Addon | Manifest-based addon endpoint | on if unset |
| Query fallback — Movies | Extra title searches when ID lookup is short | Off; threshold `3` |
| Query fallback — TV | Off / Title+episode / Broad | Off; threshold `3` |

Treat each generated **token** as a secret. Adapter URLs look like:

- Newznab: `http://nzbdav:3000/adapters/newznab/{token}`
- Addon: `http://nzbdav:3000/adapters/addon/{token}/manifest.json`
- JSON: `GET /api/search/{token}/lookup?...`

!!! tip

    Fallback queries spend per-indexer hit/rate limits.

Play link lifetime is under [Watchdog](watchdog.md) (`play.resolution-cache-ttl-hours`) [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }.
