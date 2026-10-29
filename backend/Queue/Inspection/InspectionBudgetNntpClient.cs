using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Queue.Inspection;

/// <summary>Thrown when an inspection exhausts one of its <see cref="NzbInspectionLimits"/>.</summary>
public sealed class InspectionLimitExceededException(string limit)
    : Exception($"Inspection limit reached: {limit}.")
{
    public string Limit { get; } = limit;
}

/// <summary>
/// Charges every article request against the inspection budget before delegating. Sits
/// below the per-inspection article cache and above the shared provider client, so it
/// counts requests the inspection issues to that client (per segment for batched calls,
/// including requests that later fail). Provider failover and retries inside the client
/// are not counted separately, and some requests may be served from the client's caches.
/// Exhaustion is latched in <see cref="Exceeded"/> as well as thrown, because some
/// planning steps deliberately swallow failures (for example nested RAR expansion
/// keeps an archive opaque); the latch lets the caller refuse a partial manifest.
/// </summary>
internal sealed class InspectionBudgetNntpClient(
    INntpClient usenetClient,
    NzbInspectionLimits limits,
    IReadOnlyDictionary<string, long> declaredSegmentBytes) : WrappingNntpClient(usenetClient)
{
    // Budgeted for articles the NZB does not size (NZB segment sizes are declared encoded bytes).
    private const long UnknownArticleBytes = 768 * 1024;

    private int _articles;
    private long _bytes;
    private volatile string? _exceededLimit;

    public int ArticleRequests => Volatile.Read(ref _articles);
    public bool Exceeded => _exceededLimit is not null;
    public string? ExceededLimit => _exceededLimit;

    private void Charge(string segmentId)
    {
        if (_exceededLimit is { } already)
            throw new InspectionLimitExceededException(already);

        var articles = Interlocked.Increment(ref _articles);
        var bytes = Interlocked.Add(
            ref _bytes,
            declaredSegmentBytes.TryGetValue(segmentId, out var declared) && declared > 0
                ? declared
                : UnknownArticleBytes);
        var limit = articles > limits.MaxArticleRequests ? "article_requests"
            : bytes > limits.MaxDeclaredArticleBytes ? "declared_article_bytes"
            : null;
        if (limit is null) return;
        _exceededLimit = limit;
        throw new InspectionLimitExceededException(limit);
    }

    private void Charge(IEnumerable<string> segmentIds)
    {
        foreach (var segmentId in segmentIds)
            Charge(segmentId);
    }

    /// <summary>The wrapped client is the shared streaming client; inspection never disposes it.</summary>
#pragma warning disable CA2215 // base.Dispose() would dispose the shared singleton streaming client and every provider connection; this wrapper owns nothing.
    public override void Dispose() => GC.SuppressFinalize(this);
#pragma warning restore CA2215

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedBodyAsync(segmentId, cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        Charge(segmentIds.Select(x => x.ToString()));
        return base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken)
    {
        Charge(segmentIds.Select(x => x.ToString()));
        return base.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedArticleAsync(segmentId, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        Charge(segmentId.ToString());
        return base.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken);
    }

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        Charge(segmentId);
        return base.GetYencHeadersAsync(segmentId, cancellationToken);
    }

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
    {
        Charge(segmentIds);
        return base.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken);
    }

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
    {
        Charge(segmentIds);
        return base.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken);
    }
}
