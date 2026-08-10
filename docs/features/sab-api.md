# SABnzbd API compatibility

InfiniDysk implements the SABnzbd-compatible operations used by Sonarr, Radarr, and similar download clients. It is not a complete replacement for SABnzbd's administrative API.

## Supported operations

- `version`, `status`, `fullstatus`, `get_config`, and `get_cats`
- `server_stats` and `warnings` [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }
- `addfile` and `addurl`
- `queue` listing and `queue&name=delete`
- `pause` / `resume` (also `queue&name=pause` / `queue&name=resume`) and `speedlimit` [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }
- `history` listing and `history&name=delete`

Queue and history filters accept both `cat` and `category`. The default category sentinel returned by `get_cats` is `*`.

## Pause, resume, and speed limit [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

`mode=pause` / `mode=resume` stop and restart **new** queue dequeues. Workers already downloading finish naturally. WebDAV mounts keep serving — pause does not interrupt playback. Queue JSON reports `paused` accurately. Items added with SAB priority `-2` (Paused) are skipped until their priority changes; queue slots report `status: Paused` for those jobs.

`mode=speedlimit` is **accepted and stored** and reflected in queue JSON (`speedlimit` / `speedlimit_abs`). Byte-accurate download throttling is **not** enforced yet — that work is tracked in [#375](https://github.com/infinidysk/infinidysk/issues/375).

## Intentional differences

- Job identifiers are UUIDs rather than `SABnzbd_nzo_*` strings. Treat them as opaque values.
- Responses are JSON. The `output=xml` option is not implemented.
- Queue and history roots contain the fields needed by supported download clients rather than every SABnzbd UI field.
- History has no separate archive tier. `history&name=delete` permanently removes matching history rows.
- **Ignore SAB history limit** can ignore a client's `limit`; InfiniDysk still enforces a server-side maximum page size.
- Authentication failures use HTTP error status codes instead of always returning HTTP 200 with an error body.
- `server_stats` aggregates provider bandwidth from retained `ProviderHourly` rollups (plus folded lifetime totals for all-time bytes). The per-server `daily` map and article counters are bounded by the hourly retention window — pruned buckets are not reconstructed.
- `warnings` returns recent Warning-and-above log entries from the in-memory buffer. `name=clear` is accepted for SAB client compatibility but does not clear the buffer (support packs rely on it). Use Settings → Support to collect the full warning log.

## `addurl` and private / LAN hosts [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }

`mode=addurl` fetches the NZB from the URL the download client supplies. Before each hop (including redirects), InfiniDysk rejects destinations that resolve to a non-public IP — an SSRF guard that also blocks Docker DNS / RFC1918 indexers unless allowlisted.

Allow destinations under **Settings → SABnzbd → Trusted local hosts** (`api.addurl-trusted-hosts`):

| Entry | Meaning |
|-------|---------|
| `prowlarr` / `hydra.lan` | Hostname match (case-insensitive) |
| `192.168.1.50` | Exact IP |
| `192.168.1.0/24` / `fd00::/8` | CIDR for resolved addresses |
| `*` | Trust any non-public address (disables the guard) |

Only list hosts you control. Prefer `mode=addfile` when the client can upload the NZB itself.

The same allowlist can be set with `TRUSTED_INTERNAL_HOSTS` when the UI setting is empty — [Environment variables](../configuration/environment-variables.md).

## Delete behavior

Queue delete accepts UUID(s), repeated `value` parameters, or `value=all`. SAB `del_files=1` has no extra effect (no incomplete-download directory).

History delete accepts UUIDs, `value=all`, or `value=failed`. The admin UI can delete mounted content for completed jobs with InfiniDysk-specific `del_completed_files=1` — download clients should **not** send this after importing a symlink/STRM, or playback sources disappear.

[SABnzbd settings](../configuration/sabnzbd.md)
