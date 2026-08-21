using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using UsenetSharp.Exceptions;

namespace UsenetSharp.Clients;

internal readonly record struct NntpReadBuffer(ReadOnlyMemory<byte> Memory);

[SuppressMessage(
    "Reliability",
    "CA2213:Disposable fields should be disposed",
    Justification = "NntpLineReader does not own the NNTP stream.")]
internal sealed class NntpLineReader : IDisposable
{
    private const int DefaultBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly int _maximumLineLength;
    private readonly int _bufferSize;
    private readonly byte[] _buffer;
    private byte[]? _lineBuffer;
    private int _lineBufferLength;
    private int _position;
    private int _length;
    private int _exposedLength;
    private bool _exposedFromLineBuffer;
    private bool _disposed;

    internal NntpLineReader(
        Stream stream,
        int maximumLineLength = 64 * 1024,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _stream = stream;
        _maximumLineLength = maximumLineLength;
        _bufferSize = bufferSize;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineBytesAsync(cancellationToken).ConfigureAwait(false);
        return line.HasValue ? Encoding.Latin1.GetString(line.Value.Span) : null;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadLineBytesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfExposureActive();

        while (true)
        {
            if (_position >= _length)
            {
                var bytesRead = await _stream.ReadAsync(
                        _buffer.AsMemory(0, _bufferSize), cancellationToken)
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
                    throw CreateUnterminatedLineException();
                }
            }

            var available = _buffer.AsSpan(_position, _length - _position);
            var newlineIndex = available.IndexOf((byte)'\n');
            var count = newlineIndex >= 0 ? newlineIndex : available.Length;

            if (_lineBufferLength + count > _maximumLineLength)
            {
                throw CreateMaximumLineLengthException();
            }

            if (newlineIndex >= 0)
            {
                var lineStart = _position;
                _position += count + 1;

                if (_lineBufferLength == 0)
                {
                    return TrimCarriageReturn(_buffer.AsMemory(lineStart, count));
                }

                EnsureLineBufferCapacity(_lineBufferLength + count, _maximumLineLength);
                available[..count].CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
                var assembledLine = TrimCarriageReturn(
                    _lineBuffer.AsMemory(0, _lineBufferLength + count));
                _lineBufferLength = 0;
                return assembledLine;
            }

            EnsureLineBufferCapacity(_lineBufferLength + count, _maximumLineLength);
            available[..count].CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
            _lineBufferLength += count;
            _position += count;
        }
    }

    public async ValueTask<NntpReadBuffer?> ReadCompleteLinesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfExposureActive();

        while (true)
        {
            if (_position >= _length)
            {
                var bytesRead = await _stream.ReadAsync(
                        _buffer.AsMemory(0, _bufferSize), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                _length = bytesRead;
                if (_length == 0)
                {
                    if (_lineBufferLength == 0)
                    {
                        return null;
                    }

                    _lineBufferLength = 0;
                    throw CreateUnterminatedLineException();
                }
            }

            var available = _buffer.AsMemory(_position, _length - _position);
            var availableSpan = available.Span;

            if (_lineBufferLength > 0)
            {
                var newlineIndex = availableSpan.IndexOf((byte)'\n');
                if (newlineIndex < 0)
                {
                    if (_lineBufferLength + availableSpan.Length > _maximumLineLength)
                    {
                        throw CreateMaximumLineLengthException();
                    }

                    EnsureLineBufferCapacity(
                        _lineBufferLength + availableSpan.Length, _maximumLineLength);
                    availableSpan.CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
                    _lineBufferLength += availableSpan.Length;
                    _position += availableSpan.Length;
                    continue;
                }

                if (_lineBufferLength + newlineIndex > _maximumLineLength)
                {
                    throw CreateMaximumLineLengthException();
                }

                var rawCount = newlineIndex + 1;
                EnsureLineBufferCapacity(
                    _lineBufferLength + rawCount, _maximumLineLength + 1);
                availableSpan[..rawCount].CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
                _lineBufferLength += rawCount;
                _position += rawCount;
                _exposedLength = _lineBufferLength;
                _exposedFromLineBuffer = true;
                return new NntpReadBuffer(_lineBuffer.AsMemory(0, _lineBufferLength));
            }

            var lastNewline = availableSpan.LastIndexOf((byte)'\n');
            if (lastNewline >= 0)
            {
                var count = lastNewline + 1;
                if (lastNewline > _maximumLineLength)
                {
                    ThrowIfAnyCompleteLineExceedsLimit(availableSpan[..count]);
                }

                _exposedLength = count;
                _exposedFromLineBuffer = false;
                return new NntpReadBuffer(available[..count]);
            }

            if (availableSpan.Length > _maximumLineLength)
            {
                throw CreateMaximumLineLengthException();
            }

            EnsureLineBufferCapacity(availableSpan.Length, _maximumLineLength);
            availableSpan.CopyTo(_lineBuffer.AsSpan(_lineBufferLength));
            _lineBufferLength += availableSpan.Length;
            _position += availableSpan.Length;
        }
    }

    public void Advance(int byteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_exposedLength == 0)
        {
            throw new InvalidOperationException("No NNTP read buffer is active.");
        }

        if (byteCount <= 0 || byteCount > _exposedLength)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        if (GetExposedSpan()[byteCount - 1] != (byte)'\n')
        {
            throw new ArgumentException(
                "NNTP input must be advanced at a complete-line boundary.",
                nameof(byteCount));
        }

        if (_exposedFromLineBuffer)
        {
            if (byteCount != _exposedLength)
            {
                throw new ArgumentException(
                    "An assembled NNTP line must be consumed as one unit.",
                    nameof(byteCount));
            }

            _lineBufferLength = 0;
        }
        else
        {
            _position += byteCount;
        }

        _exposedLength = 0;
        _exposedFromLineBuffer = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exposedLength = 0;
        _exposedFromLineBuffer = false;
        ArrayPool<byte>.Shared.Return(_buffer);
        if (_lineBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_lineBuffer);
            _lineBuffer = null;
        }
    }

    private ReadOnlySpan<byte> GetExposedSpan()
    {
        if (_exposedFromLineBuffer)
        {
            return _lineBuffer.AsSpan(0, _exposedLength);
        }

        return _buffer.AsSpan(_position, _exposedLength);
    }

    private void ThrowIfExposureActive()
    {
        if (_exposedLength != 0)
        {
            throw new InvalidOperationException(
                "An NNTP read buffer is already active.");
        }
    }

    private void ThrowIfAnyCompleteLineExceedsLimit(ReadOnlySpan<byte> completeLines)
    {
        var start = 0;
        while (start < completeLines.Length)
        {
            var newlineIndex = completeLines[start..].IndexOf((byte)'\n');
            if (newlineIndex < 0)
            {
                return;
            }

            if (newlineIndex > _maximumLineLength)
            {
                throw CreateMaximumLineLengthException();
            }

            start += newlineIndex + 1;
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

    private void EnsureLineBufferCapacity(int requiredLength, int maximumLength)
    {
        if (requiredLength > maximumLength)
        {
            throw CreateMaximumLineLengthException();
        }

        if (_lineBuffer is { Length: var length } && length >= requiredLength)
        {
            return;
        }

        var rentSize = Math.Min(
            maximumLength,
            Math.Max(requiredLength, _lineBuffer?.Length * 2 ?? _bufferSize));
        var replacement = ArrayPool<byte>.Shared.Rent(rentSize);
        if (_lineBuffer != null)
        {
            if (_lineBufferLength > 0)
            {
                _lineBuffer.AsSpan(0, _lineBufferLength).CopyTo(replacement);
            }

            ArrayPool<byte>.Shared.Return(_lineBuffer);
        }

        _lineBuffer = replacement;
    }

    private UsenetProtocolException CreateMaximumLineLengthException() =>
        new($"NNTP response line exceeded the {_maximumLineLength}-byte limit.");

    private static UsenetProtocolException CreateUnterminatedLineException() =>
        new("The NNTP stream ended with an unterminated line.");
}
