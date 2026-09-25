# Archives

InfiniDysk can stream from inside **RAR** and **7z** archives, including many password-protected releases, without extracting the full archive to disk first.

Queue processing aggregates multi-volume sets and mounts the inner video (or other) files on the WebDAV tree. Lazy RAR parsing reduces work until content is needed. Stored RAR-inside-RAR expansion and more resilient handling of obfuscated multi-volume sets [since 0.8.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.8.0){ .nzbdav-since }.

When inner files use obfuscated or non-video extensions (for example `.xyz`), InfiniDysk sniffs the first bytes of the payload during queue import and renames mounted files to a recognized video extension (`.mkv`, `.mp4`, and others) so Sonarr and Radarr can import them. Single-file archives also adopt the release folder name when the inner filename looks obfuscated [since 1.1.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.1.0){ .nzbdav-since }.

When a completed job mounts **exactly one** video (direct, RAR, lazy-RAR, 7z, nested, or multipart), InfiniDysk renames that video to `{release-folder}{extension}` so hash-named files like `b082fa0beaa644d3aa01045d5b8d0b36.mkv` appear as `Release.Name.2026.mkv`. Companion files (subtitles, NFO) do not block the rename; two or more videos never rename. A name collision logs a warning and keeps the original filename. Disable `api.rename-single-video-to-release` to keep today's names. Existing mounts are not backfilled [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }.

If a release is archive-only and *Arr cannot import, check Automatic Queue Management rules and ignored-file globs under [SABnzbd settings](../configuration/sabnzbd.md).

## Independent archive sets [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

Multiple RAR or 7z sets in one NZB are imported independently, including sets whose inner files have the same path. Duplicate visible names use the existing duplicate-output naming without sharing archive bytes. Each resolved RAR set makes its own lazy or eager processing decision; an eager fallback does not extract files to disk.

Archive membership uses filename and archive-header evidence. Ambiguous obfuscated RAR membership fails clearly instead of combining unrelated volumes. Disabling health checks does not skip archive metadata validation. This applies to new imports only; re-import affected NZBs after upgrading rather than expecting existing mounts to be rewritten. Multiple outputs retain their member names, while the existing configurable single-video rename still applies when exactly one video remains.

## RAR-inside-7z diagnostics [since 1.4.4](https://github.com/infinidysk/infinidysk/releases/tag/v1.4.4){ .nzbdav-since }

RAR volumes stored inside a 7z archive are exposed as archive files, not expanded
into their inner movie or audio files. RAR-inside-RAR support does not imply
support for this layout.

When **Fail downloads without video or audio** is enabled and no media remains
after configured filtering, an import containing these nested RAR members fails
with an explanation of the unsupported nesting. Choose a release with directly
accessible media or a supported top-level archive. The diagnostic does not add
nested-archive playback or download the inner archives to inspect them.

The existing controls remain authoritative: a mixed release with surviving media
can still succeed, and disabling the media requirement still permits raw archive
mounts. Sample filtering keeps its existing opt-out behavior. Disabling either
control does not make the nested movie playable.

This explanation applies to new or reprocessed imports. Existing history,
mounts, and STRM files are not rewritten.
