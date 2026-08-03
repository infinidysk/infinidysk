using Microsoft.Extensions.Diagnostics.HealthChecks;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Services;

public class StreamingReadinessCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_LowBudgetWithNoReads_IsHealthy()
    {
        var budget = new InFlightArticleBudget(100);
        using var lease = await budget.LeaseAsync(89, CancellationToken.None);
        var check = CreateCheck(budget);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_SaturatedBudgetWithActiveRead_IsHealthy()
    {
        var budget = new InFlightArticleBudget(100);
        using var lease = await budget.LeaseAsync(90, CancellationToken.None);
        var activeReads = new ActiveReadRegistry();
        activeReads.GetOrCreate("/view/movie.mkv", "client", "movie.mkv", 100);
        var check = CreateCheck(budget, activeReads);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_StuckConditionWithinDwellTime_IsHealthy()
    {
        var budget = new InFlightArticleBudget(100);
        using var lease = await budget.LeaseAsync(90, CancellationToken.None);
        var time = new TestTimeProvider();
        var check = CreateCheck(budget, timeProvider: time);

        var initial = await check.CheckHealthAsync(new HealthCheckContext());
        time.Advance(StreamingReadinessCheck.DwellTime - TimeSpan.FromMilliseconds(1));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, initial.Status);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_StuckConditionPastDwellTime_IsUnhealthy()
    {
        var budget = new InFlightArticleBudget(100);
        using var lease = await budget.LeaseAsync(90, CancellationToken.None);
        var time = new TestTimeProvider();
        var check = CreateCheck(budget, timeProvider: time);

        await check.CheckHealthAsync(new HealthCheckContext());
        time.Advance(StreamingReadinessCheck.DwellTime);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("leased 90 of 100 bytes", result.Description);
        Assert.Equal(90L, result.Data["leasedBytes"]);
        Assert.Equal(0, result.Data["activeReads"]);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenStuckConditionClears_ResetsDwellTime()
    {
        var budget = new InFlightArticleBudget(100);
        var lease = await budget.LeaseAsync(90, CancellationToken.None);
        var time = new TestTimeProvider();
        var check = CreateCheck(budget, timeProvider: time);

        await check.CheckHealthAsync(new HealthCheckContext());
        time.Advance(StreamingReadinessCheck.DwellTime);
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext());

        lease.Dispose();
        var recovered = await check.CheckHealthAsync(new HealthCheckContext());

        using var nextLease = await budget.LeaseAsync(90, CancellationToken.None);
        var restartedDwell = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.Equal(HealthStatus.Healthy, restartedDwell.Status);
    }

    private static StreamingReadinessCheck CreateCheck(
        InFlightArticleBudget budget,
        ActiveReadRegistry? activeReads = null,
        TimeProvider? timeProvider = null) =>
        new(
            budget,
            activeReads ?? new ActiveReadRegistry(),
            timeProvider ?? new TestTimeProvider());

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
