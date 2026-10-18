using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Streams;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreIdFile(
    DavItem davItem,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreReadonlyItem, IDetachedStreamSource
{
    public SharedContentIdentity ContentIdentity =>
        new(UniqueKey, davItem.FileBlobId, FileSize);
    public Guid DavItemId => davItem.Id;
    public override string Name => davItem.Id.ToString();
    public override string UniqueKey => davItem.Id.ToString();
    public override long FileSize => davItem.FileSize!.Value;
    public override DateTime CreatedAt => davItem.CreatedAt;

    // Name is overridden to the GUID so rclone symlink targets stay stable; this
    // exposes the underlying human-readable filename for surfaces like the
    // Active Reads widget that need to display something the user recognises.
    public string FriendlyName => davItem.Name;

    public Guid? HistoryItemId => davItem.HistoryItemId;

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        return GetItem(davItem).GetReadableStreamAsync(cancellationToken);
    }

    public async Task<DetachedStreamLease> GetDetachedReadableStreamAsync(CancellationToken cancellationToken)
    {
        var item = GetItem(davItem);
        if (item is IDetachedStreamSource source)
        {
            var lease = await source.GetDetachedReadableStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DetachedStreamLease
            {
                Stream = lease.Stream,
                Ownership = lease.Ownership,
                ContentIdentity = ContentIdentity,
                DavItem = lease.DavItem,
            };
        }

        throw new InvalidOperationException($"Id child type {davItem.SubType} cannot open a detached stream.");
    }

    private IStoreItem GetItem(DavItem davItem)
    {
        return davItem.SubType switch
        {
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
            _ => throw new ArgumentException("Unrecognized id child type.")
        };
    }
}
