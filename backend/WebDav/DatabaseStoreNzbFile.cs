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
    public DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;
    public override Guid? NzbBlobId => davNzbFile.NzbBlobId;

    protected override async Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        // store the DavItem being accessed in the http context
        Context.Items["DavItem"] = davNzbFile;

        var file = await dbClient.GetDavNzbFileAsync(davNzbFile, cancellationToken).ConfigureAwait(false);
        if (file is null)
            throw new MissingFilePayloadException(davNzbFile, DavItem.ItemSubType.NzbFile);
        return GetStream(file);
    }

    private NzbFileStream GetStream(DavNzbFile nzbFile)
    {
        return usenetClient.GetFileStream(
            nzbFile.SegmentIds,
            FileSize,
            Config.GetArticleBufferSize(),
            nzbFile.SegmentByteRanges,
            Config.IsPipelinedBodyRequestsEnabled(),
            davNzbFile.Path,
            nzbFile.SegmentFallbackIds,
            inFlightArticleBudget,
            useContainerAwareFill: Config.IsContainerAwareFillEnabled(),
            streamingBodyBatchWidth: Config.GetStreamingBodyBatchWidth(),
            knownCorruptSegmentIds: ResolveKnownCorruptSegmentIds(nzbFile),
            knownMissingSegmentIndices: ResolveKnownMissingSegmentIndices(nzbFile));
    }

    private HashSet<string>? ResolveKnownCorruptSegmentIds(DavNzbFile nzbFile)
    {
        if (!Config.IsCorruptionTrackingEnabled()) return null;
        if (nzbFile.CorruptSegmentIndices is not { Length: > 0 } indices) return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indices.Where(i => (uint)i < (uint)nzbFile.SegmentIds.Length))
            ids.Add(nzbFile.SegmentIds[index]);

        return ids.Count == 0 ? null : ids;
    }

    private HashSet<int>? ResolveKnownMissingSegmentIndices(DavNzbFile nzbFile)
    {
        if (!Config.IsDegradedToleranceEnabled()) return null;
        if (nzbFile.SegmentByteRanges is not { } ranges || ranges.Length != nzbFile.SegmentIds.Length)
            return null;
        if (nzbFile.MissingSegmentIndices is not { Length: > 0 } indices) return null;

        var validIndices = indices
            .Where(index => index > 0 && (uint)index < (uint)nzbFile.SegmentIds.Length)
            .ToHashSet();
        return validIndices.Count == 0 ? null : validIndices;
    }
}
