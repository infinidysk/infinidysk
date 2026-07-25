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

/// <summary>
/// Maps a persisted <see cref="DavItem"/> onto the corresponding NWebDav store item.
/// Centralized here so the directory listing path (<see cref="DatabaseStoreCollection"/>)
/// and the direct path lookup (<see cref="DatabaseStore"/>) build items identically.
/// </summary>
public static class DatabaseStoreItemFactory
{
    public static IStoreItem Create(
        DavItem davItem,
        HttpContext httpContext,
        DavDatabaseClient dbClient,
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        QueueManager queueManager,
        WebsocketManager websocketManager,
        LazyRarResolver lazyRarResolver,
        InFlightArticleBudget inFlightArticleBudget)
    {
        return davItem.SubType switch
        {
            DavItem.ItemSubType.IdsRoot =>
                new DatabaseStoreIdsCollection(
                    davItem.Name, "", httpContext, dbClient, usenetClient, configManager, lazyRarResolver,
                    inFlightArticleBudget),
            DavItem.ItemSubType.NzbsRoot =>
                new DatabaseStoreWatchFolder(
                    davItem, dbClient, configManager, queueManager, websocketManager),
            DavItem.ItemSubType.Directory or DavItem.ItemSubType.ContentRoot =>
                new DatabaseStoreCollection(
                    davItem, httpContext, dbClient, configManager, usenetClient, queueManager, websocketManager,
                    lazyRarResolver, inFlightArticleBudget),
            DavItem.ItemSubType.SymlinkRoot =>
                new DatabaseStoreSymlinkCollection(
                    davItem, dbClient, configManager),
            DavItem.ItemSubType.NzbFile =>
                new DatabaseStoreNzbFile(
                    davItem, httpContext, dbClient, usenetClient, configManager, inFlightArticleBudget),
            DavItem.ItemSubType.RarFile =>
                new DatabaseStoreRarFile(
                    davItem, httpContext, dbClient, usenetClient, configManager, inFlightArticleBudget),
            DavItem.ItemSubType.MultipartFile =>
                new DatabaseStoreMultipartFile(
                    davItem, httpContext, dbClient, usenetClient, configManager, lazyRarResolver,
                    inFlightArticleBudget),
            _ => throw new ArgumentException("Unrecognized directory child type.")
        };
    }
}
