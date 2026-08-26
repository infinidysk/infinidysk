using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Polls enabled Radarr/Sonarr instances for import handoff telemetry. Completely
/// dormant (no timer, no HTTP, no DB, no logs) unless Arr Health is enabled and at
/// least one instance is enabled.
/// </summary>
public sealed class ArrHealthService : BackgroundService
{
    internal const int MaxHistoryPages = 5;
    internal const int HistoryPageSize = 100;
    internal const int MaxAwaitingPerInstance = 100;
    internal const int OfflineFailureThreshold = 2;
    internal const int ImportEventType = 3;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> WakeConfigKeys =
    [
        ConfigKeys.ArrInstances,
        ConfigKeys.ArrHealthEnabled,
    ];

    private readonly ConfigManager _configManager;
    private readonly ArrInstanceBackoff _backoff;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly Dictionary<string, ArrHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    internal Func<string, ArrConfig.ConnectionDetails, ArrClient> ClientFactory { get; set; } = CreateClient;
    internal Func<MetricsDbContext> MetricsContextFactory { get; set; } = static () => new MetricsDbContext();
    internal Func<DavDatabaseContext> DavContextFactory { get; set; }
    internal int CycleAttempts => _cycleAttempts;
    private int _cycleAttempts;

    public ArrHealthService(
        ConfigManager configManager,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null,
        ArrInstanceBackoff? backoff = null)
    {
        _configManager = configManager;
        _backoff = backoff ?? new ArrInstanceBackoff();
        DavContextFactory = dbContextFactory is null
            ? static () => new DavDatabaseContext()
            : dbContextFactory.CreateDbContext;
        _configManager.OnConfigChanged += OnConfigChanged;
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _cycleGate.Dispose();
        base.Dispose();
    }

    public IReadOnlyList<ArrHealthSnapshot> GetSnapshots()
    {
        lock (_snapshotLock)
            return _snapshots.Values.ToList();
    }

    internal void ReplaceSnapshotsForTests(IEnumerable<ArrHealthSnapshot> snapshots)
    {
        lock (_snapshotLock)
        {
            _snapshots.Clear();
            foreach (var snapshot in snapshots)
                _snapshots[snapshot.InstanceKey] = snapshot;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!IsActive())
                {
                    try
                    {
                        await CurrentWake.WaitAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                var wakeBeforeCycle = CurrentWake;
                await TryRunCycleAsync(stoppingToken).ConfigureAwait(false);
                if (wakeBeforeCycle.IsCompleted) continue;

                try
                {
                    var delay = Task.Delay(PollInterval, stoppingToken);
                    await Task.WhenAny(CurrentWake, delay).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    internal async Task<bool> TryRunCycleAsync(CancellationToken ct)
    {
        if (!_cycleGate.Wait(0, CancellationToken.None))
            return false;

        try
        {
            Interlocked.Increment(ref _cycleAttempts);
            await RunCycleAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private bool IsActive()
    {
        return _configManager.IsArrHealthEnabled()
               && _configManager.GetArrConfig().GetEnabledInstances().Any();
    }

    private Task CurrentWake => Volatile.Read(ref _wake).Task;

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (!e.ChangedConfig.Keys.Any(WakeConfigKeys.Contains)) return;
        var previous = Interlocked.Exchange(
            ref _wake,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        if (!_configManager.IsArrHealthEnabled())
        {
            PruneSnapshots([]);
            return;
        }

        var instances = _configManager.GetArrConfig().GetEnabledInstances().ToList();
        if (instances.Count == 0)
        {
            PruneSnapshots([]);
            return;
        }

        await Task.WhenAll(instances.Select(instance =>
            PollInstanceAsync(instance.AppType, instance.Details, ct))).ConfigureAwait(false);

        var enabledKeys = instances
            .Select(i => ArrConfig.MakeInstanceKey(i.AppType, i.Details.Host))
            .ToHashSet(StringComparer.Ordinal);
        PruneSnapshots(enabledKeys);
    }

    private async Task PollInstanceAsync(string appType, ArrConfig.ConnectionDetails details, CancellationToken ct)
    {
        var key = ArrConfig.MakeInstanceKey(appType, details.Host);
        var displayName = string.IsNullOrWhiteSpace(details.Name) ? details.Host : details.Name;

        // Skip a host that is timing out or refusing connections until its backoff
        // elapses — polling a dying peer on the fixed cadence only adds load it cannot
        // serve. The last-known snapshot stays in place so the UI keeps showing it.
        if (_backoff.IsInBackoff(details.Host))
        {
            Log.Debug(
                "Arr health poll for {Host} skipped; instance is in backoff for {Remaining}",
                details.Host,
                _backoff.GetRemainingBackoff(details.Host));
            return;
        }

        ArrClient client;
        try
        {
            client = ClientFactory(appType, details);
        }
        catch (Exception e) when (e is not OutOfMemoryException && e is not OperationCanceledException)
        {
            RecordFailure(key, appType, details.Host, displayName, e);
            return;
        }

        try
        {
            var queueStatus = await CallAsync(callCt => client.GetQueueStatusAsync(callCt), ct).ConfigureAwait(false);
            var queue = await CallAsync(callCt => client.GetQueueAsync(callCt), ct).ConfigureAwait(false);

            await using var dav = DavContextFactory();
            await using var metrics = MetricsContextFactory();

            var awaiting = await BuildAwaitingAsync(queue.Records, dav, ct).ConfigureAwait(false);
            await IngestHistoryAsync(key, client, dav, metrics, ct).ConfigureAwait(false);

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cutoff30d = nowMs - (long)ArrHealthMath.MedianWindow.TotalMilliseconds;
            var handoffs = await metrics.ArrImportEvents
                .Where(e => e.InstanceKey == key && e.HandoffMs != null && e.ImportedAtMs >= cutoff30d)
                .Select(e => e.HandoffMs!.Value)
                .ToListAsync(ct).ConfigureAwait(false);
            var median = ArrHealthMath.Percentile(handoffs, 0.50);
            var lastImport = await metrics.ArrImportEvents
                .Where(e => e.InstanceKey == key)
                .Select(e => (long?)e.ImportedAtMs)
                .MaxAsync(ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var unusual = awaiting.Items.Any(item =>
                ArrHealthMath.IsUnusual(
                    ArrHealthMath.ComputeWaitingMs(item.CreatedAt, now),
                    median,
                    handoffs.Count));

            var hasWarnings = queueStatus.Warnings || queueStatus.UnknownWarnings;
            var hasErrors = queueStatus.Errors || queueStatus.UnknownErrors;
            var status = hasWarnings || hasErrors || unusual
                ? ArrInstanceHealthStatus.Degraded
                : ArrInstanceHealthStatus.Healthy;

            _backoff.RecordSuccess(details.Host);
            RecordSuccess(new ArrHealthSnapshot
            {
                InstanceKey = key,
                DisplayName = displayName,
                AppType = appType,
                Host = details.Host,
                Status = status,
                QueueCount = queueStatus.TotalCount,
                AwaitingCount = awaiting.TotalCount,
                HasWarnings = hasWarnings,
                HasErrors = hasErrors,
                LastImportAtMs = lastImport,
                LastPolledAt = now,
                LastError = null,
                MedianHandoffMs30d = median,
                MedianSampleCount30d = handoffs.Count,
                Awaiting = awaiting.Items,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown — do not log or mark Offline
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.LogWarningKnownOrStack("Arr health poll failed for {Host}", details.Host);
            _backoff.RecordFailure(details.Host, e);
            RecordFailure(key, appType, details.Host, displayName, e);
        }
    }

    private static async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerCallTimeout);
        return await call(timeout.Token).ConfigureAwait(false);
    }

    private async Task IngestHistoryAsync(
        string instanceKey,
        ArrClient client,
        DavDatabaseContext dav,
        MetricsDbContext metrics,
        CancellationToken ct)
    {
        var cursor = await metrics.ArrImportEvents
            .Where(e => e.InstanceKey == instanceKey)
            .Select(e => (int?)e.ArrRecordId)
            .MaxAsync(ct).ConfigureAwait(false) ?? 0;

        for (var page = 1; page <= MaxHistoryPages; page++)
        {
            var history = await CallAsync(
                callCt => client.GetImportHistoryAsync(page, HistoryPageSize, callCt),
                ct).ConfigureAwait(false);
            var records = history.Records ?? [];
            if (records.Count == 0) break;

            var newRecords = records.Where(r => r.Id > cursor && r.EventType == ImportEventType).ToList();
            if (newRecords.Count == 0) break;

            await InsertNewEventsAsync(instanceKey, newRecords, dav, metrics, ct).ConfigureAwait(false);

            if (records.Any(r => r.Id <= cursor)) break;
        }
    }

    private static async Task InsertNewEventsAsync(
        string instanceKey,
        List<ArrHistoryRecord> records,
        DavDatabaseContext dav,
        MetricsDbContext metrics,
        CancellationToken ct)
    {
        var candidates = records
            .Select(record => (Record: record, DownloadId: Guid.TryParse(record.DownloadId, out var id) ? id : (Guid?)null))
            .Where(candidate => candidate.DownloadId is not null)
            .Select(candidate => (Record: candidate.Record, DownloadId: candidate.DownloadId!.Value))
            .ToList();
        var downloadIds = candidates.Select(candidate => candidate.DownloadId).ToList();

        if (candidates.Count == 0) return;

        var historyById = await dav.HistoryItems
            .AsNoTracking()
            .Where(h => downloadIds.Contains(h.Id) && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .ToDictionaryAsync(h => h.Id, ct).ConfigureAwait(false);

        var toInsert = new List<ArrImportEvent>();
        foreach (var (record, downloadId) in candidates)
        {
            historyById.TryGetValue(downloadId, out var historyItem);
            toInsert.Add(new ArrImportEvent
            {
                InstanceKey = instanceKey,
                ArrRecordId = record.Id,
                DownloadId = downloadId,
                ImportedAtMs = record.Date.ToUnixTimeMilliseconds(),
                HandoffMs = ArrHealthMath.ComputeHandoffMs(record.Date, historyItem?.CreatedAt),
                Title = record.SourceTitle,
            });
        }

        await SaveNewEventsAsync(metrics, toInsert, ct).ConfigureAwait(false);
    }

    private static async Task SaveNewEventsAsync(
        MetricsDbContext metrics,
        List<ArrImportEvent> toInsert,
        CancellationToken ct)
    {
        metrics.ArrImportEvents.AddRange(toInsert);
        try
        {
            await metrics.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e.IsUniqueConstraintException())
        {
            metrics.ChangeTracker.Clear();
            foreach (var item in toInsert)
            {
                metrics.ArrImportEvents.Add(item);
                try
                {
                    await metrics.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception inner) when (inner.IsUniqueConstraintException())
                {
                    metrics.ChangeTracker.Clear();
                }
            }
        }
    }

    private static async Task<AwaitingBuildResult> BuildAwaitingAsync(
        IReadOnlyList<ArrQueueRecord> records,
        DavDatabaseContext dav,
        CancellationToken ct)
    {
        var allAwaitingRecords = records.Where(r => r.IsAwaitingImport).ToList();
        if (allAwaitingRecords.Count == 0) return new AwaitingBuildResult(0, []);

        // Correlate the full import-pending population first: Arr does not
        // guarantee queue ordering, so truncating before we know CreatedAt can
        // hide the oldest stuck imports.
        var awaitingRecords = allAwaitingRecords;

        var downloadIds = awaitingRecords
            .Select(record => Guid.TryParse(record.DownloadId, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        Dictionary<Guid, DateTime> createdAtById = [];
        if (downloadIds.Count > 0)
        {
            createdAtById = await dav.HistoryItems
                .AsNoTracking()
                .Where(h => downloadIds.Contains(h.Id))
                .Select(h => new { h.Id, h.CreatedAt })
                .ToDictionaryAsync(h => h.Id, h => h.CreatedAt, ct).ConfigureAwait(false);
        }

        var items = awaitingRecords.Select(record =>
        {
            Guid? downloadId = Guid.TryParse(record.DownloadId, out var parsed) ? parsed : null;
            DateTime? createdAt = downloadId is { } id && createdAtById.TryGetValue(id, out var at) ? at : null;
            return new ArrAwaitingSnapshot
            {
                Title = record.Title,
                DownloadId = downloadId,
                CreatedAt = createdAt,
                TrackedDownloadState = record.TrackedDownloadState,
                StatusReason = record.StatusMessages
                    .SelectMany(message => message.Messages)
                    .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)),
            };
        })
        .OrderBy(item => item.CreatedAt ?? DateTime.MaxValue)
        .Take(MaxAwaitingPerInstance)
        .ToList();

        return new AwaitingBuildResult(allAwaitingRecords.Count, items);
    }

    private sealed record AwaitingBuildResult(int TotalCount, List<ArrAwaitingSnapshot> Items);

    private void RecordSuccess(ArrHealthSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _consecutiveFailures[snapshot.InstanceKey] = 0;
            _snapshots[snapshot.InstanceKey] = snapshot;
        }
    }

    private void RecordFailure(
        string key,
        string appType,
        string host,
        string displayName,
        Exception exception)
    {
        lock (_snapshotLock)
        {
            _consecutiveFailures.TryGetValue(key, out var failures);
            failures++;
            _consecutiveFailures[key] = failures;
            _snapshots.TryGetValue(key, out var previous);
            var status = failures >= OfflineFailureThreshold
                ? ArrInstanceHealthStatus.Offline
                : previous?.Status ?? ArrInstanceHealthStatus.Pending;
            _snapshots[key] = new ArrHealthSnapshot
            {
                InstanceKey = key,
                DisplayName = displayName,
                AppType = appType,
                Host = host,
                Status = status,
                QueueCount = previous?.QueueCount ?? 0,
                AwaitingCount = previous?.AwaitingCount ?? 0,
                HasWarnings = previous?.HasWarnings ?? false,
                HasErrors = previous?.HasErrors ?? false,
                LastImportAtMs = previous?.LastImportAtMs,
                LastPolledAt = DateTimeOffset.UtcNow,
                LastError = exception.TryGetKnownErrorMessage(out var reason) ? reason : exception.Message,
                MedianHandoffMs30d = previous?.MedianHandoffMs30d,
                MedianSampleCount30d = previous?.MedianSampleCount30d ?? 0,
                Awaiting = previous?.Awaiting ?? [],
            };
        }
    }

    private void PruneSnapshots(HashSet<string> enabledKeys)
    {
        lock (_snapshotLock)
        {
            var stale = _snapshots.Keys.Where(k => !enabledKeys.Contains(k)).ToList();
            foreach (var key in stale)
            {
                _snapshots.Remove(key);
                _consecutiveFailures.Remove(key);
            }
        }
    }

    private static ArrClient CreateClient(string appType, ArrConfig.ConnectionDetails details) =>
        appType == "radarr"
            ? new RadarrClient(details.Host, details.ApiKey)
            : new SonarrClient(details.Host, details.ApiKey);
}
