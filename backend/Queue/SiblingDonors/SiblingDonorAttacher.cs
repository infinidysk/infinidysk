using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using Serilog;

namespace NzbWebDAV.Queue.SiblingDonors;

/// <summary>
/// Precomputes cross-NZB donor MessageIds into per-segment fallback lists at import
/// time so playback can recover holes through the existing fallback arrays.
/// </summary>
internal static class SiblingDonorAttacher
{
    public static async Task AttachToNewImportAsync(
        DavDatabaseClient dbClient,
        QueueItem queueItem,
        List<NzbFile> nzbFiles,
        ConfigManager configManager,
        CancellationToken ct)
    {
        try
        {
            if (!configManager.IsVariantsSegmentDonorsEnabled()) return;
            if (string.IsNullOrEmpty(queueItem.ContentGroupKey)) return;
            if (nzbFiles.Count == 0) return;

            var maxSiblings = configManager.GetVariantsSegmentDonorsMaxSiblings();
            if (maxSiblings <= 0) return;
            var maxPerSegment = configManager.GetVariantsSegmentDonorsMaxPerSegment();

            var siblings = await LoadCompletedSiblingsAsync(
                    dbClient.Ctx, queueItem.ContentGroupKey, maxSiblings, ct)
                .ConfigureAwait(false);
            if (siblings.Count == 0) return;

            var contributing = 0;
            foreach (var sibling in siblings)
            {
                ct.ThrowIfCancellationRequested();
                if (contributing >= maxSiblings) break;

                var document = await LoadSiblingDocumentAsync(sibling.NzbBlobId!.Value, ct)
                    .ConfigureAwait(false);
                if (document is null) continue;

                var added = 0;
                foreach (var primary in nzbFiles)
                {
                    foreach (var donor in document.Files)
                    {
                        if (!IsDonorMatch(primary, donor)) continue;
                        added += MergeDonorIds(primary, donor, maxPerSegment);
                    }
                }

                if (added > 0)
                {
                    contributing++;
                    Log.Debug(
                        "Attached {Count} sibling donor MessageId(s) to {JobName} from sibling {SiblingJob}.",
                        added, queueItem.JobName, sibling.JobName);
                }
            }
        }
#pragma warning disable CA2016 // classify cancellation regardless of the ambient token
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            e.LogWarningKnownOrStack(
                "Sibling segment-donor attach skipped for {JobName}.", queueItem.JobName);
        }
    }

    internal static async Task<List<HistoryItem>> LoadCompletedSiblingsAsync(
        DavDatabaseContext ctx,
        string contentGroupKey,
        int maxSiblings,
        CancellationToken ct)
    {
        if (maxSiblings <= 0) return [];

        // Fetch a window larger than maxSiblings so identical postings (zero new IDs)
        // and unparseable blobs can be skipped without consuming the contributing cap.
        var fetch = Math.Min(32, Math.Max(maxSiblings * 4, maxSiblings));
        return await ctx.HistoryItems.AsNoTracking()
            .Where(h => h.ContentGroupKey == contentGroupKey
                        && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                        && h.NzbBlobId != null)
            .OrderByDescending(h => h.CreatedAt)
            .Take(fetch)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    internal static async Task<NzbDocument?> LoadSiblingDocumentAsync(Guid nzbBlobId, CancellationToken ct)
    {
        await using var stream = BlobStore.ReadBlob(nzbBlobId);
        if (stream is null)
        {
            Log.Debug("Sibling NZB blob {NzbBlobId} is missing; skipping donor attach.", nzbBlobId);
            return null;
        }

        try
        {
            return await NzbDocument.LoadAsync(stream, ct).ConfigureAwait(false);
        }
#pragma warning disable CA2016 // classify cancellation regardless of the ambient token
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(e, "Sibling NZB blob {NzbBlobId} is unparseable; skipping donor attach.", nzbBlobId);
            return null;
        }
    }

    internal static bool IsDonorMatch(NzbFile primary, NzbFile donor)
    {
        if (primary.Segments.Count == 0 || primary.Segments.Count != donor.Segments.Count)
            return false;
        if (primary.GetTotalYencodedSize() != donor.GetTotalYencodedSize())
            return false;

        for (var i = 0; i < primary.Segments.Count; i++)
        {
            if (primary.Segments[i].Bytes != donor.Segments[i].Bytes)
                return false;
        }

        var primaryName = primary.GetSubjectFileName();
        var donorName = donor.GetSubjectFileName();
        if (string.IsNullOrEmpty(primaryName) || string.IsNullOrEmpty(donorName))
            return false;

        return string.Equals(primaryName, donorName, StringComparison.OrdinalIgnoreCase);
    }

    internal static int MergeDonorIds(NzbFile primary, NzbFile donor, int maxPerSegment)
    {
        var added = 0;
        for (var i = 0; i < primary.Segments.Count; i++)
        {
            var segment = primary.Segments[i];
            var existing = segment.FallbackMessageIds;
            var before = existing.Length;
            if (!AppendIds(
                    ref existing,
                    segment.MessageId,
                    DonorIds(donor.Segments[i]),
                    maxPerSegment,
                    out var updated))
                continue;

            segment.FallbackMessageIds = updated;
            added += updated.Length - before;
        }

        return added;
    }

    internal static IEnumerable<string> DonorIds(NzbSegment segment)
    {
        yield return segment.MessageId;
        foreach (var id in segment.FallbackMessageIds)
            yield return id;
    }

    internal static string SegmentKey(IReadOnlyList<string> segmentIds) =>
        string.Join("\u0001", segmentIds);

    internal static bool AppendIds(
        ref string[] existing,
        string ownPrimary,
        IEnumerable<string> candidates,
        int maxPerSegment,
        out string[] updated)
    {
        existing ??= [];
        updated = existing;
        if (existing.Length >= maxPerSegment) return false;

        var seen = new HashSet<string>(existing, StringComparer.Ordinal) { ownPrimary };
        List<string>? extra = null;
        foreach (var id in candidates)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            extra ??= [.. existing];
            extra.Add(id);
            if (extra.Count >= maxPerSegment) break;
        }

        if (extra is null) return false;
        updated = extra.ToArray();
        existing = updated;
        return true;
    }
}
