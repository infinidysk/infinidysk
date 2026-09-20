using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Streams;

/// <summary>One final-content opening path for WebDAV and background warming, without synthetic HTTP requests.</summary>
public sealed class DavContentStreamFactory(
    DavDatabaseClient database, UsenetStreamingClient usenet, ConfigManager config,
    LazyRarResolver lazyResolver, InFlightArticleBudget articleBudget)
{
    public Task<Stream> OpenAsync(DavItem item, CancellationToken cancellationToken) => item.SubType switch
    {
        DavItem.ItemSubType.NzbFile => OpenNzbAsync(item, database, usenet, config, articleBudget, cancellationToken),
        DavItem.ItemSubType.RarFile => OpenRarAsync(item, database, usenet, config, articleBudget, cancellationToken),
        DavItem.ItemSubType.MultipartFile => OpenMultipartAsync(item, database, usenet, config, lazyResolver, articleBudget, cancellationToken),
        _ => throw new ArgumentException("Only streamable media items can be opened.", nameof(item))
    };

    public static async Task<Stream> OpenNzbAsync(DavItem item, DavDatabaseClient database, INntpClient usenet,
        ConfigManager config, InFlightArticleBudget budget, CancellationToken cancellationToken)
    {
        var file = await database.GetDavNzbFileAsync(item, cancellationToken).ConfigureAwait(false)
            ?? throw new MissingFilePayloadException(item, DavItem.ItemSubType.NzbFile);
        var corrupt = config.IsCorruptionTrackingEnabled() && file.CorruptSegmentIndices is { Length: > 0 }
            ? file.CorruptSegmentIndices.Where(index => (uint)index < (uint)file.SegmentIds.Length)
                .Select(index => file.SegmentIds[index]).ToHashSet(StringComparer.Ordinal)
            : null;
        var missing = config.IsDegradedToleranceEnabled() && file.SegmentByteRanges?.Length == file.SegmentIds.Length
            && file.MissingSegmentIndices is { Length: > 0 }
            ? file.MissingSegmentIndices.Where(index => index > 0 && (uint)index < (uint)file.SegmentIds.Length).ToHashSet()
            : null;
        return usenet.GetFileStream(file.SegmentIds, item.FileSize!.Value, config.GetArticleBufferSize(),
            file.SegmentByteRanges, config.IsPipelinedBodyRequestsEnabled(), item.Path, file.SegmentFallbackIds, budget,
            useContainerAwareFill: config.IsContainerAwareFillEnabled(), streamingBodyBatchWidth: config.GetStreamingBodyBatchWidth(),
            knownCorruptSegmentIds: corrupt is { Count: > 0 } ? corrupt : null,
            knownMissingSegmentIndices: missing is { Count: > 0 } ? missing : null,
            segmentByteRangesTrusted: file.SegmentByteRangesTrusted == true, verificationProof: file.VerificationProof);
    }

    public static async Task<Stream> OpenRarAsync(DavItem item, DavDatabaseClient database, UsenetStreamingClient usenet,
        ConfigManager config, InFlightArticleBudget budget, CancellationToken cancellationToken)
    {
        var file = await database.GetDavRarFileAsync(item, cancellationToken).ConfigureAwait(false)
            ?? throw new MissingFilePayloadException(item, DavItem.ItemSubType.RarFile);
        return new DavMultipartFileStream(new DavMultipartFile { Id = file.Id, Metadata = file.ToDavMultipartFileMeta() },
            usenet, config.GetArticleBufferSize(), resolver: null, config.IsPipelinedBodyRequestsEnabled(), item.Path,
            budget, config.GetStreamingBodyBatchWidth());
    }

    public static async Task<Stream> OpenMultipartAsync(DavItem item, DavDatabaseClient database, UsenetStreamingClient usenet,
        ConfigManager config, LazyRarResolver resolver, InFlightArticleBudget budget, CancellationToken cancellationToken)
    {
        var file = await database.GetDavMultipartFileAsync(item, cancellationToken).ConfigureAwait(false)
            ?? throw new MissingFilePayloadException(item, DavItem.ItemSubType.MultipartFile);
        if (file.Metadata.AesParams is not null && file.Metadata.IsLazy && file.Metadata.PendingParts is { Length: > 0 })
            await resolver.EnsureResolvedThroughAsync(file, long.MaxValue, cancellationToken).ConfigureAwait(false);
        var packed = new DavMultipartFileStream(file, usenet, config.GetArticleBufferSize(), resolver,
            config.IsPipelinedBodyRequestsEnabled(), item.Path, budget, config.GetStreamingBodyBatchWidth());
        try { return file.Metadata.AesParams is { } aes ? new AesDecoderStream(packed, aes) : packed; }
        catch { await packed.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
