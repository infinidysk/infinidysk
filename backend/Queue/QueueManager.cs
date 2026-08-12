using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public sealed class QueueManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, InProgressQueueItem> _inProgress = new();
    private readonly ConcurrentDictionary<Guid, int> _retryAttempts = new();

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _finalizeLock = new(1, 1);
    private readonly Lock _admissionLock = new();
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly ProviderUsageTracker _providerUsageTracker;
    private readonly WatchdogLog _watchdogLog;
    private readonly QueueItemSourceTracker _sourceTracker;
    private readonly BenchmarkGate _benchmarkGate;

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();
    private int _loopStarted;
    private Task? _coordinatorTask;
    private Guid? _primaryId;
    private int _pendingAdmissions;
    private bool _admissionPaused;

    private static readonly TimeSpan DefaultStuckItemThreshold =
        EnvironmentUtil.GetLongVariable("QUEUE_ITEM_STUCK_MINUTES") is long minutes and > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(5);

    // Overridable in tests so persistent-failure / idle-sleep behaviour can be
    // exercised without a real database.
    internal TimeSpan ErrorBackoffDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan IdleDelay { get; set; } = TimeSpan.FromMinutes(1);
    internal TimeSpan StuckItemThreshold { get; set; } = DefaultStuckItemThreshold;
    internal TimeSpan StuckItemCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan StuckItemPauseWriteTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal Func<IReadOnlyCollection<Guid>, CancellationToken, Task<(QueueItem? queueItem, Stream? queueNzbStream)>>?
        GetTopQueueItemOverride
    { get; set; }
    internal Func<CancellationToken, Task<DateTime?>>? GetNextPauseUntilOverride { get; set; }
    internal Func<DavDatabaseContext>? CreateDbContextOverride { get; set; }

    private DavDatabaseContext CreateDbContext() => DavDatabaseContexts.Create(CreateDbContextOverride);

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker providerUsageTracker,
        WatchdogLog watchdogLog,
        QueueItemSourceTracker sourceTracker,
        BenchmarkGate benchmarkGate
    ) : this(
        usenetClient, configManager, websocketManager, providerUsageTracker,
        watchdogLog, sourceTracker, benchmarkGate, startLoop: false)
    {
    }

    internal QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker providerUsageTracker,
        WatchdogLog watchdogLog,
        QueueItemSourceTracker sourceTracker,
        BenchmarkGate benchmarkGate,
        bool startLoop
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _providerUsageTracker = providerUsageTracker;
        _watchdogLog = watchdogLog;
        _sourceTracker = sourceTracker;
        _benchmarkGate = benchmarkGate;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        if (startLoop)
            StartProcessing();
    }

    /// <summary>
    /// Starts the background queue loop. Safe to call more than once; only the
    /// first call starts processing. DI construction leaves the loop stopped so
    /// Kestrel can bind before the first BODY decode.
    /// </summary>
    public void StartProcessing()
    {
        if (Interlocked.Exchange(ref _loopStarted, 1) == 1) return;
        _coordinatorTask = ProcessQueueAsync(_cancellationTokenSource!.Token);
    }

    /// <summary>True while any NZB queue item is actively processing.</summary>
    public bool HasActiveQueueItems => !_inProgress.IsEmpty;

    internal IDisposable? TryReserveQueueSlot(
        int persistedCount,
        int maxItems,
        int resumeThreshold)
    {
        if (maxItems <= 0) return new QueueAdmissionReservation(static () => { });

        lock (_admissionLock)
        {
            var effectiveCount = (long)Math.Max(0, persistedCount) + _pendingAdmissions;
            var effectiveResumeThreshold = resumeThreshold <= 0
                ? maxItems
                : Math.Min(resumeThreshold, maxItems);

            if (_admissionPaused)
            {
                if (effectiveCount > effectiveResumeThreshold)
                    return null;
                _admissionPaused = false;
            }

            if (effectiveCount >= maxItems)
            {
                _admissionPaused = true;
                return null;
            }

            _pendingAdmissions++;
            return new QueueAdmissionReservation(ReleaseQueueSlotReservation);
        }
    }

    private void ReleaseQueueSlotReservation()
    {
        lock (_admissionLock)
        {
            if (_pendingAdmissions > 0)
                _pendingAdmissions--;
        }
    }

    private sealed class QueueAdmissionReservation(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    /// <summary>
    /// Immutable snapshot of every in-flight queue item and its progress.
    /// Primary (preferred) item is listed first when present.
    /// </summary>
    public IReadOnlyList<InProgressQueueItemSnapshot> GetInProgressQueueItems()
    {
        var items = _inProgress.Values
            .Select(ToSnapshot)
            .ToList();

        items.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            return a.QueueItem.CreatedAt.CompareTo(b.QueueItem.CreatedAt);
        });
        return items;
    }

    /// <summary>
    /// Compatibility helper: returns the primary in-progress item, or the oldest
    /// active item when no primary is designated yet.
    /// </summary>
    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        var items = GetInProgressQueueItems();
        if (items.Count == 0) return (null, null);
        return (items[0].QueueItem, items[0].ProgressPercentage);
    }

    public InProgressQueueItemSnapshot? FindInProgressQueueItem(Guid queueItemId)
    {
        return _inProgress.TryGetValue(queueItemId, out var item)
            ? ToSnapshot(item)
            : null;
    }

    public void AwakenQueue(DateTime? dateTime = null)
    {
        TimeSpan? cancelAfter = dateTime.HasValue ? (dateTime.Value - DateTime.Now) : null;
        lock (_sleepingQueueLock)
        {
            if (cancelAfter.HasValue && cancelAfter.Value > TimeSpan.Zero)
                _sleepingQueueToken.CancelAfter(cancelAfter.Value);
            else
                _sleepingQueueToken.Cancel();
        }
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        List<InProgressQueueItem> toCancel = [];
        await LockAsync(() =>
        {
            toCancel = _inProgress.Values
                .Where(x => queueItemIds.Contains(x.QueueItem.Id))
                .ToList();
        }, ct).ConfigureAwait(false);

        foreach (var item in toCancel)
        {
            try
            {
                await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await item.ProcessingTask.ConfigureAwait(false);
            }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                Log.Debug(e, "Queue item {QueueItemId} exited with error after cancel", item.QueueItem.Id);
            }
        }

        await LockAsync(async () =>
        {
            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var id in queueItemIds) _retryAttempts.TryRemove(id, out _);
        }, ct).ConfigureAwait(false);
    }


    public async Task PauseQueueItemsAsync(
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default)
    {
        if (queueItemIds.Count == 0) return;

        await LockAsync(async () =>
        {
            await dbClient.Ctx.QueueItems
                .Where(item => queueItemIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(q => q.Priority, QueueItem.PriorityOption.Paused),
                    ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await CancelInProgressQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
        AwakenQueue();
    }

    public async Task ResumeQueueItemsAsync(
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default)
    {
        if (queueItemIds.Count == 0) return;

        await LockAsync(async () =>
        {
            await dbClient.Ctx.QueueItems
                .Where(item => queueItemIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(q => q.Priority, QueueItem.PriorityOption.Normal)
                        .SetProperty(q => q.PauseUntil, (DateTime?)null),
                    ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        AwakenQueue();
    }

    public async Task SetQueueItemsPriorityAsync(
        List<Guid> queueItemIds,
        QueueItem.PriorityOption priority,
        DavDatabaseClient dbClient,
        CancellationToken ct = default)
    {
        if (queueItemIds.Count == 0) return;

        await LockAsync(async () =>
        {
            var update = dbClient.Ctx.QueueItems
                .Where(item => queueItemIds.Contains(item.Id));
            if (priority != QueueItem.PriorityOption.Paused)
            {
                await update.ExecuteUpdateAsync(
                    s => s
                        .SetProperty(q => q.Priority, priority)
                        .SetProperty(q => q.PauseUntil, (DateTime?)null),
                    ct).ConfigureAwait(false);
            }
            else
            {
                await update.ExecuteUpdateAsync(
                    s => s.SetProperty(q => q.Priority, priority),
                    ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        if (priority == QueueItem.PriorityOption.Paused)
            await CancelInProgressQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);

        AwakenQueue();
    }

    public async Task<List<Guid>> SetQueueItemsCategoryAsync(
        List<Guid> queueItemIds,
        string category,
        DavDatabaseClient dbClient,
        CancellationToken ct = default)
    {
        if (queueItemIds.Count == 0) return [];

        var inProgressIds = _inProgress.Keys.ToHashSet();
        var eligibleIds = queueItemIds.Where(id => !inProgressIds.Contains(id)).ToList();
        if (eligibleIds.Count == 0) return [];

        await LockAsync(async () =>
        {
            await dbClient.Ctx.QueueItems
                .Where(item => eligibleIds.Contains(item.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(q => q.Category, category),
                    ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return eligibleIds;
    }

    private async Task CancelInProgressQueueItemsAsync(List<Guid> queueItemIds, CancellationToken ct)
    {
        if (queueItemIds.Count == 0) return;

        List<InProgressQueueItem> toCancel = [];
        await LockAsync(() =>
        {
            toCancel = _inProgress.Values
                .Where(x => queueItemIds.Contains(x.QueueItem.Id))
                .ToList();
        }, ct).ConfigureAwait(false);

        foreach (var item in toCancel)
        {
            try
            {
                await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                await item.ProcessingTask.ConfigureAwait(false);
            }
#pragma warning disable CA2016
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                Log.Debug(e, "Queue item {QueueItemId} exited with error after cancel", item.QueueItem.Id);
            }
        }
    }


    internal async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // While a speed-test is running, or the SAB-compatible queue pause is
            // active (mode=pause), hold off starting new downloads so a benchmark
            // gets the provider's full connection budget and a paused queue stops
            // claiming work. Any item already in progress finishes naturally; this
            // only gates new work. ResumeController calls AwakenQueue so resume
            // does not wait out the full poll interval.
            if (_benchmarkGate.IsPaused || _configManager.IsSabQueuePaused())
            {
                try
                {
                    using var pauseWait = CancellationTokenSource.CreateLinkedTokenSource(
                        ct, _sleepingQueueToken.Token);
                    await Task.Delay(TimeSpan.FromSeconds(1), pauseWait.Token).ConfigureAwait(false);
                }
                catch when (_sleepingQueueToken.IsCancellationRequested)
                {
                    ResetSleepingQueueToken();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // shutting down
                }
                continue;
            }

            try
            {
                // Reap before fill so completed primaries do not occupy slots or
                // block secondary promotion while new workers are claimed.
                await ReapCompletedWorkersAsync(ct).ConfigureAwait(false);
                await FillWorkerSlotsAsync(ct).ConfigureAwait(false);

                if (_inProgress.IsEmpty)
                {
                    await IdleSleepAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // Wait for any worker to finish, an awaken signal, or a short
                // poll so worker-count increases can fill new slots promptly.
                var workerTasks = _inProgress.Values.Select(x => x.CompletionSignal.Task).ToArray();
                if (!await WaitForWorkerOrAwakenAsync(workerTasks, ct).ConfigureAwait(false))
                    break;
            }
            catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
            {
                Log.Error(e, "An unexpected error occurred while processing the queue");
                try { await Task.Delay(ErrorBackoffDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutting down */ }
            }
        }

        // Shutdown: cancel remaining workers and observe their tasks.
        foreach (var item in _inProgress.Values)
            await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);

        var remaining = _inProgress.Values.Select(x => x.ProcessingTask).ToArray();
        if (remaining.Length > 0)
        {
            try { await Task.WhenAll(remaining).ConfigureAwait(false); }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                Log.Debug(e, "Queue workers finished with errors during shutdown");
            }
        }

        await ReapCompletedWorkersAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FillWorkerSlotsAsync(CancellationToken ct)
    {
        while (!_benchmarkGate.IsPaused && !_configManager.IsSabQueuePaused() && !ct.IsCancellationRequested)
        {
            var workerCount = _configManager.GetQueueWorkerCount();
            if (_inProgress.Count >= workerCount)
                return;

            var started = await TryStartNextWorkerAsync(ct).ConfigureAwait(false);
            if (!started)
                return;
        }
    }

    private async Task<bool> TryStartNextWorkerAsync(CancellationToken ct)
    {
        DavDatabaseContext? dbContext = null;
        DavDatabaseClient? dbClient = null;
        QueueItem? queueItem = null;
        Stream? queueNzbStream = null;
        InProgressQueueItem? inProgress = null;
        CancellationTokenContext? queueContextRegistration = null;

        try
        {
            await _stateLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var workerCount = _configManager.GetQueueWorkerCount();
                if (_inProgress.Count >= workerCount)
                    return false;

                var excludeIds = _inProgress.Keys.ToHashSet();
                var reservedMountKeys = _inProgress.Values
                    .Select(x => (x.QueueItem.Category, x.QueueItem.JobName))
                    .ToHashSet();

                // Skip mount-key conflicts by excluding them from subsequent queries.
                while (true)
                {
                    (QueueItem? item, Stream? stream) claimed;
                    if (GetTopQueueItemOverride is not null)
                    {
                        claimed = await GetTopQueueItemOverride(excludeIds, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        dbContext ??= CreateDbContext();
                        dbClient ??= new DavDatabaseClient(dbContext);
                        claimed = await dbClient.GetTopQueueItem(excludeIds, ct).ConfigureAwait(false);
                    }

                    if (claimed.item is null)
                    {
                        if (claimed.stream is not null)
                            await claimed.stream.DisposeAsync().ConfigureAwait(false);
                        return false;
                    }

                    if (reservedMountKeys.Contains((claimed.item.Category, claimed.item.JobName)))
                    {
                        excludeIds.Add(claimed.item.Id);
                        if (claimed.stream is not null)
                            await claimed.stream.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    queueItem = claimed.item;
                    queueNzbStream = claimed.stream;
                    break;
                }

                // Own a dedicated DB context for this worker (may already be
                // created above when claiming from the database).
                if (dbContext is null)
                {
                    dbContext = CreateDbContext();
                    dbClient = new DavDatabaseClient(dbContext);
                }

                // Treat a completed-but-not-yet-reaped primary as vacant so Fill
                // can claim a new preferred worker without waiting for the next loop.
                var isPrimary = _primaryId is null ||
                    !_inProgress.TryGetValue(_primaryId.Value, out var primaryItem) ||
                    primaryItem.ProcessingTask.IsCompleted;
                var queueDownloadContext = new QueueDownloadContext
                {
                    IsPrimary = isPrimary,
                    GetFanOutConcurrency = () => ComputeFanOutConcurrency(queueItem.Id),
                };

#pragma warning disable CA2000 // worker CTS ownership transfers to InProgressQueueItem and is disposed when the queue item completes; the not-transferred path disposes in finally
                var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
#pragma warning restore CA2000
#pragma warning disable CA2000 // registration ownership transfers to InProgressQueueItem (disposed on completion); otherwise disposed in finally
                queueContextRegistration = workerCts.Token.SetContext(queueDownloadContext);
#pragma warning restore CA2000

                inProgress = BeginProcessingQueueItem(
                    dbClient!,
                    queueItem,
                    queueNzbStream,
                    workerCts,
                    queueDownloadContext,
                    queueContextRegistration,
                    dbContext);

                _inProgress[queueItem.Id] = inProgress;
                if (isPrimary)
                    _primaryId = queueItem.Id;
                else
                    EnsurePrimaryDesignation();

                // Ownership transferred to InProgressQueueItem / worker task.
                dbContext = null;
                dbClient = null;
                queueNzbStream = null;
                queueContextRegistration = null;
                inProgress = null;
                return true;
            }
            finally
            {
                _stateLock.Release();
            }
        }
        catch
        {
            if (queueNzbStream is not null)
                await queueNzbStream.DisposeAsync().ConfigureAwait(false);
            if (dbContext is not null)
                await dbContext.DisposeAsync().ConfigureAwait(false);
            queueContextRegistration?.Dispose();
            inProgress?.CancellationTokenSource.Dispose();
            throw;
        }
    }

    private int ComputeFanOutConcurrency(Guid queueItemId)
    {
        var maxQueue = _configManager.GetMaxQueueConnections();
        var secondaryCount = _inProgress.Values.Count(x => !x.QueueDownloadContext.IsPrimary);
        var isPrimary = _primaryId == queueItemId ||
            (_inProgress.TryGetValue(queueItemId, out var item) && item.QueueDownloadContext.IsPrimary);

        if (isPrimary)
        {
            return secondaryCount > 0
                ? QueueFanOut.PrimaryFanOutWhenSharing(maxQueue, secondaryCount)
                : QueueFanOut.PrimaryFanOut(maxQueue);
        }

        return QueueFanOut.SecondaryFanOut(maxQueue, secondaryCount);
    }

    private void EnsurePrimaryDesignation()
    {
        // Ignore completed workers still awaiting reap so a finished primary
        // cannot block promotion or keep IsPrimary while Fill starts new work.
        var live = _inProgress.Values
            .Where(x => !x.ProcessingTask.IsCompleted)
            .ToList();

        if (_primaryId is not null &&
            _inProgress.TryGetValue(_primaryId.Value, out var current) &&
            !current.ProcessingTask.IsCompleted)
        {
            foreach (var item in _inProgress.Values)
                item.QueueDownloadContext.IsPrimary = item.QueueItem.Id == _primaryId.Value;
            return;
        }

        // Promote the oldest live secondary before claiming a new primary.
        var oldest = live
            .OrderBy(x => x.StartedAt)
            .ThenBy(x => x.QueueItem.CreatedAt)
            .FirstOrDefault();

        if (oldest is null)
        {
            _primaryId = null;
            foreach (var item in _inProgress.Values)
                item.QueueDownloadContext.IsPrimary = false;
            return;
        }

        _primaryId = oldest.QueueItem.Id;
        foreach (var item in _inProgress.Values)
            item.QueueDownloadContext.IsPrimary = item.QueueItem.Id == _primaryId.Value;
    }

    private async Task ReapCompletedWorkersAsync(CancellationToken ct)
    {
        List<InProgressQueueItem> completed = [];
        await LockAsync(() =>
        {
            completed = _inProgress.Values
                .Where(x => x.ProcessingTask.IsCompleted)
                .ToList();

            foreach (var item in completed)
            {
                _inProgress.TryRemove(item.QueueItem.Id, out _);
                if (_primaryId == item.QueueItem.Id)
                    _primaryId = null;
            }

            if (completed.Count > 0)
                EnsurePrimaryDesignation();
        }, ct).ConfigureAwait(false);

        foreach (var item in completed)
        {
            try
            {
                await item.ProcessingTask.ConfigureAwait(false);
            }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                Log.Error(e, "Queue worker for {QueueItemId} faulted", item.QueueItem.Id);
            }

            await item.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task IdleSleepAsync(CancellationToken ct)
    {
        DavDatabaseContext? dbContext = null;
        try
        {
            DavDatabaseClient? dbClient = null;
            if (GetNextPauseUntilOverride is null && GetTopQueueItemOverride is null)
            {
                dbContext = CreateDbContext();
                dbClient = new DavDatabaseClient(dbContext);
            }

            var idleDelay = await ComputeIdleDelayAsync(dbClient, ct).ConfigureAwait(false);
            using var idleWait = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _sleepingQueueToken.Token);
            await Task.Delay(idleDelay, idleWait.Token).ConfigureAwait(false);
        }
        catch when (_sleepingQueueToken.IsCancellationRequested)
        {
            ResetSleepingQueueToken();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            if (dbContext is not null)
                await dbContext.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Wait for any worker to finish, an awaken signal, or a short poll. Returns
    // false when shutdown is requested. Task.WhenAny completes without observing
    // wakeDelay's cancellation, so the awaken signal is consumed here explicitly
    // rather than in a catch clause that never runs.
    internal async Task<bool> WaitForWorkerOrAwakenAsync(Task[] workerTasks, CancellationToken ct)
    {
        using var wakeWait = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _sleepingQueueToken.Token);
        var wakeDelay = Task.Delay(TimeSpan.FromSeconds(1), wakeWait.Token);

        // The poll always joins the wait set, so the wait stays bounded when no
        // workers are in flight and Task.WhenAny never sees an empty array.
        await Task.WhenAny([.. workerTasks, wakeDelay]).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
            return false;

        if (_sleepingQueueToken.IsCancellationRequested)
            ResetSleepingQueueToken();

        return true;
    }

    // Resetting discards a CancelAfter scheduled by a paused add. The pause is
    // still honored because ComputeIdleDelayAsync re-derives the next wake from
    // the database once the queue goes idle.
    private void ResetSleepingQueueToken()
    {
        lock (_sleepingQueueLock)
        {
            if (!_sleepingQueueToken.TryReset())
            {
                _sleepingQueueToken.Dispose();
                _sleepingQueueToken = new CancellationTokenSource();
            }
        }
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseClient dbClient,
        QueueItem queueItem,
        Stream? queueNzbStream,
        CancellationTokenSource cts,
        QueueDownloadContext queueDownloadContext,
        CancellationTokenContext queueContextRegistration,
        DavDatabaseContext dbContext
    )
    {
        // Per-item article cache; disposed with the worker.
        var cachingUsenetClient = new ArticleCachingNntpClient(_usenetClient);
        var progressHook = new Progress<int>();
        var completionSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inProgressQueueItem = new InProgressQueueItem
        {
            QueueItem = queueItem,
            ProcessingTask = null!, // set below, after the processor is created
            CompletionSignal = completionSignal,
            ProgressPercentage = 0,
            CancellationTokenSource = cts,
            QueueDownloadContext = queueDownloadContext,
            QueueContextRegistration = queueContextRegistration,
            DbContext = dbContext,
            QueueNzbStream = queueNzbStream,
            CachingUsenetClient = cachingUsenetClient,
            StartedAt = DateTime.UtcNow,
            LastThroughputSampleTime = DateTime.UtcNow,
            LastThroughputSampleProgress = 0,
            WatchdogCts = null!, // set below, after the worker task is created
        };

        var task = new QueueItemProcessor(
            queueItem, queueNzbStream, dbClient, cachingUsenetClient,
            _configManager, _websocketManager, _providerUsageTracker,
            _watchdogLog, _sourceTracker, progressHook, _retryAttempts,
            _finalizeLock, cts.Token,
            stageReporter: stage => inProgressQueueItem.CurrentStage = stage
        ).ProcessAsync();
        inProgressQueueItem.ProcessingTask = task;

        var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        inProgressQueueItem.WatchdogCts = watchdogCts;

        _ = task.ContinueWith(
            t =>
            {
                try { watchdogCts.Cancel(); }
                catch (ObjectDisposedException) { /* already disposed */ }

                if (t.IsFaulted)
                    Log.Error(t.Exception!.GetBaseException(),
                        "Unhandled queue processor fault for {QueueItemId}", queueItem.Id);
                completionSignal.TrySetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        inProgressQueueItem.WatchdogTask =
            WatchForStuckProgressAsync(inProgressQueueItem, watchdogCts.Token);

        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
        var providersDebounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
        var progressLock = new object();
        var latestProgress = 0;
        var lastSentProgress = -1;

        void SendLatestProgress()
        {
            int value;
            lock (progressLock)
            {
                if (latestProgress <= lastSentProgress) return;
                value = latestProgress;
                lastSentProgress = value;
            }

            _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, $"{queueItem.Id}|{value}");
        }

        progressHook.ProgressChanged += (_, progress) =>
        {
            try
            {
                lock (progressLock)
                {
                    if (progress > latestProgress) latestProgress = progress;
                    inProgressQueueItem.ProgressPercentage = latestProgress;
                    var sample = QueueThroughput.Update(
                        new QueueThroughput.SampleState(
                            inProgressQueueItem.BytesPerSecond,
                            inProgressQueueItem.LastThroughputSampleTime,
                            inProgressQueueItem.LastThroughputSampleProgress),
                        latestProgress,
                        queueItem.TotalSegmentBytes,
                        DateTime.UtcNow);
                    inProgressQueueItem.BytesPerSecond = sample.BytesPerSecond;
                    inProgressQueueItem.LastThroughputSampleTime = sample.LastSampleTime;
                    inProgressQueueItem.LastThroughputSampleProgress = sample.LastSampleProgress;
                }

                if (progress is 100 or 200) SendLatestProgress();
                else debounce(SendLatestProgress);
                providersDebounce(() => _websocketManager.SendMessage(
                    WebsocketTopic.QueueItemProviders, BuildProvidersMessage(queueItem.Id)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning(e, "Queue progress broadcast failed for {QueueItemId}", queueItem.Id);
            }
        };
        return inProgressQueueItem;
    }

    private async Task WatchForStuckProgressAsync(InProgressQueueItem item, CancellationToken ct)
    {
        var lastProgress = item.ProgressPercentage;
        var lastFetchCount = GetSuccessfulFetchCount(item.QueueItem.Id);
        var lastChangeTick = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StuckItemCheckInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            // "Stuck" means no visible progress AND no segment fetches. Long
            // silent stages (PAR2 descriptor walks, RAR header parses) keep
            // fetching articles, so they must not trip the watchdog — only a
            // worker genuinely waiting on a semaphore/pool fetch stops both.
            var currentProgress = item.ProgressPercentage;
            var currentFetchCount = GetSuccessfulFetchCount(item.QueueItem.Id);
            if (currentProgress != lastProgress || currentFetchCount != lastFetchCount)
            {
                lastProgress = currentProgress;
                lastFetchCount = currentFetchCount;
                lastChangeTick = Environment.TickCount64;
                continue;
            }

            var idleMs = Environment.TickCount64 - lastChangeTick;
            if (idleMs < StuckItemThreshold.TotalMilliseconds)
                continue;

            await HandleStuckItemAsync(item, idleMs).ConfigureAwait(false);
            return;
        }
    }

    private long GetSuccessfulFetchCount(Guid queueItemId)
    {
        var snapshot = _providerUsageTracker.Snapshot(queueItemId);
        return snapshot.Values.Aggregate(0L, (sum, count) => sum + count);
    }

    private async Task HandleStuckItemAsync(InProgressQueueItem item, long idleMs)
    {
        var queueItem = item.QueueItem;
        var progress = item.ProgressPercentage;
        var idleMinutes = idleMs / 60000d;
        var phase = DescribeProgressPhase(progress);
        var stage = item.CurrentStage;
#pragma warning disable CA5394 // pause jitter is not security-sensitive
        var jitterSeconds = Random.Shared.Next(0, 301);
#pragma warning restore CA5394
        var pauseUntil = DateTime.Now + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(jitterSeconds);

        try
        {
            await using var ctx = CreateDbContext();
            using var writeCts = new CancellationTokenSource(StuckItemPauseWriteTimeout);
            await ctx.QueueItems
                .Where(q => q.Id == queueItem.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.PauseUntil, pauseUntil), writeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Warning(
                "Timed out persisting PauseUntil for stuck queue item {QueueItemId} ({JobName}); " +
                "proceeding with worker cancellation",
                queueItem.Id,
                queueItem.JobName);
        }
#pragma warning disable CA2016
        catch (Exception e) when (e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Warning(e,
                "Failed to persist PauseUntil for stuck queue item {QueueItemId} ({JobName})",
                queueItem.Id,
                queueItem.JobName);
        }

        Log.Warning(
            "Queue item stuck with no progress; pausing and cancelling. JobName={JobName} QueueItemId={QueueItemId} " +
            "IdleMinutes={IdleMinutes:F1} ProgressPhase={ProgressPhase} CurrentStage={CurrentStage} " +
            "ProgressPercentage={ProgressPercentage} PauseUntil={PauseUntil}",
            queueItem.JobName,
            queueItem.Id,
            idleMinutes,
            phase,
            stage,
            progress,
            pauseUntil);

        try
        {
            await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Worker may have finished and disposed the CTS concurrently.
        }
    }

    private static string DescribeProgressPhase(int progress) =>
        progress < 50 ? "fetch first segments"
        : progress < 100 ? "file processing"
        : "full health check";

    private string BuildProvidersMessage(Guid queueItemId)
    {
        var snapshot = _providerUsageTracker.Snapshot(queueItemId);
        var providers = _configManager.GetUsenetProviderConfig().Providers;
        var displayByMetricsKey = ProviderUsageHelper.BuildDisplayByMetricsKey(providers);

        // The wire format is host-based; resolve metrics keys to display hosts so
        // Guids never reach the UI, aggregating same-host accounts into one entry.
        var merged = new Dictionary<string, long>();
        foreach (var kv in snapshot)
        {
            var host = displayByMetricsKey.TryGetValue(kv.Key, out var display) ? display.Host : kv.Key;
            merged.TryGetValue(host, out var existing);
            merged[host] = existing + kv.Value;
        }

        var configured = providers
            .Select(p => p.Host)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct();
        foreach (var host in configured.Where(host => !merged.ContainsKey(host)))
            merged[host] = 0;
        var payload = string.Join(",", merged.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{queueItemId}|{payload}";
    }

    private async Task LockAsync(Func<Task> actionAsync, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await actionAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task LockAsync(Action action, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<TimeSpan> ComputeIdleDelayAsync(
        DavDatabaseClient? dbClient, CancellationToken ct)
    {
        try
        {
            DateTime? nextPause;
            if (GetNextPauseUntilOverride is not null)
                nextPause = await GetNextPauseUntilOverride(ct).ConfigureAwait(false);
            else if (dbClient is not null)
                nextPause = await dbClient.GetNextQueueItemPauseUntil(ct).ConfigureAwait(false);
            else
                return IdleDelay;

            if (nextPause is null) return IdleDelay;

            // Small buffer so we wake just AFTER the pause expires; waking a hair
            // early would find no eligible item and sleep a full IdleDelay again.
            var untilNextPause = nextPause.Value - DateTime.Now + TimeSpan.FromMilliseconds(250);
            if (untilNextPause <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(250);
            return untilNextPause < IdleDelay ? untilNextPause : IdleDelay;
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(e, "Failed to compute next queue pause; falling back to idle delay");
            return IdleDelay;
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        try
        {
            _coordinatorTask?.GetAwaiter().GetResult();
        }
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
        {
            Log.Debug(e, "Queue coordinator exited with error during dispose");
        }

        _cancellationTokenSource?.Dispose();
        _stateLock.Dispose();
        _finalizeLock.Dispose();
        _sleepingQueueToken.Dispose();
    }

    public readonly record struct InProgressQueueItemSnapshot(
        QueueItem QueueItem,
        int ProgressPercentage,
        bool IsPrimary,
        TimeSpan? Eta = null,
        double BytesPerSecond = 0);

    private static InProgressQueueItemSnapshot ToSnapshot(InProgressQueueItem item) =>
        new(
            item.QueueItem,
            item.ProgressPercentage,
            item.IsPrimary,
            QueueThroughput.ComputeEta(
                item.BytesPerSecond,
                item.ProgressPercentage,
                item.QueueItem.TotalSegmentBytes),
            item.BytesPerSecond);

    private sealed class InProgressQueueItem
    {
        public QueueItem QueueItem { get; init; } = null!;
        public int ProgressPercentage { get; set; }
        public double BytesPerSecond { get; set; }
        public DateTime LastThroughputSampleTime { get; set; }
        public int LastThroughputSampleProgress { get; set; }

        /// <summary>
        /// Name of the queue stage currently executing (e.g. par2, lazy-rar,
        /// processors). Set by the processor; read by the stuck watchdog.
        /// </summary>
        public string CurrentStage { get; set; } = string.Empty;
        public Task ProcessingTask { get; set; } = null!;
        public TaskCompletionSource CompletionSignal { get; init; } = null!;
        public CancellationTokenSource CancellationTokenSource { get; init; } = null!;
        public QueueDownloadContext QueueDownloadContext { get; init; } = null!;
        public CancellationTokenContext QueueContextRegistration { get; init; } = null!;
        public DavDatabaseContext DbContext { get; init; } = null!;
        public Stream? QueueNzbStream { get; init; }
        public ArticleCachingNntpClient CachingUsenetClient { get; init; } = null!;
        public DateTime StartedAt { get; init; }
        public CancellationTokenSource WatchdogCts { get; set; } = null!;
        public Task WatchdogTask { get; set; } = Task.CompletedTask;
        public bool IsPrimary => QueueDownloadContext.IsPrimary;

        public async ValueTask DisposeAsync()
        {
            try { await WatchdogCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* already disposed */ }

            try { await WatchdogTask.ConfigureAwait(false); }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
            {
                // Watchdog may fault after the worker is torn down; ignore during cleanup.
            }

            WatchdogCts.Dispose();
            QueueContextRegistration.Dispose();
            CancellationTokenSource.Dispose();
            CachingUsenetClient.Dispose();
            if (QueueNzbStream is not null)
                await QueueNzbStream.DisposeAsync().ConfigureAwait(false);
            await DbContext.DisposeAsync().ConfigureAwait(false);
        }
    }
}
