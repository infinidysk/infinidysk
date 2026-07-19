using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreStreamFile(HttpContext context, DavDatabaseClient dbClient) : BaseStoreReadonlyItem
{
    public abstract DavItem DavItem { get; }

    protected abstract Task<Stream> GetStreamAsync(CancellationToken cancellationToken);

    public override async Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        // serve from the local prefetch cache, if a complete copy is available on disk
        var cachedStream = await TryGetCachedStreamAsync(cancellationToken).ConfigureAwait(false);
        if (cachedStream != null) return cachedStream;

        var downloadPriorityContext = new DownloadPriorityContext() { Priority = SemaphorePriority.High };
        var scopedDownloadPriorityContext = cancellationToken.SetContext(downloadPriorityContext);
        context.Response.OnCompleted(() =>
        {
            scopedDownloadPriorityContext.Dispose();
            return Task.CompletedTask;
        });

        return await GetStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream?> TryGetCachedStreamAsync(CancellationToken cancellationToken)
    {
        var cachedEpisode = await dbClient
            .GetCompleteCachedEpisodeAsync(DavItem.Id, cancellationToken)
            .ConfigureAwait(false);
        if (cachedEpisode is null || !File.Exists(cachedEpisode.FilePath)) return null;

        // store the DavItem being accessed in the http context, same as the live-download paths do
        context.Items["DavItem"] = DavItem;

        // touch last-accessed time in the background; a cache hit shouldn't wait on a DB write
        _ = dbClient.TouchCachedEpisodeLastAccessedAsync(DavItem.Id, CancellationToken.None);

        return new FileStream(cachedEpisode.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}