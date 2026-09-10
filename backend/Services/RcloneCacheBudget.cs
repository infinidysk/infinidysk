namespace NzbWebDAV.Services;

/// <summary>
/// Decides how much disk the built-in mount's VFS cache may use.
///
/// rclone's own <c>--vfs-cache-max-size</c> default is "off", which means a
/// <c>full</c> cache mode mount will grow until the filesystem is full. The
/// default cache directory sits beside InfiniDysk's databases, so "until full"
/// takes the database down with it. Rather than making every operator discover
/// that and pick a number, the ceiling is derived from the free space actually
/// available, and the database's filesystem keeps a reserve that the cache is
/// never allowed to eat into.
///
/// An explicit <c>VfsCacheMaxSizeBytes</c> on the mount still wins: someone who
/// has set a number has thought about it.
/// </summary>
public sealed class RcloneCacheBudget
{
    /// <summary>Ceiling regardless of how much space is free, matching the documented sidecar.</summary>
    internal const long DefaultCapBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Smallest cache worth handing to rclone; below this, streaming stutters.</summary>
    internal const long FloorBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Floor of last resort, for a volume too full to spare even
    /// <see cref="FloorBytes"/>. Playback will stutter, but rclone is given a
    /// real limit rather than the unlimited one it infers from zero.
    /// </summary>
    internal const long MinimumBytes = 16L * 1024 * 1024;

    /// <summary>Space kept free on the filesystem holding the databases.</summary>
    internal const long DatabaseHeadroomBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Seam for tests; production reads the real filesystem. Null means the free
    /// space could not be read, which is deliberately different from a volume
    /// that really has nothing left.
    /// </summary>
    public Func<string, long?> AvailableFreeSpace { get; init; } = DefaultAvailableFreeSpace;

    /// <summary>Seam for tests; production resolves the real mount point.</summary>
    public Func<string, string?> MountPointOf { get; init; } = DefaultMountPointOf;

    /// <summary>
    /// Samples the filesystem and returns the cache ceiling for a mount.
    /// </summary>
    /// <param name="cacheDir">Directory rclone writes its VFS cache into.</param>
    /// <param name="databaseDir">Directory holding InfiniDysk's databases.</param>
    /// <param name="configuredBytes">An explicit per-mount limit, when one is set.</param>
    public long ResolveFor(string cacheDir, string databaseDir, long? configuredBytes)
    {
        if (configuredBytes is > 0) return configuredBytes.Value;

        return Resolve(
            AvailableFreeSpace(cacheDir),
            SharesVolumeWith(cacheDir, databaseDir),
            configuredBytes);
    }

    /// <summary>
    /// The ceiling in bytes for an already-sampled volume.
    /// </summary>
    /// <param name="freeBytes">
    /// Free space on the cache volume, or null when it could not be read.
    /// </param>
    /// <param name="sharesDatabaseVolume">
    /// Whether filling the cache would also starve the databases.
    /// </param>
    /// <param name="configuredBytes">An explicit per-mount limit, when one is set.</param>
    /// <remarks>
    /// Deliberately free of side effects: the status endpoint calls this on every
    /// poll, so anything logged here would repeat every few seconds.
    /// </remarks>
    public static long Resolve(long? freeBytes, bool sharesDatabaseVolume, long? configuredBytes)
    {
        if (configuredBytes is > 0) return configuredBytes.Value;

        if (freeBytes is not { } free)
        {
            // Free space could not be read. The documented default is still a far
            // better answer than rclone's unlimited one.
            return DefaultCapBytes;
        }

        // Half of free space leaves room for everything else on the volume, and
        // for the cache overshooting its own limit between evictions.
        var budget = Math.Min(DefaultCapBytes, free / 2);

        if (sharesDatabaseVolume)
            budget = Math.Min(budget, free - DatabaseHeadroomBytes);

        // Below the floor the volume cannot spare a cache worth having. Clamping
        // up to the floor anyway would hand rclone a ceiling the disk cannot
        // honour -- which on a shared volume is how the databases get starved --
        // and zero is not an option, because rclone reads that as unlimited. Give
        // the smallest real limit instead; the caller warns whenever the result
        // lands this low.
        if (budget < FloorBytes) return Math.Max(MinimumBytes, Math.Min(budget, FloorBytes));

        return Math.Min(budget, DefaultCapBytes);
    }

    /// <summary>
    /// Whether both paths live on the same filesystem, so that filling one
    /// starves the other.
    /// </summary>
    internal bool SharesVolumeWith(string cacheDir, string databaseDir)
    {
        var cacheVolume = MountPointOf(cacheDir);
        var databaseVolume = MountPointOf(databaseDir);
        return cacheVolume is not null
               && databaseVolume is not null
               && string.Equals(cacheVolume, databaseVolume, StringComparison.Ordinal);
    }

    private static long? DefaultAvailableFreeSpace(string path)
    {
        try
        {
            return FindDrive(path)?.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? DefaultMountPointOf(string path)
    {
        try
        {
            return FindDrive(path) is { } drive ? NormalizeRoot(drive.RootDirectory.FullName) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The mounted filesystem a path resolves to: the drive whose root is the
    /// longest prefix of the path. <c>/</c> always matches, so a result is
    /// expected on any working container.
    /// </summary>
    private static DriveInfo? FindDrive(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        DriveInfo? best = null;
        var bestLength = -1;

        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = NormalizeRoot(drive.RootDirectory.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!IsWithin(full, root) || root.Length <= bestLength) continue;

            best = drive;
            bestLength = root.Length;
        }

        return best;
    }

    private static bool IsWithin(string path, string root) =>
        root == "/"
        || string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string NormalizeRoot(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(root);
        return normalized.Length == 0 ? "/" : normalized;
    }
}
