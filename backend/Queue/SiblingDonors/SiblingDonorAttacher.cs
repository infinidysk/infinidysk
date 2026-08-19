using MemoryPack;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Queue.SiblingDonors;

/// <summary>
/// Precomputes cross-NZB donor MessageIds into per-segment fallback lists at import
/// (forward) and completion (bidirectional backfill) so playback can recover holes
/// through the existing fallback arrays.
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
                    foreach (var donor in document.Files.Where(donor => IsDonorMatch(primary, donor)))
                    {
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

    public static async Task BackfillCompletedSiblingsAsync(
        DavDatabaseClient dbClient,
        QueueItem queueItem,
        NzbDocument nzb,
        ConfigManager configManager,
        CancellationToken ct)
    {
        try
        {
            if (!configManager.IsVariantsSegmentDonorsEnabled()) return;
            if (string.IsNullOrEmpty(queueItem.ContentGroupKey)) return;

            var maxSiblings = configManager.GetVariantsSegmentDonorsMaxSiblings();
            if (maxSiblings <= 0) return;
            var maxPerSegment = configManager.GetVariantsSegmentDonorsMaxPerSegment();

            var siblings = await LoadCompletedSiblingsAsync(
                    dbClient.Ctx, queueItem.ContentGroupKey, maxSiblings, ct)
                .ConfigureAwait(false);
            if (siblings.Count == 0) return;

            var newFiles = nzb.Files.Where(file => file.Segments.Count > 0).ToList();
            var newFilesByKey = IndexBySegmentIds(newFiles);
            // Snapshot before staging sibling clones so those pending writes are not
            // mistaken for this import's FileAggregator blobs.
            var pendingNewBlobs = dbClient.Ctx.BlobNzbFiles.ToArray();

            foreach (var sibling in siblings)
            {
                ct.ThrowIfCancellationRequested();
                var document = await LoadSiblingDocumentAsync(sibling.NzbBlobId!.Value, ct)
                    .ConfigureAwait(false);
                if (document is null) continue;

                var siblingFilesByKey = IndexBySegmentIds(document.Files);
                await BackfillSiblingBlobsAsync(
                        dbClient.Ctx,
                        sibling.Id,
                        siblingFilesByKey,
                        newFiles,
                        maxPerSegment,
                        ct)
                    .ConfigureAwait(false);

                foreach (var pending in pendingNewBlobs)
                    MergeSiblingIntoPendingBlob(pending, newFilesByKey, document.Files, maxPerSegment);
            }
        }
#pragma warning disable CA2016 // classify cancellation regardless of the ambient token
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            e.LogWarningKnownOrStack(
                "Sibling segment-donor backfill skipped for {JobName}.", queueItem.JobName);
        }
    }

    private static async Task BackfillSiblingBlobsAsync(
        DavDatabaseContext ctx,
        Guid siblingHistoryId,
        Dictionary<IReadOnlyList<string>, NzbFile> siblingFilesByKey,
        List<NzbFile> newFiles,
        int maxPerSegment,
        CancellationToken ct)
    {
        var davItems = await ctx.Items
            .Where(d => d.HistoryItemId == siblingHistoryId
                        && d.SubType == DavItem.ItemSubType.NzbFile
                        && d.FileBlobId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var davItem in davItems)
        {
            ct.ThrowIfCancellationRequested();
            var copy = await DeserializeDavNzbFileCopyAsync(davItem.FileBlobId!.Value, ct)
                .ConfigureAwait(false);
            if (copy is null || copy.SegmentIds.Length == 0) continue;
            if (!siblingFilesByKey.TryGetValue(copy.SegmentIds, out var siblingFile))
                continue;

            var newFile = FindMatchingFile(newFiles, siblingFile);
            if (newFile is null) continue;

            var donorPrimaries = newFile.GetSegmentIds();
            if (donorPrimaries.Length != copy.SegmentIds.Length) continue;
            if (!MergePrimaryIdsIntoDavNzbFile(copy, donorPrimaries, maxPerSegment))
                continue;

            var newBlobId = Guid.NewGuid();
            copy.Id = newBlobId;
            ctx.AddBlob(copy);
            davItem.FileBlobId = newBlobId;
        }
    }

    private static void MergeSiblingIntoPendingBlob(
        DavNzbFile pending,
        Dictionary<IReadOnlyList<string>, NzbFile> newFilesByKey,
        IReadOnlyList<NzbFile> siblingFiles,
        int maxPerSegment)
    {
        if (pending.SegmentIds.Length == 0) return;
        if (!newFilesByKey.TryGetValue(pending.SegmentIds, out var newFile))
            return;

        foreach (var siblingFile in siblingFiles.Where(siblingFile => IsDonorMatch(newFile, siblingFile)))
        {
            MergeDonorIdsIntoDavNzbFile(pending, siblingFile, maxPerSegment);
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
        foreach (var id in candidates.Where(id => !string.IsNullOrEmpty(id)))
        {
            if (!seen.Add(id)) continue;
            extra ??= [.. existing];
            extra.Add(id);
            if (extra.Count >= maxPerSegment) break;
        }

        if (extra is null) return false;
        updated = extra.ToArray();
        existing = updated;
        return true;
    }

    internal static Dictionary<IReadOnlyList<string>, NzbFile> IndexBySegmentIds(IEnumerable<NzbFile> files)
    {
        var map = new Dictionary<IReadOnlyList<string>, NzbFile>(SegmentIdListComparer.Instance);
        foreach (var file in files.Where(file => file.Segments.Count > 0))
        {
            map.TryAdd(file.GetSegmentIds(), file);
        }

        return map;
    }

    internal static NzbFile? FindMatchingFile(IEnumerable<NzbFile> files, NzbFile target)
    {
        return files.FirstOrDefault(file => IsDonorMatch(file, target));
    }

    internal static bool MergePrimaryIdsIntoDavNzbFile(
        DavNzbFile target,
        string[] donorPrimaries,
        int maxPerSegment)
    {
        EnsureFallbackSlots(target);
        var added = false;
        var count = Math.Min(target.SegmentIds.Length, donorPrimaries.Length);
        for (var i = 0; i < count; i++)
        {
            var existing = target.SegmentFallbackIds![i];
            if (!AppendIds(ref existing, target.SegmentIds[i], [donorPrimaries[i]], maxPerSegment, out var updated))
                continue;
            target.SegmentFallbackIds[i] = updated;
            added = true;
        }

        return added;
    }

    internal static bool MergeDonorIdsIntoDavNzbFile(
        DavNzbFile target,
        NzbFile donor,
        int maxPerSegment)
    {
        EnsureFallbackSlots(target);
        var added = false;
        var count = Math.Min(target.SegmentIds.Length, donor.Segments.Count);
        for (var i = 0; i < count; i++)
        {
            var existing = target.SegmentFallbackIds![i];
            if (!AppendIds(
                    ref existing,
                    target.SegmentIds[i],
                    DonorIds(donor.Segments[i]),
                    maxPerSegment,
                    out var updated))
                continue;
            target.SegmentFallbackIds[i] = updated;
            added = true;
        }

        return added;
    }

    internal static void EnsureFallbackSlots(DavNzbFile file)
    {
        var n = file.SegmentIds.Length;
        if (file.SegmentFallbackIds is { Length: var len } && len == n)
        {
            for (var i = 0; i < n; i++)
                file.SegmentFallbackIds[i] ??= [];
            return;
        }

        var aligned = new string[n][];
        for (var i = 0; i < n; i++)
        {
            aligned[i] = file.SegmentFallbackIds is { Length: var l } && i < l
                ? file.SegmentFallbackIds[i] ?? []
                : [];
        }

        file.SegmentFallbackIds = aligned;
    }

    internal static async Task<DavNzbFile?> DeserializeDavNzbFileCopyAsync(Guid blobId, CancellationToken ct)
    {
        await using var stream = BlobStore.ReadBlob(blobId);
        if (stream is null) return null;

        try
        {
            await using var decompression = new DecompressionStream(stream);
            return await MemoryPackSerializer
                .DeserializeAsync<DavNzbFile>(decompression, cancellationToken: ct)
                .ConfigureAwait(false);
        }
#pragma warning disable CA2016 // classify cancellation regardless of the ambient token
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(e, "Sibling DavNzbFile blob {BlobId} is unreadable; skipping donor backfill.", blobId);
            return null;
        }
    }

    private sealed class SegmentIdListComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static SegmentIdListComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
            => ReferenceEquals(x, y) ||
               (x is not null && y is not null && x.Count == y.Count &&
                x.SequenceEqual(y, StringComparer.Ordinal));

        public int GetHashCode(IReadOnlyList<string> segmentIds)
        {
            var hash = new HashCode();
            foreach (var segmentId in segmentIds)
                hash.Add(segmentId, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
