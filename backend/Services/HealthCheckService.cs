using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services;

internal enum HealthCheckRefillOutcome
{
    Started,
    NoCandidate,
    Blocked,
}

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService, IHealthCheckQuiescence
{
    private const int MaximumMissingSegmentIds = 100_000;
    internal const string MissingPayloadMessagePrefix = "Streaming payload missing.";
    internal const string UnreadablePayloadMessagePrefix = "Streaming payload unreadable.";
    private const int MissingPayloadConfirmationsRequired = 3;
    private static readonly TimeSpan MissingPayloadInitialRecheck = TimeSpan.FromDays(1);
    private static readonly TimeSpan MissingPayloadConfirmedRecheck = TimeSpan.FromDays(7);
    private static readonly TimeSpan HealthCheckProgressTimeout = TimeSpan.FromMinutes(5);

    // Repeated remove-and-blocklist repairs for the same library path in a short window indicate
    // a replacement loop (Arr keeps re-grabbing a release repair keeps rejecting, issue #732).
    // After the limit is hit, further repairs at that path are deferred instead of deleting again.
    internal const int RepairRecurrenceLimit = 3;
    internal static readonly TimeSpan RepairRecurrenceWindow = TimeSpan.FromHours(6);
    internal static readonly TimeSpan InfrastructureFailureCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerDrainTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumTrackedRepairPaths = 10_000;

    internal static bool IsMissingPayloadMessage(string? message) =>
        message?.StartsWith(
            MissingPayloadMessagePrefix,
            StringComparison.Ordinal) == true;

    /// <summary>
    /// True when the message starts with the unreadable payload prefix, indicating a
    /// prior check detected a corrupted blob.
    /// </summary>
    internal static bool IsUnreadablePayloadMessage(string? message) =>
        message?.StartsWith(
            UnreadablePayloadMessagePrefix,
            StringComparison.Ordinal) == true;

    internal static Task<int> GetMissingPayloadConfirmationAsync(
        DavDatabaseContext context,
        Guid davItemId,
        CancellationToken ct) =>
        GetPayloadConfirmationAsync(context, davItemId, IsMissingPayloadMessage, ct);

    /// <summary>
    /// Counts recent health check results for this DAV item that reported an unreadable
    /// streaming metadata blob. Used to detect persistent corruption and escalate actions.
    /// </summary>
    /// <param name="context">Database context.</param>
    /// <param name="davItemId">The DAV item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of consecutive unreadable confirmations.</returns>
    internal static Task<int> GetUnreadablePayloadConfirmationAsync(
        DavDatabaseContext context,
        Guid davItemId,
        CancellationToken ct) =>
        GetPayloadConfirmationAsync(context, davItemId, IsUnreadablePayloadMessage, ct);

    private static async Task<int> GetPayloadConfirmationAsync(
        DavDatabaseContext context,
        Guid davItemId,
        Func<string?, bool> isMatchingPriorMessage,
        CancellationToken ct)
    {
        var recentMessages = await context.HealthCheckResults
            .AsNoTracking()
            .Where(result => result.DavItemId == davItemId)
            .OrderByDescending(result => result.CreatedAt)
            .ThenByDescending(result => result.Id)
            .Select(result => result.Message)
            .Take(MissingPayloadConfirmationsRequired - 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return recentMessages
            .TakeWhile(isMatchingPriorMessage)
            .Count() + 1;
    }

    internal static TimeSpan GetMissingPayloadRecheckDelay(int confirmations) =>
        confirmations >= MissingPayloadConfirmationsRequired
            ? MissingPayloadConfirmedRecheck
            : MissingPayloadInitialRecheck;

    // How many of a rejected release's segment ids to seed into the fail-fast cache.
    // Bounded so a single large release cannot evict the whole FIFO cache; the queue
    // precheck fails on any overlap, so a prefix is sufficient to reject a re-grab.
    internal const int RejectedReleaseSeedSegments = 200;

    // Files at or below this many segments are checked in full, before any aging taper.
    public const int SampleFloor = 8000;

    /// <summary>
    /// Operator-forced full recheck sentinel for <see cref="DavItem.NextHealthCheck"/>, written by
    /// the reset-health-check-queue endpoint. Like the UnixEpoch urgent sentinel it overrides the
    /// history-linked exclusion, but it is processed as a normal STAT check (not an urgent repair)
    /// and is overwritten by regular scheduling once the check completes.
    /// </summary>
    public static readonly DateTimeOffset ForcedRecheckSentinel = DateTimeOffset.UnixEpoch.AddSeconds(1);

    /// <summary>
    /// One-time boot delay before the first background sweep. Queue resume, first streams,
    /// and pool warm-up hit cold connection pools simultaneously at startup; giving them a
    /// short grace window keeps health-check STATs out of the connection storm (#881).
    /// <para>
    /// Settable for tests so coverage does not wait on the real delay. Tests that override
    /// it must restore the original value in a finally block; otherwise later tests in the
    /// same process silently skip the grace.
    /// </para>
    /// </summary>
    internal static TimeSpan StartupGracePeriod { get; set; } = TimeSpan.FromSeconds(20);

    // A release keeps full depth for its first year, then tapers until it stops aging at ten.
    private const double FullDepthDays = 365;
    private const double MinDepthDays = 3650;

    private readonly ConfigManager _configManager;
    private readonly ArrReplacementSearchBudget _replacementSearchBudget;
    private readonly ArrInstanceBackoff _arrBackoff;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly BenchmarkGate _benchmarkGate;
    private readonly StreamingFailureTracker _failureTracker;
    private readonly IQueueCoordinator _queueManager;
    private readonly Par2RepairService _par2RepairService;
    private readonly RepairPatchStore _repairPatchStore;
    private readonly IDbContextFactory<DavDatabaseContext>? _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly HealthWorkSchedulePolicy? _healthWorkSchedule;
    private readonly HealthCheckConnectionGate _healthCheckConnectionGate;
    private readonly ConcurrentDictionary<Guid, InProgressHealthCheck> _inProgress = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _workerFailureCooldowns = new();
    private readonly SemaphoreSlim _workerAdmissionGate = new(1, 1);
    private TaskCompletionSource _workerStateChanged = CreateWorkerStateSignal();
    private long _infrastructureBackoffUntilUtcTicks;
    private int _disposed;

    private static readonly HashSet<string> _missingSegmentIds = [];
    private static readonly Queue<string> _missingSegmentOrder = [];
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> _recentRepairRemovalsByPath = new();

    internal TimeSpan CoordinatorPollInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan CoordinatorIdleInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal Func<DavDatabaseContext>? CreateDbContextOverride { get; set; }
    internal Func<HashSet<Guid>, HealthWorkAdmission, CancellationToken, Task<Guid?>>? SelectCandidateOverride
    { get; set; }
    internal Func<Guid, CancellationToken, Task>? ProcessCandidateOverride { get; set; }
    internal Func<bool>? HasActiveQueueItemsOverride { get; set; }
    internal IReadOnlyCollection<Guid> InProgressHealthCheckIds => _inProgress.Keys.ToArray();

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager,
        BenchmarkGate benchmarkGate,
        StreamingFailureTracker failureTracker,
        IQueueCoordinator queueManager,
        Par2RepairService par2RepairService,
        RepairPatchStore repairPatchStore,
        ArrReplacementSearchBudget replacementSearchBudget,
        HealthCheckConnectionGate healthCheckConnectionGate,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null,
        TimeProvider? timeProvider = null,
        HealthWorkSchedulePolicy? healthWorkSchedule = null,
        ArrInstanceBackoff? arrBackoff = null
    )
    {
        _configManager = configManager;
        _replacementSearchBudget = replacementSearchBudget;
        _arrBackoff = arrBackoff ?? new ArrInstanceBackoff();
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;
        _benchmarkGate = benchmarkGate;
        _failureTracker = failureTracker;
        _queueManager = queueManager;
        _par2RepairService = par2RepairService;
        _repairPatchStore = repairPatchStore;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthWorkSchedule = healthWorkSchedule;
        _healthCheckConnectionGate = healthCheckConnectionGate;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when provider settings change, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
            lock (_missingSegmentIds)
            {
                _missingSegmentIds.Clear();
                _missingSegmentOrder.Clear();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupGracePeriod, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await ClearNonMediaHealthCheckEntriesAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCoordinatorIterationAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
                {
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    RecordInfrastructureBackoff();
                    if (e.TryGetKnownErrorMessage(out var reason))
                    {
                        Log.Warning("Background health coordinator deferred. Reason: {Reason}", reason);
                        Log.Debug(e, "Background health coordinator known failure stack");
                    }
                    else
                    {
                        Log.Error(
                            e,
                            "Unexpected error coordinating background health checks: {Message}",
                            e.Message);
                    }

                    await Task.Delay(CoordinatorIdleInterval, _timeProvider, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (
            stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
        {
            // Normal hosted-service shutdown.
        }
        finally
        {
            var drainDeadline = Task.Delay(WorkerDrainTimeout, CancellationToken.None);
            await CancelAllActiveWorkersAsync(drainDeadline).ConfigureAwait(false);
            await DrainActiveWorkersAsync(drainDeadline).ConfigureAwait(false);
        }
    }

    internal Task ExecuteHostedServiceForTests(CancellationToken stoppingToken) =>
        ExecuteAsync(stoppingToken);

    internal async Task RunCoordinatorIterationAsync(CancellationToken ct)
    {
        await ReapCompletedWorkersAsync().ConfigureAwait(false);
        var outcome = await RefillWorkerSlotsAsync(ct).ConfigureAwait(false);
        await WaitForCoordinatorWakeAsync(outcome, ct).ConfigureAwait(false);
        await ReapCompletedWorkersAsync().ConfigureAwait(false);
    }

    internal async Task<HealthCheckRefillOutcome> RefillWorkerSlotsAsync(CancellationToken ct)
    {
        await _workerAdmissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInfrastructureBackoffActive())
                return HealthCheckRefillOutcome.Blocked;

            while (_inProgress.Count < _configManager.GetHealthCheckWorkers())
            {
                if (_benchmarkGate.IsPaused || !_configManager.IsRepairJobEnabled())
                    return HealthCheckRefillOutcome.Blocked;

                var admission = _healthWorkSchedule?.Evaluate(_timeProvider.GetUtcNow())
                    ?? new HealthWorkAdmission(
                        ChecksOpen: true,
                        RepairsOpen: true,
                        NextChecksChange: null,
                        NextRepairsChange: null,
                        TimeZoneId: TimeZoneInfo.Local.Id,
                        ManualRunActive: false);
                if (!admission.ChecksOpen && !admission.RepairsOpen)
                    return HealthCheckRefillOutcome.Blocked;

                // Preserve current-main admission semantics: do not start another library check
                // while queue work is active. A check already in flight may overlap with newly
                // admitted queue work, where the shared gate gives queue verification priority.
                if (HasActiveQueueItems)
                    return HealthCheckRefillOutcome.Blocked;

                var reservedIds = GetReservedHealthCheckIds();
                if (SelectCandidateOverride is { } selector)
                {
                    var candidateId = await selector(reservedIds, admission, ct)
                        .ConfigureAwait(false);
                    if (candidateId is not { } id)
                    {
                        if (_inProgress.IsEmpty)
                            _healthWorkSchedule?.EndManualRun();
                        return HealthCheckRefillOutcome.NoCandidate;
                    }
                    if (IsWorkerFailureCooldownActive(id))
                        return HealthCheckRefillOutcome.Blocked;
                    if (!TryStartWorker(id, ct))
                        return _inProgress.IsEmpty
                            ? HealthCheckRefillOutcome.Blocked
                            : HealthCheckRefillOutcome.Started;
                    continue;
                }

                var availableSlots = _configManager.GetHealthCheckWorkers() - _inProgress.Count;
                var candidateIds = await SelectNextHealthCheckIdsAsync(
                        reservedIds,
                        admission.ChecksOpen,
                        admission.RepairsOpen,
                        availableSlots,
                        ct)
                    .ConfigureAwait(false);
                if (candidateIds.Count == 0)
                {
                    if (_inProgress.IsEmpty)
                        _healthWorkSchedule?.EndManualRun();
                    return HealthCheckRefillOutcome.NoCandidate;
                }

                foreach (var id in candidateIds)
                {
                    if (_inProgress.Count >= _configManager.GetHealthCheckWorkers()) break;
                    _ = TryStartWorker(id, ct);
                }

                return HealthCheckRefillOutcome.Started;
            }

            return HealthCheckRefillOutcome.Started;
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            RecordInfrastructureBackoff();
            throw;
        }
        finally
        {
            _workerAdmissionGate.Release();
        }
    }

    private bool TryStartWorker(Guid id, CancellationToken ct)
    {
#pragma warning disable CA2000 // cancellation ownership transfers to the registered worker and is disposed by the coordinator reaper
        var worker = new InProgressHealthCheck(
            ContextualCancellationTokenSource.CreateLinkedTokenSource(ct));
#pragma warning restore CA2000
        if (!_inProgress.TryAdd(id, worker))
        {
            worker.Dispose();
            return false;
        }

        try
        {
#pragma warning disable CA2025 // the coordinator owns this task, observes it in ReapCompletedWorkersAsync, and only then disposes the worker
            worker.ProcessingTask = RunHealthCheckWorkerAsync(id, worker);
#pragma warning restore CA2025
        }
        catch
        {
            _inProgress.TryRemove(id, out _);
            worker.Dispose();
            SignalWorkerStateChanged();
            throw;
        }
        SignalWorkerStateChanged();
        return true;
    }

    internal async Task<IReadOnlyList<Guid>> SelectNextHealthCheckIdsAsync(
        HashSet<Guid> activeIds,
        bool allowChecks,
        bool allowRepairs,
        int maximumCount,
        CancellationToken ct)
    {
        if (maximumCount <= 0) return [];

        await using var dbContext = CreateContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var currentDateTime = _timeProvider.GetUtcNow();
        IQueryable<DavItem> queue = GetHealthCheckQueueItems(dbClient)
            .Where(item =>
                item.NextHealthCheck == null ||
                item.NextHealthCheck < currentDateTime ||
                (allowRepairs && item.HealthRepairPending));
        if (activeIds.Count > 0)
            queue = queue.Where(item => !activeIds.Contains(item.Id));

        var selected = new List<Guid>(maximumCount);
        await foreach (var item in queue
            .AsAsyncEnumerable()
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            var isRepair = item.NextHealthCheck == DateTimeOffset.UnixEpoch
                || item.HealthRepairPending;
            if ((allowRepairs && isRepair)
                || (allowChecks && !isRepair && FilenameUtil.IsHealthCheckCandidate(item.Name)))
            {
                selected.Add(item.Id);
                if (selected.Count == maximumCount) break;
            }
        }

        return selected;
    }

    internal Task<IReadOnlyList<Guid>> SelectNextHealthCheckIdsAsync(
        HashSet<Guid> activeIds,
        bool allowUrgentRepair,
        int maximumCount,
        CancellationToken ct) =>
        SelectNextHealthCheckIdsAsync(
            activeIds,
            allowChecks: true,
            allowRepairs: allowUrgentRepair,
            maximumCount: maximumCount,
            ct: ct);

    private async Task RunHealthCheckWorkerAsync(
        Guid davItemId,
        InProgressHealthCheck worker)
    {
        var workerToken = worker.Cancellation.Token;
        try
        {
            if (ProcessCandidateOverride is { } processCandidate)
            {
                await processCandidate(davItemId, workerToken).ConfigureAwait(false);
                return;
            }

            await using var dbContext = CreateContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var davItem = await dbContext.Items
                .SingleOrDefaultAsync(item => item.Id == davItemId, workerToken)
                .ConfigureAwait(false);
            if (davItem is null) return;

            var concurrency = _configManager.GetHealthCheckConcurrency();
            await PerformHealthCheck(
                    davItem,
                    dbClient,
                    concurrency,
                    workerToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            RecordWorkerInfrastructureFailure(davItemId);
            CompleteHealthProgressIfStarted(davItemId, worker);
            if (e.TryGetKnownErrorMessage(out var reason))
            {
                Log.Warning(
                    "Background health check for {DavItemId} deferred. Reason: {Reason}",
                    davItemId,
                    reason);
                Log.Debug(e, "Background health check known failure stack for {DavItemId}", davItemId);
            }
            else
            {
                Log.Error(
                    e,
                    "Unexpected error performing background health check for {DavItemId}: {Message}",
                    davItemId,
                    e.Message);
            }
        }
        finally
        {
            SignalWorkerStateChanged();
        }
    }

    internal async Task ReapCompletedWorkersAsync()
    {
        var completed = _inProgress
            .Where(entry => entry.Value.ProcessingTask?.IsCompleted == true)
            .ToArray();
        ExceptionDispatchInfo? fatalFailure = null;
        ExceptionDispatchInfo? otherFailure = null;
        foreach (var (davItemId, worker) in completed)
        {
            try
            {
                await worker.ProcessingTask!.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (e is OutOfMemoryException)
                {
                    fatalFailure ??= ExceptionDispatchInfo.Capture(e);
                }
                else
                {
                    otherFailure ??= ExceptionDispatchInfo.Capture(e);
                }
            }
            finally
            {
                CompleteHealthProgressIfStarted(davItemId, worker);
                if (_inProgress.TryGetValue(davItemId, out var current)
                    && ReferenceEquals(current, worker))
                    _inProgress.TryRemove(davItemId, out _);
                worker.Dispose();
                SignalWorkerStateChanged();
            }
        }

        if (fatalFailure is not null)
            RequestCancellationForAllWorkers();
        (fatalFailure ?? otherFailure)?.Throw();
    }

    private async Task DrainActiveWorkersAsync(Task deadline)
    {
        ExceptionDispatchInfo? failure = null;
        var timedOut = false;
        var detachedWorkerCount = 0;
        try
        {
            while (!_inProgress.IsEmpty)
            {
                try
                {
                    await ReapCompletedWorkersAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                    break;
                }

                var workers = _inProgress.Values
                    .Select(worker => worker.ProcessingTask)
                    .Where(task => task is not null)
                    .Cast<Task>()
                    .ToArray();
                if (workers.Length == 0)
                {
                    var stateChanged = Volatile.Read(ref _workerStateChanged).Task;
                    if (ReferenceEquals(
                            await Task.WhenAny(stateChanged, deadline).ConfigureAwait(false),
                            deadline))
                    {
                        timedOut = true;
                        break;
                    }
                    continue;
                }
                if (ReferenceEquals(
                        await Task.WhenAny([.. workers, deadline]).ConfigureAwait(false),
                        deadline))
                {
                    timedOut = true;
                    break;
                }
            }
        }
        finally
        {
            if (!_inProgress.IsEmpty)
            {
                detachedWorkerCount = _inProgress.Count;
                ObserveRemainingWorkers();
            }
        }

        if (timedOut)
        {
            Log.Warning(
                "Background health worker drain exceeded {Timeout}; " +
                "{Count} worker(s) will be observed asynchronously",
                WorkerDrainTimeout,
                detachedWorkerCount);
        }

        failure?.Throw();
    }

    private void ObserveRemainingWorkers()
    {
        foreach (var (davItemId, worker) in _inProgress.ToArray())
        {
            if (worker.ProcessingTask is not { } task)
            {
                if (_inProgress.TryGetValue(davItemId, out var current)
                    && ReferenceEquals(current, worker))
                    _inProgress.TryRemove(davItemId, out _);
                worker.Dispose();
                SignalWorkerStateChanged();
                continue;
            }
#pragma warning disable CA2025 // this continuation is the explicit owner/observer after the bounded coordinator drain expires
            _ = task.ContinueWith(
                static (completedTask, state) =>
                {
                    var (service, id, detachedWorker) =
                        ((HealthCheckService, Guid, InProgressHealthCheck))state!;
                    service.CompleteDetachedWorker(id, detachedWorker, completedTask);
                },
                (this, davItemId, worker),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
#pragma warning restore CA2025
        }
    }

    private void CompleteDetachedWorker(
        Guid davItemId,
        InProgressHealthCheck worker,
        Task completedTask)
    {
        CompleteHealthProgressIfStarted(davItemId, worker);
        if (completedTask.Exception is { } aggregate)
        {
            foreach (var exception in aggregate.Flatten().InnerExceptions)
            {
                if (exception is OutOfMemoryException)
                    Log.Fatal(exception, "Detached health worker {DavItemId} ran out of memory", davItemId);
                else
                    Log.Error(exception, "Detached health worker {DavItemId} faulted", davItemId);
            }
        }

        if (_inProgress.TryGetValue(davItemId, out var current)
            && ReferenceEquals(current, worker))
            _inProgress.TryRemove(davItemId, out _);
        worker.Dispose();
        SignalWorkerStateChanged();
    }

    private async Task WaitForCoordinatorWakeAsync(
        HealthCheckRefillOutcome outcome,
        CancellationToken ct)
    {
        var stateChanged = Volatile.Read(ref _workerStateChanged).Task;
        if (HasCompletedWorker()) return;

        var interval = GetCoordinatorWaitInterval(outcome);
        var delay = Task.Delay(interval, _timeProvider, ct);
        await Task.WhenAny(stateChanged, delay).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    internal TimeSpan GetCoordinatorWaitInterval(HealthCheckRefillOutcome outcome) =>
        _inProgress.IsEmpty
        && outcome is HealthCheckRefillOutcome.NoCandidate or HealthCheckRefillOutcome.Blocked
            ? CoordinatorIdleInterval
            : CoordinatorPollInterval;

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken)
    {
        await _workerAdmissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _workerAdmissionGate.Release();

        while (!_inProgress.IsEmpty)
        {
            var stateChanged = Volatile.Read(ref _workerStateChanged).Task;
            if (_inProgress.IsEmpty) break;
            await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _healthCheckConnectionGate.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        base.Dispose();
        _workerAdmissionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool HasCompletedWorker() =>
        _inProgress.Values.Any(worker => worker.ProcessingTask?.IsCompleted == true);

    private void RequestCancellationForAllWorkers()
    {
        foreach (var worker in _inProgress.Values)
            _ = worker.CancelAsync();
    }

    private async Task CancelAllActiveWorkersAsync(Task deadline)
    {
        var cancellations = _inProgress.Values
            .Select(worker => worker.CancelAsync())
            .ToArray();
        if (cancellations.Length == 0) return;

        var allCancellations = Task.WhenAll(cancellations);
        if (ReferenceEquals(
                await Task.WhenAny(allCancellations, deadline).ConfigureAwait(false),
                deadline))
        {
            Log.Warning(
                "Background health worker cancellation exceeded {Timeout}; " +
                "continuing the bounded drain",
                WorkerDrainTimeout);
            return;
        }

        await allCancellations.ConfigureAwait(false);
    }

    private HashSet<Guid> GetReservedHealthCheckIds()
    {
        var utcNow = _timeProvider.GetUtcNow();
        var reserved = _inProgress.Keys.ToHashSet();
        foreach (var (id, cooldownUntil) in _workerFailureCooldowns)
        {
            if (cooldownUntil > utcNow)
                reserved.Add(id);
            else if (_workerFailureCooldowns.TryGetValue(id, out var current)
                     && current == cooldownUntil)
                _workerFailureCooldowns.TryRemove(id, out _);
        }

        return reserved;
    }

    internal bool IsWorkerFailureCooldownActive(Guid davItemId)
    {
        if (!_workerFailureCooldowns.TryGetValue(davItemId, out var cooldownUntil))
            return false;
        if (cooldownUntil > _timeProvider.GetUtcNow())
            return true;

        if (_workerFailureCooldowns.TryGetValue(davItemId, out var current)
            && current == cooldownUntil)
            _workerFailureCooldowns.TryRemove(davItemId, out _);
        return false;
    }

    private void RecordWorkerInfrastructureFailure(Guid davItemId)
    {
        var cooldownUntil = _timeProvider.GetUtcNow() + InfrastructureFailureCooldown;
        _workerFailureCooldowns.AddOrUpdate(
            davItemId,
            cooldownUntil,
            (_, current) => current >= cooldownUntil ? current : cooldownUntil);
        RecordInfrastructureBackoff();
    }

    private bool IsInfrastructureBackoffActive() =>
        _timeProvider.GetUtcNow().UtcTicks
        < Interlocked.Read(ref _infrastructureBackoffUntilUtcTicks);

    private void RecordInfrastructureBackoff()
    {
        var nextAttempt = (_timeProvider.GetUtcNow() + InfrastructureFailureCooldown).UtcTicks;
        while (true)
        {
            var current = Interlocked.Read(ref _infrastructureBackoffUntilUtcTicks);
            if (current >= nextAttempt) return;
            if (Interlocked.CompareExchange(
                    ref _infrastructureBackoffUntilUtcTicks,
                    nextAttempt,
                    current) == current)
                return;
        }
    }

    private static TaskCompletionSource CreateWorkerStateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalWorkerStateChanged()
    {
        var previous = Interlocked.Exchange(
            ref _workerStateChanged,
            CreateWorkerStateSignal());
        previous.TrySetResult();
    }

    private sealed class InProgressHealthCheck(
        ContextualCancellationTokenSource cancellation) : IDisposable
    {
        private const int ProgressStartedFlag = 1;
        private const int ProgressClosedFlag = 2;
        private readonly Lock _cancellationLock = new();
        private readonly Lock _progressLock = new();
        private Task? _cancellationTask;
        private int _disposed;
        private int _progressState;

        public ContextualCancellationTokenSource Cancellation { get; } = cancellation;
        public Task? ProcessingTask { get; set; }

        public bool TryStartProgress()
        {
            lock (_progressLock)
            {
                if ((_progressState & ProgressClosedFlag) != 0) return false;
                _progressState |= ProgressStartedFlag;
                return true;
            }
        }

        public bool TryPublishProgress(Action publish)
        {
            lock (_progressLock)
            {
                if ((_progressState & ProgressClosedFlag) != 0) return false;
                publish();
                return true;
            }
        }

        public bool TryCloseProgress(out bool wasStarted)
        {
            lock (_progressLock)
            {
                wasStarted = (_progressState & ProgressStartedFlag) != 0;
                if ((_progressState & ProgressClosedFlag) != 0) return false;
                _progressState |= ProgressClosedFlag;
                return true;
            }
        }

        public Task CancelAsync()
        {
            lock (_cancellationLock)
                return _cancellationTask ??= CancelCoreAsync();
        }

        private async Task CancelCoreAsync()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            try
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A completed worker can be reaped while a fatal sibling requests cancellation.
            }
            catch (Exception e)
            {
                if (e is OutOfMemoryException)
                    Log.Fatal(e, "Health worker cancellation callback ran out of memory");
                else
                    Log.Error(e, "Health worker cancellation callback failed");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Cancellation.Dispose();
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        // Playback-triggered urgent and schedule-deferred repairs stay first. Never-checked
        // files come next so routine rechecks cannot starve the initial scan, then
        // operator-forced rechecks (newest release first), then routine scheduled rechecks.
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x =>
                x.NextHealthCheck == DateTimeOffset.UnixEpoch || x.HealthRepairPending ? 0 :
                x.NextHealthCheck == null ? 1 :
                x.NextHealthCheck == ForcedRecheckSentinel ? 2 : 3)
            .ThenBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        // History-linked files are skipped for routine STAT checks so they do not race SAB
        // post-processing. UnixEpoch is the playback-triggered urgent sentinel from
        // ExceptionMiddleware; ForcedRecheckSentinel is the operator-triggered full-library
        // recheck from the reset-health-check-queue endpoint. Those sentinels and deferred
        // repairs intentionally override only that exclusion.
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x =>
                x.HistoryItemId == null ||
                x.NextHealthCheck == DateTimeOffset.UnixEpoch ||
                x.HealthRepairPending ||
                x.NextHealthCheck == ForcedRecheckSentinel);
    }

    private DavDatabaseContext CreateContext() =>
        DavDatabaseContexts.Create(CreateDbContextOverride, _dbContextFactory);

    private bool HasActiveQueueItems =>
        HasActiveQueueItemsOverride?.Invoke() ?? _queueManager.HasActiveQueueItems;

    /// <summary>
    /// One-shot cleanup: clear <c>NextHealthCheck</c>/<c>LastHealthCheck</c> for non-media files
    /// (images, subtitles, NFOs, etc.) that were queued before the media-type filter was added.
    /// Urgent repairs (<c>UnixEpoch</c> sentinel) are never cleared.
    /// </summary>
    private async Task ClearNonMediaHealthCheckEntriesAsync(CancellationToken ct)
    {
        try
        {
            await using var dbContext = CreateContext();
            await ClearNonMediaHealthCheckEntries(dbContext, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warning(e, "Could not clear non-media health-check entries: {Message}", e.Message);
        }
    }

    internal static async Task ClearNonMediaHealthCheckEntries(
        DavDatabaseContext dbContext,
        CancellationToken ct)
    {
        var urgent = DateTimeOffset.UnixEpoch;
        const int batchSize = 1000;
        var cleared = 0;
        var lastId = Guid.Empty;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = dbContext.Items
                .Where(x => x.Type == DavItem.ItemType.UsenetFile)
                .Where(x => x.NextHealthCheck != urgent)
                .Where(x => x.NextHealthCheck != null || x.LastHealthCheck != null);
            if (lastId != Guid.Empty) query = query.Where(x => x.Id > lastId);

            var batch = await query
                .OrderBy(x => x.Id)
                .Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);

            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var item in batch.Where(x => !FilenameUtil.IsHealthCheckCandidate(x.Name)))
            {
                item.NextHealthCheck = null;
                item.LastHealthCheck = null;
                cleared++;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
        }

        if (cleared > 0)
        {
            Log.Information(
                "Cleared health-check schedule for {Count} non-media file(s) " +
                "(images, subtitles, NFOs, and other non-playable files are no longer health-checked)",
                cleared);
        }
    }

    // internal for tests: the degraded-classification scenarios drive this directly.
    internal async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        // Urgent sentinel set by ExceptionMiddleware when streaming confirms a permanent failure.
        // Skip the STAT-only recheck and repair immediately: STAT can pass while BODY returns 430
        // (see nzbdav-dev#209), and structurally corrupt archives can have every article present.
        var isUrgentRepair = davItem.NextHealthCheck == DateTimeOffset.UnixEpoch;
        var repairsAdmitted = _healthWorkSchedule?.Evaluate(_timeProvider.GetUtcNow()).RepairsOpen ?? true;

        // Attribution for latency histograms — does not change pool admission priority.
        using var maintenanceScope = ct.SetContext(MaintenanceDownloadContext.Instance);
        using var fetchAttribution = FetchAttributionContext.Begin(davItem.Name);

        ContextualCancellationTokenSource? statCts = null;
        try
        {
            if (isUrgentRepair)
            {
                // Validate the local payload before Repair: missing metadata is
                // local data loss, not a bad release, and must not reach the
                // Arr remove-and-blocklist path. Routine checks get the same
                // guarantee from LoadHealthCheckPayloadAsync below.
                try
                {
                    await EnsurePayloadExistsAsync(davItem, dbClient, ct).ConfigureAwait(false);
                }
                catch (OutOfMemoryException oom)
                {
                    await DeferPayloadOutOfMemoryAsync(davItem, dbClient, oom, ct).ConfigureAwait(false);
                    return;
                }
                Log.Information("Performing urgent dynamic repair for {FilePath}", davItem.Path);
                await HandleUrgentRepair(davItem, dbClient, repairsAdmitted, ct).ConfigureAwait(false);
                return;
            }

            HealthCheckPayload payload;
            try
            {
                payload = await LoadHealthCheckPayloadAsync(davItem, dbClient, ct).ConfigureAwait(false);
            }
            catch (OutOfMemoryException oom)
            {
                await DeferPayloadOutOfMemoryAsync(davItem, dbClient, oom, ct).ConfigureAwait(false);
                return;
            }

            var segments = payload.Segments;
            var nzbFile = payload.NzbFile;

            // update the release date, if null
            if (davItem.ReleaseDate == null)
            {
                using var healthAdmissionScope = ct.SetContext(
                    new HealthCheckAdmissionContext(
                        _healthCheckConnectionGate,
                        HealthCheckAdmissionPriority.Background));
                await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);
            }

            // Sample large files to reduce NNTP load while keeping head/tail/stride coverage.
            // SegmentIndexView retains source indices, so later repair logic does not need to
            // build a dictionary over the full payload just to recover an index.
            var totalSegments = segments.Count;
            var age = _configManager.IsHealthCheckAgingEnabled() && davItem.ReleaseDate is { } posted
                ? DateTimeOffset.UtcNow - posted
                : (TimeSpan?)null;
            SegmentIndexView sampled;
            SegmentIndexView statSegments;
            try
            {
                sampled = SampleSegmentsIndexed(segments, _configManager.GetHealthCheckDepth(), age);
                statSegments = nzbFile != null
                    ? FilterSegmentsForStat(sampled, nzbFile, _repairPatchStore)
                    : sampled;
            }
            catch (OutOfMemoryException oom)
            {
                await DeferPayloadOutOfMemoryAsync(davItem, dbClient, oom, ct).ConfigureAwait(false);
                return;
            }

            // A damaged-but-tolerable video file can only be told apart from a failed one by a
            // full-coverage sweep of an eligible container with recorded segment sizes (#461).
            // PAR2-patched segments are stripped from the STAT list by FilterSegmentsForStat but
            // still count toward coverage: they are served locally and can never be holes, so the
            // full-coverage test uses the sampled count, not the STAT count.
            var segmentRanges = nzbFile?.SegmentByteRanges;
            // SegmentByteRanges is [NotMapped] and never materializes for legacy EF-fallback items, so a non-null value already implies FileBlobId != null.
            var canClassify = _configManager.IsDegradedToleranceEnabled()
                              && davItem.SubType == DavItem.ItemSubType.NzbFile
                              && FilenameUtil.IsDegradedToleranceEligible(davItem.Name)
                              && segmentRanges is not null
                              && segmentRanges.Length == totalSegments
                              && sampled.Count == totalSegments;

            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            _inProgress.TryGetValue(davItem.Id, out var progressWorker);
            progressHook.ProgressChanged += (_, progress) =>
            {
                try { statCts?.CancelAfter(HealthCheckProgressTimeout); }
                catch (ObjectDisposedException)
                {
                    // statCts may already be disposed when a progress event races teardown.
                }
                if (!MarkHealthProgressStarted(davItem.Id, progressWorker)) return;
                var message = $"{davItem.Id}|{progress}";
                debounce(() =>
                {
                    if (progressWorker is null
                        || !_inProgress.TryGetValue(davItem.Id, out var currentWorker)
                        || !ReferenceEquals(currentWorker, progressWorker))
                        return;

                    _ = progressWorker.TryPublishProgress(
                        () => _ = _websocketManager.SendMessage(
                            WebsocketTopic.HealthItemProgress,
                            message));
                });
            };

            // Only cancel a STAT sweep after it has made no progress for a sustained
            // period. A complete/deep scan can otherwise run as long as it continues
            // advancing; cancellation reaches and drains every in-flight STAT request.
            List<int>? confirmedHoles = null;
            using (statCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                using var healthAdmissionScope = statCts.Token.SetContext(
                    new HealthCheckAdmissionContext(
                        _healthCheckConnectionGate,
                        HealthCheckAdmissionPriority.Background));
                statCts.CancelAfter(HealthCheckProgressTimeout);
                var progress = progressHook.ToPercentage(statSegments.Count);
                if (!canClassify)
                {
                    await ArticleExistenceChecker.CheckAsync(
                        _usenetClient, statSegments, concurrency, progress, statCts.Token).ConfigureAwait(false);
                }
                else
                {
                    // Sweep-and-collect every confirmed miss so the damage classifier can weigh
                    // the full hole set instead of aborting on the first one. STAT sweep chunk
                    // sizing is fixed in BaseNntpClient; the depth argument is BODY-oriented
                    // interface baggage and unused here.
                    var missingIds = await _usenetClient.CollectMissingSegmentsPipelinedAsync(
                            statSegments, depth: 0, concurrency, progress, statCts.Token)
                        .ConfigureAwait(false);
                    confirmedHoles = await ConfirmHolesThroughFallbacksAsync(
                            missingIds, statSegments, nzbFile!, concurrency, statCts)
                        .ConfigureAwait(false);
                }
            }
            CompleteHealthProgress(davItem.Id);

            var statHoles = confirmedHoles ?? [];
            List<int> remainingCorrupt = [];
            // canClassify is only true when SegmentByteRanges materialized from this nzbFile.
            if (canClassify
                && _configManager.IsCorruptionTrackingEnabled()
                && nzbFile!.CorruptSegmentIndices is { Length: > 0 } recorded)
            {
                remainingCorrupt = await FilterRecordedCorruptIndicesAsync(nzbFile, recorded, ct)
                    .ConfigureAwait(false);
            }

            if (canClassify && (statHoles.Count > 0 || remainingCorrupt.Count > 0))
            {
                await HandleConfirmedHolesAsync(
                        davItem, dbClient, nzbFile!, segments, segmentRanges!,
                        statHoles, remainingCorrupt, repairsAdmitted, ct)
                    .ConfigureAwait(false);
                return;
            }

            // update the database.
            // the next check is scheduled so the interval doubles with the item's age since release.
            // clamp to a minimum interval: a null release-date (zero-segment item) or a future-dated
            // article header would otherwise schedule the item in the past and hot-loop the service.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);

            // A previously degraded file that now sweeps clean has recovered (provider-side
            // restoration): drop the stale hole and corrupt records. The probed container
            // class is a permanent property of the file and survives the clear.
            if (canClassify
                && (nzbFile!.MissingSegmentIndices != null || nzbFile.CorruptSegmentIndices != null))
                await SwapNzbFileBlobAsync(davItem, nzbFile, null, null, replaceCorruptRecord: true)
                    .ConfigureAwait(false);

            var repairedCount = nzbFile != null
                ? Par2RepairService.CountRepairedSegments(nzbFile, _repairPatchStore)
                : 0;
            var healthyMessage = repairedCount > 0
                ? $"File is healthy ({repairedCount} segment{(repairedCount == 1 ? "" : "s")} repaired from PAR2 parity)."
                : sampled.Count < totalSegments
                    ? $"File is healthy (sampled {sampled.Count}/{totalSegments} segments)."
                    : "File is healthy.";
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                HealthCheckResult.RepairAction.None,
                healthyMessage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested && statCts?.IsCancellationRequested == true)
        {
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            Log.Warning(
                "Health check for {Path} made no STAT progress for {Timeout}. Deferred next check.",
                davItem.Path, HealthCheckProgressTimeout);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Health check deferred: no STAT progress for {HealthCheckProgressTimeout.TotalMinutes:0} minutes.",
                ct).ConfigureAwait(false);
        }
        catch (MissingFilePayloadException e)
        {
            // Local payload metadata is gone (commonly a database-only restore).
            // This says nothing about the release's health, so surface it for
            // operator action instead of deleting or blocklisting through Arr.
            CompleteHealthProgress(davItem.Id);
            var confirmations = await GetMissingPayloadConfirmationAsync(
                dbClient.Ctx,
                davItem.Id,
                ct).ConfigureAwait(false);
            var utcNow = _timeProvider.GetUtcNow();
            var delay = GetMissingPayloadRecheckDelay(confirmations);
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + delay;
            Log.Warning(
                "Health check cannot run for {Path}: {Reason} " +
                "(confirmation {Count}/{Threshold}; next check in {Delay})",
                davItem.Path,
                e.Message,
                confirmations,
                MissingPayloadConfirmationsRequired,
                delay);
            Log.Debug(e, "Missing streaming payload stack for {Path}", davItem.Path);
            var state = confirmations >= MissingPayloadConfirmationsRequired
                ? "The payload has been missing for at least 3 consecutive checks; " +
                  "this DavItem is orphaned and will be rechecked weekly."
                : "The file will be rechecked daily.";
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    MissingPayloadMessagePrefix,
                    "The file's streaming data is missing from the server",
                    "(often a database restore without the blobs/ folder).",
                    "Remove and re-download the release, or restore from a backup that includes blobs.",
                    state,
                ]), ct).ConfigureAwait(false);
        }
        catch (CorruptedBlobPayloadException e)
        {
            // The local streaming metadata blob exists but failed to decode (truncated
            // write, unclean shutdown). This is not evidence of a bad release, so surface
            // it for operator action instead of deleting or blocklisting through Arr.
            CompleteHealthProgress(davItem.Id);
            var confirmations = await GetUnreadablePayloadConfirmationAsync(
                dbClient.Ctx,
                davItem.Id,
                ct).ConfigureAwait(false);
            var utcNow = _timeProvider.GetUtcNow();
            var delay = GetMissingPayloadRecheckDelay(confirmations);
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + delay;
            Log.Warning(
                "Health check cannot run for {Path}: {Reason} " +
                "(confirmation {Count}/{Threshold}; next check in {Delay})",
                davItem.Path,
                e.Message,
                confirmations,
                MissingPayloadConfirmationsRequired,
                delay);
            Log.Debug(e, "Unreadable streaming metadata blob stack for {Path}", davItem.Path);
            var state = confirmations >= MissingPayloadConfirmationsRequired
                ? "The payload has been unreadable for at least 3 consecutive checks; " +
                  "this DavItem is orphaned and will be rechecked weekly."
                : "The file will be rechecked daily.";
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    UnreadablePayloadMessagePrefix,
                    "The file's local streaming metadata is present but unreadable",
                    "(often a truncated write or unclean shutdown).",
                    "Restore a backup of the blobs/ folder that matches the database,",
                    "or remove and re-download the release.",
                    state,
                ]), ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            CompleteHealthProgress(davItem.Id);
            if (FilenameUtil.IsImportantFileType(davItem.Name))
            {
                lock (_missingSegmentIds)
                {
                    if (_missingSegmentIds.Add(e.SegmentId))
                        _missingSegmentOrder.Enqueue(e.SegmentId);
                    while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                        _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
                }
            }

            if (!repairsAdmitted)
            {
                await DeferRepairUntilWindow(davItem, dbClient, ct).ConfigureAwait(false);
                return;
            }

            // When no Arr replacement is available, PAR2 remains the only automatic recovery path.
            var par2Outcome = ShouldAttemptPar2Repair()
                ? await _par2RepairService.TryPar2RepairAsync(
                    davItem, [e.SegmentId], ct).ConfigureAwait(false)
                : Par2RepairOutcome.NotRepaired;
            if (par2Outcome is not Par2RepairOutcome.NotRepaired)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Healthy,
                    par2Outcome is Par2RepairOutcome.Repaired
                        ? HealthCheckResult.RepairAction.RepairedViaPar2
                        : HealthCheckResult.RepairAction.None,
                    par2Outcome is Par2RepairOutcome.Repaired
                        ? "Missing segment repaired from PAR2 parity."
                        : "PAR2 verified every file slice and found no damage.",
                    ct).ConfigureAwait(false);
                return;
            }

            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
        }
        catch (UsenetUnexpectedResponseException e)
        {
            // Connection-level STAT failures (e.g. buffered 400 goodbye) must not trigger
            // repair or leave NextHealthCheck unset — defer and surface ActionNeeded.
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Unexpected NNTP response during health check: {e.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e.IsTransientTransportException() && e is not OutOfMemoryException)
        {
            // STAT/read timeouts and socket/IO failures must not dump stacks or trigger Arr repair —
            // defer and surface ActionNeeded with a single human-readable Warning.
            CompleteHealthProgress(davItem.Id);
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            e.TryGetKnownErrorMessage(out var reason);
            Log.Warning(
                "NNTP transport failure during health check for {Path}. Deferred next check. Reason: {Reason}",
                davItem.Path, reason);
            Log.Debug(e, "Health check transport failure stack for {Path}", davItem.Path);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                FormatTransportFailureHealthMessage(reason), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            await DeferHealthCheck(davItem, dbClient, e, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verdict handling for a full-coverage sweep that found confirmed holes on an
    /// eligible video file (#461): PAR2 reconstruction first, then container-aware
    /// classification. Degraded files persist their holes and stay on the recheck
    /// schedule instead of triggering Arr repair; Failed files take the same repair
    /// path as the legacy first-miss flow.
    /// </summary>
    private async Task HandleConfirmedHolesAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        DavNzbFile nzbFile,
        IReadOnlyList<string> segments,
        LongRange[] segmentRanges,
        List<int> missingIndices,
        List<int> corruptIndices,
        bool repairsAdmitted,
        CancellationToken ct)
    {
        if (!repairsAdmitted)
        {
            await DeferRepairUntilWindow(davItem, dbClient, ct).ConfigureAwait(false);
            return;
        }

        var holeIndices = missingIndices
            .Concat(corruptIndices)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        var holeSegmentIds = holeIndices.Select(index => segments[index]).ToArray();

        // PAR2 first, with the full hole list: reconstruct from parity before any verdict.
        // It is also the only automatic recovery path when no Arr replacement is available.
        var par2Outcome = ShouldAttemptPar2Repair()
            ? await _par2RepairService.TryPar2RepairAsync(
                davItem, holeSegmentIds, ct).ConfigureAwait(false)
            : Par2RepairOutcome.NotRepaired;
        if (par2Outcome is not Par2RepairOutcome.NotRepaired)
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            // The patched segments are served locally now; any earlier hole/corrupt record is obsolete.
            if (par2Outcome is Par2RepairOutcome.Repaired
                && (nzbFile.MissingSegmentIndices != null || nzbFile.CorruptSegmentIndices != null))
                await SwapNzbFileBlobAsync(davItem, nzbFile, null, null, replaceCorruptRecord: true)
                    .ConfigureAwait(false);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                par2Outcome is Par2RepairOutcome.Repaired
                    ? HealthCheckResult.RepairAction.RepairedViaPar2
                    : HealthCheckResult.RepairAction.None,
                par2Outcome is Par2RepairOutcome.Repaired
                    ? "Missing segment(s) repaired from PAR2 parity."
                    : "PAR2 verified every file slice and found no damage.",
                ct).ConfigureAwait(false);
            return;
        }

        var (containerClass, probedClass, criticalHeadEndExclusive) = await ResolveContainerClassAsync(
                davItem, nzbFile, segments, holeIndices, ct)
            .ConfigureAwait(false);
        var caps = new SegmentDamageCaps(
            _configManager.GetDegradedMaxConsecutiveMissing(),
            _configManager.GetDegradedMaxTotalMissing(),
            _configManager.GetDegradedMaxMissingBytePercent());
        var exactSegmentSizes = segmentRanges.Select(range => range.Count).ToArray();
        var segmentStarts = segmentRanges.Select(range => range.StartInclusive).ToArray();
        var verdict = SegmentDamageClassifier.Classify(
            holeIndices, segments.Count, exactSegmentSizes, segmentStarts,
            containerClass, caps, criticalHeadEndExclusive, out var reason);
        long? probedExtent = probedClass != null ? criticalHeadEndExclusive : null;

        if (verdict == SegmentDamageVerdict.Failed)
        {
            Log.Information(
                "Health check classified {Path} as failed: {Reason} Starting repair.",
                davItem.Path, reason);
            // Persist a fresh probe so a later check can reuse the class/extent without
            // another BODY. Do not record holes: Failed is not a degraded keep-the-file
            // verdict. Repair() commits the swapped FileBlobId with the health row.
            if (probedClass != null)
                await SwapNzbFileBlobAsync(
                    davItem, nzbFile, nzbFile.MissingSegmentIndices, probedClass, probedExtent)
                    .ConfigureAwait(false);
            // Seed the queue precheck with every confirmed miss so a re-grab of this release
            // fails fast pre-import (issue #732), then take today's repair path.
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                AddMissingSegmentIds(holeSegmentIds);
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
            return;
        }

        // Degraded: keep the file and persist the confirmed holes — but only when the record
        // actually changed, so an unchanged recheck does not churn blobs. Do not call Repair,
        // do not seed the fail-fast reimport cache (a re-grab of a release we chose to keep
        // must not fail), and do not clear the streaming-failure count: confirmed damage is
        // not health, and real playback failures keep escalating toward auto-remove.
        var missingToStore = missingIndices.Count == 0
            ? null
            : missingIndices.Distinct().OrderBy(index => index).ToArray();
        var corruptToStore = corruptIndices.Count == 0
            ? null
            : corruptIndices.Distinct().OrderBy(index => index).ToArray();
        var recordChanged = !SameIndexRecord(nzbFile.MissingSegmentIndices, missingToStore)
                            || !SameIndexRecord(nzbFile.CorruptSegmentIndices, corruptToStore)
                            || (probedClass != null && nzbFile.ContainerClass != probedClass)
                            || (probedExtent != null && nzbFile.CriticalHeadEndExclusive != probedExtent);
        if (recordChanged)
            await SwapNzbFileBlobAsync(
                    davItem, nzbFile, missingToStore, probedClass, probedExtent,
                    corruptToStore, replaceCorruptRecord: true)
                .ConfigureAwait(false);

        Log.Warning(
            "Health check classified {Path} as degraded: {Reason} Playback fills the gaps; repair skipped.",
            davItem.Path, reason);
        var degradedUtcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = degradedUtcNow;
        davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, degradedUtcNow);
        await RecordHealthResult(
            dbClient, davItem,
            HealthCheckResult.HealthResult.Degraded,
            HealthCheckResult.RepairAction.None,
            $"{reason} within tolerance for {MediaContainerClassMapping.Describe(containerClass)}. " +
            "Playback fills the gaps; repair skipped.", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the container class for the verdict: fixed by extension for the MKV/TS
    /// family, reused once persisted, otherwise probed once from a bounded read of the
    /// file head (MP4 family only). Probe failures propagate to the caller's catches —
    /// no verdict is recorded this cycle and the probe is retried next time.
    /// </summary>
    private async Task<(MediaContainerClass ContainerClass, byte? ProbedClass, long CriticalHeadEndExclusive)>
        ResolveContainerClassAsync(
            DavItem davItem,
            DavNzbFile nzbFile,
            IReadOnlyList<string> segments,
            List<int> holeIndices,
            CancellationToken ct)
    {
        if (MediaContainerClassMapping.ByExtension(davItem.Name) is { } byExtension)
            return (byExtension, null, 0);

        if (nzbFile.ContainerClass is byte persisted && Enum.IsDefined((MediaContainerClass)persisted))
            return ((MediaContainerClass)persisted, null, nzbFile.CriticalHeadEndExclusive ?? 0);

        // A hole at segment 0 fails classification regardless, and probing a missing
        // segment would just throw; never probe when the head is a hole.
        if (holeIndices[0] == 0)
            return (MediaContainerClass.Unknown, null, 0);

        // One bounded head read per file, ever: the probed class is persisted with the
        // hole record. Runs inside the caller's maintenance download context (attribution)
        // and cancellation scope; early disposal of the body stream is by design.
        using var healthAdmissionScope = ct.SetContext(
            new HealthCheckAdmissionContext(
                _healthCheckConnectionGate,
                HealthCheckAdmissionPriority.Background));
        var response = await _usenetClient.DecodedBodyAsync(segments[0], ct).ConfigureAwait(false);
        if (response.Stream is not { } headStream)
            throw new UsenetUnexpectedResponseException(segments[0], response.ResponseMessage);
        await using (headStream)
        {
            var buffer = new byte[64 * 1024];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await headStream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                if (read <= 0) break;
                filled += read;
            }

            var (probed, extent) = Mp4LayoutProbe.ClassifyMp4Head(buffer.AsSpan(0, filled));
            return (probed, (byte)probed, extent);
        }
    }

    /// <summary>
    /// Playback fetches fallback MessageIds before zero-filling
    /// (MultiSegmentStream/UnbufferedMultiSegmentStream), so a primary-miss segment with
    /// any live fallback is servable, not a hole. A segment is a hole only when the
    /// primary and every fallback are definitively missing. Non-definitive responses
    /// throw into the defer catches rather than guessing a verdict.
    /// </summary>
    private async Task<List<int>> ConfirmHolesThroughFallbacksAsync(
        IReadOnlyList<string> primaryMissIds,
        SegmentIndexView statSegments,
        DavNzbFile nzbFile,
        int concurrency,
        ContextualCancellationTokenSource statCts)
    {
        if (primaryMissIds.Count == 0) return [];

        var primaryMisses = new HashSet<string>(primaryMissIds, StringComparer.Ordinal);
        var missingIndices = new List<int>(primaryMisses.Count);
        for (var index = 0; index < statSegments.Count; index++)
        {
            if (primaryMisses.Contains(statSegments[index]))
                missingIndices.Add(statSegments.SourceIndexAt(index));
        }

        var checks = missingIndices
            .Select(async index => (
                Index: index,
                IsHole: await IsConfirmedHoleAsync(index, nzbFile, statCts.Token).ConfigureAwait(false)))
            .WithConcurrencyAsync(concurrency, statCts.Token);

        var holes = new List<int>();
        await foreach (var (index, isHole) in checks.ConfigureAwait(false))
        {
            // keep the no-progress watchdog armed while fallback STATs are in flight
            statCts.CancelAfter(HealthCheckProgressTimeout);
            if (isHole) holes.Add(index);
        }

        holes.Sort();
        return holes;
    }

    private async Task<bool> IsConfirmedHoleAsync(int segmentIndex, DavNzbFile nzbFile, CancellationToken ct)
    {
        if (nzbFile.SegmentFallbackIds is not { } fallbackIds ||
            segmentIndex >= fallbackIds.Length ||
            fallbackIds[segmentIndex] is not { Length: > 0 } alternates)
            return true;

        foreach (var fallbackId in alternates)
        {
            var response = await _usenetClient.StatAsync(fallbackId, ct).ConfigureAwait(false);
            if (response.ResponseType == UsenetResponseType.ArticleExists)
                return false;
            if (!UsenetArticleAvailability.IsDefinitiveMissing(response))
                throw new UsenetUnexpectedResponseException(fallbackId, response.ResponseMessage);
        }

        return true;
    }

    /// <summary>
    /// Persists the degraded-damage record by writing a NEW blob and swapping
    /// <see cref="DavItem.FileBlobId"/>, committed by the caller's SaveChangesAsync in
    /// the same transaction as the health row. The TR_DavItems_Update_AddBlobCleanup
    /// trigger queues the old blob for deferred cleanup and in-flight readers keep their
    /// open handle. The instance handed in is the shared MetadataCache entry — never
    /// mutate it; write a fresh copy. Delegates to
    /// <see cref="DavNzbFileBlobUpdater"/> so a concurrent corruption persist cannot
    /// drop Missing/Container fields (and vice versa).
    /// </summary>
    private static Task SwapNzbFileBlobAsync(
        DavItem davItem,
        DavNzbFile nzbFile,
        int[]? missingSegmentIndices,
        byte? probedContainerClass,
        long? probedCriticalHeadEndExclusive = null,
        int[]? corruptSegmentIndices = null,
        bool replaceCorruptRecord = false) =>
        DavNzbFileBlobUpdater.MutateAsync(
            davItem,
            current =>
            {
                current.MissingSegmentIndices = missingSegmentIndices;
                current.ContainerClass = probedContainerClass ?? current.ContainerClass;
                current.CriticalHeadEndExclusive =
                    probedCriticalHeadEndExclusive ?? current.CriticalHeadEndExclusive;
                if (replaceCorruptRecord)
                    current.CorruptSegmentIndices = corruptSegmentIndices;
                return current;
            },
            fallback: nzbFile);

    private static bool SameIndexRecord(int[]? stored, int[]? next) =>
        (stored ?? []).SequenceEqual(next ?? []);

    private async Task<List<int>> FilterRecordedCorruptIndicesAsync(
        DavNzbFile nzbFile,
        IReadOnlyList<int> recorded,
        CancellationToken ct)
    {
        using var healthAdmissionScope = ct.SetContext(
            new HealthCheckAdmissionContext(
                _healthCheckConnectionGate,
                HealthCheckAdmissionPriority.Background));
        var remaining = new List<int>();
        var cap = _configManager.GetDegradedMaxTotalMissing();
        var probed = 0;
        var ranges = nzbFile.SegmentByteRanges;
        foreach (var index in recorded.Distinct().OrderBy(i => i)
                     .Where(i => (uint)i < (uint)nzbFile.SegmentIds.Length))
        {
            var segmentId = nzbFile.SegmentIds[index];
            var expectedSize = ranges is not null && index < ranges.Length ? ranges[index].Count : 0;
            if (expectedSize > 0 && _repairPatchStore.IsRepaired(segmentId, expectedSize))
                continue;

            if (probed >= cap)
            {
                remaining.Add(index);
                continue;
            }

            probed++;
            if (await TryConfirmSegmentCleanAsync(segmentId, ct).ConfigureAwait(false))
                continue;
            remaining.Add(index);
        }

        return remaining;
    }

    private async Task<bool> TryConfirmSegmentCleanAsync(string segmentId, CancellationToken ct)
    {
        try
        {
            var body = await _usenetClient.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
            await SegmentResponseValidator
                .ThrowOnSegmentIdMismatchAsync(segmentId, body)
                .ConfigureAwait(false);
            var stream = body.Stream;
            if (stream is null) return false;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, 8192), ct).ConfigureAwait(false);
                    if (read == 0) return true;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception e) when (
            e is UsenetCorruptArticleException
                or UsenetArticleNotFoundException
                or UsenetUnexpectedResponseException)
        {
            return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Re-confirmation probe of recorded corrupt segment {SegmentId} failed", segmentId);
            return false;
        }
    }

    internal bool MarkHealthProgressStarted(Guid davItemId)
    {
        _inProgress.TryGetValue(davItemId, out var worker);
        return MarkHealthProgressStarted(davItemId, worker);
    }

    private bool MarkHealthProgressStarted(
        Guid davItemId,
        InProgressHealthCheck? worker)
    {
        if (worker is null)
            return false;
        if (!_inProgress.TryGetValue(davItemId, out var currentWorker)
            || !ReferenceEquals(currentWorker, worker))
            return false;
        return worker.TryStartProgress();
    }

    private void CompleteHealthProgressIfStarted(
        Guid davItemId,
        InProgressHealthCheck worker)
    {
        if (!worker.TryCloseProgress(out var wasStarted) || !wasStarted)
            return;
        SendHealthProgressCompletion(davItemId);
    }

    private void CompleteHealthProgress(Guid davItemId)
    {
        if (_inProgress.TryGetValue(davItemId, out var worker)
            && !worker.TryCloseProgress(out _))
            return;

        SendHealthProgressCompletion(davItemId);
    }

    private void SendHealthProgressCompletion(Guid davItemId)
    {
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItemId}|100");
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItemId}|done");
    }

    private async Task DeferPayloadOutOfMemoryAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        OutOfMemoryException exception,
        CancellationToken ct)
    {
        CompleteHealthProgress(davItem.Id);
        OomDiagnostics.LogHeapStateOnOom(exception, $"health-check payload preparation for {davItem.Path}");

        var utcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
        await RecordHealthResult(
            dbClient,
            davItem,
            HealthCheckResult.HealthResult.Unhealthy,
            HealthCheckResult.RepairAction.ActionNeeded,
            "Health check deferred: the file's segment metadata exceeded the process memory limit. " +
            "Increase the address-space limit or reduce the release size before retrying.",
            ct).ConfigureAwait(false);
    }

    internal async Task DeferHealthCheck(
        DavItem davItem,
        DavDatabaseClient dbClient,
        Exception exception,
        CancellationToken ct)
    {
        var isKnownFailure = exception.TryGetKnownErrorMessage(out var reason);
        var utcNow = _timeProvider.GetUtcNow();
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = ComputeFailureNextHealthCheck(utcNow, isKnownFailure);

        CompleteHealthProgress(davItem.Id);

        if (isKnownFailure)
        {
            Log.Warning(
                "Health check failed for {Path}. Deferred next check until {NextHealthCheck}. Reason: {Reason}",
                davItem.Path, davItem.NextHealthCheck, reason);
            Log.Debug(exception, "Health check known failure stack for {Path}", davItem.Path);
        }
        else
        {
            Log.Error(
                exception,
                "Unexpected error during health check for {Path}. Deferred next check until {NextHealthCheck}",
                davItem.Path, davItem.NextHealthCheck);
        }

        try
        {
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                isKnownFailure
                    ? $"Health check deferred: {reason}"
                    : $"Unexpected error during health check: {exception.Message}",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception persistenceException) when (persistenceException is DbUpdateException or InvalidOperationException)
        {
            Log.Error(
                persistenceException,
                "Could not record deferred health-check result for {Path}; retrying schedule persistence",
                davItem.Path);

            foreach (var entry in dbClient.Ctx.ChangeTracker.Entries<HealthCheckResult>()
                         .Where(x => x.State == EntityState.Added))
                entry.State = EntityState.Detached;

            try
            {
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception scheduleException) when (scheduleException is DbUpdateException or InvalidOperationException)
            {
                RecordWorkerInfrastructureFailure(davItem.Id);
                Log.Error(
                    scheduleException,
                    "Could not persist deferred health-check schedule for {Path}",
                    davItem.Path);
            }
        }
    }

    /// <summary>
    /// Health-result message for deferred NNTP transport failures (timeouts, socket/IO).
    /// </summary>
    internal static string FormatTransportFailureHealthMessage(string reason) =>
        $"NNTP transport failure during health check: {reason}";

    /// <summary>
    /// Schedules a failed health check far enough in the future to avoid starving
    /// other items, while allowing known provider/configuration failures more time
    /// to be corrected than unexpected application failures.
    /// </summary>
    public static DateTimeOffset ComputeFailureNextHealthCheck(
        DateTimeOffset utcNow,
        bool knownFailure) =>
        utcNow + (knownFailure ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));

    /// <summary>
    /// Schedules the next health check so the interval doubles with the item's age since release,
    /// floored at one hour from <paramref name="utcNow"/> so null or future release dates cannot
    /// schedule the item in the past and hot-loop the service.
    /// </summary>
    public static DateTimeOffset ComputeNextHealthCheck(DateTimeOffset? releaseDate, DateTimeOffset utcNow)
    {
        var minimumNextHealthCheck = utcNow + TimeSpan.FromHours(1);
        var nextHealthCheck = releaseDate + 2 * (utcNow - releaseDate);
        return nextHealthCheck == null || nextHealthCheck < minimumNextHealthCheck
            ? minimumNextHealthCheck
            : nextHealthCheck.Value;
    }

    /// <summary>
    /// How many segments to STAT for one file. Files up to the floor are checked in full,
    /// larger ones are sampled based on their size, and an optional age scales the result
    /// down from there. <see cref="HealthCheckDepth.Complete"/> skips all of it.
    /// </summary>
    public static int SampleTarget(int segmentCount, HealthCheckDepth depth, TimeSpan? age = null)
    {
        if (depth == HealthCheckDepth.Complete) return segmentCount;
        var multiplier = CurveMultiplier(depth);
        var curve = Math.Max(SampleFloor, multiplier * Math.Sqrt((double)SampleFloor * segmentCount));
        return (int)Math.Min(segmentCount, curve * AgeWeight(age));
    }

    /// <summary>
    /// What each depth multiplies the sampling curve by. Complete has no multiplier because
    /// it never reaches the curve.
    /// </summary>
    private static double CurveMultiplier(HealthCheckDepth depth) => depth switch
    {
        HealthCheckDepth.Standard => 0.5,
        HealthCheckDepth.Enhanced => 1.0,
        HealthCheckDepth.Deep => 2.0,
        _ => throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth has no curve multiplier."),
    };

    /// <summary>
    /// Scales coverage down as a release ages with the same square root curve used for size.
    /// A post that has survived its first year is far less likely to be broken, so it
    /// gets a lighter check. The taper stops at ten years so nothing decays toward zero.
    /// </summary>
    private static double AgeWeight(TimeSpan? age)
    {
        if (age is not { } elapsed) return 1.0;
        return Math.Sqrt(FullDepthDays / Math.Clamp(elapsed.TotalDays, FullDepthDays, MinDepthDays));
    }

    /// <summary>
    /// Returns a stratified sample of <paramref name="segments"/>: first 100, last 100, and
    /// evenly spaced middle segments, sized by <see cref="SampleTarget"/>.
    /// </summary>
    public static List<string> SampleSegments(
        List<string> segments,
        HealthCheckDepth depth = ConfigManager.DefaultHealthCheckDepth,
        TimeSpan? age = null)
    {
        if (segments.Count <= SampleTarget(segments.Count, depth, age))
            return segments;

        return SampleSegmentsIndexed(segments, depth, age).ToList();
    }

    private static SegmentIndexView SampleSegmentsIndexed(
        IReadOnlyList<string> segments,
        HealthCheckDepth depth,
        TimeSpan? age)
    {
        var count = segments.Count;
        var target = SampleTarget(count, depth, age);
        if (count <= target) return new SegmentIndexView(segments);

        const int headCount = 100;
        const int tailCount = 100;
        var indexes = new HashSet<int>();

        for (var i = 0; i < Math.Min(headCount, count); i++)
            indexes.Add(i);

        for (var i = Math.Max(0, count - tailCount); i < count; i++)
            indexes.Add(i);

        var carry = 0L;
        for (var i = 0; i < count; i++)
        {
            carry += target;
            if (carry < count) continue;
            carry -= count;
            indexes.Add(i);
        }

        return new SegmentIndexView(segments, indexes.OrderBy(i => i).ToArray());
    }

    internal static List<string> FilterSegmentsForStat(
        List<string> sampledSegmentIds,
        List<string> allSegmentIds,
        DavNzbFile nzbFile,
        RepairPatchStore patchStore)
    {
        if (!patchStore.IsCatalogReady) return sampledSegmentIds;

        var ranges = nzbFile.SegmentByteRanges;
        var filtered = new List<string>(sampledSegmentIds.Count);
        foreach (var segmentId in sampledSegmentIds)
        {
            var index = allSegmentIds.IndexOf(segmentId);
            if (index < 0
                || ranges == null
                || index >= ranges.Length
                || !patchStore.IsRepaired(segmentId, ranges[index].Count))
                filtered.Add(segmentId);
        }

        return filtered;
    }

    private static SegmentIndexView FilterSegmentsForStat(
        SegmentIndexView sampledSegments,
        DavNzbFile nzbFile,
        RepairPatchStore patchStore)
    {
        if (!patchStore.IsCatalogReady || patchStore.EntryCount == 0) return sampledSegments;

        var ranges = nzbFile.SegmentByteRanges;
        List<int>? filteredIndexes = null;
        for (var index = 0; index < sampledSegments.Count; index++)
        {
            var sourceIndex = sampledSegments.SourceIndexAt(index);
            var segmentId = sampledSegments[index];
            var isRepaired = ranges != null
                             && sourceIndex < ranges.Length
                             && patchStore.IsRepaired(segmentId, ranges[sourceIndex].Count);
            if (!isRepaired)
            {
                filteredIndexes?.Add(sourceIndex);
                continue;
            }

            if (filteredIndexes != null) continue;

            filteredIndexes = new List<int>(sampledSegments.Count - 1);
            for (var prior = 0; prior < index; prior++)
                filteredIndexes.Add(sampledSegments.SourceIndexAt(prior));
        }

        return filteredIndexes is null
            ? sampledSegments
            : new SegmentIndexView(sampledSegments.Source, filteredIndexes.ToArray());
    }

    private async Task UpdateReleaseDate(DavItem davItem, IReadOnlyList<string> segments, CancellationToken ct)
    {
        var firstSegmentId = segments.Count == 0 ? null : StringUtil.EmptyToNull(segments[0]);
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await _usenetClient.HeadAsync(firstSegmentId, ct).ConfigureAwait(false);
        if (articleHeadersResponse.ArticleHeaders is not { } articleHeaders)
            throw new UsenetUnexpectedResponseException(
                firstSegmentId, articleHeadersResponse.ResponseMessage);
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private static async Task EnsurePayloadExistsAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        var exists = davItem.SubType switch
        {
            DavItem.ItemSubType.NzbFile =>
                await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            DavItem.ItemSubType.RarFile =>
                await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            DavItem.ItemSubType.MultipartFile =>
                await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false) is not null,
            _ => true,
        };
        if (!exists) throw new MissingFilePayloadException(davItem, davItem.SubType);
    }

    private async Task<HealthCheckPayload> LoadHealthCheckPayloadAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile is null
                ? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.NzbFile)
                : new HealthCheckPayload(nzbFile.SegmentIds, nzbFile);
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile is null
                ? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.RarFile)
                : new HealthCheckPayload(
                    new ConcatenatedSegmentView(rarFile.RarParts.Select(part => part.SegmentIds).ToArray()),
                    null);
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile is null
                ? throw new MissingFilePayloadException(davItem, DavItem.ItemSubType.MultipartFile)
                : new HealthCheckPayload(
                    new ConcatenatedSegmentView(multipartFile.Metadata.FileParts.Select(part => part.SegmentIds).ToArray()),
                    null);
        }

        return new HealthCheckPayload(Array.Empty<string>(), null);
    }

    private readonly record struct HealthCheckPayload(
        IReadOnlyList<string> Segments,
        DavNzbFile? NzbFile);

    /// <summary>
    /// A sampled projection that retains its original segment indexes. The projection only
    /// stores indexes when it is actually sampled; full checks use the source directly.
    /// </summary>
    private sealed class SegmentIndexView : IReadOnlyList<string>
    {
        private readonly int[]? _sourceIndexes;

        public SegmentIndexView(IReadOnlyList<string> source, int[]? sourceIndexes = null)
        {
            Source = source;
            _sourceIndexes = sourceIndexes;
        }

        public IReadOnlyList<string> Source { get; }
        public int Count => _sourceIndexes?.Length ?? Source.Count;
        public string this[int index] => Source[SourceIndexAt(index)];

        public int SourceIndexAt(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _sourceIndexes?[index] ?? index;
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Presents archive parts as one indexable sequence without copying every Message-ID
    /// into a temporary flattened list.
    /// </summary>
    internal sealed class ConcatenatedSegmentView : IReadOnlyList<string>
    {
        private readonly string[][] _parts;
        private readonly int[] _partEnds;

        public ConcatenatedSegmentView(string[][] parts)
        {
            _parts = parts.Where(part => part.Length > 0).ToArray();
            _partEnds = new int[_parts.Length];
            var count = 0;
            for (var index = 0; index < _parts.Length; index++)
            {
                count = checked(count + _parts[index].Length);
                _partEnds[index] = count;
            }
        }

        public int Count => _partEnds.Length == 0 ? 0 : _partEnds[^1];

        public string this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                var partIndex = Array.BinarySearch(_partEnds, index + 1);
                if (partIndex < 0) partIndex = ~partIndex;
                var partStart = partIndex == 0 ? 0 : _partEnds[partIndex - 1];
                return _parts[partIndex][index - partStart];
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            foreach (var part in _parts)
            {
                foreach (var segmentId in part)
                    yield return segmentId;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// How an urgent (streaming-triggered) repair should proceed given the streaming-failure threshold.
    /// </summary>
    public enum UrgentRepairDisposition
    {
        /// <summary>Call Repair() without forcing deletion.</summary>
        RepairNormally,
        /// <summary>Clear the urgent flag and wait for more streaming failures.</summary>
        Defer,
        /// <summary>Force the repair-delete path even for library-linked items.</summary>
        ForceDelete,
        /// <summary>Force deletion only if the fresh repair-boundary lookup finds no library link.</summary>
        ForceDeleteIfUnlinked,
    }

    /// <summary>
    /// Decides urgent-repair disposition from failure count and auto-remove config.
    /// Threshold 0 preserves immediate repair; otherwise all streaming-triggered actions wait
    /// until the configured number of consecutive failures.
    /// </summary>
    public static UrgentRepairDisposition GetUrgentRepairDisposition(
        int threshold,
        int failureCount,
        bool autoRemoveUnlinkedOnly)
    {
        if (threshold <= 0)
            return UrgentRepairDisposition.RepairNormally;

        if (failureCount < threshold)
            return UrgentRepairDisposition.Defer;

        return autoRemoveUnlinkedOnly
            ? UrgentRepairDisposition.ForceDeleteIfUnlinked
            : UrgentRepairDisposition.ForceDelete;
    }

    /// <summary>
    /// How repair should treat the result of the organized-library lookup.
    /// </summary>
    internal enum LibraryLinkRepairDisposition
    {
        RepairLinked,
        DeferMissingLink,
        ForceDelete,
    }

    internal static LibraryLinkRepairDisposition GetLibraryLinkRepairDisposition(
        string? symlinkOrStrmPath,
        bool forceDelete)
    {
        if (forceDelete)
            return LibraryLinkRepairDisposition.ForceDelete;

        return symlinkOrStrmPath == null
            ? LibraryLinkRepairDisposition.DeferMissingLink
            : LibraryLinkRepairDisposition.RepairLinked;
    }

    /// <summary>
    /// True when the linked library path has already had <see cref="RepairRecurrenceLimit"/>
    /// downloads removed by repair within <see cref="RepairRecurrenceWindow"/>. The path is the
    /// stable identity across replacement cycles: each re-grab creates a new DavItem, but Arr
    /// imports it to the same library location.
    /// </summary>
    internal static bool IsRepairRateLimited(string linkedPath, DateTimeOffset utcNow)
    {
        if (!_recentRepairRemovalsByPath.TryGetValue(linkedPath, out var removals))
            return false;
        lock (removals)
        {
            removals.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
            return removals.Count >= RepairRecurrenceLimit;
        }
    }

    internal static void RecordRepairRemoval(string linkedPath, DateTimeOffset utcNow)
    {
        var removals = _recentRepairRemovalsByPath.GetOrAdd(linkedPath, _ => []);
        lock (removals)
        {
            removals.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
            removals.Add(utcNow);
        }

        if (_recentRepairRemovalsByPath.Count <= MaximumTrackedRepairPaths)
            return;

        // Best-effort prune of fully stale paths; a concurrent add racing a TryRemove can at
        // worst drop one timestamp, which only makes the rate limiter marginally more lenient.
        foreach (var entry in _recentRepairRemovalsByPath)
        {
            bool stale;
            lock (entry.Value)
            {
                entry.Value.RemoveAll(x => utcNow - x >= RepairRecurrenceWindow);
                stale = entry.Value.Count == 0;
            }

            if (stale)
                _recentRepairRemovalsByPath.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Outcome of consulting Arr instances for a library-linked unhealthy item.
    /// </summary>
    public enum ArrLinkedRepairDecision
    {
        /// <summary>An Arr instance owned the file and remove-and-blocklist succeeded.</summary>
        RemoveAndBlocklistSucceeded,
        /// <summary>
        /// An Arr instance owned the file and remove-and-blocklist succeeded, but the
        /// replacement search was withheld by the per-media search budget.
        /// </summary>
        RemoveAndBlocklistSucceededSearchWithheld,
        /// <summary>
        /// At least one Arr instance was unreachable/unusable and no instance completed repair —
        /// leave the DavItem in place.
        /// </summary>
        DeferUnreachable,
        /// <summary>
        /// An exact Arr media item was found, but no unique original download ID could be proven.
        /// </summary>
        DeferMissingDownloadIdentity,
        /// <summary>
        /// Multiple import-history download IDs matched the exact media file and path.
        /// </summary>
        DeferAmbiguousDownloadIdentity,
        /// <summary>
        /// The Arr media item exists and a specific download ID is known, but Arr has no grabbed
        /// history for that ID. Leave it in place rather than searching again without blocklisting.
        /// </summary>
        DeferMissingDownloadHistory,
        /// <summary>
        /// At least one Arr root folder contained the organized path, but no instance returned an
        /// exact media-file match. Leave the organized link in place.
        /// </summary>
        DeferNoMatchingMediaItem,
        /// <summary>
        /// Arr was reachable, but no reported root folder contains the organized path seen by
        /// InfiniDysk. The containers may expose the same host directory under different paths.
        /// </summary>
        DeferRootPathMismatch,
    }

    internal sealed record ArrLinkedRepairResult(
        ArrLinkedRepairDecision Decision,
        Guid? RecoveredDownloadId = null,
        string? RecoveryHost = null);

    /// <summary>
    /// True when <paramref name="candidatePath"/> is the same as <paramref name="rootPath"/> or a
    /// child of it at a directory boundary. Arr paths are compared as opaque strings: no filesystem
    /// resolution, separator rewriting, or case folding.
    /// </summary>
    internal static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(rootPath))
            return false;

        var normalizedRoot = rootPath.Length > 1
            ? rootPath.TrimEnd('/', '\\')
            : rootPath;

        // Fail closed on degenerate roots such as "//" that trim to nothing;
        // an empty prefix would otherwise match every path.
        if (normalizedRoot.Length == 0)
            return false;

        if (string.Equals(candidatePath, normalizedRoot, StringComparison.Ordinal))
            return true;

        if (!candidatePath.StartsWith(normalizedRoot, StringComparison.Ordinal))
            return false;

        if (normalizedRoot is "/" or "\\")
            return true;

        return candidatePath.Length > normalizedRoot.Length &&
               candidatePath[normalizedRoot.Length] is '/' or '\\';
    }

    /// <summary>
    /// Consults Arr clients to decide whether a library-linked unhealthy item should trigger
    /// remove-and-blocklist or be deferred when ownership cannot be confirmed safely.
    /// Extracted so the unreachable-instance fail-safe can be unit-tested without a full Repair harness.
    /// </summary>
    internal static async Task<ArrLinkedRepairResult> DecideArrLinkedRepairAsync(
        IEnumerable<ArrClient> arrClients,
        string symlinkOrStrmPath,
        Guid? downloadId,
        CancellationToken ct,
        Func<ArrClient, IReadOnlyList<string>, bool>? shouldRequestSearch = null,
        ArrInstanceBackoff? arrBackoff = null)
    {
        var anInstanceFailed = false;
        var sawConfiguredClient = false;
        var matchedArrRootFolder = false;
        var sawAmbiguousIdentity = false;
        var sawMissingIdentity = false;
        var sawMissingDownloadHistory = false;
        Guid? recoveredDownloadId = null;
        string? recoveryHost = null;
        var recoveredConflict = false;

        foreach (var arrClient in arrClients)
        {
            sawConfiguredClient = true;
            ct.ThrowIfCancellationRequested();

            // Skip the root-folder query for an instance that is timing out or refusing
            // connections — it cannot answer, and asking only adds load to a dying peer.
            // Treat it like an unreachable instance so repair defers rather than deletes.
            if (arrBackoff is not null && arrBackoff.IsInBackoff(arrClient.Host))
            {
                anInstanceFailed = true;
                Log.Debug(
                    "Health-check repair: skipping root-folder query for {Host}; instance is in backoff for {Remaining}",
                    arrClient.Host,
                    arrBackoff.GetRemainingBackoff(arrClient.Host));
                continue;
            }

            List<ArrRootFolder> rootFolders;
            try
            {
                rootFolders = await arrClient.GetRootFolders(ct).ConfigureAwait(false);
                arrBackoff?.RecordSuccess(arrClient.Host);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                anInstanceFailed = true;
                arrBackoff?.RecordFailure(arrClient.Host, e);
                LogArrRepairFailure(
                    e,
                    "Health-check repair: could not query root folders from {Host}",
                    arrClient.Host);
                continue;
            }

            var hasMatchingRoot = rootFolders.Any(root =>
                !string.IsNullOrEmpty(root.Path) &&
                IsPathWithinRoot(symlinkOrStrmPath, root.Path));

            if (!hasMatchingRoot)
            {
                // A null/empty root path is a malformed response: this instance can be
                // neither ruled in nor ruled out, so it must defer as unreachable rather
                // than count toward a path-mismatch diagnosis.
                if (rootFolders.Any(x => string.IsNullOrEmpty(x.Path)))
                    anInstanceFailed = true;
                continue;
            }

            matchedArrRootFolder = true;

            ArrMediaFileMatch? mediaFile;
            try
            {
                mediaFile = await arrClient.FindMediaFileAsync(symlinkOrStrmPath, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                anInstanceFailed = true;
                LogArrRepairFailure(
                    e,
                    "Health-check repair: could not look up the Arr media file on {Host}",
                    arrClient.Host);
                continue;
            }

            if (mediaFile is null)
                continue;

            var effectiveId = downloadId;
            if (effectiveId is null)
            {
                ArrClient.ArrImportHistoryCollection collected;
                try
                {
                    collected = await arrClient.CollectMediaImportHistoryAsync(mediaFile, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    anInstanceFailed = true;
                    LogArrRepairFailure(
                        e,
                        "Health-check repair: could not query Arr import history on {Host}",
                        arrClient.Host);
                    continue;
                }

                var resolution = ArrDownloadIdResolver.Resolve(
                    collected.Records, mediaFile, symlinkOrStrmPath);
                if (resolution.Kind == ArrDownloadIdResolutionKind.Unique && !collected.Exhausted)
                    resolution = new ArrDownloadIdResolution(ArrDownloadIdResolutionKind.NotFound);

                if (resolution.Kind == ArrDownloadIdResolutionKind.Unique)
                {
                    effectiveId = resolution.DownloadId;
                    if (recoveredDownloadId is null)
                    {
                        recoveredDownloadId = effectiveId;
                        recoveryHost = arrClient.Host;
                    }
                    else if (recoveredDownloadId != effectiveId)
                    {
                        recoveredConflict = true;
                    }
                }
                else if (resolution.Kind == ArrDownloadIdResolutionKind.Ambiguous)
                {
                    sawAmbiguousIdentity = true;
                    continue;
                }
                else
                {
                    sawMissingIdentity = true;
                    continue;
                }
            }

            if (effectiveId is null)
            {
                sawMissingIdentity = true;
                continue;
            }

            ArrRepairOutcome repairOutcome;
            try
            {
                repairOutcome = await arrClient.RemoveAndBlocklist(
                    mediaFile,
                    effectiveId.Value,
                    shouldRequestSearch is null ? null : identity => shouldRequestSearch(arrClient, identity),
                    ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                anInstanceFailed = true;
                LogArrRepairFailure(
                    e,
                    "Health-check repair: remove-and-blocklist failed on {Host}",
                    arrClient.Host);
                continue;
            }

            var persistedRecovery = recoveredConflict ? null : recoveredDownloadId;
            if (repairOutcome == ArrRepairOutcome.RemoveAndBlocklistSucceeded)
            {
                return new ArrLinkedRepairResult(
                    ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded,
                    persistedRecovery,
                    recoveryHost);
            }

            if (repairOutcome == ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld)
            {
                return new ArrLinkedRepairResult(
                    ArrLinkedRepairDecision.RemoveAndBlocklistSucceededSearchWithheld,
                    persistedRecovery,
                    recoveryHost);
            }

            if (repairOutcome == ArrRepairOutcome.DownloadHistoryNotFound)
            {
                sawMissingDownloadHistory = true;
                continue;
            }
        }

        var recovered = recoveredConflict || sawAmbiguousIdentity
            ? null
            : recoveredDownloadId;
        if (anInstanceFailed)
        {
            return new ArrLinkedRepairResult(
                ArrLinkedRepairDecision.DeferUnreachable, recovered, recoveryHost);
        }

        if (sawAmbiguousIdentity)
        {
            return new ArrLinkedRepairResult(
                ArrLinkedRepairDecision.DeferAmbiguousDownloadIdentity, recovered, recoveryHost);
        }

        if (sawMissingIdentity)
        {
            return new ArrLinkedRepairResult(
                ArrLinkedRepairDecision.DeferMissingDownloadIdentity, recovered, recoveryHost);
        }

        if (sawMissingDownloadHistory)
        {
            return new ArrLinkedRepairResult(
                ArrLinkedRepairDecision.DeferMissingDownloadHistory, recovered, recoveryHost);
        }

        if (sawConfiguredClient && !matchedArrRootFolder)
            return new ArrLinkedRepairResult(ArrLinkedRepairDecision.DeferRootPathMismatch);

        return new ArrLinkedRepairResult(ArrLinkedRepairDecision.DeferNoMatchingMediaItem);
    }

    private static void LogArrRepairFailure(Exception exception, string messageTemplate, string host)
    {
        if (exception.TryGetCausingException<HttpRequestException>(out var httpException) &&
            httpException is not null)
        {
            var reason = httpException.StatusCode is { } statusCode
                ? $"HTTP {(int)statusCode} ({statusCode})"
                : httpException.Message;
            Log.Warning(messageTemplate + ". Reason: {Reason}", host, reason);
            Log.Debug(exception, "Health-check repair Arr HTTP failure stack");
            return;
        }

        if (exception.TryGetKnownErrorMessage(out var knownReason))
        {
            Log.Warning(messageTemplate + ". Reason: {Reason}", host, knownReason);
            Log.Debug(exception, "Health-check repair Arr known failure stack");
            return;
        }

        Log.Warning(exception, messageTemplate, host);
    }

    private async Task HandleUrgentRepair(
        DavItem davItem,
        DavDatabaseClient dbClient,
        bool repairsAdmitted,
        CancellationToken ct)
    {
        if (!repairsAdmitted)
        {
            await DeferRepairUntilWindow(davItem, dbClient, ct).ConfigureAwait(false);
            return;
        }

        var threshold = _configManager.GetAutoRemoveAfterFailures();
        var failureSnapshot = _failureTracker.GetSnapshot(davItem.Id);
        var failureCount = failureSnapshot.Count;
        var unlinkedOnly = _configManager.IsAutoRemoveUnlinkedOnly();
        var disposition = GetUrgentRepairDisposition(threshold, failureCount, unlinkedOnly);

        if (disposition == UrgentRepairDisposition.Defer)
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromHours(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    "File failed during streaming.",
                    $"Streaming failure count: {failureCount}/{threshold}.",
                    "Repair and replacement deferred until the failure threshold is reached."
                ]), ct).ConfigureAwait(false);
            return;
        }

        var par2Outcome = ShouldAttemptPar2Repair()
            ? await _par2RepairService.TryPar2RepairAsync(
                davItem,
                failureSnapshot.HasTargetableSegmentIds ? failureSnapshot.SegmentIds : null,
                ct).ConfigureAwait(false)
            : Par2RepairOutcome.NotRepaired;
        if (par2Outcome is not Par2RepairOutcome.NotRepaired)
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                par2Outcome is Par2RepairOutcome.Repaired
                    ? HealthCheckResult.RepairAction.RepairedViaPar2
                    : HealthCheckResult.RepairAction.None,
                par2Outcome is Par2RepairOutcome.Repaired
                    ? "Missing segment(s) repaired from PAR2 parity after streaming failure."
                    : "PAR2 verified every file slice and found no damage.",
                ct)
                .ConfigureAwait(false);
            return;
        }

        await Repair(
            davItem,
            dbClient,
            ct,
            forceDelete: disposition == UrgentRepairDisposition.ForceDelete,
            forceDeleteIfUnlinked: disposition == UrgentRepairDisposition.ForceDeleteIfUnlinked,
            streamingFailureCount: failureCount).ConfigureAwait(false);
    }

    private bool ShouldAttemptPar2Repair()
    {
        if (!_configManager.IsPar2RepairEnabled())
            return false;

        return _configManager.IsPar2PreferredOverArr()
               || !_configManager.GetArrConfig().GetArrClients().Any();
    }

    /// <summary>
    /// Removes a dav-item together with the strm sidecar generated for it.
    /// The deleter verifies on-disk ownership before deleting and no-ops when the item
    /// has no generated output. A filesystem failure must not block the repair.
    /// </summary>
    internal static void RemoveDavItemWithGeneratedSidecars(DavDatabaseClient dbClient, DavItem davItem)
    {
        try
        {
            CreateStrmFilesPostProcessor.DeleteStrmFile(davItem);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(
                e,
                "Could not remove the generated strm sidecar for {Path} during health repair. The webdav item is still being removed; the sidecar file may need manual cleanup.",
                davItem.Path);
        }

        dbClient.Ctx.Items.Remove(davItem);
    }

    private async Task Repair(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct,
        bool forceDelete = false,
        bool forceDeleteIfUnlinked = false,
        int? streamingFailureCount = null)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "health validation failed; filename matches blocklist pattern");
                RemoveDavItemWithGeneratedSidecars(dbClient, davItem);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    string.Join(" ", [
                        "File failed health validation.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // A missing library link is not proof that the item is orphaned: a FUSE mount can
            // temporarily present a successfully empty or partial view. Only an explicit
            // force-delete policy may remove the item from this branch.
            var libraryDir = _configManager.GetLibraryDir();
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            var linkDisposition = GetLibraryLinkRepairDisposition(
                symlinkOrStrmPath,
                forceDelete || (forceDeleteIfUnlinked && symlinkOrStrmPath == null));
            if (linkDisposition == LibraryLinkRepairDisposition.ForceDelete)
            {
                if (symlinkOrStrmPath != null)
                    await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);

                var auditReason = streamingFailureCount is > 0
                    ? $"auto-removed after repeated streaming failures (count={streamingFailureCount})"
                    : "auto-removed after repeated streaming failures";
                DeletionAuditLog.Record("health-repair", davItem, auditReason);

                RemoveDavItemWithGeneratedSidecars(dbClient, davItem);
                _failureTracker.ClearFailure(davItem.Id);

                var failureNote = streamingFailureCount is > 0
                    ? $" Streaming failure count: {streamingFailureCount}."
                    : "";
                string deleteMessage;
                if (symlinkOrStrmPath != null)
                {
                    var forceDeleteLinkType = symlinkOrStrmPath.ToLowerInvariant().EndsWith("strm", StringComparison.Ordinal) ? "strm-file" : "symlink";
                    deleteMessage = string.Join(" ", [
                        "File failed during streaming.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        $"Deleted the webdav-file and {forceDeleteLinkType}."
                    ]);
                }
                else
                {
                    deleteMessage = string.Join(" ", [
                        "File failed during streaming.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        "Deleted file."
                    ]);
                }

                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    deleteMessage, ct).ConfigureAwait(false);
                return;
            }

            if (linkDisposition == LibraryLinkRepairDisposition.DeferMissingLink)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                var missingLinkMessage = libraryDir == null
                    ? string.Join(" ", [
                        "File failed health validation.",
                        "No Library Directory is configured, so an Arr replacement cannot be determined.",
                        "The webdav-file was left in place."
                    ])
                    : string.Join(" ", [
                        "File failed health validation.",
                        "Could not find a corresponding symlink or strm-file within Library Dir.",
                        "The library scan may be incomplete, so the webdav-file was left in place.",
                        "Use Remove Orphaned Files for deliberate unlinked-item cleanup."
                    ]);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    missingLinkMessage, ct).ConfigureAwait(false);
                return;
            }

            var linkedPath = symlinkOrStrmPath!;
            var linkType = linkedPath.ToLowerInvariant().EndsWith("strm", StringComparison.Ordinal) ? "strm-file" : "symlink";

            // Rate-limit repairs per library file: when Arr keeps re-importing broken
            // replacements to the same location, deleting again only fuels the loop.
            if (IsRepairRateLimited(linkedPath, DateTimeOffset.UtcNow))
            {
                Log.Warning(
                    "Health-check repair rate limit reached for library file {LinkedPath}: " +
                    "{RemovalCount} downloads already removed for this file within {WindowHours} hours. " +
                    "Leaving the file in place to break the replacement loop.",
                    linkedPath,
                    RepairRecurrenceLimit,
                    RepairRecurrenceWindow.TotalHours);
                var deferUntil = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = deferUntil;
                davItem.NextHealthCheck = deferUntil + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Repair already removed {RepairRecurrenceLimit} downloads for this library file",
                        $"within the last {RepairRecurrenceWindow.TotalHours:0} hours,",
                        "which indicates a replacement loop.",
                        "Leaving the file in place rather than triggering another replacement."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            // The per-path rate limit above misses alternate releases (each import gets a
            // new filename), so replacement searches are additionally budgeted by the Arr
            // media identity, which stays stable across re-grabs of the same movie/episode.
            var arrConfig = _configManager.GetArrConfig();
            var arrClients = arrConfig.GetArrClients().ToArray();
            if (arrClients.Length == 0)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "No enabled Radarr/Sonarr instances are configured, so replacement was skipped.",
                        $"The webdav-file and {linkType} were left in place."
                    ]), ct).ConfigureAwait(false);
                return;
            }
            var arrResult = await DecideArrLinkedRepairAsync(
                arrClients,
                linkedPath,
                davItem.ArrDownloadId,
                ct,
                (arrClient, mediaIdentities) => _replacementSearchBudget.TryReserveAll(
                    mediaIdentities
                        .Select(identity => $"{arrClient.Host.TrimEnd('/').ToLowerInvariant()}|{identity}")
                        .ToArray(),
                    arrConfig.EffectiveQueueReplacementSearchLimit(),
                    arrConfig.EffectiveQueueReplacementSearchWindow()),
                _arrBackoff).ConfigureAwait(false);

            if (davItem.ArrDownloadId is null && arrResult.RecoveredDownloadId is { } recovered)
            {
                davItem.ArrDownloadId = recovered;
                Log.Information(
                    "Recovered Arr download provenance for {Path} from exact import history on {Host}.",
                    davItem.Path,
                    arrResult.RecoveryHost);
            }

            var arrDecision = arrResult.Decision;

            if (arrDecision is ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded
                or ArrLinkedRepairDecision.RemoveAndBlocklistSucceededSearchWithheld)
            {
                RecordRepairRemoval(linkedPath, DateTimeOffset.UtcNow);
                await SeedRejectedReleaseSegmentsAsync(davItem, dbClient, ct).ConfigureAwait(false);
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "health validation failed; Arr media removed and original download blocklisted");
                RemoveDavItemWithGeneratedSidecars(dbClient, davItem);
                _failureTracker.ClearFailure(davItem.Id);
                var searchClause = arrDecision is ArrLinkedRepairDecision.RemoveAndBlocklistSucceededSearchWithheld
                    ? "The automatic replacement search was withheld because the per-media search limit was reached."
                    : "Arr was notified to search for a replacement.";
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Repaired,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "Removed the Arr media file and blocklisted its original download.",
                        searchClause
                    ]), ct).ConfigureAwait(false);
                return;
            }

            async Task DeferMissingProvenanceAsync(string reason, string messageTail)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                Log.Warning(
                    "Health-check repair deferred for {Path}: no unique Arr download provenance could be proven. Reason: {Reason}",
                    davItem.Path,
                    reason);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} and Arr media item found,",
                        messageTail,
                    ]), ct).ConfigureAwait(false);
            }

            if (arrDecision == ArrLinkedRepairDecision.DeferMissingDownloadIdentity)
            {
                await DeferMissingProvenanceAsync(
                    "exact Arr media item found, but no unique original download ID could be proven",
                    "but no unique original Arr download ID could be proven. " +
                    "Leaving the file in place rather than searching again without blocklisting the failed release.")
                    .ConfigureAwait(false);
                return;
            }

            if (arrDecision == ArrLinkedRepairDecision.DeferAmbiguousDownloadIdentity)
            {
                await DeferMissingProvenanceAsync(
                    "multiple import-history download IDs matched",
                    "but multiple Arr import-history download IDs matched. " +
                    "Leaving the file in place rather than guessing which release to blocklist.")
                    .ConfigureAwait(false);
                return;
            }

            if (arrDecision == ArrLinkedRepairDecision.DeferMissingDownloadHistory)
            {
                await DeferMissingProvenanceAsync(
                    "exact media item and download ID found, but no grabbed history record exists to blocklist",
                    "but the original Arr download history could not be identified. " +
                    "Leaving the file in place rather than searching again without blocklisting the failed release.")
                    .ConfigureAwait(false);
                return;
            }

            // Ownership indeterminate (an instance could not be reached or fully queried, and no
            // instance confirmed the file as an orphan): don't delete a link it may own. Defer like
            // the catch below so the item isn't re-selected every scan cycle while the instance is down.
            if (arrDecision == ArrLinkedRepairDecision.DeferUnreachable)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir,",
                        "but at least one Arr instance could not be reached or fully queried, so ownership",
                        "of the file could not be determined. Leaving the file in place rather than deleting it."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            if (arrDecision == ArrLinkedRepairDecision.DeferRootPathMismatch)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File failed health validation.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "No enabled Radarr/Sonarr root folder contains the library path reported by InfiniDysk.",
                        "The containers may expose the same host library under different absolute paths; configure InfiniDysk and Arr to use the same absolute container path.",
                        $"The webdav-file and {linkType} were left in place, and replacement search was skipped because the Arr media item could not be identified safely."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // A containing Arr root existed, but no instance confirmed an exact media-file match.
            // That miss is inconclusive (path mapping, stale Arr data, or a genuine orphan) and
            // must never authorize deletion of a still-linked library file.
            var noMatchUtcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = noMatchUtcNow;
            davItem.NextHealthCheck = noMatchUtcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    "File failed health validation.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "An Arr root folder contains the library path, but no configured Radarr/Sonarr instance confirmed an exact matching media item.",
                    $"The webdav-file and {linkType} were left in place because an inconclusive lookup cannot authorize automatic deletion."
                ]), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Error performing file repair: {e.Message}", ct).ConfigureAwait(false);
        }
    }


    private async Task DeferRepairUntilWindow(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var admission = _healthWorkSchedule?.Evaluate(now);
        var scheduledResume = admission?.NextRepairsChange ?? now + TimeSpan.FromDays(1);
        davItem.HealthRepairPending = true;
        davItem.LastHealthCheck = now;
        // Keep the UnixEpoch urgent sentinel. Replacing it with the window time would
        // send the item through STAT on resume, and STAT can pass after BODY failed.
        davItem.NextHealthCheck = NextHealthCheckAfterScheduleDeferral(
            davItem.NextHealthCheck, scheduledResume);
        Log.Warning(
            "Repair deferred by schedule for {Path}. Next repair window opens {NextChange}.",
            davItem.Path,
            scheduledResume);
        await RecordHealthResult(
            dbClient,
            davItem,
            HealthCheckResult.HealthResult.Unhealthy,
            HealthCheckResult.RepairAction.ActionNeeded,
            $"Repair deferred by the repair schedule. Next repair window opens {scheduledResume:u}.",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Playback-triggered urgent repairs use <see cref="DateTimeOffset.UnixEpoch"/> so the
    /// next admission skips STAT. Schedule deferral must not replace that sentinel.
    /// </summary>
    internal static DateTimeOffset NextHealthCheckAfterScheduleDeferral(
        DateTimeOffset? currentNextHealthCheck,
        DateTimeOffset scheduledResume) =>
        currentNextHealthCheck == DateTimeOffset.UnixEpoch
            ? DateTimeOffset.UnixEpoch
            : scheduledResume;

    private async Task RecordHealthResult
    (
        DavDatabaseClient dbClient,
        DavItem davItem,
        HealthCheckResult.HealthResult result,
        HealthCheckResult.RepairAction repairStatus,
        string message,
        CancellationToken ct
    )
    {
        if (result is HealthCheckResult.HealthResult.Healthy
            || repairStatus is HealthCheckResult.RepairAction.Repaired
                or HealthCheckResult.RepairAction.Deleted
                or HealthCheckResult.RepairAction.RepairedViaPar2)
        {
            davItem.HealthRepairPending = false;
        }

        var identity = repairStatus is HealthCheckResult.RepairAction.Deleted or HealthCheckResult.RepairAction.Repaired
            ? await GetRepairHistoryIdentityAsync(dbClient.Ctx, davItem, ct).ConfigureAwait(false)
            : null;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            NzbFileName = identity?.NzbFileName,
            JobName = identity?.JobName,
            CreatedAt = DateTimeOffset.UtcNow,
            Result = result,
            RepairStatus = repairStatus,
            Message = message
        }));
        try
        {
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException e)
        {
            Log.Warning(
                "Health check result not recorded because the file was deleted while the check was running. Path: {Path}",
                davItem.Path);
            Log.Debug(e, "Health check concurrency stack for {Path}", davItem.Path);
            foreach (var entry in e.Entries)
                entry.State = EntityState.Detached;
        }
    }

    internal static async Task<RepairHistoryIdentity?> GetRepairHistoryIdentityAsync
    (
        DavDatabaseContext context,
        DavItem davItem,
        CancellationToken ct
    )
    {
        if (davItem.NzbBlobId is not Guid nzbBlobId) return null;

        var nzbFileName = await context.NzbNames
            .AsNoTracking()
            .Where(x => x.Id == nzbBlobId)
            .Select(x => x.FileName)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (nzbFileName is null) return null;

        return new RepairHistoryIdentity(nzbFileName, FilenameUtil.GetJobName(nzbFileName));
    }

    internal sealed record RepairHistoryIdentity(string NzbFileName, string JobName);

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    /// <summary>
    /// Seeds the fail-fast cache with the segment ids of a release that repair just rejected
    /// via remove-and-blocklist. The cache semantically holds "segments of rejected releases"
    /// here: a corrupt-archive or truncated-stream rejection can have every article present,
    /// but a re-grab of the same release carries identical message-ids, so failing it at the
    /// queue step-0 precheck — pre-import, where Arr blocklisting works — is the intended
    /// outcome regardless of which failure type triggered the repair (issue #732).
    /// </summary>
    private async Task SeedRejectedReleaseSegmentsAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        try
        {
            var payload = await LoadHealthCheckPayloadAsync(davItem, dbClient, ct).ConfigureAwait(false);
            AddMissingSegmentIds(EnumerateRejectedReleaseSeedSegments(payload.Segments));
        }
        catch (OutOfMemoryException oom)
        {
            // Cache seeding is an optimization after Arr has already rejected the release.
            // Do not let a huge payload turn that completed repair into a process-fatal OOM.
            OomDiagnostics.LogHeapStateOnOom(oom, "rejected-release cache seeding");
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            // A missing blob must not abort the repair; the release is already
            // blocklisted in Arr, we only lose the pre-import fail-fast.
            Log.Warning(
                "Could not seed the fail-fast cache with segments of rejected release {Path}. " +
                "A re-grab of the same release may import once more before failing. Reason: {Reason}",
                davItem.Path, e.Message);
            Log.Debug(e, "Rejected-release cache seeding failure stack for {Path}", davItem.Path);
        }
    }

    /// <summary>
    /// Selects the bounded prefix of a rejected release's segment ids to seed into the cache.
    /// </summary>
    internal static List<string> SelectRejectedReleaseSeedSegments(List<string> segments) =>
        segments.Count <= RejectedReleaseSeedSegments
            ? segments
            : segments.Take(RejectedReleaseSeedSegments).ToList();

    private static IEnumerable<string> EnumerateRejectedReleaseSeedSegments(IReadOnlyList<string> segments)
    {
        for (var index = 0; index < Math.Min(segments.Count, RejectedReleaseSeedSegments); index++)
            yield return segments[index];
    }

    public static void AddMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
            {
                if (_missingSegmentIds.Add(segmentId))
                    _missingSegmentOrder.Enqueue(segmentId);
                while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                    _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
            }
        }
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds.Where(segmentId => _missingSegmentIds.Contains(segmentId)))
                throw new UsenetArticleNotFoundException(segmentId);
        }
    }
}
