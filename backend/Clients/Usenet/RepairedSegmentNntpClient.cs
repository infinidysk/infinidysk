using NzbWebDAV.Clients.Usenet.Models;
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

        string id = segmentId;
        if (_patchStore.TryGet(id, out var patched))
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            PrometheusMetrics.Current?.RecordPar2PatchHit();
            return patched!;
        }

        return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value == null
            && _patchStore.Contains(segmentId))
            return new UsenetExclusiveConnection(onConnectionReadyAgain: null);
        return await base.AcquireExclusiveConnectionAsync(segmentId, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);

        string id = segmentId;
        if (_patchStore.TryGet(id, out var patched))
        {
            exclusiveConnection.OnConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            PrometheusMetrics.Current?.RecordPar2PatchHit();
            return patched!;
        }

        return await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);
    }
}
