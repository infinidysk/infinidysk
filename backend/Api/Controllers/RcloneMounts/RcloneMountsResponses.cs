namespace NzbWebDAV.Api.Controllers.RcloneMounts;

/// <summary>Response of GET /api/rclone-mounts/status.</summary>
public class RcloneMountsStatusResponse : BaseApiResponse
{
    /// <summary>Whether built-in mode is switched on.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the built-in rclone daemon is running.</summary>
    public bool Running { get; init; }

    /// <summary>Version of the bundled rclone, when it could be read.</summary>
    public string? RcloneVersion { get; init; }

    /// <summary>
    /// Whether the WebDAV remote exists. Without it the daemon can run but can
    /// never mount, so the UI asks for the WebDAV password instead of showing a
    /// mount that will never come up.
    /// </summary>
    public bool RemoteConfigured { get; init; }

    /// <summary>
    /// Why the built-in mount cannot run. Empty when nothing blocks it. These are
    /// written for the operator: each one names the change that fixes it.
    /// </summary>
    public List<string> BlockingReasons { get; init; } = [];

    /// <summary>
    /// Where symlink imports expect the mount (<c>rclone.mount-dir</c>). Sent so
    /// the UI can prefill it and warn when no mount covers it: imported files
    /// resolve through this path, so a mount somewhere else leaves them broken.
    /// </summary>
    public string? SymlinkMountDir { get; init; }

    /// <summary>Directory the daemon writes its VFS cache into.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Free space on the cache directory's volume, when it could be read.</summary>
    public long? CacheDirFreeBytes { get; init; }

    /// <summary>
    /// The ceiling actually handed to rclone. Derived from free space unless a
    /// mount sets an explicit limit, because rclone's own default is unlimited.
    /// </summary>
    public long? CacheSizeLimitBytes { get; init; }

    public List<RcloneMountRow> Mounts { get; init; } = [];

    public class RcloneMountRow
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }
        public string MountPoint { get; init; } = "";
        public string RemotePath { get; init; } = "/";
        public string VfsCacheMode { get; init; } = "full";

        /// <summary>False when this is mounted but no longer described by config.</summary>
        public bool Configured { get; init; }

        public bool Enabled { get; init; }
        public bool Mounted { get; init; }

        public bool AllowOther { get; init; } = true;
        public bool Links { get; init; } = true;

        // Durations are seconds rather than TimeSpan so the UI does not have to
        // parse .NET's "00:00:20" form.
        public long DirCacheTimeSeconds { get; init; }
        public long VfsCacheMaxAgeSeconds { get; init; }

        /// <summary>Null means the limit is derived from free disk space.</summary>
        public long? VfsCacheMaxSizeBytes { get; init; }

        /// <summary>Null means rclone's own default.</summary>
        public long? ReadAheadBytes { get; init; }
    }
}

/// <summary>Response of POST /api/rclone-mounts/apply and .../remount.</summary>
public class RcloneMountsApplyResponse : BaseApiResponse
{
    public List<string> Mounted { get; init; } = [];
    public List<string> Unmounted { get; init; } = [];

    /// <summary>Mounts that could not be brought to the configured state.</summary>
    public List<string> Errors { get; init; } = [];
}

/// <summary>Response of POST /api/rclone-mounts/clear-cache.</summary>
public class RcloneCacheClearedResponse : BaseApiResponse
{
    /// <summary>Bytes removed from the cache directory.</summary>
    public long FreedBytes { get; init; }

    /// <summary>Mount points remounted after the cache was emptied.</summary>
    public List<string> Mounted { get; init; } = [];

    /// <summary>Anything that stopped the cache being emptied or the mounts restored.</summary>
    public List<string> Errors { get; init; } = [];
}

/// <summary>Response of GET /api/rclone-mounts/logs.</summary>
public class RcloneMountLogsResponse : BaseApiResponse
{
    public List<string> Lines { get; init; } = [];
}

/// <summary>Response of POST /api/rclone-mounts/import-external.</summary>
public class RcloneImportPreviewResponse : BaseApiResponse
{
    /// <summary>Whether anything usable was found to import.</summary>
    public bool Imported { get; init; }

    public List<RcloneMountsStatusResponse.RcloneMountRow> Mounts { get; init; } = [];

    /// <summary>
    /// Everything the operator should read before applying, including the cutover
    /// order and anything that could not be translated.
    /// </summary>
    public List<string> Warnings { get; init; } = [];
}
