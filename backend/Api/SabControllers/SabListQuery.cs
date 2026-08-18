using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers;

#pragma warning disable CA1311 // PostgreSQL translates ToLower to SQL LOWER.
internal static class SabListQuery
{
    internal static string? NormalizeSearch(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? NormalizeDirection(string? value) =>
        string.Equals(value, "asc", StringComparison.OrdinalIgnoreCase) ? "asc"
        : string.Equals(value, "desc", StringComparison.OrdinalIgnoreCase) ? "desc"
        : null;

    internal static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    // SQLite LIKE is ASCII case-insensitive by default; PostgreSQL LIKE is
    // case-sensitive, so the Postgres path uses ILIKE to preserve behavior.
    internal static IQueryable<QueueItem> ApplySearch(
        IQueryable<QueueItem> query, string? search, bool isPostgres = false)
    {
        if (search is null) return query;
        var pattern = $"%{EscapeLikePattern(search)}%";
        return isPostgres
            ? query.Where(x =>
                EF.Functions.ILike(x.JobName, pattern, "\\") ||
                EF.Functions.ILike(x.FileName, pattern, "\\"))
            : query.Where(x =>
                EF.Functions.Like(x.JobName, pattern, "\\") ||
                EF.Functions.Like(x.FileName, pattern, "\\"));
    }

    internal static IQueryable<HistoryItem> ApplySearch(
        IQueryable<HistoryItem> query, string? search, bool isPostgres = false)
    {
        if (search is null) return query;
        var pattern = $"%{EscapeLikePattern(search)}%";
        return isPostgres
            ? query.Where(x =>
                EF.Functions.ILike(x.JobName, pattern, "\\") ||
                EF.Functions.ILike(x.FileName, pattern, "\\"))
            : query.Where(x =>
                EF.Functions.Like(x.JobName, pattern, "\\") ||
                EF.Functions.Like(x.FileName, pattern, "\\"));
    }

    internal static IQueryable<QueueItem> ApplyQueueSort(
        IQueryable<QueueItem> query,
        string? sort,
        string? dir,
        bool isPostgres = false)
    {
        var ascending = dir == "asc";
        return sort switch
        {
            "name" => ascending
                ? isPostgres
                    ? query.OrderBy(x => x.FileName.ToLower()).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderBy(x => EF.Functions.Collate(x.FileName, "NOCASE")).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : isPostgres
                    ? query.OrderByDescending(x => x.FileName.ToLower()).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderByDescending(x => EF.Functions.Collate(x.FileName, "NOCASE")).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "category" => ascending
                ? isPostgres
                    ? query.OrderBy(x => x.Category.ToLower()).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderBy(x => EF.Functions.Collate(x.Category, "NOCASE")).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : isPostgres
                    ? query.OrderByDescending(x => x.Category.ToLower()).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderByDescending(x => EF.Functions.Collate(x.Category, "NOCASE")).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "status" => ascending
                ? query.OrderBy(x => x.Priority == QueueItem.PriorityOption.Paused ? 1 : 0).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.Priority == QueueItem.PriorityOption.Paused ? 1 : 0).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "size" => ascending
                ? query.OrderBy(x => x.TotalSegmentBytes).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.TotalSegmentBytes).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id),
        };
    }

    internal static IQueryable<HistoryItem> ApplyHistorySort(
        IQueryable<HistoryItem> query,
        string? sort,
        string? dir,
        bool isPostgres = false)
    {
        var ascending = dir == "asc";
        return sort switch
        {
            "name" => ascending
                ? isPostgres
                    ? query.OrderBy(x => x.JobName.ToLower()).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderBy(x => EF.Functions.Collate(x.JobName, "NOCASE")).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                : isPostgres
                    ? query.OrderByDescending(x => x.JobName.ToLower()).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderByDescending(x => EF.Functions.Collate(x.JobName, "NOCASE")).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "category" => ascending
                ? isPostgres
                    ? query.OrderBy(x => x.Category.ToLower()).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderBy(x => EF.Functions.Collate(x.Category, "NOCASE")).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                : isPostgres
                    ? query.OrderByDescending(x => x.Category.ToLower()).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                    : query.OrderByDescending(x => EF.Functions.Collate(x.Category, "NOCASE")).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "status" => ascending
                ? query.OrderBy(x => x.DownloadStatus).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.DownloadStatus).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "size" => ascending
                ? query.OrderBy(x => x.TotalSegmentBytes).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.TotalSegmentBytes).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "completed" => ascending
                ? query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                : query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
        };
    }
}
