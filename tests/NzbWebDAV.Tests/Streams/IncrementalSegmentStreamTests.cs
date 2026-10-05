using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class IncrementalSegmentStreamTests
{
    [Fact]
    public async Task DeliversPrefixBeforeBodyFinishes()
    {
        var source = new GatedStream([1, 2, 3], [4, 5]);
        var completions = new List<(long Drained, long Length)>();
        await using var stream = new IncrementalSegmentStream(
            source, 0, 5, (_, _) => throw new InvalidOperationException("no recovery expected"),
            (drained, length) => completions.Add((drained, length)), CancellationToken.None);

        var buffer = new byte[16];
        var first = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([1, 2, 3], buffer[..first]);
        Assert.Empty(completions);

        source.Release();
        Assert.Equal([4, 5], await ReadToEndAsync(stream));
        Assert.Equal([(5L, 5L)], completions);
    }

    [Fact]
    public async Task MidBodyFailure_ContinuesFromReplacementAtDeliveredOffset()
    {
        var source = new GatedStream([1, 2, 3], fail: true);
        var recoveries = 0;
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6,
            (_, _) =>
            {
                recoveries++;
                return Task.FromResult<Stream>(new MemoryStream([9, 9, 9, 4, 5, 6]));
            },
            (_, _) => { }, CancellationToken.None);

        source.Release();
        Assert.Equal([1, 2, 3, 4, 5, 6], await ReadToEndAsync(stream));
        Assert.Equal(1, recoveries);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ShortBody_IsZeroPaddedToRecordedLength()
    {
        var source = new GatedStream([7, 7], []);
        await using var stream = new IncrementalSegmentStream(
            source, 0, 4, (_, _) => throw new InvalidOperationException(), (_, _) => { }, CancellationToken.None);

        source.Release();
        Assert.Equal([7, 7, 0, 0], await ReadToEndAsync(stream));
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(5));
        return output.ToArray();
    }

    /// <summary>Returns <c>head</c>, then blocks until released before the tail or a failure.</summary>
    private sealed class GatedStream(byte[] head, byte[]? tail = null, bool fail = false) : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stage;

        public bool Disposed { get; private set; }

        public void Release() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_stage++)
            {
                case 0:
                    head.CopyTo(buffer);
                    return head.Length;
                case 1:
                    await _gate.Task.WaitAsync(cancellationToken);
                    if (fail) throw new IOException("connection reset");
                    var rest = tail!;
                    rest.CopyTo(buffer);
                    return rest.Length;
                default:
                    return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
