using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace NzbWebDAV.Services;

public sealed class QueueCoordinatorHealthCheck(
    IQueueCoordinatorLiveness liveness,
    IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = liveness.GetState();
        var result = state switch
        {
            QueueCoordinatorState.NotStarted
                when !lifetime.ApplicationStarted.IsCancellationRequested =>
                HealthCheckResult.Healthy("Queue coordinator is waiting for application startup."),

            QueueCoordinatorState.NotStarted =>
                HealthCheckResult.Unhealthy(
                    "Queue coordinator did not start after application startup."),

            QueueCoordinatorState.Running =>
                HealthCheckResult.Healthy(),

            QueueCoordinatorState.Stopped
                when lifetime.ApplicationStopping.IsCancellationRequested =>
                HealthCheckResult.Healthy("Queue coordinator is stopping."),

            QueueCoordinatorState.Stopped =>
                HealthCheckResult.Unhealthy(
                    "Queue coordinator stopped while the host is still running."),

            QueueCoordinatorState.Faulted =>
                HealthCheckResult.Unhealthy(
                    "Queue coordinator terminated unexpectedly; process restart is required."),

            _ => HealthCheckResult.Unhealthy("Queue coordinator state is invalid."),
        };

        return Task.FromResult(result);
    }
}
