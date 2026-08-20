using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public class WatchtowerStore(PreflightCache preflightCache, IDbContextFactory<DavDatabaseContext> dbContextFactory)
{
    public async Task TryWarmCacheAsync(string type, string contentId, CancellationToken ct)
    {
        try
        {
            await using var ctx = dbContextFactory.CreateDbContext();
            if (await TryWarmKeyAsync(ctx, $"{type}:{contentId}", ct).ConfigureAwait(false)) return;

            if (type == "series")
            {
                var parts = contentId.Split(':');
                if (parts.Length >= 3 && int.TryParse(parts[^2], out var season))
                    await TryWarmKeyAsync(ctx, $"season:{parts[0]}:{season}", ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            Log.Debug(e, "Watchtower: cache warm failed for {Type}/{Id}", type, contentId);
        }
    }

    private async Task<bool> TryWarmKeyAsync(DavDatabaseContext ctx, string key, CancellationToken ct)
    {
        var item = await ctx.WantedItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Key == key, ct)
            .ConfigureAwait(false);
        if (item is null || item.State != WantedItem.StateReady || item.WinnerNzb is null) return false;

        var winner = WtJson.ReadPointers(item.Shortlist).FirstOrDefault();
        if (winner is null || winner.Verdict != "available") return false;

        preflightCache.SetVerified(winner.NzbUrl, item.WinnerNzb,
            PlaybackFastVerifier.Verdict.Available, item.ResponderHost);
        Log.Debug("Watchtower: warmed cache for {Key} ({Title})", key, winner.Title);
        return true;
    }
}
