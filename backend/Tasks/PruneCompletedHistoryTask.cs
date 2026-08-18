using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <remarks>
/// <see cref="BaseTask"/> uses a single static semaphore shared by every subclass, so this task
/// is mutually exclusive with <see cref="RemoveUnlinkedFilesTask"/> and other maintenance tasks.
/// </remarks>
public class PruneCompletedHistoryTask : BaseTask
{
    private const int BatchSize = 100;
    private static readonly TimeSpan DefaultProgressHeartbeatInterval = TimeSpan.FromSeconds(2);
    private readonly WebsocketManager _websocketManager;
    private readonly bool _isDryRun;
    private readonly string? _category;
    private readonly int? _olderThanDays;
    private readonly Func<DavDatabaseContext>? _createContext;
    private readonly TimeSpan _progressHeartbeatInterval;
    private readonly Action<string>? _progressObserver;
    private ProgressHeartbeat? _progressHeartbeat;

    public PruneCompletedHistoryTask(WebsocketManager websocketManager, bool isDryRun, string? category = null, int? olderThanDays = null)
        : this(websocketManager, isDryRun, category, olderThanDays, null) { }

    internal PruneCompletedHistoryTask(WebsocketManager websocketManager, bool isDryRun, string? category, int? olderThanDays,
        Func<DavDatabaseContext>? createContext, TimeSpan? progressHeartbeatInterval = null, Action<string>? progressObserver = null)
    {
        _websocketManager = websocketManager;
        _isDryRun = isDryRun;
        _category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        _olderThanDays = olderThanDays is > 0 ? olderThanDays : null;
        _createContext = createContext;
        _progressHeartbeatInterval = progressHeartbeatInterval ?? DefaultProgressHeartbeatInterval;
        _progressObserver = progressObserver;
    }

    private DavDatabaseContext CreateContext() => DavDatabaseContexts.Create(_createContext);

    protected override async Task ExecuteInternal()
    {
        await using var progressHeartbeat = new ProgressHeartbeat(Report, _progressHeartbeatInterval);
        _progressHeartbeat = progressHeartbeat;
        try { await PruneCompletedHistory().ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Complete($"Failed: {e.Message}");
            if (TryGetKnownFailureReason(e, out var reason))
            { Log.Warning("Could not prune completed history. Reason: {Reason}", reason); Log.Debug(e, "Prune completed history known failure stack"); }
            else Log.Error(e, "Failed to prune completed history.");
        }
        finally { _progressHeartbeat = null; }
    }

    private async Task PruneCompletedHistory()
    {
        await using var dbContext = CreateContext();
        var dbClient = new DavDatabaseClient(dbContext);
        StartPhase("Counting completed history items...");
        var totalCount = await CountMatchingItemsAsync(dbContext).ConfigureAwait(false);
        UpdatePhase($"Counting completed history items...\nFound {totalCount} item(s) to prune.");
        if (totalCount == 0)
        {
            Complete(_isDryRun ? "Done. Identified 0 completed history items." : "Done. Pruned 0 completed history items.");
            return;
        }
        if (_isDryRun)
        {
            StartPhase("Identifying completed history items...");
            Complete($"Done. Identified {await DryRunIdentifyAsync(dbContext).ConfigureAwait(false)} completed history item(s).");
            return;
        }
        StartPhase("Pruning completed history items...");
        Complete($"Done. Pruned {await PruneBatchesAsync(dbClient).ConfigureAwait(false)} completed history item(s).");
    }

    internal static IQueryable<HistoryItem> BuildFilterQuery(DavDatabaseContext dbContext, string? category, int? olderThanDays)
    {
        var query = dbContext.HistoryItems.AsNoTracking().Where(h => h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(h => h.Category == category);
        if (olderThanDays is > 0)
        {
            var days = olderThanDays.Value;
            query = query.Where(h => h.CreatedAt < DateTime.UtcNow.AddDays(-days));
        }
        return query;
    }

    private Task<int> CountMatchingItemsAsync(DavDatabaseContext dbContext) =>
        BuildFilterQuery(dbContext, _category, _olderThanDays).CountAsync(CancellationToken);

    private async Task<int> DryRunIdentifyAsync(DavDatabaseContext dbContext)
    {
        var identified = 0; var lastId = Guid.Empty;
        while (true)
        {
            var filterQuery = BuildFilterQuery(dbContext, _category, _olderThanDays);
            if (lastId != Guid.Empty) filterQuery = filterQuery.Where(h => h.Id > lastId);
            var batch = await filterQuery.OrderBy(h => h.Id).Select(h => h.Id).Take(BatchSize).ToListAsync(CancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) break;
            identified += batch.Count; lastId = batch[^1];
            UpdatePhase($"Identifying completed history items...\nFound {identified}...");
        }
        return identified;
    }

    private async Task<int> PruneBatchesAsync(DavDatabaseClient dbClient)
    {
        var pruned = 0; string? lastStuckBatchKey = null;
        while (true)
        {
            var ids = await BuildFilterQuery(dbClient.Ctx, _category, _olderThanDays).OrderBy(h => h.CreatedAt).Select(h => h.Id).Take(BatchSize).ToListAsync(CancellationToken).ConfigureAwait(false);
            if (ids.Count == 0) break;
            var existingCount = await dbClient.Ctx.HistoryItems.CountAsync(h => ids.Contains(h.Id), CancellationToken).ConfigureAwait(false);
            await dbClient.RemoveHistoryItemsAsync(
                    ids, deleteFiles: false, source: "prune-completed-history", ct: CancellationToken)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
            dbClient.Ctx.ChangeTracker.Clear();
            var remainingCount = await dbClient.Ctx.HistoryItems.CountAsync(h => ids.Contains(h.Id), CancellationToken).ConfigureAwait(false);
            if (remainingCount == existingCount && existingCount > 0)
            {
                var batchKey = string.Join(",", ids);
                if (batchKey == lastStuckBatchKey)
                    throw new InvalidOperationException($"selected {ids.Count} completed history items but pruned 0 twice; aborting to avoid an infinite loop.");
                lastStuckBatchKey = batchKey; continue;
            }
            lastStuckBatchKey = null; pruned += existingCount - remainingCount;
            UpdatePhase($"Pruning completed history items...\nPruned {pruned}...");
        }
        return pruned;
    }

    private bool TryGetKnownFailureReason(Exception exception, out string reason)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.IsCancellationException(CancellationToken)) { reason = "nzbdav is shutting down"; return true; }
            // SQLITE_BUSY/LOCKED/READONLY/FULL or PostgreSQL serialization failure /
            // deadlock / lock timeout — transient contention the next sweep retries.
            if (current.IsTransientDatabaseException()) { reason = current.Message; return true; }
        }
        reason = string.Empty; return false;
    }

    private void StartPhase(string message) => _progressHeartbeat?.StartPhase(message);
    private void UpdatePhase(string message) => _progressHeartbeat?.UpdatePhase(message);
    private void Complete(string message) { if (_progressHeartbeat is not null) _progressHeartbeat.Complete(message); else Report(message); }
    private void Report(string message) { var progress = $"{(_isDryRun ? "Dry Run - " : string.Empty)}{message}"; _progressObserver?.Invoke(progress); _ = _websocketManager.SendMessage(WebsocketTopic.PruneCompletedHistoryTaskProgress, progress); }

    internal sealed class ProgressHeartbeat : IAsyncDisposable
    {
        private readonly object _sync = new(); private readonly Action<string> _report; private readonly TimeSpan _interval; private readonly Timer _timer;
        private string? _message; private long _runStartedAt; private bool _completed; private bool _disposed;
        public ProgressHeartbeat(Action<string> report, TimeSpan interval)
        { _report = report; _interval = interval; _timer = new Timer(static s => ((ProgressHeartbeat)s!).ReportHeartbeat(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        public void StartPhase(string message) { lock (_sync) { if (_disposed || _completed) return; if (_runStartedAt == 0) _runStartedAt = Stopwatch.GetTimestamp(); _message = message; ReportWithElapsed(); _timer.Change(_interval, _interval); } }
        public void UpdatePhase(string message) { lock (_sync) { if (_disposed || _completed) return; _message = message; ReportWithElapsed(); } }
        public void Complete(string message) { lock (_sync) { if (_disposed || _completed) return; _completed = true; _message = null; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); _report(message); } }
        private void ReportHeartbeat() { lock (_sync) { if (_disposed || _message is null) return; ReportWithElapsed(); } }
        private void ReportWithElapsed() { if (_message is null) return; if (_runStartedAt == 0) _runStartedAt = Stopwatch.GetTimestamp(); _report($"{_message}\nElapsed: {FormatElapsed(Stopwatch.GetElapsedTime(_runStartedAt))}"); }
        private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{Math.Max(1, (int)elapsed.TotalSeconds)}s";
        public async ValueTask DisposeAsync() { lock (_sync) { if (_disposed) return; _disposed = true; _message = null; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } await _timer.DisposeAsync().ConfigureAwait(false); }
    }
}
