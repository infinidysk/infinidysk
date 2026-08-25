using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Queue.PostProcessors;

/// <summary>
/// Creates optional filesystem symlinks for completed media. The default symlink
/// output remains the virtual WebDAV completed-symlinks tree and does not use this
/// post-processor.
/// </summary>
public class CreateSymlinkFilesPostProcessor(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    Guid historyItemId)
{
    public async Task CreateSymlinkFilesAsync()
    {
        var outputDirectory = configManager.GetSymlinkOutputDirectory();
        if (outputDirectory is null)
            return;

        var created = new List<DavItem>();
        try
        {
            foreach (var videoItem in CollectVideoItems())
            {
                if (await CreateSymlinkAsync(outputDirectory, videoItem).ConfigureAwait(false))
                    created.Add(videoItem);
            }
        }
        catch
        {
            foreach (var createdItem in created)
                DeleteSymlinkFile(createdItem);
            throw;
        }
    }

    internal List<DavItem> CollectVideoItems()
    {
        var byId = new Dictionary<Guid, DavItem>();

        foreach (var item in dbClient.Ctx.ChangeTracker.Entries<DavItem>()
                     .Where(x => x.State == EntityState.Added)
                     .Select(x => x.Entity)
                     .Where(CreateStrmFilesPostProcessor.IsStrmCandidate))
        {
            byId[item.Id] = item;
        }

        foreach (var item in dbClient.Ctx.Items
                     .Where(x => x.HistoryItemId == historyItemId
                                 && x.Type != DavItem.ItemType.Directory)
                     .AsEnumerable()
                     .Where(CreateStrmFilesPostProcessor.IsStrmCandidate))
        {
            byId.TryAdd(item.Id, item);
        }

        return byId.Values.ToList();
    }

    private async Task<bool> CreateSymlinkAsync(string outputDirectory, DavItem davItem)
    {
        var symlinkPath = GetSymlinkFilePath(outputDirectory, davItem);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var fullPath = Path.GetFullPath(symlinkPath);
        if (!CreateStrmFilesPostProcessor.IsPathWithinRoot(fullPath, outputRoot))
            throw new IOException($"Generated symlink path '{symlinkPath}' escapes its configured output directory.");
        if (CreateStrmFilesPostProcessor.HasSymlinkedAncestor(fullPath, outputRoot))
            throw new IOException($"Generated symlink path '{symlinkPath}' is beneath a symbolic-link directory.");

        var directoryName = Path.GetDirectoryName(fullPath);
        if (directoryName is not null)
            await Task.Run(() => Directory.CreateDirectory(directoryName)).ConfigureAwait(false);
        if (CreateStrmFilesPostProcessor.HasSymlinkedAncestor(fullPath, outputRoot))
            throw new IOException($"Generated symlink path '{symlinkPath}' is beneath a symbolic-link directory.");

        var target = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, configManager.GetRcloneMountDir());
        var wasCreated = await Task.Run(() => CreateOwnedSymlink(fullPath, target)).ConfigureAwait(false);
        davItem.GeneratedSymlinkOutputRoot = outputRoot;
        davItem.GeneratedSymlinkPath = fullPath;
        davItem.GeneratedSymlinkTarget = target;
        return wasCreated;
    }

    internal static string GetSymlinkFilePath(string outputDirectory, DavItem davItem)
    {
        return Path.Join(
            outputDirectory,
            CreateStrmFilesPostProcessor.GetPathRelativeToContentRoot(davItem.Path));
    }

    /// <summary>
    /// Removes a generated symlink only when its target belongs to <paramref name="davItem"/>.
    /// </summary>
    /// <returns>True when an owned symlink was deleted.</returns>
    internal static bool DeleteSymlinkFile(DavItem davItem)
    {
        if (!CreateStrmFilesPostProcessor.IsStrmCandidate(davItem)
            || string.IsNullOrWhiteSpace(davItem.GeneratedSymlinkPath)
            || string.IsNullOrWhiteSpace(davItem.GeneratedSymlinkTarget)
            || string.IsNullOrWhiteSpace(davItem.GeneratedSymlinkOutputRoot))
            return false;

        var outputRoot = Path.GetFullPath(davItem.GeneratedSymlinkOutputRoot);
        var symlinkPath = Path.GetFullPath(davItem.GeneratedSymlinkPath);
        if (!CreateStrmFilesPostProcessor.IsPathWithinRoot(symlinkPath, outputRoot)
            || CreateStrmFilesPostProcessor.HasSymlinkedAncestor(symlinkPath, outputRoot))
            return false;

        var file = new FileInfo(symlinkPath);
        if (!string.Equals(file.LinkTarget, davItem.GeneratedSymlinkTarget, GetPathComparison()))
            return false;

        File.Delete(symlinkPath);
        try
        {
            CreateStrmFilesPostProcessor.DeleteEmptyParentDirectories(
                Path.GetDirectoryName(symlinkPath),
                outputRoot);
        }
        catch (DirectoryNotFoundException)
        {
            // A concurrent cleanup already pruned the empty parent directory.
        }

        return true;
    }

    private static bool CreateOwnedSymlink(string path, string target)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            if (string.Equals(file.LinkTarget, target, GetPathComparison()))
                return false;

            throw new IOException(
                $"Refusing to replace existing symlink '{path}' because it targets a different location.");
        }

        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Refusing to replace existing filesystem entry '{path}'.");

        File.CreateSymbolicLink(path, target);
        return true;
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
