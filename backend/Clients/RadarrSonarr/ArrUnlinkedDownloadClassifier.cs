using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public enum ArrUnlinkedFileVerdict
{
    /// <summary>Arr history does not prove the file is unwanted.</summary>
    Unknown,
    /// <summary>Arr imported this file and later deleted that import as an upgrade.</summary>
    Superseded,
    /// <summary>Arr marked the download failed and never imported anything from it.</summary>
    DownloadFailed,
    /// <summary>Arr imported other files from the download, but not this one.</summary>
    NotImported,
}

/// <summary>
/// Proves from Arr history that an unlinked file is no longer wanted. Anything short of proof is
/// <see cref="ArrUnlinkedFileVerdict.Unknown"/>, so callers keep the file.
/// </summary>
public static class ArrUnlinkedDownloadClassifier
{
    private const int DownloadFolderImported = 3;
    private const int DownloadFailed = 4;

    /// <summary>Arr may still be importing the rest of a multi-file download shortly after the first import.</summary>
    internal static readonly TimeSpan ImportSettleTime = TimeSpan.FromDays(1);

    /// <summary>
    /// The latest import of the file at <paramref name="contentRelativePath"/> (category/job/...)
    /// from this download, matched against the dropped symlink or its generated <c>.strm</c> sidecar.
    /// </summary>
    public static ArrHistoryRecord? FindFileImport(
        IEnumerable<ArrHistoryRecord> downloadHistory,
        string contentRelativePath)
    {
        var suffix = "/" + Normalize(contentRelativePath).TrimStart('/');
        return downloadHistory
            .Where(record => record.EventType == DownloadFolderImported && GetDroppedPath(record) is { } dropped &&
                             (dropped.EndsWith(suffix, StringComparison.Ordinal) ||
                              dropped.EndsWith(suffix + ".strm", StringComparison.Ordinal)))
            .MaxBy(record => record.Date);
    }

    /// <summary>Classifies a download whose history has no import of this file.</summary>
    public static ArrUnlinkedFileVerdict ClassifyWithoutFileImport(
        ArrHistory downloadHistory,
        string contentRelativePath,
        DateTimeOffset utcNow)
    {
        var records = downloadHistory.Records;
        // Absence of an import is only proof when Arr returned the complete history.
        if (records.Count == 0 || downloadHistory.TotalRecords > records.Count)
            return ArrUnlinkedFileVerdict.Unknown;

        var imports = records.Where(record => record.EventType == DownloadFolderImported).ToList();
        if (imports.Count == 0)
        {
            // A retry of the same download gets a day to import before the failure counts.
            var lastFailure = records.Where(record => record.EventType == DownloadFailed)
                .Select(record => (DateTimeOffset?)record.Date)
                .Max();
            return lastFailure + ImportSettleTime <= utcNow
                ? ArrUnlinkedFileVerdict.DownloadFailed
                : ArrUnlinkedFileVerdict.Unknown;
        }

        // Without a dropped path on every import, this file may be among them; an import of the
        // same file name at an unexpected path may also be this file.
        var fileName = Path.GetFileName(Normalize(contentRelativePath));
        if (imports.Any(record => GetDroppedPath(record) is not { } dropped ||
                                  Path.GetFileName(dropped) == fileName ||
                                  Path.GetFileName(dropped) == fileName + ".strm"))
            return ArrUnlinkedFileVerdict.Unknown;

        return imports.Max(record => record.Date) + ImportSettleTime <= utcNow
            ? ArrUnlinkedFileVerdict.NotImported
            : ArrUnlinkedFileVerdict.Unknown;
    }

    /// <summary>
    /// True when Arr deleted the library file created by <paramref name="fileImport"/> as an upgrade.
    /// </summary>
    public static bool IsSupersededByUpgrade(ArrHistoryRecord fileImport, IEnumerable<ArrHistoryRecord> deletions)
    {
        var importedPath = fileImport.Data?.ImportedPath;
        if (string.IsNullOrEmpty(importedPath))
            return false;

        return deletions.Any(deletion =>
            deletion.Date > fileImport.Date &&
            string.Equals(deletion.Data?.Reason, "Upgrade", StringComparison.OrdinalIgnoreCase) &&
            IsSamePath(importedPath, deletion.SourceTitle));
    }

    private static string? GetDroppedPath(ArrHistoryRecord record) =>
        string.IsNullOrEmpty(record.Data?.DroppedPath) ? null : Normalize(record.Data.DroppedPath);

    private static string Normalize(string path) => path.Replace('\\', '/');

    // Arr records the deleted file's path relative to the series/movie folder, or in full.
    private static bool IsSamePath(string importedPath, string? deletedPath)
    {
        if (string.IsNullOrEmpty(deletedPath))
            return false;

        var imported = importedPath.Replace('\\', '/');
        var deleted = deletedPath.Replace('\\', '/');
        return string.Equals(imported, deleted, StringComparison.Ordinal) ||
               (!deleted.StartsWith('/') && imported.EndsWith("/" + deleted, StringComparison.Ordinal));
    }
}
