using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class LimitedReadStreamTests
{
    [Fact]
    public async Task Copy_ExactlyMaxBytes_Succeeds()
    {
        var payload = new byte[1024];
        new Random(42).NextBytes(payload);
        await using var inner = new MemoryStream(payload);
        await using var limited = new LimitedReadStream(
            inner, payload.Length, () => new InvalidOperationException("limit"));

        await using var copied = new MemoryStream();
        await limited.CopyToAsync(copied);

        Assert.Equal(payload, copied.ToArray());
    }

    [Fact]
    public async Task Copy_OneBytePastLimit_ThrowsFactoryException()
    {
        await using var inner = new MemoryStream(new byte[1025]);
        await using var limited = new LimitedReadStream(
            inner, 1024, () => new InvalidOperationException("limit tripped"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => limited.CopyToAsync(Stream.Null));

        Assert.Equal("limit tripped", ex.Message);
    }

    [Fact]
    public async Task Copy_OversizeSource_InnerReadStopsAtLimitPlusOne()
    {
        // A payload far larger than the limit must not be drained a full copy
        // buffer past it: the inner read is capped at the remaining allowance
        // plus the single byte that trips the limit.
        await using var inner = new MemoryStream(new byte[1 << 20]);
        await using var limited = new LimitedReadStream(
            inner, 1024, () => new InvalidDataException("limit"));

        await Assert.ThrowsAsync<InvalidDataException>(() => limited.CopyToAsync(Stream.Null));

        Assert.True(
            inner.Position <= 1024 + 1,
            $"inner stream was read to {inner.Position}, beyond the limit plus one byte");
    }

    [Fact]
    public void Read_OversizeSource_SyncPathCapsInnerRead()
    {
        using var inner = new MemoryStream(new byte[1 << 20]);
        using var limited = new LimitedReadStream(
            inner, 1024, () => new InvalidDataException("limit"));

        var buffer = new byte[8192];
        Assert.Throws<InvalidDataException>(() => limited.Read(buffer, 0, buffer.Length));

        Assert.Equal(1025, inner.Position);
    }

    [Fact]
    public async Task Copy_Cancelled_ThrowsOperationCanceledNotLimit()
    {
        // Deterministic cancellation after 64 KiB of reads (a timer races the
        // MemoryStream's own size limit on fast machines). The stream limit sits
        // far above the cancel point, so the copy must end via cancellation.
        using var cts = new CancellationTokenSource();
        await using var inner = TestStreams.CancelAfterBytes(
            new RepeatingByteStream(long.MaxValue), cancelAfterBytes: 64 * 1024, cts);
        await using var limited = new LimitedReadStream(
            inner, long.MaxValue, () => new InvalidOperationException("limit"));

        // ThrowsAny: an OCE crossing async method boundaries resurfaces as
        // TaskCanceledException; either proves cancellation beat the limit.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limited.CopyToAsync(Stream.Null, cts.Token));
    }

    /// <summary>
    /// Yields <paramref name="length"/> zero bytes without allocating them.
    /// </summary>
    internal sealed class RepeatingByteStream(long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, take);
            _remaining -= take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..take].Clear();
            _remaining -= take;
            return ValueTask.FromResult(take);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
