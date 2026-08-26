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

        var copied = new MemoryStream();
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
            () => limited.CopyToAsync(new MemoryStream()));

        Assert.Equal("limit tripped", ex.Message);
    }

    [Fact]
    public async Task Copy_Cancelled_ThrowsOperationCanceledNotLimit()
    {
        // A repeating inner stream never ends; the read token must win over the
        // limit accounting so cancellation stays cancellation.
        await using var inner = new RepeatingByteStream(long.MaxValue);
        await using var limited = new LimitedReadStream(
            inner, long.MaxValue, () => new InvalidOperationException("limit"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => limited.CopyToAsync(new MemoryStream(), cts.Token));
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
