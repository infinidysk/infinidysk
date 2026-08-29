# Archives

InfiniDysk can stream from inside **RAR** and **7z** archives, including many password-protected releases, without extracting the full archive to disk first.

Queue processing aggregates multi-volume sets and mounts the inner video (or other) files on the WebDAV tree. Lazy RAR parsing reduces work until content is needed. Nested RAR extraction and more resilient handling of obfuscated multi-volume sets [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }.

When inner files use obfuscated or non-video extensions (for example `.xyz`), InfiniDysk sniffs the first bytes of the payload during queue import and renames mounted files to a recognized video extension (`.mkv`, `.mp4`, and others) so Sonarr and Radarr can import them. Single-file archives also adopt the release folder name when the inner filename looks obfuscated [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }.

When a completed job mounts **exactly one** video (direct, RAR, lazy-RAR, 7z, nested, or multipart), InfiniDysk renames that video to `{release-folder}{extension}` so hash-named files like `b082fa0beaa644d3aa01045d5b8d0b36.mkv` appear as `Release.Name.2026.mkv`. Companion files (subtitles, NFO) do not block the rename; two or more videos never rename. A name collision logs a warning and keeps the original filename. Disable `api.rename-single-video-to-release` to keep today's names. Existing mounts are not backfilled [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }.

If a release is archive-only and *Arr cannot import, check Automatic Queue Management rules and ignored-file globs under [SABnzbd settings](../configuration/sabnzbd.md).
