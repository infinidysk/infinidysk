using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

public class BlocklistedFilePostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    public void RemoveFilteredFiles()
    {
        var addedFiles = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .ToList();

        foreach (var (file, reason) in GetFilesToRemove(addedFiles))
        {
            Log.Information("Filtering out {FileName} ({Reason}).", file.Name, reason);
            RemoveFile(file, reason);
        }
    }

    /// <summary>
    /// Legacy entry point kept for callers that have not yet migrated.
    /// </summary>
    public void RemoveBlocklistedFiles() => RemoveFilteredFiles();

    private IEnumerable<(DavItem File, string Reason)> GetFilesToRemove(IReadOnlyCollection<DavItem> addedFiles)
    {
        var blocklistedFilenames = configManager.GetBlocklistedFiles();
        var sampleFilterEnabled = configManager.IsSampleFilterEnabled();

        // The sample heuristic compares each candidate against the largest video
        // in the same release, so the largest video can never be a sample itself.
        var largestVideoFileSize = sampleFilterEnabled
            ? addedFiles
                .Where(x => FilenameUtil.IsVideoFile(x.Name))
                .Select(x => x.FileSize ?? 0)
                .DefaultIfEmpty(0)
                .Max()
            : 0;

        foreach (var file in addedFiles)
        {
            if (FileFilterUtil.MatchesAnyGlob(file.Name, blocklistedFilenames))
                yield return (file, "blacklisted filename");

            else if (sampleFilterEnabled
                     && FileFilterUtil.IsSampleFile(file.Name, file.FileSize, largestVideoFileSize, file.Path))
                yield return (file, FileFilterUtil.LooksLikeSampleName(file.Name) ? "sample file" : "sample directory");
        }
    }

    /// <summary>
    /// Glob-only match used by health repair (no sibling-size context for samples).
    /// </summary>
    public static bool MatchesAnyPattern(string fileName, HashSet<string> patterns)
    {
        return FileFilterUtil.MatchesAnyGlob(fileName, patterns);
    }

    private void RemoveFile(DavItem davItem, string reason)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            dbClient.Ctx.RemoveNzbBlob(davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavNzbFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.NzbFiles.Remove(file);
        }

        else if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            dbClient.Ctx.RemoveRarBlob(davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavRarFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.RarFiles.Remove(file);
        }

        else if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            dbClient.Ctx.RemoveMultipartBlob(davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavMultipartFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.MultipartFiles.Remove(file);
        }

        else
        {
            Log.Error("Error filtering {FileName} ({Reason}) from downloading.", davItem.Name, reason);
            return;
        }

        DeletionAuditLog.Record(
            "blocklist-filter",
            davItem,
            $"{reason} during queue post-process");
        dbClient.Ctx.Items.Remove(davItem);
    }
}
