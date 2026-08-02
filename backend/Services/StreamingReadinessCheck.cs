using Microsoft.Extensions.Diagnostics.HealthChecks;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Services;

public sealed class StreamingReadinessCheck : IHealthCheck
{
    internal static readonly TimeSpan DwellTime = TimeSpan.FromSeconds(30);
    private const double SaturationThreshold = 0.9;

    private readonly InFlightArticleBudget _budget;
    private readonly ActiveReadRegistry _activeReads;
    private readonly TimeProvider _timeProvider;
    private long _stuckSinceUtcTicks;

    public StreamingReadinessCheck(
        InFlightArticleBudget budget,
        ActiveReadRegistry activeReads)
        : this(budget, activeReads, TimeProvider.System)
    {
    }

    internal StreamingReadinessCheck(
        InFlightArticleBudget budget,
        ActiveReadRegistry activeReads,
        TimeProvider timeProvider)
    {
        _budget = budget;
        _activeReads = activeReads;
        _timeProvider = timeProvider;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var leasedBytes = _budget.LeasedBytes;
        var capBytes = _budget.CapBytes;
        var activeReadCount = _activeReads.Count;
        var isSaturated = leasedBytes > 0
            && leasedBytes >= capBytes * SaturationThreshold;

        if (!isSaturated || activeReadCount > 0)
        {
            Interlocked.Exchange(ref _stuckSinceUtcTicks, 0);
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        var now = _timeProvider.GetUtcNow();
        var stuckSinceUtcTicks = Interlocked.Read(ref _stuckSinceUtcTicks);
        if (stuckSinceUtcTicks == 0)
        {
            stuckSinceUtcTicks = Interlocked.CompareExchange(
                ref _stuckSinceUtcTicks,
                now.UtcTicks,
                0);
            if (stuckSinceUtcTicks == 0)
                stuckSinceUtcTicks = now.UtcTicks;
        }

        var stuckFor = now - new DateTimeOffset(stuckSinceUtcTicks, TimeSpan.Zero);
        if (stuckFor < DwellTime)
            return Task.FromResult(HealthCheckResult.Healthy());

        var description =
            $"Streaming is stuck: article memory has remained saturated with no active reads " +
            $"for {stuckFor.TotalSeconds:F0}s (leased {leasedBytes} of {capBytes} bytes).";
        var data = new Dictionary<string, object>
        {
            ["leasedBytes"] = leasedBytes,
            ["capBytes"] = capBytes,
            ["activeReads"] = activeReadCount,
            ["stuckForSeconds"] = stuckFor.TotalSeconds,
        };

        return Task.FromResult(HealthCheckResult.Unhealthy(description, data: data));
    }
}
