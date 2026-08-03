using System.Buffers;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Pipelines STAT existence checks and returns the ordered responses (RFC 3977 §3.5).
    /// </summary>
    /// <remarks>
    /// Responses map one-to-one, in order, to <paramref name="segmentIds"/>. Batches larger than
    /// <see cref="UsenetClientOptions.MaxPipelineDepth"/> are windowed internally. A mid-batch
    /// protocol or transport failure poisons the connection; cancellation drains the in-flight
    /// replies and keeps the connection reusable under
    /// <see cref="ConnectionReleasePolicy.DrainToReuse"/>.
    /// </remarks>
    public async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            return Array.Empty<UsenetStatResponse>();
        }

        var segments = new SegmentId[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
        {
            segments[index] = segmentIds[index];
            ValidateSegmentId(segmentIds[index]);
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeStarted = false;
        var nextToWrite = 0;
        var readIndex = 0;
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            using var operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);

            var depth = Math.Min(_options.MaxPipelineDepth, segments.Length);
            var results = new UsenetStatResponse[segments.Length];

            // Bytes may reach the wire from here on (RFC 3977 §3.5).
            writeStarted = true;
            await WritePipelinedStatCommandsAsync(segments, 0, depth, ioTimeout)
                .ConfigureAwait(false);
            nextToWrite = depth;

            for (; readIndex < segments.Length; readIndex++)
            {
                // Refill before blocking on the next read so the window stays full.
                if (nextToWrite < segments.Length &&
                    nextToWrite - readIndex <= depth / 2)
                {
                    var refillCount = Math.Min(
                        depth - (nextToWrite - readIndex),
                        segments.Length - nextToWrite);
                    if (refillCount > 0)
                    {
                        await WritePipelinedStatCommandsAsync(
                                segments, nextToWrite, refillCount, ioTimeout)
                            .ConfigureAwait(false);
                        nextToWrite += refillCount;
                    }
                }

                var line = await ReadLineAsync(ioTimeout).ConfigureAwait(false)
                    ?? throw new UsenetProtocolException(
                        "The NNTP connection closed before all pipelined STAT responses were received.");
                var code = ParseResponseCode(line);

                if (code == (int)UsenetResponseType.ArticleExists)
                {
                    ThrowIfDesyncedStatEcho(line, segments[readIndex]);
                }
                else
                {
                    await DrainUnexpectedMultiLineAsync(code, operationCts.Token)
                        .ConfigureAwait(false);
                    // A failed drain poisons without throwing; abort so later replies
                    // are not misaligned against the FIFO.
                    ThrowIfUnhealthy();
                }

                results[readIndex] = new UsenetStatResponse
                {
                    ResponseCode = code,
                    ResponseMessage = line,
                    ArticleExists = code == (int)UsenetResponseType.ArticleExists,
                };
            }

            return results;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && writeStarted)
        {
            if (_options.CancellationPolicy == ConnectionReleasePolicy.AbandonConnection)
            {
                RecordConnectionFailure(exception);
                throw;
            }

            var inFlight = nextToWrite - readIndex;
            var drainFailure = await TryDrainPipelinedStatsAsync(inFlight)
                .ConfigureAwait(false);
            if (drainFailure != null)
            {
                RecordConnectionFailure(drainFailure);
            }

            throw;
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
            _commandLock.Release();
        }
    }

    private async ValueTask WritePipelinedStatCommandsAsync(
        IReadOnlyList<SegmentId> segments,
        int start,
        int count,
        CoalescedReadTimeout ioTimeout)
    {
        var totalLength = 0;
        for (var index = 0; index < count; index++)
        {
            // "STAT <id>\r\n"
            totalLength += 5 + 1 + segments[start + index].Value.Length + 1 + 2;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var written = 0;
            for (var index = 0; index < count; index++)
            {
                written += FormatStatCommand(buffer.AsSpan(written), segments[start + index]);
            }

            await WriteCommandAsync(buffer.AsMemory(0, written), ioTimeout)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FormatStatCommand(Span<byte> destination, SegmentId segmentId)
    {
        var written = Encoding.Latin1.GetBytes("STAT <", destination);
        written += Encoding.Latin1.GetBytes(segmentId.Value, destination[written..]);
        destination[written++] = (byte)'>';
        destination[written++] = (byte)'\r';
        destination[written++] = (byte)'\n';
        return written;
    }

    private static void ThrowIfDesyncedStatEcho(string line, SegmentId expected)
    {
        var open = line.IndexOf('<');
        if (open < 0)
        {
            return;
        }

        var close = line.IndexOf('>', open + 1);
        if (close < 0)
        {
            return;
        }

        var echoed = line.AsSpan(open + 1, close - open - 1);
        if (echoed.SequenceEqual(expected.Value))
        {
            return;
        }

        throw new UsenetProtocolException(
            $"Pipelined STAT reply echoed <{echoed.ToString()}> but expected <{expected}>; " +
            "connection response FIFO is desynchronized.");
    }

    private async Task<Exception?> TryDrainPipelinedStatsAsync(int responseCount)
    {
        try
        {
            for (var index = 0; index < responseCount; index++)
            {
                using var operationCts = CreateOperationTokenSource(CancellationToken.None);
                var response = await ReadLineAsync(operationCts.Token).ConfigureAwait(false);
                if (response == null)
                {
                    return new UsenetProtocolException(
                        "The NNTP connection closed while draining pipelined STAT replies.");
                }

                var responseCode = ParseResponseCode(response);
                if (IsMultiLineCode(responseCode))
                {
                    var unexpectedDrain = await TryDrainBodyAsync().ConfigureAwait(false);
                    if (unexpectedDrain != null)
                    {
                        return unexpectedDrain;
                    }
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
