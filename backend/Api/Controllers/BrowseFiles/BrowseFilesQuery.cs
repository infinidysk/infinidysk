using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.DeleteWebdavItem;
using NzbWebDAV.Api.Controllers.GetHealthCheckQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.BrowseFiles;

internal static class BrowseFilesQuery
{
    internal sealed class FileProjection
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public long? Size { get; init; }
        public DavItem.ItemSubType SubType { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTimeOffset? ReleaseDate { get; init; }
        public DateTimeOffset? LastHealthCheck { get; init; }
        public DateTimeOffset? NextHealthCheck { get; init; }
        public bool HealthRepairPending { get; init; }
        public string Health { get; init; } = "unknown";
        public Guid? HealthResultId { get; init; }
        public DateTimeOffset? HealthResultAt { get; init; }
        public HealthCheckResult.RepairAction? RepairAction { get; init; }
        public string? HealthMessage { get; init; }
        public Guid? HistoryItemId { get; init; }
        public string? JobName { get; init; }
        public string? NzbFileName { get; init; }
        public string? Category { get; init; }
        public string? IndexerName { get; init; }
        public DateTimeOffset? LastPlayedAt { get; init; }
        public Guid? NzbBlobId { get; init; }
    }

    internal static IQueryable<FileProjection> BuildProjection(DavDatabaseContext context, string scopePath, bool showHidden)
    {
        var prefix = scopePath + "/";
        var files = context.Items.AsNoTracking().Where(item => item.Type == DavItem.ItemType.UsenetFile)
            .Where(item => item.Path.Length >= prefix.Length && item.Path.Substring(0, prefix.Length) == prefix);
        if (!showHidden) files = files.Where(item => !item.Path.Contains("/."));
        return from item in files
            join historyItem in context.HistoryItems.AsNoTracking() on item.HistoryItemId equals (Guid?)historyItem.Id into histories
            from history in histories.DefaultIfEmpty()
            let results = context.HealthCheckResults.Where(result => result.DavItemId == item.Id)
                .OrderByDescending(result => result.CreatedAt)
                .ThenBy(result => result.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded ? 1 : 0)
                .ThenByDescending(result => result.Id).AsEnumerable()
            let latestId = results.Select(result => (Guid?)result.Id).FirstOrDefault()
            let latestResult = results.Select(result => (HealthCheckResult.HealthResult?)result.Result).FirstOrDefault()
            let latestRepair = results.Select(result => (HealthCheckResult.RepairAction?)result.RepairStatus).FirstOrDefault()
            let nzbId = item.NzbBlobId ?? (history == null ? null : history.NzbBlobId)
            select new FileProjection
            {
                Id = item.Id, ParentId = item.ParentId, Name = item.Name, Path = item.Path, Size = item.FileSize,
                SubType = item.SubType, CreatedAt = item.CreatedAt, ReleaseDate = item.ReleaseDate,
                LastHealthCheck = item.LastHealthCheck, NextHealthCheck = item.NextHealthCheck,
                HealthRepairPending = item.HealthRepairPending,
                Health = item.HealthRepairPending || item.NextHealthCheck == DateTimeOffset.UnixEpoch ? "needs-attention"
                    : latestId == null ? "unknown"
                    : latestRepair == HealthCheckResult.RepairAction.ActionNeeded || latestResult == HealthCheckResult.HealthResult.Unhealthy ? "needs-attention"
                    : latestResult == HealthCheckResult.HealthResult.Degraded ? "degraded" : "healthy",
                HealthResultId = latestId,
                HealthResultAt = results.Select(result => (DateTimeOffset?)result.CreatedAt).FirstOrDefault(),
                RepairAction = latestRepair, HealthMessage = results.Select(result => result.Message).FirstOrDefault(),
                HistoryItemId = item.HistoryItemId, JobName = history == null ? null : history.JobName,
                NzbFileName = history == null ? null : history.FileName, Category = history == null ? null : history.Category,
                IndexerName = history == null ? null : history.IndexerName, LastPlayedAt = history == null ? null : history.LastPlayedAt,
                NzbBlobId = nzbId != null && context.NzbNames.Any(name => name.Id == nzbId) ? nzbId : null,
            };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1311", Justification = "EF translates parameterless ToLower to database lower; culture overloads are not provider-neutral SQL.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1862", Justification = "EF translates lower and Contains; StringComparison overloads are not provider-neutral SQL.")]
    internal static IQueryable<FileProjection> ApplyFilters(IQueryable<FileProjection> files, BrowseFilesRequest request,
        FilesLibrarySnapshot library, IReadOnlyDictionary<Guid, int> active, DateTimeOffset now)
    {
        if (request.Q is { } text)
        {
            var query = text.ToLowerInvariant();
            files = files.Where(file => file.Name.ToLower().Contains(query) || file.Path.ToLower().Contains(query) ||
                (file.JobName != null && file.JobName.ToLower().Contains(query)) ||
                (file.NzbFileName != null && file.NzbFileName.ToLower().Contains(query)));
        }
        if (request.Health.Length > 0) files = files.Where(file => request.Health.Contains(file.Health));
        if (request.Category is not null) files = files.Where(file => file.Category == request.Category);
        if (request.Indexer is not null) files = files.Where(file => file.IndexerName == request.Indexer);
        if (request.SubType is not null) files = files.Where(file => file.SubType == request.SubType);
        if (request.RepairAction is not null) files = files.Where(file => file.RepairAction == request.RepairAction);
        if (request.MinSize is not null) files = files.Where(file => file.Size >= request.MinSize);
        if (request.MaxSize is not null) files = files.Where(file => file.Size <= request.MaxSize);
        if (request.HasNzb is not null) files = files.Where(file => (file.NzbBlobId != null) == request.HasNzb);
        if (request.AddedAfter is { } addedAfter)
        {
            var bound = DateTime.SpecifyKind(addedAfter.LocalDateTime, DateTimeKind.Unspecified);
            files = files.Where(file => file.CreatedAt >= bound);
        }
        if (request.AddedBefore is { } addedBefore)
        {
            var bound = DateTime.SpecifyKind(addedBefore.LocalDateTime, DateTimeKind.Unspecified);
            files = files.Where(file => file.CreatedAt < bound);
        }
        if (request.PostedAfter is not null) files = files.Where(file => file.ReleaseDate >= request.PostedAfter);
        if (request.PostedBefore is not null) files = files.Where(file => file.ReleaseDate < request.PostedBefore);
        if (request.CheckedAfter is not null) files = files.Where(file => file.LastHealthCheck >= request.CheckedAfter);
        if (request.CheckedBefore is not null) files = files.Where(file => file.LastHealthCheck < request.CheckedBefore);
        if (request.PlayedAfter is not null) files = files.Where(file => file.LastPlayedAt >= request.PlayedAfter);
        if (request.PlayedBefore is not null) files = files.Where(file => file.LastPlayedAt < request.PlayedBefore);
        if (request.Library is "in-library" or "not-in-library")
        {
            if (library.State != "ready") files = files.Where(file => false);
            else
            {
                var libraryIds = library.Links.Keys.ToArray();
                files = request.Library == "in-library" ? files.Where(file => libraryIds.Contains(file.Id))
                    : files.Where(file => !libraryIds.Contains(file.Id));
            }
        }
        else if (request.Library != "all" && request.Library != library.State) files = files.Where(file => false);
        return ApplyScheduleFilter(files, request.Schedule, active.Keys.ToArray(), now);
    }

    private static IQueryable<FileProjection> ApplyScheduleFilter(IQueryable<FileProjection> files, string schedule, Guid[] activeIds, DateTimeOffset now)
    {
        var urgent = DateTimeOffset.UnixEpoch;
        var forced = HealthCheckService.ForcedRecheckSentinel;
        return schedule switch
        {
            "all" => files,
            "checking" => files.Where(file => activeIds.Contains(file.Id)),
            "repair-pending" => files.Where(file => file.HealthRepairPending || file.NextHealthCheck == urgent),
            "recheck-queued" => files.Where(file => !file.HealthRepairPending && file.NextHealthCheck == forced),
            "never-scheduled" => files.Where(file => file.NextHealthCheck == null),
            "due" => files.Where(file => !file.HealthRepairPending && file.NextHealthCheck > forced && file.NextHealthCheck <= now),
            "scheduled" => files.Where(file => !file.HealthRepairPending && file.NextHealthCheck > forced && file.NextHealthCheck > now),
            _ => throw new InvalidOperationException("Unvalidated schedule filter."),
        };
    }

    internal static IOrderedQueryable<FileProjection> ApplySort(IQueryable<FileProjection> files, string sort, string direction)
    {
        var descending = direction == "desc";
        IOrderedQueryable<FileProjection> Order<TValue>(Expression<Func<FileProjection, TValue>> field,
            Expression<Func<FileProjection, int>> missing) => descending
                ? files.OrderBy(missing).ThenByDescending(field) : files.OrderBy(missing).ThenBy(field);
        var ordered = sort switch
        {
            "size" => Order(file => file.Size, file => file.Size == null ? 1 : 0),
            "added" => Order(file => file.CreatedAt, file => 0),
            "posted" => Order(file => file.ReleaseDate, file => file.ReleaseDate == null ? 1 : 0),
            "last-check" => Order(file => file.LastHealthCheck, file => file.LastHealthCheck == null ? 1 : 0),
            "next-check" => Order(file => file.NextHealthCheck == DateTimeOffset.UnixEpoch || file.NextHealthCheck == HealthCheckService.ForcedRecheckSentinel ? null : file.NextHealthCheck,
                file => file.NextHealthCheck == null || file.NextHealthCheck == DateTimeOffset.UnixEpoch || file.NextHealthCheck == HealthCheckService.ForcedRecheckSentinel ? 1 : 0),
            "type" => Order(file => file.SubType, file => 0),
            "health" => Order(file => file.Health == "needs-attention" ? 0 : file.Health == "degraded" ? 1 : file.Health == "unknown" ? 2 : 3, file => 0),
            _ => Order(file => file.Name, file => 0),
        };
        return ordered.ThenBy(file => file.Path).ThenBy(file => file.Id);
    }

    private sealed class DirectoryProjection
    {
        public Guid? Id { get; init; }
        public Guid? ParentId { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public DateTime? CreatedAt { get; init; }
        public bool HasChildren { get; init; }
    }

    internal static async Task<BrowseFilesResponse> ReadAsync(DavDatabaseClient dbClient, ConfigManager configManager,
        BrowseFilesRequest request, FilesLibrarySnapshot library, IReadOnlyDictionary<Guid, int> active,
        IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> queue, HealthWorkSchedulePolicy healthWorkSchedule,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = dbClient.Ctx;
        var showHidden = configManager.ShowHiddenWebdavFiles();
        var categories = configManager.GetApiCategories().Where(name => showHidden || !name.StartsWith('.')).ToHashSet(StringComparer.Ordinal);
        async Task<DavItem?> Resolve(string path)
        {
            if (!showHidden && path.Contains("/.", StringComparison.Ordinal))
                throw new BadHttpRequestException("Directory not found.", StatusCodes.Status404NotFound);
            if (path == "/content") return DavItem.ContentFolder;
            var item = await context.Items.AsNoTracking().SingleOrDefaultAsync(item => item.Path == path, cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                if (item.Type != DavItem.ItemType.Directory) throw new BadHttpRequestException("Scope and parent must be directories.");
                return item;
            }
            if (path.Count(character => character == '/') == 2 && categories.Contains(path[9..])) return null;
            throw new BadHttpRequestException("Directory not found.", StatusCodes.Status404NotFound);
        }
        var scope = await Resolve(request.ScopePath).ConfigureAwait(false);
        var parent = request.Mode == "tree" && request.ParentPath != request.ScopePath
            ? await Resolve(request.ParentPath).ConfigureAwait(false) : scope;
        var files = ApplyFilters(BuildProjection(context, request.ScopePath, showHidden), request, library, active, now);
        var matchingCount = await files.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<BrowseFilesResponse.FileRow>();
        var totalRows = matchingCount;
        if (request.Mode == "list")
        {
            var page = await ApplySort(files, request.Sort, request.Direction).Skip(request.Offset).Take(request.Limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Select(file => MapFile(file, configManager, library, active, queue, now)));
        }
        else
        {
            var parentId = parent?.Id;
            var directories = context.Items.AsNoTracking().Where(item => parentId != null && item.ParentId == parentId && item.Type == DavItem.ItemType.Directory);
            if (!showHidden) directories = directories.Where(item => !item.Path.Contains("/."));
            if (request.HasFilters) directories = directories.Where(directory => files.Any(file =>
                file.Path.Length > directory.Path.Length && file.Path.Substring(0, directory.Path.Length + 1) == directory.Path + "/"));
            var projected = directories.Select(directory => new DirectoryProjection
            {
                Id = directory.Id, ParentId = directory.ParentId, Name = directory.Name, Path = directory.Path, CreatedAt = directory.CreatedAt,
                HasChildren = request.HasFilters || context.Items.Any(child => child.ParentId == directory.Id && (showHidden || !child.Path.Contains("/."))),
            });
            List<DirectoryProjection> directoryPage;
            int directoryCount;
            if (request.ParentPath == "/content")
            {
                var shallow = await projected.ToListAsync(cancellationToken).ConfigureAwait(false);
                if (!request.HasFilters)
                {
                    var realNames = shallow.Select(directory => directory.Name).ToHashSet(StringComparer.Ordinal);
                    shallow.AddRange(categories.Where(name => !realNames.Contains(name)).Select(name => new DirectoryProjection
                    {
                        Name = name, Path = "/content/" + name, ParentId = DavItem.ContentFolder.Id,
                    }));
                }
                directoryCount = shallow.Count;
                directoryPage = shallow.OrderBy(directory => directory.Name, StringComparer.Ordinal).Skip(request.Offset).Take(request.Limit).ToList();
            }
            else
            {
                directoryCount = await projected.CountAsync(cancellationToken).ConfigureAwait(false);
                directoryPage = await projected.OrderBy(directory => directory.Name).ThenBy(directory => directory.Id)
                    .Skip(request.Offset).Take(request.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            rows.AddRange(directoryPage.Select(directory => MapDirectory(directory, configManager, queue)));
            var children = files.Where(file => parentId != null && file.ParentId == parentId);
            totalRows = directoryCount + await children.CountAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Count < request.Limit)
            {
                var page = await ApplySort(children, request.Sort, request.Direction).Skip(Math.Max(0, request.Offset - directoryCount))
                    .Take(request.Limit - rows.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
                rows.AddRange(page.Select(file => MapFile(file, configManager, library, active, queue, now)));
            }
        }
        var admission = healthWorkSchedule.Evaluate(now);
        var pending = await context.Items.CountAsync(item => item.HealthRepairPending, cancellationToken).ConfigureAwait(false);
        return new BrowseFilesResponse
        {
            Mode = request.Mode, ScopePath = request.ScopePath, ParentPath = request.ParentPath, Offset = request.Offset,
            Limit = request.Limit, TotalRows = totalRows, MatchingFileCount = matchingCount,
            HasMore = (long)request.Offset + rows.Count < totalRows, ObservedAt = now,
            LibraryScanState = library.State, LibraryScannedAt = library.ScannedAt, LibraryError = library.Error, Rows = rows,
            Schedule = new GetHealthCheckQueueResponse.HealthCheckScheduleStatus
            {
                TimeZoneId = admission.TimeZoneId, ChecksOpen = admission.ChecksOpen, RepairsOpen = admission.RepairsOpen,
                NextChecksChange = admission.NextChecksChange, NextRepairsChange = admission.NextRepairsChange,
                PendingRepairCount = pending, ManualRunActive = admission.ManualRunActive,
            },
        };
    }

    private static string? DeleteReason(Guid? id, Guid? parentId, string path, ConfigManager config,
        IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> queue)
    {
        if (id is null || new DavItem { Id = id.Value, ParentId = parentId }.IsProtected()) return "Protected directory.";
        if (config.IsEnforceReadonlyWebdavEnabled()) return "WebDAV is configured read-only.";
        return DeleteWebdavItemSupport.HasInProgressDownload(path, queue) ? "A matching download is in progress." : null;
    }

    private static BrowseFilesResponse.FileRow MapDirectory(DirectoryProjection directory, ConfigManager config,
        IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> queue)
    {
        var reason = DeleteReason(directory.Id, directory.ParentId, directory.Path, config, queue);
        return new BrowseFilesResponse.FileRow
        {
            Key = directory.Id?.ToString("D") ?? "category:" + directory.Name, Id = directory.Id, ParentId = directory.ParentId,
            Name = directory.Name, Path = directory.Path, IsDirectory = true, HasChildren = directory.HasChildren, Size = null, SubType = null,
            AddedAt = directory.CreatedAt is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified)) : null,
            ReleaseDate = null, LastHealthCheck = null, NextCheckAt = null, Health = null, ScanState = null, Progress = null,
            HealthResultId = null, HealthResultAt = null, RepairAction = null, HealthMessage = null, HistoryItemId = null,
            JobName = null, NzbFileName = null, Category = null, IndexerName = null, LastPlayedAt = null, NzbBlobId = null,
            LibraryState = null, LibraryLinkCount = 0, LibraryPaths = [], CanRecheck = false, RecheckDisabledReason = "Select a file.",
            CanDelete = reason is null, DeleteDisabledReason = reason, CanSearchArr = false, SearchArrDisabledReason = "Select a file.",
        };
    }

    private static BrowseFilesResponse.FileRow MapFile(FileProjection file, ConfigManager config, FilesLibrarySnapshot library,
        IReadOnlyDictionary<Guid, int> active, IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> queue, DateTimeOffset now)
    {
        var candidate = FilenameUtil.IsHealthCheckCandidate(file.Name);
        var enabled = config.IsRepairJobEnabled();
        var checking = active.TryGetValue(file.Id, out var progress);
        var urgent = file.HealthRepairPending || file.NextHealthCheck == DateTimeOffset.UnixEpoch;
        var next = file.NextHealthCheck == DateTimeOffset.UnixEpoch || file.NextHealthCheck == HealthCheckService.ForcedRecheckSentinel ? null : file.NextHealthCheck;
        var scan = !candidate ? "not-applicable" : checking ? "checking" : !enabled ? "disabled" : urgent ? "repair-pending"
            : next is null ? "queued" : next <= now ? "due" : "scheduled";
        var paths = library.State == "ready" && library.Links.TryGetValue(file.Id, out var links) ? links : [];
        var libraryState = library.State == "ready" ? paths.Length > 0 ? "in-library" : "not-in-library" : library.State;
        var recheckReason = !candidate ? "This file type does not support a health recheck." : !enabled ? config.GetRepairDisabledReason() ?? "Background repairs are disabled." : null;
        var deleteReason = DeleteReason(file.Id, file.ParentId, file.Path, config, queue);
        var searchReason = !candidate ? "Select a media file." : library.State != "ready" ? "Configure the Library Directory and wait for its scan."
            : paths.Length == 0 ? "No recognized library link for this file." : !config.GetArrConfig().GetEnabledInstances().Any() ? "Configure an enabled Arr instance." : null;
        return new BrowseFilesResponse.FileRow
        {
            Key = file.Id.ToString("D"), Id = file.Id, ParentId = file.ParentId, Name = file.Name, Path = file.Path,
            IsDirectory = false, HasChildren = false, Size = file.Size, SubType = (int)file.SubType,
            AddedAt = new DateTimeOffset(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Unspecified)), ReleaseDate = file.ReleaseDate,
            LastHealthCheck = file.LastHealthCheck, NextCheckAt = next, Health = file.Health, ScanState = scan,
            Progress = checking ? progress : null, HealthResultId = file.HealthResultId, HealthResultAt = file.HealthResultAt,
            RepairAction = (int?)file.RepairAction, HealthMessage = file.HealthMessage, HistoryItemId = file.HistoryItemId,
            JobName = file.JobName, NzbFileName = file.NzbFileName, Category = file.Category, IndexerName = file.IndexerName,
            LastPlayedAt = file.LastPlayedAt, NzbBlobId = file.NzbBlobId, LibraryState = libraryState, LibraryLinkCount = paths.Length,
            LibraryPaths = paths, CanRecheck = recheckReason is null, RecheckDisabledReason = recheckReason,
            CanDelete = deleteReason is null, DeleteDisabledReason = deleteReason, CanSearchArr = searchReason is null, SearchArrDisabledReason = searchReason,
        };
    }
}