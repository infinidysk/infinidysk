using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Pipelines decoded BODY commands and returns their ordered response tasks.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked until earlier streams are drained, and each decoded pipe applies bounded
    /// backpressure according to <see cref="UsenetClientOptions"/>. Observe
    /// <see cref="UsenetDecodedBodyBatch.Completion"/> after draining or abandoning the batch.
    /// </remarks>
    public Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken)
    {
        return DecodedBodiesAsync(segmentIds, null, cancellationToken);
    }

    /// <summary>
    /// Pipelines decoded BODY commands and reports when the complete batch releases the connection.
    /// </summary>
    /// <remarks>
    /// Consume or dispose each response stream before awaiting the next response. Later responses
    /// remain blocked until earlier streams are drained, and each decoded pipe applies bounded
    /// backpressure according to <see cref="UsenetClientOptions"/>. The completion callback reports
    /// <see cref="ArticleBodyResult.NotFound"/> for clean 430 responses,
    /// <see cref="ArticleBodyResult.Cancelled"/> after a successfully drained cancellation, and
    /// <see cref="ArticleBodyResult.NotRetrieved"/> only when the connection is unsafe to reuse.
    /// <see cref="UsenetDecodedBodyBatch.Completion"/> finishes after that callback and after the
    /// command lock is released.
    /// </remarks>
    public async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        if (segmentIds.Count > _options.MaxPipelineDepth)
        {
            throw new ArgumentException(
                $"Batch exceeds MaxPipelineDepth ({_options.MaxPipelineDepth}); " +
                "split into smaller batches to avoid TCP-window pipeline deadlock (RFC 3977 §3.5).",
                nameof(segmentIds));
        }

        var segments = new SegmentId[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
        {
            segments[index] = segmentIds[index];
            ValidateSegmentId(segmentIds[index]);
        }

        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            InvokeBatchCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
            throw;
        }

        var pumpStarted = false;
        var writeStarted = false;
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            using (var operationCts = CreateOperationTokenSource(cancellationToken))
            using (var writeTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, operationCts.Token))
            {
                // Bytes may reach the wire from here on (RFC 3977 §3.5).
                writeStarted = true;
                await WritePipelinedBodyCommandsAsync(segments, writeTimeout)
                    .ConfigureAwait(false);
            }

            var completions = new TaskCompletionSource<UsenetDecodedBodyResponse>[segments.Length];
            var responses = new Task<UsenetDecodedBodyResponse>[segments.Length];
            for (var index = 0; index < segments.Length; index++)
            {
                completions[index] = new TaskCompletionSource<UsenetDecodedBodyResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                responses[index] = completions[index].Task;
            }

            pumpStarted = true;
            var completion = ProcessDecodedBodyBatchAsync(
                segments,
                completions,
                onConnectionReadyAgain,
                cancellationToken);

            return new UsenetDecodedBodyBatch
            {
                Responses = responses,
                Completion = completion
            };
        }
        catch (Exception exception)
        {
            if (writeStarted)
            {
                RecordConnectionFailure(exception);
            }

            throw;
        }
        finally
        {
            if (!pumpStarted)
            {
                _commandLock.Release();
                InvokeBatchCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
            }
        }
    }

    /// <summary>
    /// Pipelines decoded BODY commands and yields their responses in request order.
    /// </summary>
    public IAsyncEnumerable<UsenetDecodedBodyResponse> EnumerateDecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken)
    {
        return EnumerateDecodedBodiesAsync(segmentIds, null, cancellationToken);
    }

    /// <summary>
    /// Pipelines decoded BODY commands, yields responses in request order, and reports when
    /// the complete operation releases the connection.
    /// </summary>
    public async IAsyncEnumerable<UsenetDecodedBodyResponse> EnumerateDecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var enumerationCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UsenetDecodedBodyBatch? batch = null;
        UsenetDecodedBodyResponse? activeResponse = null;
        var completed = false;
        var yieldedResponseCount = 0;
        try
        {
            batch = await DecodedBodiesAsync(
                    segmentIds, onConnectionReadyAgain, enumerationCts.Token)
                .ConfigureAwait(false);
            foreach (var responseTask in batch.Responses)
            {
                var response = await responseTask.ConfigureAwait(false);
                activeResponse = response;
                yieldedResponseCount++;
                yield return response;
            }

            await batch.Completion.ConfigureAwait(false);
            activeResponse = null;
            completed = true;
        }
        finally
        {
            if (!completed && batch != null)
            {
                DisposeResponseStream(activeResponse);
                await enumerationCts.CancelAsync().ConfigureAwait(false);
                await DisposeUnyieldedResponsesAsync(batch, yieldedResponseCount)
                    .ConfigureAwait(false);
                await ObserveBatchCompletionAsync(batch.Completion).ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposeUnyieldedResponsesAsync(
        UsenetDecodedBodyBatch batch,
        int yieldedResponseCount)
    {
        for (var index = yieldedResponseCount; index < batch.Responses.Count; index++)
        {
            try
            {
                var response = await batch.Responses[index].ConfigureAwait(false);
                DisposeResponseStream(response);
            }
            catch
            {
                // Cancellation or a batch failure leaves no response stream to dispose.
            }
        }
    }

    private static void DisposeResponseStream(UsenetDecodedBodyResponse? response)
    {
        try
        {
            response?.Stream?.Dispose();
        }
        catch
        {
            // Cleanup must continue so the batch pump can release the command lease.
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
            // Early abandonment must still observe the producer task.
        }
    }

    private async ValueTask WritePipelinedBodyCommandsAsync(
        SegmentId[] segments,
        CoalescedReadTimeout ioTimeout)
    {
        var totalLength = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            // "BODY <id>\r\n"
            totalLength += 4 + 1 + 1 + segments[index].Value.Length + 1 + 2;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var written = 0;
            for (var index = 0; index < segments.Length; index++)
            {
                written += FormatBodyCommand(buffer.AsSpan(written), segments[index]);
            }

            await WriteCommandAsync(buffer.AsMemory(0, written), ioTimeout)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FormatBodyCommand(Span<byte> destination, SegmentId segmentId)
    {
        var written = Encoding.Latin1.GetBytes("BODY <", destination);
        written += Encoding.Latin1.GetBytes(segmentId.Value, destination[written..]);
        destination[written++] = (byte)'>';
        destination[written++] = (byte)'\r';
        destination[written++] = (byte)'\n';
        return written;
    }

    private async Task ProcessDecodedBodyBatchAsync(
        SegmentId[] segmentIds,
        TaskCompletionSource<UsenetDecodedBodyResponse>[] completions,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken callerCancellationToken)
    {
        Exception? failure = null;
        var completionResult = ArticleBodyResult.Retrieved;
        string? completionReason = null;
        var nextResponseIndex = 0;
        CancellationTokenSource? operationCts = null;
        CoalescedReadTimeout? sharedReadTimeout = null;
        try
        {
            operationCts = CreateOperationTokenSource(callerCancellationToken);
            sharedReadTimeout = new CoalescedReadTimeout(
                _options.ReadTimeout,
                _timeProvider,
                operationCts.Token);
            while (nextResponseIndex < segmentIds.Length)
            {
                var segmentId = segmentIds[nextResponseIndex];
                var response = await ReadLineAsync(sharedReadTimeout).ConfigureAwait(false);
                var responseCode = ParseResponseCode(response);

                if (responseCode != (int)UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                        .ConfigureAwait(false);
                    if (responseCode == (int)UsenetResponseType.NoArticleWithThatMessageId &&
                        completionResult != ArticleBodyResult.NotRetrieved)
                    {
                        completionResult = ArticleBodyResult.NotFound;
                    }
                    else if (responseCode != (int)UsenetResponseType.NoArticleWithThatMessageId)
                    {
                        completionResult = ArticleBodyResult.NotRetrieved;
                        completionReason ??= $"unexpected-response-{responseCode}";
                    }
                    completions[nextResponseIndex].TrySetResult(new UsenetDecodedBodyResponse
                    {
                        SegmentId = segmentId,
                        ResponseCode = responseCode,
                        ResponseMessage = response!,
                        Stream = null
                    });
                    nextResponseIndex++;
                    continue;
                }

                var pipe = new Pipe(_decodedBodyPipeOptions);
                var decodedStream = new DecodedBodyReadStream(
                    pipe.Reader.AsStream(), AdjustBufferedDecodedBodyBytes);
                var headersCompletion =
                    new TaskCompletionSource<UsenetYencHeader?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                completions[nextResponseIndex].TrySetResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = new YencStream(decodedStream, headersCompletion.Task)
                });

                var bodyReadResult = await ReadDecodedBodyToPipeAsync(
                        pipe.Writer,
                        headersCompletion,
                        operationCts,
                        onConnectionReadyAgain: null,
                        decodedStream: decodedStream,
                        releaseCommandLock: false,
                        sharedReadTimeout: sharedReadTimeout,
                        callerCancellationToken: callerCancellationToken)
                    .ConfigureAwait(false);
                if (bodyReadResult.Failure == null)
                {
                    nextResponseIndex++;
                    await decodedStream.Completion.WaitAsync(operationCts.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                failure = bodyReadResult.Failure;
                nextResponseIndex++;
                var cancelledByCaller =
                    bodyReadResult.Failure is OperationCanceledException &&
                    callerCancellationToken.IsCancellationRequested;
                if (cancelledByCaller &&
                    _options.CancellationPolicy == ConnectionReleasePolicy.AbandonConnection)
                {
                    RecordConnectionFailure(bodyReadResult.Failure);
                    completionResult = ArticleBodyResult.NotRetrieved;
                    completionReason = DescribeFailure(bodyReadResult.Failure);
                    break;
                }

                var drainFailure = await TryDrainPipelinedBodiesAsync(
                        segmentIds.Length - nextResponseIndex)
                    .ConfigureAwait(false);
                if (drainFailure != null)
                {
                    RecordConnectionFailure(drainFailure);
                }

                completionResult =
                    cancelledByCaller &&
                    bodyReadResult.ConnectionReusable &&
                    drainFailure == null
                        ? ArticleBodyResult.Cancelled
                        : ArticleBodyResult.NotRetrieved;
                if (completionResult == ArticleBodyResult.NotRetrieved)
                {
                    completionReason = drainFailure != null
                        ? DescribeFailure(drainFailure)
                        : DescribeFailure(bodyReadResult.Failure);
                }
                break;
            }
        }
        catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = exception;
            if (_options.CancellationPolicy == ConnectionReleasePolicy.AbandonConnection)
            {
                RecordConnectionFailure(exception);
                completionResult = ArticleBodyResult.NotRetrieved;
                completionReason = DescribeFailure(exception);
            }
            else
            {
                var drainFailure = await TryDrainPipelinedBodiesAsync(
                        segmentIds.Length - nextResponseIndex)
                    .ConfigureAwait(false);
                if (drainFailure != null)
                {
                    RecordConnectionFailure(drainFailure);
                }

                completionResult = drainFailure == null
                    ? ArticleBodyResult.Cancelled
                    : ArticleBodyResult.NotRetrieved;
                if (drainFailure != null)
                {
                    completionReason = DescribeFailure(drainFailure);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            completionResult = ArticleBodyResult.NotRetrieved;
            completionReason = exception is OutOfMemoryException
                ? "out-of-memory"
                : DescribeFailure(exception);
            RecordConnectionFailure(exception);
        }
        finally
        {
            if (failure != null)
            {
                for (var index = nextResponseIndex; index < completions.Length; index++)
                {
                    completions[index].TrySetException(failure);
                }
            }

            sharedReadTimeout?.Dispose();
            operationCts?.Dispose();
            _commandLock.Release();
            InvokeBatchCallback(
                onConnectionReadyAgain,
                completionResult,
                completionReason);
        }

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task<Exception?> TryDrainPipelinedBodiesAsync(int responseCount)
    {
        try
        {
            for (var index = 0; index < responseCount; index++)
            {
                using var operationCts = CreateOperationTokenSource(CancellationToken.None);
                var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
                var responseCode = ParseResponseCode(response);
                if (responseCode != (int)UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    if (IsMultiLineCode(responseCode))
                    {
                        var unexpectedDrain = await TryDrainBodyAsync().ConfigureAwait(false);
                        if (unexpectedDrain != null)
                        {
                            return unexpectedDrain;
                        }
                    }

                    continue;
                }

                var bodyFailure = await TryDrainBodyAsync().ConfigureAwait(false);
                if (bodyFailure != null)
                {
                    return bodyFailure;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return exception;
        }
    }

    private static void InvokeBatchCallback(
        ArticleBodyCompletionHandler? callback,
        ArticleBodyResult result,
        string? failureReason = null)
    {
        try
        {
            callback?.Invoke(result, failureReason);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // User callbacks must not fault command setup or the background pump.
        }
    }

    /// <summary>
    /// Short, log-friendly failure classification: the outermost exception type, plus the
    /// socket error when one is wrapped — e.g. "IOException (SocketException: ConnectionReset)".
    /// Lets circuit-breaker and metrics reasons name the root cause instead of "NotRetrieved".
    /// </summary>
    internal static string? DescribeFailure(Exception? failure)
    {
        if (failure == null) return null;
        var description = failure.GetType().Name;

        // A direct SocketException (no wrapper) carries the socket error code on itself.
        // Format matches the wrapped case so "SocketException:" filters work uniformly.
        if (failure is System.Net.Sockets.SocketException directSocket)
            return $"SocketException: {directSocket.SocketErrorCode}";

        // Otherwise look for a SocketException wrapped by an outer transport exception.
        for (var inner = failure.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException socketException)
                return $"{description} (SocketException: {socketException.SocketErrorCode})";
        }

        return failure.InnerException is { } direct
            ? $"{description} ({direct.GetType().Name})"
            : description;
    }
}
