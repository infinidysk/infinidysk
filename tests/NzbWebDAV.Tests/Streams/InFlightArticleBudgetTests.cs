using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class InFlightArticleBudgetTests
{
    [Fact]
    public void AccountBufferedPipeBytes_PositiveNegativeAndZero_MatchLeaseCounter()
    {
        var budget = new InFlightArticleBudget(10_000);
        Assert.Equal(0, budget.LeasedBytes);

        budget.AccountBufferedPipeBytes(4_000);
        Assert.Equal(4_000, budget.LeasedBytes);

        budget.AccountBufferedPipeBytes(0);
        Assert.Equal(4_000, budget.LeasedBytes);

        budget.AccountBufferedPipeBytes(-1_500);
        Assert.Equal(2_500, budget.LeasedBytes);

        budget.AccountBufferedPipeBytes(-2_500);
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public void AccountBufferedPipeBytes_SimulatedBodyLifecycle_ReturnsToBaseline()
    {
        var budget = new InFlightArticleBudget(8_192);
        const int bodyBytes = 3_500;

        budget.AccountBufferedPipeBytes(bodyBytes);
        Assert.Equal(bodyBytes, budget.LeasedBytes);

        budget.AccountBufferedPipeBytes(-(bodyBytes / 2));
        budget.AccountBufferedPipeBytes(-(bodyBytes - bodyBytes / 2));

        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task AccountBufferedPipeBytes_PipeChargeSaturation_WakesFifoWaiterOnNegativeDelta()
    {
        const long cap = 1_000;
        var budget = new InFlightArticleBudget(cap);
        budget.AccountBufferedPipeBytes(cap);
        Assert.Equal(cap, budget.LeasedBytes);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = budget.LeaseAsync(100, cts.Token).AsTask();
        for (var i = 0; i < 50 && budget.ThrottleEvents == 0; i++)
            await Task.Delay(10);

        Assert.True(budget.ThrottleEvents > 0);
        Assert.False(waiter.IsCompleted);

        budget.AccountBufferedPipeBytes(-400);
        using var lease = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(700, budget.LeasedBytes);

        lease.Dispose();
        budget.AccountBufferedPipeBytes(-(cap - 400));
        Assert.Equal(0, budget.LeasedBytes);
    }

    [Fact]
    public async Task AccountBufferedPipeBytes_PositiveDeltaDoesNotWakeWaiter()
    {
        var budget = new InFlightArticleBudget(1_000);
        using var held = await budget.LeaseAsync(1_000, CancellationToken.None);
        var waiter = budget.LeaseAsync(100, CancellationToken.None).AsTask();
        for (var i = 0; i < 50 && budget.ThrottleEvents == 0; i++)
            await Task.Delay(10);

        Assert.True(budget.ThrottleEvents > 0);
        Assert.False(waiter.IsCompleted);

        budget.AccountBufferedPipeBytes(200);
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1_200, budget.LeasedBytes);

        held.Dispose();
        using var lease = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(300, budget.LeasedBytes);

        lease.Dispose();
        budget.AccountBufferedPipeBytes(-200);
        Assert.Equal(0, budget.LeasedBytes);
    }
}
