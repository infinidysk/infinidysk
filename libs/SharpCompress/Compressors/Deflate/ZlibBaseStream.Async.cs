using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Deflate;

internal partial class ZlibBaseStream
{
    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (crc is not null)
        {
            crc.NotNull().SlurpBlock(buffer, offset, count);
        }

        if (_streamMode == StreamMode.Undefined)
        {
            _streamMode = StreamMode.Writer;
        }
        else if (_streamMode != StreamMode.Writer)
        {
            throw new ZlibException("Cannot Write after Reading.");
        }

        if (count == 0)
        {
            return;
        }

        z.InputBuffer = buffer;
        _z.NextIn = offset;
        _z.AvailableBytesIn = count;
        var done = false;
        do
        {
            _z.OutputBuffer = workingBuffer;
            _z.NextOut = 0;
            _z.AvailableBytesOut = _workingBuffer.Length;
            var rc = (_wantCompress) ? _z.Deflate(_flushMode) : _z.Inflate(_flushMode);
            if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_STREAM_END)
            {
                throw new ZlibException((_wantCompress ? "de" : "in") + "flating: " + _z.Message);
            }

            await _stream
                .WriteAsync(
                    _workingBuffer.AsMemory(0, _workingBuffer.Length - _z.AvailableBytesOut),
                    cancellationToken
                )
                .ConfigureAwait(false);

            done = _z.AvailableBytesIn == 0 && _z.AvailableBytesOut != 0;

            if (_flavor == ZlibStreamFlavor.GZIP && !_wantCompress)
            {
                done = (_z.AvailableBytesIn == 8 && _z.AvailableBytesOut != 0);
            }
        } while (!done);
    }

    private async ValueTask finishAsync(CancellationToken cancellationToken = default)
    {
        if (_z is null)
        {
            return;
        }

        if (_streamMode == StreamMode.Writer)
        {
            var done = false;
            do
            {
                _z.OutputBuffer = workingBuffer;
                _z.NextOut = 0;
                _z.AvailableBytesOut = _workingBuffer.Length;
                var rc =
                    (_wantCompress) ? _z.Deflate(FlushType.Finish) : _z.Inflate(FlushType.Finish);

                if (rc != ZlibConstants.Z_STREAM_END && rc != ZlibConstants.Z_OK)
                {
                    var verb = (_wantCompress ? "de" : "in") + "flating";
                    if (_z.Message is null)
                    {
                        throw new ZlibException(
                            String.Format(Constants.DefaultCultureInfo, "{0}: (rc = {1})", verb, rc)
                        );
                    }
                    throw new ZlibException(verb + ": " + _z.Message);
                }

                if (_workingBuffer.Length - _z.AvailableBytesOut > 0)
                {
                    await _stream
                        .WriteAsync(
                            _workingBuffer.AsMemory(
                                0,
                                _workingBuffer.Length - _z.AvailableBytesOut
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                done = _z.AvailableBytesIn == 0 && _z.AvailableBytesOut != 0;

                if (_flavor == ZlibStreamFlavor.GZIP && !_wantCompress)
                {
                    done = (_z.AvailableBytesIn == 8 && _z.AvailableBytesOut != 0);
                }
            } while (!done);

            await FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_flavor == ZlibStreamFlavor.GZIP)
            {
                if (_wantCompress)
                {
                    var intBuf = ArrayPool<byte>.Shared.Rent(4);
                    try
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(intBuf, crc.NotNull().Crc32Result);
                        await _stream
                            .WriteAsync(intBuf.AsMemory(0, 4), cancellationToken)
                            .ConfigureAwait(false);
                        var c2 = (int)(crc.NotNull().TotalBytesRead & 0x00000000FFFFFFFF);
                        BinaryPrimitives.WriteInt32LittleEndian(intBuf, c2);
                        await _stream
                            .WriteAsync(intBuf.AsMemory(0, 4), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(intBuf);
                    }
                }
                else
                {
                    throw new ZlibException("Writing with decompression is not supported.");
                }
            }
        }
        else if (_streamMode == StreamMode.Reader)
        {
            if (_flavor == ZlibStreamFlavor.GZIP)
            {
                if (!_wantCompress)
                {
                    if (_z.TotalBytesOut == 0L)
                    {
                        return;
                    }

                    var trailer = ArrayPool<byte>.Shared.Rent(8);
                    try
                    {
                        if (_z.AvailableBytesIn != 8)
                        {
                            _z.InputBuffer.AsSpan(_z.NextIn, _z.AvailableBytesIn).CopyTo(trailer);
                            var bytesNeeded = 8 - _z.AvailableBytesIn;
                            var bytesRead = await _stream
                                .ReadAsync(
                                    trailer.AsMemory(_z.AvailableBytesIn, bytesNeeded),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            if (bytesNeeded != bytesRead)
                            {
                                throw new ZlibException(
                                    String.Format(
                                        Constants.DefaultCultureInfo,
                                        "Protocol error. AvailableBytesIn={0}, expected 8",
                                        _z.AvailableBytesIn + bytesRead
                                    )
                                );
                            }
                        }
                        else
                        {
                            _z.InputBuffer.AsSpan(_z.NextIn, 8).CopyTo(trailer);
                        }

                        ValidateGzipTrailer(trailer.AsSpan(0, 8));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(trailer);
                    }
                }
                else
                {
                    throw new ZlibException("Reading with compression is not supported.");
                }
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
        if (_stream is null)
        {
            return;
        }
        try
        {
            await finishAsync().ConfigureAwait(false);
        }
        finally
        {
            end();
            ReturnWorkingBuffer();
            if (_stream != null)
            {
                if (!_leaveOpen)
                {
                    if (_stream is IAsyncDisposable asyncDisposableStream)
                    {
                        await asyncDisposableStream.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _stream.Dispose();
                    }
                }
                _stream = null!;
            }
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_streamMode == StreamMode.Writer)
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ReturnOverread();
        }
    }

    private async ValueTask<string> ReadZeroTerminatedStringAsync(
        CancellationToken cancellationToken
    )
    {
        var list = new List<byte>();
        var done = false;
        var one = new byte[1];
        do
        {
            var n = await _stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (n != 1)
            {
                throw new ZlibException("Unexpected EOF reading GZIP header.");
            }
            if (one[0] == 0)
            {
                done = true;
            }
            else
            {
                list.Add(one[0]);
            }
        } while (!done);
        var buffer = list.ToArray();
        return _encoding.NotNull().GetString(buffer, 0, buffer.Length);
    }

    private async ValueTask<int> _ReadAndValidateGzipHeaderAsync(
        CancellationToken cancellationToken
    )
    {
        var totalBytesRead = 0;

        var header = ArrayPool<byte>.Shared.Rent(10);
        try
        {
            await _stream
                .ReadAtLeastAsync(header.AsMemory(0, 10), 10, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (header[0] != 0x1F || header[1] != 0x8B || header[2] != 8)
            {
                throw new ZlibException("Bad GZIP header.");
            }

            var timet = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            _GzipMtime = TarHeader.EPOCH.AddSeconds(timet);
            totalBytesRead += 10;

            if ((header[3] & 0x04) == 0x04)
            {
                await _stream
                    .ReadAtLeastAsync(
                        header.AsMemory(0, 2),
                        2,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                totalBytesRead += 2;

                var extraLength = (short)(header[0] + header[1] * 256);
                var extra = ArrayPool<byte>.Shared.Rent(extraLength);
                try
                {
                    await _stream
                        .ReadAtLeastAsync(
                            extra.AsMemory(0, extraLength),
                            extraLength,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);
                    totalBytesRead += extraLength;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(extra);
                }
            }
            if ((header[3] & 0x08) == 0x08)
            {
                _GzipFileName = await ReadZeroTerminatedStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            if ((header[3] & 0x10) == 0x010)
            {
                _GzipComment = await ReadZeroTerminatedStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            if ((header[3] & 0x02) == 0x02)
            {
                var crc16 = ArrayPool<byte>.Shared.Rent(1);
                try
                {
                    await _stream
                        .ReadAtLeastAsync(
                            crc16.AsMemory(0, 1),
                            1,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);
                    totalBytesRead += 1;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(crc16);
                }
            }

            return totalBytesRead;
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_streamMode == StreamMode.Undefined)
        {
            if (!_stream.CanRead)
            {
                throw new ZlibException("The stream is not readable.");
            }

            _streamMode = StreamMode.Reader;

            z.AvailableBytesIn = 0;
            if (_flavor == ZlibStreamFlavor.GZIP)
            {
                _gzipHeaderByteCount = await _ReadAndValidateGzipHeaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (_gzipHeaderByteCount == 0)
                {
                    return 0;
                }
            }
        }

        if (_streamMode != StreamMode.Reader)
        {
            throw new ZlibException("Cannot Read after Writing.");
        }

        var rc = 0;

        _z.OutputBuffer = buffer;
        _z.NextOut = offset;
        _z.AvailableBytesOut = count;

        if (count == 0)
        {
            return 0;
        }
        if (nomoreinput && _wantCompress)
        {
            rc = _z.Deflate(FlushType.Finish);

            if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_STREAM_END)
            {
                throw new ZlibException(
                    String.Format(
                        Constants.DefaultCultureInfo,
                        "Deflating:  rc={0}  msg={1}",
                        rc,
                        _z.Message
                    )
                );
            }

            rc = (count - _z.AvailableBytesOut);

            if (crc is not null)
            {
                crc.NotNull().SlurpBlock(buffer, offset, rc);
            }

            return rc;
        }
        ThrowHelper.ThrowIfNull(buffer);
        ThrowHelper.ThrowIfNegative(count);
        ThrowHelper.ThrowIfLessThan(offset, buffer.GetLowerBound(0));
        if ((offset + count) > buffer.GetLength(0))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _z.InputBuffer = workingBuffer;

        do
        {
            if ((_z.AvailableBytesIn == 0) && (!nomoreinput))
            {
                _z.NextIn = 0;
                _z.AvailableBytesIn = await _stream
                    .ReadAsync(_workingBuffer.AsMemory(0, _bufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (_z.AvailableBytesIn == 0)
                {
                    nomoreinput = true;
                }
            }

            rc = (_wantCompress) ? _z.Deflate(_flushMode) : _z.Inflate(_flushMode);

            if (nomoreinput && (rc == ZlibConstants.Z_BUF_ERROR))
            {
                return 0;
            }

            if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_STREAM_END)
            {
                throw new ZlibException(
                    String.Format(
                        Constants.DefaultCultureInfo,
                        "{0}flating:  rc={1}  msg={2}",
                        (_wantCompress ? "de" : "in"),
                        rc,
                        _z.Message
                    )
                );
            }

            if (
                (nomoreinput || rc == ZlibConstants.Z_STREAM_END) && (_z.AvailableBytesOut == count)
            )
            {
                break;
            }
        } while (_z.AvailableBytesOut > 0 && !nomoreinput && rc == ZlibConstants.Z_OK);

        if (_z.AvailableBytesOut > 0)
        {
            if (nomoreinput)
            {
                if (_wantCompress)
                {
                    rc = _z.Deflate(FlushType.Finish);

                    if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_STREAM_END)
                    {
                        throw new ZlibException(
                            String.Format(
                                Constants.DefaultCultureInfo,
                                "Deflating:  rc={0}  msg={1}",
                                rc,
                                _z.Message
                            )
                        );
                    }
                }
            }
        }

        rc = (count - _z.AvailableBytesOut);

        if (crc is not null)
        {
            crc.NotNull().SlurpBlock(buffer, offset, rc);
        }

        if (rc == ZlibConstants.Z_STREAM_END && z.AvailableBytesIn != 0 && !_wantCompress)
        {
            this.RewindOrThrow(z.AvailableBytesIn);
            z.AvailableBytesIn = 0;
        }

        return rc;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = await ReadAsync(array, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
            array.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}
