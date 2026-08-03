using System.Buffers;
using System.Text;
using UsenetSharp.Exceptions;

namespace UsenetSharp.Clients;

internal sealed class NntpLineReader(Stream stream, int maximumLineLength = 64 * 1024) : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private byte[]? _lineBuffer;
    private int _lineBufferLength;
    private int _position;
    private int _length;
    private bool _disposed;

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineBytesAsync(cancellationToken).ConfigureAwait(false);
        return line.HasValue ? Encoding.Latin1.GetString(line.Value.Span) : null;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadLineBytesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            if (_position >= _length)
            {
                var bytesRead = await stream.ReadAsync(
                        _buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                _length = bytesRead;
                if (_length == 0)
                {
                    if (_lineBufferLength == 0)
                    {
                        return null; // clean EOF at a line boundary
                    }

                    _lineBufferLength = 0;
                    throw new UsenetProtocolException(
                        "The NNTP stream ended with an unterminated line.");
                }
            }

            var available = _buffer.AsSpan(_position, _length - _position);
            var newlineIndex = available.IndexOf((byte)'\n');
            var count = newlineIndex >= 0 ? newlineIndex : available.Length;

            if (_lineBufferLength + count > maximumLineLength)
            {
                throw new UsenetProtocolException(
                    $"NNTP response line exceeded the {maximumLineLength}-byte limit.");
            }

            if (newlineIndex >= 0)
            {
                var lineStart = _position;
                _position += count + 1;

                if (_lineBufferLength == 0)
                {
                    return TrimCarriageReturn(_buffer.AsMemory(lineStart, count));
                }

                EnsureLineBufferCapacity(_lineBufferLength + count);
                available[..count].CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
                var assembledLine = TrimCarriageReturn(
                    _lineBuffer.AsMemory(0, _lineBufferLength + count));
                _lineBufferLength = 0;
                return assembledLine;
            }

            EnsureLineBufferCapacity(_lineBufferLength + count);
            available[..count].CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
            _lineBufferLength += count;
            _position += count;
        }
    }

    private static ReadOnlyMemory<byte> TrimCarriageReturn(ReadOnlyMemory<byte> line)
    {
        if (!line.IsEmpty && line.Span[^1] == (byte)'\r')
        {
            return line[..^1];
        }

        return line;
    }

    private void EnsureLineBufferCapacity(int requiredLength)
    {
        if (_lineBuffer is { Length: var length } && length >= requiredLength)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(
            Math.Min(maximumLineLength, Math.Max(requiredLength, _lineBuffer?.Length * 2 ?? BufferSize)));
        if (_lineBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_lineBuffer);
        }

        _lineBuffer = replacement;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        if (_lineBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_lineBuffer);
            _lineBuffer = null;
        }
    }
}
