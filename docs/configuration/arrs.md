# Arr Apps

Supported Arr app credentials and automatic stuck-queue handling. Radarr and
Sonarr are supported today; this settings area is intended to accommodate
additional Arr apps such as Lidarr in the future. Config key: `arr.instances`
(`NZBDAV_CONFIG__ARR__INSTANCES` — see [headless](headless.md)).

| Control | Effect |
|---------|--------|
| Radarr Host / API Key | Test Conn available |
| Sonarr Host / API Key | Test Conn available |
| Automatic Queue Management | Per status message: Do Nothing / Remove / Remove+Blocklist / Remove+Blocklist+Search |

**Test Conn** validates host reachability, API key auth, and a successful API response
(`GET /api`). It works with already-saved (masked) API keys — you do not need to re-enter
the key after reload. Failures show a reason (authentication, HTTP status, or network error).

Only **Usenet** queue items are acted upon. Typical mappings:

- **Remove, Blocklist, and Search** — no eligible files, samples, no audio tracks
- **Remove and Blocklist** — not an upgrade / custom format
- **Remove** — already imported
- **Do Nothing** — ID mismatches and similar manual-import cases

Any action other than **Do Nothing** tells the Arr to delete the queue record with `removeFromClient=true`. The Arr then removes the download from InfiniDysk History even when its own **Remove Completed** checkbox is off. That is independent of mounted files, which stay.

[Connect *Arr](../getting-started/connect-arr.md)
