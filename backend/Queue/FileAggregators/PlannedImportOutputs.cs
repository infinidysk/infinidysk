using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileAggregators;

/// <summary>
/// Pure projection of the files a completed import will mount, computed from processor
/// results before the database is touched. Mirrors each aggregator's output naming so
/// pre-commit filtering (import-readiness target selection) applies the same sample
/// size heuristic the blocklist post-processor applies to persisted items. Duplicate
/// renames preserve extensions, so planned and persisted video sets always match.
/// </summary>
internal static class PlannedImportOutputs
{
    internal static class Kinds
    {
        public const string Direct = "direct";
        public const string Rar = "rar";
        public const string LazyRar = "lazyRar";
        public const string SevenZip = "7z";
        public const string SplitVideo = "split";
    }

    /// <summary>
    /// One planned mount output. <paramref name="RawPath"/> is the name as posted or as
    /// stored inside the archive; <paramref name="Name"/> is the planned mount name.
    /// <paramref name="Origin"/> is the processor-level source: the grouped
    /// <see cref="RarProcessor.StoredFileSegment"/> list, a <see cref="LazyRarProcessor.Result"/>,
    /// a <see cref="SevenZipProcessor.SevenZipFile"/>, a <see cref="FileAggregator.PlannedDirectFile"/>
    /// or a <see cref="MultipartMkvProcessor.Result"/>.
    /// </summary>
    internal sealed record PlannedOutput(
        string Kind,
        string? ArchiveSetId,
        string RawPath,
        string Name,
        long FileSize,
        string? SniffedVideoExtension,
        object Origin);

    internal static long GetLargestVideoFileSize(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        return PlanOutputs(processorResults, mountName)
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .Select(x => x.FileSize)
            .DefaultIfEmpty(0)
            .Max();
    }

    internal static IEnumerable<PlannedOutput> PlanOutputs(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        foreach (var direct in FileAggregator.PlanDirectFiles(processorResults, mountName))
        {
            yield return new PlannedOutput(
                Kinds.Direct, null, direct.RelativePath, direct.Name, direct.FileSize,
                direct.SniffedVideoExtension, direct);
        }

        var rarGroups = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(x => x.StoredFileSegments)
                        .GroupBy(x => (x.ArchiveSetId, x.PathWithinArchive))
            .ToList();
        foreach (var group in rarGroups)
        {
            var parts = group.ToList();
            var sniffedVideoExtension = parts
                .Select(x => x.SniffedVideoExtension)
                .FirstOrDefault(x => x is not null);
            yield return new PlannedOutput(
                Kinds.Rar,
                group.Key.ArchiveSetId,
                group.Key.PathWithinArchive,
                ImportableVideoNamer.Normalize(
                                        PathSanitizer.SanitizeComponent(Path.GetFileName(group.Key.PathWithinArchive)),
                    sniffedVideoExtension,
                    mountName,
                      allowBaseRename: rarGroups.Count == 1),
                RarAggregator.ResolvePublishedFileSize(parts),
                sniffedVideoExtension,
                parts);
        }

        foreach (var lazy in processorResults.OfType<LazyRarProcessor.Result>())
        {
            yield return new PlannedOutput(
                Kinds.LazyRar,
                lazy.ArchiveSetId,
                lazy.PathInArchive,
                ImportableVideoNamer.Normalize(
                    PathSanitizer.SanitizeComponent(Path.GetFileName(lazy.PathInArchive)),
                    lazy.SniffedVideoExtension,
                    mountName,
                    allowBaseRename: true),
                lazy.TotalFileSize,
                lazy.SniffedVideoExtension,
                lazy);
        }

        foreach (var result in processorResults.OfType<SevenZipProcessor.Result>())
        {
            foreach (var sevenZipGroup in result.SevenZipFiles.GroupBy(x => x.ArchiveSetId, StringComparer.Ordinal))
            {
                var sevenZipFiles = sevenZipGroup.ToList();
                foreach (var sevenZipFile in sevenZipFiles)
                {
                    var meta = sevenZipFile.DavMultipartFileMeta;
                    yield return new PlannedOutput(
                        Kinds.SevenZip,
                        sevenZipFile.ArchiveSetId,
                        sevenZipFile.PathWithinArchive,
                        ImportableVideoNamer.Normalize(
                            PathSanitizer.SanitizeComponent(Path.GetFileName(sevenZipFile.PathWithinArchive)),
                            sevenZipFile.SniffedVideoExtension,
                            mountName,
                            allowBaseRename: sevenZipFiles.Count == 1),
                        meta.AesParams?.DecodedSize
                            ?? meta.FileParts.Sum(x => x.FilePartByteRange.Count),
                        sevenZipFile.SniffedVideoExtension,
                        sevenZipFile);
                }
            }
        }

        foreach (var multipart in processorResults.OfType<MultipartMkvProcessor.Result>())
        {
            yield return new PlannedOutput(
                Kinds.SplitVideo,
                null,
                multipart.Filename,
                PathSanitizer.SanitizeComponent(multipart.Filename),
                multipart.Parts.Sum(x => x.FilePartByteRange.Count),
                null,
                multipart);
        }
    }
}
