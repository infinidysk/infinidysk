using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Contexts;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

internal sealed record LocalBatchHit(
    int OriginalIndex,
    SegmentId RequestedId,
    UsenetDecodedBodyResponse Response);

internal sealed record RemoteBatchMiss(
    int OriginalIndex,
    int RemoteIndex,
    SegmentId RequestedId);

internal sealed record LocalBatchPartition(
    IReadOnlyList<LocalBatchHit> Hits,
    IReadOnlyList<RemoteBatchMiss> Misses);

internal readonly struct LocalLookupResult
{
    public bool Found { get; }
    public UsenetDecodedBodyResponse? Response { get; }

    private LocalLookupResult(bool found, UsenetDecodedBodyResponse? response)
    {
        Found = found;
        Response = response;
    }

    public static LocalLookupResult Miss => default;

    public static LocalLookupResult Hit(UsenetDecodedBodyResponse response) =>
        new(true, response ?? throw new ArgumentNullException(nameof(response)));
}

/// <summary>
/// Ordered local-data overlay for pipelined BODY batches. Store-specific lookup and
/// cache population stay in the owning wrappers; this helper owns partition, merge,
/// readiness gating, and the exactly-once aggregate callback.
/// </summary>
internal static class LocalDataBatchOverlay
{
    internal static Task<UsenetDecodedBodyResponse> PassThroughRemote(
        SegmentId _,
        UsenetDecodedBodyResponse response,
        CancellationToken __) =>
        Task.FromResult(response);

    internal static async Task<UsenetDecodedBodyBatch> ExecuteAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? outerCallback,
        Func<SegmentId, LocalLookupResult> tryOpenLocal,
        Func<IReadOnlyList<SegmentId>, ArticleBodyCompletionHandler, CancellationToken,
            Task<UsenetDecodedBodyBatch>> fetchMisses,
        Func<SegmentId, UsenetDecodedBodyResponse, CancellationToken,
            Task<UsenetDecodedBodyResponse>> transformRemote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        ArgumentNullException.ThrowIfNull(tryOpenLocal);
        ArgumentNullException.ThrowIfNull(fetchMisses);
        ArgumentNullException.ThrowIfNull(transformRemote);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ArticleBodyCompletion.InvokeContained(outerCallback, ArticleBodyResult.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
        }

        LocalBatchPartition partition;
        try
        {
            partition = Partition(segmentIds, tryOpenLocal, cancellationToken);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException &&
                            cancellationToken.IsCancellationRequested;
            ArticleBodyCompletion.InvokeContained(
                outerCallback,
                cancelled ? ArticleBodyResult.Cancelled : ArticleBodyResult.NotRetrieved,
                cancelled ? null : "local-batch-setup");
            throw;
        }

        var output = CreateOutputSources(segmentIds.Count);
        var responses = new Task<UsenetDecodedBodyResponse>[output.Length];
        for (var index = 0; index < output.Length; index++)
            responses[index] = output[index].Task;

        if (partition.Misses.Count == 0)
        {
            var state = new BatchCompletionState(outerCallback, hasInnerBatch: false, cancellationToken);
            var publisher = PublishInOrderAsync(
                segmentIds, partition, inner: null, output, state, transformRemote, cancellationToken);
#pragma warning disable CA2025 // returned Completion observes the publisher; callers own returned local streams
            return new UsenetDecodedBodyBatch
            {
                Responses = responses,
                Completion = state.CompleteAsync(publisher, Task.CompletedTask),
            };
#pragma warning restore CA2025
        }

        var deferred = new DeferredArticleBodyCallback();
        var abandonCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UsenetDecodedBodyBatch? inner = null;
        var mismatchHandled = false;
        try
        {
            var missIds = new SegmentId[partition.Misses.Count];
            for (var index = 0; index < partition.Misses.Count; index++)
                missIds[index] = partition.Misses[index].RequestedId;

            inner = await fetchMisses(missIds, deferred.Invoke, abandonCts.Token)
                .ConfigureAwait(false);
            if (inner.Responses.Count != partition.Misses.Count)
            {
                mismatchHandled = true;
                try
                {
                    await DecodedBodyBatchCleanup.AbandonAsync(inner, abandonCts).ConfigureAwait(false);
                }
                finally
                {
                    deferred.Discard();
                    DisposeHits(partition.Hits);
                    ArticleBodyCompletion.InvokeContained(
                        outerCallback, ArticleBodyResult.NotRetrieved, "batch-response-count-mismatch");
                }

                throw new InvalidOperationException(
                    $"Pipelined BODY returned {inner.Responses.Count} responses for {partition.Misses.Count} requests.");
            }
        }
        catch (Exception exception)
        {
            if (!mismatchHandled)
            {
                deferred.Discard();
                var cancelled = exception is OperationCanceledException &&
                                cancellationToken.IsCancellationRequested;
                try
                {
                    try
                    {
                        DisposeHits(partition.Hits);
                    }
                    finally
                    {
                        if (inner is not null)
                            await DecodedBodyBatchCleanup.AbandonAsync(inner, abandonCts)
                                .ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArticleBodyCompletion.InvokeContained(
                        outerCallback,
                        cancelled ? ArticleBodyResult.Cancelled : ArticleBodyResult.NotRetrieved,
                        cancelled ? null : "local-batch-setup");
                    abandonCts.Dispose();
                }
            }
            else
            {
                abandonCts.Dispose();
            }
            throw;
        }

        var overlayState = new BatchCompletionState(outerCallback, hasInnerBatch: true, cancellationToken);
        deferred.Activate(overlayState.RecordInner);
        var overlayPublisher = PublishInOrderAsync(
            segmentIds, partition, inner, output, overlayState, transformRemote, cancellationToken);
#pragma warning disable CA2025 // Completion owns abandonCts and disposes it after overlay and inner lifecycle finish
        return new UsenetDecodedBodyBatch
        {
            Responses = responses,
            Completion = CompleteThenDisposeAsync(
                overlayState.CompleteAsync(overlayPublisher, inner.Completion),
                abandonCts),
        };
#pragma warning restore CA2025
    }

    private static LocalBatchPartition Partition(
        IReadOnlyList<SegmentId> segmentIds,
        Func<SegmentId, LocalLookupResult> tryOpenLocal,
        CancellationToken cancellationToken)
    {
        var hits = new List<LocalBatchHit>();
        var misses = new List<RemoteBatchMiss>();
        try
        {
            for (var index = 0; index < segmentIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = segmentIds[index];
                var lookup = tryOpenLocal(requested);
                if (lookup.Found && lookup.Response is not null)
                {
                    hits.Add(new LocalBatchHit(index, requested, lookup.Response));
                }
                else
                {
                    misses.Add(new RemoteBatchMiss(index, misses.Count, requested));
                }
            }

            return new LocalBatchPartition(hits, misses);
        }
        catch
        {
            DisposeHits(hits);
            throw;
        }
    }

    private static TaskCompletionSource<UsenetDecodedBodyResponse>[] CreateOutputSources(int count)
    {
        var output = new TaskCompletionSource<UsenetDecodedBodyResponse>[count];
        for (var index = 0; index < count; index++)
        {
            output[index] = new TaskCompletionSource<UsenetDecodedBodyResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return output;
    }

    private static async Task PublishInOrderAsync(
        IReadOnlyList<SegmentId> requested,
        LocalBatchPartition partition,
        UsenetDecodedBodyBatch? inner,
        TaskCompletionSource<UsenetDecodedBodyResponse>[] output,
        BatchCompletionState state,
        Func<SegmentId, UsenetDecodedBodyResponse, CancellationToken,
            Task<UsenetDecodedBodyResponse>> transformRemote,
        CancellationToken cancellationToken)
    {
        var localByIndex = new LocalBatchHit?[requested.Count];
        foreach (var hit in partition.Hits)
            localByIndex[hit.OriginalIndex] = hit;

        var missByIndex = new RemoteBatchMiss?[requested.Count];
        foreach (var miss in partition.Misses)
            missByIndex[miss.OriginalIndex] = miss;

        Task previousTerminal = Task.CompletedTask;
        for (var index = 0; index < requested.Count; index++)
        {
            await ObserveForOrderingAsync(previousTerminal).ConfigureAwait(false);

            try
            {
                UsenetDecodedBodyResponse response;
                if (localByIndex[index] is { } local)
                {
                    response = local.Response;
                }
                else
                {
                    var miss = missByIndex[index]!;
                    response = await inner!.Responses[miss.RemoteIndex].ConfigureAwait(false);
                    response = await transformRemote(miss.RequestedId, response, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (response.Stream is null)
                {
                    output[index].TrySetResult(response);
                    previousTerminal = Task.CompletedTask;
                    state.ObserveResponse(response);
                    continue;
                }

                var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                response = response with
                {
                    Stream = new OrderedBatchYencStream(
                        response.Stream,
                        terminal,
                        state.ObserveStreamTerminal),
                };

                output[index].TrySetResult(response);
                previousTerminal = terminal.Task;
                state.ObserveResponse(response);
            }
            catch (Exception exception)
            {
                state.ObserveFailure(exception);
                output[index].TrySetException(exception);
                previousTerminal = Task.CompletedTask;
            }
        }

        await ObserveForOrderingAsync(previousTerminal).ConfigureAwait(false);
    }

    private static async Task ObserveForOrderingAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Predecessor already recorded and surfaced on its own response/stream path.
        }
    }

    private static async Task CompleteThenDisposeAsync(
        Task completion,
        ContextualCancellationTokenSource abandonCts)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            abandonCts.Dispose();
        }
    }

    private static void DisposeHits(IEnumerable<LocalBatchHit> hits)
    {
        ExceptionDispatchInfo? fatal = null;
        foreach (var hit in hits)
        {
            try
            {
                hit.Response.Stream?.Dispose();
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException)
                    fatal = BatchLifecycle.PreferFailure(fatal, exception);
            }
        }

        fatal?.Throw();
    }
}

internal sealed class BatchCompletionState
{
    private readonly ArticleBodyCompletionHandler? _outer;
    private readonly CancellationToken _callerToken;
    private readonly bool _hasInnerBatch;
    private int _outerCallbackFired;
    private int _notFound;
    private int _cancelled;
    private ExceptionDispatchInfo? _firstFailure;
    private InnerCallback? _inner;

    public BatchCompletionState(
        ArticleBodyCompletionHandler? outer,
        bool hasInnerBatch,
        CancellationToken callerToken)
    {
        _outer = outer;
        _callerToken = callerToken;
        _hasInnerBatch = hasInnerBatch;
    }

    public void RecordInner(ArticleBodyResult result, string? reason)
    {
        var payload = new InnerCallback(result, reason);
        Interlocked.CompareExchange(ref _inner, payload, null);
    }

    public void ObserveResponse(UsenetDecodedBodyResponse response)
    {
        if (response.ResponseType == UsenetResponseType.NoArticleWithThatMessageId ||
            UsenetArticleAvailability.IsDefinitiveMissing(response))
        {
            Interlocked.Increment(ref _notFound);
        }
    }

    public void ObserveStreamTerminal(Exception? failure)
    {
        if (failure is not null)
            ObserveFailure(failure);
    }

    public void ObserveFailure(Exception exception)
    {
        if (exception is OperationCanceledException && _callerToken.IsCancellationRequested)
        {
            Volatile.Write(ref _cancelled, 1);
            return;
        }

        Interlocked.CompareExchange(
            ref _firstFailure,
            ExceptionDispatchInfo.Capture(exception),
            null);
    }

    public async Task CompleteAsync(Task publisher, Task innerCompletion)
    {
        var publisherFailure = await BatchLifecycle.ObserveAsync(publisher).ConfigureAwait(false);
        var innerFailure = await BatchLifecycle.ObserveAsync(innerCompletion).ConfigureAwait(false);
        var observed = BatchLifecycle.Combine(publisherFailure, innerFailure);
        if (observed is not null)
            ObserveFailure(observed.SourceException);

        if (Volatile.Read(ref _inner) is null && _hasInnerBatch)
            RecordInner(ArticleBodyResult.NotRetrieved, "inner-callback-missing");

        FireOuterOnce(SelectResult(), SelectReason());

        var firstFailure = Volatile.Read(ref _firstFailure);
        if (firstFailure is null)
            return;
        if (firstFailure.SourceException is OperationCanceledException &&
            _callerToken.IsCancellationRequested)
        {
            return;
        }

        firstFailure.Throw();
    }

    private ArticleBodyResult SelectResult()
    {
        var inner = Volatile.Read(ref _inner);
        if (Volatile.Read(ref _firstFailure) is not null)
            return ArticleBodyResult.NotRetrieved;
        if (_hasInnerBatch && inner?.Result == ArticleBodyResult.NotRetrieved)
            return ArticleBodyResult.NotRetrieved;
        if (Volatile.Read(ref _cancelled) != 0)
            return ArticleBodyResult.Cancelled;
        if (_hasInnerBatch && inner?.Result == ArticleBodyResult.Cancelled)
            return ArticleBodyResult.Cancelled;
        if (Volatile.Read(ref _notFound) != 0 ||
            (_hasInnerBatch && inner?.Result == ArticleBodyResult.NotFound))
        {
            return ArticleBodyResult.NotFound;
        }

        return ArticleBodyResult.Retrieved;
    }

    private string? SelectReason()
    {
        var inner = Volatile.Read(ref _inner);
        var result = SelectResult();
        return result switch
        {
            ArticleBodyResult.NotRetrieved when Volatile.Read(ref _firstFailure) is not null
                => "overlay-lifecycle-failure",
            ArticleBodyResult.NotRetrieved => inner?.Reason,
            ArticleBodyResult.Cancelled => inner?.Reason,
            ArticleBodyResult.NotFound => inner?.Reason,
            _ => null,
        };
    }

    private void FireOuterOnce(ArticleBodyResult result, string? reason)
    {
        if (Interlocked.Exchange(ref _outerCallbackFired, 1) != 0)
            return;
        ArticleBodyCompletion.InvokeContained(_outer, result, reason);
    }

    private sealed record InnerCallback(ArticleBodyResult Result, string? Reason);
}

internal sealed class OrderedBatchYencStream : YencStream
{
    private readonly YencStream _inner;
    private readonly TaskCompletionSource _terminal;
    private readonly Action<Exception?> _observeTerminal;
    private int _signaled;
    private int _disposed;

    public OrderedBatchYencStream(
        YencStream inner,
        TaskCompletionSource terminal,
        Action<Exception?> observeTerminal)
        : base(Null)
    {
        _inner = inner;
        _terminal = terminal;
        _observeTerminal = observeTerminal;
    }

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetYencHeadersAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                Signal(null);
            return read;
        }
        catch (Exception exception)
        {
            Signal(exception);
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            Signal(null);
            return;
        }

        Exception? failure = null;
        try
        {
            _inner.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            Signal(failure);
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            Signal(null);
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            Signal(failure);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Signal(Exception? failure)
    {
        if (Interlocked.Exchange(ref _signaled, 1) != 0)
            return;
        _observeTerminal(failure);
        if (failure is null)
            _terminal.TrySetResult();
        else
            _terminal.TrySetException(failure);
    }
}
