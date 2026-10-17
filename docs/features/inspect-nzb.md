# NZB inspection

`POST /api/inspect-nzb` reports what InfiniDysk can establish about an NZB **without importing it**: the files inside its archives, how the archives are structured and encrypted, and whether InfiniDysk can serve them. It runs the same planning pipeline as a queue import (first segments, PAR2 descriptors, file names, lazy and eager RAR, 7z, split video and nested stored RAR expansion), so the report describes how an import would actually interpret the release.

Use it to validate a release before sending it to the queue, for example from an automation that must confirm a season pack contains the expected episodes. A report describes what InfiniDysk could establish during that request; it is not a guarantee of future provider availability.

## Request

`multipart/form-data`, authenticated with the admin API key (`x-api-key`):

| Field | Required | Meaning |
| --- | --- | --- |
| `nzbFile` | yes | The NZB document. |
| `password` | no | Archive password. Like an import, a `{{password}}` in `name` or the NZB `password` metadata is used when omitted. Send it in the form body, never in the query string. |
| `name` | no | Release name, used for planned mount names. |

```bash
curl -s -H "x-api-key: $API_KEY" \
  -F "nzbFile=@Show.S01.1080p.nzb" -F "name=Show.S01.1080p" \
  http://localhost:3000/api/inspect-nzb
```

## Response

The report is returned under `inspection`:

- `manifestComplete` — `true` only when the complete inner file namespace was established: planning finished, no limit was reached and no archive was left unexpanded. It says nothing about whether the content is readable.
- `files[]` — planned outputs, empty unless planning finished:
    - `rawPath` — the name as posted or as stored inside the archive. Use this to identify content.
    - `plannedName` — the name an import would plan to mount. Informational: a single video may be renamed to the release name.
    - `size`, `mediaExtension`, `nameSource` (`par2`, `subject`, `yencHeader` or `archiveHeader`), `archiveSetId`, `nestingDepth`.
- `archives[]` — one entry per RAR/7z archive set:
    - `archiveSetId`, `archiveType` (`rar` or `7z`), `nestingDepth`.
    - `encryption` — `none`, `data` or `unknown`.
    - `contentAccess` — `plain`, `password_required`, `password_validated`, `unsupported` or `unknown`. `password_validated` is reported only when the password was actually checked (RAR5 archives with a password-check value); a password for RAR4 or 7z is reported as `unknown` with a `password_not_validated` warning.
    - `streamSupported` — whether InfiniDysk supports streaming this archive or content shape at all (`false` for compressed 7z; `null` when not established).
    - The two are independent: `streamSupported` describes the shape, while `contentAccess` describes whether this inspection had what it needs, such as a password, to read the content. `streamSupported: true` with `contentAccess: password_required` means a supported archive that needs a password.
- `diagnostics` — `missingFirstSegments` (observed, never inferred), `warnings`, `failure` (`kind`, `stage`, `message`) when planning did not finish, the planning `stages` entered, and `articleRequests`.
    - `articleRequests` counts article requests the inspection issued to InfiniDysk's provider client, per segment and including requests that failed. It is not a count of physical provider attempts: failover and retries between providers are not counted, and some requests may be served from InfiniDysk's caches.

Failure kinds are `missing_articles`, `provider_unavailable`, `timeout`, `limit_exceeded`, `unsupported`, `password` and `failed`. A failure never produces a partial file list.

A complete manifest with `contentAccess: password_required` means the files are known but cannot be read without a password; an incomplete manifest with a `provider_unavailable` or `timeout` failure means nothing was learned about the contents.

## Side effects

Inspection fetches bounded metadata — first segments, PAR2 descriptors and archive headers — through the normal provider connections, so it consumes provider traffic and can update provider statistics. It does **not** create queue or history entries, publish WebDAV items, write STRM files or symlinks, contact Sonarr or Radarr, or record missing articles in the caches that later imports and playback use to fail fast or skip a provider. It still honours misses those caches already hold.

## Limits

The limits are defensive guardrails, sized to accommodate large season packs and archive sets while bounding the work a single request can cause. Inspection stops with `manifestComplete: false` and a `limit_exceeded` failure when one is reached: NZB size (50 MiB), NZB files (1,000), archive sets (200), reported files (5,000), article requests (3,000) and a budget of 2 GiB of NZB-declared article sizes (a budget, not a measurement of bytes transferred). Nested stored archives are expanded up to three levels deep; deeper archives stay unexpanded and leave the manifest incomplete. Each inspection has a two-minute time limit and stops when the request is cancelled.

One inspection runs at a time, because inspections share provider connections with playback and imports. A request made while another inspection is running is rejected immediately with HTTP `429` rather than queued; retry when the running inspection finishes.
