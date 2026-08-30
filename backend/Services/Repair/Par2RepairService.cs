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
}

public class Par2RepairService : BackgroundService
{
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
    private SliceSegmentAccessor? _activeSource;
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
            flight.Completion.TrySetResult(Par2RepairOutcome.NotRepaired);
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

        var flight = new RepairFlight();
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
            _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItem.Id, flight));
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

        var mine = new RepairFlight();
        var flight = _repairFlights.GetOrAdd(davItem.Id, mine);
        if (ReferenceEquals(flight, mine))
            return await RunFlightAsync(flight, davItem, missingSegmentIds, queueGuard: false, ct)
                .ConfigureAwait(false);

        try
        {
            return await flight.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The repair owner stopped; callers should follow their normal safe fallback
            // rather than surfacing a cancellation that they did not request.
            return Par2RepairOutcome.NotRepaired;
        }
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

        await RunFlightAsync(item.Flight, davItem, item.MissingSegmentIds, queueGuard: true, ct)
            .ConfigureAwait(false);
    }

    private async Task<Par2RepairOutcome> RunFlightAsync(
        RepairFlight flight,
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        bool queueGuard,
        CancellationToken ct)
    {
        try
        {
            var result = await RunRepairAsync(davItem, missingSegmentIds, queueGuard, ct).ConfigureAwait(false);
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
            _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItem.Id, flight));
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

    private void CompleteFlight(
        Guid davItemId,
        RepairFlight flight,
        Par2RepairOutcome result)
    {
        flight.Completion.TrySetResult(result);
        _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItemId, flight));
    }

    private void CancelFlight(Guid davItemId, RepairFlight flight, CancellationToken cancellationToken)
    {
        flight.Completion.TrySetCanceled(cancellationToken);
        _repairFlights.TryRemove(new KeyValuePair<Guid, RepairFlight>(davItemId, flight));
    }

    private async Task<Par2RepairOutcome> RunRepairAsync(
        DavItem davItem,
        IReadOnlyList<string>? missingSegmentIds,
        bool queueGuard,
        CancellationToken ct)
    {
        if (!_configManager.IsPar2RepairEnabled())
        {
            if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
            return Par2RepairOutcome.NotRepaired;
        }

        Par2RepairJob? job = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            job = await CreateOrResumeJobAsync(davItem, missingSegmentIds, ct).ConfigureAwait(false);
            if (job == null)
            {
                if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
                return Par2RepairOutcome.NotRepaired;
            }

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
                await PersistJobAsync(job, ct).ConfigureAwait(false);
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
            if (queueGuard) _queuedOrRunning.TryRemove(davItem.Id, out _);
        }
    }

    private async Task<RepairExecutionResult> ExecuteRepairJobAsync(
        DavItem davItem,
        Par2RepairJob job,
        CancellationToken ct)
    {
        if (davItem.SubType != DavItem.ItemSubType.NzbFile)
            return RepairExecutionResult.NotFeasible("PAR2 repair supports plain NZB files only.");

        if (davItem.NzbBlobId is not Guid nzbBlobId)
            return RepairExecutionResult.NotFeasible("NZB blob id is missing for this file.");

        await using var nzbStream = BlobStore.ReadBlob(nzbBlobId);
        if (nzbStream == null)
            return RepairExecutionResult.NotFeasible("NZB blob is no longer available.");

        NzbDocument nzbDocument;
        try
        {
            nzbDocument = await NzbDocument.LoadAsync(nzbStream, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return RepairExecutionResult.NotFeasible($"Could not parse NZB blob: {e.Message}");
        }

        await using var dbContext = CreateContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
        if (nzbFile?.SegmentIds is not { Length: > 0 } segmentIds)
            return RepairExecutionResult.NotFeasible("Streaming payload metadata is missing.");

        var contentNzb = FindContentNzbFile(nzbDocument, davItem.Name);
        if (contentNzb == null)
            return RepairExecutionResult.NotFeasible("Could not locate the content file in the NZB.");

        var par2Context = await DiscoverPar2SetAsync(nzbDocument, contentNzb, davItem.Name, ct)
            .ConfigureAwait(false);
        if (par2Context == null)
            return RepairExecutionResult.NotFeasible("No matching PAR2 recovery set found in the NZB.");

        var sliceSize = (int)par2Context.Main.SliceSize;
        var targetKey = Convert.ToHexString(par2Context.Main.FileIds[par2Context.TargetFileIndex]);
        var targetDesc = par2Context.FileDescsById[targetKey];
        var targetIfsc = par2Context.IfscsByFileId[targetKey];
        var fileLength = davItem.FileSize ?? (long)targetDesc.FileLength;
        var globalSliceBase = GlobalSliceOffset(
            par2Context.TargetFileIndex, par2Context.Main, par2Context.IfscsByFileId);

        LongRange[] segmentRanges;
        try
        {
            segmentRanges = BuildSegmentRanges(nzbFile, segmentIds.Length, fileLength);
        }
        catch (InvalidOperationException e)
        {
            return RepairExecutionResult.NotFeasible(e.Message);
        }

        if (!Par2FileSliceMap.TryCreate(
                fileLength,
                globalSliceBase,
                sliceSize,
                targetIfsc.Slices.Count,
                segmentRanges,
                out var sliceMap,
                out var mapError)
            || sliceMap is null)
        {
            return RepairExecutionResult.NotFeasible(mapError ?? "Could not map segments onto PAR2 slices.");
        }

        var requested = ResolveMissingSegments(job.MissingSegmentIds, segmentIds);
        var persistedMissing = ValidIndices(nzbFile.MissingSegmentIndices, segmentIds.Length);
        var persistedCorrupt = ValidIndices(nzbFile.CorruptSegmentIndices, segmentIds.Length);
        var unavailableSegments = new HashSet<int>();
        foreach (var item in requested)
            unavailableSegments.Add(item.Index);
        unavailableSegments.UnionWith(persistedMissing);
        unavailableSegments.UnionWith(persistedCorrupt);

        var verifyAll = job.MissingSegmentIds.Length == 0
                        && persistedMissing.Count == 0
                        && persistedCorrupt.Count == 0;

        if (!verifyAll && unavailableSegments.Count == 0)
            return RepairExecutionResult.NotFeasible("No missing or corrupt segments to repair.");

        var unavailableSlices = new HashSet<int>();
        try
        {
            foreach (var index in unavailableSegments)
            {
                foreach (var slice in sliceMap.GlobalSlicesForSegment(index))
                    unavailableSlices.Add(slice);
            }
        }
        catch (OverflowException)
        {
            return RepairExecutionResult.NotFeasible("Segment-to-slice mapping overflowed.");
        }

        if (!verifyAll && unavailableSlices.Count == 0)
            return RepairExecutionResult.NotFeasible("Missing or corrupt segments do not map to PAR2 slices.");

        var maxMissingSlices = _configManager.GetPar2MaxMissingSlices();
        if (unavailableSlices.Count > maxMissingSlices)
        {
            Log.Warning(
                "PAR2 repair targeting {Path} infeasible before discovery. " +
                "Initial={Initial} Discovered={Discovered} Cap={Cap} BytesRead={BytesRead} Elapsed={Elapsed}",
                davItem.Path, unavailableSlices.Count, 0, maxMissingSlices, 0L, TimeSpan.Zero);
            return RepairExecutionResult.NotFeasible(
                $"Missing slice count {unavailableSlices.Count} exceeds cap {maxMissingSlices}.");
        }

        var fetchConcurrency = _configManager.GetPar2FetchConcurrency();
        using var fetchGate = new SemaphoreSlim(fetchConcurrency, fetchConcurrency);
        var bytesRead = 0L;
        var maxMemoryBytes = _configManager.GetPar2MaxMemoryMb() * 1024L * 1024L;
        SetRepairPhase("discovery", maxMemoryBytes);
        long discoverySourceCap;
        try
        {
            // A pass holds one assembled slice while the source retains the segments that
            // overlap it. Keep one further slice of headroom for the caller/reconstructor.
            discoverySourceCap = checked(maxMemoryBytes - (2L * sliceSize));
        }
        catch (OverflowException)
        {
            return RepairExecutionResult.NotFeasible("PAR2 memory cap is too small for its slice size.");
        }

        if (discoverySourceCap <= 0)
            return RepairExecutionResult.NotFeasible("PAR2 memory cap is too small for its slice size.");

        var accessor = new SliceSegmentAccessor(
            segmentIds,
            sliceMap,
            nzbDocument,
            par2Context,
            _usenetClient,
            fetchGate,
            unavailableSlices,
            discoverySourceCap,
            onBytesRead: n =>
            {
                bytesRead += n;
                Interlocked.Add(ref _activeBytesRead, n);
            });
        _activeSource = accessor;
        foreach (var index in persistedMissing)
            accessor.NoteMissing(index);
        foreach (var index in persistedCorrupt)
            accessor.NoteCorrupt(index);

        try
        {
            var initialUnavailableSlices = unavailableSlices.Count;
            var discoveryWatch = Stopwatch.StartNew();
            var exceededCap = await DiscoverUnavailableSourcesAsync(
                    accessor, sliceMap, targetIfsc, unavailableSegments, unavailableSlices,
                    maxMissingSlices, ct)
                .ConfigureAwait(false);
            discoveryWatch.Stop();

            var discoveredMissing = accessor.MissingSegmentIndices.Except(persistedMissing).Except(requested.Select(x => x.Index)).Count();
            var discoveredCorrupt = accessor.CorruptSegmentIndices.Except(persistedCorrupt).Except(requested.Select(x => x.Index)).Count();
            Log.Information(
                "PAR2 repair targeting {Path}: requested={Requested} persistedMissing={PersistedMissing} "
                + "persistedCorrupt={PersistedCorrupt} discoveredMissing={DiscoveredMissing} "
                + "discoveredCorrupt={DiscoveredCorrupt} slices={Slices}",
                davItem.Path,
                requested.Count,
                persistedMissing.Count,
                persistedCorrupt.Count,
                discoveredMissing,
                discoveredCorrupt,
                unavailableSlices.Count);

            if (exceededCap || unavailableSlices.Count > maxMissingSlices)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                Log.Warning(
                    "PAR2 repair targeting {Path} infeasible after discovery. " +
                    "Initial={Initial} Discovered={Discovered} Cap={Cap} BytesRead={BytesRead} Elapsed={Elapsed}",
                    davItem.Path,
                    initialUnavailableSlices,
                    unavailableSlices.Count,
                    maxMissingSlices,
                    bytesRead,
                    discoveryWatch.Elapsed);
                return RepairExecutionResult.NotFeasible(
                    $"Missing slice count {unavailableSlices.Count} exceeds cap {maxMissingSlices}.",
                    bytesRead);
            }

            if (verifyAll && unavailableSlices.Count == 0)
                return RepairExecutionResult.Verified(bytesRead);

            var patchTargets = SegmentsOverlappingSlices(sliceMap, unavailableSlices, segmentIds);
            var stagedPatchBytes = patchTargets.Sum(target => segmentRanges[target.Index].Count);
            long workingSetBytes;
            try
            {
                workingSetBytes = EstimateWorkingSetBytes(
                    EstimateMaxSourceWindowBytes(sliceMap, par2Context, nzbDocument),
                    unavailableSlices.Count,
                    unavailableSlices.Count,
                    stagedPatchBytes,
                    sliceSize);
            }
            catch (OverflowException)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.NotFeasible("PAR2 working-set estimate overflowed.", bytesRead);
            }

            if (workingSetBytes > maxMemoryBytes)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.NotFeasible(
                    $"PAR2 working set {workingSetBytes} bytes exceeds memory cap.",
                    bytesRead);
            }

            Interlocked.Exchange(ref _activeEstimatedWorkingSetBytes, workingSetBytes);
            var releaseBytesCap = _configManager.GetPar2MaxReleaseGb() * 1024L * 1024L * 1024L;
            var releaseBytes = EstimateReleaseBytes(par2Context.Main, par2Context.FileDescsById);
            if (releaseBytes > releaseBytesCap)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.NotFeasible(
                    $"Recovery set size {releaseBytes} bytes exceeds release cap.",
                    bytesRead);
            }

            var missingSliceIndices = unavailableSlices.OrderBy(x => x).ToList();
            SetRepairPhase("recovery-volumes", maxMemoryBytes);
            accessor.SetRetainedByteLimit(checked(maxMemoryBytes - EstimateNonSourceWorkingSetBytes(
                unavailableSlices.Count,
                unavailableSlices.Count,
                stagedPatchBytes,
                sliceSize)));
            var recoverySlices = await CollectRecoverySlicesAsync(
                par2Context.VolumeFiles,
                missingSliceIndices.Count,
                par2Context.Main.SliceSize,
                fetchGate,
                ct,
                onBytesRead: n => bytesRead += n).ConfigureAwait(false);
            if (recoverySlices.Count < missingSliceIndices.Count)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.NotFeasible(
                    $"Need {missingSliceIndices.Count} recovery slices but only collected {recoverySlices.Count}.",
                    bytesRead);
            }

            var reconstructor = new Par2Reconstructor();
            SetRepairPhase("reconstruction", maxMemoryBytes);
            accessor.BeginSequentialPass();
            var reconstruction = await reconstructor.ReconstructAsync(
                par2Context.Main,
                par2Context.FileDescsById,
                par2Context.IfscsByFileId,
                missingSliceIndices,
                recoverySlices,
                (sliceIndex, size, token) => accessor.FetchSliceBytesAsync(sliceIndex, size, token),
                ct).ConfigureAwait(false);

            if (!reconstruction.Success)
            {
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("slice");
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.Failed(
                    reconstruction.FailureReason ?? "Reconstruction failed.",
                    bytesRead);
            }

            if (patchTargets.Count == 0)
            {
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.NotFeasible(
                    "No missing or corrupt segments were confirmed during PAR2 repair.",
                    bytesRead);
            }

            SetRepairPhase("assembling-patches", maxMemoryBytes);
            accessor.BeginSequentialPass();
            var commits = await ExtractSegmentPatchesAsync(
                patchTargets,
                sliceMap,
                reconstruction.ReconstructedSlices,
                accessor,
                davItem.Name,
                fileLength,
                segmentIds.Length,
                ct).ConfigureAwait(false);

            SetRepairPhase("whole-file-verification", maxMemoryBytes);
            accessor.BeginSequentialPass();
            var md5Reason = await TryVerifyWholeFileMd5Async(
                    targetDesc, sliceMap, reconstruction.ReconstructedSlices, accessor, ct)
                .ConfigureAwait(false);
            if (md5Reason is not null)
            {
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("file");
                await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
                return RepairExecutionResult.Failed(md5Reason, bytesRead);
            }

            _patchStore.CommitPatches(
                commits.Select(patch => (patch.SegmentId, patch.Bytes, patch.Header)).ToList());

            SetRepairPhase("committing-patches", maxMemoryBytes);
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            PrometheusMetrics.Current?.AddPar2RepairBytesRead(bytesRead);
            PrometheusMetrics.Current?.AddPar2SlicesReconstructed(reconstruction.ReconstructedSlices.Count);
            PrometheusMetrics.Current?.AddPar2SegmentsCommitted(commits.Count);
            job.MissingSegmentIds = patchTargets.Select(x => x.SegmentId).ToArray();
            return RepairExecutionResult.Succeeded(bytesRead, reconstruction.ReconstructedSlices.Count, commits.Count);
        }
        catch (Par2MemoryCapExceededException e)
        {
            await PersistDiscoveredDamageAsync(davItem, nzbFile, accessor, ct).ConfigureAwait(false);
            return RepairExecutionResult.NotFeasible(e.Message, bytesRead);
        }
    }

    private static async Task<bool> DiscoverUnavailableSourcesAsync(
        SliceSegmentAccessor accessor,
        Par2FileSliceMap sliceMap,
        IfscPacket targetIfsc,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices,
        int maxMissingSlices,
        CancellationToken ct)
    {
        var expanded = true;
        while (expanded)
        {
            ct.ThrowIfCancellationRequested();
            expanded = false;
            accessor.BeginSequentialPass();
            AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices, ref expanded);
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
                AbsorbAccessorDiscoveries(accessor, sliceMap, unavailableSegments, unavailableSlices, ref expanded);
                expanded |= (assembled is null ||
                             !Par2Reconstructor.VerifySliceChecksum(assembled, targetIfsc.Slices[local]))
                            && unavailableSlices.Add(globalSlice);
                if (unavailableSlices.Count > maxMissingSlices)
                    return true;
            }
        }

        return false;
    }

    private static void AbsorbAccessorDiscoveries(
        SliceSegmentAccessor accessor,
        Par2FileSliceMap sliceMap,
        HashSet<int> unavailableSegments,
        HashSet<int> unavailableSlices,
        ref bool expanded)
    {
        foreach (var index in accessor.MissingSegmentIndices
                     .Concat(accessor.CorruptSegmentIndices)
                     .Where(unavailableSegments.Add))
        {
            foreach (var slice in sliceMap.GlobalSlicesForSegment(index).Where(unavailableSlices.Add))
                expanded = true;
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

    private static long EstimateMaxSourceWindowBytes(
        Par2FileSliceMap targetMap,
        Par2SetContext par2,
        NzbDocument nzbDocument)
    {
        var peak = targetMap.EstimateMaxOverlappingSegmentBytes();
        for (var fileIndex = 0; fileIndex < par2.Main.FileIds.Count; fileIndex++)
        {
            if (fileIndex == par2.TargetFileIndex)
                continue;

            var key = Convert.ToHexString(par2.Main.FileIds[fileIndex]);
            if (!par2.FileDescsById.TryGetValue(key, out var desc)
                || !par2.IfscsByFileId.TryGetValue(key, out var ifsc))
            {
                continue;
            }

            var nzbFile = FindContentNzbFile(nzbDocument, desc.FileName);
            var ranges = nzbFile?.GetSegmentByteRanges()
                         ?? (nzbFile is null ? null : TryInferSegmentRanges(nzbFile, (long)desc.FileLength));
            if (ranges is null
                || !Par2FileSliceMap.TryCreate(
                    (long)desc.FileLength,
                    GlobalSliceOffset(fileIndex, par2.Main, par2.IfscsByFileId),
                    (int)par2.Main.SliceSize,
                    ifsc.Slices.Count,
                    ranges,
                    out var map,
                    out _)
                || map is null)
            {
                continue;
            }

            peak = Math.Max(peak, map.EstimateMaxOverlappingSegmentBytes());
        }

        return peak;
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
        SliceSegmentAccessor accessor,
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
        SliceSegmentAccessor accessor,
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
        SliceSegmentAccessor accessor,
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

    private static LongRange[]? TryInferSegmentRanges(NzbFile nzbFile, long fileLength)
    {
        var count = nzbFile.Segments.Count;
        if (count == 0 || fileLength <= 0)
            return null;

        var ranges = new LongRange[count];
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var remaining = fileLength - start;
            if (remaining <= 0)
                return null;
            var size = i == count - 1 ? remaining : nzbFile.Segments[i].Bytes;
            if (size <= 0 || size > remaining)
                return null;
            ranges[i] = LongRange.FromStartAndSize(start, size);
            start += size;
        }

        return start == fileLength ? ranges : null;
    }

    private static List<MissingSegment> ResolveMissingSegments(string[] requestedIds, string[] segmentIds)
    {
        var indexById = segmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        return requestedIds
            .Where(indexById.ContainsKey)
            .Select(id => new MissingSegment(id, indexById[id]))
            .ToList();
    }

    private async Task<List<Par2Reconstructor.RecoverySlice>> CollectRecoverySlicesAsync(
        IReadOnlyList<NzbFile> volumeFiles,
        int needed,
        ulong sliceSize,
        SemaphoreSlim fetchGate,
        CancellationToken ct,
        Action<long>? onBytesRead = null)
    {
        var byExponent = new Dictionary<uint, byte[]>();
        foreach (var volume in volumeFiles)
        {
            if (byExponent.Count >= needed) break;

            await fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var segments = volume.GetSegmentIds();
                var fileSize = await _usenetClient.GetFileSizeAsync(volume, ct).ConfigureAwait(false);
                onBytesRead?.Invoke(fileSize);
                await using var stream = _usenetClient.GetFileStream(segments, fileSize, articleBufferSize: 0);
                while (stream.Position < stream.Length && byExponent.Count < needed)
                {
                    ct.ThrowIfCancellationRequested();
                    Par2Packet packet;
                    try
                    {
                        packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: true, ct)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException e)
                    {
                        PrometheusMetrics.Current?.RecordPar2ValidationFailure("packet");
                        Log.Debug(e, "Skipping invalid PAR2 packet while reading recovery volume {Subject}", volume.Subject);
                        break;
                    }

                    if (packet is RecvSlic recvSlic && recvSlic.Payload.Length == (int)sliceSize
                        && byExponent.TryAdd(recvSlic.Exponent, recvSlic.Payload)
                        && byExponent.Count >= needed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                fetchGate.Release();
            }
        }

        return byExponent
            .OrderBy(x => x.Key)
            .Take(needed)
            .Select(x => new Par2Reconstructor.RecoverySlice(x.Key, x.Value))
            .ToList();
    }

    private async Task<Par2SetContext?> DiscoverPar2SetAsync(
        NzbDocument nzbDocument,
        NzbFile contentNzb,
        string davItemName,
        CancellationToken ct)
    {
        var candidates = nzbDocument.Files
            .Where(IsPar2CandidateSubject)
            .OrderBy(x => x.Segments.Count)
            .ThenBy(x => x.Segments.FirstOrDefault()?.MessageId, StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in candidates.Where(x => !Par2.ParVolume.IsMatch(x.GetSubjectFileName())))
        {
            var context = await TryParsePar2IndexAsync(candidate, contentNzb, davItemName, nzbDocument, ct)
                .ConfigureAwait(false);
            if (context != null) return context;
        }

        if (candidates.Count > 0)
        {
            return await TryParsePar2IndexAsync(candidates[0], contentNzb, davItemName, nzbDocument, ct)
                .ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            if (await HasPar2MagicAsync(candidate, ct).ConfigureAwait(false))
            {
                var context = await TryParsePar2IndexAsync(candidate, contentNzb, davItemName, nzbDocument, ct)
                    .ConfigureAwait(false);
                if (context != null) return context;
            }
        }

        return null;
    }

    private static bool IsPar2CandidateSubject(NzbFile file)
    {
        var name = file.GetSubjectFileName();
        return name.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)
               || Par2.ParVolume.IsMatch(name);
    }

    private async Task<bool> HasPar2MagicAsync(NzbFile file, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return false;
        try
        {
            var response = await _usenetClient.DecodedBodyAsync(file.Segments[0].MessageId, ct)
                .ConfigureAwait(false);
            await using var stream = response.Stream!;
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            return read >= 8 && Par2.HasPar2MagicBytes(buffer);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Debug(e, "PAR2 magic sniff failed for {Subject}", file.Subject);
            return false;
        }
    }

    private async Task<Par2SetContext?> TryParsePar2IndexAsync(
        NzbFile indexFile,
        NzbFile contentNzb,
        string davItemName,
        NzbDocument nzbDocument,
        CancellationToken ct)
    {
        var segments = indexFile.GetSegmentIds();
        if (segments.Length == 0) return null;

        var fileSize = await _usenetClient.GetFileSizeAsync(indexFile, ct).ConfigureAwait(false);
        await using var stream = _usenetClient.GetFileStream(segments, fileSize, articleBufferSize: 0);

        var fileDescs = new Dictionary<string, FileDesc>(StringComparer.Ordinal);
        MainPacket? main = null;
        var ifscs = new Dictionary<string, IfscPacket>(StringComparer.Ordinal);

        while (stream.Position < stream.Length)
        {
            Par2Packet packet;
            try
            {
                packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, readRecvSlicPayload: false, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                PrometheusMetrics.Current?.RecordPar2ValidationFailure("packet");
                break;
            }

            switch (packet)
            {
                case FileDesc fileDesc:
                    fileDescs[Convert.ToHexString(fileDesc.FileID)] = fileDesc;
                    break;
                case MainPacket mainPacket:
                    main = mainPacket;
                    break;
                case IfscPacket ifsc:
                    ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                    break;
                case RecvSlic:
                    goto done;
            }
        }

    done:
        if (main == null || fileDescs.Count == 0 || ifscs.Count == 0)
            return null;

        var targetDesc = fileDescs.Values.FirstOrDefault(desc =>
            string.Equals(desc.FileName, davItemName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(desc.FileName, contentNzb.GetSubjectFileName(), StringComparison.OrdinalIgnoreCase));
        if (targetDesc == null) return null;

        var targetKey = Convert.ToHexString(targetDesc.FileID);
        if (!ifscs.ContainsKey(targetKey)) return null;

        var targetFileIndex = -1;
        for (var i = 0; i < main.FileIds.Count; i++)
        {
            if (Convert.ToHexString(main.FileIds[i]).Equals(targetKey, StringComparison.Ordinal))
            {
                targetFileIndex = i;
                break;
            }
        }

        if (targetFileIndex < 0) return null;

        var volumeFiles = nzbDocument.Files
            .Where(x => x != indexFile)
            .Where(x => IsPar2CandidateSubject(x) || Par2.ParVolume.IsMatch(x.GetSubjectFileName()))
            .ToList();

        return new Par2SetContext(main, fileDescs, ifscs, targetFileIndex, volumeFiles);
    }

    private static NzbFile? FindContentNzbFile(NzbDocument document, string davItemName)
    {
        return document.Files.FirstOrDefault(f =>
                   string.Equals(f.GetSubjectFileName(), davItemName, StringComparison.OrdinalIgnoreCase))
               ?? document.Files.FirstOrDefault(f =>
                   f.GetSubjectFileName().EndsWith(davItemName, StringComparison.OrdinalIgnoreCase));
    }

    private static long EstimateReleaseBytes(
        MainPacket main,
        Dictionary<string, FileDesc> fileDescs)
    {
        return main.FileIds
            .Select(fileId => Convert.ToHexString(fileId))
            .Sum(key => fileDescs.TryGetValue(key, out var desc) ? (long)desc.FileLength : 0L);
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

        if (existing is { Attempts: >= MaxAttempts }
            and ({ State: Par2RepairJob.RepairJobState.Failed } or { State: Par2RepairJob.RepairJobState.Infeasible }))
            return null;

        var segments = missingSegmentIds?.ToArray()
                       ?? existing?.MissingSegmentIds
                       ?? Array.Empty<string>();

        if (existing is { State: Par2RepairJob.RepairJobState.Queued or Par2RepairJob.RepairJobState.Failed or Par2RepairJob.RepairJobState.Infeasible })
        {
            existing.Attempts++;
            existing.MissingSegmentIds = segments;
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

    private sealed class RepairFlight
    {
        public TaskCompletionSource<Par2RepairOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Par2RepairOutcome> Task => Completion.Task;
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
        int TargetFileIndex,
        List<NzbFile> VolumeFiles);

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

    private sealed class SliceSegmentAccessor
    {
        private readonly string[] _segmentIds;
        private readonly Par2FileSliceMap _map;
        private readonly NzbDocument _nzbDocument;
        private readonly Par2SetContext _par2;
        private readonly UsenetStreamingClient _client;
        private readonly SemaphoreSlim _fetchGate;
        private readonly HashSet<int> _targetSlices;
        private readonly Action<long>? _onBytesRead;
        private readonly Dictionary<int, byte[]> _segmentBodies = new();
        private readonly Dictionary<int, SiblingLayout> _siblingLayouts = new();
        private readonly Dictionary<(int FileIndex, int SegmentIndex), byte[]> _siblingSegmentBodies = new();
        private int _activeSiblingFileIndex = -1;
        private readonly HashSet<int> _missingSegmentIndices = new();
        private readonly HashSet<int> _corruptSegmentIndices = new();
        private long _cachedBodyBytes;
        private long _peakCachedBodyBytes;
        private long _retainedByteLimit;

        public SliceSegmentAccessor(
            string[] segmentIds,
            Par2FileSliceMap map,
            NzbDocument nzbDocument,
            Par2SetContext par2,
            UsenetStreamingClient client,
            SemaphoreSlim fetchGate,
            HashSet<int> targetSlices,
            long retainedByteLimit,
            Action<long>? onBytesRead)
        {
            _segmentIds = segmentIds;
            _map = map;
            _nzbDocument = nzbDocument;
            _par2 = par2;
            _client = client;
            _fetchGate = fetchGate;
            _targetSlices = targetSlices;
            _retainedByteLimit = retainedByteLimit;
            _onBytesRead = onBytesRead;
        }

        public IReadOnlyCollection<int> MissingSegmentIndices => _missingSegmentIndices;
        public IReadOnlyCollection<int> CorruptSegmentIndices => _corruptSegmentIndices;
        public long CachedBodyBytes => Interlocked.Read(ref _cachedBodyBytes);
        public long PeakCachedBodyBytes => Interlocked.Read(ref _peakCachedBodyBytes);
        public long RetainedByteLimit => Interlocked.Read(ref _retainedByteLimit);

        public void NoteMissing(int segmentIndex) => _missingSegmentIndices.Add(segmentIndex);

        public void NoteCorrupt(int segmentIndex) => _corruptSegmentIndices.Add(segmentIndex);

        /// <summary>
        /// Starts a sequential pass over source slices. Source bodies are deliberately
        /// not shared across passes: retaining a target file from discovery through
        /// reduction and MD5 verification was the unbounded-memory failure mode.
        /// </summary>
        public void BeginSequentialPass()
        {
            _segmentBodies.Clear();
            _siblingSegmentBodies.Clear();
            _activeSiblingFileIndex = -1;
            Interlocked.Exchange(ref _cachedBodyBytes, 0);
        }

        public void SetRetainedByteLimit(long limit)
        {
            if (limit <= 0)
                throw new InvalidOperationException("PAR2 repair has no memory available for source segments.");
            Interlocked.Exchange(ref _retainedByteLimit, limit);
            if (CachedBodyBytes > limit)
                BeginSequentialPass();
        }

        public async Task<byte[]?> FetchSliceBytesAsync(int globalSliceIndex, int sliceSize, CancellationToken ct)
        {
            var local = globalSliceIndex - _map.GlobalSliceBase;
            if ((uint)local >= (uint)_map.SliceCount)
                return await FetchForeignSliceAsync(globalSliceIndex, sliceSize, ct).ConfigureAwait(false);

            if (_targetSlices.Contains(globalSliceIndex))
                return null;

            var sliceRange = _map.SliceFileRange(globalSliceIndex);
            ClearSiblingBodies();
            EvictPassedTargetSegments(sliceRange.StartInclusive);
            var buffer = new byte[sliceSize];
            var copied = 0;
            foreach (var segmentIndex in _map.SegmentIndicesForGlobalSlice(globalSliceIndex))
            {
                var body = await GetSegmentBodyAsync(segmentIndex, ct).ConfigureAwait(false);
                if (body is null)
                {
                    if (sliceRange.EndExclusive < _map.FileLength)
                        return null;
                    break;
                }

                var segmentRange = _map.SegmentRanges[segmentIndex];
                var intersectStart = Math.Max(sliceRange.StartInclusive, segmentRange.StartInclusive);
                var intersectEnd = Math.Min(sliceRange.EndExclusive, segmentRange.EndExclusive);
                if (intersectEnd <= intersectStart)
                    continue;

                var destOffset = (int)(intersectStart - sliceRange.StartInclusive);
                var srcOffset = (int)(intersectStart - segmentRange.StartInclusive);
                var count = (int)(intersectEnd - intersectStart);
                if (srcOffset < 0 || srcOffset + count > body.Length)
                    return null;
                Buffer.BlockCopy(body, srcOffset, buffer, destOffset, count);
                copied += count;
            }

            if (copied < sliceRange.Count && sliceRange.EndExclusive < _map.FileLength)
                return null;
            return buffer;
        }

        /// <summary>
        /// Fetches a target segment only while assembling a final patch. Most patch
        /// bytes come from reconstructed slices; this covers the untouched portion
        /// of a segment that merely overlaps a bad slice. Callers reset the pass
        /// after each patch so this never becomes a release-sized cache.
        /// </summary>
        public Task<byte[]?> GetSegmentBodyForPatchAsync(int segmentIndex, CancellationToken ct)
        {
            // A known-bad source segment is completely covered by its unavailable
            // slices, so reconstructing it must never retry the broken article.
            // A neighbouring healthy segment can overlap one repaired slice and
            // still supply the untouched bytes around that slice.
            if (_map.GlobalSlicesForSegment(segmentIndex).All(_targetSlices.Contains))
                return Task.FromResult<byte[]?>(null);
            return GetSegmentBodyAsync(segmentIndex, ct);
        }

        private async Task<byte[]?> GetSegmentBodyAsync(int segmentIndex, CancellationToken ct)
        {
            if (_missingSegmentIndices.Contains(segmentIndex) || _corruptSegmentIndices.Contains(segmentIndex))
                return null;
            if (_segmentBodies.TryGetValue(segmentIndex, out var cached))
                return cached;

            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_segmentBodies.TryGetValue(segmentIndex, out cached))
                    return cached;
                if (_missingSegmentIndices.Contains(segmentIndex) || _corruptSegmentIndices.Contains(segmentIndex))
                    return null;

                var segmentId = _segmentIds[segmentIndex];
                try
                {
                    EnsureRetainedCapacity(checked((int)_map.SegmentRanges[segmentIndex].Count));
                    var response = await _client.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
                    await using var stream = response.Stream!;
                    var bytes = await ReadExpectedSegmentBodyAsync(
                            stream, _map.SegmentRanges[segmentIndex].Count, ct)
                        .ConfigureAwait(false);
                    _onBytesRead?.Invoke(bytes.Length);
                    _segmentBodies[segmentIndex] = bytes;
                    AddRetainedBytes(bytes.Length);
                    return bytes;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (e.IsCancellationException(ct))
                        throw;

                    if (e.TryGetCausingException<UsenetArticleNotFoundException>(out _))
                    {
                        _missingSegmentIndices.Add(segmentIndex);
                        return null;
                    }

                    if (e.TryGetCausingException<UsenetCorruptArticleException>(out _))
                    {
                        _corruptSegmentIndices.Add(segmentIndex);
                        return null;
                    }

                    if (e is InvalidDataException or EndOfStreamException)
                    {
                        _corruptSegmentIndices.Add(segmentIndex);
                        return null;
                    }

                    throw;
                }
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        private sealed record SiblingLayout(Par2FileSliceMap Map, string[] SegmentIds);

        private async Task<byte[]?> FetchForeignSliceAsync(int globalSliceIndex, int sliceSize, CancellationToken ct)
        {
            var offset = 0;
            for (var fileIndex = 0; fileIndex < _par2.Main.FileIds.Count; fileIndex++)
            {
                var key = Convert.ToHexString(_par2.Main.FileIds[fileIndex]);
                if (!_par2.IfscsByFileId.TryGetValue(key, out var ifsc))
                    return null;

                var count = ifsc.Slices.Count;
                if (globalSliceIndex >= offset + count)
                {
                    offset += count;
                    continue;
                }

                if (fileIndex == _par2.TargetFileIndex)
                    return null;

                if (!TryGetSiblingLayout(fileIndex, out var layout) || layout is null)
                    return null;

                return await AssembleSiblingSliceAsync(fileIndex, layout, globalSliceIndex, sliceSize, ct)
                    .ConfigureAwait(false);
            }

            return null;
        }

        private bool TryGetSiblingLayout(int fileIndex, out SiblingLayout? layout)
        {
            if (_siblingLayouts.TryGetValue(fileIndex, out layout))
                return true;

            layout = null;
            var key = Convert.ToHexString(_par2.Main.FileIds[fileIndex]);
            if (!_par2.FileDescsById.TryGetValue(key, out var desc)
                || !_par2.IfscsByFileId.TryGetValue(key, out var ifsc))
                return false;

            var nzbFile = FindContentNzbFile(_nzbDocument, desc.FileName);
            if (nzbFile is null || nzbFile.Segments.Count == 0)
                return false;

            var fileLength = (long)desc.FileLength;
            var ranges = nzbFile.GetSegmentByteRanges()
                         ?? Par2RepairService.TryInferSegmentRanges(nzbFile, fileLength);
            if (ranges is null)
                return false;

            var globalBase = GlobalSliceOffset(fileIndex, _par2.Main, _par2.IfscsByFileId);
            if (!Par2FileSliceMap.TryCreate(
                    fileLength,
                    globalBase,
                    (int)_par2.Main.SliceSize,
                    ifsc.Slices.Count,
                    ranges,
                    out var map,
                    out _)
                || map is null)
                return false;

            layout = new SiblingLayout(map, nzbFile.GetSegmentIds());
            _siblingLayouts[fileIndex] = layout;
            return true;
        }

        private async Task<byte[]?> AssembleSiblingSliceAsync(
            int fileIndex,
            SiblingLayout layout,
            int globalSliceIndex,
            int sliceSize,
            CancellationToken ct)
        {
            NoteActiveSiblingFile(fileIndex);
            var sliceRange = layout.Map.SliceFileRange(globalSliceIndex);
            EvictPassedSiblingSegments(fileIndex, layout, sliceRange.StartInclusive);

            var buffer = new byte[sliceSize];
            var copied = 0;
            foreach (var segmentIndex in layout.Map.SegmentIndicesForGlobalSlice(globalSliceIndex))
            {
                var body = await GetSiblingSegmentBodyAsync(fileIndex, layout, segmentIndex, ct)
                    .ConfigureAwait(false);
                if (body is null)
                {
                    if (sliceRange.EndExclusive < layout.Map.FileLength)
                        return null;
                    break;
                }

                var segmentRange = layout.Map.SegmentRanges[segmentIndex];
                var intersectStart = Math.Max(sliceRange.StartInclusive, segmentRange.StartInclusive);
                var intersectEnd = Math.Min(sliceRange.EndExclusive, segmentRange.EndExclusive);
                if (intersectEnd <= intersectStart)
                    continue;

                var destOffset = (int)(intersectStart - sliceRange.StartInclusive);
                var srcOffset = (int)(intersectStart - segmentRange.StartInclusive);
                var count = (int)(intersectEnd - intersectStart);
                if (srcOffset < 0 || srcOffset + count > body.Length)
                    return null;
                Buffer.BlockCopy(body, srcOffset, buffer, destOffset, count);
                copied += count;
            }

            if (copied < sliceRange.Count && sliceRange.EndExclusive < layout.Map.FileLength)
                return null;
            return buffer;
        }

        private async Task<byte[]?> GetSiblingSegmentBodyAsync(
            int fileIndex,
            SiblingLayout layout,
            int segmentIndex,
            CancellationToken ct)
        {
            var cacheKey = (fileIndex, segmentIndex);
            if (_siblingSegmentBodies.TryGetValue(cacheKey, out var cached))
                return cached;

            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_siblingSegmentBodies.TryGetValue(cacheKey, out cached))
                    return cached;

                var segmentId = layout.SegmentIds[segmentIndex];
                try
                {
                    EnsureRetainedCapacity(checked((int)layout.Map.SegmentRanges[segmentIndex].Count));
                    var response = await _client.DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
                    await using var stream = response.Stream!;
                    var bytes = await ReadExpectedSegmentBodyAsync(
                            stream, layout.Map.SegmentRanges[segmentIndex].Count, ct)
                        .ConfigureAwait(false);
                    _onBytesRead?.Invoke(bytes.Length);
                    _siblingSegmentBodies[cacheKey] = bytes;
                    AddRetainedBytes(bytes.Length);
                    return bytes;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (e.IsCancellationException(ct))
                        throw;

                    if (e.TryGetCausingException<UsenetArticleNotFoundException>(out _)
                        || e.TryGetCausingException<UsenetCorruptArticleException>(out _))
                        return null;

                    if (e is InvalidDataException or EndOfStreamException)
                        return null;

                    throw;
                }
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        private void NoteActiveSiblingFile(int fileIndex)
        {
            if (_activeSiblingFileIndex == fileIndex)
                return;
            ClearTargetBodies();
            if (_activeSiblingFileIndex >= 0)
                EvictSiblingFile(_activeSiblingFileIndex);
            _activeSiblingFileIndex = fileIndex;
        }

        private void EvictPassedSiblingSegments(int fileIndex, SiblingLayout layout, long sliceStart)
        {
            for (var i = 0; i < layout.Map.SegmentRanges.Count; i++)
            {
                if (layout.Map.SegmentRanges[i].EndExclusive > sliceStart)
                    continue;
                EvictSiblingSegment(fileIndex, i);
            }
        }

        private void EvictSiblingFile(int fileIndex)
        {
            foreach (var key in _siblingSegmentBodies.Keys.Where(key => key.FileIndex == fileIndex).ToList())
                EvictSiblingSegment(key.FileIndex, key.SegmentIndex);
        }

        private void EvictSiblingSegment(int fileIndex, int segmentIndex)
        {
            if (_siblingSegmentBodies.Remove((fileIndex, segmentIndex), out var body))
                AddRetainedBytes(-body.Length);
        }

        private void EvictPassedTargetSegments(long sliceStart)
        {
            foreach (var segmentIndex in _segmentBodies.Keys
                         .Where(index => _map.SegmentRanges[index].EndExclusive <= sliceStart)
                         .ToArray())
            {
                if (_segmentBodies.Remove(segmentIndex, out var body))
                {
                    AddRetainedBytes(-body.Length);
                }
            }
        }

        private void ClearTargetBodies()
        {
            foreach (var body in _segmentBodies.Values)
                AddRetainedBytes(-body.Length);
            _segmentBodies.Clear();
        }

        private void ClearSiblingBodies()
        {
            foreach (var body in _siblingSegmentBodies.Values)
                AddRetainedBytes(-body.Length);
            _siblingSegmentBodies.Clear();
            _activeSiblingFileIndex = -1;
        }

        private void EnsureRetainedCapacity(int incomingBytes)
        {
            if (incomingBytes < 0 || incomingBytes > RetainedByteLimit - CachedBodyBytes)
            {
                throw new Par2MemoryCapExceededException(
                    $"PAR2 source window needs {incomingBytes:N0} bytes with {CachedBodyBytes:N0} retained, "
                    + $"exceeding the repair cap of {RetainedByteLimit:N0} bytes.");
            }
        }

        private void AddRetainedBytes(long delta)
        {
            var current = Interlocked.Add(ref _cachedBodyBytes, delta);
            if (delta <= 0)
                return;

            long peak;
            while (current > (peak = Interlocked.Read(ref _peakCachedBodyBytes)))
            {
                if (Interlocked.CompareExchange(ref _peakCachedBodyBytes, current, peak) == peak)
                    break;
            }
        }

        private static async Task<byte[]> ReadExpectedSegmentBodyAsync(
            Stream stream,
            long expectedLength,
            CancellationToken ct)
        {
            if (expectedLength < 0 || expectedLength > int.MaxValue)
                throw new InvalidDataException("PAR2 source segment has an unsupported length.");

            var bytes = GC.AllocateUninitializedArray<byte>((int)expectedLength);
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);

            var extra = new byte[1];
            if (await stream.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
                throw new InvalidDataException("PAR2 source segment exceeded its recorded byte range.");

            return bytes;
        }
    }

    private sealed class Par2MemoryCapExceededException(string message) : InvalidOperationException(message);
}
