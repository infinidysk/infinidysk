using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx, IBlobStore? blobStore = null)
{
    public DavDatabaseContext Ctx => ctx;

    // file
    public Task<DavItem?> GetFileById(string id)
    {
        // non-guid names (e.g. clients probing /.ids paths) are simply not found
        if (!Guid.TryParse(id, out var guid)) return Task.FromResult<DavItem?>(null);
        return ctx.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == guid);
    }

    public Task<List<DavItem>> GetFilesByIdPrefix(string prefix)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return GetDirectoryChildrenQuery(dirId).ToListAsync(ct);
    }

    public async IAsyncEnumerable<DavItem> GetDirectoryChildrenEnumerableAsync(
        Guid dirId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var child in GetDirectoryChildrenQuery(dirId)
                           .AsAsyncEnumerable()
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            yield return child;
        }
    }

    public async Task<List<DavItem>> GetItemsByIdsBatchedAsync(
        IEnumerable<Guid> ids, int batchSize = 500, CancellationToken ct = default)
    {
        var result = new List<DavItem>();
        foreach (var batch in ids.Distinct().ToBatches(batchSize))
            result.AddRange(await Ctx.Items.AsNoTracking().Where(x => batch.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false));
        return result;
    }

    public Task<DavItem?> GetDirectoryChildAsync(Guid dirId, string childName, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ParentId == dirId && x.Name == childName, ct);
    }

    private IQueryable<DavItem> GetDirectoryChildrenQuery(Guid dirId)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(x => x.ParentId == dirId)
            .OrderBy(x => x.Name);
    }

    // Resolves a persisted item by its absolute virtual path in a single indexed lookup,
    // instead of one query per path segment. Returns null for synthetic items that have no
    // stored row (empty category folders, .ids children, the readme, etc.), in which case
    // callers fall back to walking the collection hierarchy.
    public Task<DavItem?> GetItemByPathAsync(string path, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Path == path, ct);
    }

    public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
    {
        if (dirId == DavItem.Root.Id)
        {
            return await Ctx.Items.SumAsync(x => x.FileSize, ct).ConfigureAwait(false) ?? 0;
        }

        const string sql = """
            WITH RECURSIVE "RecursiveChildren" AS (
                SELECT "Id", "FileSize"
                FROM "DavItems"
                WHERE "ParentId" = @parentId

                UNION ALL

                SELECT d."Id", d."FileSize"
                FROM "DavItems" d
                INNER JOIN "RecursiveChildren" rc ON d."ParentId" = rc."Id"
            )
            SELECT COALESCE(SUM("FileSize"), 0)
            FROM "RecursiveChildren";
        """;
        var connection = Ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@parentId";
        parameter.Value = dirId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    // usenet files
    public async Task<bool> StreamingPayloadExistsAsync(
        DavItem davItem,
        CancellationToken ct = default)
    {
        if (davItem.FileBlobId is { } blobId && (blobStore ?? BlobStore.Current).Exists(blobId))
            return true;

        return davItem.SubType switch
        {
            DavItem.ItemSubType.NzbFile =>
                await ctx.NzbFiles.AsNoTracking()
                    .AnyAsync(x => x.Id == davItem.Id, ct)
                    .ConfigureAwait(false),
            DavItem.ItemSubType.RarFile =>
                await ctx.RarFiles.AsNoTracking()
                    .AnyAsync(x => x.Id == davItem.Id, ct)
                    .ConfigureAwait(false),
            DavItem.ItemSubType.MultipartFile =>
                await ctx.MultipartFiles.AsNoTracking()
                    .AnyAsync(x => x.Id == davItem.Id, ct)
                    .ConfigureAwait(false),
            _ => false,
        };
    }

    public async Task<DavNzbFile?> GetDavNzbFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavNzbFile>(blobId.Value).ConfigureAwait(false);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.NzbFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavRarFile?> GetDavRarFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavRarFile>(blobId.Value).ConfigureAwait(false);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.RarFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavMultipartFile?> GetDavMultipartFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        DavMultipartFile? multipartFile = null;

        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            multipartFile = await BlobStore.ReadBlob<DavMultipartFile>(blobId.Value).ConfigureAwait(false);
        }

        // read from database
        multipartFile ??= await ctx.MultipartFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);

        if (multipartFile?.Metadata.IsLazy == true
            && multipartFile.Metadata.ExpectedFileSize is null
            && davItem.FileSize is { } expectedFileSize
            && expectedFileSize >= 0
            && expectedFileSize < long.MaxValue)
        {
            multipartFile.Metadata.ExpectedFileSize = expectedFileSize;
        }

        return multipartFile;
    }

    // queue
    public async Task<(QueueItem? queueItem, Stream? queueNzbStream)> GetTopQueueItem
    (
        CancellationToken ct = default
    )
    {
        return await GetTopQueueItem(excludeIds: null, ct).ConfigureAwait(false);
    }

    public async Task<(QueueItem? queueItem, Stream? queueNzbStream)> GetTopQueueItem
    (
        IReadOnlyCollection<Guid>? excludeIds,
        CancellationToken ct = default
    )
    {
        // read queue item from database
        var nowTime = DateTime.Now;
        var query = Ctx.QueueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.SortOrder)
            .ThenBy(q => q.CreatedAt)
            .ThenBy(q => q.Id)
            .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
            .Where(q => q.Priority != QueueItem.PriorityOption.Paused);

        if (excludeIds is { Count: > 0 })
        {
            var excluded = excludeIds as HashSet<Guid> ?? excludeIds.ToHashSet();
            query = query.Where(q => !excluded.Contains(q.Id));
        }

        var queueItem = await query
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // attempt to read nzb contents from blob-store.
        var queueNzbStream = queueItem != null
            ? BlobStore.ReadBlob(queueItem.Id)
            : null;

        // otherwise, read nzb contents from database.
        if (queueItem != null && queueNzbStream == null)
        {
            var queueNzbContents = await Ctx.QueueNzbContents
                .FirstOrDefaultAsync(q => q.Id == queueItem.Id, ct)
                .ConfigureAwait(false);

            if (queueNzbContents != null)
                return (queueItem, CreateNzbContentStream(queueNzbContents.NzbContents));
        }

        // return
        return (queueItem, queueNzbStream);
    }

    private static MemoryStream CreateNzbContentStream(string contents) =>
        new(Encoding.UTF8.GetBytes(contents));

    public Task<DateTime?> GetNextQueueItemPauseUntil(CancellationToken ct = default)
    {
        // Matches GetTopQueueItem's local-time convention (PauseUntil is written
        // with DateTime.Now). MIN over an empty set yields null.
        var nowTime = DateTime.Now;
        return Ctx.QueueItems
            .AsNoTracking()
            .Where(q => q.PauseUntil != null && q.PauseUntil > nowTime)
            .MinAsync(q => q.PauseUntil, ct);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        var queueItems = category != null
            ? Ctx.QueueItems.Where(q => q.Category == category)
            : Ctx.QueueItems;
        return queueItems
            .AsNoTracking()
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.SortOrder)
            .ThenBy(q => q.CreatedAt)
            .ThenBy(q => q.Id)
            .Skip(start)
            .Take(limit)
            .ToArrayAsync(cancellationToken: ct);
    }

    public Task<int> GetQueueItemsCount(string? category, CancellationToken ct = default)
    {
        var queueItems = category != null
            ? Ctx.QueueItems.Where(q => q.Category == category)
            : Ctx.QueueItems;
        return queueItems.CountAsync(cancellationToken: ct);
    }

    public async Task RemoveQueueItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        // Capture group keys before delete so we can cascade-clean orphaned
        // watchdog attempts whose only link was via the now-gone queue item.
        var groupKeys = await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id) && x.ContentGroupKey != null)
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await CascadeWatchdogEntriesAsync(ids, groupKeys, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the given queue items to the front of the queue by setting
    /// <see cref="QueueItem.PriorityOption.Force"/> and assigning earlier
    /// persistent sort-order values. Preserves the relative order
    /// of <paramref name="ids"/> (first id becomes the absolute top among
    /// moved items). Does not preempt an already in-progress download.
    /// </summary>
    /// <returns>The ids that were actually updated (unknown ids are skipped).</returns>
    public async Task<List<Guid>> MoveQueueItemsToTopAsync(
        List<Guid> ids,
        IReadOnlyCollection<Guid>? excludedIds = null,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        var idSet = ids.ToHashSet();
        var items = await Ctx.QueueItems
            .Where(q => idSet.Contains(q.Id) && (excludedIds == null || !excludedIds.Contains(q.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (items.Count == 0)
            return [];

        var byId = items.ToDictionary(q => q.Id);
        var ordered = ids.Where(byId.ContainsKey).Distinct().ToList();

        var earliest = await Ctx.QueueItems
            .Where(q => q.Priority == QueueItem.PriorityOption.Force)
            .Select(q => (long?)q.SortOrder)
            .MinAsync(ct)
            .ConfigureAwait(false) ?? 0;
        // Place moved items strictly before every other queue item.
        var baseOrder = earliest - QueueItem.SortOrderStride * ordered.Count;

        for (var i = 0; i < ordered.Count; i++)
        {
            var item = byId[ordered[i]];
            item.Priority = QueueItem.PriorityOption.Force;
            item.SortOrder = baseOrder + QueueItem.SortOrderStride * i;
            item.PauseUntil = null;
        }

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return ordered;
    }

    public sealed record QueueSwitchResult(int Position, int Priority)
    {
        public static readonly QueueSwitchResult NotMoved = new(-1, 0);
    }

    /// <summary>
    /// Moves one non-active queue item to a peer's original position, or an
    /// absolute visible position. The caller serializes this with queue claims.
    /// </summary>
    public async Task<QueueSwitchResult> SwitchQueueItemAsync(
        Guid sourceId,
        string target,
        IReadOnlyList<Guid> activeIds,
        CancellationToken ct = default)
    {
        if (activeIds.Contains(sourceId) || string.IsNullOrWhiteSpace(target))
            return QueueSwitchResult.NotMoved;

        var items = await Ctx.QueueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.SortOrder)
            .ThenBy(q => q.CreatedAt)
            .ThenBy(q => q.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queued = items.Where(item => !activeIds.Contains(item.Id)).ToList();
        var sourceIndex = queued.FindIndex(item => item.Id == sourceId);
        if (sourceIndex < 0)
            return QueueSwitchResult.NotMoved;

        QueueItem? targetItem;
        var activeCount = activeIds.Count;
        if (Guid.TryParse(target, out var targetId))
        {
            targetItem = activeIds.Contains(targetId)
                ? queued.FirstOrDefault()
                : queued.FirstOrDefault(item => item.Id == targetId);
        }
        else if (int.TryParse(target, out var targetPosition) && targetPosition >= 0)
        {
            var queuedPosition = Math.Max(0, targetPosition - activeCount);
            targetItem = queuedPosition < queued.Count ? queued[queuedPosition] : null;
        }
        else
        {
            return QueueSwitchResult.NotMoved;
        }

        if (targetItem is null || targetItem.Id == sourceId)
            return QueueSwitchResult.NotMoved;

        // SAB's switch inserts the source at the target's original index. The
        // list removal naturally gives "before" when promoting and "after"
        // when demoting.
        var targetIndex = queued.FindIndex(item => item.Id == targetItem.Id);
        queued.RemoveAt(sourceIndex);
        var source = items.Single(item => item.Id == sourceId);
        source.Priority = targetItem.Priority;
        if (source.Priority != QueueItem.PriorityOption.Paused)
            source.PauseUntil = null;
        queued.Insert(targetIndex, source);

        // Dense reassignment is deliberately limited to the destination band.
        // SortOrder has a generous stride, and this makes repeated moves
        // deterministic even when adjacent gaps have been exhausted.
        var band = queued.Where(item => item.Priority == source.Priority).ToList();
        for (var index = 0; index < band.Count; index++)
            band[index].SortOrder = QueueItem.SortOrderStride * (index + 1);

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        var position = activeCount + queued.FindIndex(item => item.Id == sourceId);
        return new QueueSwitchResult(position, (int)source.Priority);
    }

    // Delete watchdog attempts that were tied to the deleted queue/history items.
    // - QueueItemId match: direct link (queue-processor flow).
    // - ContentGroupKey match: orphaned group — no remaining queue or history item
    //   still references it. Skips groups that other items still reference so we
    //   don't nuke unrelated history when only one of several queue items is removed.
    //
    // excludeHistoryIds: history rows that are tracked for removal but not yet
    // committed; they'd otherwise appear "still referenced" and block cleanup.
    private async Task CascadeWatchdogEntriesAsync(
        List<Guid> queueItemIds,
        List<string> contentGroupKeys,
        CancellationToken ct,
        List<Guid>? excludeHistoryIds = null)
    {
        if (queueItemIds.Count > 0)
        {
            await Ctx.WatchdogEntries
                .Where(x => x.QueueItemId != null && queueItemIds.Contains(x.QueueItemId!.Value))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        if (contentGroupKeys.Count == 0) return;

        var stillReferencedInQueue = await Ctx.QueueItems
            .Where(x => x.ContentGroupKey != null && contentGroupKeys.Contains(x.ContentGroupKey!))
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var historyQuery = Ctx.HistoryItems
            .Where(x => x.ContentGroupKey != null && contentGroupKeys.Contains(x.ContentGroupKey!));
        if (excludeHistoryIds is { Count: > 0 })
            historyQuery = historyQuery.Where(x => !excludeHistoryIds.Contains(x.Id));

        var stillReferencedInHistory = await historyQuery
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var stillReferenced = new HashSet<string>(stillReferencedInQueue);
        stillReferenced.UnionWith(stillReferencedInHistory);

        var orphanedKeys = contentGroupKeys.Where(k => !stillReferenced.Contains(k)).ToList();
        if (orphanedKeys.Count == 0) return;

        await Ctx.WatchdogEntries
            .Where(x => x.ContentGroupKey != null && orphanedKeys.Contains(x.ContentGroupKey!))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // history
    public async Task<HistoryItem?> GetHistoryItemAsync(string id)
    {
        return await Ctx.HistoryItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == Guid.Parse(id)).ConfigureAwait(false);
    }

    public async Task RemoveHistoryItemsAsync(
        List<Guid> ids,
        bool deleteFiles,
        string source = "history-delete",
        CancellationToken ct = default)
    {
        // Capture group keys before delete so we can cascade-clean orphaned watchdog
        // attempts below. Done up front because the deleteFiles=false path doesn't
        // load the HistoryItem rows otherwise.
        var groupKeys = await Ctx.HistoryItems
            .Where(x => ids.Contains(x.Id) && x.ContentGroupKey != null)
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        if (deleteFiles)
        {
            var results = await (
                from h in Ctx.HistoryItems
                where ids.Contains(h.Id)
                join d in Ctx.Items on h.DownloadDirId equals d.Id into items
                from d in items.DefaultIfEmpty()
                select new { HistoryItem = h, DavItem = d }
            ).ToListAsync(ct).ConfigureAwait(false);

            var historyItems = results.Select(r => r.HistoryItem).ToList();
            var davItems = results.Where(r => r.DavItem != null).Select(r => r.DavItem!).ToList();
            foreach (var davItem in davItems)
            {
                DeletionAuditLog.Record(
                    "history-delete",
                    davItem,
                    $"{source} with deleteFiles=true (download dir)");
            }

            Ctx.Items.RemoveRange(davItems);
            Ctx.HistoryItems.RemoveRange(historyItems);
            await AddCleanupItemsIdempotentAsync(historyItems, deleteFiles, ct).ConfigureAwait(false);
        }
        else
        {
            // Only remove ids that actually exist. Stub entities for stale ids make EF emit
            // zero-row deletes and roll back the entire batch with a concurrency exception.
            var existing = await Ctx.HistoryItems
                .Where(h => ids.Contains(h.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (existing.Count > 0)
            {
                const int sampleSize = 10;
                Log.Information(
                    "history-remove source={Source} count={Count} deleteFiles=false sampleIds={SampleIds} sampleNames={SampleNames}",
                    source,
                    existing.Count,
                    string.Join(",", existing.Take(sampleSize).Select(x => x.Id)),
                    string.Join(", ", existing.Take(sampleSize).Select(x => x.JobName)));
            }

            Ctx.HistoryItems.RemoveRange(existing);
            await AddCleanupItemsIdempotentAsync(existing, deleteFiles, ct).ConfigureAwait(false);
        }

        await CascadeWatchdogEntriesAsync(
            queueItemIds: [],
            contentGroupKeys: groupKeys,
            ct: ct,
            excludeHistoryIds: ids).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a HistoryCleanupItem for each removed history row, skipping ids that already
    /// have a pending cleanup row. A concurrent RemoveFromHistory (or a retry while the
    /// previous cleanup row is still pending) would otherwise collide on the primary key
    /// and 500 the SAB call; a duplicate cleanup request is already satisfied, so it is
    /// treated as success.
    /// </summary>
    private async Task AddCleanupItemsIdempotentAsync(
        List<HistoryItem> removed,
        bool deleteFiles,
        CancellationToken ct)
    {
        if (removed.Count == 0) return;
        var removedIds = removed.Select(x => x.Id).ToList();
        var alreadyPending = await Ctx.HistoryCleanupItems
            .Where(x => removedIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(ct)
            .ConfigureAwait(false);
        alreadyPending.UnionWith(Ctx.ChangeTracker.Entries<HistoryCleanupItem>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.Id));
        Ctx.HistoryCleanupItems.AddRange(removed
            .Where(x => !alreadyPending.Contains(x.Id))
            .Select(x => new HistoryCleanupItem
            {
                Id = x.Id,
                DeleteMountedFiles = deleteFiles
            }));
    }

    /// <summary>
    /// Commits a history-removal change set, treating a concurrent delete of the
    /// same history row or a duplicate <see cref="HistoryCleanupItem"/> insert as
    /// success. Two SAB remove-from-history calls can otherwise collide on the
    /// cleanup primary key after both have staged the same id.
    ///
    /// SQLite's EF provider issues one modification command per batch, so a
    /// colliding delete of N ids can surface N separate concurrency (or unique)
    /// exceptions. The retry budget is therefore the number of pending entries,
    /// plus one for the successful save — not a fixed attempt count.
    /// </summary>
    public async Task SaveHistoryRemovalAsync(CancellationToken ct = default)
    {
        var maxAttempts = CountPendingSaveEntries() + 1;
        DbUpdateException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (
                ex.Entries.All(e => e.Entity is HistoryItem or DavItem))
            {
                last = ex;
                var pendingBefore = CountPendingSaveEntries();
                DetachVanishedEntries(ex);
                if (CountPendingSaveEntries() >= pendingBefore)
                    throw;
            }
            catch (DbUpdateException ex) when (TryDetachDuplicateCleanup(ex))
            {
                last = ex;
            }
        }

        if (last is not null)
            throw last;
    }

    private int CountPendingSaveEntries() =>
        Ctx.ChangeTracker.Entries()
            .Count(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified);

    private void DetachVanishedEntries(DbUpdateConcurrencyException ex)
    {
        var vanishedHistoryIds = ex.Entries
            .Select(e => e.Entity)
            .OfType<HistoryItem>()
            .Select(item => item.Id)
            .ToHashSet();

        foreach (var entry in ex.Entries)
            entry.State = EntityState.Detached;

        if (vanishedHistoryIds.Count == 0)
            return;

        foreach (var entry in Ctx.ChangeTracker.Entries<HistoryCleanupItem>()
                     .Where(e => e.State == EntityState.Added && vanishedHistoryIds.Contains(e.Entity.Id))
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool TryDetachDuplicateCleanup(DbUpdateException ex)
    {
        if (!ex.IsUniqueConstraintException())
            return false;

        var cleanup = ex.Entries.Where(e => e.Entity is HistoryCleanupItem).ToList();
        if (cleanup.Count == 0)
            return false;

        foreach (var entry in cleanup)
            entry.State = EntityState.Detached;
        return true;
    }

    public sealed record DavSubtreeDeleteEntry(
        Guid Id,
        string Path,
        Guid? HistoryItemId,
        DavItem.ItemType Type);

    public async Task<List<DavSubtreeDeleteEntry>> GetSubtreeForDeleteAsync(
        Guid rootId,
        CancellationToken ct = default)
    {
        // Identifiers stay double-quoted: PostgreSQL folds unquoted names to lowercase,
        // but EF Core creates the table and columns with case-sensitive quoted names.
        const string sql = """
            WITH RECURSIVE "Subtree" AS (
                SELECT "Id", "Path", "HistoryItemId", "Type"
                FROM "DavItems"
                WHERE "Id" = @rootId

                UNION ALL

                SELECT d."Id", d."Path", d."HistoryItemId", d."Type"
                FROM "DavItems" d
                INNER JOIN "Subtree" s ON d."ParentId" = s."Id"
            )
            SELECT "Id", "Path", "HistoryItemId", "Type" FROM "Subtree";
            """;

        var connection = Ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@rootId";
        parameter.Value = rootId;
        command.Parameters.Add(parameter);

        var entries = new List<DavSubtreeDeleteEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Guid? historyItemId = null;
            if (!await reader.IsDBNullAsync(2, ct).ConfigureAwait(false))
                historyItemId = reader.GetGuid(2);

            entries.Add(new DavSubtreeDeleteEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                historyItemId,
                (DavItem.ItemType)reader.GetInt32(3)));
        }

        return entries;
    }

    public async Task<List<Guid>> PruneUnreferencedHistoryItemsAsync(
        IReadOnlyCollection<Guid> historyItemIds,
        string source = "webdav-unreferenced-prune",
        CancellationToken ct = default)
    {
        if (historyItemIds.Count == 0) return [];

        const int batchSize = 500;
        var distinctIds = historyItemIds.Distinct().ToList();

        var stillReferenced = new HashSet<Guid>();
        foreach (var chunk in distinctIds.Chunk(batchSize))
        {
            var batch = chunk.ToList();
            var refs = await Ctx.Items
                .AsNoTracking()
                .Where(x => x.HistoryItemId != null && batch.Contains(x.HistoryItemId.Value))
                .Select(x => x.HistoryItemId!.Value)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);
            stillReferenced.UnionWith(refs);
        }

        var orphanedIds = distinctIds.Where(id => !stillReferenced.Contains(id)).ToList();
        if (orphanedIds.Count == 0) return [];

        foreach (var chunk in orphanedIds.Chunk(batchSize))
            await RemoveHistoryItemsAsync(chunk.ToList(), deleteFiles: false, source, ct).ConfigureAwait(false);

        return orphanedIds;
    }

    private class FileSizeResult
    {
        public long TotalSize { get; init; }
    }

    // health check
    public async Task<List<HealthCheckStat>> GetHealthCheckStatsAsync
    (
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default
    )
    {
        return await Ctx.HealthCheckStats
            .AsNoTracking()
            .Where(h => h.DateStartInclusive >= from && h.DateStartInclusive <= to)
            .GroupBy(h => new { h.Result, h.RepairStatus })
            .Select(g => new HealthCheckStat
            {
                Result = g.Key.Result,
                RepairStatus = g.Key.RepairStatus,
                Count = g.Select(r => r.Count).Sum(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // completed-symlinks
    public Task<List<DavItem>> GetCompletedSymlinkCategoryChildren(string category,
        CancellationToken ct = default)
    {
        return GetCompletedSymlinkCategoryChildrenQuery(category).ToListAsync(ct);
    }

    public async IAsyncEnumerable<DavItem> GetCompletedSymlinkCategoryChildrenEnumerableAsync(
        string category,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var child in GetCompletedSymlinkCategoryChildrenQuery(category)
                           .AsAsyncEnumerable()
                           .WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            yield return child;
        }
    }

    private IQueryable<DavItem> GetCompletedSymlinkCategoryChildrenQuery(string category)
    {
        var query = from historyItem in Ctx.HistoryItems
            .AsNoTracking()
                    where historyItem.Category == category
                          && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                          && historyItem.DownloadDirId != null
                    join davItem in Ctx.Items.AsNoTracking() on historyItem.DownloadDirId equals davItem.Id
                    where davItem.Type == DavItem.ItemType.Directory
                    select davItem;
        return query.Distinct().OrderBy(x => x.Name);
    }
}
