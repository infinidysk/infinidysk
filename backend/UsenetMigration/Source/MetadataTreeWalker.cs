namespace NzbWebDAV.UsenetMigration.Source;

/// <summary>
/// Enumerates <c>.meta</c> files under the Altmount metadata root. Skips
/// AltMount's real metadata-root pollution (<c>.ids/</c> symlink shard tree and
/// <c>corrupted_metadata/</c> quarantine), directory and file symlinks, and the
/// defensive <c>failed/</c> name (which AltMount creates under <c>.nzbs/</c>, not
/// the metadata root).
/// </summary>
public static class MetadataTreeWalker
{
    public const string MetaExtension = ".meta";
    public const string IdsDirName = ".ids";
    public const string CorruptedMetadataDirName = "corrupted_metadata";

    /// <summary>
    /// Lazily yields absolute paths of every regular <c>.meta</c> file beneath
    /// <paramref name="metadataRoot"/>, in a stable directory-first order.
    /// Unreadable directories invoke <paramref name="onError"/> when supplied.
    /// </summary>
    public static IEnumerable<string> EnumerateMetaFiles(
        string metadataRoot,
        Action<string, string>? onError = null)
    {
        if (!Directory.Exists(metadataRoot))
            yield break;

        var stack = new Stack<string>();
        stack.Push(metadataRoot);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(dir, e.Message);
                continue;
            }

            Array.Sort(subdirs, StringComparer.Ordinal);
            foreach (var sub in subdirs)
            {
                if (IsExcludedDir(sub)) continue;
                // Do not recurse into directory symlinks (cycle / escape risk).
                if (Directory.ResolveLinkTarget(sub, returnFinalTarget: false) is not null)
                    continue;
                stack.Push(sub);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*" + MetaExtension);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(dir, e.Message);
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                // Skip .meta entries that are themselves symlinks (e.g. .ids shards).
                if (File.ResolveLinkTarget(file, returnFinalTarget: false) is not null)
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsExcludedDir(string dirPath)
    {
        var name = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // .ids and corrupted_metadata are AltMount's real metadata-root pollution.
        // failed is defensive only — AltMount creates it under .nzbs/, not the meta root.
        return string.Equals(name, IdsDirName, StringComparison.Ordinal)
               || string.Equals(name, CorruptedMetadataDirName, StringComparison.Ordinal)
               || string.Equals(name, StorePathParser.FailedDirName, StringComparison.Ordinal);
    }
}
