using NzbWebDAV.Models;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.NestedRarExpansion;

/// <summary>
/// Maps a byte range within a composed outer-archive member onto the
/// underlying NZB volume segments (SevenZip-style interpolation).
/// </summary>
public static class NestedRarRangeMapper
{
    public static RarProcessor.StoredFileSegment[] Map(
        LongRange rangeWithinComposed,
        RarProcessor.StoredFileSegment[] sortedOuterSegments,
        string pathWithinArchive,
        string archiveName,
        string archiveSetId,
        RarProcessor.PartNumber partNumber,
        AesParams? aesParams,
        long fileUncompressedSize,
        DateTimeOffset releaseDate,
        bool isUncompressedSizeUnknown = false)
    {
        if (sortedOuterSegments.Length == 0)
            return [];

        var partLayouts = BuildLayouts(sortedOuterSegments);
        var composedSize = partLayouts[^1].ComposedRange.EndExclusive;

        var (startIndex, startComposedRange) = InterpolationSearch.Find(
            rangeWithinComposed.StartInclusive,
            new LongRange(0, partLayouts.Length),
            new LongRange(0, composedSize),
            guess => partLayouts[guess].ComposedRange);

        var (endIndex, endComposedRange) = InterpolationSearch.Find(
            rangeWithinComposed.EndExclusive - 1,
            new LongRange(0, partLayouts.Length),
            new LongRange(0, composedSize),
            guess => partLayouts[guess].ComposedRange);

        var results = new List<RarProcessor.StoredFileSegment>(endIndex - startIndex + 1);
        for (var index = startIndex; index <= endIndex; index++)
        {
            var layout = partLayouts[index];
            var partStart = index == startIndex
                ? rangeWithinComposed.StartInclusive - startComposedRange.StartInclusive
                : 0;
            var partEnd = index == endIndex
                ? rangeWithinComposed.EndExclusive - endComposedRange.StartInclusive
                : layout.Outer.ByteRangeWithinPart.Count;
            var partCount = partEnd - partStart;
            if (partCount <= 0)
                continue;

            // Single outer segment: this group is one complete inner volume
            // (shape B) — use the nested archive's part identity. Multiple outer
            // segments: the nested archive is itself split across outer volumes
            // (shape A) — inherit each outer volume's part number.
            var mappedPartNumber = sortedOuterSegments.Length == 1
                ? partNumber
                : layout.Outer.PartNumber;

            results.Add(new RarProcessor.StoredFileSegment
            {
                NzbFile = layout.Outer.NzbFile,
                  ArchiveSetId = archiveSetId,
                PartSize = layout.Outer.PartSize,
                ArchiveName = archiveName,
                PartNumber = mappedPartNumber,
                ReleaseDate = releaseDate,
                PathWithinArchive = pathWithinArchive,
                ByteRangeWithinPart = LongRange.FromStartAndSize(
                    layout.Outer.ByteRangeWithinPart.StartInclusive + partStart,
                    partCount),
                AesParams = aesParams,
                FileUncompressedSize = fileUncompressedSize,
                IsUncompressedSizeUnknown = isUncompressedSizeUnknown,
            });
        }

        return results.ToArray();
    }

    private static PartLayout[] BuildLayouts(RarProcessor.StoredFileSegment[] sortedOuterSegments)
    {
        var layouts = new PartLayout[sortedOuterSegments.Length];
        long offset = 0;
        for (var i = 0; i < sortedOuterSegments.Length; i++)
        {
            var outer = sortedOuterSegments[i];
            var count = outer.ByteRangeWithinPart.Count;
            layouts[i] = new PartLayout(outer, LongRange.FromStartAndSize(offset, count));
            offset += count;
        }

        return layouts;
    }

    private readonly record struct PartLayout(
        RarProcessor.StoredFileSegment Outer,
        LongRange ComposedRange);
}
