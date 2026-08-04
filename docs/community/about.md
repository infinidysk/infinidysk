# About

InfiniDysk is a single product: a WebDAV + SABnzbd-compatible Usenet streaming server.

## Ecosystem

Playback quality depends on the full stack. UsenetSharp, RapidYencSharp, and SharpCompress are developed in this repository under `libs/`; the native **rapidyenc** library remains a [standalone repo](https://github.com/nzbdav/rapidyenc) consumed as a git submodule. Connection, yEnc, and archive fixes land with the product instead of waiting on a fragmented NuGet publish cycle.

## Heritage

This project is a maintained fork of [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav), with an **official drop-in upgrade path from `v0.6.4`**. Operators have also successfully migrated from community forks such as [Pukabyte/nzbdav](https://github.com/Pukabyte/nzbdav) and [qooode/nzbdavex](https://github.com/qooode/nzbdavex). See [Migration paths](../getting-started/migration.md) for steps and caveats. Ideas and contributions were also absorbed from elfhosted, kha-kis, mrghxst, and others.

Historical stack announcement notes: [0.7.x coordinated release](history/release-0.7.md). Prefer the [Changelog](changelog.md) for current releases.

## License

[MIT](https://github.com/nzbdav/nzbdav/blob/main/LICENSE).

InfiniDysk is intended for legally obtained or public domain content only.
