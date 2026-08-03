using System;
using System.IO;
using SharpCompress.Common;
using SharpCompress.IO;
using Xunit;

namespace SharpCompress.Test.Streams;

public class StreamStackRewindTests
{
    private class NonSeekableStreamWrapper : Stream
    {
        private readonly Stream _baseStream;

        public NonSeekableStreamWrapper(Stream baseStream) => _baseStream = baseStream;

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _baseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _baseStream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => _baseStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _baseStream.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Rewind_WithinRingBuffer_Succeeds()
    {
        var ms = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        using var stream = SharpCompressStream.Create(new NonSeekableStreamWrapper(ms), 16);
        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(4, stream.Position);

        Assert.True(((IStreamStack)stream).Rewind(2));
        Assert.Equal(2, stream.Position);

        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(3, buffer[0]);
        Assert.Equal(4, buffer[1]);
    }

    [Fact]
    public void Rewind_BeyondBufferRange_ReturnsFalse()
    {
        var data = new byte[64];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        var ms = new MemoryStream(data);
        using var stream = SharpCompressStream.Create(new NonSeekableStreamWrapper(ms), 8);
        var buffer = new byte[32];
        Assert.Equal(32, stream.Read(buffer, 0, 32));

        // Position 32; ring holds only 8 bytes → rewind of 16 is outside the buffer.
        Assert.False(((IStreamStack)stream).Rewind(16));
        Assert.Equal(32, stream.Position);
    }

    [Fact]
    public void RewindOrThrow_BeyondBufferRange_ThrowsArchiveOperationException()
    {
        var data = new byte[64];
        var ms = new MemoryStream(data);
        using var stream = SharpCompressStream.Create(new NonSeekableStreamWrapper(ms), 8);
        var buffer = new byte[32];
        Assert.Equal(32, stream.Read(buffer, 0, 32));

        var exception = Assert.Throws<ArchiveOperationException>(() =>
            ((IStreamStack)stream).RewindOrThrow(16)
        );

        Assert.Contains(
            nameof(Constants.RewindableBufferSize),
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Rewind_NegativeTarget_ReturnsFalse()
    {
        var ms = new MemoryStream([1, 2, 3, 4]);
        using var stream = SharpCompressStream.Create(new NonSeekableStreamWrapper(ms), 16);
        var buffer = new byte[2];
        Assert.Equal(2, stream.Read(buffer, 0, 2));

        Assert.False(((IStreamStack)stream).Rewind(8));
    }
}
