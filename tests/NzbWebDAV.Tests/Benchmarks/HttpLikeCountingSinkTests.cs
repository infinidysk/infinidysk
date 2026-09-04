using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using NzbWebDAV.Benchmarks;

namespace NzbWebDAV.Tests.Benchmarks;

public sealed class HttpLikeCountingSinkTests
{
    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(128 * 1024)]
    [InlineData(256 * 1024)]
    public async Task CopyFromAsync_UsesConfiguredChunkAndReturnsBuffer(int chunkBytes)
    {
        var payload = new byte[(chunkBytes * 2) + 17];
        new Random(42).NextBytes(payload);
        var pool = new TrackingArrayPool();
        await using var source = new RecordingReadStream(payload);
        var sink = new HttpLikeCountingSink(chunkBytes, Stopwatch.GetTimestamp(), pool);

        var hash = await sink.CopyFromAsync(source, verifyHash: true, CancellationToken.None);

        Assert.Equal(payload.Length, sink.BytesWritten);
        Assert.Equal(chunkBytes, source.MaximumRequestedBytes);
        Assert.NotNull(sink.TimeToFirstByte);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), hash);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task CopyFromAsync_ReadFailure_ReturnsBuffer()
    {
        var pool = new TrackingArrayPool();
        await using var source = new ThrowingReadStream(new IOException("read failed"));
        var sink = new HttpLikeCountingSink(64 * 1024, Stopwatch.GetTimestamp(), pool);

        await Assert.ThrowsAsync<IOException>(() =>
            sink.CopyFromAsync(source, verifyHash: false, CancellationToken.None));

        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public async Task CopyFromAsync_Cancellation_ReturnsBuffer()
    {
        var pool = new TrackingArrayPool();
        await using var source = new ThrowingReadStream(new OperationCanceledException());
        var sink = new HttpLikeCountingSink(64 * 1024, Stopwatch.GetTimestamp(), pool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sink.CopyFromAsync(source, verifyHash: false, CancellationToken.None));

        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public void Summarize_ComputesMedianAndMedianAbsoluteDeviation()
    {
        var summary = HttpResponseCopyChunkReport.Summarize([1, 2, 4, 8, 16]);

        Assert.Equal(4, summary.Median);
        Assert.Equal(3, summary.Mad);
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        public int RentCount { get; private set; }
        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            _ = array;
            _ = clearArray;
            ReturnCount++;
        }
    }

    private sealed class RecordingReadStream(byte[] payload) : MemoryStream(payload)
    {
        public int MaximumRequestedBytes { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);
    }
}
