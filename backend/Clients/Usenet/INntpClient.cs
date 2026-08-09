using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public interface INntpClient : IDisposable
{
    // core methods
    Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken);

    Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken);

    Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    // optimized for concurrency
    Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken);

    Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection connection, CancellationToken cancellationToken);

    Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, UsenetExclusiveConnection connection,
        CancellationToken cancellationToken);

    Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection connection, CancellationToken cancellationToken);

    // pipelined batch fetch
    IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);

    IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);

    IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);

    // helpers
    Task<UsenetYencHeader> GetYencHeadersAsync(
        string segmentId, CancellationToken ct);

    Task<long> GetFileSizeAsync(
        NzbFile file, CancellationToken ct);

    Task<NzbFileStream> GetFileStream(
        NzbFile nzbFile,
        int articleBufferSize,
        CancellationToken ct,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false);

    NzbFileStream GetFileStream(
        NzbFile nzbFile,
        long fileSize,
        int articleBufferSize,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false);

    NzbFileStream GetFileStream(
        string[] segmentIds,
        long fileSize,
        int articleBufferSize,
        LongRange[]? segmentByteRanges = null,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        string[][]? segmentFallbacks = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false);

    Task CheckAllSegmentsAsync(
        IEnumerable<string> segmentIds, int concurrency, IProgress<int>? progress, CancellationToken cancellationToken);

    Task CheckAllSegmentsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, int fallbackConcurrency, IProgress<int>? progress,
        CancellationToken cancellationToken);

    // pipelining config (0 = disabled). Resolved from ConfigManager at the downloading layer.
    int PipeliningDepth { get; }
}
