using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Periodic sweep for the compatibility segment cache: drops expired playback sessions,
/// idle or orphaned segments, and temporary files left by an interrupted encode.
/// </summary>
public sealed class CatalogTranscodeCleanupService(
    CatalogTranscodeService transcodeService,
    CatalogPlaybackSessionStore sessionStore,
    CatalogTranscodeOptions options) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            // Sweep once at startup: a restart never needs to keep the previous run's files.
            Sweep();
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                Sweep();
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private void Sweep()
    {
        try
        {
            var now = options.TimeProvider.GetUtcNow();
            sessionStore.Prune(now);
            transcodeService.Prune(now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Catalog transcode cache cleanup failed: {Reason}", e.Message);
            Log.Debug(e, "Catalog transcode cache cleanup stack");
        }
    }
}
