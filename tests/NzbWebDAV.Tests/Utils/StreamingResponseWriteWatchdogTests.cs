using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class StreamingResponseWriteWatchdogTests
{
    [Fact]
    public void CopyChunkBytes_RemainsSixtyFourKiB()
    {
        Assert.Equal(64 * 1024, StreamingResponseWriteWatchdog.CopyChunkBytes);
    }

    [Fact]
    public async Task ObserveWrite_TrickleUnderContention_SignalsReclaim()
    {
        var budget = new InFlightArticleBudget(1_000);
        var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        using var waiterCts = new CancellationTokenSource();
        var waiter = budget.LeaseAsync(1, waiterCts.Token).AsTask();
        await WaitUntil(() => budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        Assert.True(watchdog.ObserveWrite(100, TimeSpan.FromMilliseconds(50)));
        Assert.False(readCts.IsCancellationRequested);

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task ObserveWrite_TrickleWithoutWaiters_DoesNotReclaim()
    {
        var budget = new InFlightArticleBudget(1_000);
        using var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        Assert.False(budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        Assert.False(watchdog.ObserveWrite(100, TimeSpan.FromMilliseconds(50)));
        Assert.False(readCts.IsCancellationRequested);
    }

    [Fact]
    public async Task ObserveWrite_FullChunkWithWaiters_DoesNotReclaim()
    {
        var budget = new InFlightArticleBudget(1_000);
        var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        using var waiterCts = new CancellationTokenSource();
        var waiter = budget.LeaseAsync(1, waiterCts.Token).AsTask();
        await WaitUntil(() => budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        Assert.False(watchdog.ObserveWrite(
            StreamingResponseWriteWatchdog.CopyChunkBytes,
            TimeSpan.FromMilliseconds(50)));
        Assert.False(readCts.IsCancellationRequested);

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
    }

    [Fact]
    public void ObserveWrite_ZeroTimeout_DisablesAggregateReclaim()
    {
        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.Zero, readCts, new InFlightArticleBudget(1_000));

        Assert.False(watchdog.ObserveWrite(1, TimeSpan.FromSeconds(60)));
        Assert.False(readCts.IsCancellationRequested);
    }

    [Fact]
    public async Task WriteAsync_CancelsReadTokenWhenClientStalls()
    {
        using var readCts = new CancellationTokenSource();
        using var dest = new NeverCompletingWriteStream();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget: null);

        var ex = await Assert.ThrowsAsync<StreamingWriteTimeoutException>(async () =>
            await watchdog.WriteAsync(dest, new byte[1024], CancellationToken.None));

        Assert.True(readCts.IsCancellationRequested);
        Assert.Equal(StreamingWriteTimeoutException.PerWriteStallReason, ex.Reason);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task WriteAsync_TrickleUnderContention_CancelsWithReclaimReason()
    {
        var budget = new InFlightArticleBudget(1_000);
        var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        using var waiterCts = new CancellationTokenSource();
        var waiter = budget.LeaseAsync(1, waiterCts.Token).AsTask();
        await WaitUntil(() => budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        using var dest = new BlockingWriteStream(writeCount: 2);
        var timeProvider = new ManualTimeProvider();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(700), readCts, budget, timeProvider);

        var firstWrite = watchdog.WriteAsync(dest, new byte[100], CancellationToken.None).AsTask();
        await dest.WaitForWriteAsync(0);
        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        dest.ReleaseWrite(0);
        await firstWrite;

        var secondWrite = watchdog.WriteAsync(dest, new byte[100], CancellationToken.None).AsTask();
        await dest.WaitForWriteAsync(1);
        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        dest.ReleaseWrite(1);
        var ex = await Assert.ThrowsAsync<StreamingWriteTimeoutException>(() => secondWrite);

        Assert.True(readCts.IsCancellationRequested);
        Assert.Equal(StreamingWriteTimeoutException.AggregateReclaimReason, ex.Reason);
        Assert.Contains("other streams waited", ex.Message, StringComparison.Ordinal);

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition, int maxAttempts = 50)
    {
        for (var i = 0; i < maxAttempts && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class NeverCompletingWriteStream : Stream
    {
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class BlockingWriteStream(int writeCount) : Stream
    {
        private readonly WriteGate[] _gates = Enumerable.Range(0, writeCount)
            .Select(_ => new WriteGate())
            .ToArray();
        private int _nextWrite;

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public Task WaitForWriteAsync(int index) => _gates[index].Started.Task;

        public void ReleaseWrite(int index) => _gates[index].Release.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var gate = _gates[Interlocked.Increment(ref _nextWrite) - 1];
            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed class WriteGate
        {
            public TaskCompletionSource Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
