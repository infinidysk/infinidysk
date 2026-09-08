using Serilog;

namespace NzbWebDAV.Services;

/// <summary>What a purge removed, and why it did not run.</summary>
/// <param name="FreedBytes">Bytes deleted from the cache directory.</param>
/// <param name="Error">Why nothing was deleted, or null when the purge ran.</param>
public sealed record RcloneCachePurgeResult(long FreedBytes, string? Error);

/// <summary>
/// Empties rclone's VFS cache directory.
///
/// Lowering the cache ceiling does not reclaim what is already written, and
/// rclone only evicts on its own schedule, so an operator who needs the space
/// back had no option but deleting files inside the container by hand.
///
/// The directory itself is kept: rclone expects it to exist, and recreating it
/// would lose whatever ownership and permissions the container set up.
/// </summary>
public static class RcloneCachePurger
{
    /// <summary>
    /// Deletes everything inside <paramref name="cacheDir"/>.
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

        long freed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                try
                {
                    freed += new FileInfo(file).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Sizing is only for the report; a file that vanished between
                    // the walk and the stat is not a failure.
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(full))
                Directory.Delete(directory, recursive: true);

            foreach (var file in Directory.EnumerateFiles(full))
                File.Delete(file);
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
