using System;
using System.Buffers;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarStream : Stream
{
    // 64 KB matches RAR unpack write-block granularity; oversizing wastes pool memory per open entry.
    private const int TmpBufferSize = 65536;

    private readonly IRarUnpack unpack;
    private readonly FileHeader fileHeader;
    private readonly Stream readStream;
    private readonly bool ownsUnpack;
    private readonly Action? onDispose;

    private bool fetch;

    private byte[] tmpBuffer = null!;
    private int tmpOffset;
    private int tmpCount;

    private Memory<byte> outBuffer;
    private int outTotal;
    private bool initialized;
    private bool isDisposed;
    private long _position;

    public RarStream(
        IRarUnpack unpack,
        FileHeader fileHeader,
        Stream readStream,
        bool ownsUnpack = false,
        Action? onDispose = null
    )
    {
        this.unpack = unpack;
        this.fileHeader = fileHeader;
        this.readStream = readStream;
        this.ownsUnpack = ownsUnpack;
        this.onDispose = onDispose;
        tmpBuffer = ArrayPool<byte>.Shared.Rent(TmpBufferSize);
    }

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }

        fetch = true;
        unpack.DoUnpack(fileHeader, readStream, this);
        fetch = false;
        initialized = true;
        _position = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (!isDisposed)
        {
            if (disposing)
            {
                if (tmpBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(this.tmpBuffer);
                    this.tmpBuffer = null!;
                }
                readStream.Dispose();
                if (ownsUnpack && unpack is IDisposable disposableUnpack)
                {
                    disposableUnpack.Dispose();
                }

                onDispose?.Invoke();
            }
            isDisposed = true;
            base.Dispose(disposing);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override void Flush() { }

    public override long Length => fileHeader.UncompressedSize;

    //commented out code always returned the length of the file
    public override long Position
    {
        get => _position; /* fileHeader.UncompressedSize - unpack.DestSize;*/
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        Initialize();
        outTotal = 0;
        if (tmpCount > 0)
        {
            var toCopy = tmpCount < count ? tmpCount : count;
            Buffer.BlockCopy(tmpBuffer, tmpOffset, buffer, offset, toCopy);
            tmpOffset += toCopy;
            tmpCount -= toCopy;
            offset += toCopy;
            count -= toCopy;
            outTotal += toCopy;
        }
        if (count > 0 && unpack.DestSize > 0)
        {
            outBuffer = buffer.AsMemory(offset, count);
            fetch = true;
            unpack.DoUnpack();
            fetch = false;
        }
        _position += outTotal;
        if (count > 0 && outTotal == 0 && _position < Length)
        {
            // sanity check, eg if we try to decompress a redir entry
            throw new ArchiveOperationException(
                $"unpacked file size does not match header: expected {Length} found {_position}"
            );
        }
        return outTotal;
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        Initialize();
        outTotal = 0;
        if (tmpCount > 0)
        {
            var toCopy = tmpCount < buffer.Length ? tmpCount : buffer.Length;
            tmpBuffer.AsSpan(tmpOffset, toCopy).CopyTo(buffer);
            tmpOffset += toCopy;
            tmpCount -= toCopy;
            buffer = buffer.Slice(toCopy);
            outTotal += toCopy;
        }

        if (buffer.Length > 0 && unpack.DestSize > 0)
        {
            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                outBuffer = rented.AsMemory(0, buffer.Length);
                fetch = true;
                unpack.DoUnpack();
                fetch = false;
                var written = buffer.Length - outBuffer.Length;
                rented.AsSpan(0, written).CopyTo(buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        _position += outTotal;
        if (buffer.Length > 0 && outTotal == 0 && _position < Length)
        {
            throw new ArchiveOperationException(
                $"unpacked file size does not match header: expected {Length} found {_position}"
            );
        }
        return outTotal;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!fetch)
        {
            throw new NotSupportedException();
        }
        if (outBuffer.Length > 0)
        {
            var toCopy = outBuffer.Length < count ? outBuffer.Length : count;
            buffer.AsSpan(offset, toCopy).CopyTo(outBuffer.Span);
            outBuffer = outBuffer.Slice(toCopy);
            offset += toCopy;
            count -= toCopy;
            outTotal += toCopy;
        }
        if (count > 0)
        {
            EnsureBufferCapacity(count);
            Buffer.BlockCopy(buffer, offset, tmpBuffer, tmpCount, count);
            tmpCount += count;
            tmpOffset = 0;
            unpack.Suspended = true;
        }
        else
        {
            unpack.Suspended = false;
        }
    }

    private void EnsureBufferCapacity(int count)
    {
        if (this.tmpBuffer.Length < this.tmpCount + count)
        {
            var newLength =
                this.tmpBuffer.Length * 2 > this.tmpCount + count
                    ? this.tmpBuffer.Length * 2
                    : this.tmpCount + count;
            var newBuffer = ArrayPool<byte>.Shared.Rent(newLength);
            try
            {
                Buffer.BlockCopy(this.tmpBuffer, 0, newBuffer, 0, this.tmpCount);
                var oldBuffer = this.tmpBuffer;
                this.tmpBuffer = newBuffer;
                newBuffer = null!;
                ArrayPool<byte>.Shared.Return(oldBuffer);
            }
            finally
            {
                if (newBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(newBuffer);
                }
            }
        }
    }
}
