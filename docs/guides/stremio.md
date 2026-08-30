# Stremio via AIOStreams

Stream Usenet on demand in Stremio using [AIOStreams](https://github.com/Viren070/AIOStreams). Upstream guide: [AIOStreams Usenet docs](https://docs.aiostreams.viren070.me/guides/usenet/).

There are two supported setups. Choose one. Do not combine them for the same indexers.

## Path A — InfiniDysk owns search (recommended)

Use this when InfiniDysk should search your Newznab indexers and return playable streams. AIOStreams only filters, sorts, and formats those streams.

1. Configure Usenet providers and Newznab indexers in InfiniDysk.
2. Set a public base URL / reverse proxy so AIOStreams and the playback client can reach InfiniDysk over HTTPS.
3. Create a **Search Profile**, choose indexers, enable **Addon**, and configure query fallback if you want it.
4. Copy the Addon **manifest URL**. Treat the full URL as a secret: it contains the profile token.
5. In AIOStreams Marketplace:
   - After a release that includes the dedicated **InfiniDysk** preset, add that preset and paste the manifest URL. No InfiniDysk/NzbDAV service credentials are required.
   - Until that preset is released, add **Custom Addon** and paste the same manifest URL. Custom Addon fetches streams but does **not** report AIOStreams' final order to `/failover_order`.
6. Do **not** also add the InfiniDysk/NzbDAV service, and do **not** duplicate those indexers as AIOStreams Newznab addons.
7. Save and install the combined AIOStreams addon in Stremio.

If InfiniDysk is mounted under a path such as `/nzbdav`, the copied manifest URL already includes that prefix.

Give AIOStreams enough timeout for indexer search. Play links expire; search again after the Watchdog search-link lifetime. The first play of a new release may take time because `notWebReady` is intentional: InfiniDysk resolves and queues the NZB before redirecting to `/view`. Ready and verified markers are best-effort and do not guarantee the provider still has the articles.

AIOStreams proxying is optional on this path. Direct play URLs work when the player can reach InfiniDysk.

## Path B — AIOStreams owns search

Use this when AIOStreams should search Newznab addons itself and hand NZBs to InfiniDysk only for playback.

In AIOStreams → **Services** → **InfiniDysk**:

| Setting | Value |
|---------|-------|
| URL | `http://nzbdav:3000` on the same Docker network, or your HTTPS URL |
| Public URL | Leave blank when using the AIOStreams proxy; otherwise HTTPS reachable by players |
| API Key | InfiniDysk **Settings → SABnzbd** |
| WebDAV Username / Password | **Settings → WebDAV** |
| AIOStreams Auth Token | Recommended `username:password` from `AIOSTREAMS_AUTH` |

Providing the auth token lets AIOStreams proxy streams — keeps InfiniDysk private and avoids protocol mismatches.

Then in **Addons → Marketplace → Usenet → Newznab**, add each indexer (URL, API key). Search mode **Both** when your API budget allows.

**Save & Install** in AIOStreams, then install the addon in Stremio.

## Repairs without *Arr [since 1.2.5](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.5){ .nzbdav-since }

**Background Repairs** is on by default: health checks, PAR2 reconstruction of recoverable gaps,
and limited damage tolerance run without a Library Directory or *Arr. Those are only necessary to
replace linked library items. To auto-remove a broken unlinked item after repeated playback failures,
set **Repair After Streaming Failures** above `0`; the default (`0`) keeps the item and marks it
**Action needed**. Turn the toggle off if you do not want idle STAT or PAR2 work.

## Security and reachability

- The profile token in the Path A manifest URL is a capability credential. Do not paste it into public chats or commit it.
- Players and AIOStreams must be able to reach the public InfiniDysk URL, including any URL-base prefix.
- HTTPS and a reverse proxy in front of the container are the usual production setup.

## Related

[Search profiles](../configuration/profiles.md) · [Indexer search](../features/indexer-search.md) · [Streaming-only use case](../use-cases/streaming-only.md) · [Watchtower](../features/watchtower.md)
