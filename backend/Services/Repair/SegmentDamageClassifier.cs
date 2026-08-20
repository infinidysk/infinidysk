using System.Diagnostics.CodeAnalysis;

namespace NzbWebDAV.Services.Repair;

public enum SegmentDamageVerdict
{
    Clean,
    Degraded,
    Failed,
}

[SuppressMessage("Design", "CA1028:Enum Storage should be Int32", Justification = "Persisted in a compact metadata blob.")]
public enum MediaContainerClass : byte
{
    Unknown = 0,
    ResyncTolerant = 1,
    Mp4FastStart = 2,
    Mp4MoovAtEnd = 3,
}

public sealed record SegmentDamageCaps(
    int MaxConsecutiveMissing,
    int MaxTotalMissing,
    double MaxMissingBytePercent);

public static class SegmentDamageClassifier
{
    public static SegmentDamageVerdict Classify(
        IReadOnlyList<int> missingIndices,
        int totalSegments,
        IReadOnlyList<long> exactSegmentSizes,
        IReadOnlyList<long> segmentStartOffsets,
        MediaContainerClass containerClass,
        SegmentDamageCaps caps,
        long criticalHeadEndExclusive,
        out string reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSegments);
        if (exactSegmentSizes.Count != totalSegments)
            throw new ArgumentException("Segment size count must match totalSegments.", nameof(exactSegmentSizes));
        if (segmentStartOffsets.Count != totalSegments)
            throw new ArgumentException("Segment start offset count must match totalSegments.", nameof(segmentStartOffsets));

        // The run/head checks below assume sorted, unique, in-range indices; normalize
        // defensively so a mis-ordered or duplicated list cannot misclassify.
        var missing = missingIndices.Distinct().OrderBy(index => index).ToArray();
        foreach (var index in missing.Where(index => index < 0 || index >= totalSegments))
            throw new ArgumentOutOfRangeException(nameof(missingIndices), index, "Missing segment index out of range.");

        var missingBytes = missing.Sum(index => exactSegmentSizes[index]);
        var totalBytes = exactSegmentSizes.Sum();
        var bytePercent = totalBytes == 0 ? 100d : missingBytes * 100d / totalBytes;
        var longestRun = GetLongestRun(missing);
        reason = $"{missing.Length} missing/corrupt segment(s) (largest run {longestRun}, {bytePercent:0.##}% of file)";

        if (missing.Length == 0) return SegmentDamageVerdict.Clean;
        if (containerClass is MediaContainerClass.Unknown or MediaContainerClass.Mp4MoovAtEnd)
            return SegmentDamageVerdict.Failed;
        if (missing[0] == 0) return SegmentDamageVerdict.Failed;
        if (containerClass == MediaContainerClass.Mp4FastStart
            && criticalHeadEndExclusive > 0
            && missing.Any(index => segmentStartOffsets[index] < criticalHeadEndExclusive))
            return SegmentDamageVerdict.Failed;
        if (longestRun > caps.MaxConsecutiveMissing) return SegmentDamageVerdict.Failed;
        if (missing.Length > caps.MaxTotalMissing) return SegmentDamageVerdict.Failed;
        if (bytePercent > caps.MaxMissingBytePercent) return SegmentDamageVerdict.Failed;

        return SegmentDamageVerdict.Degraded;
    }

    private static int GetLongestRun(IReadOnlyList<int> missingIndices)
    {
        var longestRun = 0;
        var currentRun = 0;
        var previous = int.MinValue;
        foreach (var index in missingIndices)
        {
            currentRun = index == previous + 1 ? currentRun + 1 : 1;
            longestRun = Math.Max(longestRun, currentRun);
            previous = index;
        }

        return longestRun;
    }
}

public static class MediaContainerClassMapping
{
    /// <summary>
    /// The container class fixed by the file extension, or null for the MP4 family
    /// (.mp4/.m4v/.mov), whose layout (faststart / moov-at-end / fragmented) must be
    /// probed from the file head. Only meaningful for
    /// <see cref="Utils.FilenameUtil.IsDegradedToleranceEligible"/> files.
    /// </summary>
    public static MediaContainerClass? ByExtension(string filename) =>
        Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".mkv" or ".mk3d" or ".webm" or ".ts" or ".m2ts" => MediaContainerClass.ResyncTolerant,
            _ => null,
        };

    /// <summary>Human-readable container phrase for health-result messages.</summary>
    public static string Describe(MediaContainerClass containerClass) => containerClass switch
    {
        MediaContainerClass.ResyncTolerant => "a resync-tolerant container",
        MediaContainerClass.Mp4FastStart => "a fast-start MP4 container",
        MediaContainerClass.Mp4MoovAtEnd => "an MP4 container with the moov atom at the end",
        _ => "an unrecognized container",
    };
}
