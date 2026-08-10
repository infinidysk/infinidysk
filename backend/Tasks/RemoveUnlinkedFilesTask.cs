using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RemoveUnlinkedFilesTask : BaseTask
{
    private static readonly TimeSpan DefaultProgressHeartbeatInterval = TimeSpan.FromSeconds(2);
    private static List<string> _allRemovedPaths = [];
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly bool _isDryRun;
    private readonly Func<DavDatabaseContext>? _createContext;
    private readonly TimeSpan _progressHeartbeatInterval;
    private readonly Action<string>? _progressObserver;
    private ProgressHeartbeat? _progressHeartbeat;

    internal record UnlinkedItemInfo(string Id, int Type, string Path);

    public RemoveUnlinkedFilesTask(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        bool isDryRun)
        : this(configManager, websocketManager, isDryRun, null)
    {
    }

    internal RemoveUnlinkedFilesTask(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        bool isDryRun,
        Func<DavDatabaseContext>? createContext,
        TimeSpan? progressHeartbeatInterval = null,
        Action<string>? progressObserver = null)
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _isDryRun = isDryRun;
        _createContext = createContext;
        _progressHeartbeatInterval = progressHeartbeatInterval ?? DefaultProgressHeartbeatInterval;
        _progressObserver = progressObserver;
    }

    private DavDatabaseContext CreateContext() => DavDatabaseContexts.Create(_createContext);

    protected override async Task ExecuteInternal()
    {
        await using var progressHeartbeat = new ProgressHeartbeat(Report, _progressHeartbeatInterval);
        _progressHeartbeat = progressHeartbeat;
        try
        {
            await RemoveUnlinkedFiles().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Complete($"Failed: {e.Message}");
            if (TryGetKnownFailureReason(e, out var reason))
            {
                Log.Warning("Could not remove unlinked files. Reason: {Reason}", reason);
                Log.Debug(e, "Remove unlinked files known failure stack");
            }
            else
            {
                Log.Error(e, "Failed to remove unlinked files.");
            }
        }
        finally
        {
            _progressHeartbeat = null;
        }
    }

    /// <summary>
    /// Environmental failures an operator can act on from a single log line: a database that is
    /// busy, locked, read-only or full, and shutdown mid-run. Everything else keeps its stack so
    /// genuine bugs and corruption stay diagnosable.
    /// </summary>
    private bool TryGetKnownFailureReason(Exception exception, out string reason)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.IsCancellationException(CancellationToken))
            {
                reason = "nzbdav is shutting down";
                return true;
            }

            // SQLITE_BUSY, SQLITE_LOCKED, SQLITE_READONLY, SQLITE_FULL
            if (current is SqliteException { SqliteErrorCode: 5 or 6 or 8 or 13 })
            {
                reason = current.Message;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private async Task RemoveUnlinkedFiles()
    {
        // get linked file paths
        StartPhase("Scanning all linked files...");
        var startTime = DateTime.Now;
        var linkedIdCount = await WriteLinkedIdsToTable().ConfigureAwait(false);
        if (linkedIdCount < 5)
        {
            _allRemovedPaths.Clear();
            Complete(
                "Aborted: There are less than five linked files found in your library. " +
                "Cancelling operation to prevent accidental bulk deletion.");
            return;
        }

        StartPhase("Searching for unlinked webdav items...");
        var unlinkedItems = await CountUnlinkedItems(startTime).ConfigureAwait(false);
        UpdatePhase(
            $"Searching for unlinked webdav items...\nFound {unlinkedItems} webdav items to remove.");

        // The `linkedIdCount < 5` check above only catches a COMPLETELY empty scan. A library
        // dir that is partially mounted, or pointed at the wrong path, can still expose a
        // handful of symlinks and sail past it -- and then nearly every webdav item looks
        // unlinked. Refuse to delete an implausible share of the deletable population.
        // A healthy library here sits around 31% unlinked (samples, nfos, unimported extras),
        // so 90% leaves wide headroom while still catching a broken scan.
        var deletableItems = await CountDeletableItems(startTime).ConfigureAwait(false);
        var extremeUnlinkedRatio = deletableItems > 0 && unlinkedItems > deletableItems * 0.9;
        if (extremeUnlinkedRatio)
        {
            var percent = 100.0 * unlinkedItems / deletableItems;
            var detail =
                $"{unlinkedItems} of {deletableItems} webdav items appear unlinked ({percent:F0}%). " +
                $"That usually means the library directory is missing, unmounted, or misconfigured " +
                $"rather than that the items are orphaned.";

            if (!_isDryRun)
            {
                _allRemovedPaths.Clear();
                Complete($"Aborted: {detail} Cancelling to prevent accidental bulk deletion. " +
                         "Run a dry-run to inspect if this is genuinely expected.");
                return;
            }

            UpdatePhase($"Warning: {detail} A non-dry-run would abort.");
        }

        if (_isDryRun)
        {
            StartPhase("Identifying unlinked files...");
            await DryRunIdentifyUnlinkedFiles(startTime).ConfigureAwait(false);
            Complete($"Done. Identified {_allRemovedPaths.Count} unlinked files.");
        }
        else
        {
            await RemoveUnlinkedItems(startTime, unlinkedItems).ConfigureAwait(false);
            await RemoveEmptyDirectories(startTime).ConfigureAwait(false);
            Complete($"Done. Removed {_allRemovedPaths.Count} unlinked files.");
        }
    }

    private async Task<int> WriteLinkedIdsToTable()
    {
        await using var dbContext = CreateContext();

        // Create a new table "TMP_LINKED_FILES", dropping old one if it already exists.
        // No index initially for fast writes.
        // TMP_LINKED_FILES_UNIQUE is dropped too: the indexing step below commits each
        // statement separately, so a failure between its CREATE and RENAME strands the
        // unique table and every later run would fail on "table already exists".
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS TMP_LINKED_FILES;
            DROP TABLE IF EXISTS TMP_LINKED_FILES_UNIQUE;
            CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL);
            """).ConfigureAwait(false);

        var scannedCount = 0;
        var batches = GetLinkedIds().ToBatches(100);
        foreach (var batch in batches)
        {
            await InsertLinkedIdBatchAsync(dbContext, batch).ConfigureAwait(false);
            scannedCount += batch.Count;
        }

        // Remove duplicates and add primary key index.
        // COLLATE NOCASE on the column (not predicates) so the PK remains seekable while
        // matching uppercase TMP_LINKED_FILES ids to lowercase migration-seeded DavItems.Id.
        StartPhase($"Indexing {scannedCount} linked files...");
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE TMP_LINKED_FILES_UNIQUE (Id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY);
            INSERT OR IGNORE INTO TMP_LINKED_FILES_UNIQUE (Id) SELECT Id FROM TMP_LINKED_FILES;
            DROP TABLE TMP_LINKED_FILES;
            ALTER TABLE TMP_LINKED_FILES_UNIQUE RENAME TO TMP_LINKED_FILES;
            """).ConfigureAwait(false);

        // Guard uses distinct dav-item ids, not raw symlink/strm count (many links can
        // point at the same item and otherwise sail past the < 5 safety check).
        return await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM TMP_LINKED_FILES")
            .FirstAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parameterized multi-row INSERT so large libraries do not pay one round-trip per id.
    /// </summary>
    internal static async Task InsertLinkedIdBatchAsync(
        DavDatabaseContext dbContext,
        IReadOnlyList<Guid> batch,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
            return;

        var parameters = new SqliteParameter[batch.Count];
        var valueSql = new string[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var name = $"@p{i}";
            valueSql[i] = $"({name})";
            parameters[i] = new SqliteParameter(name, batch[i].ToString().ToUpperInvariant());
        }

        // Parameter names are generated locally (@p0..@pN); values are bound via SqliteParameter.
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync(
            $"INSERT INTO TMP_LINKED_FILES (Id) VALUES {string.Join(",", valueSql)}",
            parameters.AsEnumerable(),
            cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Deletes DavItems by the exact Id text returned from a raw SELECT. Going through
    /// Guid.Parse + ExecuteDelete re-serializes Ids as uppercase, which silently misses
    /// rows stored lowercase (e.g. the folder seeded by the Fix-Empty-Categories migration).
    /// </summary>
    private static async Task<int> DeleteItemsByIdTextAsync(
        DavDatabaseContext dbContext,
        List<UnlinkedItemInfo> items,
        CancellationToken cancellationToken = default)
    {
        var parameters = new SqliteParameter[items.Count];
        var placeholders = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var name = $"@p{i}";
            placeholders[i] = name;
            parameters[i] = new SqliteParameter(name, items[i].Id);
        }

        // Placeholder names are generated locally (@p0..@pN); values are bound via SqliteParameter.
#pragma warning disable EF1002
        return await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM DavItems WHERE Id IN ({string.Join(",", placeholders)})",
            parameters.AsEnumerable(),
            cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Deletes empty directories by Id, re-checking emptiness in the same statement so a
    /// concurrent insert under a selected folder cannot race into a cascade delete.
    /// </summary>
    internal static async Task<int> DeleteEmptyDirectoriesByIdTextAsync(
        DavDatabaseContext dbContext,
        IReadOnlyList<UnlinkedItemInfo> items,
        CancellationToken cancellationToken = default)
    {
        var parameters = new SqliteParameter[items.Count];
        var placeholders = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var name = $"@p{i}";
            placeholders[i] = name;
            parameters[i] = new SqliteParameter(name, items[i].Id);
        }

        // Placeholder names are generated locally (@p0..@pN); values are bound via SqliteParameter.
#pragma warning disable EF1002
        return await dbContext.Database.ExecuteSqlRawAsync(
            $"""
             DELETE FROM DavItems
             WHERE Id IN ({string.Join(",", placeholders)})
               AND NOT EXISTS (
                   SELECT 1 FROM DavItems c WHERE c.ParentId = DavItems.Id
               )
             """,
            parameters.AsEnumerable(),
            cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    private IEnumerable<Guid> GetLinkedIds()
    {
        var linkedIds = OrganizedLinksUtil
            .GetLibraryDavItemLinks(_configManager)
            .Select(x => x.DavItemId);

        var count = 0;
        var lastProgressAt = Stopwatch.GetTimestamp();
        foreach (var linkedId in linkedIds)
        {
            count++;
            if (Stopwatch.GetElapsedTime(lastProgressAt) >= TimeSpan.FromMilliseconds(500))
            {
                UpdatePhase($"Scanning all linked files...\nFound {count}...");
                lastProgressAt = Stopwatch.GetTimestamp();
            }
            yield return linkedId;
        }

        UpdatePhase($"Scanning all linked files...\nFound {count}...");
    }

    /// <summary>
    /// The population CountUnlinkedItems draws from: every item this task is allowed to delete,
    /// linked or not. Must mirror CountUnlinkedItems' predicates exactly (minus the link join),
    /// otherwise the safety ratio compares two different populations.
    /// </summary>
    private async Task<int> CountDeletableItems(DateTime createdBefore)
    {
        await using var dbContext = CreateContext();
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;

        return await dbContext.Database
            .SqlQuery<int>(
                $"""
                 SELECT COUNT(i.Id) AS Value FROM DavItems i
                 WHERE i.Type = {usenetFileType}
                   AND i.HistoryItemId IS NULL
                   AND i.CreatedAt < {createdBefore}
                 """)
            .FirstAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> CountUnlinkedItems(DateTime createdBefore)
    {
        await using var dbContext = CreateContext();
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;

        // LEFT JOIN is equivalent to the NOT EXISTS used by RemoveUnlinkedItems /
        // DryRunIdentifyUnlinkedFiles; CountDeletableItems mirrors these predicates without
        // the link join so the safety ratio compares the same population.
        // Join as t.Id = i.Id so SQLite inherits NOCASE from TMP_LINKED_FILES (left operand)
        // and can seek the PK; i.Id = t.Id would use BINARY and miss both index and casing.
        var count = await dbContext.Database
            .SqlQuery<int>(
                $"""
                 SELECT COUNT(i.Id) AS Value FROM DavItems i
                 LEFT JOIN TMP_LINKED_FILES t ON t.Id = i.Id
                 WHERE i.Type = {usenetFileType}
                   AND i.HistoryItemId IS NULL
                   AND i.CreatedAt < {createdBefore}
                   AND t.Id IS NULL
                 """)
            .FirstAsync()
            .ConfigureAwait(false);

        return count;
    }

    private async Task RemoveUnlinkedItems(DateTime createdBefore, int totalCount)
    {
        StartPhase("Removing unlinked items...");
        _allRemovedPaths.Clear();
        await using var dbContext = CreateContext();
        var removed = 0;
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;

        while (true)
        {
            // Select items to delete (batch of 100). t.Id on the left inherits NOCASE from
            // the TMP_LINKED_FILES PK so lowercase DavItems.Id still match uppercase links.
            var itemsToDelete = await dbContext.Database
                .SqlQuery<UnlinkedItemInfo>(
                    $"""
                     SELECT Id, Type, Path FROM DavItems
                     WHERE Type = {usenetFileType}
                       AND HistoryItemId IS NULL
                       AND CreatedAt < {createdBefore}
                       AND NOT EXISTS (
                           SELECT 1 FROM TMP_LINKED_FILES t
                           WHERE t.Id = DavItems.Id
                       )
                     LIMIT 100
                     """)
                .ToListAsync()
                .ConfigureAwait(false);

            // If there are no more items to delete, we're done.
            if (itemsToDelete.Count == 0)
                break;

            // Delete by the exact Id text from the select so stored casing never matters.
            foreach (var item in itemsToDelete)
            {
                DeletionAuditLog.Record(
                    "remove-orphaned",
                    new DavItem { Id = Guid.Parse(item.Id), Path = item.Path },
                    "no library symlink/strm link");
            }

            var deleted = await DeleteItemsByIdTextAsync(dbContext, itemsToDelete)
                .ConfigureAwait(false);

            // A batch that selects rows but deletes none would loop forever with a climbing
            // counter. Throw so ExecuteInternal reports a terminal "Failed:" status.
            if (deleted == 0)
            {
                throw new InvalidOperationException(
                    $"selected {itemsToDelete.Count} unlinked items but deleted 0; " +
                    $"aborting to avoid an infinite loop.");
            }

            // Trigger rclone vfs/forget for deleted items
            _ = DavDatabaseContext.RcloneVfsForget(itemsToDelete.Select(x => new DavItem
            {
                Id = Guid.Parse(x.Id),
                Type = (DavItem.ItemType)x.Type,
                Path = x.Path
            }).ToList());

            // Track removed paths
            _allRemovedPaths.AddRange(itemsToDelete.Select(x => x.Path));
            removed += deleted;

            UpdatePhase($"Removing unlinked items...\nRemoved {removed}/{totalCount}...");
        }

        UpdatePhase($"Removing unlinked items...\nRemoved {removed} of {removed}...");
    }

    private async Task RemoveEmptyDirectories(DateTime createdBefore)
    {
        StartPhase("Removing empty directories...");
        await using var dbContext = CreateContext();
        await RemoveEmptyDirectoriesAsync(
            dbContext,
            createdBefore,
            removedSoFar => UpdatePhase($"Removing empty directories...\nRemoved {removedSoFar}..."),
            dirs => DavDatabaseContext.RcloneVfsForget(dirs),
            CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes empty regular directories in batches and returns the number removed.
    /// Category folders under /content and /nzbs are excluded (WebDAV synthesizes them;
    /// deleting them races with in-flight queue processing via DavCleanupService cascade).
    /// Mount folders still linked to SAB history are also excluded. The DELETE re-checks
    /// emptiness so a concurrent child insert cannot race into a cascade delete.
    /// </summary>
    internal static async Task<int> RemoveEmptyDirectoriesAsync(
        DavDatabaseContext dbContext,
        DateTime createdBefore,
        Action<int>? onProgress = null,
        Func<List<DavItem>, Task>? onDeleted = null,
        CancellationToken cancellationToken = default)
    {
        var removed = 0;
        var directorySubType = (int)DavItem.ItemSubType.Directory;
        var contentFolderId = DavItem.ContentFolder.Id.ToString();
        var nzbFolderId = DavItem.NzbFolder.Id.ToString();
        // When a selected batch deletes fewer rows than selected (child appeared mid-flight),
        // retry. Only abort if the same batch ids keep making no progress.
        string? lastStuckBatchKey = null;

        while (true)
        {
            // NOT EXISTS uses IX_DavItems_ParentId_Name's ParentId prefix; avoid the
            // previous LEFT JOIN anti-join which rescanned poorly at large scale.
            var emptyDirs = await dbContext.Database
                .SqlQuery<UnlinkedItemInfo>(
                    $"""
                     SELECT d.Id AS Id, d.Type AS Type, d.Path AS Path FROM DavItems d
                     WHERE d.SubType = {directorySubType}
                       AND d.HistoryItemId IS NULL
                       AND d.CreatedAt < {createdBefore}
                       AND d.ParentId NOT IN ({contentFolderId}, {nzbFolderId})
                       AND NOT EXISTS (
                           SELECT 1 FROM DavItems c WHERE c.ParentId = d.Id
                       )
                     LIMIT 100
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (emptyDirs.Count == 0)
                break;

            // Delete by the exact Id text from the select so stored casing never matters
            // (the Fix-Empty-Categories migration seeds a lowercase-Id folder). Re-check
            // emptiness in the DELETE so a concurrent insert under a selected folder
            // cannot race into TR_DavItems_DeleteDirectory + DavCleanupService cascade.
            var deleted = await DeleteEmptyDirectoriesByIdTextAsync(
                    dbContext, emptyDirs, cancellationToken)
                .ConfigureAwait(false);

            if (deleted == 0)
            {
                var batchKey = string.Join(",", emptyDirs.Select(x => x.Id));
                if (batchKey == lastStuckBatchKey)
                {
                    throw new InvalidOperationException(
                        $"selected {emptyDirs.Count} empty directories but deleted 0 twice; " +
                        $"aborting to avoid an infinite loop.");
                }

                lastStuckBatchKey = batchKey;
                continue;
            }

            lastStuckBatchKey = null;

            // Prefer auditing the full selected batch when every row deleted. On a partial
            // delete the survivors remain for the next iteration; over-auditing the rare
            // race case is preferable to missing deleted paths.
            foreach (var dir in emptyDirs)
            {
                DeletionAuditLog.Record(
                    "remove-orphaned",
                    new DavItem { Id = Guid.Parse(dir.Id), Path = dir.Path },
                    "empty directory after orphaned-file cleanup");
            }

            if (onDeleted is not null)
            {
                _ = onDeleted(emptyDirs.Select(x => new DavItem
                {
                    Id = Guid.Parse(x.Id),
                    Type = (DavItem.ItemType)x.Type,
                    Path = x.Path
                }).ToList());
            }

            removed += deleted;
            onProgress?.Invoke(removed);
        }

        return removed;
    }

    private async Task DryRunIdentifyUnlinkedFiles(DateTime createdBefore)
    {
        _allRemovedPaths.Clear();
        await using var dbContext = CreateContext();
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;
        // Keyset pagination: LIMIT/OFFSET without ORDER BY has no stable order in SQLite,
        // which can duplicate or skip rows across batches.
        var lastId = string.Empty;

        while (true)
        {
            var batch = await dbContext.Database
                .SqlQuery<UnlinkedItemInfo>(
                    $"""
                     SELECT Id, Type, Path FROM DavItems
                     WHERE Type = {usenetFileType}
                       AND HistoryItemId IS NULL
                       AND CreatedAt < {createdBefore}
                       AND Id > {lastId}
                       AND NOT EXISTS (
                           SELECT 1 FROM TMP_LINKED_FILES t
                           WHERE t.Id = DavItems.Id
                       )
                     ORDER BY Id
                     LIMIT 100
                     """)
                .ToListAsync()
                .ConfigureAwait(false);

            if (batch.Count == 0)
                break;

            _allRemovedPaths.AddRange(batch.Select(x => x.Path));
            lastId = batch[^1].Id;
            UpdatePhase($"Identifying unlinked files...\nFound {_allRemovedPaths.Count}...");
        }
    }

    private void StartPhase(string message) => _progressHeartbeat?.StartPhase(message);

    private void UpdatePhase(string message) => _progressHeartbeat?.UpdatePhase(message);

    private void Complete(string message)
    {
        if (_progressHeartbeat is not null)
            _progressHeartbeat.Complete(message);
        else
            Report(message);
    }

    private void Report(string message)
    {
        var dryRun = _isDryRun ? "Dry Run - " : string.Empty;
        var progress = $"{dryRun}{message}";
        _progressObserver?.Invoke(progress);
        _ = _websocketManager.SendMessage(WebsocketTopic.CleanupTaskProgress, progress);
    }

    internal sealed class ProgressHeartbeat : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly Action<string> _report;
        private readonly TimeSpan _interval;
        private readonly Timer _timer;
        private string? _message;
        private long _runStartedAt;
        private bool _completed;
        private bool _disposed;

        public ProgressHeartbeat(Action<string> report, TimeSpan interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _report = report;
            _interval = interval;
            _timer = new Timer(
                static state => ((ProgressHeartbeat)state!).ReportHeartbeat(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        public void StartPhase(string message)
        {
            lock (_sync)
            {
                if (_disposed || _completed) return;
                if (_runStartedAt == 0)
                    _runStartedAt = Stopwatch.GetTimestamp();
                _message = message;
                ReportWithElapsed();
                _timer.Change(_interval, _interval);
            }
        }

        public void UpdatePhase(string message)
        {
            lock (_sync)
            {
                if (_disposed || _completed) return;
                _message = message;
                ReportWithElapsed();
            }
        }

        public void Complete(string message)
        {
            lock (_sync)
            {
                if (_disposed || _completed) return;
                _completed = true;
                _message = null;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _report(message);
            }
        }

        private void ReportHeartbeat()
        {
            lock (_sync)
            {
                if (_disposed || _message is null) return;
                ReportWithElapsed();
            }
        }

        private void ReportWithElapsed()
        {
            if (_message is null) return;
            if (_runStartedAt == 0)
                _runStartedAt = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_runStartedAt);
            _report($"{_message}\nElapsed: {FormatElapsed(elapsed)}");
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : $"{Math.Max(1, (int)elapsed.TotalSeconds)}s";

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _message = null;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            await _timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static string GetAuditReport()
    {
        return _allRemovedPaths.Count > 0
            ? string.Join("\n", _allRemovedPaths)
            : "This list is Empty.\nYou must first run the task.";
    }

    internal static void ClearAuditPathsForTests() => _allRemovedPaths = [];
}
