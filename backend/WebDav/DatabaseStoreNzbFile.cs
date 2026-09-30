using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreStreamFile(httpContext, configManager)
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
        Context.Items["DavItem"] = davNzbFile;

        return DavContentStreamFactory.OpenNzbAsync(davNzbFile, dbClient, usenetClient, Config,
            inFlightArticleBudget, cancellationToken);
    }
}
