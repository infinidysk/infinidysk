using Microsoft.Extensions.Hosting;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Samples the aggregate provider fetch rate once per second so the overview can
/// report a true momentary peak. Only the per-minute maximum is retained in
/// memory; MetricsRollupService persists it on its own tick.
/// </summary>
public class FetchPeakSamplerService(ProviderBytesTracker bytesTracker) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;
                bytesTracker.SampleFetchRate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ex.LogWarningKnownOrStack("FetchPeakSamplerService tick failed.");
            }
        }
    }
}
