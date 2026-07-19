using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Downloads a DavItem into the local prefetch cache ahead of playback, so the next
/// episode in a series can start instantly instead of waiting on usenet. Triggered by
/// the Jellyfin webhook once watch-progress crosses the configured threshold, or by a
/// manual trigger (e.g. from the Explore file browser, or the Phase-1 debug endpoint).
/// </summary>
public class PrefetchCacheService(
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
)
{
    // guards against a single DavItem being prefetched twice concurrently
    // (e.g. two PlaybackProgress webhook events arriving in quick succession).
    private readonly ConcurrentDictionary<Guid, byte> _inProgress = new();

    public enum TriggerResult
    {
        Started,
        Disabled,
        AlreadyCached,
        AlreadyInProgress,
        NotFound,
        CacheFull,
        InsufficientFreeSpace,
    }

    public async Task<TriggerResult> TriggerPrefetchAsync(Guid davItemId, CancellationToken ct = default)
    {
        if (!configManager.IsPrefetchCacheEnabled()) return TriggerResult.Disabled;
        if (!_inProgress.TryAdd(davItemId, 0)) return TriggerResult.AlreadyInProgress;

        // DownloadAsync (once scheduled) owns removing the guard; every other return
        // path below must remove it here instead, tracked via `handedOffToDownload`.
        var handedOffToDownload = false;
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);

            var davItem = await dbClient.GetFileById(davItemId.ToString()).ConfigureAwait(false);
            if (davItem is null || davItem.Type != DavItem.ItemType.UsenetFile)
                return TriggerResult.NotFound;

            var existing = await dbClient.GetCachedEpisodeAsync(davItemId, ct).ConfigureAwait(false);
            if (existing is { Status: CachedEpisode.CacheStatus.Complete } && File.Exists(existing.FilePath))
                return TriggerResult.AlreadyCached;
            if (existing is { Status: CachedEpisode.CacheStatus.Prefetching })
                return TriggerResult.AlreadyInProgress;

            var cachedCount = await dbClient
                .GetCachedEpisodeCountAsync(CachedEpisode.CacheStatus.Complete, ct)
                .ConfigureAwait(false);
            if (cachedCount >= configManager.GetPrefetchCacheMaxEpisodes())
            {
                Log.Information(
                    $"Prefetch cache already holds {cachedCount} episode(s); " +
                    $"skipping prefetch of `{davItem.Path}` until the next eviction sweep frees a slot.");
                return TriggerResult.CacheFull;
            }

            var cacheDir = configManager.GetPrefetchCacheDir();
            Directory.CreateDirectory(cacheDir);

            var estimatedSize = davItem.FileSize ?? 0;
            var minFreeSpaceBytes = (long)(configManager.GetPrefetchCacheMinFreeSpaceGb() * 1024 * 1024 * 1024);
            // pass the cache dir itself (not its drive root) so this respects the actual
            // mount/volume `cache.dir` lives on, e.g. a dedicated Docker volume.
            var driveInfo = new DriveInfo(cacheDir);
            if (driveInfo.AvailableFreeSpace - estimatedSize < minFreeSpaceBytes)
            {
                Log.Information(
                    $"Not enough free space to prefetch `{davItem.Path}` while keeping " +
                    $"{configManager.GetPrefetchCacheMinFreeSpaceGb()}GB free; skipping.");
                return TriggerResult.InsufficientFreeSpace;
            }

            // replace any leftover row from a previously-failed attempt
            if (existing != null)
                await dbClient.RemoveCachedEpisodesAsync([existing.Id], ct).ConfigureAwait(false);

            var filePath = Path.Combine(cacheDir, $"{davItem.Id}.cache");
            var cachedEpisode = new CachedEpisode
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                FilePath = filePath,
                FileSize = 0,
                CachedAt = DateTimeOffset.Now,
                LastAccessedAt = DateTimeOffset.Now,
                Status = CachedEpisode.CacheStatus.Prefetching,
            };
            await dbClient.AddCachedEpisodeAsync(cachedEpisode, ct).ConfigureAwait(false);

            // the actual download runs detached from the triggering webhook/API call;
            // it removes the _inProgress guard itself once it finishes (success or failure).
            handedOffToDownload = true;
            _ = Task.Run(() => DownloadAsync(davItem, cachedEpisode.Id, filePath));
            return TriggerResult.Started;
        }
        finally
        {
            if (!handedOffToDownload) _inProgress.TryRemove(davItemId, out _);
        }
    }

    private async Task DownloadAsync(DavItem davItem, Guid cachedEpisodeId, string filePath)
    {
        try
        {
            await BroadcastStatusAsync(davItem, 0, davItem.FileSize ?? 0).ConfigureAwait(false);

            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var ct = SigtermUtil.GetCancellationToken();

            // deliberately not tagged with a High-priority context: an untagged download
            // resolves to SemaphorePriority.Low (see DownloadingNntpClient.ResolvePriority),
            // the same tier queue/ingestion downloads use, so it never competes evenly
            // with an actively-playing stream.
            await using var sourceStream = await DavFileStreamFactory.GetStreamAsync(
                davItem, dbClient, usenetClient, configManager.GetArticleBufferSize(), ct
            ).ConfigureAwait(false);

            long totalRead;
            await using (var fileStream = new FileStream(
                             filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[256 * 1024];
                totalRead = 0;
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;

                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(2))
                    {
                        lastReport = DateTime.UtcNow;
                        await BroadcastStatusAsync(davItem, totalRead, davItem.FileSize ?? totalRead)
                            .ConfigureAwait(false);
                    }
                }
            }

            await dbContext.CachedEpisodes
                .Where(x => x.Id == cachedEpisodeId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, CachedEpisode.CacheStatus.Complete)
                    .SetProperty(x => x.FileSize, totalRead))
                .ConfigureAwait(false);

            await BroadcastStatusAsync(davItem, totalRead, davItem.FileSize ?? totalRead, isComplete: true)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, $"Failed to prefetch `{davItem.Path}` into the local cache: {e.Message}");
            try
            {
                await using var dbContext = new DavDatabaseContext();
                await dbContext.CachedEpisodes
                    .Where(x => x.Id == cachedEpisodeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CachedEpisode.CacheStatus.Failed))
                    .ConfigureAwait(false);
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (Exception cleanupException)
            {
                Log.Error(cleanupException, $"Failed to clean up failed prefetch attempt for `{davItem.Path}`.");
            }
        }
        finally
        {
            _inProgress.TryRemove(davItem.Id, out _);
        }
    }

    private Task BroadcastStatusAsync(DavItem davItem, long downloadedBytes, long totalBytes, bool isComplete = false)
    {
        var payload = JsonSerializer.Serialize(new
        {
            davItemId = davItem.Id,
            name = davItem.Name,
            downloadedBytes,
            totalBytes,
            isComplete,
        });
        return websocketManager.SendMessage(WebsocketTopic.PrefetchCacheStatus, payload);
    }
}
