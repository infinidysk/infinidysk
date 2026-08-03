using System.Buffers;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    /// <summary>
    /// Probes a segment's yEnc headers (<c>=ybegin</c>/<c>=ypart</c>) via BODY,
    /// draining the remaining article so the connection stays reusable.
    /// </summary>
    public Task<UsenetYencHeaderResponse> YencHeadersAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return YencHeadersAsync(
            segmentId, ConnectionReleasePolicy.DrainToReuse, cancellationToken);
    }

    /// <summary>
    /// Probes a segment's yEnc headers (<c>=ybegin</c>/<c>=ypart</c>) via BODY,
    /// releasing the connection according to <paramref name="releasePolicy"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectionReleasePolicy.DrainToReuse"/> downloads and discards
    /// the remaining article (bounded by
    /// <see cref="UsenetClientOptions.AbandonedBodyDrainLimit"/>).
    /// <see cref="ConnectionReleasePolicy.AbandonConnection"/> returns after the
    /// header lines and marks the connection unusable (<see cref="IsHealthy"/>
    /// becomes <see langword="false"/>); the owner should dispose and reconnect.
    /// There is no background transfer: the connection lease is released when
    /// this method returns.
    /// </remarks>
    public async Task<UsenetYencHeaderResponse> YencHeadersAsync(
        SegmentId segmentId,
        ConnectionReleasePolicy releasePolicy,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            using var operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                segmentId,
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("BODY", id, timeout))
                .ConfigureAwait(false);

            if (responseCode != (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                    .ConfigureAwait(false);
                return new UsenetYencHeaderResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    YencHeader = null,
                };
            }

            UsenetYencHeader? header;
            try
            {
                header = await ReadYencHeaderProbeAsync(releasePolicy, operationCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Unread body data remains buffered; the response FIFO cannot
                // be trusted (RFC 3977 §3.5).
                RecordConnectionFailure(e);
                throw;
            }

            return new UsenetYencHeaderResponse
            {
                SegmentId = segmentId,
                ResponseCode = responseCode,
                ResponseMessage = response,
                YencHeader = header,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<UsenetYencHeader?> ReadYencHeaderProbeAsync(
        ConnectionReleasePolicy releasePolicy,
        CancellationToken cancellationToken)
    {
        byte[]? ybeginBuffer = null;
        try
        {
            using var readTimeout = new CoalescedReadTimeout(
                cancellationToken, _options.ReadTimeout, _timeProvider);
            var ybeginLength = 0;
            long skippedBytes = 0;

            while (true)
            {
                ReadOnlyMemory<byte>? lineMemory;
                try
                {
                    lineMemory = await ReadLineBytesAsync(readTimeout).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                if (!lineMemory.HasValue)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                var lineBytes = lineMemory.Value;

                // Terminator before any yEnc content: clean non-yEnc body.
                if (lineBytes.Length == 1 && lineBytes.Span[0] == (byte)'.')
                {
                    return ybeginBuffer == null
                        ? null
                        : YencStream.ParseYencHeaders(ybeginBuffer.AsSpan(0, ybeginLength));
                }

                if (ybeginBuffer == null)
                {
                    if (YencStream.StartsWithYBegin(lineBytes.Span))
                    {
                        ybeginBuffer = ArrayPool<byte>.Shared.Rent(lineBytes.Length);
                        lineBytes.Span.CopyTo(ybeginBuffer);
                        ybeginLength = lineBytes.Length;
                        continue;
                    }

                    skippedBytes += lineBytes.Length + 2;
                    if (skippedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The NNTP body contained more non-yEnc data than the configured drain limit.");
                    }

                    continue;
                }

                // One line after =ybegin decides single-part vs multipart.
                var header = YencStream.StartsWithYPart(lineBytes.Span)
                    ? YencStream.ParseYencHeaders(
                        ybeginBuffer.AsSpan(0, ybeginLength), lineBytes.Span)
                    : YencStream.ParseYencHeaders(ybeginBuffer.AsSpan(0, ybeginLength));

                await ReleaseProbeConnectionAsync(releasePolicy).ConfigureAwait(false);
                return header;
            }
        }
        finally
        {
            if (ybeginBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(ybeginBuffer);
            }
        }
    }

    private async Task ReleaseProbeConnectionAsync(ConnectionReleasePolicy releasePolicy)
    {
        if (releasePolicy == ConnectionReleasePolicy.AbandonConnection)
        {
            // Intentional poison: unread body data remains, so the connection
            // must not be reused. The owner disposes and reconnects.
            RecordConnectionFailure(new UsenetProtocolException(
                "The connection was abandoned by a yEnc header probe " +
                "(ConnectionReleasePolicy.AbandonConnection)."));
            return;
        }

        var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
        if (drainFailure != null)
        {
            // Reuse was requested but the drain overflowed or failed; the
            // header result is still valid, the connection is not.
            RecordConnectionFailure(drainFailure);
        }
    }
}
