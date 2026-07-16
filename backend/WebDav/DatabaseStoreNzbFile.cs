using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext, dbClient)
{
    public override DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;
    public override Guid? NzbBlobId => davNzbFile.NzbBlobId;

    protected override Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davNzbFile;

        return DavFileStreamFactory.GetStreamAsync(
            davNzbFile, dbClient, usenetClient, configManager.GetArticleBufferSize(), cancellationToken);
    }
}