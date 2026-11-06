# Changelog

## [2.0.0-beta.6](https://github.com/infinidysk/infinidysk/compare/v2.0.0-beta.5...v2.0.0-beta.6) (2026-11-06)


### Features

* **db:** choose where infinidysk keeps its database and files ([#1858](https://github.com/infinidysk/infinidysk/issues/1858)) ([c1f6f93](https://github.com/infinidysk/infinidysk/commit/c1f6f936462bd2e2a003971ff67c88dd28e331fe))
* **ui:** the Queue link shows when a download needs you in the Arr ([#1849](https://github.com/infinidysk/infinidysk/issues/1849)) ([2c1a095](https://github.com/infinidysk/infinidysk/commit/2c1a0953d2aa2a3c06f1044a8255c07dad959b22))


### Bug Fixes

* **api:** link the update notice to the release notes of the newer tag ([#1860](https://github.com/infinidysk/infinidysk/issues/1860)) ([005812a](https://github.com/infinidysk/infinidysk/commit/005812a4248f4d5685257dd76dbb429181e4aff2))
* **ui:** open tabs no longer flood the server with refreshes during large cleanups ([#1850](https://github.com/infinidysk/infinidysk/issues/1850)) ([ae1b19c](https://github.com/infinidysk/infinidysk/commit/ae1b19ca39a4d3abddc81484a7fadd74177393c5))


### Performance Improvements

* **db:** index the health summary count ([#1851](https://github.com/infinidysk/infinidysk/issues/1851)) ([ca5c4d0](https://github.com/infinidysk/infinidysk/commit/ca5c4d03437eb343890301a542ec8d17d3932675))
* **queue:** imports into large categories no longer read every existing folder ([#1856](https://github.com/infinidysk/infinidysk/issues/1856)) ([3402409](https://github.com/infinidysk/infinidysk/commit/340240947b71cd7f5c99e8fb5fd66dfa8c1b4e88))


### Build System

* publish Linux binary archives with each release ([#1859](https://github.com/infinidysk/infinidysk/issues/1859)) ([cdedc4b](https://github.com/infinidysk/infinidysk/commit/cdedc4b4deb7fef5abed4b15c907cddda8870759))

## [2.0.0-beta.5](https://github.com/infinidysk/infinidysk/compare/v2.0.0-beta.4...v2.0.0-beta.5) (2026-11-06)


### Features

* **api:** better Lidarr support: more audio formats, faster cleanup after album deletes, and a tagging warning ([#1838](https://github.com/infinidysk/infinidysk/issues/1838)) ([e138a06](https://github.com/infinidysk/infinidysk/commit/e138a062aa1df9f9a85a0aaecf532a2e060fbc98))
* **arr:** support Sonarr v5 ([#1833](https://github.com/infinidysk/infinidysk/issues/1833)) ([b743f54](https://github.com/infinidysk/infinidysk/commit/b743f544313a0ad447687c8bcd7a678abab56a63))
* **auth:** show your identity provider's name on the SSO sign-in button ([#1835](https://github.com/infinidysk/infinidysk/issues/1835)) ([1845fe8](https://github.com/infinidysk/infinidysk/commit/1845fe8182b8f084da671ca399a12bff028500d0))
* **mount:** turn off the built-in FUSE mount when you do not need it, without warnings ([#1841](https://github.com/infinidysk/infinidysk/issues/1841)) ([d721677](https://github.com/infinidysk/infinidysk/commit/d721677513bc8f0e23005450114a2a6f93ea7379))


### Bug Fixes

* **db:** cleanup runs on SQLite no longer stop when other work writes to the database ([#1840](https://github.com/infinidysk/infinidysk/issues/1840)) ([6d676ab](https://github.com/infinidysk/infinidysk/commit/6d676ab28a308139bcf8c3d12e3446c91fd11837))
* **db:** failed maintenance runs and API errors now say what the database reported ([#1839](https://github.com/infinidysk/infinidysk/issues/1839)) ([43ac7fd](https://github.com/infinidysk/infinidysk/commit/43ac7fd1f399ef79ae9032c01e4276ef487a3924))
* **deps:** Bump the npm-minor-patch group in /ui with 3 updates ([#1844](https://github.com/infinidysk/infinidysk/issues/1844)) ([ba17029](https://github.com/infinidysk/infinidysk/commit/ba17029c9e28fd63a39ec257dbbed551f71c31cb))
* **health:** broken files from an Arr that cannot repair them are kept for you instead of deleted ([#1837](https://github.com/infinidysk/infinidysk/issues/1837)) ([c3bd0ba](https://github.com/infinidysk/infinidysk/commit/c3bd0baf90b13846da6aca35e26f945b106385e4))

## [2.0.0-beta.4](https://github.com/infinidysk/infinidysk/compare/v2.0.0-beta.3...v2.0.0-beta.4) (2026-11-05)


### ⚠ BREAKING CHANGES

* **api:** infinidysk no longer starts while a Readarr Arr instance is configured. Before upgrading, remove Readarr instances or change them to Chaptarr in Settings > Arr instances. If already upgraded, start the previous version to do so, or override the list with INFINIDYSK__ARR_INSTANCES.

### Features

* **api:** support Chaptarr instead of the abandoned Readarr ([#1827](https://github.com/infinidysk/infinidysk/issues/1827)) ([24e55b5](https://github.com/infinidysk/infinidysk/commit/24e55b51b6843a7df13364c2a310d77cf36a999c))
* **nntp:** add the news-central.giganews.com server to the Giganews profile ([#1824](https://github.com/infinidysk/infinidysk/issues/1824)) ([028d6fb](https://github.com/infinidysk/infinidysk/commit/028d6fb3663d9a266396ecd39cf7cc17e94bb98d))
* **ui:** backups get their own settings page ([#1826](https://github.com/infinidysk/infinidysk/issues/1826)) ([343cd7f](https://github.com/infinidysk/infinidysk/commit/343cd7f3340c5a0b3320cdec91f65c8e6a54d058))


### Bug Fixes

* **health:** a provider outage no longer marks good files as damaged ([#1829](https://github.com/infinidysk/infinidysk/issues/1829)) ([e8963ed](https://github.com/infinidysk/infinidysk/commit/e8963edac9355be750a6073988403993cd3986a9))
* **health:** repairs no longer replace a newer file the Arr imported from another download ([#1832](https://github.com/infinidysk/infinidysk/issues/1832)) ([107cabe](https://github.com/infinidysk/infinidysk/commit/107cabe2704dcb0ad23e703c4a36fc816ed64b57))
* **mount:** keep files referenced by legacy library links ([#1828](https://github.com/infinidysk/infinidysk/issues/1828)) ([4e1c914](https://github.com/infinidysk/infinidysk/commit/4e1c914b7ad927bbf205126199ff9955981ef2bc))
* **ui:** stalls on the overview count only pauses a viewer would notice ([#1830](https://github.com/infinidysk/infinidysk/issues/1830)) ([317497d](https://github.com/infinidysk/infinidysk/commit/317497d5cc93c013315f95b24172db5f271533e5))
* **webdav:** completed downloads no longer reappear right after an Arr import deletes them ([#1821](https://github.com/infinidysk/infinidysk/issues/1821)) ([41fb2f0](https://github.com/infinidysk/infinidysk/commit/41fb2f0d1878b80a2197dafe332e0d7a11a4bdf4))

## [2.0.0-beta.3](https://github.com/infinidysk/infinidysk/compare/v2.0.0-beta.2...v2.0.0-beta.3) (2026-11-05)


### Features

* **auth:** sign in with single sign-on (OIDC), carried over from 1.x ([#1812](https://github.com/infinidysk/infinidysk/issues/1812)) ([4fc3b6a](https://github.com/infinidysk/infinidysk/commit/4fc3b6a4e834c478313466e1af95aa9bbf0b7350))
* **import:** upgrade 1.x installs that keep their data in PostgreSQL ([#1815](https://github.com/infinidysk/infinidysk/issues/1815)) ([6ee6cbf](https://github.com/infinidysk/infinidysk/commit/6ee6cbfc686a4be55d4f04afcc4ce74af634d35a))


### Bug Fixes

* **arr:** skip the automatic-search wait when no release was blocklisted ([#1817](https://github.com/infinidysk/infinidysk/issues/1817)) ([23be919](https://github.com/infinidysk/infinidysk/commit/23be9194448dbb028bee7cb4c21378f2357610c2))
* **ui:** memory overview no longer counts reclaimable kernel cache as app memory ([#1813](https://github.com/infinidysk/infinidysk/issues/1813)) ([d4f5daf](https://github.com/infinidysk/infinidysk/commit/d4f5daf5991447d2726a5b22d9232c5d9c265c94))
* **ui:** season packs no longer repeat the same Arr import message per episode ([#1811](https://github.com/infinidysk/infinidysk/issues/1811)) ([c141857](https://github.com/infinidysk/infinidysk/commit/c1418571699a19c0ac9c60c2b60ba3353aa24c25))

## [2.0.0-beta.2](https://github.com/infinidysk/infinidysk/compare/v2.0.0-beta.1...v2.0.0-beta.2) (2026-11-04)


### Features

* **db:** back up your database nightly and restore it from the Maintenance page ([#1809](https://github.com/infinidysk/infinidysk/issues/1809)) ([0c40be7](https://github.com/infinidysk/infinidysk/commit/0c40be7d06360f75e2bca544b940744212ea6357))
* **db:** move from SQLite to PostgreSQL from the settings page ([#1807](https://github.com/infinidysk/infinidysk/issues/1807)) ([20a87fb](https://github.com/infinidysk/infinidysk/commit/20a87fbbb770026a549acd335c350ce01cb353c6))
* **mount:** read history shows which app started ffmpeg or ffprobe ([#1791](https://github.com/infinidysk/infinidysk/issues/1791)) ([25e114a](https://github.com/infinidysk/infinidysk/commit/25e114ac59e4738f9594a30c4ccb30d98d945817))
* **ui:** overview activity shows download speed by workload as bars ([#1810](https://github.com/infinidysk/infinidysk/issues/1810)) ([dfa8146](https://github.com/infinidysk/infinidysk/commit/dfa81468bc7504bea4c00f7c3f4c3d83849ff93b))


### Bug Fixes

* **api:** an unresponsive STRM folder no longer freezes settings and the SAB API ([#1795](https://github.com/infinidysk/infinidysk/issues/1795)) ([a7eb784](https://github.com/infinidysk/infinidysk/commit/a7eb784e4e39b7db6850817a00bac2ef3b2b1332))
* **api:** Arrs that start after InfiniDysk now connect without a restart ([#1806](https://github.com/infinidysk/infinidysk/issues/1806)) ([ba4972f](https://github.com/infinidysk/infinidysk/commit/ba4972f6a813f765cb49847098e489644735b9d9))
* **archive:** malformed 7z headers can no longer hang or exhaust memory during import ([#1794](https://github.com/infinidysk/infinidysk/issues/1794)) ([7091745](https://github.com/infinidysk/infinidysk/commit/7091745063ffba6feb7826191f9fea16a219be54))
* **arr:** files imported by a second Arr are no longer deleted by cleanup of the first ([#1802](https://github.com/infinidysk/infinidysk/issues/1802)) ([3464a0c](https://github.com/infinidysk/infinidysk/commit/3464a0c14d3f0187e113c962b132e267c42ec5de))
* **import:** migrate large legacy metadata without size-based skips ([#1797](https://github.com/infinidysk/infinidysk/issues/1797)) ([29f8e2f](https://github.com/infinidysk/infinidysk/commit/29f8e2f4b9eb83f4cfc41fc739b1e853321a9534))
* **import:** migration no longer follows symlinks out of the old config folder ([#1796](https://github.com/infinidysk/infinidysk/issues/1796)) ([a877923](https://github.com/infinidysk/infinidysk/commit/a877923a8bb0cbaacf7f71662bd1f8bd4747c6bf))
* **import:** reject Arr paths that climb out of a library folder ([#1800](https://github.com/infinidysk/infinidysk/issues/1800)) ([fe058ed](https://github.com/infinidysk/infinidysk/commit/fe058edfa5dcd20dc2b98ce2bce766d2ba54d053))
* **import:** upgrading from 1.x shows progress while it checks your library instead of looking stuck ([#1799](https://github.com/infinidysk/infinidysk/issues/1799)) ([53bd352](https://github.com/infinidysk/infinidysk/commit/53bd3524485eed2339fe61669eb0985066f356d0))
* **nntp:** fail over on articles that end before their announced size ([#1805](https://github.com/infinidysk/infinidysk/issues/1805)) ([27ce044](https://github.com/infinidysk/infinidysk/commit/27ce0448d4b6a27f4ffe990dadf2b195c29d72e5))
* **par2:** index large PAR2 sets without quadratic lookups ([#1801](https://github.com/infinidysk/infinidysk/issues/1801)) ([8c1fdff](https://github.com/infinidysk/infinidysk/commit/8c1fdff880d8693bb1b4081580587aa209794f67))
* **queue:** bound outstanding import tasks ([#1803](https://github.com/infinidysk/infinidysk/issues/1803)) ([1155315](https://github.com/infinidysk/infinidysk/commit/11553154af8a75c533527c85a6679078115a780c))
* **queue:** limit concurrent archive key derivations ([#1804](https://github.com/infinidysk/infinidysk/issues/1804)) ([6073369](https://github.com/infinidysk/infinidysk/commit/607336974219104987752825e99fd17ba9c6ae4a))
* **queue:** STRM and library files are no longer written through symlinked subfolders ([#1798](https://github.com/infinidysk/infinidysk/issues/1798)) ([b030f90](https://github.com/infinidysk/infinidysk/commit/b030f9001a1bccd85ad223533ee7c6cd10753117))
* **queue:** STRM cleanup and writes no longer follow planted paths out of the STRM folder ([#1793](https://github.com/infinidysk/infinidysk/issues/1793)) ([ce0868e](https://github.com/infinidysk/infinidysk/commit/ce0868e4175c58dba3a9611d462847938482324d))

## [2.0.0-beta.1](https://github.com/infinidysk/infinidysk/compare/v2.0.0-alpha.17...v2.0.0-beta.1) (2026-11-03)

### Beta

* InfiniDysk 2.0 is now in beta. It is a rewrite in Rust: one native binary with the server, the web UI and a built-in FUSE mount. It speaks the same SABnzbd API and serves the same WebDAV paths as 1.x, so Arr apps and media servers see no difference ([What's new in 2.x](https://www.infinidysk.com/v2/whats-new/))
* The image is ghcr.io/infinidysk/infinidysk-next:v2.0.0-beta.1 for Linux x64 and arm64. The edge tag is always the newest 2.x release; there is no latest tag until 2.0.0 ([Run with Docker](https://www.infinidysk.com/v2/install/docker/))

### Upgrading from 1.x

* Moving from 1.x to 2.x is a breaking change with manual steps: copy your config, run the import, and replace the rclone sidecar with the built-in mount. Changing the image name alone is not an upgrade ([Before you upgrade](https://www.infinidysk.com/v2/upgrade/))
* Some 1.x features are not in 2.x yet ([Feature comparison](https://www.infinidysk.com/v2/whats-new/#feature-comparison))
* 1.x continues on the v1 branch with bug fixes only and keeps the ghcr.io/infinidysk/infinidysk image and all its tags ([Looking for 1.x?](https://github.com/infinidysk/infinidysk#looking-for-1x))

### Other changes

* **ui:** update @tanstack/react-query, fast-uri, qs and systeminformation

### Reporting problems

* Open a GitHub issue (the bug form asks which version you run) or ask on Discord ([Reporting a bug](https://www.infinidysk.com/v2/help/reporting/))

## 2.0.0-alpha.17 (2026-11-02)


### Bug Fixes

* **stream:** keep disk cache writes the startup scan used to delete
* **stream:** keep reading the disk cache after its directory link changes
* **stream:** stop a reopened disk cache deleting writes still in progress

## 2.0.0-alpha.16 (2026-11-02)


### Features

* **docker:** containers report healthy only once the mount and database are ready


### Bug Fixes

* **ui:** ignore extension scripts when checking for updates
* **ui:** keep activity visible beside large spikes
* **ui:** keep the read position bar at a fixed width
* **ui:** show playback in green and health checks in blue on the activity graph

## 2.0.0-alpha.15 (2026-11-01)


### Features

* **api:** check provider logins and report each provider's connection state
* **api:** search and filter health and import history
* **health:** repair through Lidarr and Whisparr
* **ui:** explain each health state on the Health page
* **ui:** search and filter import and health history
* **ui:** show which providers serve each read in progress
* **ui:** test provider connections and show their state


### Bug Fixes

* **deps:** bump the cargo-minor-patch group with 3 updates
* **health:** check ebooks instead of ignoring them
* **import:** check imported files with recorded misses right away
* **ui:** align overview read rows and drop the activity chart's y-axis
* **ui:** bigger tap targets for slider thumbs, Advanced settings and inline links
* **ui:** call the stats library card Library and make provider settings a button
* **ui:** clearer provider name and Arr download-client hints
* **ui:** fill the system card grid and give the mount path room on phones
* **ui:** full-width editor sheets on phones and an explicit provider pick
* **ui:** name 1.x STRM links plainly and hide the notice when unused
* **ui:** one split button to start read traces
* **ui:** readable maintenance errors, relative run times and why Run now is off
* **ui:** show a dash for empty explorer cells and explain a missing import time
* **ui:** show provider misses as warnings on stats
* **ui:** show which performance settings the environment sets
* **ui:** stop the update banner from reloading every page
* **ui:** tidy phone layouts on overview, providers and explorer
* **ui:** type delete to run unlinked-file removal by hand

## 2.0.0-alpha.14 (2026-11-01)


### Features

* **ui:** show disk cache use and health on the System page
* **ui:** show release notes from the System page


### Bug Fixes

* **api:** let empty folders of categories no longer offered be removed
* **ui:** color the wordmark on the sign-in and setup screens
* **ui:** keep unsaved settings when saved settings change underneath
* **ui:** reload open tabs onto a new version after an upgrade
* **ui:** show the release folder for obfuscated file names
* **ui:** tuck reader process ids behind an info tip

## 2.0.0-alpha.13 (2026-11-01)


### Features

* **import:** serve upgrade progress while importing C# data


### Bug Fixes

* **api:** check the public image registry for newer releases
* **import:** upgraded installs bring back files with an extra listed article
* **ui:** preserve the app when upgrade status refresh fails


### Performance Improvements

* **import:** reuse matching measured volume geometry

## 2.0.0-alpha.12 (2026-11-01)


### Bug Fixes

* **import:** upgrades carry over archive files with wrong sizes in their posts again
* **import:** upgrades no longer hide a missing article in archive volumes that match an earlier volume's size
* **stream:** hedge pending responses when the preferred provider has spare capacity

## 2.0.0-alpha.11 (2026-10-31)


### Bug Fixes

* **import:** restore more skipped upgrade files and correct log ownership

## 2.0.0-alpha.10 (2026-10-31)


### Features

* **health:** count a 100% import as the first health check
* **import:** note that WebDAV deletes stop working after upgrading from a writable C# setup
* **ui:** explain what the health sample size covers


### Bug Fixes

* **auth:** accept the admin username in any case, as 1.x did
* **queue:** posts with wrong yEnc file sizes now import, and upgrades bring back files they skipped

## 2.0.0-alpha.9 (2026-10-30)


### Features

* **api:** say which setting a validation error is about
* **ui:** add a Stats page and show what needs attention on Overview
* **ui:** color dysk in the sidebar wordmark
* **ui:** edit providers in a side sheet
* **ui:** lay settings out as grouped panels
* **ui:** manage Arr instances in a list and side sheet
* **ui:** merge Admin API and Authentication into Access
* **ui:** move Support to the System page
* **ui:** move the download speed limit to Performance
* **ui:** show connection details on WebDAV, Download client and Mount
* **ui:** split Library settings from Imports
* **ui:** switch experiments on and off in Settings
* **ui:** tidy provider row and header actions


### Bug Fixes

* **archive:** key RAR archives with the first 127 password characters
* **deps:** bump crc32fast
* **stream:** keep win-floor out of analysis reads
* **stream:** stop speculative fetches past bounded read ranges
* **ui:** fit explorer, queue and logs on phones; tidy stats and health empty states
* **ui:** fit tables on phones and follow the design system on queue, explorer, health, system and stats
* **ui:** fix settings copy and placeholders
* **ui:** format sizes the same way on Performance
* **ui:** give icon-only buttons tooltips and specific names
* **ui:** harden settings forms
* **ui:** keep text on the type scale
* **ui:** keep the live status in the sidebar footer
* **ui:** link the health-checks-off notice to health settings
* **ui:** make preset cards one radio group
* **ui:** make sign-in errors, focus and button contrast accessible
* **ui:** mobile menu scrolls and closes on every navigation
* **ui:** page titles, skip link and screen-reader labels in the shell
* **ui:** put the WebDAV password beside its label
* **ui:** readable warning and success text in light theme
* **ui:** rename Report 1970 file dates to Disable file timestamps
* **ui:** shorten settings hints
* **ui:** show maintenance run reasons
* **ui:** show recent reads ten at a time
* **ui:** show server validation errors on their field
* **ui:** show the provider speed test as a row button
* **ui:** use the Advanced settings toggle in Performance


### Performance Improvements

* **nntp:** add pipe-weight experiment that charges large articles more download slots
* **nntp:** charge large playback articles one download slot per MB
* **ui:** load app pages on demand so sign-in renders first

## 2.0.0-alpha.8 (2026-10-27)


### Features

* **mount:** optional 1970 file dates to match rclone --no-modtime mounts


### Bug Fixes

* **api:** show why an Arr rejected a request
* **mount:** answer GETATTR on an open file from its handle
* **mount:** keep the FUSE session at error when RUST_LOG is set
* **stream:** stream progress only for the two segments read next


### Performance Improvements

* **db:** skip the idle-connection ping before SQLite queries
* **mount:** read kernel requests without a blocking-pool hop when running as root
* **mount:** stop revalidating fixed directories every second
* **nntp:** share decoded bodies with the memory cache instead of copying them
* **queue:** parse PAR2 index articles once as they arrive
* reopen files without re-reading their metadata
* **stream:** skip a body copy on each disk cache write and hit

## 2.0.0-alpha.7 (2026-10-26)


### Features

* **ui:** show warm provider connections in the sidebar


### Bug Fixes

* **mount:** allow group members to unlink completed symlinks
* **mount:** stop FUSE reads stalling forever behind a busy-reader probe
* **nntp:** log what a provider replied when it sends an unexpected response
* **nntp:** retain connection headroom during background requests
* **queue:** group old-style RAR volumes past .r99 with their set
* **queue:** import files whose NZB lists a part twice
* **queue:** stop the article check at the first missing segment of a media file
* **queue:** time out STRM directory checks and writes during handoff
* **ui:** copy keys and stream links on plain-HTTP LAN addresses

## 2.0.0-alpha.6 (2026-10-25)


### Features

* **api:** warn when a read stalls and add stall details to live status
* **mount:** report the effective kernel FUSE limits and queue
* **nntp:** show what each busy provider connection is waiting for
* **stream:** show which stage each waiting read is stuck in
* **ui:** restart from settings to apply startup-only changes


### Bug Fixes

* **deps:** bump the cargo-minor-patch group with 2 updates
* **deps:** bump the npm-minor-patch group in /ui with 6 updates
* **deps:** update ctutils to 0.4.4 for RUSTSEC-2026-0385
* **import:** refuse C# installs older than v1.3.0 before reading their schema
* **stream:** never let a blocked read wait on the read-ahead budget
* **stream:** warn about misaligned segments once a minute per file
* **ui:** make settings usable on touch and guard unsaved changes


### Build System

* **deps:** bump rust-toolchain from 1.99.0 to 1.99.1

## 2.0.0-alpha.5 (2026-10-23)


### Features

* **health:** remove dead library links and have the Arr search again
* **health:** repair migrated files that have no download through the Arr that links them
* **ui:** mark service restarts in the logs
* **ui:** show queue and action-needed counts in the sidebar nav


### Bug Fixes

* **api:** record owners of earlier imports as soon as the Arr is reachable
* **health:** a renamed file no longer counts as replaced
* **health:** never delete a migrated file before its Arrs were asked
* **import:** bring back skipped migrated files the library still links to
* **import:** carry over migrated files whose C# segment list lacks an article
* **import:** find the Arr download of migrated files so broken ones get replaced
* **import:** keep broken migrated files for review instead of deleting them
* **import:** keep broken unowned files for review on installs already migrated from C#
* **import:** list every file the C# import left out
* **import:** recognise library links into the C# mount folder when the mount moved
* **mount:** keep out-of-order kernel reads from resetting the read-ahead
* **nntp:** an article the NZB never listed counts as missing
* **ui:** reword the setting for broken files not imported by an Arr
* **ui:** say nothing to reclaim when compacting a database is skipped
* **ui:** show the maintenance window in the server's time zone

## 2.0.0-alpha.4 (2026-10-23)


### Features

* **api:** remove unlinked files as a scheduled maintenance job
* **db:** compact the databases in the maintenance window
* **import:** carry over the C# unlinked-file cleanup schedule
* **import:** carry the C# download limit over
* **nntp:** cap total download speed from providers
* **ui:** download limit under Settings &gt; Network
* **ui:** explain stalls in the recent reads table
* **ui:** list Maintenance under System in settings
* **ui:** maintenance page with remove unlinked files
* **ui:** move live activity to the middle of the sidebar and shorten the version label


### Bug Fixes

* **api:** give maintenance schemas unique names and query paging params
* **db:** pause background writes while compacting the database
* **health:** catch articles that decode but belong to another part of the file
* **ui:** clearer maintenance job description and schedule toggle
* **ui:** connections in use never exceeds the connection limit


### Performance Improvements

* **db:** keep query planner statistics fresh
* **mount:** cap max playback read-ahead memory at 2000 MB
* **mount:** size preset read-ahead memory for 5, 10 and 20 streams


### Refactors

* **health:** call broken files no Arr imported "unowned"

## 2.0.0-alpha.3 (2026-10-22)


### Features

* **queue:** show import progress and time left


### Bug Fixes

* **health:** judge a segment by the message-id playback reads
* **mount:** serve modified times in whole seconds
* **queue:** blame the NZB, not the providers, when it lacks a file's first segment
* **queue:** fail NZBs that lack segments without downloading the whole file
* **queue:** import obfuscated RAR sets listed out of order
* **queue:** leave out an incomplete non-media RAR member instead of failing the job
* **queue:** wait out the retry backoff when a job's result cannot be written
* **sab:** accept the speedlimit command and ignore it
* **stream:** report a corrupt segment even after the reader streamed past it
* **ui:** keep the explorer video player inside its window


### Performance Improvements

* **queue:** parse, hash and compress NZBs off the async runtime

## 2.0.0-alpha.2 (2026-10-22)


### Bug Fixes

* **import:** read C# times in the container's time zone
* **stream:** hedge a slow last segment


### Performance Improvements

* **nntp:** send hedges only to idle connections
* **stream:** read 60 MB ahead by default

## 2.0.0-alpha.1 (2026-10-21)


### ⚠ BREAKING CHANGES

* **db:** databases created by earlier pre-releases no longer open. Back up the config directory (database, -wal and metrics files), then start with a new database.

### Features

* **api:** add DB-backed settings with an authoritative env overlay, catalog providers and arr instances
* **api:** add per-file read history, most-read files and workload split
* **api:** add provider speed test and pipeline auto-tune
* **api:** add public URL setting under network
* **api:** add read, import, health and cache trends and usage by app to the overview
* **api:** admin API with OpenAPI document and live settings
* **api:** discover which Arr download clients point at this installation
* **api:** download queued and finished NZBs and retry failed jobs from the queue page
* **api:** explorer API with signed player links
* **api:** export redacted traces for bug reports
* **api:** keep logs across restarts
* **api:** list rejected releases and report stuck counts on queued jobs
* **api:** live updates over server-sent events
* **api:** provider download quotas with monthly or block resets, and a timed speed test
* **api:** record usage metrics with live samples, overview history and Prometheus export
* **api:** report container CPU and memory in live metrics
* **api:** reveal stored API and download keys in settings
* **api:** richer overview metrics with per-provider series, records, catalogue and live minute totals
* **api:** run the server with the SAB API and WebDAV on one port, the queue worker and the FUSE mount
* **api:** toggle read tracing and download traces
* **api:** trust local network ranges as reverse proxies by default
* **api:** upload NZBs into a category through the admin API
* **archive:** add a sans-I/O RAR4/RAR5 header scanner that maps members to byte ranges
* **archive:** read 7z headers, including LZMA-encoded ones, and map stored members to byte ranges
* **arr:** clear stuck imports from Arr queues
* **arr:** reconcile owned files against each Arr's library daily
* **arr:** record owners of earlier imports from each Arr's history
* **arr:** register webhooks and delete items their Arr deleted
* **auth:** admin account with argon2id, signed session cookies and an admin API key
* **auth:** let sign-in be required always, off for local addresses, or off
* **core:** add item ids, file metadata and the versioned zstd blob format
* **db:** add SQLite and PostgreSQL storage for items, blobs and config
* **db:** add the metrics store with minute, hour and lifetime rollups
* **db:** broadcast queue, history and health changes
* **db:** health check schedule and results
* **db:** keep unreferenced NZBs for a retention window and prune old history
* **db:** list recent health check results
* **db:** paged, filtered file queries for the explorer
* **db:** query id shards, completed download dirs and remove queued jobs
* **db:** record which item each finished read was of
* **db:** squash migrations into one initial schema
* **db:** store queue and history
* **db:** store the single admin login in accounts
* **health:** analyze media with ffprobe and repair broken MP4 containers
* **health:** choose the check sample size with a slider
* **health:** find the Arr that imported a STRM handoff
* **health:** report playback damage, hide replaced files and retry with 503
* **health:** share a durable per-media search limit
* **health:** show the check schedule and link checked files to the explorer
* **health:** STAT health checks with Sonarr/Radarr repair
* **health:** two-strike checks, damage classification and Arr replacement
* **import:** accept C# admin and WebDAV passwords at the first sign-in
* **import:** carry a C# install's library, history and settings over on first boot
* **import:** carry C#'s STRM setup over and keep its STRM links playing
* **import:** convert the library when the import strategy, STRM key, public URL or mount changes
* **import:** keep Arrs waiting when the upgrade from C# fails
* **import:** map C# settings onto ours in the upgrade report
* **import:** report what importing a C# /config would carry over
* **mount:** log FUSE requests and mount lifecycle
* **mount:** mount the tree read-only over FUSE with remount backoff and kernel tuning
* **mount:** name FUSE readers by process, pid and uid/gid
* **nntp:** add community-maintained provider catalog compiled from providers/*.toml
* **nntp:** add Easyusenet and more provider servers to the catalog
* **nntp:** add implicit TLS, pipelined BODY and zero-copy streaming decode
* **nntp:** add missing provider servers to the catalog
* **nntp:** add pipelined per-provider connection pool with global download permits
* **nntp:** add pipelined STAT and a keepalive warm connection floor
* **nntp:** count breaker trips and provider rescues, trace reply latency and decode
* **nntp:** count provider requests and trace segment fetches
* **nntp:** fail over across providers with per-provider circuit breakers and connection-limit throttling
* **nntp:** fetch and decode yEnc article bodies over one connection
* **nntp:** let diagnostics take a provider exclusively and yield to playback
* **nntp:** log provider outages and connection limits once as warnings
* **nntp:** meter provider downloads and skip providers whose quota is used up
* **nntp:** open connections ahead of demand with prewarm
* **nntp:** route by provider priority levels and skip missed backbones
* **nntp:** skip providers that recently lacked an article
* **par2:** read PAR2 file descriptions and slice checksums from partial files and verify slices
* **queue:** count imports and health check results since start
* **queue:** expand stored archives nested inside archives
* **queue:** fail full disc releases in video categories
* **queue:** fail releases with missing or invalid media and enforce the owning Arr's media type
* **queue:** hand jobs off as STRM files in STRM mode
* **queue:** import and stream AES-encrypted RAR and 7z members
* **queue:** import queued NZBs into /content with PAR2 names, archive members and STAT sampling
* **queue:** import two jobs at once and drop results of jobs removed mid-import
* **queue:** name obfuscated files after their release folder
* **queue:** parse NZBs with SABnzbd's acceptance rules, including gzip and namespaced files
* **queue:** pause the queue or single jobs, configure concurrency and back off retries
* **queue:** requeue imports that stall and fail them on the third stall
* **queue:** return the queued job for a duplicate NZB and record refused NZBs as failed
* **queue:** store Windows-safe file and folder names
* **sab:** add NZBs by URL with mode=addurl
* **sab:** identify as SABnzbd when fetching NZBs
* **sab:** retry failed jobs from their stored NZB
* **sab:** serve the SABnzbd API for adding NZBs, queue and history
* **sab:** tell owning Arrs about finished jobs
* **stream:** add a shared segment cache and performance settings
* **stream:** add seekable segment reader with ordered prefetch
* **stream:** bound decoded bytes in flight with a shared in-order budget
* **stream:** fill missing MPEG-TS segments with null packets
* **stream:** stop playback past too much missing data
* **stream:** trace read-ahead window, connection queueing, hedge wins and drained replies
* **ui:** add a performance settings page
* **ui:** add Network settings with trusted proxies
* **ui:** add NZBs from the queue page with a chosen category
* **ui:** add provider and settings screens
* **ui:** add provider speed test and auto-tune pipeline actions
* **ui:** add sign-in requirement to Settings &gt; Authentication
* **ui:** add sign-in, queue, history and health screens
* **ui:** add Sportarr instances, move providers under settings as NNTP Providers and settings below system
* **ui:** add the overview dashboard, Prometheus toggle and trace export
* **ui:** chip input for download client categories
* **ui:** choose between symlinks and STRM files on the Imports page
* **ui:** collapse subtitle tracks into one media line
* **ui:** confirm the admin password during first-time setup
* **ui:** download a support pack from Settings &gt; Support
* **ui:** edit extra blocked filename patterns in import settings
* **ui:** filter the Explorer by encryption and show it in file details
* **ui:** group repeated reads of a file into one playback row
* **ui:** group settings into platform, automation and system sections and rename account to authentication
* **ui:** health monitoring settings, state filters and needs-attention list
* **ui:** hide system folders in the explorer tree behind a toggle
* **ui:** in-browser player with recovery and playback diagnostics
* **ui:** library explorer with tree, filters, details and removal preview
* **ui:** link completed queue jobs to their files in the explorer
* **ui:** list overview providers in failover order
* **ui:** mute release details after the year in item names
* **ui:** pause and resume the queue and jobs, set import concurrency
* **ui:** preview and follow library conversions from the Imports page
* **ui:** provider quota settings, usage bars, usage reset and timed test progress
* **ui:** redesign the overview dashboard with activity, provider, rescue, record and catalogue panels
* **ui:** replace the logo with a simpler vector mark
* **ui:** restyle with shadcn and the logo palette
* **ui:** rework overview layout, chart controls and stat hierarchy
* **ui:** say on the health page when routine checks are off
* **ui:** serve the embedded web UI
* **ui:** show container CPU and memory on the System page
* **ui:** show each Arr's SABnzbd clients and whether they point here
* **ui:** show history below the queue and drop the history page
* **ui:** show import errors in a details dialog and quiet queue rows
* **ui:** show media analysis and broken containers in the explorer
* **ui:** show mount status and recent logs
* **ui:** show shared backbones and failover order on the providers page
* **ui:** show streaming quality, provider tail latency, workload cost and read history
* **ui:** show the segment cache budget on the memory bar
* **ui:** show trends, usage by app, rejected releases, stall counts and import durations
* **ui:** show version and live activity in the sidebar
* **ui:** show when a newer build is available on main
* **ui:** split segment cache from app memory on the System page
* **ui:** split settings into a page per category
* **ui:** test an Arr connection from Settings &gt; Arr instances
* **ui:** trace reads and show where playback stalled
* **ui:** view the C# import report again from Settings &gt; Support
* **vfs:** list open reads and trace them on demand
* **vfs:** serve /content, /.ids, /completed-symlinks and /nzbs with multipart readers
* **webdav:** serve the tree over WebDAV and /view with ranges, Basic auth and NZB drop-in
* **webdav:** show the client address in read history
* **yenc:** add incremental decode that stops at yEnc control lines and the NNTP terminator
* **yenc:** add rapidyenc-backed yEnc decode, encode and CRC32 crate


### Bug Fixes

* **api:** check for newer releases instead of newer commits on main
* **api:** count only failures the caller sees as errors
* **api:** decimal performance presets that keep at least the previous byte budgets
* **api:** group read usage by the app's first word
* **api:** load library folders saved before they gained an Arr path
* **api:** no trailing dot on links converted from STRM files for extensionless items
* **api:** read the player filter of /reads from the query string
* **api:** say the server keeps no copy of a support pack
* **api:** start with defaults instead of failing on an out-of-range stored setting
* **api:** write the support pack generated_at as an RFC 3339 time
* **arr:** reconcile only after the first discovery found each Arr's clients
* **auth:** say the username or password is wrong when sign-in fails
* **db:** delete a job folder emptied through its subfolders
* **db:** delete job folders left empty when their last file is deleted
* **db:** keep metrics stores that fail migration and quarantine corrupt ones
* **deps:** update rustls to 0.23.46 for RUSTSEC-2026-0346..0349
* **deps:** update rustls-webpki and webpki-roots for distrusted CA advisories
* **health:** keep items when an Arr reports a file missing from disk
* **health:** keep repair owner lookup working after SAB history deletion
* **health:** never replace a file while it is being watched
* **health:** one failing file no longer stops health checks for the library
* **health:** repair migrated files whose download history is gone
* **import:** accept a library folder that holds the mount or STRM directory
* **import:** bring C# health checks scheduled years out within 90 days
* **import:** carry over C# downloads with an empty category or unknown size
* **import:** carry over C# quota usage and storage-group backbones
* **mount:** date fixed directories from server start instead of 1970
* **mount:** give fixed folders a constant date instead of server start
* **mount:** keep FUSE reads in order and survive panics and kills
* **mount:** let Arr apps import links from the FUSE mount
* **mount:** measure first byte from the first read, not from open
* **mount:** warn when FUSE readahead can't be raised in Docker
* **mount:** warn when the mount is not visible outside the container
* **nntp:** a STAT success no longer resets the breaker back-off
* **nntp:** count a dropped provider connection as one failure
* **nntp:** fail over to the next provider when a stream read times out
* **nntp:** fail over when a provider serves the wrong part of a post
* **nntp:** probe a tripped provider once its cooldown ends
* **nntp:** report unavailable rather than corrupt when a provider was skipped
* **nntp:** requests that finish after a trip no longer close the breaker
* **nntp:** stop hung new connections from stalling playback
* **nntp:** stream reads give up after 30 s without a download slot
* **queue:** cap each import at 16 provider requests across all steps
* **queue:** fail over when a provider serves the wrong article during import
* **queue:** one crashing import no longer stops the queue
* **queue:** rename every file of packs that carry one PAR2 set per file
* **sab:** create category folders up front so Arr path checks pass
* **sab:** match parameter and NZB file field names case-insensitively
* **stream:** keep disk cache recency across restarts
* **stream:** stamp body progress at arrival and allow per-reader experiment switches
* **ui:** align overview tile trends and show short read times in seconds
* **ui:** allow picking NZB files on iOS
* **ui:** clarify the read-ahead hint
* **ui:** drop decimals from overview activity totals
* **ui:** fit provider settings on mobile and name slider thumbs
* **ui:** fit the player to the window and warn about undecodable audio
* **ui:** focus the username field on the sign-in page
* **ui:** format sub-millisecond first bytes and single stalls in read history
* **ui:** give settings section headers their own tier
* **ui:** indent the orphan action under auto-repair
* **ui:** mute release details in overview file names
* **ui:** open the player on plain-HTTP LAN addresses
* **ui:** pluralize held reconciliation counts
* **ui:** put Add Arr instance at the top like Add provider
* **ui:** queue page no longer crashes when the default category is empty
* **ui:** rename the providers settings link to Providers
* **ui:** scroll a new Arr instance fully into view
* **ui:** scroll dialogs that are taller than the window
* **ui:** scroll to the file details card when it opens in Explorer
* **ui:** show an error with a reload button instead of a blank page
* **ui:** show sizes, speeds and tuning in decimal MB and GB
* **ui:** show the hostname, port, TLS and backbone a provider profile sets
* **ui:** show the orphan action only while auto-repair is on
* **ui:** stop asking to convert a library that needs no changes
* **ui:** stop library conversion buttons from saving settings
* **ui:** wrap page header actions on narrow screens


### Performance Improvements

* **db:** index job folders, NZB references and latest health checks
* **db:** keep an hourly provider-bytes summary for metrics records
* **db:** look up latest health only for the explorer page
* **db:** open the metrics store in the metrics task
* **mount:** raise FUSE background limits inside containers
* **nntp:** keep two connections warm to each primary provider
* **nntp:** serve waiting playback requests before imports and health
* **nntp:** spread requests across idle connections before pipelining
* **nntp:** stream decoded body chunks and skip requests cancelled while queued
* **nntp:** wait for a connecting slot instead of queueing behind a busy connection
* **sab:** filter and page history in SQL
* **stream:** cache bodies that arrive after their reader left
* **stream:** hedge slow head segments and warn on size mismatches
* **stream:** keep the previous segment so out-of-order reads do not reset prefetch
* **stream:** keep the read-ahead open when playback keeps pace with downloads
* **stream:** open the read-ahead window fully after the first segment
* **stream:** prewarm connections when a read window starts
* **stream:** serve the awaited segment as it arrives and open bounded ranges fully
* **stream:** stream every prefetched segment in as it arrives


### Refactors

* **health:** move the Arr client into an arr crate shared by all six profiles


### Build System

* **docker:** build the web UI into the image
* **docker:** include ffprobe in the runtime image
* **docker:** move the image and release builds to Ubuntu 26.04
* **docker:** ship the server as an ubuntu:24.04 image with FUSE support
* target glibc on Ubuntu 24.04 instead of musl
