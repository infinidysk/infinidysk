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
| Result ordering [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since } | Keep grabs/size/date order, or sort by resolution / resolution+source | Off |
| Query fallback — Movies | Extra title searches when ID lookup is short | Off; threshold `3` |
| Query fallback — TV | Off / Title+episode / Broad | Off; threshold `3` |

## Result matching and ordering [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

When an indexer has **Strict matching** enabled, Search Profiles reject movie releases whose explicit
year is clearly wrong once a canonical title and year are known. A missing year is not treated as a
mismatch. TV, season, and episode strict matching stay title- and `SxxExx`-based; a show premiere
year is never applied to episode results.

**Result ordering** is profile-scoped and defaults to **Off**, so existing profiles keep the previous
grabs (when Prefer Downloaded is on), then size, then posted-date order. Choose:

- **Resolution** to prefer 2160p, then 1080p, 720p, then SD, before those tie-breakers.
- **Resolution + source** to keep that resolution order and then prefer Remux, BluRay, WEB-DL,
  WEBRip, HDTV, DVD, unknown, and CAM/TS.

Resolution always ranks above source. Watchtower-verified candidates can still be boosted above this
ordinary indexer order. Settings → Indexers manual search does not use Search Profiles and is
unchanged.

Treat each generated **token** as a secret. Adapter URLs look like:

- Newznab: `http://nzbdav:3000/adapters/newznab/{token}`
- Addon: `http://nzbdav:3000/adapters/addon/{token}/manifest.json`
- JSON: `GET /api/search/{token}/lookup?...`

!!! tip

    Fallback queries spend per-indexer hit/rate limits.

Play link lifetime is under [Watchdog](watchdog.md) (`play.resolution-cache-ttl-hours`) [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }.
