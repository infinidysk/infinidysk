using System.Globalization;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

internal enum ArrDownloadIdResolutionKind
{
    NotFound,
    Unique,
    Ambiguous,
}

internal sealed record ArrDownloadIdResolution(
    ArrDownloadIdResolutionKind Kind,
    Guid? DownloadId = null);

internal static class ArrDownloadIdResolver
{
    public static ArrDownloadIdResolution Resolve(
        IEnumerable<ArrHistoryRecord> records,
        ArrMediaFileMatch mediaFile,
        string organizedPath)
    {
        var candidates = records
            .Select(record => (
                HasFileId: int.TryParse(
                    record.Data?.FileId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var fileId),
                FileId: fileId,
                HasDownloadId: Guid.TryParse(record.DownloadId, out var downloadId),
                DownloadId: downloadId,
                ImportedPath: record.Data?.ImportedPath))
            .Where(record => record.HasFileId
                && record.FileId == mediaFile.FileId
                && string.Equals(record.ImportedPath, organizedPath, StringComparison.Ordinal)
                && record.HasDownloadId)
            .Select(record => record.DownloadId)
            .Distinct()
            .ToArray();

        return candidates.Length switch
        {
            1 => new ArrDownloadIdResolution(ArrDownloadIdResolutionKind.Unique, candidates[0]),
            > 1 => new ArrDownloadIdResolution(ArrDownloadIdResolutionKind.Ambiguous),
            _ => new ArrDownloadIdResolution(ArrDownloadIdResolutionKind.NotFound),
        };
    }
}
