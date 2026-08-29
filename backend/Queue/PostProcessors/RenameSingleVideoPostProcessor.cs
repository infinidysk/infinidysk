using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

/// <summary>
/// Renames a job's single surviving video to the release (mount folder) name.
/// Runs after blocklist/sample filtering so removed files no longer count.
/// See issue #1090.
/// </summary>
public class RenameSingleVideoPostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    public void RenameToReleaseName(DavItem mountFolder)
    {
        if (!configManager.IsRenameSingleVideoToReleaseEnabled()) return;

        var videos = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => x.HistoryItemId == mountFolder.HistoryItemId)
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .ToList();
        if (videos.Count != 1) return;

        var video = videos[0];
        var extension = Path.GetExtension(video.Name);
        if (string.IsNullOrEmpty(extension)) return;
        var extensionDigitsOnly = extension.TrimStart('.').All(char.IsDigit);
        if (extension.Length <= 1 || extensionDigitsOnly) return;

        var baseName = mountFolder.Name;
        var releaseBaseName = baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? baseName[..^extension.Length]
            : baseName;
        var newName = PathSanitizer.SanitizeComponent(releaseBaseName) + extension;
        if (string.Equals(newName, video.Name, StringComparison.Ordinal)) return;

        if (SiblingNameTaken(video, newName))
        {
            Log.Warning(
                "Skipped single-video rename to {NewName}: a sibling with that name already exists under {ParentPath}",
                newName, Path.GetDirectoryName(video.Path));
            return;
        }

        var oldPath = video.Path;
        video.Name = newName;
        video.Path = Path.Join(Path.GetDirectoryName(oldPath), newName);
        Log.Information(
            "Renamed single imported video {OldPath} to {NewPath} for release {ReleaseName} (history item {HistoryItemId})",
            oldPath, video.Path, mountFolder.Name, mountFolder.HistoryItemId);
    }

    private bool SiblingNameTaken(DavItem video, string newName)
    {
        var trackedConflict = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Unchanged)
            .Select(x => x.Entity)
            .Any(x => x.Id != video.Id && x.ParentId == video.ParentId && x.Name == newName);
        if (trackedConflict) return true;

        return dbClient.Ctx.Items
            .Any(x => x.ParentId == video.ParentId && x.Name == newName && x.Id != video.Id);
    }
}
