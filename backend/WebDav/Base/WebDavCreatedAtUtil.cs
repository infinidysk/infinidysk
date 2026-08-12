namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Normalizes WebDAV resource timestamps so PROPFIND/Last-Modified never emit
/// <see cref="DateTime.MinValue"/> (year 0001), which breaks macOS mount_webdav
/// and rclone/Plex mtimes.
/// </summary>
internal static class WebDavCreatedAtUtil
{
    /// <summary>Deterministic fallback for virtual folders and legacy rows with no real CreatedAt.</summary>
    public static DateTime Fallback { get; } = DateTime.UnixEpoch;

    public static DateTime Normalize(DateTime createdAt)
        => createdAt == default ? Fallback : createdAt;

    public static DateTime GetLastModifiedUtc(DateTime createdAt)
        => Normalize(createdAt).ToUniversalTime();
}
