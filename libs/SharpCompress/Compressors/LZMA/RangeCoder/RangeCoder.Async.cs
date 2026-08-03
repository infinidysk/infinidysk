using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.LZMA.RangeCoder;

internal partial class Encoder
{
    private byte[] SingleByteBuffer => _singleByteBuffer ??= new byte[1];

    public async ValueTask ShiftLowAsync(CancellationToken cancellationToken = default)
    {
        if ((uint)_low < 0xFF000000 || (uint)(_low >> 32) == 1)
        {
            var temp = _cache;
            do
            {
                var b = (byte)(temp + (_low >> 32));
                var buffer = SingleByteBuffer;
                buffer[0] = b;
                await _stream
                    .WriteAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);
                temp = 0xFF;
            } while (--_cacheSize != 0);
            _cache = (byte)(((uint)_low) >> 24);
        }
        _cacheSize++;
        _low = ((uint)_low) << 8;
    }

    public async ValueTask EncodeBitAsync(
        uint size0,
        int numTotalBits,
        uint symbol,
        CancellationToken cancellationToken = default
    )
    {
        var newBound = (_range >> numTotalBits) * size0;
        if (symbol == 0)
        {
            _range = newBound;
        }
        else
        {
            _low += newBound;
            _range -= newBound;
        }
        while (_range < K_TOP_VALUE)
        {
            _range <<= 8;
            await ShiftLowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask EncodeDirectBitsAsync(
        uint v,
        int numTotalBits,
        CancellationToken cancellationToken = default
    )
    {
        for (var i = numTotalBits - 1; i >= 0; i--)
        {
            _range >>= 1;
            if (((v >> i) & 1) == 1)
            {
                _low += _range;
            }
            if (_range < K_TOP_VALUE)
            {
                _range <<= 8;
                await ShiftLowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask FlushStreamAsync(CancellationToken cancellationToken = default) =>
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

internal partial class Decoder
{
    private byte[] SingleByteBuffer => _singleByteBuffer ??= new byte[1];
    private long _declaredInputLength = -1;
    private long _underlyingBytesRead;

    public ValueTask InitAsync(Stream stream, CancellationToken cancellationToken = default) =>
        InitAsync(stream, -1, cancellationToken);

    public async ValueTask InitAsync(
        Stream stream,
        long inputLength,
        CancellationToken cancellationToken = default
    )
    {
        _stream = stream;
        ConfigureInputMode(inputLength);

        _code = 0;
        _range = 0xFFFFFFFF;
        for (var i = 0; i < 5; i++)
        {
            _code = (_code << 8) | await NextByteAsync(cancellationToken).ConfigureAwait(false);
        }

        _total = 5;
    }

    private void ConfigureInputMode(long inputLength)
    {
        var effectiveLength =
            inputLength >= 0 ? inputLength : TryResolveBoundedStreamLength(_stream);
        if (effectiveLength >= 0)
        {
            _declaredInputLength = effectiveLength;
            _underlyingBytesRead = 0;
            _inPos = 0;
            _inLen = 0;
            _inRemaining = effectiveLength;
            _useBufferedInput = true;
            _inBuffer ??= ArrayPool<byte>.Shared.Rent(InBufferSize);
            return;
        }

        _declaredInputLength = -1;
        _underlyingBytesRead = 0;
        _inPos = 0;
        _inLen = 0;
        _inRemaining = long.MaxValue;
        _useBufferedInput = false;
        if (_inBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_inBuffer);
            _inBuffer = null;
        }
    }

    private static long TryResolveBoundedStreamLength(Stream stream)
    {
        if (stream is ReadOnlySubStream subStream)
        {
            return subStream.BytesLeftToRead;
        }

        if (stream is BufferedSubStream bufferedSubStream)
        {
            return bufferedSubStream.Length;
        }

        return -1;
    }

    private ValueTask<byte> NextByteAsync(CancellationToken cancellationToken)
    {
        if (_useBufferedInput)
        {
            if (_inPos < _inLen)
            {
                _total++;
                return ValueTask.FromResult(_inBuffer![_inPos++]);
            }

            return NextBufferedByteAsync(cancellationToken);
        }

        return NextLegacyByteAsync(cancellationToken);
    }

    private async ValueTask<byte> NextBufferedByteAsync(CancellationToken cancellationToken)
    {
        await RefillAsync(cancellationToken).ConfigureAwait(false);
        if (_inLen == 0)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        _total++;
        return _inBuffer![_inPos++];
    }

    private async ValueTask<byte> NextLegacyByteAsync(CancellationToken cancellationToken)
    {
        var buffer = SingleByteBuffer;
        var read = await _stream
            .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        return buffer[0];
    }

    private async ValueTask RefillAsync(CancellationToken cancellationToken)
    {
        _inPos = 0;
        _inLen = 0;

        if (_inRemaining <= 0)
        {
            return;
        }

        var toRead = (int)Math.Min(InBufferSize, _inRemaining);
        var read = await _stream
            .ReadAsync(_inBuffer.AsMemory(0, toRead), cancellationToken)
            .ConfigureAwait(false);
        _underlyingBytesRead += read;

        if (_declaredInputLength >= 0 && _underlyingBytesRead > _declaredInputLength)
        {
            throw new InvalidDataException("Range decoder read past declared input length.");
        }

        if (read == 0)
        {
            return;
        }

        _inLen = read;
        _inRemaining -= read;
    }

    private bool TryPopBufferedByte(out byte value)
    {
        if (_inPos < _inLen)
        {
            value = _inBuffer![_inPos++];
            _total++;
            return true;
        }

        value = 0;
        return false;
    }

    public ValueTask NormalizeAsync(CancellationToken cancellationToken = default)
    {
        if (!_useBufferedInput)
        {
            return NormalizeLegacyAsync(cancellationToken);
        }

        while (_range < K_TOP_VALUE)
        {
            if (!TryPopBufferedByte(out var value))
            {
                return NormalizeBufferedRefillAsync(cancellationToken);
            }

            _code = (_code << 8) | value;
            _range <<= 8;
        }

        return default;
    }

    private async ValueTask NormalizeBufferedRefillAsync(CancellationToken cancellationToken)
    {
        do
        {
            await RefillAsync(cancellationToken).ConfigureAwait(false);
            if (_inLen == 0)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.");
            }

            while (_range < K_TOP_VALUE)
            {
                if (!TryPopBufferedByte(out var value))
                {
                    break;
                }

                _code = (_code << 8) | value;
                _range <<= 8;
            }
        } while (_range < K_TOP_VALUE);
    }

    private async ValueTask NormalizeLegacyAsync(CancellationToken cancellationToken)
    {
        while (_range < K_TOP_VALUE)
        {
            var buffer = SingleByteBuffer;
            var read = await _stream
                .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.");
            }

            _code = (_code << 8) | buffer[0];
            _range <<= 8;
            _total++;
        }
    }

    public ValueTask Normalize2Async(CancellationToken cancellationToken = default)
    {
        if (_range >= K_TOP_VALUE)
        {
            return default;
        }

        if (_useBufferedInput)
        {
            if (TryPopBufferedByte(out var value))
            {
                _code = (_code << 8) | value;
                _range <<= 8;
                return default;
            }

            return Normalize2BufferedRefillAsync(cancellationToken);
        }

        return Normalize2LegacyAsync(cancellationToken);
    }

    private async ValueTask Normalize2BufferedRefillAsync(CancellationToken cancellationToken)
    {
        await RefillAsync(cancellationToken).ConfigureAwait(false);
        if (_inLen == 0)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        if (!TryPopBufferedByte(out var value))
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        _code = (_code << 8) | value;
        _range <<= 8;
    }

    private async ValueTask Normalize2LegacyAsync(CancellationToken cancellationToken)
    {
        var buffer = SingleByteBuffer;
        var read = await _stream
            .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        _code = (_code << 8) | buffer[0];
        _range <<= 8;
        _total++;
    }

    public ValueTask<uint> DecodeDirectBitsAsync(
        int numTotalBits,
        CancellationToken cancellationToken = default
    )
    {
        if (!_useBufferedInput)
        {
            return DecodeDirectBitsLegacyAsync(numTotalBits, cancellationToken);
        }

        var range = _range;
        var code = _code;
        uint result = 0;
        for (var i = numTotalBits; i > 0; i--)
        {
            range >>= 1;
            var t = (code - range) >> 31;
            code -= range & (t - 1);
            result = (result << 1) | (1 - t);

            if (range < K_TOP_VALUE)
            {
                if (!TryPopBufferedByte(out var value))
                {
                    return DecodeDirectBitsBufferedRefillAsync(
                        range,
                        code,
                        result,
                        i,
                        cancellationToken
                    );
                }

                code = (code << 8) | value;
                range <<= 8;
            }
        }

        _range = range;
        _code = code;
        return ValueTask.FromResult(result);
    }

    private async ValueTask<uint> DecodeDirectBitsBufferedRefillAsync(
        uint range,
        uint code,
        uint result,
        int remainingBits,
        CancellationToken cancellationToken
    )
    {
        await RefillAsync(cancellationToken).ConfigureAwait(false);
        if (_inLen == 0 || !TryPopBufferedByte(out var value))
        {
            throw new IncompleteArchiveException("Unexpected end of stream.");
        }

        code = (code << 8) | value;
        range <<= 8;

        for (var i = remainingBits - 1; i > 0; i--)
        {
            range >>= 1;
            var t = (code - range) >> 31;
            code -= range & (t - 1);
            result = (result << 1) | (1 - t);

            if (range < K_TOP_VALUE)
            {
                if (!TryPopBufferedByte(out value))
                {
                    await RefillAsync(cancellationToken).ConfigureAwait(false);
                    if (_inLen == 0 || !TryPopBufferedByte(out value))
                    {
                        throw new IncompleteArchiveException("Unexpected end of stream.");
                    }
                }

                code = (code << 8) | value;
                range <<= 8;
            }
        }

        _range = range;
        _code = code;
        return result;
    }

    private async ValueTask<uint> DecodeDirectBitsLegacyAsync(
        int numTotalBits,
        CancellationToken cancellationToken
    )
    {
        var range = _range;
        var code = _code;
        uint result = 0;
        var buffer = SingleByteBuffer;
        for (var i = numTotalBits; i > 0; i--)
        {
            range >>= 1;
            var t = (code - range) >> 31;
            code -= range & (t - 1);
            result = (result << 1) | (1 - t);

            if (range < K_TOP_VALUE)
            {
                var read = await _stream
                    .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IncompleteArchiveException("Unexpected end of stream.");
                }

                code = (code << 8) | buffer[0];
                range <<= 8;
                _total++;
            }
        }

        _range = range;
        _code = code;
        return result;
    }

    public async ValueTask<uint> DecodeBitAsync(
        uint size0,
        int numTotalBits,
        CancellationToken cancellationToken = default
    )
    {
        var newBound = (_range >> numTotalBits) * size0;
        uint symbol;
        if (_code < newBound)
        {
            symbol = 0;
            _range = newBound;
        }
        else
        {
            symbol = 1;
            _code -= newBound;
            _range -= newBound;
        }

        await NormalizeAsync(cancellationToken).ConfigureAwait(false);
        return symbol;
    }

    public async ValueTask DecodeAsync(
        uint start,
        uint size,
        CancellationToken cancellationToken = default
    )
    {
        _code -= start * _range;
        _range *= size;
        await NormalizeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReleaseInputState()
    {
        if (_inBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_inBuffer);
            _inBuffer = null;
        }

        if (_declaredInputLength >= 0 && _underlyingBytesRead > _declaredInputLength)
        {
            throw new InvalidDataException("Range decoder read past declared input length.");
        }

        _inPos = 0;
        _inLen = 0;
        _inRemaining = 0;
        _declaredInputLength = -1;
        _underlyingBytesRead = 0;
        _useBufferedInput = false;
    }
}
