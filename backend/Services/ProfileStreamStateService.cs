using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class ProfileStreamStateService(
    DavDatabaseClient dbClient,
    CandidateNegativeCache negativeCache)
{
    public async Task<IReadOnlySet<string>> GetReadyNzbFileNamesAsync(
        IReadOnlyList<NzbResolutionCache.Candidate> candidates,
        CancellationToken ct)
    {
        var names = candidates
            .Select(candidate => ProfileReleaseName.ToNzbFileName(candidate.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var brokenIds = negativeCache.SnapshotBrokenHistoryItems();
            var loweredNames = names.Select(name => name.ToLowerInvariant()).ToList();
            var historyQuery = dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(history =>
#pragma warning disable CA1311 // EF translates ToLower() to SQL LOWER(); ToLowerInvariant is not translatable.
                    loweredNames.Contains(history.FileName.ToLower())
#pragma warning restore CA1311
                    && history.DownloadStatus == HistoryItem.DownloadStatusOption.Completed);
            if (brokenIds.Count > 0)
                historyQuery = historyQuery.Where(history => !brokenIds.Contains(history.Id));

            var rows = await (
                    from history in historyQuery
                    join item in dbClient.Ctx.Items.AsNoTracking()
                        on history.Id equals item.HistoryItemId
                    where item.Type == DavItem.ItemType.UsenetFile
                    select new { history.FileName, item.Name })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var ready = new HashSet<string>(
                rows.GroupBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Any(row => ContentTypeUtil.GetContentType(row.Name)
                        .StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
                    .Select(group => group.Key),
                StringComparer.OrdinalIgnoreCase);

            return ready;
        }
        catch (Exception e) when (
            e is DbUpdateException
                or InvalidOperationException
                or SqliteException
                or NpgsqlException)
        {
            Log.Debug(e, "Profile stream ready-state lookup failed");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
