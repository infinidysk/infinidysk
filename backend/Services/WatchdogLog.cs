using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Persists watchdog entries to SQLite. Surfaced via the Watchdog settings
/// tab so users can see what was tried, what failed, and what won — across
/// restarts. Writes are fire-and-forget so playback never blocks on a DB
/// hiccup. Cleanup happens both on cascade (queue/history deletion) and via
/// a time-based background purge.
/// </summary>
public class WatchdogLog
{
    public void Record(WatchdogEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var ctx = new DavDatabaseContext();
                ctx.WatchdogEntries.Add(entry);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
            {
                Log.Warning(e, "Failed to persist watchdog entry: {Message}", e.Message);
            }
        });
    }

    public async Task<IReadOnlyList<WatchdogEntry>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var ctx = new DavDatabaseContext();
        return await ctx.WatchdogEntries
            .AsNoTracking()
            .OrderByDescending(x => x.AttemptedAt)
            .ThenByDescending(x => x.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
