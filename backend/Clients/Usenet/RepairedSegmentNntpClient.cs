using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Services.Repair;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public sealed class RepairedSegmentNntpClient : WrappingNntpClient
{
    private readonly RepairPatchStore _patchStore;

    public RepairedSegmentNntpClient(INntpClient inner, RepairPatchStore patchStore) : base(inner)
    {
        _patchStore = patchStore;
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, ct);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);

        if (TryGetPatchedResponse(segmentId, out var patched))
        {
            ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, ArticleBodyResult.Retrieved);
            PrometheusMetrics.Current?.RecordPar2PatchHit();
            return patched!;
        }

        return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId segmentId, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.TryGetLocalDecodedBodyAsync(segmentId, ct).ConfigureAwait(false);

        if (TryGetPatchedResponse(segmentId, out var patched))
        {
            PrometheusMetrics.Current?.RecordPar2PatchHit();
            return patched;
        }

        return await base.TryGetLocalDecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MultiProviderNntpClient.AttributionContext.Value is not null || !_patchStore.HasUsablePatch(segmentId))
            return base.StatAsync(segmentId, cancellationToken);
        return Task.FromResult(new UsenetStatResponse
        {
            ResponseCode = (int)UsenetResponseType.ArticleExists,
            ResponseMessage = $"223 0 <{segmentId}> (repair patch store)",
            ArticleExists = true,
        });
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (MultiProviderNntpClient.AttributionContext.Value is not null)
        {
            await foreach (var result in base.StatsPipelinedAsync(segmentIds, depth, cancellationToken).ConfigureAwait(false))
                yield return result;
            yield break;
        }

        var local = new bool[segmentIds.Count];
        var misses = new List<string>();
        for (var index = 0; index < segmentIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            local[index] = _patchStore.HasUsablePatch(segmentIds[index]);
            if (!local[index]) misses.Add(segmentIds[index]);
        }

        await using var remote = misses.Count == 0 ? null
            : base.StatsPipelinedAsync(misses, depth, cancellationToken).GetAsyncEnumerator(cancellationToken);
        for (var index = 0; index < segmentIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (local[index])
            {
                yield return new PipelinedStatResult { SegmentId = segmentIds[index], Exists = true };
                continue;
            }
            if (remote is null || !await remote.MoveNextAsync().ConfigureAwait(false)
                || remote.Current.SegmentId != segmentIds[index])
                throw new UsenetUnexpectedResponseException(segmentIds[index], "Unordered or incomplete STAT pipeline response.");
            yield return remote.Current;
        }
        if (remote is not null && await remote.MoveNextAsync().ConfigureAwait(false))
            throw new UsenetUnexpectedResponseException(remote.Current.SegmentId, "Extra STAT pipeline response.");
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);

        if (TryGetPatchedResponse(segmentId, out var patched))
        {
            ArticleBodyCompletion.InvokeContained(
                exclusiveConnection.OnConnectionReadyAgain, ArticleBodyResult.Retrieved);
            PrometheusMetrics.Current?.RecordPar2PatchHit();
            return patched!;
        }

        return await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        if (MultiProviderNntpClient.AttributionContext.Value is not null)
            return base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);

        return LocalDataBatchOverlay.ExecuteAsync(
            segmentIds,
            onConnectionReadyAgain,
            TryOpenPatchedResponse,
            (misses, callback, token) => base.DecodedBodiesAsync(misses, callback, token),
            LocalDataBatchOverlay.PassThroughRemote,
            cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken)
    {
        if (MultiProviderNntpClient.AttributionContext.Value is not null)
            return base.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);

        return LocalDataBatchOverlay.ExecuteAsync(
            segmentIds,
            exclusiveConnection.OnConnectionReadyAgain,
            TryOpenPatchedResponse,
            (misses, callback, token) =>
                base.DecodedBodiesAsync(misses, new UsenetExclusiveConnection(callback), token),
            LocalDataBatchOverlay.PassThroughRemote,
            cancellationToken);
    }

    private LocalLookupResult TryOpenPatchedResponse(SegmentId segmentId)
    {
        if (!TryGetPatchedResponse(segmentId, out var patched) || patched is null)
            return LocalLookupResult.Miss;

        PrometheusMetrics.Current?.RecordPar2PatchHit();
        return LocalLookupResult.Hit(patched);
    }

    private bool TryGetPatchedResponse(SegmentId segmentId, out UsenetDecodedBodyResponse? response)
    {
        string id = segmentId;
        return _patchStore.TryGet(id, out response);
    }
}
