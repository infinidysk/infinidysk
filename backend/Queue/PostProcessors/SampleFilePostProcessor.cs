using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.PostProcessors;

public class SampleFilePostProcessor(DavDatabaseClient dbClient)
{
    public void RemoveSampleFilesOrThrow()
    {
        var addedEntries = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        var byId = addedEntries.ToDictionary(x => x.Id);

        var videoFiles = addedEntries
            .Where(x => x.Type != DavItem.ItemType.Directory && FilenameUtil.IsVideoFile(x.Name))
            .ToList();

        var samples = videoFiles.Where(x => IsSample(x, byId)).ToList();
        if (samples.Count == 0) return;

        var realVideos = videoFiles.Except(samples).ToList();
        if (realVideos.Count == 0)
        {
            throw new SampleOnlyReleaseException(
                "Only a sample video was found in this release; no full-length video was found.");
        }

        foreach (var sample in samples)
            DavItemRemover.Remove(dbClient, sample);
    }

    private static bool IsSample(DavItem item, Dictionary<Guid, DavItem> addedEntriesById)
    {
        if (FilenameUtil.IsSampleFile(item.Name))
            return true;

        return item.ParentId is { } parentId
               && addedEntriesById.TryGetValue(parentId, out var parent)
               && FilenameUtil.IsSampleFile(parent.Name);
    }
}
