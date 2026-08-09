using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private const int DecodedBodyChunkSize = 64 * 1024;
    private const int RawBodyFlushThreshold = 8 * 1024;

    public Task<UsenetBodyResponse> BodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return BodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetBodyResponse> BodyAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        CancellationTokenSource? operationCts = null;

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            #pragma warning disable CA2000 // operationCts ownership transfers to the background body-read task (which disposes it on completion) via the `operationCts = null` handoff below; the finally disposes it only on non-transferred paths
            operationCts = CreateOperationTokenSource(cancellationToken);
            #pragma warning restore CA2000
            using var ioTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, operationCts.Token);

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                segmentId,
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("BODY", id, timeout))
                .ConfigureAwait(false);

            // Article retrieved - body follows
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                // Create a pipe for streaming the body data
                var pipe = new Pipe(RawBodyPipeOptions);

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                #pragma warning disable CA2025 // the background body-read task owns and disposes the pipe writer and operationCts; the caller awaits/disposes the reader side
                _ = ReadBodyToPipeAsync(
                                    pipe.Writer, operationCts, onConnectionReadyAgain, cancellationToken);
                #pragma warning restore CA2025
                operationCts = null;

                // Return immediately with the stream and headers
                return new UsenetBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                .ConfigureAwait(false);

            return new UsenetBodyResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts?.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    public Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        CancellationTokenSource? operationCts = null;

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            #pragma warning disable CA2000 // operationCts ownership transfers to the background body-read task (which disposes it on completion) via the `operationCts = null` handoff below; the finally disposes it only on non-transferred paths
            operationCts = CreateOperationTokenSource(cancellationToken);
            #pragma warning restore CA2000
            using var ioTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, operationCts.Token);

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                segmentId,
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("BODY", id, timeout))
                .ConfigureAwait(false);

            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                var pipe = new Pipe(_decodedBodyPipeOptions);
                var decodedStream = new DecodedBodyReadStream(
                    pipe.Reader.AsStream(), AdjustBufferedDecodedBodyBytes);
                var headersCompletion =
                    new TaskCompletionSource<UsenetYencHeader?>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                isReadBodyToPipeAsyncStarted = true;
                #pragma warning disable CA2025 // the background decode task owns and disposes the pipe writer, operationCts and decoded stream; completion is coordinated through headersCompletion and the response callbacks
                _ = ReadDecodedBodyToPipeAsync(
                                    pipe.Writer,
                    headersCompletion,
                    operationCts,
                    onConnectionReadyAgain,
                    decodedStream,
                    callerCancellationToken: cancellationToken);
                #pragma warning restore CA2025
                operationCts = null;

                return new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    Stream = new YencStream(decodedStream, headersCompletion.Task),
                };
            }

            await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                .ConfigureAwait(false);

            return new UsenetDecodedBodyResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts?.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task<DecodedBodyReadResult> ReadDecodedBodyToPipeAsync(
        PipeWriter writer,
        TaskCompletionSource<UsenetYencHeader?> headersCompletion,
        CancellationTokenSource operationCts,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        DecodedBodyReadStream decodedStream,
        bool releaseCommandLock = true,
        CoalescedReadTimeout? sharedReadTimeout = null,
        BatchDecodeBuffer? sharedEncodedBuffer = null,
        CancellationToken callerCancellationToken = default)
    {
        Exception? failure = null;
        var connectionReusable = true;
        byte[]? encodedBuffer = null;
        byte[]? ybeginBuffer = null;
        CoalescedReadTimeout? ownedReadTimeout = null;
        var ownsEncodedBuffer = sharedEncodedBuffer == null;
        try
        {
            if (_reader == null)
            {
                throw new UsenetNotConnectedException(
                    "The NNTP connection closed before the article body was read.");
            }

            if (sharedEncodedBuffer != null)
            {
                encodedBuffer = sharedEncodedBuffer.Buffer;
            }
            else
            {
                encodedBuffer = ArrayPool<byte>.Shared.Rent(DecodedBodyChunkSize + 2);
            }

            var encodedLength = 0;
            var shouldWrite = true;
            var dataEnded = false;
            var headersRead = false;
            var isMultipart = false;
            long drainedBytes = 0;
            long skippedBytes = 0;
            var ybeginLength = 0;
            RapidYencDecoderState? decoderState = RapidYencDecoderState.RYDEC_STATE_CRLF;
            uint decodedCrc32 = 0;
            var cancellationToken = operationCts.Token;
            if (sharedReadTimeout == null)
            {
                ownedReadTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, cancellationToken);
            }

            var readTimeout = sharedReadTimeout ?? ownedReadTimeout!;

            while (true)
            {
                ReadOnlyMemory<byte>? lineMemory;
                try
                {
                    lineMemory = await ReadLineBytesAsync(readTimeout)
                        .ConfigureAwait(false);
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
                if (lineBytes.Length == 1 && lineBytes.Span[0] == (byte)'.')
                {
                    if (!headersRead)
                    {
                        if (ybeginBuffer == null)
                        {
                            throw new InvalidDataException(
                                "Reached end of NNTP body without finding =ybegin header.");
                        }

                        headersCompletion.TrySetResult(
                            YencStream.ParseYencHeaders(ybeginBuffer.AsSpan(0, ybeginLength)));
                    }

                    if (shouldWrite && encodedLength > 0)
                    {
                        var flush = await DecodeAndFlushAsync(
                            writer,
                            encodedBuffer.AsMemory(0, encodedLength),
                            decoderState,
                            decodedCrc32,
                            _options.CrcValidation != YencCrcValidationMode.Off,
                            decodedStream,
                            cancellationToken).ConfigureAwait(false);
                        decoderState = flush.DecoderState;
                        decodedCrc32 = flush.Crc32;
                    }

                    if (_options.CrcValidation == YencCrcValidationMode.Require && !dataEnded)
                    {
                        throw new InvalidDataException(
                            "Reached end of NNTP body without finding a yEnc trailer.");
                    }

                    break;
                }

                if (!shouldWrite)
                {
                    drainedBytes += lineBytes.Length + 2;
                    if (drainedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The abandoned NNTP body exceeded the configured drain limit.");
                    }

                    continue;
                }

                if (dataEnded)
                {
                    skippedBytes += lineBytes.Length + 2;
                    if (skippedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The NNTP body contained more non-yEnc data than the configured drain limit.");
                    }

                    continue;
                }

                if (!headersRead)
                {
                    if (ybeginBuffer == null)
                    {
                        if (YencStream.StartsWithYBegin(lineBytes.Span))
                        {
                            ybeginBuffer = ArrayPool<byte>.Shared.Rent(lineBytes.Length);
                            lineBytes.Span.CopyTo(ybeginBuffer);
                            ybeginLength = lineBytes.Length;
                        }
                        else
                        {
                            skippedBytes += lineBytes.Length + 2;
                            if (skippedBytes > _options.AbandonedBodyDrainLimit)
                            {
                                throw new UsenetProtocolException(
                                    "The NNTP body contained more non-yEnc data than the configured drain limit.");
                            }
                        }

                        continue;
                    }

                    if (YencStream.StartsWithYPart(lineBytes.Span))
                    {
                        headersRead = true;
                        isMultipart = true;
                        headersCompletion.TrySetResult(
                            YencStream.ParseYencHeaders(
                                ybeginBuffer.AsSpan(0, ybeginLength), lineBytes.Span));
                        continue;
                    }

                    headersRead = true;
                    headersCompletion.TrySetResult(
                        YencStream.ParseYencHeaders(ybeginBuffer.AsSpan(0, ybeginLength)));
                }

                if (YencStream.StartsWithYEnd(lineBytes.Span))
                {
                    dataEnded = true;
                    if (encodedLength > 0)
                    {
                        var flush = await DecodeAndFlushAsync(
                            writer,
                            encodedBuffer.AsMemory(0, encodedLength),
                            decoderState,
                            decodedCrc32,
                            _options.CrcValidation != YencCrcValidationMode.Off,
                            decodedStream,
                            cancellationToken).ConfigureAwait(false);
                        encodedLength = 0;
                        decoderState = flush.DecoderState;
                        decodedCrc32 = flush.Crc32;
                        shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                    }

                    if (_options.CrcValidation != YencCrcValidationMode.Off && shouldWrite)
                    {
                        ValidateDecodedBodyCrc32(
                            lineBytes.Span, isMultipart, decodedCrc32, _options.CrcValidation);
                    }

                    continue;
                }

                var requiredLength = lineBytes.Length + 2;
                if (encodedLength > 0 &&
                    encodedLength + requiredLength > encodedBuffer.Length)
                {
                    var flush = await DecodeAndFlushAsync(
                        writer,
                        encodedBuffer.AsMemory(0, encodedLength),
                        decoderState,
                        decodedCrc32,
                        _options.CrcValidation != YencCrcValidationMode.Off,
                        decodedStream,
                        cancellationToken).ConfigureAwait(false);
                    encodedLength = 0;
                    decoderState = flush.DecoderState;
                    decodedCrc32 = flush.Crc32;
                    shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                    if (!shouldWrite)
                    {
                        continue;
                    }
                }

                if (requiredLength > encodedBuffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(encodedBuffer);
                    encodedBuffer = ArrayPool<byte>.Shared.Rent(requiredLength);
                    if (sharedEncodedBuffer != null)
                    {
                        sharedEncodedBuffer.Buffer = encodedBuffer;
                    }
                }

                lineBytes.Span.CopyTo(encodedBuffer.AsSpan(encodedLength));
                encodedLength += lineBytes.Length;
                encodedBuffer[encodedLength++] = (byte)'\r';
                encodedBuffer[encodedLength++] = (byte)'\n';

                if (encodedLength >= DecodedBodyChunkSize)
                {
                    var flush = await DecodeAndFlushAsync(
                        writer,
                        encodedBuffer.AsMemory(0, encodedLength),
                        decoderState,
                        decodedCrc32,
                        _options.CrcValidation != YencCrcValidationMode.Off,
                        decodedStream,
                        cancellationToken).ConfigureAwait(false);
                    encodedLength = 0;
                    decoderState = flush.DecoderState;
                    decodedCrc32 = flush.Crc32;
                    shouldWrite = !flush.Result.IsCompleted && !flush.Result.IsCanceled;
                }
            }
        }
        catch (OperationCanceledException e) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = e;
            if (_options.CancellationPolicy == ConnectionReleasePolicy.AbandonConnection)
            {
                connectionReusable = false;
                RecordConnectionFailure(e);
            }
            else
            {
                var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
                if (drainFailure != null)
                {
                    connectionReusable = false;
                    RecordConnectionFailure(drainFailure);
                }
            }
        }
        catch (Exception e)
        {
            failure = e;
            connectionReusable = false;
            RecordConnectionFailure(e);
        }
        finally
        {
            if (ownsEncodedBuffer && encodedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(encodedBuffer);
            }

            if (ybeginBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(ybeginBuffer);
            }

            if (failure != null)
            {
                headersCompletion.TrySetException(failure);
            }

            await writer.CompleteAsync(failure).ConfigureAwait(false);
            ownedReadTimeout?.Dispose();
            if (sharedReadTimeout == null)
            {
                operationCts.Dispose();
            }

            if (releaseCommandLock)
            {
                _commandLock.Release();
            }

            try
            {
                var result = failure switch
                {
                    null => ArticleBodyResult.Retrieved,
                    OperationCanceledException when connectionReusable =>
                        ArticleBodyResult.Cancelled,
                    _ => ArticleBodyResult.NotRetrieved
                };
                onConnectionReadyAgain?.Invoke(
                    result,
                    result == ArticleBodyResult.NotRetrieved ? DescribeFailure(failure) : null);
            }
            catch
            {
                // User callbacks must not fault the unobserved background transfer task.
            }
        }

        return new DecodedBodyReadResult(failure, connectionReusable);
    }

    private sealed class BatchDecodeBuffer
    {
        public required byte[] Buffer;
    }

    private readonly record struct DecodedBodyReadResult(
        Exception? Failure,
        bool ConnectionReusable);

    private static async ValueTask<(
        FlushResult Result,
        RapidYencDecoderState? DecoderState,
        uint Crc32)> DecodeAndFlushAsync(
        PipeWriter writer,
        ReadOnlyMemory<byte> encoded,
        RapidYencDecoderState? decoderState,
        uint crc32,
        bool computeCrc32,
        DecodedBodyReadStream decodedStream,
        CancellationToken cancellationToken)
    {
        var destination = writer.GetSpan(encoded.Length);
        var decodedLength = YencDecoder.DecodeEx(
            encoded.Span, destination, ref decoderState, isRaw: true);
        if (computeCrc32)
        {
            crc32 = Crc32.Compute(destination[..decodedLength], crc32);
        }

        decodedStream.AddBufferedBytes(decodedLength);
        writer.Advance(decodedLength);
        var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (result, decoderState, crc32);
    }

    private void AdjustBufferedDecodedBodyBytes(long delta)
    {
        Interlocked.Add(ref _bufferedDecodedBodyBytes, delta);
    }

    private static void ValidateDecodedBodyCrc32(
        ReadOnlySpan<byte> yendLine,
        bool isMultipart,
        uint actualCrc32,
        YencCrcValidationMode mode)
    {
        var fieldName = isMultipart ? "pcrc32"u8 : "crc32"u8;
        if (!TryParseYencTrailerCrc32(yendLine, fieldName, out var expectedCrc32))
        {
            if (mode == YencCrcValidationMode.WhenPresent)
            {
                return;
            }

            throw new InvalidDataException(
                $"The yEnc trailer does not contain a valid {Encoding.ASCII.GetString(fieldName)} value.");
        }

        if (actualCrc32 != expectedCrc32)
        {
            throw new InvalidDataException(
                $"The decoded yEnc CRC32 was {actualCrc32:x8}, but the trailer expected {expectedCrc32:x8}.");
        }
    }

    private static bool TryParseYencTrailerCrc32(
        ReadOnlySpan<byte> trailer,
        ReadOnlySpan<byte> fieldName,
        out uint crc32)
    {
        var position = 0;
        while (position < trailer.Length)
        {
            while (position < trailer.Length && IsAsciiWhitespace(trailer[position]))
            {
                position++;
            }

            var tokenStart = position;
            while (position < trailer.Length && !IsAsciiWhitespace(trailer[position]))
            {
                position++;
            }

            var token = trailer[tokenStart..position];
            var separator = token.IndexOf((byte)'=');
            if (separator <= 0 ||
                !AsciiEqualsIgnoreCase(token[..separator], fieldName))
            {
                continue;
            }

            var value = token[(separator + 1)..];
            return Utf8Parser.TryParse(value, out crc32, out var consumed, 'X') &&
                consumed == value.Length;
        }

        crc32 = 0;
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];
            if (leftValue is >= (byte)'A' and <= (byte)'Z')
            {
                leftValue += (byte)('a' - 'A');
            }

            if (rightValue is >= (byte)'A' and <= (byte)'Z')
            {
                rightValue += (byte)('a' - 'A');
            }

            if (leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private async Task ReadBodyToPipeAsync(
        PipeWriter writer,
        CancellationTokenSource operationCts,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken callerCancellationToken)
    {
        Exception? failure = null;
        try
        {
            if (_reader == null)
            {
                throw new UsenetNotConnectedException("The NNTP connection closed before the article body was read.");
            }

            var shouldWrite = true;
            long drainedBytes = 0;
            var unflushed = 0;
            var cancellationToken = operationCts.Token;
            using var readTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, cancellationToken);

            // Read lines until we encounter the termination sequence (single dot on a line)
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

                var line = lineMemory.Value.Span;

                // Check for NNTP termination sequence (single dot)
                if (line.Length == 1 && line[0] == (byte)'.')
                {
                    break;
                }

                if (!shouldWrite)
                {
                    drainedBytes += line.Length + 2;
                    if (drainedBytes > _options.AbandonedBodyDrainLimit)
                    {
                        throw new UsenetProtocolException(
                            "The abandoned NNTP body exceeded the configured drain limit.");
                    }

                    continue;
                }

                // NNTP escaping: Lines starting with ".." should have the first dot removed
                if (line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.')
                {
                    line = line[1..];
                }

                // Copy protocol bytes directly so yEnc data never round-trips through UTF-16.
                var destination = writer.GetSpan(line.Length + 2);
                line.CopyTo(destination);
                destination[line.Length] = (byte)'\r';
                destination[line.Length + 1] = (byte)'\n';
                writer.Advance(line.Length + 2);
                unflushed += line.Length + 2;

                // Batch flushes (~8 KiB) to cut per-line FlushAsync overhead while keeping
                // first-byte latency acceptable for small-header readers.
                if (unflushed >= RawBodyFlushThreshold)
                {
                    var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    unflushed = 0;
                    if (result.IsCompleted || result.IsCanceled)
                    {
                        shouldWrite = false;
                    }
                }
            }

            if (shouldWrite && unflushed > 0)
            {
                var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsCompleted || result.IsCanceled)
                {
                    shouldWrite = false;
                }
            }
        }
        catch (OperationCanceledException e) when (callerCancellationToken.IsCancellationRequested)
        {
            failure = e;
            if (_options.CancellationPolicy == ConnectionReleasePolicy.AbandonConnection)
            {
                RecordConnectionFailure(e);
            }
            else
            {
                var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
                if (drainFailure != null)
                {
                    RecordConnectionFailure(drainFailure);
                }
            }
        }
        catch (Exception e)
        {
            failure = e;
            RecordConnectionFailure(e);
        }
        finally
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
            operationCts.Dispose();
            _commandLock.Release();
            try
            {
                onConnectionReadyAgain?.Invoke(
                    failure == null ? ArticleBodyResult.Retrieved : ArticleBodyResult.NotRetrieved,
                    failure == null ? null : DescribeFailure(failure));
            }
            catch
            {
                // User callbacks must not fault the unobserved background transfer task.
            }
        }
    }

    private async Task<Exception?> TryDrainBodyAsync()
    {
        try
        {
            using var drainCts = CreateOperationTokenSource(CancellationToken.None);
            using var readTimeout = new CoalescedReadTimeout(_options.ReadTimeout, _timeProvider, drainCts.Token);
            long drainedBytes = 0;

            while (true)
            {
                ReadOnlyMemory<byte>? line;
                try
                {
                    line = await ReadLineBytesAsync(readTimeout).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return new UsenetProtocolException(
                        "The NNTP connection closed while draining a cancelled body.");
                }

                if (!line.HasValue)
                {
                    return new UsenetProtocolException(
                        "The NNTP connection closed while draining a cancelled body.");
                }

                var bytes = line.Value.Span;
                if (bytes.Length == 1 && bytes[0] == (byte)'.')
                {
                    return null;
                }

                drainedBytes += bytes.Length + 2;
                if (drainedBytes > _options.AbandonedBodyDrainLimit)
                {
                    return new UsenetProtocolException(
                        "The cancelled NNTP body exceeded the configured drain limit.");
                }
            }
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private void RecordConnectionFailure(Exception failure)
    {
        lock (_stateLock)
        {
            _backgroundException = ExceptionDispatchInfo.Capture(failure);
        }
    }
}
