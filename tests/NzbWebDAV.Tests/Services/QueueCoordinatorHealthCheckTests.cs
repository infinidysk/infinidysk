using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class QueueCoordinatorHealthCheckTests
{
    [Theory]
    [InlineData(QueueCoordinatorState.NotStarted, false, false, HealthStatus.Healthy)]
    [InlineData(QueueCoordinatorState.NotStarted, true, false, HealthStatus.Unhealthy)]
    [InlineData(QueueCoordinatorState.Running, true, false, HealthStatus.Healthy)]
    [InlineData(QueueCoordinatorState.Stopped, true, true, HealthStatus.Healthy)]
    [InlineData(QueueCoordinatorState.Stopped, true, false, HealthStatus.Unhealthy)]
    [InlineData(QueueCoordinatorState.Faulted, true, false, HealthStatus.Unhealthy)]
    [InlineData(QueueCoordinatorState.Faulted, true, true, HealthStatus.Unhealthy)]
    public async Task CheckHealthAsync_MatchesLifecycleTruthTable(
        QueueCoordinatorState state,
        bool applicationStarted,
        bool applicationStopping,
        HealthStatus expected)
    {
        using var lifetime = new TestHostApplicationLifetime();
        if (applicationStarted)
            lifetime.SignalStarted();
        if (applicationStopping)
            lifetime.StopApplication();

        var check = new QueueCoordinatorHealthCheck(new FakeLiveness(state), lifetime);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(expected, result.Status);
        if (expected == HealthStatus.Unhealthy)
            Assert.DoesNotContain("Exception", result.Description ?? "", StringComparison.Ordinal);
    }

    private sealed class FakeLiveness(QueueCoordinatorState state) : IQueueCoordinatorLiveness
    {
        public QueueCoordinatorState GetState() => state;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void SignalStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
