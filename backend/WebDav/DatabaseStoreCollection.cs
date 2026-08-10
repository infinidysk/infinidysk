using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreCollection(
    DavItem davDirectory,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreReadonlyCollection
{
    public override string Name => davDirectory.Name;
    public override string UniqueKey => davDirectory.Id.ToString();
    public override DateTime CreatedAt => davDirectory.CreatedAt;
    private static readonly StaticEmbeddedFile Readme = new("StaticFiles.root.README.md", "README");

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // return readme file
        var isReadme = davDirectory.Id == DavItem.Root.Id && request.Name == Readme.Name;
        if (isReadme) return Readme;

        // return database item
        var child = await dbClient
            .GetDirectoryChildAsync(davDirectory.Id, request.Name, request.CancellationToken)
            .ConfigureAwait(false);
        if (child is not null)
            return DatabaseStoreItemFactory.Create(
                child, httpContext, dbClient, configManager, usenetClient, queueManager, websocketManager,
                lazyRarResolver, inFlightArticleBudget);

        // return empty category folder
        var isContentFolder = davDirectory.Id == DavItem.ContentFolder.Id;
        if (isContentFolder)
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
        var isContentFolder = davDirectory.Id == DavItem.ContentFolder.Id;
        HashSet<string>? childNames = isContentFolder ? [] : null;

        await foreach (var child in dbClient
                           .GetDirectoryChildrenEnumerableAsync(davDirectory.Id, cancellationToken)
                           .ConfigureAwait(false))
        {
            childNames?.Add(child.Name);
            yield return DatabaseStoreItemFactory.Create(
                child, httpContext, dbClient, configManager, usenetClient, queueManager, websocketManager,
                lazyRarResolver, inFlightArticleBudget);
        }

        // include the readme file
        if (davDirectory.Id == DavItem.Root.Id)
            yield return Readme;

        // include any missing category folders
        if (isContentFolder)
        {
            foreach (var category in configManager.GetApiCategories().Where(category => !childNames!.Contains(category)))
            {
                yield return new BaseStoreEmptyCollection(category);
            }
        }
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }

    protected override async Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        // Cannot delete items if readonly-webdav is enabled
        if (configManager.IsEnforceReadonlyWebdavEnabled())
            return DavStatusCode.Forbidden;

        // Cannot delete items from dav root.
        if (davDirectory.Id == DavItem.Root.Id)
            return DavStatusCode.Forbidden;

        // Get the item being deleted
        var davItem = await dbClient.GetDirectoryChildAsync(davDirectory.Id, request.Name, request.CancellationToken)
            .ConfigureAwait(false);
        if (davItem is null) return DavStatusCode.NotFound;

        // If the item is a file, simply delete it and we're done.
        if (davItem.Type is DavItem.ItemType.UsenetFile)
        {
            var historyItemId = davItem.HistoryItemId;
            DeletionAuditLog.Record("webdav-delete", davItem, "client DELETE on UsenetFile");
            dbClient.Ctx.Items.Remove(davItem);
            await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
            await PruneEmptyHistoryAsync(historyItemId, request.CancellationToken).ConfigureAwait(false);
            return DavStatusCode.Ok;
        }

        // If the item is a directory and it not a protected directory, simply delete it.
        if (davItem.Type == DavItem.ItemType.Directory && !davItem.IsProtected())
        {
            var historyItemId = davItem.HistoryItemId;
            DeletionAuditLog.Record("webdav-delete", davItem, "client DELETE on directory");
            dbClient.Ctx.Items.Remove(davItem);
            await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
            await PruneEmptyHistoryAsync(historyItemId, request.CancellationToken).ConfigureAwait(false);
            return DavStatusCode.Ok;
        }

        // forbid deletion of any other items
        return DavStatusCode.Forbidden;
    }

    // After deleting a DavItem, if no other DavItems still reference its HistoryItem,
    // remove the HistoryItem too. Without this, external tools polling /api?mode=history
    // (third-party SAB-compatible clients, Sonarr, etc.) see the entry as Completed and hand the
    // player a URL pointing at the file we just deleted — re-clicking never re-enqueues.
    private async Task PruneEmptyHistoryAsync(Guid? historyItemId, CancellationToken ct)
    {
        if (historyItemId is null) return;
        var pruned = await dbClient.PruneUnreferencedHistoryItemsAsync([historyItemId.Value], ct)
            .ConfigureAwait(false);
        if (pruned.Count == 0) return;

        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, pruned[0].ToString());
    }
}
