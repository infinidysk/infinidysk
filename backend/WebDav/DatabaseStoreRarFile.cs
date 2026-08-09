using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreRarFile(
    DavItem davRarFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreStreamFile(httpContext, configManager)
{
    public DavItem DavItem => davRarFile;
    public override string Name => davRarFile.Name;
    public override string UniqueKey => davRarFile.Id.ToString();
    public override long FileSize => davRarFile.FileSize!.Value;
    public override DateTime CreatedAt => davRarFile.CreatedAt;
    public override Guid? NzbBlobId => davRarFile.NzbBlobId;

    protected override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        Context.Items["DavItem"] = davRarFile;

        var id = davRarFile.Id;
        var rarFile = await dbClient.GetDavRarFileAsync(davRarFile, ct).ConfigureAwait(false);
        if (rarFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");
        return GetStream(rarFile);
    }

    private DavMultipartFileStream GetStream(DavRarFile rarFile)
    {
        // Legacy DavRarFile records are always fully resolved (no lazy
        // PendingParts), so we wrap them in a transient DavMultipartFile
        // and pass a null resolver.
        var transient = new DavMultipartFile
        {
            Id = rarFile.Id,
            Metadata = rarFile.ToDavMultipartFileMeta(),
        };
        return new DavMultipartFileStream
        (
            transient,
            usenetClient,
            Config.GetArticleBufferSize(),
            resolver: null,
            usePipelinedBodyRequests: Config.IsPipelinedBodyRequestsEnabled(),
            fileName: davRarFile.Path,
            inFlightArticleBudget: inFlightArticleBudget
        );
    }
}
