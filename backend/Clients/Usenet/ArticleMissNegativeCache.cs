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
    internal enum ArticleMissOperation { Stat, Body, Article, Head }
    private const int PersistenceQueueCapacity = 4096;
    private const int MaxPersistenceBatchSize = 256;
    private const int MaxCleanupRounds = 8;

    private readonly ConfigManager _configManager;
    private readonly Func<DavDatabaseContext>? _contextFactory;
    private readonly ConcurrentDictionary<(long Generation, string Key), DateTimeOffset> _missingAt = new();
    private readonly Channel<PersistenceWorkItem>? _persistenceQueue;
    private readonly object _persistenceStateLock = new();
    private CancellationTokenSource? _persistenceLoopCts;
    private Task _persistenceLoop = Task.CompletedTask;
    private volatile bool _persistenceLoopStarted;
    private bool _stopping;
    private bool _persistenceLoopExited;
    private Task? _pendingClearWrite;
    private int _cleanupRunning;
    private int _cleanupContinuationScheduled;
    private long _hits;

    private abstract record PersistenceWorkItem;

    private sealed record MarkItem(long Generation, string Key, long ConfirmedAtUnix) : PersistenceWorkItem;

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
                    FullMode = BoundedChannelFullMode.Wait,
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

    internal static string BuildKey(string articleId, string metricsKey, string? storageGroup,
        ArticleMissOperation operation) =>
        $"v2:{operation.ToString().ToLowerInvariant()}\u0001{BuildKey(articleId, metricsKey, storageGroup)}";

    public bool IsMissing(string key, long generation = 0)
    {
        if (!_missingAt.TryGetValue((generation, key), out var markedAt)) return false;
        if (DateTimeOffset.UtcNow - markedAt < _configManager.GetArticleMissCacheTtl())
        {
            Interlocked.Increment(ref _hits);
            return true;
        }
        _missingAt.TryRemove((generation, key), out _);
        return false;
    }

    public void MarkMissing(string key, long? generation = 0)
    {
        if (generation is not { } evidenceGeneration) return;
        var now = DateTimeOffset.UtcNow;
        MarkMissingInMemory(evidenceGeneration, key, now);
        lock (_persistenceStateLock)
        {
            if (!_stopping)
                _persistenceQueue?.Writer.TryWrite(new MarkItem(evidenceGeneration, key, now.ToUnixTimeMilliseconds()));
        }
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
            var validEntries = entries
                .Select(entry =>
                {
                    var parsed = TryReadPersistedKey(entry.CacheKey, out var generation, out var key);
                    return (entry, parsed, generation, key);
                })
                .Where(x => x.parsed);
            foreach (var (entry, _, generation, key) in validEntries)
                _missingAt[(generation, key)] = DateTimeOffset.FromUnixTimeMilliseconds(entry.ConfirmedAtUnix);

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
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_persistenceStateLock)
            _stopping = true;
        try
        {
            await _persistenceQueue.Writer.WriteAsync(new BarrierItem(barrier), cancellationToken)
                .ConfigureAwait(false);
            await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _persistenceLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Another teardown path has already drained and completed the queue.
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
        lock (_persistenceStateLock)
        {
            _stopping = true;
            _persistenceQueue?.Writer.TryComplete();
        }
        _persistenceLoopCts?.Cancel();
        _persistenceLoopCts?.Dispose();
        _persistenceLoopCts = null;
    }

    /// <summary>Test helper: mark an entry as if it were recorded at <paramref name="at"/>.</summary>
    internal void MarkMissingAtForTests(string key, DateTimeOffset at)
    {
        MarkMissingInMemory(0, key, at);
    }

    /// <summary>
    /// Test hook: invoked after each cleanup round while the single-flight is still
    /// held, so marks added by the hook skip cleanup instead of recursing into it.
    /// </summary>
    internal Action? CleanupRoundCompletedForTests { get; set; }

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

    private void MarkMissingInMemory(long generation, string key, DateTimeOffset at)
    {
        _missingAt[(generation, key)] = at;
        var maxEntries = _configManager.GetArticleMissCacheMaxEntries();
        if (_missingAt.Count <= maxEntries) return;
        try
        {
            Cleanup(maxEntries);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The in-memory mark already succeeded; eviction must never fail STAT/BODY.
            Log.Debug(e, "Article-miss cache cleanup failed; in-memory mark was retained.");
        }
    }

    private void Cleanup(int maxEntries)
    {
        // Single-flight: a STAT storm must not start N concurrent O(n log n) sorts.
        // The flight is released between rounds and the count re-checked, so marks
        // that skipped cleanup while another thread held the flight are handled by
        // the next round instead of waiting for a future MarkMissing. The round cap
        // bounds the work done under a sustained mark storm.
        for (var round = 0; round < MaxCleanupRounds; round++)
        {
            if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0)
                return;

            try
            {
                CleanupRound(maxEntries);
                CleanupRoundCompletedForTests?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
            }

            if (_missingAt.Count <= maxEntries) return;
        }

        // Marks kept landing after each round's snapshot, so the loop is still over
        // cap and every caller that added them skipped cleanup (the flight was
        // held). Don't strand the cache over cap until some future MarkMissing —
        // queue one coalesced continuation to keep trimming in the background.
        ScheduleCleanupContinuation();
    }

    private void ScheduleCleanupContinuation()
    {
        if (Interlocked.CompareExchange(ref _cleanupContinuationScheduled, 1, 0) != 0)
            return;
        _ = Task.Run(() =>
        {
            // Re-arm before running, so a still-over-cap result can schedule the
            // next coalesced continuation from inside Cleanup.
            Interlocked.Exchange(ref _cleanupContinuationScheduled, 0);
            try
            {
                Cleanup(_configManager.GetArticleMissCacheMaxEntries());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "Article-miss cache cleanup continuation failed; a later mark retriggers cleanup.");
            }
        });
    }

    private void CleanupRound(int maxEntries)
    {
        var cutoff = DateTimeOffset.UtcNow - _configManager.GetArticleMissCacheTtl();
        // Weakly-consistent foreach — never LINQ OrderBy/ToArray on the live
        // ConcurrentDictionary (those use Count-then-CopyTo and race under writes).
        // Enumeration only yields fully constructed nodes, so keys are never null.
        var snapshot = new List<KeyValuePair<(long Generation, string Key), DateTimeOffset>>(Math.Max(4, _missingAt.Count));
        foreach (var kv in _missingAt)
        {
            if (kv.Value < cutoff)
                RemoveIfUnchanged(kv);
            else
                snapshot.Add(kv);
        }

        var overflow = _missingAt.Count - maxEntries;
        if (overflow <= 0) return;

        snapshot.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var toEvict = Math.Min(overflow, snapshot.Count);
        for (var i = 0; i < toEvict; i++)
            RemoveIfUnchanged(snapshot[i]);
    }

    // Removes only when the timestamp still matches the snapshot, so a concurrent
    // re-mark with a fresher timestamp is never evicted by a stale cleanup round.
    private void RemoveIfUnchanged(KeyValuePair<(long Generation, string Key), DateTimeOffset> entry) =>
        ((ICollection<KeyValuePair<(long Generation, string Key), DateTimeOffset>>)_missingAt).Remove(entry);

    private void EnqueueClear()
    {
        if (_persistenceQueue is null) return;
        lock (_persistenceStateLock)
        {
            if (_persistenceLoopExited) return;
            if (_persistenceQueue.Writer.TryWrite(ClearItem.Instance)) return;
            // The queue is momentarily full of pending marks and drains quickly; wait
            // for room so a provider-change clear is never dropped.
            _pendingClearWrite = _persistenceQueue.Writer.WriteAsync(ClearItem.Instance).AsTask();
            _ = _pendingClearWrite.ContinueWith(
                task =>
                {
                    if (task.IsFaulted && task.Exception is not null)
                        Log.Debug(task.Exception, "Unable to enqueue provider-change article-miss clear.");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private bool TryExitPersistence(ChannelReader<PersistenceWorkItem> reader)
    {
        lock (_persistenceStateLock)
        {
            if (!_stopping || reader.TryPeek(out _)
                || _pendingClearWrite is { IsCompleted: false })
                return false;
            _persistenceLoopExited = true;
            _persistenceQueue!.Writer.TryComplete();
            return true;
        }
    }

    private void CompletePendingClearWriteIfFinished()
    {
        lock (_persistenceStateLock)
        {
            if (_pendingClearWrite?.IsCompleted == true)
                _pendingClearWrite = null;
        }
    }

    private async Task RunPersistenceLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _persistenceQueue!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var marks = new Dictionary<(long Generation, string Key), long>();
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
                            marks[(mark.Generation, mark.Key)] = mark.ConfirmedAtUnix;
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
                    CompletePendingClearWriteIfFinished();
                    if (barrier is not null && TryExitPersistence(reader)) return;
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
        Dictionary<(long Generation, string Key), long> marks,
        CancellationToken cancellationToken)
    {
        await using var context = _contextFactory!();
        if (clearPending)
            await context.ArticleMissCacheEntries.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (marks.Count > 0)
        {
            var persistedMarks = marks.ToDictionary(
                x => PersistedKey(x.Key.Generation, x.Key.Key), x => x.Value,
                StringComparer.Ordinal);
            var keys = persistedMarks.Keys.ToList();
            var existing = await context.ArticleMissCacheEntries
                .Where(x => keys.Contains(x.CacheKey))
                .ToDictionaryAsync(x => x.CacheKey, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (key, confirmedAtUnix) in persistedMarks)
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

    private static string PersistedKey(long generation, string key) =>
        generation == 0 ? key : $"g{generation}\u0001{key}";

    private static bool TryReadPersistedKey(string persistedKey, out long generation, out string key)
    {
        generation = 0;
        key = "";
        if (!persistedKey.StartsWith('g'))
        {
            key = persistedKey;
            return key.Length > 0;
        }
        var separator = persistedKey.IndexOf('\u0001', StringComparison.Ordinal);
        if (separator <= 1 || persistedKey[0] != 'g'
            || !long.TryParse(persistedKey.AsSpan(1, separator - 1), out generation))
            return false;
        key = persistedKey[(separator + 1)..];
        return key.Length > 0;
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
