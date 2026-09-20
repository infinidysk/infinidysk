using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreMultipartFile(
    DavItem davMultipartFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreStreamFile(httpContext, configManager)
{
    public DavItem DavItem => davMultipartFile;
    public override string Name => davMultipartFile.Name;
    public override string UniqueKey => davMultipartFile.Id.ToString();
    public override long FileSize => davMultipartFile.FileSize!.Value;
    public override DateTime CreatedAt => davMultipartFile.CreatedAt;
    public override Guid? NzbBlobId => davMultipartFile.NzbBlobId;

    protected override Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        Context.Items["DavItem"] = davMultipartFile;

        return DavContentStreamFactory.OpenMultipartAsync(davMultipartFile, dbClient, usenetClient, Config,
            lazyRarResolver, inFlightArticleBudget, ct);
    }
}
