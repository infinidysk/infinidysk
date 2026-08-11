using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreSymlinkCollection(
    DavItem davDirectory,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : BaseStoreReadonlyCollection
{
    public override string Name => davDirectory.Name;
    public override string UniqueKey => davDirectory.Id.ToString();
    public override DateTime CreatedAt => davDirectory.CreatedAt;

    // Every nested collection under the virtual mount shares one throttle window so a
    // per-release metadata write storm cannot emit one Warning per directory.
    protected override string WriteRejectionScopeKey => "completed-symlinks";

    private Guid TargetId => davDirectory.Id == DavItem.SymlinkFolder.Id ? DavItem.ContentFolder.Id : davDirectory.Id;
    private DeletedFileManager DeletedFiles => new(davDirectory.Id);

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // return deleted file
        if (DeletedFiles.IsDeleted(request.Name))
            return null;

        // return database item
        var name = Regex.Replace(request.Name, @"\.rclonelink$", "");
        var child = await dbClient
            .GetDirectoryChildAsync(TargetId, name, request.CancellationToken)
            .ConfigureAwait(false);
        if (child is not null) return GetItem(child);

        // return empty category folder
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        if (isSymlinkFolder)
        {
            var categories = configManager.GetApiCategories();
            if (categories.Contains(request.Name))
            {
                return new BaseStoreEmptyCollection(request.Name);
            }
        }

        // the item does not exist
        return null;
    }

    protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // if we are a category folder within the /completed-symlinks dir,
        // then we only want to show children that correspond to Completed History items.
        var isCategoryFolder = davDirectory.ParentId == DavItem.ContentFolder.Id;
        var children = isCategoryFolder
            ? dbClient.GetCompletedSymlinkCategoryChildrenEnumerableAsync(davDirectory.Name, cancellationToken)
            : dbClient.GetDirectoryChildrenEnumerableAsync(TargetId, cancellationToken);

        // include any missing category folders
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        HashSet<string>? childNames = isSymlinkFolder ? [] : null;

        await foreach (var child in children.ConfigureAwait(false))
        {
            childNames?.Add(child.Name);
            var item = GetItem(child);
            if (!DeletedFiles.IsDeleted(item.Name))
                yield return item;
        }

        if (isSymlinkFolder)
        {
            foreach (var category in configManager.GetApiCategories().Where(category => !childNames!.Contains(category)))
            {
                var item = new BaseStoreEmptyCollection(category);
                if (!DeletedFiles.IsDeleted(item.Name))
                    yield return item;
            }
        }
    }

    protected override async Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        // Cannot delete from symlink root folder
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        if (isSymlinkFolder) return await base.DeleteItemAsync(request).ConfigureAwait(false);

        // Clearing a completed release (pruning its history) requires write access, mirroring
        // the read-only enforcement on real deletes under /content.
        if (!configManager.IsEnforceReadonlyWebdavEnabled() && davDirectory.ParentId == DavItem.ContentFolder.Id)
        {
            var child = await dbClient.GetDirectoryChildAsync(TargetId, request.Name, request.CancellationToken).ConfigureAwait(false);
            if (child is { Type: DavItem.ItemType.Directory } && !child.IsProtected())
            {
                var historyIds = await dbClient.Ctx.HistoryItems.AsNoTracking()
                    .Where(h => h.DownloadDirId == child.Id && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                    .Select(h => h.Id).ToListAsync(request.CancellationToken).ConfigureAwait(false);
                if (historyIds.Count > 0)
                {
                    await dbClient.RemoveHistoryItemsAsync(historyIds, deleteFiles: false, request.CancellationToken).ConfigureAwait(false);
                    await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
                    _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", historyIds));
                    return DavStatusCode.NoContent;
                }
            }
        }

        // Every other delete under '/completed-symlinks' must succeed, even when read-only
        // enforcement is on. Radarr/Sonarr import the symlink by moving it to the media library;
        // since the symlink lives on a separate file-system (rclone-mounted webdav), the OS
        // performs a copy-and-delete, and a refused delete fails the whole import. Nothing here
        // actually exists on disk — the symlink is created in memory per request and the
        // underlying data under '/content' is untouched — so we cache the name for 30 seconds
        // to mimic a deletion and return 204 No Content.
        DeletedFiles.AddDeletedFile(request.Name, TimeSpan.FromSeconds(30));
        return DavStatusCode.NoContent;
    }

    private IStoreItem GetItem(DavItem davItem)
    {
        return davItem.SubType switch
        {
            DavItem.ItemSubType.Directory =>
                new DatabaseStoreSymlinkCollection(davItem, dbClient, configManager, websocketManager),
            DavItem.ItemSubType.NzbFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            DavItem.ItemSubType.RarFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            DavItem.ItemSubType.MultipartFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            _ => throw new ArgumentException("Unrecognized directory child type.")
        };
    }

    private class DeletedFileManager(Guid directoryId)
    {
        private static readonly MemoryCache DeletedFiles = new(new MemoryCacheOptions());

        public void AddDeletedFile(string filename, TimeSpan? expiry = null)
        {
            using var entry = DeletedFiles.CreateEntry(GetKey(filename));
            entry.SlidingExpiration = expiry ?? TimeSpan.FromSeconds(30);
            entry.Value = true;
        }

        public bool IsDeleted(string filename)
        {
            return (bool)(DeletedFiles.Get(GetKey(filename)) ?? false);
        }

        private string GetKey(string filename)
        {
            return $"{directoryId}/{filename}";
        }
    }
}
