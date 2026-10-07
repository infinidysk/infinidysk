using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

/// <summary>
/// Per-file limits on how much confirmed damage playback may pad over before it fails
/// the read and hands the file to repair. Only built for files whose container is known
/// to resync over a gap; every other file keeps the fixed consecutive-fill limit.
/// </summary>
internal sealed class PlaybackDamageBudget
{
    private PlaybackDamageBudget(
        string[] segmentIds,
        LongRange[] segmentRanges,
        MediaContainerClass containerClass,
        long criticalHeadEndExclusive,
        SegmentDamageCaps caps,
        int[] persistedMissingIndices)
    {
        SegmentIds = segmentIds;
        SegmentRanges = segmentRanges;
        ContainerClass = containerClass;
        CriticalHeadEndExclusive = criticalHeadEndExclusive;
        Caps = caps;
        PersistedMissingIndices = persistedMissingIndices;
    }

    public string[] SegmentIds { get; }
    public LongRange[] SegmentRanges { get; }
    public MediaContainerClass ContainerClass { get; }
    public long CriticalHeadEndExclusive { get; }
    public SegmentDamageCaps Caps { get; }
    public int[] PersistedMissingIndices { get; }

    public int ConsecutiveFillLimit => Caps.MaxConsecutiveMissing + 1;

    // Built on the first hole, not per stream open; callers hold the tracker lock.
    private long[]? _segmentSizes;
    private long[]? _segmentStarts;

    public static PlaybackDamageBudget? TryCreate(string fileName, DavNzbFile nzbFile, ConfigManager config)
    {
        if (!IsEligible(fileName, nzbFile, config, out var containerClass, out var ranges))
            return null;

        // Match health classification: tracked corruption counts as damage alongside missing articles.
        var recordedCorrupt = config.IsCorruptionTrackingEnabled() ? nzbFile.CorruptSegmentIndices : null;
        var persisted = (nzbFile.MissingSegmentIndices ?? [])
            .Concat(recordedCorrupt ?? [])
            .Where(index => (uint)index < (uint)nzbFile.SegmentIds.Length)
            .ToArray();
        return new PlaybackDamageBudget(
            nzbFile.SegmentIds,
            ranges,
            containerClass,
            nzbFile.CriticalHeadEndExclusive ?? 0,
            new SegmentDamageCaps(
                config.GetDegradedMaxConsecutiveMissing(),
                config.GetDegradedMaxTotalMissing(),
                config.GetDegradedMaxMissingBytePercent()),
            persisted);
    }

    /// <summary>True when playback pads over damage in this file instead of escalating each hole.</summary>
    public static bool Applies(string fileName, DavNzbFile nzbFile, ConfigManager config) =>
        IsEligible(fileName, nzbFile, config, out _, out _);

    private static bool IsEligible(
        string fileName,
        DavNzbFile nzbFile,
        ConfigManager config,
        out MediaContainerClass containerClass,
        out LongRange[] ranges)
    {
        containerClass = default;
        ranges = [];
        if (!config.IsDegradedToleranceEnabled())
            return false;
        if (ResolveContainerClass(fileName, nzbFile) is not ({ } resolved
                and (MediaContainerClass.ResyncTolerant or MediaContainerClass.Mp4FastStart)))
            return false;
        if (nzbFile.SegmentByteRanges is not { } segmentRanges || segmentRanges.Length != nzbFile.SegmentIds.Length)
            return false;

        containerClass = resolved;
        ranges = segmentRanges;
        return true;
    }

    public bool IsExceeded(IEnumerable<int> playbackMissingIndices, out string reason)
    {
        var missing = PersistedMissingIndices.Concat(playbackMissingIndices).ToArray();
        _segmentSizes ??= SegmentRanges.Select(range => range.Count).ToArray();
        _segmentStarts ??= SegmentRanges.Select(range => range.StartInclusive).ToArray();
        var verdict = SegmentDamageClassifier.Classify(
            missing,
            SegmentIds.Length,
            _segmentSizes,
            _segmentStarts,
            ContainerClass,
            Caps,
            CriticalHeadEndExclusive,
            out reason);
        return verdict == SegmentDamageVerdict.Failed;
    }

    private static MediaContainerClass? ResolveContainerClass(string fileName, DavNzbFile nzbFile)
    {
        if (!FilenameUtil.IsDegradedToleranceEligible(fileName))
            return null;
        if (MediaContainerClassMapping.ByExtension(fileName) is { } byExtension)
            return byExtension;
        return nzbFile.ContainerClass is byte persisted && Enum.IsDefined((MediaContainerClass)persisted)
            ? (MediaContainerClass)persisted
            : null;
    }
}
