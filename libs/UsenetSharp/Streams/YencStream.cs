using System.Buffers;
using System.Buffers.Text;
using System.Text;
using RapidYencSharp;
using UsenetSharp.Models;

namespace UsenetSharp.Streams;

/// <summary>
/// A high-performance read-only stream that decodes yEnc-encoded content from an inner stream.
/// Uses chunked buffered reading and zero-allocation Span-based decoding.
/// </summary>
public class YencStream : FastReadOnlyNonSeekableStream
{
    private readonly Stream _innerStream;
    private readonly bool _leaveOpen;
    private readonly Task<UsenetYencHeader?>? _predecodedHeaders;

    // Header state
    private bool _headersRead;
    private UsenetYencHeader? _yencHeaders;

    // Read buffer for chunked reading from stream (8KB chunks)
    private byte[]? _readBuffer;
    private int _readBufferPosition;
    private int _readBufferLength;

    // Decode buffer for decoded line data
    private byte[]? _decodeBuffer;
    private int _decodeBufferPosition;
    private int _decodeBufferLength;

    // Line assembly buffer for lines spanning chunk boundaries
    private byte[]? _lineAssemblyBuffer;
    private int _lineAssemblyLength;

    // Decoder state for tracking escape sequences across lines
    private RapidYencDecoderState? _decoderState;

    private bool _endReached;

    private const int ReadBufferSize = 8192; // 8KB read chunks for efficient I/O
    private const int DecodeBufferSize = 512; // Typical yEnc line decodes to ~128 bytes
    private const int LineAssemblyBufferSize = 1024;
    private const int MaximumLineLength = 1024 * 1024;

    public YencStream(Stream innerStream, bool leaveOpen = false)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _leaveOpen = leaveOpen;
        _headersRead = false;
        _readBufferPosition = 0;
        _readBufferLength = 0;
        _decodeBufferPosition = 0;
        _decodeBufferLength = 0;
        _lineAssemblyLength = 0;
        _endReached = false;

        // Rent all buffers from ArrayPool for zero-allocation
        _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        _decodeBuffer = ArrayPool<byte>.Shared.Rent(DecodeBufferSize);
        _lineAssemblyBuffer = ArrayPool<byte>.Shared.Rent(LineAssemblyBufferSize);
    }

    internal YencStream(
        Stream decodedStream,
        Task<UsenetYencHeader?> predecodedHeaders)
    {
        _innerStream = decodedStream;
        _predecodedHeaders = predecodedHeaders;
    }

    /// <summary>
    /// Gets the yEnc headers from the stream. If headers haven't been read yet, reads and parses them asynchronously.
    /// </summary>
    public virtual async ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (_predecodedHeaders != null)
        {
            return await _predecodedHeaders.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_headersRead)
        {
            await ParseHeadersAsync(cancellationToken);
            _headersRead = true;
        }

        return _yencHeaders;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_predecodedHeaders != null)
        {
            return await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        // Parse headers on first read
        if (!_headersRead)
        {
            await ParseHeadersAsync(cancellationToken);
            _headersRead = true;
        }

        if (_endReached && _decodeBufferPosition >= _decodeBufferLength)
        {
            return 0; // End of stream
        }

        int totalRead = 0;

        while (totalRead < buffer.Length && !_endReached)
        {
            // Serve from decode buffer if we have leftover data
            if (_decodeBufferPosition < _decodeBufferLength)
            {
                int bytesToCopy = Math.Min(buffer.Length - totalRead, _decodeBufferLength - _decodeBufferPosition);
                _decodeBuffer.AsSpan(_decodeBufferPosition, bytesToCopy).CopyTo(buffer.Span.Slice(totalRead));
                _decodeBufferPosition += bytesToCopy;
                totalRead += bytesToCopy;
            }
            else
            {
                // Need to decode next line
                var lineMemory = await ReadNextLineAsync(cancellationToken);

                if (!lineMemory.HasValue)
                {
                    _endReached = true;
                    break;
                }

                var lineSpan = lineMemory.Value.Span;
                if (lineSpan.IsEmpty)
                {
                    continue;
                }

                // Check for =yend marker
                if (StartsWithYEnd(lineSpan))
                {
                    _endReached = true;
                    break;
                }

                int remainingBufferSpace = buffer.Length - totalRead;

                // Optimization: decode directly into caller's buffer if there's enough space
                // Typical decoded yEnc line is ~128 bytes (from ~170 byte encoded line)
                if (remainingBufferSpace >= lineSpan.Length)
                {
                    // Decode directly to caller's buffer - ZERO COPY!
                    int decodedLength = YencDecoder.DecodeEx(
                        lineSpan, buffer.Span.Slice(totalRead), ref _decoderState, isRaw: false);
                    totalRead += decodedLength;
                }
                else
                {
                    // Not enough space - decode to intermediate buffer and copy what fits
                    EnsureDecodeCapacity(lineSpan.Length);
                    int decodedLength = YencDecoder.DecodeEx(lineSpan, _decodeBuffer, ref _decoderState, isRaw: false);
                    _decodeBufferPosition = 0;
                    _decodeBufferLength = decodedLength;
                    // Next iteration will copy from decode buffer
                }
            }
        }

        return totalRead;
    }

    /// <summary>
    /// Reads the next line from the stream using buffered chunked reading.
    /// Handles lines spanning multiple read chunks efficiently.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>?> ReadNextLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Ensure we have data in read buffer
            if (_readBufferPosition >= _readBufferLength)
            {
                bool hasMoreData = await FillReadBufferAsync(cancellationToken);
                if (!hasMoreData && _lineAssemblyLength == 0)
                {
                    return null;
                }

                if (!hasMoreData)
                {
                    // Return partial line at EOF
                    var result = new ReadOnlyMemory<byte>(_lineAssemblyBuffer, 0, _lineAssemblyLength);
                    _lineAssemblyLength = 0;
                    return result;
                }
            }

            // Scan for line ending in current buffer
            var searchSpan = _readBuffer.AsSpan(_readBufferPosition, _readBufferLength - _readBufferPosition);
            int lfIndex = searchSpan.IndexOf((byte)'\n');

            if (lfIndex >= 0)
            {
                // Found complete line
                int lineEndPos = _readBufferPosition + lfIndex;
                int lineStartPos = _readBufferPosition;

                // Check for CRLF vs LF
                int lineLength = lfIndex;
                if (lfIndex > 0 && searchSpan[lfIndex - 1] == (byte)'\r')
                {
                    lineLength--; // Exclude CR
                }

                _readBufferPosition = lineEndPos + 1; // Move past LF

                // If we have a partial line in assembly buffer, combine them
                if (_lineAssemblyLength > 0)
                {
                    EnsureLineAssemblyCapacity(_lineAssemblyLength + lineLength);
                    searchSpan.Slice(0, lineLength).CopyTo(_lineAssemblyBuffer.AsSpan(_lineAssemblyLength));
                    int totalLength = _lineAssemblyLength + lineLength;
                    _lineAssemblyLength = 0;
                    return new ReadOnlyMemory<byte>(_lineAssemblyBuffer, 0, totalLength);
                }
                else
                {
                    // Return line directly from read buffer
                    return new ReadOnlyMemory<byte>(_readBuffer, lineStartPos, lineLength);
                }
            }
            else
            {
                // No line ending in current buffer - save to assembly buffer and read more
                int remainingLength = _readBufferLength - _readBufferPosition;
                EnsureLineAssemblyCapacity(_lineAssemblyLength + remainingLength);
                searchSpan.CopyTo(_lineAssemblyBuffer.AsSpan(_lineAssemblyLength));
                _lineAssemblyLength += remainingLength;
                _readBufferPosition = _readBufferLength; // Consumed entire buffer

                // Continue loop to read more data
            }
        }
    }

    private void EnsureLineAssemblyCapacity(int requiredLength)
    {
        if (requiredLength > MaximumLineLength)
        {
            throw new InvalidDataException(
                $"yEnc line exceeded the {MaximumLineLength}-byte safety limit.");
        }

        if (_lineAssemblyBuffer!.Length >= requiredLength)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Min(MaximumLineLength, Math.Max(requiredLength, _lineAssemblyBuffer.Length * 2)));
        _lineAssemblyBuffer.AsSpan(0, _lineAssemblyLength).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_lineAssemblyBuffer);
        _lineAssemblyBuffer = replacement;
    }

    private void EnsureDecodeCapacity(int requiredLength)
    {
        if (_decodeBuffer!.Length >= requiredLength)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(requiredLength);
        ArrayPool<byte>.Shared.Return(_decodeBuffer);
        _decodeBuffer = replacement;
    }

    /// <summary>
    /// Fills the read buffer with data from the inner stream.
    /// Returns true if data was read, false if EOF.
    /// </summary>
    private async ValueTask<bool> FillReadBufferAsync(CancellationToken cancellationToken)
    {
        _readBufferPosition = 0;
        _readBufferLength = await _innerStream.ReadAsync(_readBuffer.AsMemory(0, ReadBufferSize), cancellationToken);
        return _readBufferLength > 0;
    }

    private async Task ParseHeadersAsync(CancellationToken cancellationToken)
    {
        byte[]? ybeginBuffer = null;
        var ybeginLength = 0;
        try
        {
            // Read lines until we find =ybegin (skip empty lines that may appear before it)
            while (true)
            {
                var lineMemory = await ReadNextLineAsync(cancellationToken);

                if (!lineMemory.HasValue)
                {
                    throw new InvalidDataException("Reached end of stream without finding =ybegin header");
                }

                if (lineMemory.Value.IsEmpty)
                {
                    continue;
                }

                var lineSpan = lineMemory.Value.Span;
                if (StartsWithYBegin(lineSpan))
                {
                    ybeginBuffer = ArrayPool<byte>.Shared.Rent(lineSpan.Length);
                    lineSpan.CopyTo(ybeginBuffer);
                    ybeginLength = lineSpan.Length;
                    break;
                }
            }

            // Check if next line is =ypart or encoded data
            var nextLineMemory = await ReadNextLineAsync(cancellationToken);
            var nextLineSpan = nextLineMemory.HasValue
                ? nextLineMemory.Value.Span
                : ReadOnlySpan<byte>.Empty;
            ReadOnlySpan<byte> ypartLine = default;
            if (!nextLineSpan.IsEmpty)
            {
                if (StartsWithYPart(nextLineSpan))
                {
                    ypartLine = nextLineSpan;
                    // Next line will be encoded data, ReadAsync will handle it
                }
                else if (!StartsWithYEnd(nextLineSpan))
                {
                    // This is the first encoded data line - decode it now
                    EnsureDecodeCapacity(nextLineSpan.Length);
                    int decodedLength = YencDecoder.DecodeEx(
                        nextLineSpan, _decodeBuffer!, ref _decoderState, isRaw: false);
                    _decodeBufferPosition = 0;
                    _decodeBufferLength = decodedLength;
                }
            }

            _yencHeaders = ParseYencHeaders(
                ybeginBuffer.AsSpan(0, ybeginLength), ypartLine);
        }
        finally
        {
            if (ybeginBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(ybeginBuffer);
            }
        }
    }

    internal static bool StartsWithYBegin(ReadOnlySpan<byte> line) =>
        line.Length >= 7 && line.Slice(0, 7).SequenceEqual("=ybegin"u8);

    internal static bool StartsWithYPart(ReadOnlySpan<byte> line) =>
        line.Length >= 6 && line.Slice(0, 6).SequenceEqual("=ypart"u8);

    internal static bool StartsWithYEnd(ReadOnlySpan<byte> line) =>
        line.Length >= 5 && line.Slice(0, 5).SequenceEqual("=yend"u8);

    internal static UsenetYencHeader ParseYencHeaders(
        ReadOnlySpan<byte> ybeginLine,
        ReadOnlySpan<byte> ypartLine = default)
    {
        // Parse =ybegin line
        // Format: =ybegin part=123 total=123 line=123 size=123 name=filename.bin
        int lineLength = 128; // default
        long fileSize = 0;
        string fileName = string.Empty;
        int partNumber = 0;
        int totalParts = 0;

        var ybeginFields = ybeginLine[7..];
        foreach (var tokenRange in ybeginFields.Split((byte)' '))
        {
            ParseYbeginToken(
                ybeginFields[tokenRange],
                ref lineLength,
                ref fileSize,
                ref fileName,
                ref partNumber,
                ref totalParts);
        }

        // Parse =ypart line if present
        // Format: =ypart begin=1 end=123456
        long partSize = fileSize;
        long partOffset = 0;

        if (!ypartLine.IsEmpty)
        {
            long partBegin = 0;
            long partEnd = 0;

            var ypartFields = ypartLine[6..];
            foreach (var tokenRange in ypartFields.Split((byte)' '))
            {
                ParseYpartToken(ypartFields[tokenRange], ref partBegin, ref partEnd);
            }

            partOffset = partBegin - 1; // yEnc uses 1-based indexing
            partSize = partEnd - partBegin + 1;
        }

        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = fileSize,
            LineLength = lineLength,
            PartNumber = partNumber,
            TotalParts = totalParts,
            PartSize = partSize,
            PartOffset = partOffset
        };
    }

    private static void ParseYbeginToken(
        ReadOnlySpan<byte> token,
        ref int lineLength,
        ref long fileSize,
        ref string fileName,
        ref int partNumber,
        ref int totalParts)
    {
        var separator = token.IndexOf((byte)'=');
        if (separator <= 0)
        {
            return;
        }

        var key = token[..separator];
        var value = token[(separator + 1)..];
        if (key.SequenceEqual("line"u8))
        {
            lineLength = ParseInt32(value);
        }
        else if (key.SequenceEqual("size"u8))
        {
            fileSize = ParseInt64(value);
        }
        else if (key.SequenceEqual("name"u8))
        {
            fileName = Encoding.Latin1.GetString(value);
        }
        else if (key.SequenceEqual("part"u8))
        {
            partNumber = ParseInt32(value);
        }
        else if (key.SequenceEqual("total"u8))
        {
            totalParts = ParseInt32(value);
        }
    }

    private static void ParseYpartToken(
        ReadOnlySpan<byte> token,
        ref long partBegin,
        ref long partEnd)
    {
        var separator = token.IndexOf((byte)'=');
        if (separator <= 0)
        {
            return;
        }

        var key = token[..separator];
        var value = token[(separator + 1)..];
        if (key.SequenceEqual("begin"u8))
        {
            partBegin = ParseInt64(value);
        }
        else if (key.SequenceEqual("end"u8))
        {
            partEnd = ParseInt64(value);
        }
    }

    private static int ParseInt32(ReadOnlySpan<byte> value)
    {
        return Utf8Parser.TryParse(value, out int parsed, out var consumed) &&
               consumed == value.Length
            ? parsed
            : 0;
    }

    private static long ParseInt64(ReadOnlySpan<byte> value)
    {
        return Utf8Parser.TryParse(value, out long parsed, out var consumed) &&
               consumed == value.Length
            ? parsed
            : 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Return all buffers to ArrayPool
            if (_readBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_readBuffer);
                _readBuffer = null;
            }

            if (_decodeBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_decodeBuffer);
                _decodeBuffer = null;
            }

            if (_lineAssemblyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_lineAssemblyBuffer);
                _lineAssemblyBuffer = null;
            }

            if (!_leaveOpen)
            {
                _innerStream.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
