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

    public static PlaybackDamageBudget? TryCreate(string fileName, DavNzbFile nzbFile, ConfigManager config)
    {
        if (!config.IsDegradedToleranceEnabled())
            return null;
        var containerClass = ResolveContainerClass(fileName, nzbFile);
        if (containerClass is not (MediaContainerClass.ResyncTolerant or MediaContainerClass.Mp4FastStart))
            return null;
        if (nzbFile.SegmentByteRanges is not { } ranges || ranges.Length != nzbFile.SegmentIds.Length)
            return null;

        var persisted = nzbFile.MissingSegmentIndices?
            .Where(index => (uint)index < (uint)nzbFile.SegmentIds.Length)
            .ToArray() ?? [];
        return new PlaybackDamageBudget(
            nzbFile.SegmentIds,
            ranges,
            containerClass.Value,
            nzbFile.CriticalHeadEndExclusive ?? 0,
            new SegmentDamageCaps(
                config.GetDegradedMaxConsecutiveMissing(),
                config.GetDegradedMaxTotalMissing(),
                config.GetDegradedMaxMissingBytePercent()),
            persisted);
    }

    /// <summary>True when playback pads over damage in this file instead of escalating each hole.</summary>
    public static bool Applies(string fileName, DavNzbFile nzbFile, ConfigManager config) =>
        config.IsDegradedToleranceEnabled()
        && ResolveContainerClass(fileName, nzbFile) is MediaContainerClass.ResyncTolerant or MediaContainerClass.Mp4FastStart;

    public bool IsExceeded(IEnumerable<int> playbackMissingIndices, out string reason)
    {
        var missing = PersistedMissingIndices.Concat(playbackMissingIndices).ToArray();
        var verdict = SegmentDamageClassifier.Classify(
            missing,
            SegmentIds.Length,
            SegmentRanges.Select(range => range.Count).ToArray(),
            SegmentRanges.Select(range => range.StartInclusive).ToArray(),
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
