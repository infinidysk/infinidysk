# Configuration

Day-to-day settings live in the admin UI (**Settings**) and persist in SQLite under `/config`. Use this section as a walkthrough of every Settings tab.

For **authoritative headless** configuration of those same Settings keys via `NZBDAV_CONFIG__...`, see **[Headless environment configuration](headless.md)** [since 0.9.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0){ .nzbdav-since }. Process wiring and legacy fallbacks remain on **[Environment variables](environment-variables.md)**.

!!! note "Docs track latest"

    Settings marked [since 0.8.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0){ .nzbdav-since } (or another version) were introduced in that release. Older images will not show those controls.

## Settings hub

<div class="grid cards" markdown>

-   **Providers & search** — [Usenet](usenet.md) · [Indexers](indexers.md) · [Search profiles](profiles.md)
-   **Queue & import** — [Queue](queue.md) · [SABnzbd](sabnzbd.md)
-   **Playback & files** — [Streaming](streaming.md) · [WebDAV](webdav.md)
-   **Automation** — [Watchdog](watchdog.md) · [Preflight](preflight.md) · [Watchtower](watchtower.md) · [Warden](warden.md)
-   **Integrations** — [Arr Apps](arrs.md) · [Rclone](rclone.md)
-   **System** — [Health & repairs](repairs.md) · [Maintenance](maintenance.md) · [Backup](backup.md) · [Support](support.md) · [Migration (experimental)](../guides/altmount-migration.md) [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }
-   **Headless / ops** — [OIDC / SSO](oidc.md) · [Headless ENV config](headless.md) · [Environment variables](environment-variables.md)

</div>

!!! tip "Config vs env"

    Most tunables are UI/`ConfigItems` keys. Map any documented config key to `NZBDAV_CONFIG__...` with the [naming rule](headless.md#naming-algorithm) for authoritative headless setup. Separate domains (frontend admin account, Warden `warden.db`, restore actions) are not part of that overlay — see [headless out of scope](headless.md#what-is-out-of-scope-v09). Process variables (`CONFIG_PATH`, ports, auth cookies) and legacy Settings *fallbacks* stay on [environment variables](environment-variables.md).
