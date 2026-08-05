# NzbDAV is becoming InfiniDysk

**InfiniDysk** is the new name for this project. The product, maintainers, release
history, and commitment to streaming directly from Usenet remain the same.

The tagline **"The NzbDAV SuperFork"** keeps that lineage visible while giving
the project a distinct name that is easier to find and less likely to be
confused with the original `nzbdav-dev/nzbdav` repository.

## Why the new name?

The current name describes two implementation details — NZB documents and
WebDAV — but it does not capture the result: an effectively infinite media
library with almost none of the media stored locally.

**InfiniDysk** combines that idea with an intentionally distinctive spelling:
an infinite virtual disk, streamed on demand.

## The move is complete

The project now lives at its new home:

- GitHub: [github.com/infinidysk/infinidysk](https://github.com/infinidysk/infinidysk)
- Container image: `ghcr.io/infinidysk/infinidysk` (canonical)
- Docker Hub mirror: `docker.io/infinidysk/infinidysk`
- Documentation: [www.infinidysk.com](https://www.infinidysk.com/)

All old GitHub links, git remotes, issues, stars, and releases redirect
automatically to the new repository.

## How do I switch?

Change the image name — nothing else:

```yaml
# before
image: ghcr.io/nzbdav/nzbdav:latest
# after
image: ghcr.io/infinidysk/infinidysk:latest
```

Keep the same `/config` volume, ports, environment variables, and media
mounts. Every historical version tag was copied to the new namespace with
identical digests, so pinned versions swap 1:1. Version numbers and release
history continue unchanged.

## Will the old image stop working?

No abrupt cutoff. The old `ghcr.io/nzbdav/nzbdav` path continues receiving
releases during a transition period, and old version tags will remain pullable
indefinitely. Images published to the old path since the move show a persistent
in-app reminder with the new image name.

When the transition period ends, the old path stops receiving new releases —
installations that never switched keep working on their last version, with the
migration instructions still visible in the app.

## Is this still the same fork?

Yes. InfiniDysk remains the maintained successor and drop-in upgrade from
`nzbdav-dev/nzbdav v0.6.4`, with the same source history and MIT license. The
project will keep crediting its upstream and community-fork heritage.
