using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Statistics;

/// <summary>
/// Singleton that tracks per-provider download bytes and "article not found" counts in memory,
/// periodically flushing deltas to the database (cumulative + daily-bucketed) and broadcasting the
/// current state over the "pus" websocket topic. Survives across MultiConnectionNntpClient rebuilds
/// (which happen on every "usenet.providers" config change) since it's registered independently of
/// that lifecycle - see UsenetStreamingClient.
/// </summary>
public class ProviderUsageStatsAggregator
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(20);

    private readonly WebsocketManager _websocketManager;
    private readonly ConcurrentDictionary<Guid, ProviderCounters> _counters = new();
    private readonly ConcurrentDictionary<Guid, ActiveProvider> _activeProviders = new();
    private readonly Timer _flushTimer;

    public ProviderUsageStatsAggregator(WebsocketManager websocketManager)
    {
        _websocketManager = websocketManager;
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    /// <summary>
    /// Loads existing cumulative totals from the database. Must be called once at app startup,
    /// before providers start serving traffic, so in-memory totals don't start from zero.
    /// </summary>
    public async Task LoadAsync()
    {
        await using var dbContext = new DavDatabaseContext();
        var rows = await dbContext.ProviderUsageStats.ToListAsync().ConfigureAwait(false);
        foreach (var row in rows)
        {
            var counters = _counters.GetOrAdd(row.ProviderId, _ => new ProviderCounters());
            counters.CumulativeBytes = row.BytesDownloaded;
            counters.CumulativeNotFound = row.ArticlesNotFoundCount;
            counters.LastUsedAtTicks = row.LastUpdatedAt.UtcTicks;
        }
    }

    /// <summary>
    /// Replaces the full set of currently-wired providers. Called once per provider-client rebuild
    /// (UsenetStreamingClient.CreateMultiProviderClient) so that providers removed from the config
    /// stop appearing in the live broadcast, while their historical DB rows are left untouched.
    /// </summary>
    public void SetActiveProviders(IReadOnlyList<ActiveProvider> providers)
    {
        _activeProviders.Clear();
        foreach (var provider in providers)
            _activeProviders[provider.Id] = provider;
    }

    public void RecordBytesDownloaded(Guid providerId, long bytes)
    {
        var counters = _counters.GetOrAdd(providerId, _ => new ProviderCounters());
        Interlocked.Add(ref counters.CumulativeBytes, bytes);
        Interlocked.Add(ref counters.PendingBytes, bytes);
        Interlocked.Exchange(ref counters.LastUsedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordArticleNotFound(Guid providerId)
    {
        var counters = _counters.GetOrAdd(providerId, _ => new ProviderCounters());
        Interlocked.Increment(ref counters.CumulativeNotFound);
        Interlocked.Increment(ref counters.PendingNotFound);
        Interlocked.Exchange(ref counters.LastUsedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private async Task FlushAsync()
    {
        try
        {
            var deltas = new List<ProviderDelta>();
            foreach (var (providerId, counters) in _counters)
            {
                var bytes = Interlocked.Exchange(ref counters.PendingBytes, 0);
                var notFound = Interlocked.Exchange(ref counters.PendingNotFound, 0);
                if (bytes == 0 && notFound == 0) continue;

                var host = _activeProviders.TryGetValue(providerId, out var active) ? active.Host : "unknown";
                var lastUsedAt = new DateTimeOffset(Volatile.Read(ref counters.LastUsedAtTicks), TimeSpan.Zero);
                deltas.Add(new ProviderDelta(providerId, host, bytes, notFound, lastUsedAt));
            }

            if (deltas.Count > 0)
                await PersistDeltasAsync(deltas).ConfigureAwait(false);

            BroadcastState();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to flush provider usage stats.");
        }
    }

    private static async Task PersistDeltasAsync(List<ProviderDelta> deltas)
    {
        await using var dbContext = new DavDatabaseContext();
        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var dayStartUnix = dayStart.ToUnixTimeSeconds();
        var dayEndUnix = dayEnd.ToUnixTimeSeconds();

        foreach (var delta in deltas)
        {
            var lastUpdatedAtUnix = delta.LastUsedAt.ToUnixTimeSeconds();

            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ProviderUsageStats (ProviderId, ProviderHost, BytesDownloaded, ArticlesNotFoundCount, LastUpdatedAt)
                VALUES ({delta.ProviderId}, {delta.Host}, {delta.Bytes}, {delta.NotFound}, {lastUpdatedAtUnix})
                ON CONFLICT(ProviderId) DO UPDATE SET
                    BytesDownloaded = BytesDownloaded + {delta.Bytes},
                    ArticlesNotFoundCount = ArticlesNotFoundCount + {delta.NotFound},
                    LastUpdatedAt = {lastUpdatedAtUnix},
                    ProviderHost = {delta.Host}
                """).ConfigureAwait(false);

            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ProviderUsageStatsDaily (DateStartInclusive, DateEndExclusive, ProviderId, BytesDownloaded, ArticlesNotFoundCount)
                VALUES ({dayStartUnix}, {dayEndUnix}, {delta.ProviderId}, {delta.Bytes}, {delta.NotFound})
                ON CONFLICT(DateStartInclusive, DateEndExclusive, ProviderId) DO UPDATE SET
                    BytesDownloaded = BytesDownloaded + {delta.Bytes},
                    ArticlesNotFoundCount = ArticlesNotFoundCount + {delta.NotFound}
                """).ConfigureAwait(false);
        }
    }

    private void BroadcastState()
    {
        var entries = _activeProviders.Values.Select(provider =>
        {
            var counters = _counters.GetOrAdd(provider.Id, _ => new ProviderCounters());
            var lastUsedAtTicks = Volatile.Read(ref counters.LastUsedAtTicks);
            return new
            {
                id = provider.Id,
                host = provider.Host,
                bytesDownloaded = Volatile.Read(ref counters.CumulativeBytes),
                articlesNotFound = Volatile.Read(ref counters.CumulativeNotFound),
                isTripped = provider.CircuitBreaker.IsTripped,
                lastUsedAt = lastUsedAtTicks == 0
                    ? null
                    : new DateTimeOffset(lastUsedAtTicks, TimeSpan.Zero).ToString("O")
            };
        }).ToList();

        _websocketManager.SendMessage(WebsocketTopic.ProviderUsageStats, JsonSerializer.Serialize(entries));
    }

    public sealed record ActiveProvider(Guid Id, string Host, ProviderCircuitBreaker CircuitBreaker);

    private sealed record ProviderDelta(Guid ProviderId, string Host, long Bytes, long NotFound, DateTimeOffset LastUsedAt);

    private sealed class ProviderCounters
    {
        public long CumulativeBytes;
        public long CumulativeNotFound;
        public long PendingBytes;
        public long PendingNotFound;
        public long LastUsedAtTicks;
    }
}
