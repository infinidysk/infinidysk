using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.LZMA;

public partial class LzmaStream
{
    public static ValueTask<LzmaStream> CreateAsync(
        byte[] properties,
        Stream inputStream,
        long inputSize,
        long outputSize,
        bool leaveOpen = false
    ) =>
        CreateAsync(
            properties,
            inputStream,
            inputSize,
            outputSize,
            null,
            properties.Length < 5,
            leaveOpen
        );

    public static async ValueTask<LzmaStream> CreateAsync(
        byte[] properties,
        Stream inputStream,
        long inputSize,
        long outputSize,
        Stream? presetDictionary,
        bool isLzma2,
        bool leaveOpen = false
    )
    {
        var lzma = new LzmaStream(
            properties,
            inputStream,
            inputSize,
            outputSize,
            isLzma2,
            leaveOpen
        );
        if (!isLzma2)
        {
            if (presetDictionary != null)
            {
                await lzma._outWindow.TrainAsync(presetDictionary).ConfigureAwait(false);
            }

            await lzma
                ._rangeDecoder.InitAsync(inputStream, lzma._rangeDecoderLimit, default)
                .ConfigureAwait(false);
        }
        else
        {
            if (presetDictionary != null)
            {
                await lzma._outWindow.TrainAsync(presetDictionary).ConfigureAwait(false);
                lzma._needDictReset = false;
            }
        }
        return lzma;
    }

    /*public static async ValueTask<LzmaStream> CreateAsync(
        LzmaEncoderProperties properties,
        bool isLzma2,
        Stream? presetDictionary,
        Stream outputStream
    )
    {
        var lzma = new LzmaStream(properties, isLzma2, presetDictionary);

        lzma._encoder!.SetStreams(null, outputStream, -1, -1);

        if (presetDictionary != null)
        {
            lzma._encoder.Train(presetDictionary);
        }
        return lzma;
    }*/

    private async ValueTask DecodeChunkHeaderAsync(CancellationToken cancellationToken = default)
    {
        var headerBuffer = GetAsyncHeaderBuffer();
        await ReadHeaderOrThrowAsync(headerBuffer, 1, cancellationToken).ConfigureAwait(false);
        var control = headerBuffer[0];
        _inputPosition++;

        if (control == 0x00)
        {
            if (_isLzma2 && _decoder is { HasEndMarker: true })
            {
                throw new DataErrorException();
            }

            _endReached = true;
            return;
        }

        if (control >= 0xE0 || control == 0x01)
        {
            _needProps = true;
            _needDictReset = false;
            _outWindow.Reset();
        }
        else if (_needDictReset)
        {
            throw new DataErrorException();
        }

        if (control >= 0x80)
        {
            _uncompressedChunk = false;

            _availableBytes = (control & 0x1F) << 16;
            await ReadHeaderOrThrowAsync(headerBuffer, 2, cancellationToken).ConfigureAwait(false);
            _availableBytes += (headerBuffer[0] << 8) + headerBuffer[1] + 1;
            _inputPosition += 2;

            await ReadHeaderOrThrowAsync(headerBuffer, 2, cancellationToken).ConfigureAwait(false);
            _rangeDecoderLimit = (headerBuffer[0] << 8) + headerBuffer[1] + 1;
            _inputPosition += 2;

            if (control >= 0xC0)
            {
                _needProps = false;
                await ReadHeaderOrThrowAsync(headerBuffer, 1, cancellationToken)
                    .ConfigureAwait(false);
                Properties[0] = headerBuffer[0];
                _inputPosition++;

                _decoder ??= new Decoder();
                _decoder.SetDecoderProperties(Properties);
            }
            else if (_needProps)
            {
                throw new DataErrorException();
            }
            else if (control >= 0xA0)
            {
                _decoder ??= new Decoder();
                _decoder.SetDecoderProperties(Properties);
            }

            await _rangeDecoder
                .InitAsync(_inputStream.NotNull(), _rangeDecoderLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (control > 0x02)
        {
            throw new DataErrorException();
        }
        else
        {
            _uncompressedChunk = true;
            await ReadHeaderOrThrowAsync(headerBuffer, 2, cancellationToken).ConfigureAwait(false);
            _availableBytes = (headerBuffer[0] << 8) + headerBuffer[1] + 1;
            _inputPosition += 2;
        }
    }

    private async ValueTask ReadHeaderOrThrowAsync(
        byte[] headerBuffer,
        int count,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _inputStream!
                .ReadExactlyAsync(headerBuffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.", e);
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_endReached)
        {
            return 0;
        }

        var total = 0;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_availableBytes == 0)
            {
                if (_isLzma2)
                {
                    await DecodeChunkHeaderAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _endReached = true;
                }
                if (_endReached)
                {
                    break;
                }
            }

            var toProcess = count - total;
            if (toProcess > _availableBytes)
            {
                toProcess = (int)_availableBytes;
            }

            _outWindow.SetLimit(toProcess);
            if (_uncompressedChunk)
            {
                _inputPosition += await _outWindow
                    .CopyStreamAsync(_inputStream.NotNull(), toProcess, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (
                await _decoder!
                    .CodeAsync(_dictionarySize, _outWindow, _rangeDecoder, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                HandleEndMarker();
            }

            var read = _outWindow.Read(buffer, offset, toProcess);
            total += read;
            offset += read;
            _position += read;
            _availableBytes -= read;

            if (_availableBytes == 0 && !_uncompressedChunk)
            {
                if (_isLzma2 && _decoder!.HasEndMarker)
                {
                    throw new DataErrorException();
                }

                if (
                    !_rangeDecoder.IsFinished
                    || (_rangeDecoderLimit >= 0 && _rangeDecoder._total != _rangeDecoderLimit)
                )
                {
                    _outWindow.SetLimit(toProcess + 1);
                    if (
                        !await _decoder!
                            .CodeAsync(
                                _dictionarySize,
                                _outWindow,
                                _rangeDecoder,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        _rangeDecoder.ReleaseStream();
                        throw new DataErrorException();
                    }
                }

                _rangeDecoder.ReleaseStream();

                _inputPosition += _rangeDecoder._total;
                if (_outWindow.HasPending)
                {
                    throw new DataErrorException();
                }
            }
        }

        if (_endReached)
        {
            if (_inputSize >= 0 && _inputPosition != _inputSize)
            {
                throw new DataErrorException();
            }
            if (_outputSize >= 0 && _position != _outputSize)
            {
                throw new DataErrorException();
            }
        }

        return total;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_endReached)
        {
            return 0;
        }

        var total = 0;
        var offset = 0;
        var count = buffer.Length;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_availableBytes == 0)
            {
                if (_isLzma2)
                {
                    await DecodeChunkHeaderAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _endReached = true;
                }
                if (_endReached)
                {
                    break;
                }
            }

            var toProcess = count - total;
            if (toProcess > _availableBytes)
            {
                toProcess = (int)_availableBytes;
            }

            _outWindow.SetLimit(toProcess);
            if (_uncompressedChunk)
            {
                _inputPosition += await _outWindow
                    .CopyStreamAsync(_inputStream.NotNull(), toProcess, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (
                await _decoder!
                    .CodeAsync(_dictionarySize, _outWindow, _rangeDecoder, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                HandleEndMarker();
            }

            var read = _outWindow.Read(buffer, offset, toProcess);
            total += read;
            offset += read;
            _position += read;
            _availableBytes -= read;

            if (_availableBytes == 0 && !_uncompressedChunk)
            {
                if (_isLzma2 && _decoder!.HasEndMarker)
                {
                    throw new DataErrorException();
                }

                if (
                    !_rangeDecoder.IsFinished
                    || (_rangeDecoderLimit >= 0 && _rangeDecoder._total != _rangeDecoderLimit)
                )
                {
                    _outWindow.SetLimit(toProcess + 1);
                    if (
                        !await _decoder!
                            .CodeAsync(
                                _dictionarySize,
                                _outWindow,
                                _rangeDecoder,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        _rangeDecoder.ReleaseStream();
                        throw new DataErrorException();
                    }
                }

                _rangeDecoder.ReleaseStream();

                _inputPosition += _rangeDecoder._total;
                if (_outWindow.HasPending)
                {
                    throw new DataErrorException();
                }
            }
        }

        if (_endReached)
        {
            if (_inputSize >= 0 && _inputPosition != _inputSize)
            {
                throw new DataErrorException();
            }
            if (_outputSize >= 0 && _position != _outputSize)
            {
                throw new DataErrorException();
            }
        }

        return total;
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_encoder != null)
        {
            _position = await _encoder
                .CodeAsync(new MemoryStream(buffer, offset, count), false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_encoder != null)
        {
            _position = await _encoder
                .CodeAsync(new MemoryStream(buffer.ToArray()), false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        if (_encoder != null)
        {
            _position = await _encoder.CodeAsync(null, true).ConfigureAwait(false);
            _encoder.Dispose();
        }

        if (!_leaveOpen)
        {
            if (_inputStream is IAsyncDisposable asyncDisposableInputStream)
            {
                await asyncDisposableInputStream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _inputStream?.Dispose();
            }
        }
        await _outWindow.DisposeAsync().ConfigureAwait(false);
        ReturnAsyncHeaderBuffer();

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
