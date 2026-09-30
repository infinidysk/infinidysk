# Native Cache [since unreleased](https://github.com/infinidysk/infinidysk/pull/1585){ .nzbdav-since }

Native Cache stores verified ranges of final media files on configured local or NAS volumes. It is an alternative to the existing Segment cache. A read of a verified range uses the cache; a gap reads from the Usenet source and can fill the missing blocks. A range is marked verified only after its bytes are committed. If metadata or a cache volume is unavailable, ordinary source streaming remains available.

Choose **Settings → Native Cache** to select Off, Segment, or Native. The configured mode becomes active after a restart. Add one or more container-visible storage folders and set a decimal quota below the volume's usable capacity. Keep the metadata path on local storage rather than NFS/SMB. Probe each folder before enabling writes. Native mode requires at least one enabled folder.

Each folder has a placement priority, free-space reserve, idle-age limit, and high/low eviction watermarks. Eviction begins at the high watermark and stops at the low watermark. Active and pinned entries are protected. A read-only folder serves existing verified bytes but does not accept fills or eviction. Clearing a folder requires confirmation and removes only owned cache files.

The **Native Cache** page shows capacity, file coverage, recent activity, active writes, and eviction history. Its file browser lists verified ranges, so you can see gaps that a single coverage percentage hides. Browser queries read local metadata and do not warm media. The 24-hour counters start collecting after the feature is enabled.

Minimum file size defaults to 100 MiB; smaller files stream without Native Cache writes. Set it to zero to allow smaller files. New entries use 64 MiB chunk files by default, configurable from 4 to 256 MiB in 4 MiB steps. Existing entries retain their original layout. Each chunk tracks independently verified 4 MiB blocks. Changes to mode, folders, metadata path, buffer size, minimum file size, or chunk size require restart. The size controls are advanced settings and are not part of the setup wizard.

The local catalogue under `/config` gains additive schema fields for browser history and telemetry. Back up `/config` before upgrading. Neither cache is a backup of the Usenet source.
