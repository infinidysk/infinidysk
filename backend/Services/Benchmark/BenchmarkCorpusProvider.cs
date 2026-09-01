using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using Serilog;

namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// Builds a pool of real, re-downloadable NNTP message-ids for the speed test to
/// fetch. We reuse articles the user has already downloaded (or queued) rather
/// than inventing synthetic ones, so the test measures the same kind of traffic
/// real downloads produce. Returns an empty list (gracefully) when there's
/// nothing to draw from — the caller falls back to a latency-only result.
/// </summary>
public sealed class BenchmarkCorpusProvider(DavDatabaseClient db)
{
    // Consider a wider recent window, then rank and crack open only the best ones.
    private const int MaxNzbFilesToConsider = 200;
    // Cap how many nzb files / queue entries we crack open. A handful of large
    // healthy releases already yields thousands of segments.
    private const int MaxNzbFilesToScan = 60;
    private const int MaxQueueNzbsToScan = 10;
    private static readonly TimeSpan RecentHealthWindow = TimeSpan.FromDays(30);

    public async Task<List<string>> GetSegmentPoolAsync(int maxSegments, CancellationToken ct)
    {
        var pool = new List<string>(Math.Min(maxSegments, 4096));
        var seen = new HashSet<string>();

        try
        {
            await CollectFromCompletedFilesAsync(pool, seen, maxSegments, ct).ConfigureAwait(false);

            // Only crack open queued nzbs if completed downloads didn't give us much.
            if (pool.Count < maxSegments / 4)
                await CollectFromQueuedNzbsAsync(pool, seen, maxSegments, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
        {
            // A corpus hiccup shouldn't sink the whole benchmark — degrade to
            // whatever we managed to gather (possibly latency-only).
            e.LogWarningKnownOrStack("Benchmark corpus gathering failed; using {Count} segments.", pool.Count);
        }

        Shuffle(pool);
        return pool;
    }

    // Primary source: segment ids persisted for previously-downloaded files.
    private async Task CollectFromCompletedFilesAsync(
        List<string> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var recentNzbItems = await db.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile && x.SubType == DavItem.ItemSubType.NzbFile)
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxNzbFilesToConsider)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in RankCompletedCandidates(recentNzbItems, DateTimeOffset.UtcNow))
        {
            if (pool.Count >= maxSegments) return;
            var nzbFile = await db.GetDavNzbFileAsync(item, ct).ConfigureAwait(false);
            if (nzbFile?.SegmentIds is { Length: > 0 } ids)
                AddIds(pool, seen, ids, maxSegments);
        }
    }

    /// <summary>
    /// Prefer recently health-checked files, then larger files (more unique segments),
    /// then fresher CreatedAt. Caps the crack-open list at <see cref="MaxNzbFilesToScan"/>.
    /// </summary>
    internal static List<DavItem> RankCompletedCandidates(
        IEnumerable<DavItem> candidates, DateTimeOffset now)
    {
        var healthCutoff = now - RecentHealthWindow;
        return candidates
            .OrderByDescending(x => x.LastHealthCheck >= healthCutoff)
            .ThenByDescending(x => x.FileSize ?? 0)
            .ThenByDescending(x => x.CreatedAt)
            .Take(MaxNzbFilesToScan)
            .ToList();
    }

    // Fallback source: parse the raw nzb xml of items still sitting in the queue.
    private async Task CollectFromQueuedNzbsAsync(
        List<string> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var queuedNzbs = await db.Ctx.QueueNzbContents
            .Take(MaxQueueNzbsToScan)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var queued in queuedNzbs)
        {
            if (pool.Count >= maxSegments) return;
            if (string.IsNullOrWhiteSpace(queued.NzbContents)) continue;
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(queued.NzbContents));
                var doc = await NzbDocument.LoadAsync(stream, ct).ConfigureAwait(false);
                foreach (var file in doc.Files)
                {
                    AddIds(pool, seen, file.GetSegmentIds(), maxSegments);
                    if (pool.Count >= maxSegments) return;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
            {
                Log.Debug(e, "Skipping unparseable queued nzb during benchmark corpus build.");
            }
        }
    }

    private static void AddIds(List<string> pool, HashSet<string> seen, IEnumerable<string> ids, int maxSegments)
    {
        foreach (var id in ids)
        {
            if (pool.Count >= maxSegments) return;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) pool.Add(id);
        }
    }

    // Shuffle so sequential nzb ordering doesn't bias which segments land in the
    // smaller windows, and so retries don't keep hammering the same first article.
    private static void Shuffle(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
#pragma warning disable CA5394 // benchmark corpus shuffling is not security-sensitive
            var j = Random.Shared.Next(i + 1);
#pragma warning restore CA5394
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
