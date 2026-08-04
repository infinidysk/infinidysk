using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ProviderRecoveryProbeService(
    UsenetStreamingClient usenetClient) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await usenetClient.ProbeLatchedProvidersAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    Log.Debug(e, "Provider recovery probe round failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }
}
