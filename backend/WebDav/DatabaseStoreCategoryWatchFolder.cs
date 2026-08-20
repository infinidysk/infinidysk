using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreCategoryWatchFolder(
    string category,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : BaseStoreReadonlyCollection
{
    public override string Name => category;
    public override string UniqueKey => $"nzbs_category_{category}";
    public override DateTime CreatedAt => DateTime.Now;
    protected override bool SupportsEmptyFileStaging => true;

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        var queueItem = await dbClient.Ctx.QueueItems
            .Where(x => x.FileName == request.Name && x.Category == category)
            .FirstOrDefaultAsync(request.CancellationToken).ConfigureAwait(false);
        if (queueItem is null) return null;
        return new DatabaseStoreQueueItem(queueItem, dbClient);
    }

    protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var queueItem in await dbClient
                     .GetQueueItems(category, 0, int.MaxValue, cancellationToken)
                     .ConfigureAwait(false))
        {
            yield return new DatabaseStoreQueueItem(queueItem, dbClient);
        }
    }

    protected override async Task<StoreItemResult> CreateItemAsync(CreateItemRequest request)
    {
        var service = new NzbSubmissionService(dbClient, queueManager, configManager, websocketManager);
        var response = await service.SubmitAsync(new NzbSubmissionRequest
        {
            FileName = request.Name,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.RepairUnpackDelete,
            PauseUntil = DateTime.Now.AddSeconds(3),
            NzbFileStream = request.Stream,
            CancellationToken = request.CancellationToken,
        }).ConfigureAwait(false);
        if (!response.Status)
            return new StoreItemResult(DavStatusCode.InsufficientStorage);

        var queueItem = dbClient.Ctx.ChangeTracker
            .Entries<QueueItem>()
            .Select(x => x.Entity)
            .First(x => x.Id.ToString() == response.NzoIds[0]);
        return new StoreItemResult(DavStatusCode.Created, new DatabaseStoreQueueItem(queueItem, dbClient));
    }

    protected override async Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        var service = new QueueRemovalService(dbClient, queueManager, websocketManager);

        // get the item to delete
        var item = await dbClient.Ctx.QueueItems
            .Where(x => x.FileName == request.Name && x.Category == category)
            .FirstOrDefaultAsync(request.CancellationToken).ConfigureAwait(false);

        // if the item doesn't exist, return 404
        if (item is null)
            return DavStatusCode.NotFound;

        // delete the item
        dbClient.Ctx.ClearChangeTracker();
        await service.RemoveAsync([item.Id], request.CancellationToken).ConfigureAwait(false);
        return DavStatusCode.NoContent;
    }
}
