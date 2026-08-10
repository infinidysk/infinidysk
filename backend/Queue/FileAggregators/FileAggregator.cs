using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileAggregators;

public class FileAggregator(DavDatabaseClient dbClient, DavItem mountDirectory, bool checkedFullHealth) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        foreach (var processorResult in processorResults)
        {
            if (processorResult is not FileProcessor.Result result) continue;
            if (string.IsNullOrEmpty(result.FileName) && result.SniffedVideoExtension is null)
                continue;
            var parentDirectory = EnsureParentDirectory(
                string.IsNullOrEmpty(result.FileName)
                    ? MountDirectory.Name + result.SniffedVideoExtension
                    : result.FileName);
            var leafName = string.IsNullOrEmpty(result.FileName)
                ? MountDirectory.Name
                : Path.GetFileName(result.FileName);
            var name = ImportableVideoNamer.Normalize(
                SanitizeDavName(leafName),
                result.SniffedVideoExtension,
                MountDirectory.Name,
                allowBaseRename: true);

            var davNzbFile = new DavNzbFile()
            {
                Id = Guid.NewGuid(),
                SegmentIds = result.NzbFile.GetSegmentIds(),
                SegmentByteRanges = result.NzbFile.GetSegmentByteRanges(),
                SegmentFallbackIds = result.NzbFile.GetSegmentFallbackIds(),
            };

            var davItem = DavItem.New(
                id: Guid.NewGuid(),
                parent: parentDirectory,
                name: name,
                fileSize: result.FileSize,
                type: DavItem.ItemType.UsenetFile,
                subType: DavItem.ItemSubType.NzbFile,
                releaseDate: result.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: MountDirectory.HistoryItemId,
                fileBlobId: davNzbFile.Id,
                nzbBlobId: MountDirectory.HistoryItemId
            );

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.AddBlob(davNzbFile);
        }
    }
}
