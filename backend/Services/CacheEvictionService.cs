using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Periodically enforces the prefetch cache's retention policy: max age, max episode
/// count, and a hard minimum-free-space floor (which wins over the other two if they
/// conflict, since it protects the host system). Unlike the *CleanupItem-backed cleanup
/// services elsewhere, this isn't queue/trigger-driven -- it's ongoing policy
/// enforcement against state that changes independently of any single DB write, so a
/// periodic sweep is the right shape here.
/// </summary>
public class CacheEvictionService(ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleAttemptCutoff = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configManager.IsPrefetchCacheEnabled())
                    await RunSweepAsync(stoppingToken).ConfigureAwait(false);

                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error during prefetch-cache eviction sweep: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);

        await RemoveStaleAttemptsAsync(dbClient, ct).ConfigureAwait(false);
        await EvictExpiredByAgeAsync(dbClient, ct).ConfigureAwait(false);
        await EvictExcessByCountAsync(dbClient, ct).ConfigureAwait(false);
        await EvictUntilFreeSpaceRestoredAsync(dbClient, ct).ConfigureAwait(false);
    }

    // crashed/interrupted prefetch attempts (Prefetching or Failed rows) never get
    // cleaned up by the age/count/free-space policies below, since those only look at
    // Complete entries -- so they need their own bounded cleanup.
    private async Task RemoveStaleAttemptsAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.Now - StaleAttemptCutoff;
        var stale = await dbClient.GetStaleCachedEpisodesAsync(cutoff, ct).ConfigureAwait(false);
        await DeleteAsync(dbClient, stale, "stale/failed prefetch attempt", ct).ConfigureAwait(false);
    }

    private async Task EvictExpiredByAgeAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromHours(configManager.GetPrefetchCacheMaxTimeHours());
        var cutoff = DateTimeOffset.Now - maxAge;
        var expired = await dbClient.GetCachedEpisodesOlderThanAsync(cutoff, ct).ConfigureAwait(false);
        await DeleteAsync(dbClient, expired, "max cache time exceeded", ct).ConfigureAwait(false);
    }

    private async Task EvictExcessByCountAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        var maxEpisodes = configManager.GetPrefetchCacheMaxEpisodes();
        var complete = await dbClient
            .GetCachedEpisodesOrderedByAgeAsync(CachedEpisode.CacheStatus.Complete, ct)
            .ConfigureAwait(false);
        if (complete.Count <= maxEpisodes) return;

        var excess = complete.Take(complete.Count - maxEpisodes).ToList();
        await DeleteAsync(dbClient, excess, "max cache episodes exceeded", ct).ConfigureAwait(false);
    }

    private async Task EvictUntilFreeSpaceRestoredAsync(DavDatabaseClient dbClient, CancellationToken ct)
    {
        var cacheDir = configManager.GetPrefetchCacheDir();
        if (!Directory.Exists(cacheDir)) return;

        var minFreeSpaceBytes = (long)(configManager.GetPrefetchCacheMinFreeSpaceGb() * 1024 * 1024 * 1024);
        var driveInfo = new DriveInfo(cacheDir);
        if (driveInfo.AvailableFreeSpace >= minFreeSpaceBytes) return;

        var candidates = await dbClient
            .GetCachedEpisodesOrderedByAgeAsync(CachedEpisode.CacheStatus.Complete, ct)
            .ConfigureAwait(false);

        // DriveInfo's properties query the OS live, so re-checking AvailableFreeSpace
        // after each delete (no explicit refresh call needed) is enough to stop early.
        foreach (var candidate in candidates)
        {
            if (driveInfo.AvailableFreeSpace >= minFreeSpaceBytes) break;
            await DeleteAsync(dbClient, [candidate], "minimum free space enforcement", ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteAsync
    (
        DavDatabaseClient dbClient,
        List<CachedEpisode> entries,
        string reason,
        CancellationToken ct
    )
    {
        if (entries.Count == 0) return;

        foreach (var entry in entries)
        {
            try
            {
                if (File.Exists(entry.FilePath)) File.Delete(entry.FilePath);
            }
            catch (Exception e)
            {
                Log.Warning(e, $"Failed to delete cached file `{entry.FilePath}` during eviction: {e.Message}");
            }
        }

        await dbClient.RemoveCachedEpisodesAsync(entries.Select(x => x.Id).ToList(), ct).ConfigureAwait(false);
        Log.Information(
            $"Prefetch cache: evicted {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} ({reason}).");
    }
}
