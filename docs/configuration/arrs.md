# Arr Apps

Supported Arr app credentials and automatic stuck-queue handling. Radarr and
Sonarr are supported today; this settings area is intended to accommodate
additional Arr apps such as Lidarr in the future. Config key: `arr.instances`
(`NZBDAV_CONFIG__ARR__INSTANCES` — see [headless](headless.md)).

| Control | Effect |
|---------|--------|
| Name | Optional display name (Overview Arr Health falls back to the host URL) |
| Enabled | When off, the instance is excluded from queue management, Arr-linked repairs, **and** Arr Health |
| Radarr Host / API Key | Test Conn available |
| Sonarr Host / API Key | Test Conn available |
| Arr Health | Master toggle `arr.health-enabled` (`NZBDAV_CONFIG__ARR__HEALTH_ENABLED`) — default on |
| Automatic Queue Management | Per status message: Do Nothing / Remove / Remove+Blocklist / Remove+Blocklist+Search |

**Test Conn** validates host reachability, API key auth, and a successful API response
(`GET /api`). It works with already-saved (masked) API keys — you do not need to re-enter
the key after reload. Failures show a reason (authentication, HTTP status, or network error).

Only **Usenet** queue items that Radarr or Sonarr reports as completed or awaiting import
are acted upon. Downloads that are still queued or downloading are never removed by these
rules. Typical mappings:

- **Remove, Blocklist, and Search** — no eligible files, samples, no audio tracks
- **Remove and Blocklist** — not an upgrade / custom format
- **Remove** — already imported
- **Do Nothing** — ID mismatches and similar manual-import cases

Any action other than **Do Nothing** tells the Arr to delete the queue record with `removeFromClient=true`. The Arr then removes the download from InfiniDysk History even when its own **Remove Completed** checkbox is off. That is independent of mounted files, which stay.

## Replacement-search safety limit [since 1.2.4](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.4){ .nzbdav-since }

**Remove, Blocklist, and Search** is bounded to **3 automatic replacement searches in
30 minutes per Radarr movie or Sonarr episode** by default. Once a media item reaches
that limit, InfiniDysk still removes and blocklists its rejected release but does not
start another automatic search. Adjust both values in **Automatic queue management**
when a library needs a stricter or more permissive policy. The same per-media cap also
bounds replacement searches triggered by health-check repairs, which additionally keep
their own per-library-file rate limit.

The queue-monitor warning includes the matching Arr import message, so support packs
show the actual rejection (for example, an archive, an ineligible file, or an import
path problem) that led to the action.

Disabling an instance opts it out of stuck-queue actions, Arr-linked repairs, and health polling. Use **Arr Health** off when you still want queue rules without Overview polling.

## Arr Health on the Overview dashboard [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

When at least one Radarr or Sonarr instance is configured **and enabled**, Overview shows a compact **Arr Health** section: instance reachability, imports in the selected dashboard window, median/P95 handoff latency (InfiniDysk download completed → Arr `DownloadFolderImported`), queue depth, and items waiting unusually long for import.

The feature is **completely dormant** otherwise:

- No Overview widget
- No background polling or Arr HTTP
- No metrics writes or Arr Health log lines

Turn off **Show Arr Health on Overview** (`arr.health-enabled`) to keep instances for queue rules/repairs without health polling.

Handoff events are stored in the local metrics database (`metrics.sqlite`) for **90 days**, so the Overview **All** window for this widget is effectively ~90 days. API keys are never sent to the widget.

Back up `/config` before upgrading: the metrics store gains an additive `ArrImportEvents` table that auto-applies on startup.

Headless `NZBDAV_CONFIG__ARR__INSTANCES` JSON that includes the new `Name` / `Enabled` properties **cannot roll back** to an older image (structured ENV config is not forward compatible — see [headless](headless.md)).

[Connect *Arr](../getting-started/connect-arr.md)
