using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Models.Nzb;

public class NzbFile
{
    public required string Subject { get; init; }
    public List<NzbSegment> Segments { get; } = [];
    private bool _rejectInferredSegmentByteRanges;

    /// <summary>
    /// Records the second segment's exact yEnc range so <see cref="GetSegmentByteRanges"/>
    /// validates its uniform-size inference against more than the first and final segment.
    /// Files with fewer than three segments need no middle-segment validation.
    /// </summary>
    public async Task ProbeSecondSegmentRangeAsync(
        INntpClient client,
        long fileSize,
        CancellationToken ct)
    {
        if (Segments.Count < 3) return;

        var firstRange = Segments[0].ByteRange;
        if (firstRange is null || firstRange.Count <= 0) return;

        // From this point, do not persist first+last inference unless the middle
        // probe succeeds and confirms the expected uniform split.
        _rejectInferredSegmentByteRanges = true;

        try
        {
            var secondRange = Segments[1].ByteRange;
            if (secondRange is null)
            {
                var headers = await client.GetYencHeadersAsync(
                    Segments[1].MessageId, ct).ConfigureAwait(false);
                secondRange = LongRange.FromStartAndSize(headers.PartOffset, headers.PartSize);
                Segments[1].ByteRange = secondRange;
            }

            if (secondRange.StartInclusive != firstRange.EndExclusive ||
                secondRange.Count != firstRange.Count)
                return;

            _rejectInferredSegmentByteRanges = false;

            // A PAR2 descriptor can provide fileSize without fetching the final article.
            // Materialize its range when first + second establish the usual uniform split,
            // allowing the existing contiguous-range validator to persist all offsets.
            if (Segments[^1].ByteRange is null)
            {
                var finalStart = checked(firstRange.Count * (Segments.Count - 1));
                var finalSize = fileSize - finalStart;
                if (finalSize is > 0 && finalSize <= firstRange.Count)
                    Segments[^1].ByteRange = LongRange.FromStartAndSize(finalStart, finalSize);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Known transport failures get a single human-friendly Warning line;
            // unexpected failures keep the stack. Playback still works either way,
            // it just falls back to header probes when seeking.
            e.LogWarningKnownOrStack(
                "Second segment range probe failed for {FileName}; seeking will fall back to NNTP header probes.",
                GetSubjectFileName());
        }
    }

    /// <summary>
    /// Sort by segment number (when all present) and drop duplicates so every
    /// consumer sees one logical segment per ordinal / message-id.
    /// Duplicate numbers keep alternate MessageIds on the primary as ordered fallbacks.
    /// </summary>
    public void CanonicalizeSegments()
    {
        if (Segments.Count <= 1) return;

        var allNumbered = Segments.All(s => s.Number is not null);
        if (allNumbered)
        {
            // Stable OrderBy preserves document order within the same number.
            var ordered = Segments
                .OrderBy(s => s.Number!.Value)
                .ToList();

            var deduped = new List<NzbSegment>(ordered.Count);
            var duplicateCount = 0;
            NzbSegment? primary = null;
            List<string>? fallbacks = null;
            foreach (var segment in ordered)
            {
                if (primary is not null && primary.Number == segment.Number)
                {
                    duplicateCount++;
                    if (!string.Equals(segment.MessageId, primary.MessageId, StringComparison.Ordinal))
                    {
                        fallbacks ??= [];
                        fallbacks.Add(segment.MessageId);
                    }

                    continue;
                }

                if (primary is not null)
                {
                    primary.FallbackMessageIds = fallbacks is { Count: > 0 } ? fallbacks.ToArray() : [];
                    deduped.Add(primary);
                    fallbacks = null;
                }

                primary = segment;
            }

            if (primary is not null)
            {
                primary.FallbackMessageIds = fallbacks is { Count: > 0 } ? fallbacks.ToArray() : [];
                deduped.Add(primary);
            }

            if (duplicateCount > 0)
            {
                Log.Warning(
                    "NZB file {Subject} contained {Count} duplicate segment(s); deduplicated",
                    Subject, duplicateCount);
            }

            Segments.Clear();
            Segments.AddRange(deduped);
            return;
        }

        // Numbers missing/partial: drop exact duplicate MessageIds only.
        // Same MessageId has nothing to fall back to — keep current drop behavior.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<NzbSegment>(Segments.Count);
        var droppedIds = 0;
        foreach (var segment in Segments)
        {
            if (!seenIds.Add(segment.MessageId))
            {
                droppedIds++;
                continue;
            }

            kept.Add(segment);
        }

        if (droppedIds > 0)
        {
            Log.Warning(
                "NZB file {Subject} contained {Count} duplicate segment(s); deduplicated",
                Subject, droppedIds);
            Segments.Clear();
            Segments.AddRange(kept);
        }
    }

    public string[] GetSegmentIds()
    {
        return Segments
            .Select(x => x.MessageId)
            .ToArray();
    }

    public string[][] GetSegmentFallbackIds() =>
        Segments.Select(s => s.FallbackMessageIds).ToArray();

    public LongRange[]? GetSegmentByteRanges()
    {
        var ranges = Segments
            .Select(x => x.ByteRange)
            .ToArray();

        if (ranges.Length == 0) return null;

        if (ranges.All(x => x is not null))
            return ValidateSegmentByteRanges(ranges.Select(x => x!).ToArray());

        if (_rejectInferredSegmentByteRanges) return null;

        var firstRange = ranges[0];
        var lastRange = ranges[^1];
        if (firstRange is null || lastRange is null ||
            firstRange.StartInclusive != 0 || firstRange.Count <= 0 || lastRange.Count <= 0)
            return null;

        try
        {
            var inferredRanges = Enumerable.Range(0, ranges.Length)
                .Select(index =>
                {
                    var start = checked(firstRange.Count * index);
                    var end = index == ranges.Length - 1
                        ? lastRange.EndExclusive
                        : checked(start + firstRange.Count);
                    return new LongRange(start, end);
                })
                .ToArray();

            if (inferredRanges[^1].StartInclusive != lastRange.StartInclusive) return null;

            for (var i = 0; i < ranges.Length; i++)
            {
                if (ranges[i] is { } knownRange &&
                    (knownRange.StartInclusive != inferredRanges[i].StartInclusive ||
                     knownRange.EndExclusive != inferredRanges[i].EndExclusive))
                    return null;
            }

            return ValidateSegmentByteRanges(inferredRanges);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static LongRange[]? ValidateSegmentByteRanges(LongRange[] ranges)
    {
        if (ranges[0].StartInclusive != 0) return null;

        for (var i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Count <= 0) return null;
            if (i > 0 && ranges[i - 1].EndExclusive != ranges[i].StartInclusive) return null;
        }

        return ranges;
    }

    public long GetTotalYencodedSize()
    {
        return Segments
            .Select(x => x.Bytes)
            .Sum();
    }

    public string GetSubjectFileName()
    {
        return GetFirstValidNonEmptyFilename(
            TryParseSubjectFilename1,
            TryParseSubjectFilename2
        );
    }

    private string TryParseSubjectFilename1()
    {
        // The most common format is when filename appears in double quotes
        // example: `[1/8] - "file.mkv" yEnc 12345 (1/54321)`
        var match = Regex.Match(Subject, "\\\"(.*)\\\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private string TryParseSubjectFilename2()
    {
        // Otherwise, use sabnzbd's regex
        // https://github.com/sabnzbd/sabnzbd/blob/b6b0d10367fd4960bad73edd1d3812cafa7fc002/sabnzbd/nzbstuff.py#L106
        var match = Regex.Match(Subject, @"\b([\w\-+()' .,]+(?:\[[\w\-\/+()' .,]*][\w\-+()' .,]*)*\.[A-Za-z0-9]{2,4})\b");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string GetFirstValidNonEmptyFilename(params Func<string>[] funcs)
    {
        return funcs
            .Select(x => x.Invoke())
            .Where(x => x == Path.GetFileName(x))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
    }
}
