# SABnzbd API compatibility

InfiniDysk implements the SABnzbd-compatible operations used by Sonarr, Radarr, and similar download clients. It is not a complete replacement for SABnzbd's administrative API.

## Supported operations

- `version`, `status`, `fullstatus`, `get_config`, and `get_cats`
- `server_stats` and `warnings` [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }
- `addfile` and `addurl`
- `queue` listing and `queue&name=delete`, `queue&name=pause`, `queue&name=resume`, `queue&name=priority`, `queue&name=move`, and `queue&name=change_cat` [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }
- `pause` / `resume` (also `queue&name=pause` / `queue&name=resume`) and `speedlimit` [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }
- `change_cat` for per-job category changes on queued items [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }
- `retry` for failed history re-queue (single or bulk) [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }
- `history` listing and `history&name=delete`

Queue and history filters accept both `cat` and `category`. The default category sentinel returned by `get_cats` is `*`.

## Pause, resume, and speed limit [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

`mode=pause` / `mode=resume` stop and restart **new** queue dequeues. Workers already downloading finish naturally unless a per-job pause cancels them. WebDAV mounts keep serving — pause does not interrupt playback. Queue JSON reports `paused` accurately. Items added with SAB priority `-2` (Paused) are skipped until their priority changes; queue slots report `status: Paused` for those jobs.


Per-job pause and resume accept UUID(s) via `value` (comma-separated or repeated) or a JSON body `{"nzo_ids":["…"]}` on `mode=pause`, `mode=resume`, and the `queue&name=pause` / `queue&name=resume` aliases. Without ids, pause/resume applies to the whole queue coordinator (legacy global behavior).

`mode=queue&name=priority` sets priority for one or more jobs. Pass the SAB priority code as `value2` (or `priority`): `-2` Paused, `-1` Low, `0` Normal, `1` High, `2` Force. Paused uses the same per-job pause path as `queue&name=pause`.

`mode=change_cat` sets category for queued jobs (not actively downloading). Pass `cat` / `category` plus job id(s) in `value` or `nzo_ids`. Categories must match configured API categories.

`mode=retry` re-queues failed history items. Accepts a single `value` id or multiple ids (comma-separated / repeated `value`, or `nzo_ids` JSON). Bulk retry returns `nzo_ids` for successes and a `failed` array with per-item errors when some items cannot be retried. History bulk actions in the admin UI do not change category on retry (category is copied from the history row).

`mode=speedlimit` is **accepted and stored** and reflected in queue JSON (`speedlimit` / `speedlimit_abs`). Byte-accurate download throttling is **not** enforced yet — that work is tracked in [#375](https://github.com/infinidysk/infinidysk/issues/375).

Queue JSON reports live throughput for in-progress jobs [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }: per-slot `timeleft` (`H:MM:SS`, or `D:HH:MM:SS` past 24h), plus queue-level `timeleft`, `kbpersec` (KB/s), and `speed` (human units such as `1.3 M`). Queued and paused slots report `0:00:00`. `status` / `fullstatus` include `paused`, `speedlimit`, and `speedlimit_abs`; live speed stays on `mode=queue`.

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

Queue delete accepts UUID(s), repeated `value` parameters, `value=all`, or `value=all` with `cat` / `category` to clear only that category. SAB `del_files=1` has no extra effect (no incomplete-download directory).

History delete accepts UUIDs, `value=all`, or `value=failed`. The admin UI can delete mounted content for completed jobs with InfiniDysk-specific `del_completed_files=1` — download clients should **not** send this after importing a symlink/STRM, or playback sources disappear. The history UI also offers **Clear failed** and **Clear all** actions that call `history&name=delete` with `value=failed` or `value=all`.

[SABnzbd settings](../configuration/sabnzbd.md)
