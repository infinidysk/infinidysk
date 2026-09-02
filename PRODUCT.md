# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Self-hosted InfiniDysk administrators configuring Usenet streaming for Plex,
Emby, Jellyfin, Radarr, or Sonarr. Operators range from first-time installers to
existing users revisiting configuration after an upgrade.

## Product Purpose

InfiniDysk mounts NZB documents as a virtual WebDAV filesystem and streams media
directly from Usenet without first storing complete media files. Its admin UI
must help operators reach a reliable configuration while keeping advanced
controls available in Settings.

## Positioning

InfiniDysk combines on-demand Usenet streaming, a SABnzbd-compatible download
client API, symlink or STRM import workflows, and operational health and repair
tooling in one self-hosted product.

## Operating Context

The product normally runs as a Docker image with a .NET backend and React Router
admin UI. Symlink libraries use an rclone WebDAV sidecar and are typically
consumed by Plex. STRM libraries are typically consumed directly by Emby or
Jellyfin. Radarr and Sonarr can send downloads through the SAB-compatible API and
can also be registered with InfiniDysk for monitoring and replacement actions.

## Capabilities and Constraints

- Configuration may be persisted in the operational database or owned by
  authoritative `NZBDAV_CONFIG__...` environment variables.
- Symlink setup uses rclone VFS caching and disables InfiniDysk segment caching.
- STRM setup enables segment caching and requires a completed-downloads path and
  a media-server-reachable Base URL.
- External rclone and Arr services may be tested, but temporary unavailability
  must not prevent an administrator from completing setup.
- Configuration changes are reviewed and applied together. Existing imported
  files are not automatically converted when import strategy changes.
- Read-only users may inspect configuration but cannot mutate it.

## Brand Commitments

Preserve the InfiniDysk name, existing logo, direct operator-focused voice, and
the established daisyUI `night` application language.

## Evidence on Hand

Product behavior and setup guidance are documented in `README.md`, `AGENTS.md`,
and `docs/`. Existing operational UI patterns live under `frontend/app`, and the
product logo is available in `frontend/public`.

## Product Principles

- Prefer safe, understandable defaults over exposing every advanced setting.
- Explain consequences before changing storage, import, or integration behavior.
- Keep operator secrets masked and environment-owned values authoritative.
- Make recovery and later reconfiguration straightforward.
- Preserve stream-first behavior and bounded local storage.

## Accessibility & Inclusion

Setup controls must be keyboard operable, explicitly labelled, responsive, and
understandable without relying on color alone.