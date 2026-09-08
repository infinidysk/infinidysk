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

    internal async Task<List<BenchmarkSegment>> GetSegmentPoolAsync(int maxSegments, CancellationToken ct)
    {
        var pool = new List<BenchmarkSegment>(Math.Min(maxSegments, 4096));
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
        List<BenchmarkSegment> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var recentNzbItems = await db.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile && x.SubType == DavItem.ItemSubType.NzbFile)
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxNzbFilesToConsider)
            .ToListAsync(ct).ConfigureAwait(false);

        var sources = new List<IReadOnlyList<BenchmarkSegment>>();
        foreach (var item in RankCompletedCandidates(recentNzbItems, DateTimeOffset.UtcNow))
        {
            var nzbFile = await db.GetDavNzbFileAsync(item, ct).ConfigureAwait(false);
            if (nzbFile?.SegmentIds is { Length: > 0 } ids)
            sources.Add(BuildSegments(ids, nzbFile.SegmentFallbackIds));
        }

        AddSegmentsRoundRobin(pool, seen, sources, maxSegments);
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
        List<BenchmarkSegment> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var queuedNzbs = await db.Ctx.QueueNzbContents
            .Take(MaxQueueNzbsToScan)
            .ToListAsync(ct).ConfigureAwait(false);

        var sources = new List<IReadOnlyList<BenchmarkSegment>>();
        foreach (var queued in queuedNzbs)
        {
            if (string.IsNullOrWhiteSpace(queued.NzbContents)) continue;
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(queued.NzbContents));
                var doc = await NzbDocument.LoadAsync(stream, ct).ConfigureAwait(false);
                foreach (var file in doc.Files)
                    sources.Add(BuildSegments(file.GetSegmentIds(), file.GetSegmentFallbackIds()));
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
            {
                Log.Debug(e, "Skipping unparseable queued nzb during benchmark corpus build.");
            }
        }

        AddSegmentsRoundRobin(pool, seen, sources, maxSegments);
    }

    internal static void AddSegmentsRoundRobin(
        List<BenchmarkSegment> pool,
        HashSet<string> seen,
        IReadOnlyList<IReadOnlyList<BenchmarkSegment>> sources,
        int maxSegments)
    {
        for (var index = 0; pool.Count < maxSegments; index++)
        {
            var anySourceHadSegment = false;
            foreach (var source in sources)
            {
                if (index >= source.Count) continue;
                anySourceHadSegment = true;

                var segment = source[index];
                if (string.IsNullOrWhiteSpace(segment.PrimaryId) || !seen.Add(segment.PrimaryId)) continue;

                var fallbacks = segment.FallbackIds
                    .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    .ToArray();
                pool.Add(new BenchmarkSegment(segment.PrimaryId, fallbacks));
                if (pool.Count >= maxSegments) return;
            }

            if (!anySourceHadSegment) return;
        }
    }

    private static List<BenchmarkSegment> BuildSegments(string[] ids, string[][]? fallbacks) =>
        ids.Select((id, index) => new BenchmarkSegment(
                id,
                fallbacks is not null && index < fallbacks.Length ? fallbacks[index] : []))
            .ToList();

    // Shuffle so sequential nzb ordering doesn't bias which segments land in the
    // smaller windows, and so retries don't keep hammering the same first article.
    private static void Shuffle(List<BenchmarkSegment> list)
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
