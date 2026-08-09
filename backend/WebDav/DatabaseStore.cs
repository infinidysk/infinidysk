using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStore(
    IHttpContextAccessor httpContextAccessor,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget
) : IStore
{
    private readonly DatabaseStoreCollection _root = new(
        DavItem.Root,
        httpContextAccessor.HttpContext!,
        dbClient,
        configManager,
        usenetClient,
        queueManager,
        websocketManager,
        lazyRarResolver,
        inFlightArticleBudget
    );

    public async Task<IStoreItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = path.Trim('/');
        if (string.IsNullOrEmpty(path)) return _root;

        // Fast path: a single indexed lookup by absolute path. This handles the overwhelmingly
        // common case (streaming/serving a real, persisted file or directory) in one query
        // instead of one per path segment.
        var byPath = await dbClient.GetItemByPathAsync("/" + path, cancellationToken).ConfigureAwait(false);
        if (byPath is not null)
            return DatabaseStoreItemFactory.Create(
                byPath, httpContextAccessor.HttpContext!, dbClient, configManager,
                usenetClient, queueManager, websocketManager, lazyRarResolver, inFlightArticleBudget);

        // Fallback: walk the collection hierarchy segment-by-segment. This covers synthetic
        // items that have no persisted row (empty category folders, .ids children, the
        // readme, empty placeholder files created by WebDAV clients).
        return await _root.ResolvePath(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
    {
        return GetItemAsync(Uri.UnescapeDataString(uri.AbsolutePath), cancellationToken);
    }

    public async Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        return await GetItemAsync(uri, cancellationToken).ConfigureAwait(false) as IStoreCollection;
    }
}
