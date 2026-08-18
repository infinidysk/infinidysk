using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Bounded TTL negative cache for definitive per-provider (or per-storage-group)
/// article misses. Without this, every new streaming/batch request re-probes a
/// provider that has already returned a definitive 430/451 for the same article,
/// amplifying retries and failover metrics under incomplete retention.
///
/// Keying: articles behind providers that share a <c>StorageGroup</c> label use a
/// group-scoped key (a miss on one sibling applies to all of them); providers
/// without a storage group use a provider-scoped key (<see cref="MultiConnectionNntpClient.MetricsKey"/>).
///
/// Coherence with <c>MultiProviderNntpClient</c>'s "retry primary once" batch policy:
/// a fresh per-request 430 on the primary provider must never be marked missing
/// here until the intentional immediate retry has *also* missed — otherwise the
/// retry itself would find the cache already primed and skip, defeating the retry.
/// Callers therefore only call <see cref="MarkMissing"/> for the primary provider
/// from the retry attempt, never from the initial batch response. See
/// <c>MultiProviderNntpClient.ResolveBatchResponseAsync</c>.
///
/// Never call <see cref="MarkMissing"/> for timeouts, socket/IO errors, corrupt
/// articles, auth/connect failures, protocol errors, or cancellation — only a
/// definitive miss (<see cref="UsenetArticleAvailability.IsDefinitiveMissing"/>)
/// belongs in this cache.
/// </summary>
public sealed class ArticleMissNegativeCache : IHostedService, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly Func<DavDatabaseContext>? _contextFactory;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private long _hits;

    public ArticleMissNegativeCache(
        ConfigManager configManager,
        Func<DavDatabaseContext>? contextFactory = null)
    {
        _configManager = configManager;
        _contextFactory = contextFactory;
        configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
            Clear();
            _ = ClearPersistedAsync();
        };
    }

    /// <summary>Cumulative count of cache hits (probes skipped because of a cached miss).</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Alias of <see cref="Hits"/> — a cache hit is always a skipped probe.</summary>
    public long Skips => Hits;

    public int Entries => _missingAt.Count;

    /// <summary>
    /// Builds the cache key for an article on a given provider. When the provider
    /// has a non-empty storage group, the key is scoped to the group so siblings
    /// sharing that upstream storage are skipped together; otherwise it is scoped
    /// to the individual provider.
    /// </summary>
    public static string BuildKey(string articleId, string metricsKey, string? storageGroup)
    {
        var group = storageGroup?.Trim() ?? "";
        return group.Length > 0
            ? $"{articleId}\u0001g:{group}"
            : $"{articleId}\u0001p:{metricsKey}";
    }

    public bool IsMissing(string key)
    {
        if (!_missingAt.TryGetValue(key, out var markedAt)) return false;
        if (DateTimeOffset.UtcNow - markedAt < _configManager.GetArticleMissCacheTtl())
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        _missingAt.TryRemove(key, out _);
        return false;
    }

    public void MarkMissing(string key)
    {
        var now = DateTimeOffset.UtcNow;
        MarkMissingInMemory(key, now);
        _ = PersistMissingAsync(key, now);
    }

    public void Clear() => _missingAt.Clear();

    /// <summary>
    /// Hydrates unexpired misses before NNTP traffic starts. A DB failure leaves the
    /// in-memory cache usable; definitive misses must never block streaming.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_contextFactory is null) return;

        try
        {
            var cutoff = DateTimeOffset.UtcNow - _configManager.GetArticleMissCacheTtl();
            var cutoffUnix = cutoff.ToUnixTimeMilliseconds();
            var maxEntries = _configManager.GetArticleMissCacheMaxEntries();
            await using var context = _contextFactory();
            var entries = await context.ArticleMissCacheEntries
                .AsNoTracking()
                .Where(x => x.ConfirmedAtUnix >= cutoffUnix)
                .OrderByDescending(x => x.ConfirmedAtUnix)
                .Take(maxEntries)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in entries)
                _missingAt[entry.CacheKey] = DateTimeOffset.FromUnixTimeMilliseconds(entry.ConfirmedAtUnix);

            await TrimPersistedAsync(context, cutoffUnix, maxEntries, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warning(e, "Unable to hydrate persistent article-miss cache; continuing with memory-only misses.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _persistenceGate.Dispose();

    /// <summary>Test helper: mark an entry as if it were recorded at <paramref name="at"/>.</summary>
    internal void MarkMissingAtForTests(string key, DateTimeOffset at)
    {
        MarkMissingInMemory(key, at);
    }

    internal async Task MarkMissingAndPersistForTestsAsync(string key)
    {
        var now = DateTimeOffset.UtcNow;
        MarkMissingInMemory(key, now);
        await PersistMissingAsync(key, now).ConfigureAwait(false);
    }

    private void MarkMissingInMemory(string key, DateTimeOffset at)
    {
        _missingAt[key] = at;
        var maxEntries = _configManager.GetArticleMissCacheMaxEntries();
        if (_missingAt.Count > maxEntries) Cleanup(maxEntries);
    }

    private void Cleanup(int maxEntries)
    {
        var cutoff = DateTimeOffset.UtcNow - _configManager.GetArticleMissCacheTtl();
        foreach (var kv in _missingAt)
            if (kv.Value < cutoff) _missingAt.TryRemove(kv.Key, out _);

        var overflow = _missingAt.Count - maxEntries;
        if (overflow <= 0) return;
        // Still over the cap after expiring stale rows — evict the oldest marks
        // (approximate LRU) so runaway cardinality can't grow unbounded.
        foreach (var kv in _missingAt.OrderBy(kv => kv.Value).Take(overflow))
            _missingAt.TryRemove(kv.Key, out _);
    }

    private async Task PersistMissingAsync(string key, DateTimeOffset confirmedAt)
    {
        if (_contextFactory is null) return;

        try
        {
            await _persistenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var context = _contextFactory();
                var existing = await context.ArticleMissCacheEntries.FindAsync([key])
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    context.ArticleMissCacheEntries.Add(new ArticleMissCacheEntry
                    {
                        CacheKey = key,
                        ConfirmedAtUnix = confirmedAt.ToUnixTimeMilliseconds(),
                    });
                }
                else
                {
                    existing.ConfirmedAtUnix = confirmedAt.ToUnixTimeMilliseconds();
                }

                await context.SaveChangesAsync().ConfigureAwait(false);
                var cutoffUnix = (DateTimeOffset.UtcNow - _configManager.GetArticleMissCacheTtl())
                    .ToUnixTimeMilliseconds();
                await TrimPersistedAsync(
                    context, cutoffUnix, _configManager.GetArticleMissCacheMaxEntries(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Unable to persist definitive article miss; retaining memory-only entry.");
        }
    }

    private async Task ClearPersistedAsync()
    {
        if (_contextFactory is null) return;

        try
        {
            await _persistenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var context = _contextFactory();
                await context.ArticleMissCacheEntries.ExecuteDeleteAsync().ConfigureAwait(false);
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Unable to clear persistent article-miss cache after provider configuration changed.");
        }
    }

    private static async Task TrimPersistedAsync(
        DavDatabaseContext context,
        long cutoffUnix,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        await context.ArticleMissCacheEntries
            .Where(x => x.ConfirmedAtUnix < cutoffUnix)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var overflow = await context.ArticleMissCacheEntries.CountAsync(cancellationToken)
            .ConfigureAwait(false) - maxEntries;
        if (overflow <= 0) return;

        var oldestKeys = await context.ArticleMissCacheEntries
            .OrderBy(x => x.ConfirmedAtUnix)
            .ThenBy(x => x.CacheKey)
            .Take(overflow)
            .Select(x => x.CacheKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (oldestKeys.Count > 0)
        {
            await context.ArticleMissCacheEntries
                .Where(x => oldestKeys.Contains(x.CacheKey))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
