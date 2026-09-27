# Media Library [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since }

The **Media Library** page (`/library`) is a read-only catalog of your InfiniDysk media and the library symlinks that point at it.

Each file is one media item (deduplicated by its stable InfiniDysk identity) with its symlink mappings nested underneath. Links that resolve to InfiniDysk `/.ids/...` targets are marked **internal** and can be opened for streaming; all other symlinks are marked **external** and are inspection-only.

## File table [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since }

The default **File table** lists one media item per row. Use the visible **All media**, **TV shows**, **Movies**, and **Unmatched** tabs to switch types. A matched Plex show or movie title appears in its own column; TV season and episode have separate columns. Movies and unmatched files show a dash where episode details do not apply. Search covers filenames, paths, Plex titles, and season/episode text across the full catalog before pagination. Mapping, match, quality, cache, and season filters also apply before page counts are computed. Select a row's **Details** action for its mappings, preview/download, repair, and prewarm controls.

Choose **Grouped browse** to return to show/movie cards and expand a group into its files. Switching views keeps applicable filters. An unavailable Plex index does not hide files from **All media**; unmatched titles remain blank until a reliable Plex match is available.

## Grouped browsing [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since }

The **TV shows** and **Movies** tabs classify files by matching their symlink filename against metadata from enabled Plex servers. A Plex episode match goes under TV shows; a Plex movie match goes under Movies. Recognized library folders, including `TV-*` and `Movies-*`, organize matched files into groups. If the folder does not match the Plex media type, the Plex show or movie title supplies the group. Expand a group to see its files and their mappings without leaving the page; large groups have their own inline file pages. The **Unmatched** tab lists files with no unambiguous Plex filename match, including external-only links when Plex does not recognize their filenames.

Search matches file names, content paths, symlink paths, and targets across the full indexed catalog. The mapping filter includes internal, external, and broken links. Quality filters use resolution tags in filenames (4K, 1080p, 720p, or SD); files without a recognized tag are **Unknown**. Native Cache filters use verified bytes for the current file revision. When Native Cache is inactive or its metadata is unavailable, coverage is **Unavailable**. Summary cards count indexed files, internal files with valid mappings, files needing attention, and unmatched files for the current search and mapping filter. The scan timestamp and any stale-index warning appear above the groups.

The Plex index syncs shortly after startup and every six hours. **Sync Plex now** refreshes it on demand. The page shows the last successful sync, the number of indexed Plex items, and any sync warning. A failed or incomplete Plex request keeps the last complete index; before the first successful sync, the page leaves categories pending rather than treating every file as unmatched. The index is saved in `/config/plex-library-metadata.json`. Plex matching uses filenames, so files renamed differently from their Plex versions may remain unmatched. The page does not provide missing-episode detection or language analysis.

## File details modal [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since }

Selecting a file opens a details modal without leaving the page. External items show their mappings only; internal items also offer actions:

- **Preview** plays the file in the built-in player; **Download** saves it directly.
- **Run health check** queues all due health checks — not just this file.
- **Requeue repair** re-queues this file when its latest result needs action.
- **Prewarm** warms this file into the Native cache (hidden with an explanation when Native cache is inactive).

The modal also lists every symlink mapping for the item and its latest health-check result, with a link to **Health** (`/health`) for full history.

The catalog and mapping list are read-only: the modal never edits or deletes library files or symlinks. Health checks, repair requeues, and prewarming schedule backend work without changing the catalog's mappings. Use **Files** (`/explore`) to browse the raw virtual filesystem.

## Settings [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since }

Open **Settings → Media Library** to enable or disable the catalog, set its Library Directory, choose a link scan interval, select enabled Plex servers for matching, and see the last Plex metadata sync. The Library Directory is the parent of your Radarr and Sonarr library roots inside the InfiniDysk container. It must not point to the rclone mount. Changing it does not move files.

**Index video files only** [since unreleased](https://github.com/infinidysk/infinidysk/pull/1587){ .nzbdav-since } is optional and off by default. When enabled, link scans and catalog results include only filenames recognized as video by InfiniDysk; STRM links must identify a video target. This also excludes non-video internal files imported after the scan, so the library view follows the same rule as link discovery. Subtitles, images, and metadata files stay on disk and remain accessible outside the Media Library. Changing the option triggers a scan within about 30 seconds; turning it off makes skipped files visible again without recreating them. The setup wizard leaves this advanced option at its default. If `NZBDAV_CONFIG__MEDIA__LIBRARY_VIDEO_ONLY` owns the setting, the UI shows it as read-only.

The catalog is enabled for existing and new installs. Turning it off hides **Media Library** from the main menu and pauses link scans and Plex matching refresh. A direct visit to `/library` returns to its settings. Existing files, links, and saved catalog data remain in place; health checks and repairs continue independently. The settings tab stays available so you can turn the catalog back on.

Library link scans run **every 15 minutes** by default. You can choose 5, 15, 30, or 60 minutes, or 6 hours. A scan finishes before the next interval begins. This scans symlinks and STRM files under Library Directory. Plex metadata is a separate index: it refreshes about 15 seconds after startup, every 6 hours thereafter, and when you select **Sync Plex now**. The chosen Plex sources must be saved before a manual sync. If a Plex sync fails, the last complete metadata index remains available. By default, all enabled Plex servers contribute to matching; you can select a subset in this tab. Plex credentials and connections remain managed under **Settings → Streaming**.

The setup wizard already asks for Library Directory. It does not ask for the catalog switch, scan interval, or Plex source selection: their defaults work on a new install, and you can adjust them later. Settings owned by `NZBDAV_CONFIG__MEDIA__LIBRARY_ENABLED`, `NZBDAV_CONFIG__MEDIA__LIBRARY_SCAN_INTERVAL_MINUTES`, `NZBDAV_CONFIG__MEDIA__LIBRARY_PLEX_SERVER_IDS`, or `NZBDAV_CONFIG__MEDIA__LIBRARY_DIR` are shown read-only in the UI.

The upgrade adds an additive library link-map table. Back up `/config` before upgrading. Existing media files and symlinks are not rewritten.
