using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Abstract base class for NNTP clients with default implementations of utility methods.
/// </summary>
public abstract class NntpClient : INntpClient
{
    // Match UsenetSharp's default MaxPipelineDepth: each concurrent call fills one
    // wire window without monopolizing a connection across multiple response waves.
    internal const int StatPipelinedDispatchBatchSize = 64;

    public virtual int PipeliningDepth => 0;
    public virtual bool ReadStartWarmupEnabled => false;

    public virtual Task PrewarmConnectionsAsync(
        int targetConnections,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops generation-scoped side effects when a wrapper replaces this client while
    /// allowing work already in flight to finish before disposal.
    /// </summary>
    internal virtual void Retire()
    {
    }

    public abstract Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    public abstract Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    public abstract Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken);

    public virtual Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        Task.FromResult<UsenetDecodedBodyResponse?>(null);

    public abstract Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    public abstract void Dispose();

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support acquiring exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        return AcquireExclusiveConnectionAsync(segmentIds[0], cancellationToken);
    }

    public virtual Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodyAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodiesAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedArticleAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        var decodedBodyResponse = await DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
        await using var stream = decodedBodyResponse.Stream!;
        var headers = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
        return headers ?? throw new NonRetryableDownloadException(
            $"Article <{segmentId}> is not yEnc-encoded; only yEnc binaries are supported.");
    }

    public virtual async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return 0;
        var headers = await GetYencHeadersAsync(file.Segments[^1].MessageId, ct).ConfigureAwait(false);
        file.Segments[^1].ByteRange = LongRange.FromStartAndSize(headers.PartOffset, headers.PartSize);
        return headers.PartOffset + headers.PartSize;
    }

    public virtual async Task<NzbFileStream> GetFileStream(
        NzbFile nzbFile,
        int articleBufferSize,
        CancellationToken ct,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        int streamingBodyBatchWidth = 4,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await GetFileSizeAsync(nzbFile, ct).ConfigureAwait(false);
        var rangeIndex = nzbFile.GetSegmentByteRangeIndex();
        return new NzbFileStream(
            segmentIds,
            fileSize,
            this,
            articleBufferSize,
            rangeIndex.Ranges,
            usePipelinedBodyRequests,
            ResolveFileName(fileName, nzbFile),
            nzbFile.GetSegmentFallbackIds(),
            inFlightArticleBudget,
            useContainerAwareFill,
            streamingBodyBatchWidth,
            knownCorruptSegmentIds,
            knownMissingSegmentIndices,
            rangeIndex.IsTrusted,
            readStartWarmupEnabled: ReadStartWarmupEnabled);
    }

    public virtual NzbFileStream GetFileStream(
        NzbFile nzbFile,
        long fileSize,
        int articleBufferSize,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        int streamingBodyBatchWidth = 4,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null)
    {
        var rangeIndex = nzbFile.GetSegmentByteRangeIndex();
        return new NzbFileStream(
            nzbFile.GetSegmentIds(),
            fileSize,
            this,
            articleBufferSize,
            rangeIndex.Ranges,
            usePipelinedBodyRequests,
            ResolveFileName(fileName, nzbFile),
            nzbFile.GetSegmentFallbackIds(),
            inFlightArticleBudget,
            useContainerAwareFill,
            streamingBodyBatchWidth,
            knownCorruptSegmentIds,
            knownMissingSegmentIndices,
            rangeIndex.IsTrusted,
            readStartWarmupEnabled: ReadStartWarmupEnabled
        );
    }

    public virtual NzbFileStream GetFileStream(
        string[] segmentIds,
        long fileSize,
        int articleBufferSize,
        LongRange[]? segmentByteRanges = null,
        bool usePipelinedBodyRequests = true,
        string? fileName = null,
        string[][]? segmentFallbacks = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        int streamingBodyBatchWidth = 4,
        HashSet<string>? knownCorruptSegmentIds = null,
        IReadOnlySet<int>? knownMissingSegmentIndices = null,
        bool segmentByteRangesTrusted = true,
        long? readBudgetOverride = null)
    {
        return new NzbFileStream(
            segmentIds,
            fileSize,
            this,
            articleBufferSize,
            segmentByteRanges,
            usePipelinedBodyRequests,
            fileName,
            segmentFallbacks,
            inFlightArticleBudget,
            useContainerAwareFill,
            streamingBodyBatchWidth,
            knownCorruptSegmentIds,
            knownMissingSegmentIndices,
            segmentByteRangesTrusted,
            readBudgetOverride,
            ReadStartWarmupEnabled);
    }

    private static string? ResolveFileName(string? fileName, NzbFile nzbFile)
    {
        if (!string.IsNullOrEmpty(fileName)) return fileName;
        var subjectName = nzbFile.GetSubjectFileName();
        return string.IsNullOrEmpty(subjectName) ? null : subjectName;
    }

    public virtual async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        using var childCt = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = childCt.Token;

        var tasks = segmentIds
            .Select(async segmentId => (
                SegmentId: segmentId,
                Result: await StatAsync(segmentId, token).ConfigureAwait(false)
            ))
            .WithConcurrencyAsync(concurrency, token);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            progress?.Report(++processed);
            if (task.Result.ResponseType == UsenetResponseType.ArticleExists) continue;
            await childCt.CancelAsync().ConfigureAwait(false);

            // Definitive missing (430 / provider 451) fails the health check; any other
            // response (e.g. a stale connection's goodbye line) must stay retryable.
            if (UsenetArticleAvailability.IsDefinitiveMissing(task.Result))
                throw new UsenetArticleNotFoundException(task.SegmentId, task.Result.ResponseMessage);
            throw new UsenetUnexpectedResponseException(task.SegmentId, task.Result.ResponseMessage);
        }
    }

    public virtual async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var segmentId in segmentIds)
        {
            var response = await StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            yield return new PipelinedStatResult
            {
                SegmentId = segmentId,
                Exists = response.ResponseType == UsenetResponseType.ArticleExists,
            };
        }
    }

    public virtual async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) yield break;

        depth = Math.Max(1, depth);

        // Download priority and the streaming deadline are keyed by token identity, so a plain
        // linked token silently drops both. Scoped to the enumeration because the reader
        // outlives a single batch iteration.
        using var batchCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        for (var batchStart = 0; batchStart < segmentIds.Count; batchStart += depth)
        {
            var batchSize = Math.Min(depth, segmentIds.Count - batchStart);
            var batchIds = new SegmentId[batchSize];
            for (var index = 0; index < batchSize; index++)
                batchIds[index] = segmentIds[batchStart + index];

            var batch = await DecodedBodiesAsync(
                batchIds, onConnectionReadyAgain: null, batchCts.Token).ConfigureAwait(false);
            var position = 0;
            var fullyEnumerated = false;
            try
            {
                for (; position < batch.Responses.Count; position++)
                {
                    var segmentId = segmentIds[batchStart + position];
                    yield return await MapPipelinedBodyResultAsync(
                        batch.Responses[position], segmentId, cancellationToken).ConfigureAwait(false);
                }

                fullyEnumerated = true;
            }
            finally
            {
                // A fully drained batch belongs to the consumer, and cancelling would tear
                // down streams it still holds.
                if (!fullyEnumerated && position < batch.Responses.Count)
                {
                    try
                    {
                        await batchCts.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException e)
                    {
                        // Releasing the bodies matters more than the cancellation succeeding.
                        Log.Debug(e, "Failed to cancel an abandoned pipelined BODY batch");
                    }

                    await ReleaseRemainingBodiesAsync(batch.Responses, position).ConfigureAwait(false);
                    await ObserveBatchCompletionAsync(batch.Completion).ConfigureAwait(false);
                }
                else
                {
                    // Fully enumerated responses still belong to the consumer. Transport
                    // Completion waits for the last stream's EOF/dispose, so joining here
                    // would hang the final MoveNextAsync until that stream is released.
                    _ = ObserveBatchCompletionAsync(batch.Completion);
                }
            }
        }
    }

    /// <summary>
    /// Releases an abandoned batch from the response the loop stopped on, which the consumer
    /// may or may not have received. The batch shares one connection that is returned only
    /// once every response is read. Cancel first, or awaiting a response drives the fetch the
    /// consumer walked away from.
    /// </summary>
    private static async Task ReleaseRemainingBodiesAsync
    (
        IReadOnlyList<Task<UsenetDecodedBodyResponse>> responses,
        int startIndex
    )
    {
        for (var index = startIndex; index < responses.Count; index++)
        {
            try
            {
                var response = await responses[index].ConfigureAwait(false);
                if (response.Stream != null)
                    await response.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the batch faults every response still in flight.
            }
            catch (UsenetArticleNotFoundException)
            {
                // Expected: a definitive miss is the usual reason a consumer stops early.
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                Log.Debug(e, "Failed to release abandoned pipelined BODY response");
            }
        }
    }

    private static async Task ObserveBatchCompletionAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Debug(exception, "Pipelined BODY batch completion failed");
        }
    }

    public virtual async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var body in DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new PipelinedArticleResult
            {
                SegmentId = body.SegmentId,
                Found = body.Found,
                Stream = body.Stream,
                ArticleHeaders = null,
                DefinitivelyMissing = body.DefinitivelyMissing,
            };
        }
    }

    private static async Task<PipelinedBodyResult> MapPipelinedBodyResultAsync
    (
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (UsenetArticleAvailability.IsDefinitiveMissing(response))
            {
                return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = false,
                    Stream = null,
                    DefinitivelyMissing = true,
                };
            }

            if (response.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                throw new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage);
            }

            if (HasSegmentIdMismatch(segmentId, response.SegmentId, response.ResponseMessage, out var actualId))
            {
                Log.Warning(
                    "Pipelined BODY SegmentId mismatch: expected {Expected}, got {Actual} (message: {Message}). " +
                    "Treating as not found so queue rescue can refetch.",
                    NormalizeSegmentId(segmentId), actualId, response.ResponseMessage);
                if (response.Stream != null)
                {
                    try { await response.Stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception e) when (e is not OutOfMemoryException) { Log.Debug(e, "Failed to dispose mismatched pipelined BODY stream"); }
                }

                return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = false,
                    Stream = null,
                };
            }

            return new PipelinedBodyResult
            {
                SegmentId = segmentId,
                Found = true,
                Stream = response.Stream,
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return new PipelinedBodyResult
            {
                SegmentId = segmentId,
                Found = false,
                Stream = null,
                DefinitivelyMissing = true,
            };
        }
    }

    /// <summary>
    /// Returns true when the response carries a different message-id than requested.
    /// Prefers <see cref="UsenetDecodedBodyResponse.SegmentId"/> when set; also checks
    /// a <c>&lt;mid&gt;</c> echoed in <c>ResponseMessage</c> (typical 222 form). Cache /
    /// synthetic responses without a wire id are treated as matching.
    /// </summary>
    internal static bool HasSegmentIdMismatch(
        string requestedSegmentId,
        string? responseSegmentId,
        string? responseMessage,
        out string actualId)
    {
        var expected = NormalizeSegmentId(requestedSegmentId);
        actualId = "";

        var responseId = NormalizeSegmentId(responseSegmentId);
        if (responseId.Length > 0 &&
            !string.Equals(responseId, expected, StringComparison.OrdinalIgnoreCase))
        {
            actualId = responseId;
            return true;
        }

        if (TryExtractMessageIdFromResponseMessage(responseMessage, out var wireId) &&
            !string.Equals(NormalizeSegmentId(wireId), expected, StringComparison.OrdinalIgnoreCase))
        {
            actualId = NormalizeSegmentId(wireId);
            return true;
        }

        return false;
    }

    internal static string NormalizeSegmentId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        // Strip the angle brackets independently: an upstream SegmentId conversion
        // may have already removed one bracket while whitespace shielded the other
        // (e.g. "<id@host>\n" arrives here as "id@host>\n").
        var trimmed = id.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '<')
            trimmed = trimmed[1..];
        if (trimmed.Length > 0 && trimmed[^1] == '>')
            trimmed = trimmed[..^1];
        return trimmed.Trim();
    }

    /// <summary>
    /// Mirrors UsenetSharp's message-id shape rules so invalid ids fail as
    /// <see cref="UsenetArticleNotFoundException"/> instead of ArgumentException.
    /// </summary>
    internal static bool IsValidSegmentId(string? id)
    {
        var value = NormalizeSegmentId(id);
        if (value.Length is < 3 or > 248) return false;
        if (value[0] == '@' || value[^1] == '@' || !value.Contains('@', StringComparison.Ordinal)) return false;
        if (value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal)) return false;
        foreach (var character in value.Where(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Normalize and validate a segment id before handing it to UsenetSharp.
    /// Permanently-invalid ids throw <see cref="UsenetArticleNotFoundException"/>.
    /// </summary>
    internal static SegmentId PrepareSegmentId(SegmentId segmentId)
    {
        var normalized = NormalizeSegmentId(segmentId);
        if (!IsValidSegmentId(normalized))
            throw new UsenetArticleNotFoundException(normalized);

        return normalized;
    }

    internal static bool TryExtractMessageIdFromResponseMessage(string? message, out string messageId)
    {
        messageId = "";
        if (string.IsNullOrEmpty(message)) return false;
        var start = message.IndexOf('<', StringComparison.Ordinal);
        if (start < 0) return false;
        var end = message.IndexOf('>', start + 1);
        if (end < 0) return false;
        messageId = message[(start + 1)..end];
        return messageId.Length > 0;
    }

    public virtual async Task CheckAllSegmentsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return;

        var processed = 0;
        PipelinedStatSweep sweep;
        try
        {
            sweep = await SweepPipelinedStatsAsync(
                    segmentIds, depth, fallbackConcurrency, progress,
                    count => processed = count, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            // Session/protocol/transport failure during the primary-only sweep (e.g.
            // UsenetUnexpectedResponseException for a buffered 400 goodbye, or
            // UsenetSharp UsenetProtocolException when the connection dies mid-batch):
            // fall back to the concurrent path rather than failing the check.
            Log.Debug(
                e,
                "Pipelined STAT sweep failed after {Processed}/{Total} segments; falling back to concurrent STAT",
                processed,
                segmentIds.Count);
            // Use a synchronous adapter — Progress<T> posts asynchronously and can
            // reorder or race progress observers (and tests collecting reports).
            var fallbackProgress = progress == null
                ? null
                : new MappedProgress(progress, n => Math.Max(processed, n));
            await CheckAllSegmentsAsync(segmentIds, fallbackConcurrency, fallbackProgress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (sweep.Missing.Count == 0) return;

        // Recheck only the misses so backups can still satisfy those articles.
        // Continue progress from the already-reported pipelined count.
        var recheckProgress = progress == null
            ? null
            : new MappedProgress(progress, n => sweep.Processed + n);
        await CheckAllSegmentsAsync(sweep.Missing, fallbackConcurrency, recheckProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    public virtual async Task<IReadOnlyList<string>> CollectMissingSegmentsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return [];

        var processed = 0;
        PipelinedStatSweep sweep;
        try
        {
            sweep = await SweepPipelinedStatsAsync(
                    segmentIds, depth, fallbackConcurrency, progress,
                    count => processed = count, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(
                e,
                "Pipelined STAT sweep failed; collecting with concurrent STAT fallback for {Total} segments",
                segmentIds.Count);
            var fallbackProgress = progress == null
                ? null
                : new MappedProgress(progress, n => Math.Max(processed, n));
            return await CollectMissingSegmentsAsync(
                    segmentIds, fallbackConcurrency, fallbackProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (sweep.Missing.Count == 0) return [];

        var recheckProgress = progress == null
            ? null
            : new MappedProgress(progress, n => sweep.Processed + n);
        return await CollectMissingSegmentsAsync(
                sweep.Missing, fallbackConcurrency, recheckProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PipelinedStatSweep> SweepPipelinedStatsAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int concurrency,
        IProgress<int>? progress,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        var missing = new List<string>();
        var batchCount = (segmentIds.Count + StatPipelinedDispatchBatchSize - 1)
                         / StatPipelinedDispatchBatchSize;
        var waveSize = Math.Min(Math.Max(1, concurrency), batchCount);

        for (var waveStart = 0; waveStart < batchCount; waveStart += waveSize)
        {
            var waveCount = Math.Min(waveSize, batchCount - waveStart);
            using var waveCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readers = new ChannelReader<PipelinedStatResult>[waveCount];
            var producers = new Task[waveCount];
            for (var waveIndex = 0; waveIndex < waveCount; waveIndex++)
            {
                var batchStart = (waveStart + waveIndex) * StatPipelinedDispatchBatchSize;
                var batchSize = Math.Min(
                    StatPipelinedDispatchBatchSize,
                    segmentIds.Count - batchStart);
                var batchIds = new string[batchSize];
                for (var index = 0; index < batchSize; index++)
                    batchIds[index] = segmentIds[batchStart + index];

                var channel = Channel.CreateUnbounded<PipelinedStatResult>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true,
                    });
                readers[waveIndex] = channel.Reader;
                producers[waveIndex] = ProducePipelinedStatBatchAsync(
                    batchIds, depth, channel.Writer, waveCts.Token);
            }

            try
            {
                foreach (var reader in readers)
                {
                    await foreach (var result in reader.ReadAllAsync(waveCts.Token)
                                       .ConfigureAwait(false))
                    {
                        progress?.Report(++processed);
                        onProgress?.Invoke(processed);
                        if (!result.Exists) missing.Add(result.SegmentId);
                    }
                }
            }
            finally
            {
                await waveCts.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(producers).ConfigureAwait(false);
            }
        }

        return new PipelinedStatSweep(processed, missing);
    }

    private async Task ProducePipelinedStatBatchAsync(
        string[] segmentIds,
        int depth,
        ChannelWriter<PipelinedStatResult> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var result in StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
                await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failure = e;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task<IReadOnlyList<string>> CollectMissingSegmentsAsync(
        IReadOnlyList<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var tasks = segmentIds
            .Select(async (segmentId, index) => (
                Index: index,
                SegmentId: segmentId,
                Result: await StatAsync(segmentId, cancellationToken).ConfigureAwait(false)))
            .WithConcurrencyAsync(concurrency, cancellationToken);

        var processed = 0;
        var missing = new List<(int Index, string SegmentId)>();
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            progress?.Report(++processed);
            if (task.Result.ResponseType == UsenetResponseType.ArticleExists) continue;
            if (UsenetArticleAvailability.IsDefinitiveMissing(task.Result))
            {
                missing.Add((task.Index, task.SegmentId));
                continue;
            }

            throw new UsenetUnexpectedResponseException(task.SegmentId, task.Result.ResponseMessage);
        }

        return missing.OrderBy(result => result.Index).Select(result => result.SegmentId).ToArray();
    }

    private sealed record PipelinedStatSweep(int Processed, List<string> Missing);

    private sealed class MappedProgress(IProgress<int> target, Func<int, int> map) : IProgress<int>
    {
        public void Report(int value) => target.Report(map(value));
    }
}
