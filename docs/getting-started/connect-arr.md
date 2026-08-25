# Connect Radarr / Sonarr

InfiniDysk speaks the SABnzbd API surface \*Arr apps expect. Hostnames below assume a shared Docker network.

## Add the download client

In Radarr or Sonarr → **Settings** → **Download Clients** → **Add** → **SABnzbd**:

| Setting | Value |
|---------|-------|
| Name | `InfiniDysk` |
| Host | `nzbdav` (or reachable hostname) |
| Port | `3000` |
| API Key | InfiniDysk **Settings → SABnzbd → API Key** |
| Category | Match categories you configured (e.g. `movies`, `tv`) |

Test the connection. Prefer `addfile` when clients can upload NZB bytes; `addurl` to private indexers needs [Trusted local hosts](../configuration/sabnzbd.md) [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }.

## Register \*Arr in InfiniDysk

**Settings → Radarr/Sonarr**:

1. Add Radarr host (`http://radarr:7878`) + API key.
2. Add Sonarr host (`http://sonarr:8989`) + API key.
3. Configure **Automatic Queue Management** for stuck import messages (remove / blocklist / search).

Registered and enabled instances light up the Overview **Arr Health** widget [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }. See [Radarr/Sonarr settings](../configuration/arrs.md).

## Align import paths

| Strategy | InfiniDysk setting | \*Arr / media server |
|----------|----------------|---------------------|
| Symlinks | Rclone mount dir e.g. `/mnt/remote/nzbdav` | Same path must exist inside \*Arr |
| STRM | Completed dir e.g. `/mnt/completed-downloads` + Base URL | Media server must reach Base URL |

Enable **Repairs** with a **Library Directory** once paths and \*Arr instances exist — [Repairs](../configuration/repairs.md).

## Next

[Infinite library use case](../use-cases/infinite-library-arr.md) · [SABnzbd API details](../features/sab-api.md)
