using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreMultipartFile(
    DavItem davMultipartFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext, dbClient)
{
    public override DavItem DavItem => davMultipartFile;
    public override string Name => davMultipartFile.Name;
    public override string UniqueKey => davMultipartFile.Id.ToString();
    public override long FileSize => davMultipartFile.FileSize!.Value;
    public override DateTime CreatedAt => davMultipartFile.CreatedAt;
    public override Guid? NzbBlobId => davMultipartFile.NzbBlobId;

    protected override Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davMultipartFile;

        return DavFileStreamFactory.GetStreamAsync(
            davMultipartFile, dbClient, usenetClient, configManager.GetArticleBufferSize(), ct);
    }
}