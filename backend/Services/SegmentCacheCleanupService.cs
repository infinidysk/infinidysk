using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Preserves legacy startup cleanup for installations without an explicit cache mode.
/// Explicit mode selections retain inactive files for a later switch back.
/// </summary>
public sealed class SegmentCacheCleanupService(ConfigManager configManager) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Explicit modes retain inactive data so switching modes never deletes a user's cache.
        if (configManager.HasExplicitCacheMode() || configManager.GetActiveCacheMode() == CacheMode.Segment)
            return Task.CompletedTask;

        var cacheDir = configManager.GetSegmentCachePath();
        return Task.Run(() => Purge(cacheDir), stoppingToken);
    }

    private static void Purge(string cacheDir)
    {
        var pathClass = SegmentCacheNntpClient.ClassifyCachePath(cacheDir);
        try
        {
            var result = SegmentCacheNntpClient.PurgeDirectory(cacheDir);
            if (result.Deleted == 0 && result.Failed == 0) return;

            if (result.Failed > 0)
            {
                Log.Warning(
                    "Segment cache is disabled; some leftover cache files at {PathClass} could not be purged. " +
                    "Deleted: {Deleted}. Skipped: {Skipped}. Failed: {Failed}. Reason: {Reason}",
                    pathClass, result.Deleted, result.Skipped, result.Failed, result.FailureReason);
                return;
            }

            Log.Information(
                "Segment cache is disabled; purged leftover cache files at {PathClass}. " +
                "Deleted: {Deleted}. Skipped: {Skipped}.",
                pathClass, result.Deleted, result.Skipped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(
                "Segment cache is disabled but leftover cache files at {PathClass} could not be purged. Reason: {Reason}",
                pathClass, e.Message);
            Log.Debug(e, "Segment cache purge failure stack");
        }
    }
}
