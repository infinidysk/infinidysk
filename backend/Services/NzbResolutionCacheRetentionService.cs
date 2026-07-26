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
        // Hydrate in ExecuteAsync (not StartAsync) so host StartAsync returns and
        // Kestrel can bind before SQLite open/query finishes. Blocking StartAsync on
        // hydrate caused a container boot-loop when the entrypoint's 30s /health
        // window expired mid-open (#665).
        try
        {
            await cache.HydrateAsync(configManager.GetPlayResolutionCacheTtl(), stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.IsCancellationException(stoppingToken))
        {
            Log.Warning("Play-token cache hydrate skipped. Reason: {Reason}", "nzbdav is shutting down");
            Log.Debug(ex, "Play-token cache hydrate cancelled stack");
            return;
        }
        catch (Exception ex)
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
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception ex) when (ex.IsCancellationException(stoppingToken))
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("Play-token retention sweep failed. Reason: {Reason}", ex.Message);
                Log.Debug(ex, "Play-token retention sweep failure stack");
            }
        }
    }
}
