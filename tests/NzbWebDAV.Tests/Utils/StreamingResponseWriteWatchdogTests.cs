using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class StreamingResponseWriteWatchdogTests
{
    [Fact]
    public async Task ObserveWrite_TrickleUnderContention_CancelsReadToken()
    {
        var budget = new InFlightArticleBudget(1_000);
        var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        using var waiterCts = new CancellationTokenSource();
        var waiter = budget.LeaseAsync(1, waiterCts.Token).AsTask();
        await WaitUntil(() => budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        var ex = Assert.Throws<StreamingWriteTimeoutException>(
            () => watchdog.ObserveWrite(100, TimeSpan.FromMilliseconds(50)));

        Assert.True(readCts.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);

        await waiterCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task ObserveWrite_TrickleWithoutWaiters_DoesNotCancel()
    {
        var budget = new InFlightArticleBudget(1_000);
        using var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        Assert.False(budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        watchdog.ObserveWrite(100, TimeSpan.FromMilliseconds(50));

        Assert.False(readCts.IsCancellationRequested);
    }

    [Fact]
    public async Task ObserveWrite_FullChunkWithWaiters_DoesNotCancel()
    {
        var budget = new InFlightArticleBudget(1_000);
        var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        using var waiterCts = new CancellationTokenSource();
        var waiter = budget.LeaseAsync(1, waiterCts.Token).AsTask();
        await WaitUntil(() => budget.HasWaiters);

        using var readCts = new CancellationTokenSource();
        var watchdog = new StreamingResponseWriteWatchdog(
            TimeSpan.FromMilliseconds(50), readCts, budget);

        watchdog.ObserveWrite(
            StreamingResponseWriteWatchdog.CopyChunkBytes,
            TimeSpan.FromMilliseconds(50));

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

        watchdog.ObserveWrite(1, TimeSpan.FromSeconds(60));

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
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
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
}
