using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class IncrementalSegmentStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DeliversPrefixBeforeBodyFinishes_AndReportsTheBodyReadWait()
    {
        var source = new GatedStream([1, 2, 3], [4, 5]);
        var handler = new Handler();
        var completions = new List<(long Drained, long Length)>();
        await using var stream = new IncrementalSegmentStream(
            source, 0, 5, handler, (drained, length) => completions.Add((drained, length)), CancellationToken.None);

        var buffer = new byte[16];
        var first = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
        Assert.Equal([1, 2, 3], buffer[..first]);
        var pending = stream.ReadAsync(buffer).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Empty(completions);

        source.Release();
        var second = await pending.WaitAsync(Timeout);
        Assert.Equal([4, 5], buffer[..second]);
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout));
        Assert.Equal([(5L, 5L)], completions);
        Assert.True(handler.Waits >= 1);
        Assert.Equal([false], handler.Outcomes);
    }

    [Fact]
    public async Task LateFailure_ReplacesUnreadBytesOfTheFailedBody()
    {
        var source = new GatedStream([1, 2, 3], fail: true);
        var handler = new Handler((_, _) => new SegmentReplacement(new MemoryStream([4, 5, 6, 7, 8, 9]), false));
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6, handler, (_, _) => { }, CancellationToken.None);

        source.Release();
        await handler.RecoveryStarted.Task.WaitAsync(Timeout);

        Assert.Equal([4, 5, 6, 7, 8, 9], await ReadToEndAsync(stream));
        Assert.True(source.Disposed);
        Assert.Equal([false], handler.Outcomes);
    }

    [Fact]
    public async Task LateFailure_ContinuesWhenDeliveredBytesMatchTheReplacement()
    {
        var source = new GatedStream([1, 2, 3], fail: true);
        var handler = new Handler((_, _) => new SegmentReplacement(new MemoryStream([1, 2, 3, 4, 5, 6]), false));
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6, handler, (_, _) => { }, CancellationToken.None);

        var buffer = new byte[16];
        Assert.Equal(3, await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout));
        source.Release();

        Assert.Equal([4, 5, 6], await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task LateFailure_FailsTheReadWhenDeliveredBytesDifferFromTheReplacement()
    {
        var source = new GatedStream([1, 2, 3], fail: true);
        var handler = new Handler((_, _) => new SegmentReplacement(new MemoryStream([9, 9, 9, 4, 5, 6]), false));
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6, handler, (_, _) => { }, CancellationToken.None);

        var buffer = new byte[16];
        Assert.Equal(3, await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout));
        source.Release();

        var failure = await Assert.ThrowsAsync<PostDeliveryException>(
            () => stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout));
        Assert.Equal(3, failure.Delivered);
        Assert.Empty(handler.Outcomes);
    }

    [Fact]
    public async Task GapFillAfterDelivery_FailsTheReadWithTheGapFailure()
    {
        var gap = new InvalidOperationException("persistently corrupt");
        var source = new GatedStream([1, 2, 3], fail: true);
        var handler = new Handler((_, _) => new SegmentReplacement(new MemoryStream(new byte[6]), true, gap));
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6, handler, (_, _) => { }, CancellationToken.None);

        var buffer = new byte[16];
        Assert.Equal(3, await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout));
        source.Release();

        Assert.Same(gap, await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)));
    }

    [Fact]
    public async Task GapFillBeforeDelivery_IsAccountedBeforeItsFirstByte()
    {
        var source = new GatedStream([1, 2, 3], fail: true);
        var rejected = new InvalidOperationException("fill limit reached");
        var handler = new Handler(
            (_, _) => new SegmentReplacement(new MemoryStream(new byte[6]), true, new IOException("missing")))
        {
            Consumed = _ => throw rejected,
        };
        await using var stream = new IncrementalSegmentStream(
            source, 0, 6, handler, (_, _) => { }, CancellationToken.None);

        source.Release();
        await handler.RecoveryStarted.Task.WaitAsync(Timeout);

        var buffer = new byte[16];
        Assert.Same(rejected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)));
        Assert.Same(rejected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)));
        Assert.Equal([true], handler.Outcomes);
    }

    [Fact]
    public async Task ShortBody_IsAccountedAtThePadBoundary_ThenZeroPadded()
    {
        var source = new GatedStream([7, 7], []);
        var delivered = 0;
        var handler = new Handler { Consumed = _ => Assert.Equal(2, delivered) };
        await using var stream = new IncrementalSegmentStream(
            source, 0, 4, handler, (_, _) => { }, CancellationToken.None);

        source.Release();
        var output = new List<byte>();
        var buffer = new byte[16];
        int read;
        while ((read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)) > 0)
        {
            output.AddRange(buffer[..read]);
            delivered += read;
        }

        Assert.Equal([7, 7, 0, 0], output);
        Assert.Equal([true], handler.Outcomes);
    }

    [Fact]
    public async Task Recovery_KeepsTheCallersTokenContexts()
    {
        using var cts = new CancellationTokenSource();
        var priority = new DownloadPriorityContext { Priority = SemaphorePriority.High };
        using var _ = cts.Token.SetContext(priority);
        var source = new GatedStream([1, 2, 3], fail: true);
        var handler = new Handler((_, _) => new SegmentReplacement(new MemoryStream([1, 2, 3]), false));
        await using var stream = new IncrementalSegmentStream(source, 0, 3, handler, (_, _) => { }, cts.Token);

        source.Release();
        Assert.Equal([1, 2, 3], await ReadToEndAsync(stream));
        Assert.Same(priority, handler.RecoveryToken.GetContext<DownloadPriorityContext>());
    }

    [Fact]
    public async Task Buffers_ComeFromAndReturnToTheConfiguredPool()
    {
        var pool = new CountingPool();
        var diagnostics = new BufferPoolDiagnostics();
        var body = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var stream = new IncrementalSegmentStream(
            new MemoryStream(body), 0, -1, new Handler(), (_, _) => { }, CancellationToken.None, pool, diagnostics);

        Assert.Equal(body, await ReadToEndAsync(stream));
        await stream.DisposeAsync();

        Assert.True(pool.Rented > 1, "the buffer should grow through the pool");
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Equal(0, diagnostics.Snapshot().CheckedOutBytes);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output).WaitAsync(Timeout);
        return output.ToArray();
    }

    private sealed class PostDeliveryException(int delivered) : Exception
    {
        public int Delivered { get; } = delivered;
    }

    private sealed class Handler(Func<Exception, CancellationToken, SegmentReplacement>? recover = null)
        : IIncrementalSegmentHandler
    {
        private int _waits;

        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RecoveryToken { get; private set; }
        public List<bool> Outcomes { get; } = [];
        public Action<bool>? Consumed { get; init; }
        public int Waits => Volatile.Read(ref _waits);

        public Task<SegmentReplacement> RecoverAsync(Exception failure, CancellationToken cancellationToken)
        {
            RecoveryToken = cancellationToken;
            RecoveryStarted.TrySetResult();
            return Task.FromResult((recover ?? throw new InvalidOperationException("no recovery expected"))(
                failure, cancellationToken));
        }

        public Exception CreatePostDeliveryFailure(Exception failure, int deliveredBytes) =>
            new PostDeliveryException(deliveredBytes);

        public void OnConsumed(bool degraded)
        {
            Outcomes.Add(degraded);
            Consumed?.Invoke(degraded);
        }

        public void OnReaderWaited(TimeSpan elapsed) => Interlocked.Increment(ref _waits);
    }

    private sealed class CountingPool : ISegmentBufferPool
    {
        public int Rented { get; private set; }
        public int Returned { get; private set; }

        public byte[] Rent(int minimumLength)
        {
            Rented++;
            return new byte[minimumLength];
        }

        public void Return(byte[] buffer) => Returned++;
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
