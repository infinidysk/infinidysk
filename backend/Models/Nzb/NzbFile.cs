using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Models.Nzb;

public readonly record struct SegmentByteRangeIndex(
    LongRange[]? Ranges,
    bool IsTrusted);

public class NzbFile
{
    public required string Subject { get; init; }
    public List<NzbSegment> Segments { get; } = [];
    public Par2FileProof? VerificationProof { get; set; }
    private bool _rejectInferredSegmentByteRanges;
    private bool _inferredSegmentByteRangesValidated;

    internal const string OmittedSegmentDomain = "omitted.nzbdav.invalid";

    internal static string CreateOmittedSegmentId(string anchorMessageId, int number)
    {
        var anchor = Encoding.UTF8.GetBytes(NntpClient.NormalizeSegmentId(anchorMessageId));
        var digest = Convert.ToHexString(SHA256.HashData(anchor));
        return $"omitted-{number.ToString(CultureInfo.InvariantCulture)}-{digest}@{OmittedSegmentDomain}";
    }

    internal static bool IsOmittedSegmentId(string messageId) => Regex.IsMatch(
        NntpClient.NormalizeSegmentId(messageId),
        @"\Aomitted-[1-9][0-9]*-[0-9A-F]{64}@omitted\.nzbdav\.invalid\z",
        RegexOptions.CultureInvariant);

    internal async Task<bool> TryFillOmittedSegmentsAsync(
        UsenetYencHeader firstHeader,
        INntpClient client,
        CancellationToken ct)
    {
        if (!TryGetSparseLayout(out var totalParts)
            || firstHeader.PartNumber != 1
            || firstHeader.PartOffset != 0
            || firstHeader.PartSize <= 0
            || firstHeader.FileSize < firstHeader.PartSize)
            return false;

        var totalWasOmitted = firstHeader.HasTotalParts == false && firstHeader.TotalParts == 0;
        if (firstHeader.TotalParts != totalParts && !totalWasOmitted)
            return false;
        if ((firstHeader.FileSize - 1) / firstHeader.PartSize + 1 != totalParts)
            return false;

        LongRange? confirmedLastRange = null;
        if (totalWasOmitted)
        {
            UsenetYencHeader lastHeader;
            try
            {
                lastHeader = await client.GetYencHeadersAsync(Segments[^1].MessageId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              && exception is not OutOfMemoryException)
            {
                exception.LogWarningKnownOrStack(
                    "Could not confirm omitted NZB parts; retaining the listed segment layout.");
                return false;
            }

            var expectedStart = checked(firstHeader.PartSize * (totalParts - 1L));
            var lastTotalWasOmitted = lastHeader.HasTotalParts == false && lastHeader.TotalParts == 0;
            if (lastHeader.PartNumber != totalParts
                || lastHeader.FileSize != firstHeader.FileSize
                || lastHeader.PartOffset != expectedStart
                || lastHeader.PartSize != firstHeader.FileSize - expectedStart
                || (lastHeader.TotalParts != totalParts && !lastTotalWasOmitted))
                return false;
            confirmedLastRange = LongRange.FromStartAndSize(lastHeader.PartOffset, lastHeader.PartSize);
        }

        var expanded = BuildExpandedSegments(totalParts);
        if (confirmedLastRange is not null)
            expanded[^1].ByteRange = confirmedLastRange;
        Segments.Clear();
        Segments.AddRange(expanded);
        return true;
    }

    internal bool RestoreStoredOmittedSegments(IReadOnlySet<string> storedIds, Action<long> chargeMemory)
    {
        if (!TryGetSparseLayout(out var totalParts)) return false;

        var listedIndex = 0;
        for (var number = 1; number <= totalParts; number++)
        {
            var id = Segments[listedIndex].Number == number
                ? Segments[listedIndex++].MessageId
                : CreateOmittedSegmentId(Segments[0].MessageId, number);
            if (!storedIds.Contains(id)) return false;
        }

        chargeMemory(256L + totalParts * 512L);
        var expanded = BuildExpandedSegments(totalParts);
        Segments.Clear();
        Segments.AddRange(expanded);
        return true;
    }

    private bool TryGetSparseLayout(out int totalParts)
    {
        totalParts = 0;
        if (Segments.Count < 2 || Segments[0].Number != 1
            || Segments[^1].Number is not { } lastNumber
            || lastNumber <= Segments.Count || lastNumber > 2L * Segments.Count)
            return false;

        var previousNumber = 0;
        foreach (var segment in Segments)
        {
            if (segment.Number is not { } number || number <= previousNumber)
                return false;
            previousNumber = number;
        }

        totalParts = lastNumber;
        return true;
    }

    private List<NzbSegment> BuildExpandedSegments(int totalParts)
    {
        var expanded = new List<NzbSegment>(totalParts);
        var listedIndex = 0;
        var previousListedSegment = Segments[0];
        for (var number = 1; number <= totalParts; number++)
        {
            if (Segments[listedIndex].Number == number)
            {
                previousListedSegment = Segments[listedIndex++];
                expanded.Add(previousListedSegment);
            }
            else
            {
                expanded.Add(new NzbSegment
                {
                    Number = number,
                    MessageId = CreateOmittedSegmentId(Segments[0].MessageId, number),
                    Bytes = previousListedSegment.Bytes,
                });
            }
        }
        return expanded;
    }

    /// <summary>
    /// Records a listed interior segment's exact yEnc range so <see cref="GetSegmentByteRanges"/>
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
            var probeIndex = 1;
            while (probeIndex < Segments.Count && IsOmittedSegmentId(Segments[probeIndex].MessageId))
                probeIndex++;
            if (probeIndex == Segments.Count) return;

            var probeRange = Segments[probeIndex].ByteRange;
            if (probeRange is null)
            {
                var headers = await client.GetYencHeadersAsync(
                    Segments[probeIndex].MessageId, ct).ConfigureAwait(false);
                probeRange = LongRange.FromStartAndSize(headers.PartOffset, headers.PartSize);
                Segments[probeIndex].ByteRange = probeRange;
            }

            if (probeIndex == Segments.Count - 1)
            {
                if (Segments.Count == 3 && firstRange.StartInclusive == 0 && probeRange.Count > 0
                    && probeRange.StartInclusive > firstRange.EndExclusive && probeRange.EndExclusive == fileSize)
                    Segments[1].ByteRange = new LongRange(firstRange.EndExclusive, probeRange.StartInclusive);
                return;
            }

            if (probeRange.StartInclusive != checked(firstRange.Count * probeIndex) ||
                probeRange.Count != firstRange.Count)
                return;

            _rejectInferredSegmentByteRanges = false;
            _inferredSegmentByteRangesValidated = true;

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
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
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

    public LongRange[]? GetSegmentByteRanges() => GetSegmentByteRangeIndex().Ranges;

    public SegmentByteRangeIndex GetSegmentByteRangeIndex()
    {
        var ranges = Segments
            .Select(x => x.ByteRange)
            .ToArray();

        if (ranges.Length == 0) return default;

        if (ranges.All(x => x is not null))
        {
            var explicitRanges = ValidateSegmentByteRanges(ranges.Select(x => x!).ToArray());
            return new SegmentByteRangeIndex(explicitRanges, explicitRanges is not null);
        }

        if (_rejectInferredSegmentByteRanges) return default;

        var firstRange = ranges[0];
        var lastRange = ranges[^1];
        if (firstRange is null || lastRange is null ||
            firstRange.StartInclusive != 0 || firstRange.Count <= 0 || lastRange.Count <= 0)
            return default;

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

            if (inferredRanges[^1].StartInclusive != lastRange.StartInclusive) return default;

            for (var i = 0; i < ranges.Length; i++)
            {
                if (ranges[i] is { } knownRange &&
                    (knownRange.StartInclusive != inferredRanges[i].StartInclusive ||
                     knownRange.EndExclusive != inferredRanges[i].EndExclusive))
                    return default;
            }

            var validated = ValidateSegmentByteRanges(inferredRanges);
            var trusted = validated is not null &&
                          (ranges.Length < 3 || _inferredSegmentByteRangesValidated);
            return new SegmentByteRangeIndex(validated, trusted);
        }
        catch (OverflowException)
        {
            return default;
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
