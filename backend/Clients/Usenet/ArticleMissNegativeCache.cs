using System.Collections.Concurrent;
using NzbWebDAV.Config;

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
public sealed class ArticleMissNegativeCache
{
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingAt = new(StringComparer.Ordinal);
    private long _hits;

    public ArticleMissNegativeCache(ConfigManager configManager)
    {
        _configManager = configManager;
        configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
            Clear();
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
        _missingAt[key] = DateTimeOffset.UtcNow;
        var maxEntries = _configManager.GetArticleMissCacheMaxEntries();
        if (_missingAt.Count > maxEntries) Cleanup(maxEntries);
    }

    public void Clear() => _missingAt.Clear();

    /// <summary>Test helper: mark an entry as if it were recorded at <paramref name="at"/>.</summary>
    internal void MarkMissingAtForTests(string key, DateTimeOffset at)
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
}
