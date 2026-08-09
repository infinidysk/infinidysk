using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
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

    private static bool IsStrmCandidate(DavItem item) =>
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

        var strmFilePath = GetStrmFilePath(configManager, davItem);
        var directoryName = Path.GetDirectoryName(strmFilePath);
        if (directoryName != null)
            await Task.Run(() => Directory.CreateDirectory(directoryName), cancellationToken).ConfigureAwait(false);

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

    internal string GetStrmFilePath(DavItem davItem) =>
        GetStrmFilePath(configManager, davItem);

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
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(strmKey, pathUrl);
        var extension = Path.GetExtension(davItem.Name).ToLowerInvariant().TrimStart('.');
        return $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";
    }

    private string GetStrmTargetUrl(DavItem davItem) =>
        GetStrmTargetUrl(configManager, davItem);
}
