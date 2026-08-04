# Stremio via AIOStreams

Stream Usenet on demand in Stremio using [AIOStreams](https://github.com/Viren070/AIOStreams). Upstream guide: [AIOStreams Usenet docs](https://docs.aiostreams.viren070.me/guides/usenet/).

## Configure the InfiniDysk service

In AIOStreams → **Services** → **InfiniDysk**:

| Setting | Value |
|---------|-------|
| URL | `http://nzbdav:3000` on the same Docker network, or your HTTPS URL |
| Public URL | Leave blank when using the AIOStreams proxy; otherwise HTTPS reachable by players |
| API Key | InfiniDysk **Settings → SABnzbd** |
| WebDAV Username / Password | **Settings → WebDAV** |
| AIOStreams Auth Token | Recommended `username:password` from `AIOSTREAMS_AUTH` |

Providing the auth token lets AIOStreams proxy streams — keeps InfiniDysk private and avoids protocol mismatches.

## Newznab addon

**Addons → Marketplace → Usenet → Newznab**: add each indexer (URL, API key). Search mode **Both** when your API budget allows.

## Install to Stremio

**Save & Install** in AIOStreams, then install the addon in Stremio.

## Search profiles (optional)

Expose InfiniDysk indexers as Newznab/Addon/JSON adapters — [Search profiles](../configuration/profiles.md) and [Indexer search](../features/indexer-search.md).

## Related

[Streaming-only use case](../use-cases/streaming-only.md) · [Watchtower](../features/watchtower.md)
