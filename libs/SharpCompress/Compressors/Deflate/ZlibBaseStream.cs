using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Deflate;

internal enum ZlibStreamFlavor
{
    ZLIB = 1950,
    DEFLATE = 1951,
    GZIP = 1952,
}

/// <summary>
/// Internal base stream for <see cref="DeflateStream"/>, <see cref="ZlibStream"/>, and
/// <see cref="GZipStream"/>.
/// </summary>
internal partial class ZlibBaseStream : Stream, IStreamStack
{
    Stream IStreamStack.BaseStream() => _stream;

    protected internal ZlibCodec _z = null!;

    protected internal StreamMode _streamMode = StreamMode.Undefined;
    protected internal FlushType _flushMode;
    protected internal ZlibStreamFlavor _flavor;
    protected internal CompressionMode _compressionMode;
    protected internal CompressionLevel _level;
    protected internal byte[] _workingBuffer = null!;
    protected internal int _bufferSize = ZlibConstants.WorkingBufferSizeDefault;

    protected internal Stream _stream = null!;
    protected internal CompressionStrategy Strategy = CompressionStrategy.Default;

    private readonly CRC32? crc;
    protected internal string? _GzipFileName;
    protected internal string? _GzipComment;
    protected internal DateTime _GzipMtime;
    protected internal int _gzipHeaderByteCount;

    private readonly Encoding? _encoding;
    private readonly bool _leaveOpen;

    internal int Crc32 => crc?.Crc32Result ?? 0;

    public ZlibBaseStream(
        Stream stream,
        CompressionMode compressionMode,
        CompressionLevel level,
        ZlibStreamFlavor flavor,
        Encoding? encoding
    )
        : this(stream, compressionMode, level, flavor, leaveOpen: false, encoding) { }

    public ZlibBaseStream(
        Stream stream,
        CompressionMode compressionMode,
        CompressionLevel level,
        ZlibStreamFlavor flavor,
        bool leaveOpen,
        Encoding? encoding
    )
    {
        _flushMode = FlushType.None;
        _leaveOpen = leaveOpen;
        _stream = stream;
        _compressionMode = compressionMode;
        _flavor = flavor;
        _level = level;
        _encoding = encoding;

        if (flavor == ZlibStreamFlavor.GZIP)
        {
            crc = new CRC32();
        }
    }

    protected internal bool _wantCompress => (_compressionMode == CompressionMode.Compress);

    private ZlibCodec z
    {
        get
        {
            if (_z is null)
            {
                var wantRfc1950Header = (_flavor == ZlibStreamFlavor.ZLIB);
                _z = new ZlibCodec();
                if (_compressionMode == CompressionMode.Decompress)
                {
                    _z.InitializeInflate(wantRfc1950Header);
                }
                else
                {
                    _z.Strategy = Strategy;
                    _z.InitializeDeflate(_level, wantRfc1950Header);
                }
            }
            return _z;
        }
    }

    private byte[] workingBuffer => _workingBuffer ??= ArrayPool<byte>.Shared.Rent(_bufferSize);

    public override void Write(byte[] buffer, int offset, int count)
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

            _stream.Write(_workingBuffer, 0, _workingBuffer.Length - _z.AvailableBytesOut);

            done = _z.AvailableBytesIn == 0 && _z.AvailableBytesOut != 0;

            if (_flavor == ZlibStreamFlavor.GZIP && !_wantCompress)
            {
                done = (_z.AvailableBytesIn == 8 && _z.AvailableBytesOut != 0);
            }
        } while (!done);
    }

    private void finish()
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
                    _stream.Write(_workingBuffer, 0, _workingBuffer.Length - _z.AvailableBytesOut);
                }

                done = _z.AvailableBytesIn == 0 && _z.AvailableBytesOut != 0;

                if (_flavor == ZlibStreamFlavor.GZIP && !_wantCompress)
                {
                    done = (_z.AvailableBytesIn == 8 && _z.AvailableBytesOut != 0);
                }
            } while (!done);

            Flush();

            if (_flavor == ZlibStreamFlavor.GZIP)
            {
                if (_wantCompress)
                {
                    Span<byte> intBuf = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(intBuf, crc.NotNull().Crc32Result);
                    _stream.Write(intBuf);
                    var c2 = (int)(crc.NotNull().TotalBytesRead & 0x00000000FFFFFFFF);
                    BinaryPrimitives.WriteInt32LittleEndian(intBuf, c2);
                    _stream.Write(intBuf);
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

                    Span<byte> trailer = stackalloc byte[8];

                    if (_z.AvailableBytesIn != 8)
                    {
                        _z.InputBuffer.AsSpan(_z.NextIn, _z.AvailableBytesIn).CopyTo(trailer);
                        var bytesNeeded = 8 - _z.AvailableBytesIn;
                        var bytesRead = _stream.Read(
                            trailer.Slice(_z.AvailableBytesIn, bytesNeeded)
                        );
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
                        _z.InputBuffer.AsSpan(_z.NextIn, trailer.Length).CopyTo(trailer);
                    }

                    ValidateGzipTrailer(trailer);
                }
                else
                {
                    throw new ZlibException("Reading with compression is not supported.");
                }
            }
        }
    }

    private void ValidateGzipTrailer(ReadOnlySpan<byte> trailer)
    {
        var crc32_expected = BinaryPrimitives.ReadInt32LittleEndian(trailer);
        var crc32_actual = crc.NotNull().Crc32Result;
        var isize_expected = BinaryPrimitives.ReadInt32LittleEndian(trailer.Slice(4));
        var isize_actual = (Int32)(_z.TotalBytesOut & 0x00000000FFFFFFFF);

        if (crc32_actual != crc32_expected)
        {
            throw new ZlibException(
                String.Format(
                    Constants.DefaultCultureInfo,
                    "Bad CRC32 in GZIP stream. (actual({0:X8})!=expected({1:X8}))",
                    crc32_actual,
                    crc32_expected
                )
            );
        }

        if (isize_actual != isize_expected)
        {
            throw new ZlibException(
                String.Format(
                    Constants.DefaultCultureInfo,
                    "Bad size in GZIP stream. (actual({0})!=expected({1}))",
                    isize_actual,
                    isize_expected
                )
            );
        }
    }

    private void end()
    {
        if (z is null)
        {
            return;
        }
        if (_wantCompress)
        {
            _z.EndDeflate();
        }
        else
        {
            _z.EndInflate();
        }
        _z = null!;
    }

    protected override void Dispose(bool disposing)
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        base.Dispose(disposing);
        if (disposing)
        {
            if (_stream is null)
            {
                return;
            }
            try
            {
                finish();
            }
            finally
            {
                end();
                ReturnWorkingBuffer();
                if (!_leaveOpen)
                {
                    _stream?.Dispose();
                }
                _stream = null!;
            }
        }
    }

    private void ReturnWorkingBuffer()
    {
        if (_workingBuffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_workingBuffer, clearArray: true);
        _workingBuffer = null!;
    }

    internal void ReturnOverread()
    {
        if (_streamMode != StreamMode.Writer && z.AvailableBytesIn > 0)
        {
            if (_stream is IStreamStack stack)
            {
                stack.RewindOrThrow(z.AvailableBytesIn);
            }
            z.AvailableBytesIn = 0;
        }
    }

    public override void Flush()
    {
        if (_streamMode == StreamMode.Writer)
        {
            _stream.Flush();
        }
        else
        {
            ReturnOverread();
        }
    }

    public override Int64 Seek(Int64 offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(Int64 value) => _stream.SetLength(value);

    private bool nomoreinput;
    private bool isDisposed;

    private string ReadZeroTerminatedString()
    {
        var list = new List<byte>();
        var done = false;
        Span<byte> one = stackalloc byte[1];
        do
        {
            var n = _stream.Read(one);
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

    private int _ReadAndValidateGzipHeader()
    {
        var totalBytesRead = 0;

        Span<byte> header = stackalloc byte[10];
        var n = _stream.Read(header);

        if (n == 0)
        {
            return 0;
        }

        if (n != 10)
        {
            throw new ZlibException("Not a valid GZIP stream.");
        }

        if (header[0] != 0x1F || header[1] != 0x8B || header[2] != 8)
        {
            throw new ZlibException("Bad GZIP header.");
        }

        var timet = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
        _GzipMtime = TarHeader.EPOCH.AddSeconds(timet);
        totalBytesRead += n;
        if ((header[3] & 0x04) == 0x04)
        {
            _stream.ReadAtLeast(header.Slice(0, 2), 2);
            totalBytesRead += 2;

            var extraLength = (short)(header[0] + header[1] * 256);
            var extra = ArrayPool<byte>.Shared.Rent(extraLength);
            try
            {
                _stream.ReadAtLeast(extra.AsSpan(0, extraLength), extraLength);
                totalBytesRead += extraLength;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(extra);
            }
        }
        if ((header[3] & 0x08) == 0x08)
        {
            _GzipFileName = ReadZeroTerminatedString();
        }
        if ((header[3] & 0x10) == 0x010)
        {
            _GzipComment = ReadZeroTerminatedString();
        }
        if ((header[3] & 0x02) == 0x02)
        {
            Span<byte> crc16 = stackalloc byte[1];
            _stream.ReadAtLeast(crc16, 1);
            totalBytesRead += 1;
        }

        return totalBytesRead;
    }

    public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
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
                _gzipHeaderByteCount = _ReadAndValidateGzipHeader();

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
                _z.AvailableBytesIn = _stream.Read(_workingBuffer, 0, _bufferSize);
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

    public override Boolean CanRead => _stream.CanRead;

    public override Boolean CanSeek => _stream.CanSeek;

    public override Boolean CanWrite => _stream.CanWrite;

    public override Int64 Length => _stream.Length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal enum StreamMode
    {
        Writer,
        Reader,
        Undefined,
    }
}
