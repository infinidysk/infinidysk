using System.Text.Json;

namespace NzbWebDAV.Config;

public static class MediaLibraryOptions
{
    public static readonly int[] ScanIntervalsMinutes = [5, 15, 30, 60, 360];

    public static int ParseScanInterval(string? value) =>
        int.TryParse(value, out var minutes) && ScanIntervalsMinutes.Contains(minutes) ? minutes : 15;

    /// <summary>Additional catalog-only roots. The primary library directory still owns link creation.</summary>
    public static IReadOnlyList<string> ParseScanDirectories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        string[]? directories;
        try { directories = JsonSerializer.Deserialize<string[]>(value); }
        catch (JsonException) { throw new ArgumentException("Media Library scan directories must be a JSON array of absolute paths."); }
        if (directories is null || directories.Length > 16)
            throw new ArgumentException("Media Library supports at most 16 additional scan directories.");

        var normalized = new List<string>(directories.Length);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("Media Library scan directories must be absolute paths.");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (fullPath == Path.GetPathRoot(fullPath))
                throw new ArgumentException("A filesystem root cannot be a Media Library scan directory.");
            normalized.Add(fullPath);
        }
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            throw new ArgumentException("Media Library scan directories must be unique.");
        return normalized;
    }

    /// <summary>Null means all enabled Plex servers, including newly added servers.</summary>
    public static IReadOnlySet<string>? ParsePlexServerIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[]? ids;
        try { ids = JsonSerializer.Deserialize<string[]>(value); }
        catch (JsonException) { throw new ArgumentException("Media Library Plex sources must be a JSON array of server IDs."); }
        if (ids is null || ids.Length > 32 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256))
            throw new ArgumentException("Media Library Plex sources must contain at most 32 valid server IDs.");
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Media Library Plex source IDs must be unique.");
        return ids.ToHashSet(StringComparer.Ordinal);
    }
}
