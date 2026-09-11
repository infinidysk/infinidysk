using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>
/// Turns a mount that an external rclone is currently serving into a built-in
/// mount definition.
///
/// The values come from what rclone reports it is actually running with
/// (<c>mount/listmounts</c> plus <c>vfs/stats</c>), not from a Compose file or a
/// config on disk, so drift between what a user wrote and what is running cannot
/// survive the import.
/// </summary>
public static class RcloneImportTranslator
{
    /// <summary>
    /// rclone reports durations in nanoseconds and sizes in bytes, using -1 for
    /// "no limit".
    /// </summary>
    private const long NoLimit = -1;

    private const long NanosecondsPerTick = 100;

    public static RcloneMountConfig Translate(RcloneMountPoint live, VfsOptions? options)
    {
        var mountPoint = live.MountPoint ?? throw new ArgumentException(
            "An imported mount must have a mount point.", nameof(live));

        var mount = new RcloneMountConfig
        {
            // Derived from the mount point so re-running an import updates the same
            // entry instead of adding a duplicate.
            Id = DeriveId(mountPoint),
            Name = mountPoint,
            // Preserved exactly. Existing Sonarr/Radarr symlinks resolve through
            // this path, so changing it during an import breaks every imported file.
            MountPoint = mountPoint,
            RemotePath = ExtractRemotePath(live.Fs),
        };

        if (options is null) return mount;

        mount.VfsCacheMode = ParseCacheMode(options.CacheMode);
        mount.Links = options.Links;

        if (ToTimeSpan(options.DirCacheTime) is { } dirCacheTime) mount.DirCacheTime = dirCacheTime;
        if (ToTimeSpan(options.CacheMaxAge) is { } cacheMaxAge) mount.VfsCacheMaxAge = cacheMaxAge;

        mount.VfsCacheMaxSizeBytes = ToOptionalCeiling(options.CacheMaxSize);
        mount.ReadAheadBytes = ToOptionalBytes(options.ReadAhead);

        return mount;
    }

    /// <summary>
    /// Recovers the path portion of an fs string such as <c>nzbdav:/content</c>.
    /// A root mount comes back from rclone as <c>nzbdav:</c> with no slash at all.
    /// </summary>
    internal static string ExtractRemotePath(string? fs)
    {
        if (string.IsNullOrWhiteSpace(fs)) return "/";

        var separator = fs.IndexOf(':', StringComparison.Ordinal);
        var path = separator >= 0 ? fs[(separator + 1)..] : fs;
        if (string.IsNullOrEmpty(path)) return "/";
        if (!path.StartsWith('/')) path = "/" + path;

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    internal static string DeriveId(string mountPoint) =>
        string.Join('-', mountPoint.Split('/', StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } id
            ? id
            : "root";

    private static RcloneVfsCacheMode ParseCacheMode(string? mode) =>
        Enum.TryParse<RcloneVfsCacheMode>(mode, ignoreCase: true, out var parsed)
            ? parsed
            : RcloneVfsCacheMode.Full;

    // Zero is a setting, not an absence: --dir-cache-time=0 asks rclone to cache
    // nothing, and importing that as "unset" silently replaces it with the
    // seven-day default -- the opposite of what the mount being imported does.
    // Only a negative value means rclone had nothing to report.
    private static TimeSpan? ToTimeSpan(long nanoseconds) =>
        nanoseconds >= 0 ? TimeSpan.FromTicks(nanoseconds / NanosecondsPerTick) : null;

    // Same distinction for sizes, where rclone spells "no limit" as -1: zero
    // read-ahead is read-ahead switched off, and must survive the import.
    private static long? ToOptionalBytes(long bytes) =>
        bytes >= 0 && bytes != NoLimit ? bytes : null;

    // The cache ceiling is the exception. Nothing downstream can act on a
    // zero-byte cache -- the budget and the status endpoint both read zero as
    // "no ceiling set" -- so storing it would record a limit that is never
    // applied. Recorded as unset instead, which is what it behaves as.
    private static long? ToOptionalCeiling(long bytes) =>
        bytes > 0 && bytes != NoLimit ? bytes : null;
}
