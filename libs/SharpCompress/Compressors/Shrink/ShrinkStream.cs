using System;
using System.IO;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Shrink;

internal partial class ShrinkStream : Stream
{
    /// <summary>
    /// Shrink was a PKZIP 1.x method; real-world members are tiny. Cap allocation so a crafted
    /// local header cannot force a multi-hundred-megabyte buffer before any compressed bytes are read.
    /// </summary>
    internal const int MaxUncompressedSize = 256 * 1024 * 1024;

    private readonly Stream _inStream;

    private readonly long _uncompressedSize;
    private readonly byte[] _byteOut;
    private long _outBytesCount;
    private bool _decompressed;
    private long _position;

    public ShrinkStream(Stream stream, long uncompressedSize)
    {
        if (uncompressedSize > MaxUncompressedSize)
        {
            throw new InvalidFormatException(
                $"Shrink: declared uncompressed size {uncompressedSize} exceeds maximum supported size of {MaxUncompressedSize} bytes."
            );
        }

        _inStream = stream;

        _uncompressedSize = uncompressedSize;
        _byteOut = new byte[(int)_uncompressedSize];
        _outBytesCount = 0L;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _uncompressedSize;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_decompressed)
        {
            // Read actual compressed data from the stream rather than pre-allocating based on the
            // declared compressed size, which may be crafted to cause an OutOfMemoryException.
            // The stream is already bounded by ReadOnlySubStream in ZipFilePart.
            using var srcMs = new PooledMemoryStream();
            _inStream.CopyTo(srcMs);
            var src = srcMs.ToArray();
            var srcLen = src.Length;

            HwUnshrink.Unshrink(
                src,
                srcLen,
                out _,
                _byteOut,
                (int)_uncompressedSize,
                out var dstUsed
            );
            _outBytesCount = dstUsed;
            _decompressed = true;
            _position = 0;
        }

        long remaining = _outBytesCount - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        int toCopy = (int)Math.Min(count, remaining);
        Buffer.BlockCopy(_byteOut, (int)_position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
