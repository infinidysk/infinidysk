# ADR-004: SABnzbd-API compatibility as the Sonarr/Radarr integration strategy

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: none directly listed in §10 — this is a compatibility/adoption decision, not a performance one

## Context

NzbDav needs Sonarr and Radarr to treat it as a download client with zero special integration code
on their side.

## Decision

Implement a single `SabApiController` that mimics SABnzbd's `mode`-query-param dispatch shape
exactly (addfile/addurl/queue/history/status/version/categories/config, each a nested
`BaseController`), rather than designing a RESTful API of NzbDav's own choosing and asking
Sonarr/Radarr to support it.

## Consequences

- **Positive**: Sonarr/Radarr both already ship a generic SABnzbd client — this integration works
  with zero code changes on the arr side. This is unambiguously the correct call.
- **Negative**: NzbDav is permanently bound to SABnzbd's response JSON shape and `mode`-param
  dispatch style for this surface, rather than a resource-oriented design; the SAB surface also
  necessarily inherits SABnzbd's query-string API-key convention (§11 — secrets in query strings land
  in logs/referrers), which can't be changed without breaking the arr clients that speak it.

## Alternatives considered

Neither Sonarr nor Radarr exposes a stable third-party download-client plugin API today, so a native
plugin integration isn't a practical alternative — this was confirmed as a non-option rather than
rejected on preference. **No actionable alternative exists; this decision stands as the only
practical integration point.**
