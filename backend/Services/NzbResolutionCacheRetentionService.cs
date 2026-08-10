using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Hydrates the in-memory play-token cache from SQLite after the host has started
/// scheduling background work (so Kestrel can bind and /health can answer), then
/// hourly purges groups older than the configured TTL.
/// </summary>
public class NzbResolutionCacheRetentionService(
    NzbResolutionCache cache, ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hydrate here rather than in StartAsync: reading the whole non-expired token
        // table can outlast the entrypoint's /health retry window, and gating host
        // startup on it boot-looped containers on upgrade (#665).
        try
        {
            await cache.HydrateAsync(configManager.GetPlayResolutionCacheTtl(), stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.IsCancellationException(stoppingToken))
        {
            Log.Warning("Play-token cache hydrate stopped because nzbdav is shutting down");
            Log.Debug(ex, "Play-token cache hydrate cancellation stack");
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning("Failed to hydrate play-token cache; starting empty. Reason: {Reason}", ex.Message);
            Log.Debug(ex, "Play-token cache hydrate failure stack");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await cache.PurgeExpiredAsync(configManager.GetPlayResolutionCacheTtl(), stoppingToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception ex) when (ex.IsCancellationException() &&
#pragma warning restore CA2016
                                      (stoppingToken.IsCancellationRequested ||
                                       SigtermUtil.IsSigtermTriggered()))
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warning("Play-token retention sweep failed. Reason: {Reason}", ex.Message);
                Log.Debug(ex, "Play-token retention sweep failure stack");
            }
        }
    }
}
