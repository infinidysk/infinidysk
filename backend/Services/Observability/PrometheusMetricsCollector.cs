using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services.Diagnostics;
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
    RepairPatchStore repairPatchStore,
    HealthCheckConnectionGate healthCheckConnectionGate,
    SegmentCacheStatistics segmentCacheStatistics,
    MemoryComponentSnapshotBuilder memorySnapshotBuilder,
    NativeCache.NativeCacheService? nativeCache = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (InFlightArticleBudget.Current is { } budget)
                    {
                        metrics.Refresh(
                            memorySnapshotBuilder.Capture(),
                            activeReads,
                            concurrentReads,
                            metricsWriter,
                            usenetClient);
                        metrics.SetPar2PatchStoreBytes(repairPatchStore.CurrentBytes);
                    }
                    metrics.SetHealthCheckGate(healthCheckConnectionGate.GetSnapshot());
                    metrics.SetSegmentCache(segmentCacheStatistics.GetSnapshot());
                    if (nativeCache is not null)
                        metrics.SetNativeCache(nativeCache.Statistics.Snapshot(), nativeCache.ReservedBufferBytes, nativeCache.Store is not null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Debug(ex, "Prometheus metrics snapshot refresh failed");
                }

                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            metrics.ClearHealthCheckGate();
        }
    }
}
