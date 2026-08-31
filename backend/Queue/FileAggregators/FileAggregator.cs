using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileAggregators;

public class FileAggregator(DavDatabaseClient dbClient, DavItem mountDirectory, bool checkedFullHealth) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    /// <summary>
    /// A direct (non-archive) file an import will mount, with the exact relative path
    /// and leaf name <see cref="UpdateDatabase"/> persists. Pre-commit consumers such as
    /// import-readiness plan from this same projection so they cannot drift from what
    /// gets mounted.
    /// </summary>
    internal sealed record PlannedDirectFile(
        string RelativePath,
        string Name,
        long FileSize,
        DateTimeOffset ReleaseDate,
        NzbFile NzbFile,
        string? SniffedVideoExtension);

    internal static List<PlannedDirectFile> PlanDirectFiles(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        var planned = new List<PlannedDirectFile>();
        foreach (var processorResult in processorResults)
        {
            if (processorResult is not FileProcessor.Result result) continue;
            if (string.IsNullOrEmpty(result.FileName) && result.SniffedVideoExtension is null)
                continue;
            var relativePath = string.IsNullOrEmpty(result.FileName)
                ? mountName + result.SniffedVideoExtension
                : result.FileName;
            var leafName = string.IsNullOrEmpty(result.FileName)
                ? mountName
                : Path.GetFileName(result.FileName);
            var name = ImportableVideoNamer.Normalize(
                SanitizeDavName(leafName),
                result.SniffedVideoExtension,
                mountName,
                allowBaseRename: true);
            planned.Add(new PlannedDirectFile(
                relativePath,
                name,
                result.FileSize,
                result.ReleaseDate,
                result.NzbFile,
                result.SniffedVideoExtension));
        }

        return planned;
    }

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        foreach (var planned in PlanDirectFiles(processorResults, MountDirectory.Name))
        {
            var parentDirectory = EnsureParentDirectory(planned.RelativePath);
            var rangeIndex = planned.NzbFile.GetSegmentByteRangeIndex();
            var davNzbFile = new DavNzbFile()
            {
                Id = Guid.NewGuid(),
                SegmentIds = planned.NzbFile.GetSegmentIds(),
                SegmentByteRanges = rangeIndex.Ranges,
                SegmentByteRangesTrusted = rangeIndex.IsTrusted,
                SegmentFallbackIds = planned.NzbFile.GetSegmentFallbackIds(),
            };

            var davItem = DavItem.New(
                id: Guid.NewGuid(),
                parent: parentDirectory,
                name: planned.Name,
                fileSize: planned.FileSize,
                type: DavItem.ItemType.UsenetFile,
                subType: DavItem.ItemSubType.NzbFile,
                releaseDate: planned.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: MountDirectory.HistoryItemId,
                fileBlobId: davNzbFile.Id,
                nzbBlobId: MountDirectory.HistoryItemId,
                arrDownloadId: MountDirectory.ArrDownloadId
            );

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.AddBlob(davNzbFile);
        }
    }
}
