using Serilog;

namespace NzbWebDAV.Services;

/// <summary>What a purge removed, and why it did not run.</summary>
/// <param name="FreedBytes">Bytes deleted from the cache directory.</param>
/// <param name="Error">Why nothing was deleted, or null when the purge ran.</param>
public sealed record RcloneCachePurgeResult(long FreedBytes, string? Error);

/// <summary>
/// Empties rclone's VFS cache.
///
/// Lowering the cache ceiling does not reclaim what is already written, and
/// rclone only evicts on its own schedule, so an operator who needs the space
/// back had no option but deleting files inside the container by hand.
///
/// Only rclone's own subtrees go. The cache directory is an operator-set path
/// that can point anywhere -- the config directory, a media root -- so deleting
/// every child of it would delete databases and libraries along with the cache.
/// The directory itself is kept too: rclone expects it to exist, and recreating
/// it would lose whatever ownership and permissions the container set up.
/// </summary>
public static class RcloneCachePurger
{
    /// <summary>
    /// The directories rclone creates under its cache directory, and the only
    /// ones this will delete.
    /// </summary>
    /// <remarks>
    /// Observed against rclone v1.75.1: a <c>full</c> cache-mode mount with
    /// <c>--cache-dir</c> creates <c>vfs/&lt;remote&gt;</c> and
    /// <c>vfsMeta/&lt;remote&gt;</c> and nothing else.
    /// </remarks>
    private static readonly string[] OwnedSubdirectories = ["vfs", "vfsMeta"];

    /// <summary>
    /// Whether a path is a directory rclone could have created, rather than a
    /// link standing where one should be.
    /// </summary>
    /// <remarks>
    /// A link named <c>vfs</c> is not rclone's cache, so it is left alone: both
    /// the walk that sizes the purge and the delete would otherwise reach
    /// through it into whatever it points at.
    /// </remarks>
    private static bool IsRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes rclone's own cached data under <paramref name="cacheDir"/>,
    /// leaving anything else in that directory alone.
    /// </summary>
    /// <remarks>
    /// Callers must stop the mounts first: rclone holds open handles into this
    /// directory while a mount is up, and deleting underneath it leaves the VFS
    /// serving files whose backing chunks are gone.
    /// </remarks>
    public static RcloneCachePurgeResult Purge(string cacheDir)
    {
        if (string.IsNullOrWhiteSpace(cacheDir) || !Path.IsPathRooted(cacheDir))
            return new RcloneCachePurgeResult(0, $"'{cacheDir}' is not an absolute path.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDir));

        // A cache directory that resolves to a filesystem root would turn this
        // into a wipe of the container. Refuse rather than trust the config.
        if (full.Length == 0 || full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""))
            return new RcloneCachePurgeResult(0, $"'{cacheDir}' is a filesystem root, so it was not emptied.");

        if (!Directory.Exists(full)) return new RcloneCachePurgeResult(0, null);

        // Only rclone's own subtrees are removed, never every child of the
        // configured directory. A cache directory pointed somewhere shared -- the
        // config directory, a media root -- would otherwise make this delete
        // databases, backups and libraries. Nothing outside these two can be
        // rclone's VFS data, so nothing outside them is ours to delete.
        var owned = OwnedSubdirectories
            .Select(name => Path.Join(full, name))
            .Where(IsRealDirectory)
            .ToList();

        if (owned.Count == 0) return new RcloneCachePurgeResult(0, null);

        long freed = 0;
        try
        {
            // Reparse points are skipped rather than followed: a symlink inside
            // the cache would otherwise have whatever it points at counted here,
            // and a link loop would walk forever.
            var walk = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            foreach (var root in owned)
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", walk))
                {
                    try
                    {
                        freed += new FileInfo(file).Length;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Sizing is only for the report; a file that vanished
                        // between the walk and the stat is not a failure.
                    }
                }

                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not empty the rclone cache directory {CacheDir}.", full);
            return new RcloneCachePurgeResult(freed, $"Could not empty '{full}': {e.Message}");
        }

        Log.Information("Emptied the rclone cache directory {CacheDir}, freeing {Freed} bytes.", full, freed);
        return new RcloneCachePurgeResult(freed, null);
    }
}
