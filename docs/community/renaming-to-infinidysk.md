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

## What changes now?

- The app, documentation, and community branding now use **InfiniDysk**.
- A new logo and favicon identify the project.
- Documentation is now published at
  [www.infinidysk.com](https://www.infinidysk.com/).
- The current repository and Docker image path remain unchanged during this
  transition stage.

There is no application configuration or deployment change to make yet.

## What will change later?

The project will move to:

- GitHub: `github.com/infinidysk/infinidysk`
- Container image: `ghcr.io/infinidysk/infinidysk`

Do not switch the image reference until the new package is published and the
migration announcement says it is ready.

When it is available, moving will only require changing the image name. Keep
the same `/config` volume, ports, environment variables, and media mounts.
Existing version numbers and release history will continue.

## Will the old image stop working?

No abrupt cutoff is planned. The old `ghcr.io/nzbdav/nzbdav` path will continue
receiving releases during a transition period. Images published there after the
move will show an in-app reminder with the new path and the final support date.

Old version tags will remain pullable. The final image published at the old
path will keep the migration instructions visible for installations that have
not switched yet.

## Is this still the same fork?

Yes. InfiniDysk remains the maintained successor and drop-in upgrade from
`nzbdav-dev/nzbdav v0.6.4`, with the same source history and MIT license. The
project will keep crediting its upstream and community-fork heritage.

## When is the move?

The new identity is being introduced first so the move is not a surprise. A
final date will be posted here, in the app, in release notes, and in Discord
after the new repository and container publishing flow have been verified.
