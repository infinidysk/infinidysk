using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Clients;

internal sealed class NntpYencBodyDecoder(
    NntpLineReader reader,
    PipeWriter writer,
    TaskCompletionSource<UsenetYencHeader?> headersCompletion,
    DecodedBodyReadStream decodedStream,
    UsenetClientOptions options,
    int flushThreshold)
{
    private enum BodyPhase
    {
        SeekingYBegin,
        AwaitingYPartOrData,
        DecodingData,
        AfterYEnd
    }

    public async Task ReadAsync(
        CoalescedReadTimeout readTimeout,
        CancellationToken cancellationToken = default)
    {
        byte[]? ybeginBuffer = null;
        byte[]? yendBuffer = null;
        try
        {
            var unflushedDecodedBytes = 0;
            var shouldWrite = true;
            var phase = BodyPhase.SeekingYBegin;
            var isMultipart = false;
            long drainedBytes = 0;
            long skippedBytes = 0;
            var ybeginLength = 0;
            var yendLength = 0;
            RapidYencDecoderState? decoderState = RapidYencDecoderState.RYDEC_STATE_CRLF;
            uint decodedCrc32 = 0;

            void DecodePayload(ReadOnlySpan<byte> rawPayload)
            {
                if (!shouldWrite || rawPayload.IsEmpty)
                {
                    return;
                }

                var destination = writer.GetSpan(rawPayload.Length);
                var decodedLength = YencDecoder.DecodeEx(
                    rawPayload, destination, ref decoderState, isRaw: true);
                if (options.CrcValidation != YencCrcValidationMode.Off)
                {
                    decodedCrc32 = Crc32.Compute(destination[..decodedLength], decodedCrc32);
                }

                decodedStream.AddBufferedBytes(decodedLength);
                writer.Advance(decodedLength);
                unflushedDecodedBytes += decodedLength;
            }

            async ValueTask FlushDecodedAsync()
            {
                if (!shouldWrite || unflushedDecodedBytes == 0)
                {
                    return;
                }

                var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                unflushedDecodedBytes = 0;
                shouldWrite = !result.IsCompleted && !result.IsCanceled;
            }

            while (true)
            {
                NntpReadBuffer? batch;
                try
                {
                    batch = await ReadCompleteLinesAsync(readTimeout).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                if (!batch.HasValue)
                {
                    throw new UsenetProtocolException(
                        "The NNTP connection closed before the article body terminator was received.");
                }

                var span = batch.Value.Memory.Span;
                var offset = 0;
                var payloadStart = -1;
                var consume = 0;
                var hitTerminator = false;
                var hitYEnd = false;

                try
                {
                    while (TryReadLine(
                        span,
                        ref offset,
                        out var rawStart,
                        out var rawLength,
                        out var contentStart,
                        out var contentLength))
                    {
                        var content = span.Slice(contentStart, contentLength);
                        if (contentLength == 1 && content[0] == (byte)'.')
                        {
                            if (payloadStart >= 0)
                            {
                                DecodePayload(span[payloadStart..rawStart]);
                            }

                            payloadStart = -1;
                            consume = rawStart + rawLength;
                            if (phase == BodyPhase.SeekingYBegin)
                            {
                                throw new InvalidDataException(
                                    "Reached end of NNTP body without finding =ybegin header.");
                            }

                            if (phase == BodyPhase.AwaitingYPartOrData)
                            {
                                headersCompletion.TrySetResult(
                                    YencStream.ParseYencHeaders(
                                        ybeginBuffer.AsSpan(0, ybeginLength)));
                                phase = BodyPhase.DecodingData;
                            }

                            hitTerminator = true;
                            break;
                        }

                        if (!shouldWrite)
                        {
                            payloadStart = -1;
                            drainedBytes += contentLength + 2;
                            if (drainedBytes > options.AbandonedBodyDrainLimit)
                            {
                                consume = rawStart + rawLength;
                                throw new UsenetProtocolException(
                                    "The abandoned NNTP body exceeded the configured drain limit.");
                            }

                            consume = rawStart + rawLength;
                            continue;
                        }

                        if (phase == BodyPhase.AfterYEnd)
                        {
                            skippedBytes += contentLength + 2;
                            if (skippedBytes > options.AbandonedBodyDrainLimit)
                            {
                                consume = rawStart + rawLength;
                                throw new UsenetProtocolException(
                                    "The NNTP body contained more non-yEnc data than the configured drain limit.");
                            }

                            consume = rawStart + rawLength;
                            continue;
                        }

                        if (phase == BodyPhase.SeekingYBegin)
                        {
                            if (YencStream.StartsWithYBegin(content))
                            {
                                ybeginBuffer = ArrayPool<byte>.Shared.Rent(contentLength);
                                content.CopyTo(ybeginBuffer);
                                ybeginLength = contentLength;
                                phase = BodyPhase.AwaitingYPartOrData;
                            }
                            else
                            {
                                skippedBytes += contentLength + 2;
                                if (skippedBytes > options.AbandonedBodyDrainLimit)
                                {
                                    consume = rawStart + rawLength;
                                    throw new UsenetProtocolException(
                                        "The NNTP body contained more non-yEnc data than the configured drain limit.");
                                }
                            }

                            consume = rawStart + rawLength;
                            continue;
                        }

                        if (phase == BodyPhase.AwaitingYPartOrData)
                        {
                            if (YencStream.StartsWithYPart(content))
                            {
                                consume = rawStart + rawLength;
                                headersCompletion.TrySetResult(
                                    YencStream.ParseYencHeaders(
                                        ybeginBuffer.AsSpan(0, ybeginLength), content));
                                isMultipart = true;
                                phase = BodyPhase.DecodingData;
                                continue;
                            }

                            consume = rawStart + rawLength;
                            headersCompletion.TrySetResult(
                                YencStream.ParseYencHeaders(
                                    ybeginBuffer.AsSpan(0, ybeginLength)));
                            phase = BodyPhase.DecodingData;
                        }

                        if (YencStream.StartsWithYEnd(content))
                        {
                            if (payloadStart >= 0)
                            {
                                DecodePayload(span[payloadStart..rawStart]);
                            }

                            payloadStart = -1;
                            if (yendBuffer != null)
                            {
                                ArrayPool<byte>.Shared.Return(yendBuffer);
                            }

                            yendBuffer = ArrayPool<byte>.Shared.Rent(contentLength);
                            content.CopyTo(yendBuffer);
                            yendLength = contentLength;
                            phase = BodyPhase.AfterYEnd;
                            consume = rawStart + rawLength;
                            hitYEnd = true;
                            break;
                        }

                        if (payloadStart < 0)
                        {
                            payloadStart = rawStart;
                        }

                        consume = rawStart + rawLength;
                    }

                    if (payloadStart >= 0)
                    {
                        DecodePayload(span[payloadStart..consume]);
                    }
                }
                catch
                {
                    if (consume > 0)
                    {
                        reader.Advance(consume);
                    }

                    throw;
                }

                if (consume > 0)
                {
                    reader.Advance(consume);
                }

                if (hitYEnd)
                {
                    await FlushDecodedAsync().ConfigureAwait(false);
                    if (options.CrcValidation != YencCrcValidationMode.Off && shouldWrite)
                    {
                        ValidateDecodedBodyCrc32(
                            yendBuffer.AsSpan(0, yendLength),
                            isMultipart,
                            decodedCrc32,
                            options.CrcValidation);
                    }

                    continue;
                }

                if (unflushedDecodedBytes >= flushThreshold)
                {
                    await FlushDecodedAsync().ConfigureAwait(false);
                }

                if (hitTerminator)
                {
                    await FlushDecodedAsync().ConfigureAwait(false);
                    if (options.CrcValidation == YencCrcValidationMode.Require &&
                        phase != BodyPhase.AfterYEnd)
                    {
                        throw new InvalidDataException(
                            "Reached end of NNTP body without finding a yEnc trailer.");
                    }

                    break;
                }
            }
        }
        finally
        {
            if (ybeginBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(ybeginBuffer);
            }

            if (yendBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(yendBuffer);
            }
        }
    }

    private async ValueTask<NntpReadBuffer?> ReadCompleteLinesAsync(
        CoalescedReadTimeout readTimeout)
    {
        readTimeout.BeginIo();
        try
        {
            return await reader.ReadCompleteLinesAsync(readTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (readTimeout.IsTimeoutCancellation)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
        finally
        {
            readTimeout.EndIo();
        }
    }

    private static bool TryReadLine(
        ReadOnlySpan<byte> batch,
        ref int offset,
        out int rawStart,
        out int rawLength,
        out int contentStart,
        out int contentLength)
    {
        if (offset >= batch.Length)
        {
            rawStart = 0;
            rawLength = 0;
            contentStart = 0;
            contentLength = 0;
            return false;
        }

        var relativeNewline = batch[offset..].IndexOf((byte)'\n');
        if (relativeNewline < 0)
        {
            throw new InvalidOperationException("NNTP batch ended without a line terminator.");
        }

        rawStart = offset;
        rawLength = relativeNewline + 1;
        contentStart = rawStart;
        contentLength = relativeNewline;
        if (contentLength > 0 && batch[contentStart + contentLength - 1] == (byte)'\r')
        {
            contentLength--;
        }

        offset += rawLength;
        return true;
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
}
