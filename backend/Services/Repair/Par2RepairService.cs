using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Services.Observability;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services.Repair;

public enum Par2RepairOutcome
{
    NotRepaired = 0,
    Repaired = 1,
    VerifiedClean = 2,
    DeferredBusy = 3,
}

public partial class Par2RepairService : BackgroundService
{
    private enum RepairAdmissionMode
    {
        InlineTryOnce,
        QueuedWait,
    }

    private const int MaxQueueLength = 50;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan CatalogWarningInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CatalogMaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly ConfigManager _configManager;
    private readonly UsenetStreamingClient _usenetClient;
    private readonly RepairPatchStore _patchStore;
    private readonly IDbContextFactory<DavDatabaseContext>? _dbContextFactory;
    private readonly LogThrottle _catalogWarningThrottle = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Channel<RepairWorkItem> _queue;
    private readonly Channel<ZeroFillEvent> _zeroFillQueue;
    private readonly ConcurrentDictionary<Guid, byte> _queuedOrRunning = new();
    private readonly ConcurrentDictionary<Guid, RepairFlight> _repairFlights = new();
    private readonly SemaphoreSlim _repairAdmission = new(1, 1);
    private readonly Lock _admissionLifecycle = new();
    private int _admissionUsers;
    private bool _disposeRequested;
    private int _admissionActive;
    private int _admissionWaiters;
    private long _totalAdmissionWaitTicks;
    private long _latestAdmissionWaitTicks;
    private readonly ConcurrentDictionary<string, byte> _pendingZeroFillPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(string Id, bool IsCorruption)>> _pendingSegmentIds =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RetainedRepair> _retainedSegmentIds = new();
    private long _totalSucceeded;
    private long _totalFailed;
    private long _totalInfeasible;
    private long _totalBytesRead;
    private long _totalSlicesReconstructed;
    private long _totalSegmentsCommitted;
    private string? _activeRepairPath;
    private string? _activeRepairPhase;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Non-owning diagnostic reference; ExecuteRepairJobAsync disposes its accessor in finally.")]
    private ResolvedSliceAccessor? _activeSource;
    private long _activeBytesRead;
    private long _activeEstimatedWorkingSetBytes;
    private long _activeMemoryCapBytes;

    public Par2RepairService(
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        RepairPatchStore patchStore,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null)
        : this(configManager, usenetClient, patchStore, dbContextFactory, static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal Par2RepairService(
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        RepairPatchStore patchStore,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _patchStore = patchStore;
        _dbContextFactory = dbContextFactory;
        _delayAsync = delayAsync;
        // Wait mode makes non-blocking TryWrite report full queues as false so
        // callers can undo bookkeeping; DropWrite would return true and silently
        // discard the item, leaking _queuedOrRunning/_pendingZeroFillPaths entries.
        _queue = Channel.CreateBounded<RepairWorkItem>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _zeroFillQueue = Channel.CreateBounded<ZeroFillEvent>(new BoundedChannelOptions(MaxQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    private DavDatabaseContext CreateContext() =>
        _dbContextFactory?.CreateDbContext() ?? new DavDatabaseContext();

    internal Action? OnWorkersStarting { get; set; }
    internal Func<CancellationToken, Task>? BeforePatchPublicationForTests { get; set; }

    internal int PendingZeroFillCount => _pendingZeroFillPaths.Count;

    internal bool HasPendingZeroFillPath(string path) =>
        _pendingZeroFillPaths.ContainsKey(path);

    internal string[] PeekRetainedSegmentIdsForTests(Guid davItemId) =>
        _retainedSegmentIds.TryGetValue(davItemId, out var retained)
            ? retained.Ids.Keys.ToArray()
            : [];

    internal void ReleaseQueuedOrRunningForTests(Guid davItemId)
    {
        _queuedOrRunning.TryRemove(davItemId, out _);
        if (_repairFlights.TryRemove(davItemId, out var flight))
        {
            flight.Completion.TrySetResult(Par2RepairOutcome.NotRepaired);
            flight.Finished.TrySetResult();
        }
    }

    internal Task RequeueRetainedForTestsAsync(Guid davItemId, CancellationToken ct) =>
        TryRequeueRetainedAsync(davItemId, ct);

    /// <summary>
    /// Synchronous, allocation-light entry point for streaming zero-fill events.
    /// Runs on the playback hot path's failure branch: gate on config, accumulate
    /// segment IDs per path, and arm at most one background item per path.
    /// </summary>
    public void ReportZeroFill(string path, string segmentId)
    {
        if (!_configManager.IsPar2RepairEnabled() && !_configManager.IsDegradedToleranceEnabled())
            return;
        AccumulateAndArm(path, segmentId, isCorruption: false);
    }

    public void ReportCorruption(string path, string segmentId)
    {
        if (!_configManager.IsCorruptionTrackingEnabled()) return;
        AccumulateAndArm(path, segmentId, isCorruption: true);
    }

    private void AccumulateAndArm(string path, string segmentId, bool isCorruption)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(segmentId))
            return;

        var ids = _pendingSegmentIds.GetOrAdd(
            path,
            static _ => new ConcurrentQueue<(string Id, bool IsCorruption)>());
        ids.Enqueue((segmentId, isCorruption));
        if (!_pendingZeroFillPaths.TryAdd(path, 0))
            return;
        if (_zeroFillQueue.Writer.TryWrite(new ZeroFillEvent(path, segmentId, isCorruption)))
            return;

        _pendingZeroFillPaths.TryRemove(path, out _);
        _pendingSegmentIds.TryRemove(path, out _);
    }

    public virtual async Task EnqueueAsync(
        DavItem davItem,
        IReadOnlyList<string> missingSegmentIds,
        CancellationToken ct = default)
    {
        if (!_configManager.IsPar2RepairEnabled()) return;
        RetainSegmentIds(davItem.Id, davItem.Path, missingSegmentIds);
        if (!await ShouldEnqueueAsync(davItem.Id, ct).ConfigureAwait(false))
            return;

        if (!_queuedOrRunning.TryAdd(davItem.Id, 0))
            return;

        var ids = DrainRetainedSegmentIds(davItem.Id);
        if (ids.Length == 0)
        {
            _queuedOrRunning.TryRemove(davItem.Id, out _);
            return;
        }

        var flight = new RepairFlight(ids);
        if (!_repairFlights.TryAdd(davItem.Id, flight))
        {
            RetainSegmentIds(davItem.Id, davItem.Path, ids);
            _queuedOrRunning.TryRemove(davItem.Id, out _);
            return;
        }

        var item = new RepairWorkItem(
            davItem.Id,
            davItem.Path,
            ids,
            flight);
        if (!_queue.Writer.TryWrite(item))
        {
            RetainSegmentIds(davItem.Id, davItem.Path, ids);
            _queuedOrRunning.TryRemove(davItem.Id, out _);
            CompleteFlight(davItem.Id, flight, Par2RepairOutcome.NotRepaired);
            Log.Warning(
                "PAR2 repair queue full ({Capacity}); dropping repair request for {Path}",
                MaxQueueLength, davItem.Path);
            PrometheusMetrics.Current?.RecordPar2RepairJob("dropped");
            return;
        }

        PrometheusMetrics.Current?.RecordPar2RepairJob("queued");
    }

    /// <summary>
    /// Attempts PAR2 repair synchronously for health-check and urgent paths.
    /// Returns the outcome of the repair or verification attempt.
    /// Virtual so health-check classification tests can script the outcome.
    /// </summary>
    public virtual async Task<Par2RepairOutcome> TryPar2RepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        CancellationToken ct)
    {
        if (!_configManager.IsPar2RepairEnabled())
            return Par2RepairOutcome.NotRepaired;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var mine = new RepairFlight(missingSegmentIds);
            var flight = _repairFlights.GetOrAdd(davItem.Id, mine);
            if (ReferenceEquals(flight, mine))
                return await RunFlightAsync(flight, davItem, missingSegmentIds, queueGuard: false, RepairAdmissionMode.InlineTryOnce, ct).ConfigureAwait(false);
            if (!flight.Task.IsCompleted)
                return Par2RepairOutcome.DeferredBusy;
            try
            {
                var result = await flight.Task.WaitAsync(ct).ConfigureAwait(false);
                if (result == Par2RepairOutcome.DeferredBusy)
                    return result;
                if (result == Par2RepairOutcome.NotRepaired || flight.Covers(missingSegmentIds)
                    || missingSegmentIds is { Count: > 0 } && missingSegmentIds.All(_patchStore.HasUsablePatch))
                    return result;
                if (!flight.Finished.Task.IsCompleted)
                    return Par2RepairOutcome.DeferredBusy;
                _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItem.Id, flight));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Par2RepairOutcome.DeferredBusy;
            }
        }
        Log.Warning("PAR2 repair for {DavItemId} deferred after repeated flight contention.", davItem.Id);
        return Par2RepairOutcome.DeferredBusy;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForPatchCatalogAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await ReconcileInterruptedJobsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException oom)
        {
            OomDiagnostics.LogHeapStateOnOom(oom, "PAR2 interrupted-job reconciliation");
            Log.Warning("PAR2 interrupted-job reconciliation deferred after exhausting managed memory.");
        }
        catch (Exception e)
        {
            e.LogWarningKnownOrStack("PAR2 interrupted-job reconciliation deferred");
        }

        await RunWorkersAsync(stoppingToken).ConfigureAwait(false);
    }

    internal Task RunWorkersAsync(CancellationToken stoppingToken)
    {
        OnWorkersStarting?.Invoke();
        return Task.WhenAll(
            ProcessRepairQueueAsync(stoppingToken),
            ProcessZeroFillQueueAsync(stoppingToken));
    }

    private async Task WaitForPatchCatalogAsync(CancellationToken stoppingToken)
    {
        var failures = 0;

        while (true)
        {
            try
            {
                await _patchStore
                    .EnsureCatalogLoadedAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (failures > 0)
                {
                    _catalogWarningThrottle.Reset("par2-patch-catalog");
                    Log.Information(
                        "PAR2 patch catalog recovered after {FailureCount} failed load attempt(s).",
                        failures);
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsKnownCatalogOperationalException(exception))
            {
                failures++;
                var delay = exception.IsDatabaseCorruptionException()
                    ? BackgroundServiceErrorHandler.CorruptionDelay
                    : GetCatalogRetryDelay(failures);

                LogKnownCatalogFailure(exception, failures, delay);
                await _delayAsync(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan GetCatalogRetryDelay(int failureCount)
    {
        var shift = Math.Min(Math.Max(failureCount - 1, 0), 6);
        var seconds = Math.Min(
            CatalogMaxRetryDelay.TotalSeconds,
            1 << shift);
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsFatalCatalogException(Exception exception)
    {
        return exception.TryGetCausingException<OutOfMemoryException>(out _)
            || exception.TryGetCausingException<StackOverflowException>(out _)
            || exception.TryGetCausingException<AccessViolationException>(out _);
    }

    private static bool IsKnownCatalogOperationalException(Exception exception)
    {
        if (IsFatalCatalogException(exception))
            return false;

        return exception.TryGetCausingException<IOException>(out _)
            || exception.TryGetCausingException<UnauthorizedAccessException>(out _)
            || exception.IsTransientDatabaseException()
            || exception.IsKnownSqliteDiskException()
            || exception.IsDatabaseCorruptionException();
    }

    private void LogKnownCatalogFailure(
        Exception exception,
        int failureCount,
        TimeSpan retryDelay)
    {
        exception.TryGetKnownErrorMessage(out var reason);

        if (!_catalogWarningThrottle.ShouldLog(
                "par2-patch-catalog",
                CatalogWarningInterval,
                out var suppressed))
            return;

        if (suppressed > 0)
        {
            Log.Warning(
                "PAR2 patch catalog load failed on attempt {Attempt}. " +
                "Reason: {Reason} Retrying in {RetryDelay}. " +
                "Suppressed {Suppressed} repeated warning(s).",
                failureCount,
                reason,
                retryDelay,
                suppressed);
            return;
        }

        Log.Warning(
            "PAR2 patch catalog load failed on attempt {Attempt}. " +
            "Reason: {Reason} Retrying in {RetryDelay}.",
            failureCount,
            reason,
            retryDelay);
    }

    private async Task ReconcileInterruptedJobsAsync(CancellationToken ct)
    {
        await using var dbContext = CreateContext();
        var activeJobs = await dbContext.Par2RepairJobs
            .Where(job => job.State == Par2RepairJob.RepairJobState.Queued
                          || job.State == Par2RepairJob.RepairJobState.Running)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (activeJobs.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromHours(_configManager.GetPar2FailureCooldownHours());
        foreach (var job in activeJobs)
        {
            var wasRunning = job.State == Par2RepairJob.RepairJobState.Running;
            job.State = Par2RepairJob.RepairJobState.Failed;
            job.CompletedAt = now;
            job.FailureReason = wasRunning
                ? "PAR2 repair was interrupted by a backend restart."
                : "PAR2 repair was queued when the backend restarted.";
            // A job that had started may have triggered a cgroup kill. Cool it down
            // rather than immediately recreating the same failure loop; a queued job
            // never started and should be eligible for the next trigger immediately.
            job.NextAttemptAt = wasRunning ? now + cooldown : null;
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Warning(
            "Reconciled {Count} PAR2 repair job(s) interrupted by backend restart.",
            activeJobs.Count);
    }

    internal Task ReconcileInterruptedJobsForTestsAsync(CancellationToken ct) =>
        ReconcileInterruptedJobsAsync(ct);

    private async Task ProcessRepairQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessQueueItemAsync(item, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    CancelFlight(item.DavItemId, item.Flight, stoppingToken);
                    throw;
                }
                catch (Exception exception) when (exception is MissingFilePayloadException or CorruptedBlobPayloadException)
                {
                    _queuedOrRunning.TryRemove(item.DavItemId, out _);
                    CompleteFlight(item.DavItemId, item.Flight, Par2RepairOutcome.NotRepaired);
                    Log.Warning("PAR2 background repair cannot read the payload for {Path}. Reason: {Reason}", item.Path, exception.Message);
                    Log.Debug(exception, "PAR2 streaming payload failure for {Path}", item.Path);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _queuedOrRunning.TryRemove(item.DavItemId, out _);
                    CompleteFlight(item.DavItemId, item.Flight, Par2RepairOutcome.NotRepaired);
                    e.LogWarningKnownOrStack("PAR2 background repair worker failed for {Path}", item.Path);
                }
                catch (OutOfMemoryException oom)
                {
                    _queuedOrRunning.TryRemove(item.DavItemId, out _);
                    CompleteFlight(item.DavItemId, item.Flight, Par2RepairOutcome.NotRepaired);
                    OomDiagnostics.LogHeapStateOnOom(oom, "PAR2 background repair worker");
                    Log.Warning("PAR2 background repair worker deferred after exhausting managed memory. Path: {Path}", item.Path);
                }
            }
        }
        finally
        {
            while (_queue.Reader.TryRead(out var abandoned))
            {
                _queuedOrRunning.TryRemove(abandoned.DavItemId, out _);
                CancelFlight(abandoned.DavItemId, abandoned.Flight, stoppingToken);
            }
        }
    }

    private async Task MarkJobFailureAsync(
        Par2RepairJob? job,
        string reason,
        bool cooldown,
        CancellationToken ct)
    {
        if (job == null)
            return;

        job.State = Par2RepairJob.RepairJobState.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Attempts++;
        job.FailureReason = reason;
        job.NextAttemptAt = cooldown
            ? DateTimeOffset.UtcNow + TimeSpan.FromHours(_configManager.GetPar2FailureCooldownHours())
            : null;
        await PersistJobAsync(job, ct).ConfigureAwait(false);
    }

    private async Task TryMarkJobFailureAfterOomAsync(Par2RepairJob? job, CancellationToken ct)
    {
        try
        {
            await MarkJobFailureAsync(
                    job,
                    "PAR2 repair exceeded the process memory limit.",
                    cooldown: true,
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // The process may still be unable to allocate while handling an OOM.
            // The next startup reconciliation will release the persisted Running row.
        }
    }

    private async Task ProcessZeroFillQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in _zeroFillQueue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessZeroFillEventAsync(evt, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CorruptedBlobPayloadException e)
            {
                // Local streaming metadata is unreadable, not the release's Usenet
                // articles — surface it instead of silently dropping the trigger.
                Log.Warning(
                    "PAR2 zero-fill trigger skipped for {Path}: its streaming metadata blob {BlobId} is unreadable.",
                    evt.Path, e.BlobId);
                Log.Debug(e, "Unreadable streaming metadata blob stack for {Path}", evt.Path);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Debug(e, "PAR2 zero-fill trigger failed for {Path}", evt.Path);
            }
            catch (OutOfMemoryException oom)
            {
                OomDiagnostics.LogHeapStateOnOom(oom, "PAR2 zero-fill trigger");
                Log.Warning("PAR2 zero-fill trigger deferred after exhausting managed memory. Path: {Path}", evt.Path);
            }
            finally
            {
                _pendingZeroFillPaths.TryRemove(evt.Path, out _);
                TryRearmPendingZeroFill(evt.Path);
            }
        }
    }

    private async Task ProcessZeroFillEventAsync(ZeroFillEvent evt, CancellationToken ct)
    {
        var reports = DrainPendingSegmentReports(evt.Path);
        reports.Add((evt.SegmentId, evt.IsCorruption));

        await using var dbContext = CreateContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = await dbContext.Items
            .FirstOrDefaultAsync(x => x.Path == evt.Path, ct)
            .ConfigureAwait(false);
        if (davItem is null)
        {
            Log.Debug("Playback repair trigger skipped; no DavItem at {Path}", evt.Path);
            return;
        }

        if (davItem.SubType is DavItem.ItemSubType.RarFile or DavItem.ItemSubType.MultipartFile)
        {
            if (!_configManager.IsPar2RepairEnabled()) return;
            var multipartIds = reports.Where(report => !report.Item2 || _configManager.IsCorruptionTrackingEnabled())
                .Select(report => report.Item1).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (multipartIds.Length > 0)
                await EnqueueAsync(davItem, multipartIds, ct).ConfigureAwait(false);
            return;
        }

        var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
        if (nzbFile is null)
        {
            Log.Debug("Playback repair trigger skipped; no DavNzbFile payload for {Path}", evt.Path);
            return;
        }

        var missingIds = new List<string>();
        var corruptIds = new List<string>();
        var missingIndices = new List<int>();
        var corruptIndices = new List<int>();
        foreach (var (segmentId, isCorruption) in reports)
        {
            var index = Array.IndexOf(nzbFile.SegmentIds, segmentId);
            if (index < 0)
            {
                Log.Debug(
                    "Playback repair trigger skipped unknown segment {SegmentId} for {Path}",
                    segmentId,
                    evt.Path);
                continue;
            }

            if (isCorruption)
            {
                if (!_configManager.IsCorruptionTrackingEnabled())
                    continue;
                corruptIds.Add(segmentId);
                corruptIndices.Add(index);
            }
            else
            {
                missingIds.Add(segmentId);
                missingIndices.Add(index);
            }
        }

        if (missingIndices.Count > 0 || corruptIndices.Count > 0)
        {
            await DavNzbFileBlobUpdater.MutateAsync(
                davItem,
                current =>
                {
                    if (missingIndices.Count > 0)
                    {
                        current.MissingSegmentIndices = UnionIndices(
                            current.MissingSegmentIndices,
                            missingIndices.ToArray());
                    }

                    if (corruptIndices.Count > 0)
                    {
                        current.CorruptSegmentIndices = UnionIndices(
                            current.CorruptSegmentIndices,
                            corruptIndices.ToArray());
                    }

                    return current;
                },
                fallback: nzbFile).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (!_configManager.IsPar2RepairEnabled())
            return;

        var enqueueIds = missingIds.Concat(corruptIds).Distinct(StringComparer.Ordinal).ToArray();
        if (enqueueIds.Length > 0)
            await EnqueueAsync(davItem, enqueueIds, ct).ConfigureAwait(false);
    }

    internal Task ProcessCorruptionEventForTestsAsync(string path, string segmentId, CancellationToken ct) =>
        ProcessZeroFillEventAsync(new ZeroFillEvent(path, segmentId, IsCorruption: true), ct);

    internal Task ProcessZeroFillEventForTestsAsync(string path, string segmentId, CancellationToken ct) =>
        ProcessZeroFillEventAsync(new ZeroFillEvent(path, segmentId), ct);

    private async Task ProcessQueueItemAsync(RepairWorkItem item, CancellationToken ct)
    {
        await using var dbContext = CreateContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == item.DavItemId, ct)
            .ConfigureAwait(false);
        if (davItem == null)
        {
            _queuedOrRunning.TryRemove(item.DavItemId, out _);
            _retainedSegmentIds.TryRemove(item.DavItemId, out _);
            CompleteFlight(item.DavItemId, item.Flight, Par2RepairOutcome.NotRepaired);
            return;
        }

        await RunFlightAsync(item.Flight, davItem, item.MissingSegmentIds, queueGuard: true, RepairAdmissionMode.QueuedWait, ct)
            .ConfigureAwait(false);
    }

    private async Task<Par2RepairOutcome> RunFlightAsync(
        RepairFlight flight,
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        bool queueGuard,
        RepairAdmissionMode admissionMode,
        CancellationToken ct)
    {
        var admitted = false;
        var admissionUser = false;
        try
        {
            lock (_admissionLifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                _admissionUsers++;
                admissionUser = true;
            }
            if (admissionMode == RepairAdmissionMode.InlineTryOnce)
            {
                ct.ThrowIfCancellationRequested();
                admitted = await _repairAdmission.WaitAsync(0, ct).ConfigureAwait(false);
                if (!admitted)
                {
                    flight.Completion.TrySetResult(Par2RepairOutcome.DeferredBusy);
                    return Par2RepairOutcome.DeferredBusy;
                }
            }
            else
            {
                var wait = Stopwatch.StartNew();
                Interlocked.Increment(ref _admissionWaiters);
                PublishAdmissionMetrics();
                try
                {
                    await _repairAdmission.WaitAsync(ct).ConfigureAwait(false);
                    admitted = true;
                }
                finally
                {
                    Interlocked.Decrement(ref _admissionWaiters);
                    wait.Stop();
                    Interlocked.Add(ref _totalAdmissionWaitTicks, wait.Elapsed.Ticks);
                    Interlocked.Exchange(ref _latestAdmissionWaitTicks, wait.Elapsed.Ticks);
                    PrometheusMetrics.Current?.ObservePar2AdmissionWait(wait.Elapsed);
                    PublishAdmissionMetrics();
                }
            }

            Interlocked.Exchange(ref _admissionActive, 1);
            PublishAdmissionMetrics();
            var result = await RunRepairAsync(davItem, missingSegmentIds, flight, ct).ConfigureAwait(false);
            flight.Completion.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            flight.Completion.TrySetCanceled(ct);
            throw;
        }
        catch (Exception e)
        {
            flight.Completion.TrySetException(e);
            throw;
        }
        finally
        {
            if (admitted)
            {
                Interlocked.Exchange(ref _admissionActive, 0);
                _repairAdmission.Release();
                PublishAdmissionMetrics();
            }
            if (admissionUser)
                ReleaseAdmissionUser();
            if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
            _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItem.Id, flight));
            flight.Finished.TrySetResult();
            try
            {
                if (!ct.IsCancellationRequested)
                    await TryRequeueRetainedAsync(davItem.Id, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                Log.Debug(e, "Failed to requeue retained PAR2 segment IDs for {Path}", davItem.Path);
            }
        }
    }

    private void ReleaseAdmissionUser()
    {
        lock (_admissionLifecycle)
        {
            if (--_admissionUsers == 0 && _disposeRequested) _repairAdmission.Dispose();
        }
    }

    /// <summary>
    /// Holds the single repair permit without a repairable fixture. Registers as an
    /// admission user so disposing the service while held cannot destroy the semaphore
    /// before the returned hold releases it.
    /// </summary>
    internal async Task<IAsyncDisposable> HoldAdmissionForTestsAsync(CancellationToken ct)
    {
        lock (_admissionLifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _admissionUsers++;
        }
        try
        {
            await _repairAdmission.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseAdmissionUser();
            throw;
        }
        Interlocked.Exchange(ref _admissionActive, 1);
        PublishAdmissionMetrics();
        return new AdmissionHold(this);
    }

    private sealed class AdmissionHold(Par2RepairService service) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Exchange(ref service._admissionActive, 0);
                service._repairAdmission.Release();
                service.PublishAdmissionMetrics();
                service.ReleaseAdmissionUser();
            }
            return ValueTask.CompletedTask;
        }
    }

    private void CompleteFlight(
        Guid davItemId,
        RepairFlight flight,
        Par2RepairOutcome result)
    {
        flight.Completion.TrySetResult(result);
        _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItemId, flight));
        flight.Finished.TrySetResult();
    }

    private void PublishAdmissionMetrics() => PrometheusMetrics.Current?.SetPar2Admission(
        Volatile.Read(ref _admissionActive), Volatile.Read(ref _admissionWaiters));

    public override void Dispose()
    {
        base.Dispose();
        lock (_admissionLifecycle)
        {
            if (!_disposeRequested && _admissionUsers == 0) _repairAdmission.Dispose();
            _disposeRequested = true;
        }
        GC.SuppressFinalize(this);
    }

    private void CancelFlight(Guid davItemId, RepairFlight flight, CancellationToken cancellationToken)
    {
        flight.Completion.TrySetCanceled(cancellationToken);
        _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItemId, flight));
        flight.Finished.TrySetResult();
    }

    private async Task<Par2RepairOutcome> RunRepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        RepairFlight flight,
        CancellationToken ct)
    {
        if (!_configManager.IsPar2RepairEnabled())
            return Par2RepairOutcome.NotRepaired;

        Par2RepairJob? job = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            job = await CreateOrResumeJobAsync(davItem, missingSegmentIds, ct).ConfigureAwait(false);
            if (job == null)
                return Par2RepairOutcome.NotRepaired;

            job.State = Par2RepairJob.RepairJobState.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await PersistJobAsync(job, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordPar2RepairJob("running");

            // MaintenanceDownloadContext is attribution-only; it does NOT set AttributionContext,
            // so recovery-volume BODY fetches MAY populate the playback segment cache (harmless).
            using var maintenanceScope = ct.SetContext(MaintenanceDownloadContext.Instance);
            using var fetchAttribution = FetchAttributionContext.Begin(davItem.Name);

            BeginRepairDiagnostics(davItem.Path);
            var result = await ExecuteRepairJobAsync(davItem, job, ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.Success)
            {
                job.State = Par2RepairJob.RepairJobState.Succeeded;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.BytesRead = result.BytesRead;
                job.SlicesReconstructed = result.SlicesReconstructed;
                job.FailureReason = null;
                job.NextAttemptAt = null;
                await PersistJobAsync(job, ct).ConfigureAwait(false);
                flight.SetCoverage(job.MissingSegmentIds, result.VerifiedClean && missingSegmentIds is not { Count: > 0 });
                PrometheusMetrics.Current?.RecordPar2RepairJob("succeeded");
                PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
                PrometheusMetrics.Current?.SetPar2PatchStoreBytes(_patchStore.CurrentBytes);
                Interlocked.Increment(ref _totalSucceeded);
                Interlocked.Add(ref _totalBytesRead, result.BytesRead);
                Interlocked.Add(ref _totalSlicesReconstructed, result.SlicesReconstructed);
                Interlocked.Add(ref _totalSegmentsCommitted, result.SegmentsCommitted);
                if (result.VerifiedClean)
                {
                    Log.Information(
                        "PAR2 verification succeeded for {Path}: all slices matched, " +
                        "{Bytes} bytes read in {Elapsed}",
                        davItem.Path, result.BytesRead, stopwatch.Elapsed);
                    return Par2RepairOutcome.VerifiedClean;
                }

                Log.Information(
                    "PAR2 repair succeeded for {Path}: {Slices} slice(s) reconstructed, "
                    + "{Segments} segment(s) committed, {Bytes} bytes read in {Elapsed}",
                    davItem.Path, result.SlicesReconstructed, result.SegmentsCommitted,
                    result.BytesRead, stopwatch.Elapsed);
                return Par2RepairOutcome.Repaired;
            }

            job.State = result.IsInfeasible
                ? Par2RepairJob.RepairJobState.Infeasible
                : Par2RepairJob.RepairJobState.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.BytesRead = result.BytesRead;
            job.FailureReason = result.FailureReason;
            job.NextAttemptAt = DateTimeOffset.UtcNow +
                                TimeSpan.FromHours(_configManager.GetPar2FailureCooldownHours());
            await PersistJobAsync(job, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordPar2RepairJob(result.IsInfeasible ? "infeasible" : "failed");
            PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
            if (result.BytesRead > 0)
                Interlocked.Add(ref _totalBytesRead, result.BytesRead);
            if (result.IsInfeasible) Interlocked.Increment(ref _totalInfeasible);
            else Interlocked.Increment(ref _totalFailed);
            Log.Warning(
                "PAR2 repair {Outcome} for {Path}. Reason: {Reason}",
                result.IsInfeasible ? "infeasible" : "failed", davItem.Path, result.FailureReason);
            return Par2RepairOutcome.NotRepaired;
        }
        catch (OutOfMemoryException oom)
        {
            stopwatch.Stop();
            OomDiagnostics.LogHeapStateOnOom(oom, "PAR2 repair");
            await TryMarkJobFailureAfterOomAsync(job, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.RecordPar2RepairJob("failed");
            PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
            Interlocked.Increment(ref _totalFailed);
            Log.Warning("PAR2 repair deferred after exhausting managed memory. Path: {Path}", davItem.Path);
            return Par2RepairOutcome.NotRepaired;
        }
        catch (Exception exception) when (exception is MissingFilePayloadException or CorruptedBlobPayloadException)
        {
            await MarkJobFailureAsync(job, exception.Message, cooldown: false, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalFailed);
            PrometheusMetrics.Current?.RecordPar2RepairJob("failed");
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkJobFailureAsync(job, "PAR2 repair canceled before completion.", cooldown: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            stopwatch.Stop();
            e.TryGetKnownErrorMessage(out var reason);
            await MarkJobFailureAsync(job, reason ?? e.Message, cooldown: true, ct).ConfigureAwait(false);

            e.LogWarningKnownOrStack("PAR2 repair error for {Path}", davItem.Path);
            PrometheusMetrics.Current?.RecordPar2RepairJob("failed");
            PrometheusMetrics.Current?.ObservePar2RepairDuration(stopwatch.Elapsed);
            return Par2RepairOutcome.NotRepaired;
        }
        finally
        {
            EndRepairDiagnostics();
        }
    }

    private static async Task<bool> DiscoverUnavailableSourcesAsync(
        ResolvedSliceAccessor accessor,
        Par2FileSliceMap sliceMap,
        IfscPacket targetIfsc,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices,
        int maxMissingSlices,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        accessor.BeginSequentialPass();
        AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices);
        if (unavailableSlices.Count > maxMissingSlices)
            return true;

        for (var local = 0; local < sliceMap.SliceCount; local++)
        {
            ct.ThrowIfCancellationRequested();
            var globalSlice = checked(sliceMap.GlobalSliceBase + local);
            if (unavailableSlices.Contains(globalSlice))
                continue;

            var assembled = await accessor.FetchSliceBytesAsync(globalSlice, sliceMap.SliceSize, ct)
                .ConfigureAwait(false);
            var valid = assembled is not null && Par2Reconstructor.VerifySliceChecksum(assembled, targetIfsc.Slices[local]);
            if (assembled is not null && !valid)
            {
                foreach (var segmentIndex in sliceMap.SegmentIndicesForGlobalSlice(globalSlice))
                    accessor.NoteCorrupt(segmentIndex);
            }
            AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices);
            if (!valid)
                unavailableSlices.Add(globalSlice);
            if (unavailableSlices.Count > maxMissingSlices)
                return true;
        }

        return false;
    }

    private static void AbsorbAccessorDiscoveries(
        ResolvedSliceAccessor accessor,
        Par2FileSliceMap sliceMap,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices)
    {
        foreach (var index in accessor.MissingSegmentIndices
                     .Concat(accessor.CorruptSegmentIndices)
                     .Where(unavailableSegments.Add))
        {
            unavailableSlices.UnionWith(sliceMap.GlobalSlicesForSegment(index));
        }
    }

    private static List<MissingSegment> SegmentsOverlappingSlices(
        Par2FileSliceMap sliceMap,
        HashSet<int> unavailableSlices,
        string[] segmentIds)
    {
        var targets = new List<MissingSegment>();
        for (var i = 0; i < segmentIds.Length; i++)
        {
            if (sliceMap.GlobalSlicesForSegment(i).Any(unavailableSlices.Contains))
                targets.Add(new MissingSegment(segmentIds[i], i));
        }

        return targets;
    }

    internal static long EstimateWorkingSetBytes(
        long peakSourceBodyBytes,
        int recoverySliceCount,
        int reconstructedSliceCount,
        long stagedPatchBytes,
        int sliceSize)
    {
        checked
        {
            return peakSourceBodyBytes +
                   EstimateNonSourceWorkingSetBytes(
                       recoverySliceCount,
                       reconstructedSliceCount,
                       stagedPatchBytes,
                       sliceSize);
        }
    }

    /// <summary>
    /// Memory outside the retained source window: the current assembled/fetch slice,
    /// the Reed-Solomon scratch slice, recovery data, GF accumulators, reconstructed
    /// output, and the final segment patches.
    /// </summary>
    private static long EstimateNonSourceWorkingSetBytes(
        int recoverySliceCount,
        int reconstructedSliceCount,
        long stagedPatchBytes,
        int sliceSize)
    {
        checked
        {
            var assembled = 2L * sliceSize;
            var recovery = (long)recoverySliceCount * sliceSize;
            var accumulators = (long)recoverySliceCount * sliceSize;
            var reconstructed = (long)reconstructedSliceCount * sliceSize;
            return assembled + recovery + accumulators + reconstructed + stagedPatchBytes;
        }
    }

    private static HashSet<int> ValidIndices(int[]? indices, int segmentCount)
    {
        if (indices is not { Length: > 0 })
            return [];
        return indices.Where(index => (uint)index < (uint)segmentCount).ToHashSet();
    }

    private async Task PersistDiscoveredDamageAsync(
        DavItem davItem,
        DavNzbFile nzbFile,
        ResolvedSliceAccessor accessor,
        CancellationToken ct)
    {
        var missing = accessor.MissingSegmentIndices.OrderBy(i => i).ToArray();
        var corrupt = accessor.CorruptSegmentIndices.OrderBy(i => i).ToArray();
        if (missing.Length == 0 && corrupt.Length == 0)
            return;

        await DavNzbFileBlobUpdater.MutateAsync(
            davItem,
            current =>
            {
                if (missing.Length > 0)
                {
                    current.MissingSegmentIndices = UnionIndices(current.MissingSegmentIndices, missing);
                }

                if (corrupt.Length > 0)
                {
                    current.CorruptSegmentIndices = UnionIndices(current.CorruptSegmentIndices, corrupt);
                }

                return current;
            },
            fallback: nzbFile).ConfigureAwait(false);

        await using var dbContext = CreateContext();
        var tracked = await dbContext.Items.FirstOrDefaultAsync(x => x.Id == davItem.Id, ct).ConfigureAwait(false);
        if (tracked is not null && davItem.FileBlobId is { } blobId)
        {
            tracked.FileBlobId = blobId;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static int[] UnionIndices(int[]? existing, int[] discovered)
    {
        return (existing ?? [])
            .Concat(discovered)
            .Distinct()
            .OrderBy(i => i)
            .ToArray();
    }

#pragma warning disable CA5351 // PAR 2.0 whole-file hashes use MD5 per spec
    private static async Task<string?> TryVerifyWholeFileMd5Async(
        FileDesc desc,
        Par2FileSliceMap sliceMap,
        Dictionary<int, byte[]> reconstructedSlices,
        ResolvedSliceAccessor accessor,
        CancellationToken ct)
    {
        if (desc.FileHash is not { Length: 16 })
            return null;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long offset = 0;
        while (offset < sliceMap.FileLength)
        {
            ct.ThrowIfCancellationRequested();
            var globalSlice = sliceMap.GlobalSliceBase + (int)(offset / sliceMap.SliceSize);
            var sliceRange = sliceMap.SliceFileRange(globalSlice);
            byte[] sliceBytes;
            if (reconstructedSlices.TryGetValue(globalSlice, out var reconstructed))
            {
                sliceBytes = reconstructed;
            }
            else
            {
                var fetched = await accessor.FetchSliceBytesAsync(globalSlice, sliceMap.SliceSize, ct)
                    .ConfigureAwait(false);
                if (fetched is null)
                {
                    return $"Whole-file MD5 coverage gap at slice {globalSlice}.";
                }

                sliceBytes = fetched;
            }

            var validLen = (int)Math.Min(sliceRange.Count, sliceMap.FileLength - offset);
            if (validLen > 0)
                hasher.AppendData(sliceBytes.AsSpan(0, validLen));
            offset += validLen;
        }

        var computed = hasher.GetHashAndReset();
        if (computed.AsSpan().SequenceEqual(desc.FileHash))
            return null;

        return $"Whole-file MD5 mismatch for {desc.FileName}.";
    }
#pragma warning restore CA5351

    private static async Task<List<SegmentPatch>> ExtractSegmentPatchesAsync(
        IReadOnlyList<MissingSegment> missingSegments,
        Par2FileSliceMap sliceMap,
        Dictionary<int, byte[]> reconstructedSlices,
        ResolvedSliceAccessor accessor,
        string fileName,
        long fileSize,
        int segmentCount,
        CancellationToken ct)
    {
        var patches = new List<SegmentPatch>();
        foreach (var missing in missingSegments)
        {
            var range = sliceMap.SegmentRanges[missing.Index];
            var bytes = new byte[range.Count];
            var sourceBody = await accessor.GetSegmentBodyForPatchAsync(missing.Index, ct).ConfigureAwait(false);
            if (sourceBody is not null)
                Buffer.BlockCopy(sourceBody, 0, bytes, 0, Math.Min(sourceBody.Length, bytes.Length));

            var copied = 0L;
            var fileOffset = range.StartInclusive;
            while (copied < range.Count)
            {
                var globalSlice = sliceMap.GlobalSliceBase + (int)((fileOffset + copied) / sliceMap.SliceSize);
                if (reconstructedSlices.TryGetValue(globalSlice, out var slice))
                {
                    var offsetInSlice = (int)((fileOffset + copied) % sliceMap.SliceSize);
                    var toCopy = (int)Math.Min(range.Count - copied, sliceMap.SliceSize - offsetInSlice);
                    Buffer.BlockCopy(slice, offsetInSlice, bytes, (int)copied, toCopy);
                    copied += toCopy;
                    continue;
                }

                if (sourceBody is not null)
                {
                    copied += Math.Min(
                        range.Count - copied,
                        sliceMap.SliceSize - ((fileOffset + copied) % sliceMap.SliceSize));
                    continue;
                }

                throw new InvalidOperationException(
                    $"Neither source nor reconstructed bytes were available for PAR2 patch slice {globalSlice}.");
            }

            patches.Add(new SegmentPatch(
                missing.SegmentId,
                bytes,
                new UsenetYencHeader
                {
                    FileName = fileName,
                    FileSize = fileSize,
                    LineLength = 128,
                    PartNumber = missing.Index + 1,
                    TotalParts = segmentCount,
                    PartSize = range.Count,
                    PartOffset = range.StartInclusive,
                }));
            accessor.BeginSequentialPass();
        }

        return patches;
    }

    private static int GlobalSliceOffset(
        int targetFileIndex,
        MainPacket main,
        Dictionary<string, IfscPacket> ifscsByFileId)
    {
        var offset = 0;
        for (var i = 0; i < targetFileIndex; i++)
        {
            var key = Convert.ToHexString(main.FileIds[i]);
            offset += ifscsByFileId[key].Slices.Count;
        }

        return offset;
    }

    private static LongRange[] BuildSegmentRanges(DavNzbFile nzbFile, int segmentCount, long? fileSize)
    {
        if (nzbFile.SegmentByteRanges is { Length: var len } ranges && len == segmentCount)
            return ranges;

        if (fileSize is > 0 && segmentCount > 0)
        {
            var uniform = fileSize.Value / segmentCount;
            if (uniform > 0)
            {
                var built = new LongRange[segmentCount];
                long start = 0;
                for (var i = 0; i < segmentCount; i++)
                {
                    var size = i == segmentCount - 1 ? fileSize.Value - start : uniform;
                    built[i] = LongRange.FromStartAndSize(start, size);
                    start += size;
                }

                return built;
            }
        }

        throw new InvalidOperationException("Segment byte ranges are unavailable for PAR2 repair.");
    }

    private static bool IsPar2CandidateSubject(NzbFile file)
    {
        var name = file.GetSubjectFileName();
        return name.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)
               || Par2.ParVolume.IsMatch(name);
    }

    private async Task<bool> ShouldEnqueueAsync(Guid davItemId, CancellationToken ct)
    {
        if (_queuedOrRunning.ContainsKey(davItemId)) return false;

        await using var dbContext = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var active = await dbContext.Par2RepairJobs.AsNoTracking()
            .Where(x => x.DavItemId == davItemId)
            .Where(x => x.State == Par2RepairJob.RepairJobState.Queued
                        || x.State == Par2RepairJob.RepairJobState.Running)
            .AnyAsync(ct)
            .ConfigureAwait(false);
        if (active) return false;

        var cooling = await dbContext.Par2RepairJobs.AsNoTracking()
            .Where(x => x.DavItemId == davItemId)
            .Where(x => x.NextAttemptAt != null && x.NextAttemptAt > now)
            .AnyAsync(ct)
            .ConfigureAwait(false);
        return !cooling;
    }

    private async Task<Par2RepairJob?> CreateOrResumeJobAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        CancellationToken ct)
    {
        await using var dbContext = CreateContext();
        var existing = await dbContext.Par2RepairJobs
            .Where(x => x.DavItemId == davItem.Id)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is { State: Par2RepairJob.RepairJobState.Running })
            return null;

        if (existing?.NextAttemptAt > DateTimeOffset.UtcNow)
            return null;

        if (existing is { Attempts: >= MaxAttempts }
            and { NextAttemptAt: not null }
            and ({ State: Par2RepairJob.RepairJobState.Failed } or { State: Par2RepairJob.RepairJobState.Infeasible }))
            return null;

        var segments = missingSegmentIds?.ToArray() ?? [];

        if (existing is { State: Par2RepairJob.RepairJobState.Queued or Par2RepairJob.RepairJobState.Failed or Par2RepairJob.RepairJobState.Infeasible })
        {
            existing.Attempts++;
            existing.MissingSegmentIds = segments.Length == 0 ? segments
                : existing.MissingSegmentIds.Concat(segments).Distinct(StringComparer.Ordinal).ToArray();
            existing.State = Par2RepairJob.RepairJobState.Queued;
            existing.CreatedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        return new Par2RepairJob
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            State = Par2RepairJob.RepairJobState.Queued,
            MissingSegmentIds = segments,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempts = 1,
        };
    }

    private async Task PersistJobAsync(Par2RepairJob job, CancellationToken ct)
    {
        await using var dbContext = CreateContext();
        var tracked = await dbContext.Par2RepairJobs
            .FirstOrDefaultAsync(x => x.Id == job.Id, ct)
            .ConfigureAwait(false);
        if (tracked == null)
        {
            dbContext.Par2RepairJobs.Add(job);
        }
        else
        {
            dbContext.Entry(tracked).CurrentValues.SetValues(job);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static int CountRepairedSegments(DavNzbFile nzbFile, RepairPatchStore patchStore)
    {
        if (!patchStore.IsCatalogReady) return 0;
        var count = 0;
        var ranges = nzbFile.SegmentByteRanges;
        for (var i = 0; i < nzbFile.SegmentIds.Length; i++)
        {
            if (ranges == null || i >= ranges.Length) continue;
            var size = ranges[i].Count;

            if (patchStore.IsRepaired(nzbFile.SegmentIds[i], size))
                count++;
        }

        return count;
    }

    public Par2RepairDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        var recentJobs = GetRecentJobsForDiagnostics();
        var source = _activeSource;
        var activePath = _activeRepairPath;
        var activePhase = _activeRepairPhase;
        return new Par2RepairDiagnosticSnapshot
        {
            AdmissionActive = Volatile.Read(ref _admissionActive),
            AdmissionWaiters = Volatile.Read(ref _admissionWaiters),
            TotalAdmissionWaitSeconds = TimeSpan.FromTicks(Interlocked.Read(ref _totalAdmissionWaitTicks)).TotalSeconds,
            LatestAdmissionWaitSeconds = TimeSpan.FromTicks(Interlocked.Read(ref _latestAdmissionWaitTicks)).TotalSeconds,
            PatchStoreEntries = _patchStore.EntryCount,
            PatchHitCount = _patchStore.HitCount,
            PatchEvictionCount = _patchStore.EvictionCount,
            QueuedOrRunningCount = _queuedOrRunning.Count,
            TotalSucceeded = Interlocked.Read(ref _totalSucceeded),
            TotalFailed = Interlocked.Read(ref _totalFailed),
            TotalInfeasible = Interlocked.Read(ref _totalInfeasible),
            TotalBytesRead = Interlocked.Read(ref _totalBytesRead),
            TotalSlicesReconstructed = Interlocked.Read(ref _totalSlicesReconstructed),
            TotalSegmentsCommitted = Interlocked.Read(ref _totalSegmentsCommitted),
            RecentJobs = recentJobs,
            ActiveRepair = activePath is null
                ? null
                : new ActivePar2RepairDiagnostic(
                    activePath,
                    activePhase ?? "starting",
                    Interlocked.Read(ref _activeBytesRead),
                    Interlocked.Read(ref _activeEstimatedWorkingSetBytes),
                    Interlocked.Read(ref _activeMemoryCapBytes),
                    source?.CachedBodyBytes ?? 0,
                    source?.PeakCachedBodyBytes ?? 0,
                    source?.RetainedByteLimit ?? 0),
        };
    }

    internal bool CanAcceptInlineRepair =>
        !_disposeRequested
        && Volatile.Read(ref _admissionActive) == 0
        && Volatile.Read(ref _admissionWaiters) == 0;

    private void BeginRepairDiagnostics(string path)
    {
        _activeRepairPath = path;
        _activeRepairPhase = "starting";
        _activeSource = null;
        Interlocked.Exchange(ref _activeBytesRead, 0);
        Interlocked.Exchange(ref _activeEstimatedWorkingSetBytes, 0);
        Interlocked.Exchange(ref _activeMemoryCapBytes, 0);
    }

    private void SetRepairPhase(string phase, long memoryCapBytes)
    {
        _activeRepairPhase = phase;
        Interlocked.Exchange(ref _activeMemoryCapBytes, memoryCapBytes);
    }

    private void EndRepairDiagnostics()
    {
        _activeSource = null;
        _activeRepairPhase = null;
        _activeRepairPath = null;
    }

    private List<object> GetRecentJobsForDiagnostics()
    {
        try
        {
            using var dbContext = CreateContext();
            return dbContext.Par2RepairJobs
                .OrderByDescending(j => j.CreatedAt)
                .Take(10)
                .AsEnumerable()
                .Select(j => (object)new
                {
                    id = j.Id,
                    path = j.Path,
                    state = j.State.ToString(),
                    createdAt = j.CreatedAt,
                    startedAt = j.StartedAt,
                    completedAt = j.CompletedAt,
                    attempts = j.Attempts,
                    bytesRead = j.BytesRead,
                    slicesReconstructed = j.SlicesReconstructed,
                    failureReason = j.FailureReason,
                })
                .ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Could not load recent PAR2 repair jobs for diagnostics");
            return [];
        }
    }

    public sealed class Par2RepairDiagnosticSnapshot
    {
        public int AdmissionLimit => 1;
        public int AdmissionActive { get; init; }
        public int AdmissionWaiters { get; init; }
        public double TotalAdmissionWaitSeconds { get; init; }
        public double LatestAdmissionWaitSeconds { get; init; }
        public int PatchStoreEntries { get; init; }
        public long PatchHitCount { get; init; }
        public long PatchEvictionCount { get; init; }
        public int QueuedOrRunningCount { get; init; }
        public long TotalSucceeded { get; init; }
        public long TotalFailed { get; init; }
        public long TotalInfeasible { get; init; }
        public long TotalBytesRead { get; init; }
        public long TotalSlicesReconstructed { get; init; }
        public long TotalSegmentsCommitted { get; init; }
        public List<object> RecentJobs { get; init; } = [];
        public ActivePar2RepairDiagnostic? ActiveRepair { get; init; }
    }

    public sealed record ActivePar2RepairDiagnostic(
        string Path,
        string Phase,
        long BytesRead,
        long EstimatedWorkingSetBytes,
        long MemoryCapBytes,
        long RetainedSourceBytes,
        long PeakRetainedSourceBytes,
        long RetainedSourceLimitBytes);

    private sealed class RepairFlight(IReadOnlyList<string>? requestedIds)
    {
        private HashSet<string> _coveredIds = new(StringComparer.Ordinal);
        private bool _verifiedAll;
        private readonly bool _requestedVerification = requestedIds is not { Count: > 0 };
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Par2RepairOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Par2RepairOutcome> Task => Completion.Task;

        public void SetCoverage(IEnumerable<string> ids, bool verifiedAll)
        {
            _coveredIds = ids.ToHashSet(StringComparer.Ordinal);
            _verifiedAll = _requestedVerification && verifiedAll;
        }

        public bool Covers(IReadOnlyList<string>? ids) => ids is not { Count: > 0 }
            ? _verifiedAll : ids.All(_coveredIds.Contains);
    }

    private void RetainSegmentIds(Guid davItemId, string path, IEnumerable<string> segmentIds)
    {
        var retained = _retainedSegmentIds.GetOrAdd(
            davItemId,
            _ => new RetainedRepair { Path = path });
        foreach (var id in segmentIds.Where(id => !string.IsNullOrEmpty(id)))
            retained.Ids.TryAdd(id, 0);
    }

    private string[] DrainRetainedSegmentIds(Guid davItemId)
    {
        if (!_retainedSegmentIds.TryRemove(davItemId, out var retained))
            return [];
        return retained.Ids.Keys.ToArray();
    }

    private async Task TryRequeueRetainedAsync(Guid davItemId, CancellationToken ct)
    {
        if (!_retainedSegmentIds.ContainsKey(davItemId))
            return;

        await using var dbContext = CreateContext();
        var davItem = await dbContext.Items.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItemId, ct)
            .ConfigureAwait(false);
        if (davItem is null)
        {
            _retainedSegmentIds.TryRemove(davItemId, out _);
            return;
        }

        await EnqueueAsync(davItem, [], ct).ConfigureAwait(false);

        // A blocked requeue (failure cooldown, full queue, repair disabled) leaves no
        // flight that could drain the retained entry, so it would sit for the process
        // lifetime. Drop it: the blob's persisted MissingSegmentIndices remain the
        // durable record and are unioned into any future repair job for the item.
        if (!_queuedOrRunning.ContainsKey(davItemId))
            _retainedSegmentIds.TryRemove(davItemId, out _);
    }

    private List<(string Id, bool IsCorruption)> DrainPendingSegmentReports(string path)
    {
        var drained = new List<(string Id, bool IsCorruption)>();
        if (!_pendingSegmentIds.TryGetValue(path, out var queue))
            return drained;

        while (queue.TryDequeue(out var item))
            drained.Add(item);
        return drained;
    }

    private void TryRearmPendingZeroFill(string path)
    {
        if (!_pendingSegmentIds.TryGetValue(path, out var queue) || queue.IsEmpty)
        {
            _pendingSegmentIds.TryRemove(path, out _);
            return;
        }

        if (!_pendingZeroFillPaths.TryAdd(path, 0))
            return;

        // Dequeue the head rather than peeking it: ProcessZeroFillEventAsync drains
        // the queue and then appends the event payload, so a peeked head would be
        // reported twice.
        var segmentId = "";
        var isCorruption = false;
        if (queue.TryDequeue(out var head))
        {
            segmentId = head.Id;
            isCorruption = head.IsCorruption;
        }

        if (_zeroFillQueue.Writer.TryWrite(new ZeroFillEvent(path, segmentId, isCorruption)))
            return;

        _pendingZeroFillPaths.TryRemove(path, out _);
        _pendingSegmentIds.TryRemove(path, out _);
    }

    private sealed class RetainedRepair
    {
        public required string Path { get; init; }
        public ConcurrentDictionary<string, byte> Ids { get; } = new(StringComparer.Ordinal);
    }

    private sealed record RepairWorkItem(
        Guid DavItemId,
        string Path,
        string[] MissingSegmentIds,
        RepairFlight Flight);

    private sealed record ZeroFillEvent(string Path, string SegmentId, bool IsCorruption = false);

    private sealed record MissingSegment(string SegmentId, int Index);

    private sealed record SegmentPatch(string SegmentId, byte[] Bytes, UsenetYencHeader Header);

    private sealed record Par2SetContext(
        MainPacket Main,
        Dictionary<string, FileDesc> FileDescsById,
        Dictionary<string, IfscPacket> IfscsByFileId,
        string RecoverySetId);

    private sealed record RepairExecutionResult(
        bool Success,
        bool VerifiedClean,
        bool IsInfeasible,
        string? FailureReason,
        long BytesRead,
        int SlicesReconstructed,
        int SegmentsCommitted)
    {
        public static RepairExecutionResult Succeeded(long bytesRead, int slices, int segmentsCommitted)
            => new(true, false, false, null, bytesRead, slices, segmentsCommitted);

        public static RepairExecutionResult Verified(long bytesRead)
            => new(true, true, false, null, bytesRead, 0, 0);

        public static RepairExecutionResult NotFeasible(string reason, long bytesRead = 0)
            => new(false, false, true, reason, bytesRead, 0, 0);

        public static RepairExecutionResult Failed(string reason, long bytesRead = 0)
            => new(false, false, false, reason, bytesRead, 0, 0);
    }

    private sealed class Par2MemoryCapExceededException(string message) : InvalidOperationException(message);
}
