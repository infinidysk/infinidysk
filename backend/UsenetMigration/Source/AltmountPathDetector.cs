namespace NzbWebDAV.UsenetMigration.Source;

/// <summary>Three resolved inputs for the standard single-mount connection flow.</summary>
/// <param name="StoreRoot">
/// Intentionally mirrors <paramref name="Root"/> because the standard layout keeps
/// <c>metadata/</c>, <c>config.yaml</c>, and <c>.nzbs/</c> below one directory. It remains
/// explicit so the response maps directly to the three-path connection contract; Advanced
/// mode may provide a different store root.
/// </param>
public sealed record AltmountPathDetection(
    bool Detected,
    string Root,
    string MetadataRoot,
    string ConfigPath,
    string StoreRoot,
    string? Reason);

/// <summary>
/// Resolves the standard single-mount Altmount layout used by the migration guide.
/// Non-standard layouts remain available through the wizard's advanced fields.
/// </summary>
public static class AltmountPathDetector
{
    public const string DefaultRoot = "/altmount";
    internal const string FailureReason =
        "The selected directory does not match the standard Altmount layout.";
    internal const string InvalidConfigReason =
        "The Altmount config could not be read or parsed. Check the mount, file permissions, and YAML.";
    internal const string NoCategoriesReason =
        "The Altmount config does not contain any supported SABnzbd categories.";

    public static async Task<AltmountPathDetection> DetectAsync(
        string? root,
        CancellationToken ct = default)
    {
        var requestedRoot = root is null ? DefaultRoot : root.Trim();
        if (requestedRoot.Length == 0)
            throw new ArgumentException("root cannot be empty.", nameof(root));
        if (!Path.IsPathRooted(requestedRoot))
            throw new ArgumentException("root must be an absolute path.", nameof(root));
        if (HasNavigationSegment(requestedRoot))
            throw new ArgumentException("root cannot contain '.' or '..' path segments.", nameof(root));

        var storeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var fileSystemRoot = Path.GetPathRoot(storeRoot);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.IsNullOrEmpty(fileSystemRoot)
            && string.Equals(storeRoot, fileSystemRoot, pathComparison))
        {
            throw new ArgumentException("root cannot be the filesystem root.", nameof(root));
        }

        var metadataRoot = Path.Combine(storeRoot, "metadata");
        var configPath = Path.Combine(storeRoot, "config.yaml");

        // This detector is reached through the API-key-guarded migration controller. A fixed
        // prefix would break supported custom bind mounts, and Advanced Connect already accepts
        // arbitrary container paths. These are three bounded metadata probes; .NET has no async
        // Exists API, while Task.Run would merely move the same blocking work to another pool thread.
        // The config itself is then read asynchronously so Basic mode verifies more than existence.
        var rootExists = Directory.Exists(storeRoot);
        var metadataExists = Directory.Exists(metadataRoot);
        var configExists = File.Exists(configPath);
        var layoutDetected = rootExists && metadataExists && configExists;

        var detected = false;
        string? reason;
        if (!layoutDetected)
        {
            reason = FailureReason;
        }
        else
        {
            try
            {
                var config = await AltmountConfigReader.ReadAsync(configPath, ct).ConfigureAwait(false);
                detected = config.Categories.Count > 0;
                reason = detected ? null : NoCategoriesReason;
            }
            catch (AltmountConfigException)
            {
                reason = InvalidConfigReason;
            }
        }

        return new AltmountPathDetection(
            detected,
            storeRoot,
            metadataRoot,
            configPath,
            storeRoot,
            reason);
    }

    private static bool HasNavigationSegment(string path) =>
        path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
}
