using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Queue.PostProcessors;

public class CreateStrmFilesPostProcessor(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    Guid historyItemId)
{
    private static readonly string ContentRootPrefix =
        DavItem.ContentFolder.Path.TrimEnd('/') + "/";

    public async Task CreateStrmFilesAsync()
    {
        var candidates = CollectVideoItems();
        foreach (var videoItem in candidates)
            await CreateStrmFileAsync(videoItem).ConfigureAwait(false);
    }

    internal List<DavItem> CollectVideoItems()
    {
        var byId = new Dictionary<Guid, DavItem>();

        foreach (var item in dbClient.Ctx.ChangeTracker.Entries<DavItem>()
                     .Where(x => x.State == EntityState.Added)
                     .Select(x => x.Entity)
                     .Where(IsStrmCandidate))
        {
            byId[item.Id] = item;
        }

        foreach (var item in dbClient.Ctx.Items
                     .Where(x => x.HistoryItemId == historyItemId
                                 && x.Type != DavItem.ItemType.Directory)
                     .AsEnumerable()
                     .Where(IsStrmCandidate))
        {
            byId.TryAdd(item.Id, item);
        }

        return byId.Values.ToList();
    }

    internal static bool IsStrmCandidate(DavItem item) =>
        FilenameUtil.IsVideoFile(item.Name)
        && !item.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes (or updates) the STRM sidecar for a DavItem. Shared by queue post-processing
    /// and the Recreate STRM maintenance task.
    /// </summary>
    internal static async Task WriteStrmFileAsync(
        ConfigManager configManager,
        DavItem davItem,
        bool forceRewrite,
        CancellationToken cancellationToken = default)
    {
        if (!IsStrmCandidate(davItem))
            return;

        var strmFilePath = Path.GetFullPath(GetStrmFilePath(configManager, davItem));
        var completedDownloadsRoot = Path.GetFullPath(configManager.GetStrmCompletedDownloadDir());
        if (!IsPathWithinRoot(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' escapes its configured output directory.");
        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' is beneath a symbolic-link directory.");

        var directoryName = Path.GetDirectoryName(strmFilePath);
        if (directoryName != null)
            await Task.Run(() => Directory.CreateDirectory(directoryName), cancellationToken).ConfigureAwait(false);
        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' is beneath a symbolic-link directory.");

        var targetUrl = GetStrmTargetUrl(configManager, davItem);
        if (!forceRewrite && File.Exists(strmFilePath))
        {
            var existing = await File.ReadAllTextAsync(strmFilePath, cancellationToken).ConfigureAwait(false);
            if (existing == targetUrl)
                return;
        }

        await File.WriteAllTextAsync(strmFilePath, targetUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateStrmFileAsync(DavItem davItem) =>
        await WriteStrmFileAsync(configManager, davItem, forceRewrite: false).ConfigureAwait(false);

    internal static string GetStrmFilePath(ConfigManager configManager, DavItem davItem)
    {
        var relativePath = GetPathRelativeToContentRoot(davItem.Path) + ".strm";
        return Path.Join(configManager.GetStrmCompletedDownloadDir(), relativePath);
    }

    /// <summary>
    /// Removes a generated STRM sidecar only when its target belongs to <paramref name="davItem"/>.
    /// </summary>
    internal static void DeleteStrmFile(ConfigManager configManager, DavItem davItem)
    {
        if (!IsStrmCandidate(davItem))
            return;

        var completedDownloadsRoot = Path.GetFullPath(configManager.GetStrmCompletedDownloadDir());
        var strmFilePath = Path.GetFullPath(GetStrmFilePath(configManager, davItem));
        if (!IsPathWithinRoot(strmFilePath, completedDownloadsRoot))
            return;

        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            return;

        SymlinkAndStrmUtil.ISymlinkOrStrmInfo? strmOrSymlink;
        try
        {
            strmOrSymlink = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(strmFilePath));
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (strmOrSymlink is not SymlinkAndStrmUtil.StrmInfo strmInfo)
            return;

        var link = OrganizedLinksUtil.GetDavItemLink(strmInfo);
        if (link?.DavItemId != davItem.Id)
            return;

        File.Delete(strmFilePath);
        try
        {
            DeleteEmptyParentDirectories(Path.GetDirectoryName(strmFilePath), completedDownloadsRoot);
        }
        catch (DirectoryNotFoundException)
        {
            // A concurrent cleanup already pruned the empty parent directory.
        }
    }

    internal string GetStrmFilePath(DavItem davItem) =>
        GetStrmFilePath(configManager, davItem);

    internal static bool IsPathWithinRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison);
    }

    internal static bool HasSymlinkedAncestor(string path, string root)
    {
        var directoryPath = Path.GetDirectoryName(path);
        while (directoryPath is not null)
        {
            if (new DirectoryInfo(directoryPath).LinkTarget is not null)
                return true;

            if (string.Equals(
                    directoryPath.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return false;

            directoryPath = Path.GetDirectoryName(directoryPath);
        }

        return true;
    }

    internal static void DeleteEmptyParentDirectories(string? directoryPath, string root)
    {
        while (directoryPath != null && IsPathWithinRoot(directoryPath, root))
        {
            if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                return;

            Directory.Delete(directoryPath);
            directoryPath = Path.GetDirectoryName(directoryPath);
        }
    }

    internal static string GetPathRelativeToContentRoot(string davPath)
    {
        if (davPath.StartsWith(ContentRootPrefix, StringComparison.Ordinal))
            return davPath[ContentRootPrefix.Length..];

        // Fallback: preserve previous parts[2..] behavior for unexpected layouts.
        var parts = davPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 2 ? Path.Join(parts[2..]) : Path.GetFileName(davPath);
    }

    internal static string GetStrmTargetUrl(ConfigManager configManager, DavItem davItem)
    {
        var baseUrl = configManager.GetBaseUrl();
        if (baseUrl.EndsWith('/')) baseUrl = baseUrl.TrimEnd('/');
        var pathUrl = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, "", '/');
        if (pathUrl.StartsWith('/')) pathUrl = pathUrl.TrimStart('/');
        var strmKey = configManager.GetStrmKey();
        var downloadKey = DownloadKey.Generate(strmKey, pathUrl);
        var extension = Path.GetExtension(davItem.Name).ToLowerInvariant().TrimStart('.');
        return $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";
    }

    private string GetStrmTargetUrl(DavItem davItem) =>
        GetStrmTargetUrl(configManager, davItem);
}
