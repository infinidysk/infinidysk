using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreRarFile(
    DavItem davRarFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext, dbClient)
{
    public override DavItem DavItem => davRarFile;
    public override string Name => davRarFile.Name;
    public override string UniqueKey => davRarFile.Id.ToString();
    public override long FileSize => davRarFile.FileSize!.Value;
    public override DateTime CreatedAt => davRarFile.CreatedAt;
    public override Guid? NzbBlobId => davRarFile.NzbBlobId;

    protected override Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davRarFile;

        return DavFileStreamFactory.GetStreamAsync(
            davRarFile, dbClient, usenetClient, configManager.GetArticleBufferSize(), ct);
    }
}