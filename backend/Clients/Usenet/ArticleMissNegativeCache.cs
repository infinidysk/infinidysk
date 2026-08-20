using System.Collections.Concurrent;
using System.Threading.Channels;
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
///
/// Persistence is queued on a bounded channel drained in FIFO order by a single
/// background consumer (started in <see cref="StartAsync"/>), so provider-change
/// clears cannot be reordered behind earlier marks. <see cref="StopAsync"/>
/// completes the queue and waits for the drain, so graceful restarts lose neither
/// recently confirmed misses nor a pending clear. Marks that arrive while the
/// queue is full stay memory-only — safe, because the in-memory cache is
/// authoritative for the running process.
/// </summary>
public sealed class ArticleMissNegativeCache : IHostedService, IDisposable
{
    private const int PersistenceQueueCapacity = 4096;
    private const int MaxPersistenceBatchSize = 256;

    private readonly ConfigManager _configManager;
    private readonly Func<DavDatabaseContext>? _contextFactory;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingAt = new(StringComparer.Ordinal);
    private readonly Channel<PersistenceWorkItem>? _persistenceQueue;
    private CancellationTokenSource? _persistenceLoopCts;
    private Task _persistenceLoop = Task.CompletedTask;
    private volatile bool _persistenceLoopStarted;
    private long _hits;

    private abstract record PersistenceWorkItem;

    private sealed record MarkItem(string Key, long ConfirmedAtUnix) : PersistenceWorkItem;

    private sealed record ClearItem : PersistenceWorkItem
    {
        public static readonly ClearItem Instance = new();
    }

    private sealed record BarrierItem(TaskCompletionSource Completion) : PersistenceWorkItem;

    public ArticleMissNegativeCache(
        ConfigManager configManager,
        Func<DavDatabaseContext>? contextFactory = null)
    {
        _configManager = configManager;
        _contextFactory = contextFactory;
        if (contextFactory is not null)
        {
            _persistenceQueue = Channel.CreateBounded<PersistenceWorkItem>(
                new BoundedChannelOptions(PersistenceQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite,
                });
        }

        configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
            Clear();
            EnqueueClear();
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
        _persistenceQueue?.Writer.TryWrite(new MarkItem(key, now.ToUnixTimeMilliseconds()));
    }

    public void Clear() => _missingAt.Clear();

    /// <summary>
    /// Hydrates unexpired misses before NNTP traffic starts, then starts the
    /// background persistence consumer. A DB failure leaves the in-memory cache
    /// usable; definitive misses must never block streaming.
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

        // The loop outlives startup; it stops when StopAsync/Dispose completes the queue.
        // The loop token is cancelled only if the host ShutdownTimeout elapses mid-drain.
        _persistenceLoopCts?.Dispose();
        _persistenceLoopCts = new CancellationTokenSource();
        var loopToken = _persistenceLoopCts.Token;
        _persistenceLoop = Task.Run(() => RunPersistenceLoopAsync(loopToken), CancellationToken.None);
        _persistenceLoopStarted = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_persistenceQueue is null) return;
        _persistenceQueue.Writer.TryComplete();
        try
        {
            await _persistenceLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_persistenceLoopCts is not null)
                await _persistenceLoopCts.CancelAsync().ConfigureAwait(false);
            Log.Warning("Timed out draining the article-miss persistence queue; recent definitive misses may be lost.");
        }
    }

    public void Dispose()
    {
        _persistenceQueue?.Writer.TryComplete();
        _persistenceLoopCts?.Cancel();
        _persistenceLoopCts?.Dispose();
        _persistenceLoopCts = null;
    }

    /// <summary>Test helper: mark an entry as if it were recorded at <paramref name="at"/>.</summary>
    internal void MarkMissingAtForTests(string key, DateTimeOffset at)
    {
        MarkMissingInMemory(key, at);
    }

    internal async Task MarkMissingAndPersistForTestsAsync(string key)
    {
        MarkMissing(key);
        await FlushPersistenceForTestsAsync().ConfigureAwait(false);
    }

    /// <summary>Test helper: wait until every work item queued so far has been applied.</summary>
    internal async Task FlushPersistenceForTestsAsync()
    {
        if (_persistenceQueue is null) return;
        if (!_persistenceLoopStarted)
            throw new InvalidOperationException("StartAsync must be called before flushing persistence.");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _persistenceQueue.Writer.WriteAsync(new BarrierItem(barrier)).ConfigureAwait(false);
        await barrier.Task.ConfigureAwait(false);
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

    private void EnqueueClear()
    {
        if (_persistenceQueue is null) return;
        if (_persistenceQueue.Writer.TryWrite(ClearItem.Instance)) return;
        // The queue is momentarily full of pending marks and drains quickly; wait
        // for room so a provider-change clear is never dropped.
        _ = Task.Run(async () =>
        {
            try
            {
                await _persistenceQueue.Writer.WriteAsync(ClearItem.Instance).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Shutdown raced the config change; nothing left to drain into.
            }
        });
    }

    private async Task RunPersistenceLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _persistenceQueue!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
            var marks = new Dictionary<string, long>(StringComparer.Ordinal);
            var clearPending = false;
            TaskCompletionSource? barrier = null;
            var itemsRead = 0;
            while (itemsRead < MaxPersistenceBatchSize && reader.TryRead(out var item))
            {
                itemsRead++;
                switch (item)
                {
                    case ClearItem:
                        // Marks queued before the clear must not survive it.
                        marks.Clear();
                        clearPending = true;
                        break;
                    case MarkItem mark:
                        marks[mark.Key] = mark.ConfirmedAtUnix;
                        break;
                    case BarrierItem b:
                        barrier = b.Completion;
                        break;
                }
                if (barrier is not null) break;
            }

            try
            {
                if (clearPending || marks.Count > 0)
                    await ApplyBatchAsync(clearPending, marks, cancellationToken).ConfigureAwait(false);
                barrier?.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                barrier?.TrySetCanceled(cancellationToken);
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Unable to persist definitive article misses; retaining memory-only entries.");
                barrier?.TrySetException(e);
            }
        }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is stopping; remaining marks stay memory-only.
        }
    }

    private async Task ApplyBatchAsync(
        bool clearPending,
        Dictionary<string, long> marks,
        CancellationToken cancellationToken)
    {
        await using var context = _contextFactory!();
        if (clearPending)
            await context.ArticleMissCacheEntries.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (marks.Count > 0)
        {
            var keys = marks.Keys.ToList();
            var existing = await context.ArticleMissCacheEntries
                .Where(x => keys.Contains(x.CacheKey))
                .ToDictionaryAsync(x => x.CacheKey, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (key, confirmedAtUnix) in marks)
            {
                if (existing.TryGetValue(key, out var entry))
                    entry.ConfirmedAtUnix = confirmedAtUnix;
                else
                    context.ArticleMissCacheEntries.Add(new ArticleMissCacheEntry
                    {
                        CacheKey = key,
                        ConfirmedAtUnix = confirmedAtUnix,
                    });
            }
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var cutoffUnix = (DateTimeOffset.UtcNow - _configManager.GetArticleMissCacheTtl())
            .ToUnixTimeMilliseconds();
        await TrimPersistedAsync(
            context, cutoffUnix, _configManager.GetArticleMissCacheMaxEntries(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task TrimPersistedAsync(
        DavDatabaseContext context,
        long cutoffUnix,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        // Single round-trip: drop expired rows and, when over capacity, everything
        // outside the newest maxEntries rows. Expired rows inside the keep-set are
        // still removed by the first condition.
        var keepKeys = context.ArticleMissCacheEntries
            .OrderByDescending(x => x.ConfirmedAtUnix)
            .ThenByDescending(x => x.CacheKey)
            .Take(maxEntries)
            .Select(x => x.CacheKey);
        await context.ArticleMissCacheEntries
            .Where(x => x.ConfirmedAtUnix < cutoffUnix || !keepKeys.Contains(x.CacheKey))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
