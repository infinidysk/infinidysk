using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Services.Observability;

public sealed class PrometheusMetricsCollector(
    PrometheusMetrics metrics,
    ActiveReadRegistry activeReads,
    ConcurrentReadTracker concurrentReads,
    MetricsWriter metricsWriter,
    UsenetStreamingClient usenetClient,
    RepairPatchStore repairPatchStore) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (InFlightArticleBudget.Current is { } budget)
                {
                    metrics.Refresh(activeReads, concurrentReads, budget, metricsWriter, usenetClient);
                    metrics.SetPar2PatchStoreBytes(repairPatchStore.CurrentBytes);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "Prometheus metrics snapshot refresh failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
