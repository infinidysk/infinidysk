using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int BodyPipelineBatchSize = 4;
    private const int MaxBodyRetries = 2;
    private const int MaxCorruptionRetries = 3;
    private const int MaxConsecutiveZeroFills = 3;

    private readonly Memory<string> _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly INntpClient _usenetClient;
    private readonly long _estimatedSegmentSize;
    private readonly SegmentSizes _segmentSizes;
    private readonly bool _failFastOnFirstSegment;
    private readonly bool _useContainerAwareFill;
    private readonly long? _firstSegmentFileOffset;
    private readonly string _fileName;
    private readonly Channel<Task<SegmentDownloadResult>> _streamTasks;
    private readonly int _bodyPipelineBatchSize;
    private readonly AdaptiveBodyBatchSizer? _batchSizer;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly long? _readBudget;
    private readonly long _prefetchByteCeiling;
    private readonly int _taskWindowSize;
    private readonly InFlightArticleBudget? _budget;
    private long _inFlightPrefetchBytes;
    private TaskCompletionSource _prefetchSpace =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stream? _stream;
    private int _consecutiveZeroFills;
    private int _deliveredSegments;
    private bool _disposed;
    private readonly Task _downloadTask;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    // Segment tasks whose channel write was cancelled are disposed out-of-band by
    // the producer; DisposeCoreAsync must join them so DisposeAsync does not return
    // while their BudgetedStream leases are still held (#840 scrub wedge).
    private readonly ConcurrentQueue<Task> _orphanedDisposals = new();

    /// <summary>
    /// Optional per-instance test hook invoked with the segment-boundary readiness sample,
    /// after it is taken and before the segment task is awaited. Production code never sets
    /// this; instance scope keeps parallel stream tests from observing each other's streams.
    /// </summary>
    internal Action<bool>? TestOnSegmentReadiness;

    /// <summary>Current adaptive BODY batch width (or the fixed pipeline size when not adaptive).</summary>
    internal int PrefetchBatchWidth => _batchSizer?.Current ?? _bodyPipelineBatchSize;

    public static Stream Create(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize)
    {
        return Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            estimatedSegmentSize: 0,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            segmentFallbacks,
            inFlightArticleBudget: inFlightArticleBudget,
            useContainerAwareFill: useContainerAwareFill,
            firstSegmentFileOffset: firstSegmentFileOffset,
            bodyPipelineBatchWidth: bodyPipelineBatchWidth);
    }

    /// <param name="estimatedSegmentSize">
    /// Approximate decoded size per segment, used only for buffer capacity hints and
    /// prefetch budgeting. It must never determine how many bytes this stream emits —
    /// an estimate that is off by even one byte shifts every following byte in the file.
    /// </param>
    /// <param name="exactSegmentSizes">
    /// Exact decoded size of each segment in <paramref name="segmentIds"/>, in the same
    /// order. Supplied when the import recorded per-segment byte ranges, and required
    /// before a failed segment may be replaced with same-length gap bytes.
    /// </param>
    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(
                segmentIds, usenetClient, estimatedSegmentSize, fileName, segmentFallbacks,
                exactSegmentSizes, useContainerAwareFill, firstSegmentFileOffset,
                failFastOnFirstSegment)
            : new MultiSegmentStream(
                segmentIds,
                usenetClient,
                articleBufferSize,
                estimatedSegmentSize,
                failFastOnFirstSegment,
                usePipelinedBodyRequests,
                fileName,
                readBudget,
                segmentFallbacks,
                exactSegmentSizes,
                inFlightArticleBudget,
                useContainerAwareFill,
                firstSegmentFileOffset,
                bodyPipelineBatchWidth,
                cancellationToken);
    }

    /// <summary>
    /// Starts the first segment directly from its decoded BODY, then lazily creates the
    /// normal buffered pipeline at the segment boundary. This lets a player receive its
    /// first bytes without waiting for a whole decoded segment to drain, while retaining
    /// the established prefetch, retry, fill, and lifecycle behavior for sustained reads.
    /// </summary>
    public static Stream CreateFirstSegmentHybrid(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null,
        ReadOnlyMemory<long> exactSegmentSizes = default,
        InFlightArticleBudget? inFlightArticleBudget = null,
        bool useContainerAwareFill = false,
        long? firstSegmentFileOffset = null,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize)
    {
        if (articleBufferSize == 0 || segmentIds.Length <= 1)
        {
            return Create(
                segmentIds, usenetClient, articleBufferSize, estimatedSegmentSize,
                failFastOnFirstSegment, usePipelinedBodyRequests, cancellationToken, fileName,
                readBudget, segmentFallbacks, exactSegmentSizes, inFlightArticleBudget,
                useContainerAwareFill, firstSegmentFileOffset, bodyPipelineBatchWidth);
        }

        var effectiveReadBudget = readBudget ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        var firstExactSizes = exactSegmentSizes.Length == segmentIds.Length
            ? exactSegmentSizes[..1]
            : default;
        var remainingExactSizes = exactSegmentSizes.Length == segmentIds.Length
            ? exactSegmentSizes[1..]
            : default;
        var firstFallbacks = segmentFallbacks is { Length: > 0 } ? segmentFallbacks[..1] : null;
        var remainingFallbacks = segmentFallbacks is { Length: > 1 } ? segmentFallbacks[1..] : null;
        var remainingOffset = firstSegmentFileOffset;
        if (remainingOffset is not null && firstExactSizes.Length == 1)
        {
            try { remainingOffset = checked(remainingOffset.Value + firstExactSizes.Span[0]); }
            catch (OverflowException) { remainingOffset = null; }
        }

        var remainingBudget = effectiveReadBudget;
        if (remainingBudget is not null && firstExactSizes.Length == 1)
            remainingBudget = Math.Max(0, remainingBudget.Value - firstExactSizes.Span[0]);

        return new CombinedStream(CreateFirstSegmentThenBufferedRest());

        IEnumerable<Task<Stream>> CreateFirstSegmentThenBufferedRest()
        {
#pragma warning disable CA2000 // ownership transfers to CombinedStream when the yielded task becomes current
            yield return Task.FromResult<Stream>(new UnbufferedMultiSegmentStream(
                segmentIds[..1], usenetClient, estimatedSegmentSize, fileName, firstFallbacks,
                firstExactSizes, useContainerAwareFill, firstSegmentFileOffset,
                failFastOnFirstSegment));
#pragma warning restore CA2000
            yield return Task.FromResult(Create(
                segmentIds[1..], usenetClient, articleBufferSize, estimatedSegmentSize,
                failFastOnFirstSegment: false, usePipelinedBodyRequests, cancellationToken,
                fileName, remainingBudget, remainingFallbacks, remainingExactSizes,
                inFlightArticleBudget, useContainerAwareFill, remainingOffset,
                bodyPipelineBatchWidth));
        }
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long estimatedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        string? fileName,
        long? readBudget,
        string[][]? segmentFallbacks,
        ReadOnlyMemory<long> exactSegmentSizes,
        InFlightArticleBudget? inFlightArticleBudget,
        bool useContainerAwareFill,
        long? firstSegmentFileOffset,
        int bodyPipelineBatchWidth,
        CancellationToken cancellationToken
    )
    {
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _estimatedSegmentSize = estimatedSegmentSize;
        _segmentSizes = new SegmentSizes(exactSegmentSizes, segmentIds.Length);
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _useContainerAwareFill = useContainerAwareFill;
        _firstSegmentFileOffset = firstSegmentFileOffset;
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
        _readBudget = readBudget ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        _budget = inFlightArticleBudget ?? InFlightArticleBudget.Current;
        _bodyPipelineBatchSize = Math.Min(Math.Max(1, bodyPipelineBatchWidth), articleBufferSize);
        _taskWindowSize = CalculateTaskWindowSize(
            articleBufferSize, usePipelinedBodyRequests, _bodyPipelineBatchSize);
        _prefetchByteCeiling = _taskWindowSize > 0 && estimatedSegmentSize > 0
            ? (long)_taskWindowSize * estimatedSegmentSize
            : 0;
        _batchSizer = usePipelinedBodyRequests
            ? new AdaptiveBodyBatchSizer(_bodyPipelineBatchSize)
            : null;
        if (_batchSizer is not null && _bodyPipelineBatchSize != BodyPipelineBatchSize)
        {
            Log.Debug(
                "Streaming BODY batch width for {FileName} configured at {BatchWidth} (stock {StockWidth}).",
                _fileName,
                _bodyPipelineBatchSize,
                BodyPipelineBatchSize);
        }
        _streamTasks = Channel.CreateBounded<Task<SegmentDownloadResult>>(_taskWindowSize);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadTask = DownloadSegments(usePipelinedBodyRequests, _cts.Token);
    }

    /// <summary>
    /// Computes the number of ordered segment tasks that may wait ahead of the consumer.
    /// Pipelined BODY requests retain one connection per batch, not per segment, so a
    /// segment-only window of <paramref name="articleBufferSize"/> could use only a quarter
    /// of the per-stream connection budget at the normal four-article batch width. Expand
    /// the task window by that width; the connection semaphore remains the concurrency
    /// authority and <see cref="InFlightArticleBudget"/> remains the decoded-byte authority.
    /// </summary>
    internal static int CalculateTaskWindowSize(
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        int bodyPipelineBatchWidth = BodyPipelineBatchSize)
    {
        if (articleBufferSize <= 0) return 0;
        if (!usePipelinedBodyRequests) return articleBufferSize;

        var initialBatchWidth = Math.Min(bodyPipelineBatchWidth, articleBufferSize);
        return articleBufferSize > int.MaxValue / initialBatchWidth
            ? int.MaxValue
            : articleBufferSize * initialBatchWidth;
    }

    private async Task DownloadSegments(
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            if (usePipelinedBodyRequests)
                await DownloadPipelinedSegments(cancellationToken).ConfigureAwait(false);
            else
                await DownloadIndividualSegments(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _streamTasks.Writer.TryComplete();
        }
        catch (OutOfMemoryException oom)
        {
            OomDiagnostics.LogHeapStateOnOom(oom, "segment download pipeline");
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _streamTasks.Writer.TryComplete(exception);
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }

        return;
    }

    private async Task DownloadPipelinedSegments(CancellationToken cancellationToken)
    {
        var segmentsEnqueued = 0;
        var enqueuedBytes = 0L;
        for (var batchStart = 0; batchStart < _segmentIds.Length;)
        {
            if (ShouldStopPrefetch(segmentsEnqueued, enqueuedBytes))
                break;

            await WaitForPrefetchCeilingAsync(cancellationToken).ConfigureAwait(false);

            // Adaptive width: narrower batches → more outstanding connections at the
            // same article-buffer memory cost when the consumer is starving.
            var batchWidth = _batchSizer?.Current ?? _bodyPipelineBatchSize;
            var batchCount = Math.Min(batchWidth, _segmentIds.Length - batchStart);
            var segmentIds = new SegmentId[batchCount];
            for (var index = 0; index < batchCount; index++)
            {
                segmentIds[index] = _segmentIds.Span[batchStart + index];
            }

            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
            var leases = new ArticleByteLease?[batchCount];
            Task<SegmentDownloadResult>[]? streamTasks = null;
            try
            {
                // Reserve decoded-memory capacity before the request takes a streaming
                // permit. A saturated budget must never occupy all download slots with
                // requests that cannot yet be drained.
                for (var index = 0; index < leases.Length; index++)
                {
                    leases[index] = await LeaseSegmentBytesAsync(
                        GetPlannedSegmentBytes(batchStart + index), cancellationToken).ConfigureAwait(false);
                }

                // Use the normal client path so cache hits still bypass admission.
                var batch = await _usenetClient.DecodedBodiesAsync(
                    segmentIds, onConnectionReadyAgain: null, cancellationToken).ConfigureAwait(false);
                if (batch.Responses.Count != batchCount)
                    throw new InvalidOperationException(
                        $"Pipelined BODY returned {batch.Responses.Count} responses for {batchCount} requests.");

                streamTasks = batch.Responses
                    .Select((response, index) =>
                    {
                        var lease = leases[index]!;
                        leases[index] = null;
                        return DownloadBatchSegment(
                            response,
                            segmentIds[index],
                            segmentIndex: batchStart + index,
                            isFirstSegment: batchStart + index == 0,
                            lease,
                            cancellationToken);
                    })
                    .ToArray();
            }
            catch
            {
                foreach (var lease in leases)
                    lease?.Dispose();
                throw;
            }

            var responseIndex = 0;
            try
            {
                for (; responseIndex < streamTasks!.Length; responseIndex++)
                {
                    var planned = GetPlannedSegmentBytes(batchStart + responseIndex);
                    await _streamTasks.Writer.WriteAsync(
                        streamTasks[responseIndex], cancellationToken).ConfigureAwait(false);
                    segmentsEnqueued++;
                    enqueuedBytes += planned;
                    Interlocked.Add(ref _inFlightPrefetchBytes, planned);
                }
            }
            catch
            {
                for (; responseIndex < streamTasks!.Length; responseIndex++)
                {
                    _orphanedDisposals.Enqueue(DisposeStreamAsync(streamTasks[responseIndex]));
                }

                throw;
            }

            batchStart += batchCount;
        }
    }

    private async Task DownloadIndividualSegments(CancellationToken cancellationToken)
    {
        var enqueuedBytes = 0L;
        for (var index = 0; index < _segmentIds.Length; index++)
        {
            if (ShouldStopPrefetch(index, enqueuedBytes))
                break;

            await WaitForPrefetchCeilingAsync(cancellationToken).ConfigureAwait(false);

            var segmentId = _segmentIds.Span[index];
            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
            var lease = await LeaseSegmentBytesAsync(
                GetPlannedSegmentBytes(index), cancellationToken).ConfigureAwait(false);
            var streamTask = DownloadSegment(
                segmentId, index, lease, isFirstSegment: index == 0, cancellationToken);
            var planned = GetPlannedSegmentBytes(index);
            try
            {
                await _streamTasks.Writer.WriteAsync(streamTask, cancellationToken).ConfigureAwait(false);
                enqueuedBytes += planned;
                Interlocked.Add(ref _inFlightPrefetchBytes, planned);
            }
            catch
            {
                _orphanedDisposals.Enqueue(DisposeStreamAsync(streamTask));
                throw;
            }
        }
    }

    /// <summary>
    /// Stop enqueueing once the bytes already in flight cover the read budget plus one
    /// segment of slack, which absorbs the prefix a seek discards from the first segment.
    /// Requires recorded per-segment sizes; without them the estimate can undershoot actual
    /// segment lengths and truncate a range response, so prefetch is capped only by
    /// <see cref="WaitForPrefetchCeilingAsync"/>.
    /// </summary>
    private bool ShouldStopPrefetch(int segmentsEnqueued, long enqueuedBytes)
    {
        // Range read budget: permanent stop once enough of the file is planned.
        if (_readBudget is not null)
        {
            if (_segmentSizes.TryGetExactSize(0, out var slack))
                return enqueuedBytes >= _readBudget.Value + slack;
            return false;
        }

        // Full-file / non-range: never permanently stop (consumer needs the whole file).
        // Per-stream byte ceiling is enforced by WaitForPrefetchCeilingAsync instead.
        return false;
    }

    /// <summary>
    /// When <see cref="_readBudget"/> is null, pause the producer once in-flight planned
    /// bytes reach task-window-size × estimated segment size so full-file GETs cannot retain
    /// unbounded decoded bytes ahead of the consumer. For pipelined BODY requests the task
    /// window accounts for every segment needed to keep the connection budget occupied.
    /// </summary>
    private async Task WaitForPrefetchCeilingAsync(CancellationToken cancellationToken)
    {
        if (_prefetchByteCeiling <= 0) return;

        while (Interlocked.Read(ref _inFlightPrefetchBytes) >= _prefetchByteCeiling)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = Volatile.Read(ref _prefetchSpace);
            if (Interlocked.Read(ref _inFlightPrefetchBytes) < _prefetchByteCeiling)
                return;
            await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReleaseInFlightPrefetchBytes(long plannedBytes)
    {
        if (plannedBytes <= 0) return;
        Interlocked.Add(ref _inFlightPrefetchBytes, -plannedBytes);
        var prior = Interlocked.Exchange(
            ref _prefetchSpace,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prior.TrySetResult();
    }

    private long GetPlannedSegmentBytes(int segmentIndex) =>
        _segmentSizes.TryGetExactSize(segmentIndex, out var exact)
            ? exact
            : Math.Max(0, _estimatedSegmentSize);

    private async Task<SegmentDownloadResult> DownloadSegment(
        string segmentId,
        int segmentIndex,
        ArticleByteLease initialLease,
        bool isFirstSegment,
        CancellationToken cancellationToken
    )
    {
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        for (var attempt = 0; ; attempt++)
        {
            var lease = attempt == 0
                ? initialLease
                : await LeaseSegmentBytesAsync(estimate, cancellationToken).ConfigureAwait(false);
            try
            {
                var bodyResponse = await _usenetClient
                    .DecodedBodyAsync(segmentId, cancellationToken)
                    .ConfigureAwait(false);

                await ThrowOnSegmentIdMismatchAsync(segmentId, bodyResponse).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                var stream = await DrainSegmentAsync(
#pragma warning restore CA2000
                        bodyResponse.Stream!, segmentIndex, cancellationToken, lease, estimate)
                    .ConfigureAwait(false);
                lease = null;
                return SegmentDownloadResult.Success(stream, estimate);
            }
            catch (UsenetArticleNotFoundException e)
            {
                lease?.Dispose();
                lease = null;
                var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                    .ConfigureAwait(false);
                if (fallback is not null)
                    return SegmentDownloadResult.Success(fallback, estimate);

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    e.LogWarningKnownOrStack(
                        "First article {SegmentId} missing on all providers at playback start while reading {FileName}. " +
                        "Failing the stream so the player surfaces an error.",
                        segmentId, _fileName);
                    throw;
                }

                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                    e.SegmentId,
                    segmentIndex,
                    e);
            }
            catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
            {
                lease?.Dispose();
                lease = null;
                if (attempt >= MaxCorruptionRetries)
                {
                    var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                        .ConfigureAwait(false);
                    if (fallback is not null)
                        return SegmentDownloadResult.Success(fallback, estimate);

                    if (_failFastOnFirstSegment && isFirstSegment)
                    {
                        e.LogWarningKnownOrStack(
                            "First article {SegmentId} persistently corrupt at playback start while reading {FileName}. " +
                            "Failing the stream so the player surfaces an error.",
                            segmentId, _fileName);
                        throw;
                    }

                    return ZeroFillSegment(
                        "Article {SegmentId} persistently corrupt while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                        segmentId,
                        segmentIndex,
                        e);
                }

                Log.Debug(
                    e,
                    "Corrupt segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                    segmentId,
                    e.ProviderKey,
                    attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OutOfMemoryException oom)
            {
                OomDiagnostics.LogHeapStateOnOom(oom, "segment body retry");
                throw;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
            {
                lease?.Dispose();
                lease = null;
                if (attempt < MaxBodyRetries)
                {
                    Log.Debug(e, "Transient failure fetching segment {SegmentId} (attempt {Attempt}). Retrying.",
                        segmentId, attempt + 1);
                    if (MultiProviderNntpClient.CurrentReadSessionId is { } retrySession)
                        StreamTrace.TryRetry(retrySession, segmentId, attempt + 1, e.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    e.LogWarningKnownOrStack(
                        "Segment {SegmentId} unavailable at playback start after {Attempts} attempts while reading {FileName}. " +
                        "Failing the stream so the player surfaces an error.",
                        segmentId, attempt + 1, _fileName);
                    throw;
                }

                throw CreateTransientSegmentFailure(segmentId, segmentIndex, e);
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    private async Task<SegmentDownloadResult> DownloadBatchSegment(
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        int segmentIndex,
        bool isFirstSegment,
        ArticleByteLease initialLease,
        CancellationToken cancellationToken)
    {
        var estimate = GetPlannedSegmentBytes(segmentIndex);
        var lease = initialLease;
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
            var stream = await DrainSegmentAsync(
#pragma warning restore CA2000
                    response.Stream!, segmentIndex, cancellationToken, lease, estimate)
                .ConfigureAwait(false);
            lease = null; // owned by BudgetedStream / buffer
            return SegmentDownloadResult.Success(stream, estimate);
        }
        catch (UsenetArticleNotFoundException e)
        {
            lease?.Dispose();
            lease = null;
            var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                .ConfigureAwait(false);
            if (fallback is not null)
                return SegmentDownloadResult.Success(fallback, estimate);

            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                e.SegmentId,
                segmentIndex,
                e);
        }
        catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
        {
            lease?.Dispose();
            lease = null;
            try
            {
                var stream = await RetryCorruptSegmentAsync(
                        segmentId, segmentIndex, e, cancellationToken)
                    .ConfigureAwait(false);
                return SegmentDownloadResult.Success(stream, estimate);
            }
            catch (UsenetCorruptArticleException persistent)
            {
                if (_failFastOnFirstSegment && isFirstSegment) throw;
                return ZeroFillSegment(
                    "Article {SegmentId} persistently corrupt while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                    segmentId,
                    segmentIndex,
                    persistent);
            }
        }
        catch (OutOfMemoryException oom)
        {
            OomDiagnostics.LogHeapStateOnOom(oom, "pipelined segment batch");
            throw;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
        {
            lease?.Dispose();
            lease = null;
            // A failure inside a pipelined batch says nothing about whether the article
            // can be fetched at all: the batch shares one connection, so a stall or a
            // dropped socket takes out unrelated segments with it. Re-request this
            // segment on its own first, which is what gives provider failover and the
            // streaming-timeout retries a chance before any data is degraded.
            Stream? rescued;
            try
            {
                rescued = await TryRescueSegmentAsync(segmentId, segmentIndex, e, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException notFound)
            {
                // Rescue confirmed the article is genuinely missing — gap-fill
                // instead of treating it as a transient transport failure.
                if (_failFastOnFirstSegment && isFirstSegment) throw;
                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers while reading {FileName}. Filling the {Bytes}-byte gap to preserve later file offsets.",
                    notFound.SegmentId,
                    segmentIndex,
                    notFound);
            }

            if (rescued is not null)
                return SegmentDownloadResult.Success(rescued, estimate);

            if (_failFastOnFirstSegment && isFirstSegment) throw;
            throw CreateTransientSegmentFailure(segmentId, segmentIndex, e);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Re-requests a segment individually after its pipelined response failed. Returns
    /// null once the retries are spent. Throws <see cref="UsenetArticleNotFoundException"/>
    /// if rescue confirms the article is genuinely missing, so the caller can gap-fill
    /// rather than treating it as a transient transport failure.
    /// </summary>
    private async Task<Stream?> TryRescueSegmentAsync(
        string segmentId,
        int segmentIndex,
        Exception batchFailure,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxBodyRetries; attempt++)
        {
            Log.Debug(
                batchFailure,
                "Pipelined segment {SegmentId} failed; re-requesting it individually (attempt {Attempt}).",
                segmentId, attempt);
            if (MultiProviderNntpClient.CurrentReadSessionId is { } retrySession)
                StreamTrace.TryRetry(retrySession, segmentId, attempt, batchFailure.Message);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
                ArticleByteLease? lease = await LeaseSegmentBytesAsync(
                    GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);
                try
                {
                    var response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                        .ConfigureAwait(false);
                    await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var stream = await DrainSegmentAsync(
#pragma warning restore CA2000
                        response.Stream!, segmentIndex, cancellationToken, lease).ConfigureAwait(false);
                    lease = null;
                    return stream;
                }
                finally
                {
                    lease?.Dispose();
                }
            }
            catch (UsenetArticleNotFoundException)
            {
                throw;
            }
            catch (OutOfMemoryException oom)
            {
                OomDiagnostics.LogHeapStateOnOom(oom, "individual segment rescue");
                throw;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
            {
                Log.Debug(e, "Individual rescue of segment {SegmentId} failed (attempt {Attempt}).",
                    segmentId, attempt);
            }
        }

        return null;
    }

    private static Task ThrowOnSegmentIdMismatchAsync(
        string segmentId,
        UsenetDecodedBodyResponse response) =>
        SegmentResponseValidator.ThrowOnSegmentIdMismatchAsync(segmentId, response);

    private async Task<Stream> RetryCorruptSegmentAsync(
        string segmentId,
        int segmentIndex,
        UsenetCorruptArticleException initialFailure,
        CancellationToken cancellationToken)
    {
        var failure = initialFailure;
        for (var attempt = 1; attempt <= MaxCorruptionRetries; attempt++)
        {
            Log.Debug(
                failure,
                "Corrupt pipelined segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                segmentId,
                failure.ProviderKey,
                attempt);
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                ArticleByteLease? lease = await LeaseSegmentBytesAsync(
                    GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);
                try
                {
                    var response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                        .ConfigureAwait(false);
                    await ThrowOnSegmentIdMismatchAsync(segmentId, response).ConfigureAwait(false);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var stream = await DrainSegmentAsync(
#pragma warning restore CA2000
                        response.Stream!, segmentIndex, cancellationToken, lease).ConfigureAwait(false);
                    lease = null;
                    return stream;
                }
                finally
                {
                    lease?.Dispose();
                }
            }
            catch (UsenetCorruptArticleException exception)
            {
                failure = exception;
            }
        }

        var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
            .ConfigureAwait(false);
        if (fallback is not null)
            return fallback;

        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing a corrupt segment failure.");
    }

    /// <summary>
    /// Try alternate MessageIds for a missing primary segment. Each BODY
    /// attempt completes its callback exactly once via DecodedBodyAsync.
    /// </summary>
    private async Task<Stream?> TryFallbackSegmentsAsync(
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        var fallbacks = GetFallbacks(segmentIndex);
        if (fallbacks.Length == 0) return null;

        foreach (var fallbackId in fallbacks)
        {
            try
            {
                ArticleByteLease? lease = await LeaseSegmentBytesAsync(
                    GetPlannedSegmentBytes(segmentIndex), cancellationToken).ConfigureAwait(false);
                try
                {
                    var bodyResponse = await _usenetClient
                        .DecodedBodyAsync(fallbackId, cancellationToken)
                        .ConfigureAwait(false);
                    await ThrowOnSegmentIdMismatchAsync(fallbackId, bodyResponse).ConfigureAwait(false);
                    Log.Debug(
                        "Segment {PrimaryIndex} recovered via fallback MessageId {FallbackId} while reading {FileName}.",
                        segmentIndex, fallbackId, _fileName);
#pragma warning disable CA2000 // stream ownership transfers to the returned SegmentDownloadResult
                    var stream = await DrainSegmentAsync(
#pragma warning restore CA2000
                        bodyResponse.Stream!, segmentIndex, cancellationToken, lease).ConfigureAwait(false);
                    lease = null;
                    return stream;
                }
                finally
                {
                    lease?.Dispose();
                }
            }
            catch (UsenetArticleNotFoundException)
            {
                // Try the next alternate MessageId.
            }
            catch (UsenetCorruptArticleException)
            {
                // Corrupt fallback — try the next alternate MessageId.
            }
            catch (UsenetUnexpectedResponseException e)
            {
                Log.Debug(e, "Fallback MessageId {FallbackId} returned another article.", fallbackId);
            }
        }

        return null;
    }

    private string[] GetFallbacks(int segmentIndex)
    {
        if (_segmentFallbacks is null ||
            segmentIndex < 0 ||
            segmentIndex >= _segmentFallbacks.Length)
            return [];

        return _segmentFallbacks[segmentIndex] ?? [];
    }

    private async Task DisposeStreamAsync(
        Task<SegmentDownloadResult> streamTask,
        bool releaseInFlight = false)
    {
        try
        {
            var result = await streamTask.ConfigureAwait(false);
            await using var stream = result.Stream;
            if (releaseInFlight)
                ReleaseInFlightPrefetchBytes(result.PlannedBytes);
        }
        catch
        {
            // The producer owns reporting download failures.
        }
    }

    /// <summary>
    /// Substitutes a bounded gap for a segment that could not be downloaded, but only for
    /// a known fill length. Every byte after this segment is positioned by how many bytes
    /// it contributes, so a wrong length corrupts the rest of the file instead of just
    /// the part that failed — better to fail the read and let the player retry or report it.
    /// </summary>
    private SegmentDownloadResult ZeroFillSegment(
        string messageTemplate,
        string segmentId,
        int segmentIndex,
        Exception exception)
    {
        if (!_segmentSizes.TryGetFillLength(segmentIndex, out var fill, out var isExact))
            throw CreateUnknownLengthFailure(segmentId, segmentIndex, exception);

        if (!isExact)
        {
            Log.Debug(
                "Using the observed {Bytes}-byte segment size of {FileName} to replace failed segment {SegmentId}.",
                fill, _fileName, segmentId);
        }

        Par2RepairTriggerSink.Current?.ReportZeroFill(_fileName, segmentId, segmentIndex, fill);

#pragma warning disable CA2000 // gap-fill stream ownership transfers to the returned SegmentDownloadResult
        return SegmentDownloadResult.ZeroFill(
            CreateGapFillStream(fill, segmentIndex),
            messageTemplate,
            segmentId,
            fill,
            exception,
            GetPlannedSegmentBytes(segmentIndex));
#pragma warning restore CA2000
    }

    private Stream CreateGapFillStream(long fill, int segmentIndex)
    {
        if (!_useContainerAwareFill)
            return new ZeroStream(fill);

        long? fileOffset = _firstSegmentFileOffset;
        if (fileOffset is not null)
        {
            try
            {
                for (var i = 0; i < segmentIndex; i++)
                {
                    if (!_segmentSizes.TryGetExactSize(i, out var size))
                    {
                        fileOffset = null;
                        break;
                    }

                    fileOffset = checked(fileOffset.Value + size);
                }
            }
            catch (OverflowException)
            {
                fileOffset = null;
            }
        }

        return ContainerAwareFillStream.Create(_fileName, fill, fileOffset);
    }

    private Exception CreateUnknownLengthFailure(string segmentId, int segmentIndex, Exception failure)
    {
        var message =
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) could not be downloaded " +
            $"while reading \"{_fileName}\", and its exact length is unknown, so the rest of the file " +
            "cannot be delivered at the right offsets. Repair the item to restore its segment sizes.";
        return failure.IsNonRetryableDownloadException()
            ? new NonRetryableDownloadException(message, failure)
            : new RetryableDownloadException(message, failure);
    }

    private TransientSegmentExhaustionException CreateTransientSegmentFailure(
        string segmentId, int segmentIndex, Exception failure)
    {
        var message =
            $"Segment {segmentIndex + 1} of {_segmentIds.Length} ({segmentId}) could not be downloaded " +
            $"while reading \"{_fileName}\" after all retry attempts were exhausted. " +
            "The client should retry this range request.";
        return new TransientSegmentExhaustionException(message, failure);
    }

    private async Task<Stream> DrainSegmentAsync(
        Stream source,
        int segmentIndex,
        CancellationToken cancellationToken,
        ArticleByteLease? existingLease = null,
        long? leasedEstimate = null)
    {
        ArticleByteLease? lease = existingLease;
        var ownsLease = existingLease is null;
        PooledBufferStream? buffer = null;
        var sourceDisposeAttempted = false;
        try
        {
            var hasExactSize = _segmentSizes.TryGetExactSize(segmentIndex, out var exactSize);
            var expected = hasExactSize ? exactSize : _estimatedSegmentSize;
            var estimate = leasedEstimate
                ?? (expected is > 0 and <= int.MaxValue ? expected : _estimatedSegmentSize);
            if (estimate < 0) estimate = 0;

            if (lease is null)
                lease = await LeaseSegmentBytesAsync(estimate, cancellationToken).ConfigureAwait(false);

            var capacity = await ResolveDrainCapacityHintAsync(
                source, segmentIndex, estimate, cancellationToken).ConfigureAwait(false);
            buffer = new PooledBufferStream(capacity);
            var traceRange = MultiProviderNntpClient.CurrentStreamTraceRange;
            var drainStarted = Stopwatch.GetTimestamp();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            StreamTrace.TryStall(
                traceRange,
                StreamStallKind.BodyDrain,
                Stopwatch.GetElapsedTime(drainStarted));
            var drained = buffer.Length;
            if (hasExactSize)
                AlignDrainedSegment(buffer, segmentIndex, drained, exactSize);
            else
                _segmentSizes.RecordObservedSize(segmentIndex, drained);

            var actual = buffer.Length;
            if (actual != estimate)
                lease.Adjust(actual - estimate);

            buffer.Position = 0;
            // Keep the buffer and any internally acquired lease locally owned until
            // source disposal succeeds. A disposal failure must not strand either.
            sourceDisposeAttempted = true;
            await source.DisposeAsync().ConfigureAwait(false);
            // Build the wrapper that takes over the buffer and lease before dropping
            // local ownership, so a failure here still routes both through the catch.
            var result = ReferenceEquals(lease, ArticleByteLease.Empty)
                ? (Stream)buffer
                : new BudgetedStream(buffer, lease);
            ownsLease = false;
            buffer = null;
            return result;
        }
        catch
        {
            if (buffer is not null)
                await buffer.DisposeAsync().ConfigureAwait(false);
            if (ownsLease)
                lease?.Dispose();
            throw;
        }
        finally
        {
            if (!sourceDisposeAttempted)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uses imported ranges first, then the body's exact decoded yEnc part size, and leaves
    /// the file average as a fallback only. This chooses a rent hint; it never controls output.
    /// </summary>
    private async ValueTask<int> ResolveDrainCapacityHintAsync(
        Stream source,
        int segmentIndex,
        long estimate,
        CancellationToken cancellationToken)
    {
        if (_segmentSizes.TryGetExactSize(segmentIndex, out var exact))
            return ToCapacity(exact);

        if (estimate <= 0 || estimate > Array.MaxLength)
            return 0;

        if (source is not YencStream yencSource)
            return ToCapacity(estimate);

        UsenetYencHeader? header;
        try
        {
            header = await yencSource.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // Header parse failed on a body that still may decode via ReadAsync (test fakes
            // and some nonstandard streams). Keep the estimate; do not swallow corrupt-article
            // or other download failures — those are not thrown from GetYencHeadersAsync here.
            return ToCapacity(estimate);
        }

        if (header is not null
            && header.PartSize > 0
            && header.PartSize <= Array.MaxLength
            && IsPlausiblePartSize(
                header.PartSize, header.TotalParts, _segmentIds.Length, estimate))
            return (int)header.PartSize;

        return ToCapacity(estimate);
    }

    internal static int ToCapacity(long value) =>
        value > 0 && value <= Array.MaxLength ? (int)value : 0;

    /// <summary>
    /// Rejects remote yEnc PartSize values that cannot be a full-part size for this file
    /// average, so a malformed header cannot request an arbitrary multi-gigabyte rent.
    /// </summary>
    internal static bool IsPlausiblePartSize(
        long partSize, int totalParts, int remainingParts, long estimate)
    {
        if (partSize <= 0 || estimate <= 0) return false;
        if (totalParts < remainingParts) return false;
        if (totalParts <= 1) return partSize <= estimate;
        // EstimatedSegmentSize is floor(fileSize / totalParts), so add one before deriving
        // the strict upper bound to cover the discarded integer-division remainder.
        var upperBound = Math.Ceiling((estimate + 1d) * totalParts / (totalParts - 1));
        return partSize <= upperBound;
    }

    private async ValueTask<ArticleByteLease> LeaseSegmentBytesAsync(
        long estimate,
        CancellationToken cancellationToken)
    {
        if (_budget is null || estimate <= 0)
            return ArticleByteLease.Empty;
        return await _budget.LeaseAsync(estimate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a segment's contribution equal to its recorded length. Leaving either a
    /// shortfall or an overrun would shift every following byte, so short bodies are
    /// padded and long bodies are truncated to the recorded size.
    /// </summary>
    private void AlignDrainedSegment(PooledBufferStream buffer, int segmentIndex, long drained, long expected)
    {
        if (drained == expected) return;

        if (drained > expected)
        {
            Log.Debug(
                "Segment {SegmentIndex} of {FileName} decoded {Drained} bytes but was recorded as {Expected}. Truncating to keep offsets aligned.",
                segmentIndex, _fileName, drained, expected);
            buffer.SetLength(expected);
            return;
        }

        var shortfall = expected - drained;
        ZeroFillLogLimiter.Write(
            "Segment {SegmentId} of {FileName} decoded {Bytes} bytes short of its recorded size. " +
            "Filling the gap to keep the rest of the file aligned.",
            _segmentIds.Span[segmentIndex],
            _fileName,
            shortfall);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, _segmentIds.Span[segmentIndex], shortfall);

        buffer.SetLength(expected);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                // Time spent here is the consumer starving: prefetch has not yet delivered
                // the next segment. Low provider time with high consumer wait means the
                // pipeline is not running far enough ahead, not that the provider is slow.
                var traceRange = MultiProviderNntpClient.CurrentStreamTraceRange;
                var waitStarted = Stopwatch.GetTimestamp();
                var wasQueued = _streamTasks.Reader.TryRead(out var streamTask);
                if (!wasQueued)
                {
                    if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                    if (!_streamTasks.Reader.TryRead(out streamTask)) return 0;
                }

                // Ready means prefetch stayed ahead; use IsCompleted (not Successfully) so
                // faulted tasks still count as present when the consumer arrived.
                var nextSegment = streamTask
                    ?? throw new InvalidOperationException("Segment channel returned a null task.");
                var readyWhenNeeded = wasQueued && nextSegment.IsCompleted;
                // Test hook: fires after readiness is sampled and before the segment task is awaited,
                // so lockstep tests can keep the gate closed until starvation is observed.
                TestOnSegmentReadiness?.Invoke(readyWhenNeeded);
                var result = await nextSegment.ConfigureAwait(false);
                StreamTrace.TryStall(
                    traceRange,
                    StreamStallKind.ConsumerWait,
                    Stopwatch.GetElapsedTime(waitStarted));
                ReleaseInFlightPrefetchBytes(result.PlannedBytes);
                // Ignore the first delivered segment (startup warm-up).
                if (_deliveredSegments++ > 0)
                    ObserveBatchReadiness(readyWhenNeeded);
                _stream = AcceptSegment(result);
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private void ObserveBatchReadiness(bool readyWhenNeeded)
    {
        if (_batchSizer is null) return;
        var change = _batchSizer.Observe(readyWhenNeeded);
        if (change is null) return;

        Log.Debug(
            "Prefetch batch size for {FileName} changed from {PreviousBatchSize} to {BatchSize}. " +
            "ReadyWhenNeeded={ReadyWhenNeeded}",
            _fileName, change.Value.Previous, change.Value.Current, change.Value.ReadyWhenNeeded);

        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryPrefetchWidth(sessionId, change.Value.Previous, change.Value.Current);
    }

    private Stream AcceptSegment(SegmentDownloadResult result)
    {
        if (!result.IsZeroFill)
        {
            _consecutiveZeroFills = 0;
            return result.Stream;
        }

        _consecutiveZeroFills++;
        ZeroFillLogLimiter.Write(
            result.MessageTemplate!,
            result.SegmentId!,
            _fileName,
            result.Bytes,
            result.Failure);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, result.SegmentId!, result.Bytes);

        if (_consecutiveZeroFills < MaxConsecutiveZeroFills)
            return result.Stream;

        result.Stream.Dispose();
        _cts.Cancel();
        ExceptionDispatchInfo.Capture(result.Failure!).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing a gap-fill failure.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        // Sync Dispose must stay non-blocking (Seek calls it). Start the same
        // idempotent cleanup that DisposeAsync awaits for lease release.
        _ = EnsureDisposeAsync();
        // Must be the protected overload: the parameterless Stream.Dispose() routes
        // back through Close() into this method and recurses until the stack overflows.
        base.Dispose(disposing);
    }

#pragma warning disable CA2215 // base.DisposeAsync() would route through Close()/Dispose(true) back into EnsureDisposeAsync's sync-over-async teardown; the _disposeGate/_disposeTask pair already guarantees exactly-once cleanup (see Dispose(bool) recursion note)
    public override async ValueTask DisposeAsync()
    {
        await EnsureDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
#pragma warning restore CA2215

    private Task EnsureDisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            // Mark disposed before async cleanup so sync Dispose immediately rejects reads.
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
#pragma warning disable CA1849 // synchronous Cancel is required -- teardown callbacks must run before _streamTasks.Writer completes; CancelAsync would race TryComplete
            _cts.Cancel();
#pragma warning restore CA1849
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _streamTasks.Writer.TryComplete();

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        // Drain queued segments concurrently so a task blocked on LeaseAsync can
        // wake when another queued BudgetedStream releases its lease.
        var pending = new List<Task>();
        while (_streamTasks.Reader.TryRead(out var streamTask))
            pending.Add(DisposeStreamAsync(streamTask, releaseInFlight: true));

        try
        {
            await _downloadTask.ConfigureAwait(false);
        }
        catch
        {
            // Producer failures are surfaced on ReadAsync; teardown only needs cleanup.
        }

        while (_streamTasks.Reader.TryRead(out var streamTask))
            pending.Add(DisposeStreamAsync(streamTask, releaseInFlight: true));

        // Join the producer's out-of-band disposals so leases they hold are released
        // before DisposeAsync completes. The producer has exited by now (awaited above),
        // so no new orphans can be enqueued; drain is safe without a lock.
        while (_orphanedDisposals.TryDequeue(out var orphaned))
            pending.Add(orphaned);

        if (pending.Count > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);

        _cts.Dispose();
    }

    private sealed record SegmentDownloadResult(
        Stream Stream,
        long PlannedBytes = 0,
        string? MessageTemplate = null,
        string? SegmentId = null,
        long Bytes = 0,
        Exception? Failure = null)
    {
        public bool IsZeroFill => Failure is not null;

        public static SegmentDownloadResult Success(Stream stream, long plannedBytes = 0) =>
            new(stream, plannedBytes);

        public static SegmentDownloadResult ZeroFill(
            Stream stream,
            string messageTemplate,
            string segmentId,
            long bytes,
            Exception failure,
            long plannedBytes = 0) =>
            new(stream, plannedBytes, messageTemplate, segmentId, bytes, failure);
    }
}
