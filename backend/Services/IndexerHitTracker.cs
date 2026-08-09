using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

// Tracks per-indexer API search hits and NZB download hits against optional limits,
// using either a fixed daily reset hour (UTC) or a rolling 24-hour window — mirroring
// NZBHydra2's hitLimit / downloadLimit / hitLimitResetTime semantics.
//
// Hits are persisted in the IndexerApiHits table so counters survive restarts. The
// table is opportunistically pruned of rows older than the longest possible window
// (a few days) on each record to keep it bounded without a background job.
public class IndexerHitTracker
{
    // Keep at most this much history. Longest enforced window is 24h either way, so
    // 48h gives a safety buffer for clock drift / DST oddities.
    private static readonly TimeSpan RetainFor = TimeSpan.FromHours(48);

    // Random chance to prune on each record. Amortises cleanup across writes without
    // a background timer or sweeper service.
    private const double PruneProbability = 0.05;

    private static readonly Random Rng = new();

    public record HitCheckResult(bool Allowed, int CurrentCount, int Limit, DateTimeOffset ResetAt);

    // Per-indexer snapshot of current hit counts in the active reset window. Limits are
    // included as nullable for the unlimited case; ResetAt is the next reset boundary.
    public record UsageSnapshot(
        string IndexerName,
        int ApiHits,
        int? ApiHitLimit,
        int DownloadHits,
        int? DownloadHitLimit,
        DateTimeOffset ResetAt);

    // Returns null when no limit is configured (always allowed). Otherwise returns the
    // current count and the moment the window next clears so the caller can show a reason.
    public async Task<HitCheckResult?> CheckAsync(
        string indexerName,
        IndexerApiHit.HitType type,
        int? limit,
        int? resetHourUtc,
        CancellationToken ct)
    {
        if (limit is null || limit <= 0) return null;

        var now = DateTimeOffset.UtcNow;
        var (windowStart, nextResetAt) = ComputeWindow(now, resetHourUtc);

        await using var ctx = new DavDatabaseContext();
        var count = await ctx.IndexerApiHits
            .AsNoTracking()
            .Where(x => x.IndexerName == indexerName
                        && x.Type == type
                        && x.AccessedAt >= windowStart)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return new HitCheckResult(count < limit.Value, count, limit.Value, nextResetAt);
    }

    // Returns one snapshot per requested indexer with both API and download hit counts
    // since the start of the current window. Used by the overview to render usage bars
    // for every configured indexer in a single query.
    public async Task<List<UsageSnapshot>> GetUsageAsync(
        IReadOnlyList<(string Name, int? HitLimit, int? DownloadLimit, int? ResetHourUtc)> indexers,
        CancellationToken ct)
    {
        if (indexers.Count == 0) return new List<UsageSnapshot>();

        var now = DateTimeOffset.UtcNow;
        var names = indexers.Select(i => i.Name).ToList();

        // Compute the widest window we need (the earliest windowStart across all indexers)
        // and pull every hit since then in one query, then bucket per-indexer below.
        DateTimeOffset earliestWindowStart = indexers
            .Select(i => ComputeWindow(now, i.ResetHourUtc).WindowStart)
            .Min();

        await using var ctx = new DavDatabaseContext();
        var hits = await ctx.IndexerApiHits
            .AsNoTracking()
            .Where(x => names.Contains(x.IndexerName) && x.AccessedAt >= earliestWindowStart)
            .Select(x => new { x.IndexerName, x.Type, x.AccessedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<UsageSnapshot>(indexers.Count);
        foreach (var i in indexers)
        {
            var (windowStart, nextResetAt) = ComputeWindow(now, i.ResetHourUtc);
            var indexerHits = hits.Where(h => h.IndexerName == i.Name && h.AccessedAt >= windowStart);
            var apiCount = indexerHits.Count(h => h.Type == IndexerApiHit.HitType.Search);
            var downloadCount = indexerHits.Count(h => h.Type == IndexerApiHit.HitType.Download);
            result.Add(new UsageSnapshot(
                IndexerName: i.Name,
                ApiHits: apiCount,
                ApiHitLimit: i.HitLimit > 0 ? i.HitLimit : null,
                DownloadHits: downloadCount,
                DownloadHitLimit: i.DownloadLimit > 0 ? i.DownloadLimit : null,
                ResetAt: nextResetAt));
        }
        return result;
    }

    public async Task RecordAsync(
        string indexerName,
        IndexerApiHit.HitType type,
        CancellationToken ct)
    {
        try
        {
            await using var ctx = new DavDatabaseContext();
            ctx.IndexerApiHits.Add(new IndexerApiHit
            {
                IndexerName = indexerName,
                Type = type,
                AccessedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            if (Rng.NextDouble() < PruneProbability)
                _ = Task.Run(() => PruneAsync(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception e)
        {
            // Hit tracking is best-effort — don't fail the user's request if the DB
            // write hiccups. Just log and move on.
            Log.Warning(e, "Failed to record indexer hit for {Indexer} ({Type})", indexerName, type);
        }
    }

    private static async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - RetainFor;
            await using var ctx = new DavDatabaseContext();
            await ctx.IndexerApiHits
                .Where(x => x.AccessedAt < cutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Indexer hit pruning failed");
        }
    }

    // Returns the start of the current window (inclusive) and the moment it next
    // resets. When resetHour is null, mimic NZBHydra2's rolling 24h: window = [now-24h, now].
    private static (DateTimeOffset WindowStart, DateTimeOffset NextResetAt) ComputeWindow(
        DateTimeOffset now,
        int? resetHourUtc)
    {
        if (resetHourUtc is not int hour || hour < 0 || hour > 23)
        {
            return (now.AddHours(-24), now.AddHours(24));
        }

        var todayAtHour = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, TimeSpan.Zero);
        var windowStart = todayAtHour <= now ? todayAtHour : todayAtHour.AddDays(-1);
        var nextReset = windowStart.AddDays(1);
        return (windowStart, nextReset);
    }

    public static string FormatSkipReason(HitCheckResult r, IndexerApiHit.HitType type)
    {
        var typeLabel = type == IndexerApiHit.HitType.Search ? "API hit" : "download";
        var remaining = r.ResetAt - DateTimeOffset.UtcNow;
        var human = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"{Math.Max(1, remaining.Minutes)}m";
        return $"{typeLabel} limit reached ({r.CurrentCount}/{r.Limit}, resets in {human})";
    }
}
