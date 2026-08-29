using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

public class UsenetBandwidthLimiterTests
{
    [Fact]
    public async Task AcquireAsync_Unlimited_CompletesSynchronouslyWithoutCharging()
    {
        var limiter = new UsenetBandwidthLimiter();
        var task = limiter.AcquireAsync(1_000_000, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, limiter.TotalChargedBytes);
    }

    [Fact]
    public async Task AcquireAsync_WithinBurst_CompletesImmediatelyAndCharges()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(1_000_000);

        await limiter.AcquireAsync(64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024, limiter.TotalChargedBytes);
    }

    [Fact]
    public async Task AcquireAsync_AboveBurst_DoesNotCompleteImmediatelyAtLowRate()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);

        var task = limiter.AcquireAsync(64 * 1024, CancellationToken.None).AsTask();
        Assert.False(task.IsCompleted);

        var started = time.Now;
        await PumpUntilCompleted(task, time, TimeSpan.FromSeconds(10));
        Assert.Equal(64 * 1024, limiter.TotalChargedBytes);
        Assert.True(time.Now - started >= MinElapsed(64 * 1024, 10_000, consumeBurst: true));
    }

    [Fact]
    public async Task AcquireAsync_AfterBurst_WaitsForRefillAtConfiguredRate()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);
        await limiter.AcquireAsync(Burst(10_000), CancellationToken.None);

        var waiting = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);

        var started = time.Now;
        await PumpUntilCompleted(waiting, time, TimeSpan.FromSeconds(2));
        Assert.Equal(Burst(10_000) + 10_000, limiter.TotalChargedBytes);
        Assert.True(time.Now - started >= MinElapsed(10_000, 10_000, consumeBurst: false));
    }

    [Fact]
    public async Task AcquireAsync_OversizedGrant_WaitsInsteadOfBypassingTheCap()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);

        var first = limiter.AcquireAsync(200_000, CancellationToken.None).AsTask();
        Assert.False(first.IsCompleted);

        var started = time.Now;
        await PumpUntilCompleted(first, time, TimeSpan.FromSeconds(25));
        Assert.Equal(200_000, limiter.TotalChargedBytes);
        Assert.True(time.Now - started >= MinElapsed(200_000, 10_000, consumeBurst: true));

        var waiting = limiter.AcquireAsync(1, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);

        await PumpUntilCompleted(waiting, time, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AcquireAsync_Fifo_SecondWaiterDoesNotOvertakeFirst()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);
        await limiter.AcquireAsync(Burst(10_000), CancellationToken.None);

        var first = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        var second = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await PumpUntilCompleted(first, time, TimeSpan.FromSeconds(2));
        Assert.False(second.IsCompleted);

        await PumpUntilCompleted(second, time, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AcquireAsync_CancelledWaiter_ReleasesFifoHead()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);
        await limiter.AcquireAsync(Burst(10_000), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cancelled = limiter.AcquireAsync(10_000, cts.Token).AsTask();
        var next = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(next.IsCompleted);

        await PumpUntilCompleted(next, time, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UpdateLimit_Zero_CompletesWaitersWithoutChargingThem()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(10_000);
        await limiter.AcquireAsync(Burst(10_000), CancellationToken.None);
        var charged = limiter.TotalChargedBytes;

        var waiting = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);

        limiter.UpdateLimit(0);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(charged, limiter.TotalChargedBytes);
        Assert.Equal(0, limiter.BytesPerSecond);
    }

    [Fact]
    public async Task UpdateLimit_Increase_WakesWaiterSooner()
    {
        var time = new ControllableTimeProvider();
        var limiter = new UsenetBandwidthLimiter(time);
        limiter.UpdateLimit(1_000);
        await limiter.AcquireAsync(Burst(1_000), CancellationToken.None);

        var waiting = limiter.AcquireAsync(10_000, CancellationToken.None).AsTask();
        limiter.UpdateLimit(10_000);
        await PumpUntilCompleted(waiting, time, TimeSpan.FromSeconds(2));
    }

    private static int Burst(long bytesPerSecond) => (int)Math.Max(1, bytesPerSecond * 0.25);

    private static TimeSpan MinElapsed(int requestedBytes, long bytesPerSecond, bool consumeBurst)
    {
        var remaining = consumeBurst
            ? Math.Max(0, requestedBytes - Burst(bytesPerSecond))
            : requestedBytes;
        return TimeSpan.FromSeconds(remaining / (double)bytesPerSecond);
    }

    private static async Task PumpUntilCompleted(Task task, ControllableTimeProvider time, TimeSpan max)
    {
        var maxSteps = Math.Max(1, (int)(max.TotalMilliseconds / 50) + 1);
        for (var step = 0; !task.IsCompleted && step < maxSteps; step++)
            time.Advance(TimeSpan.FromMilliseconds(50));

        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
