using NzbWebDAV.Models.Nzb;
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
    internal sealed record PlannedOutput(string Name, long FileSize, string RelativePath)
    {
        internal NzbFile? DirectFile { get; init; }

        /// <summary>Opening bytes of an archived or split output, read during import; null when unavailable.</summary>
        internal byte[]? ArchivedHead { get; init; }
    }

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
            yield return new PlannedOutput(direct.Name, direct.FileSize, direct.RelativePath)
            { DirectFile = direct.NzbFile };

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
            var name = ImportableVideoNamer.Normalize(
                PathSanitizer.SanitizeComponent(Path.GetFileName(group.Key.PathWithinArchive)),
                sniffedVideoExtension, mountName, allowBaseRename: rarGroups.Count == 1);
            yield return new PlannedOutput(name, RarAggregator.ResolvePublishedFileSize(parts), group.Key.PathWithinArchive)
            { ArchivedHead = parts.Select(x => x.MediaHead).FirstOrDefault(x => x is not null) };
        }

        foreach (var lazy in processorResults.OfType<LazyRarProcessor.Result>())
        {
            var name = ImportableVideoNamer.Normalize(
                PathSanitizer.SanitizeComponent(Path.GetFileName(lazy.PathInArchive)),
                lazy.SniffedVideoExtension, mountName, allowBaseRename: true);
            yield return new PlannedOutput(name, lazy.TotalFileSize, lazy.PathInArchive)
            { ArchivedHead = lazy.MediaHead };
        }

        foreach (var result in processorResults.OfType<SevenZipProcessor.Result>())
        {
            foreach (var sevenZipGroup in result.SevenZipFiles.GroupBy(x => x.ArchiveSetId, StringComparer.Ordinal))
            {
                var sevenZipFiles = sevenZipGroup.ToList();
                foreach (var sevenZipFile in sevenZipFiles)
                {
                    var meta = sevenZipFile.DavMultipartFileMeta;
                    var name = ImportableVideoNamer.Normalize(
                        PathSanitizer.SanitizeComponent(Path.GetFileName(sevenZipFile.PathWithinArchive)),
                        sevenZipFile.SniffedVideoExtension, mountName, allowBaseRename: sevenZipFiles.Count == 1);
                    yield return new PlannedOutput(
                        name, meta.AesParams?.DecodedSize ?? meta.FileParts.Sum(x => x.FilePartByteRange.Count),
                        sevenZipFile.PathWithinArchive)
                    { ArchivedHead = sevenZipFile.MediaHead };
                }
            }
        }

        foreach (var multipart in processorResults.OfType<MultipartMkvProcessor.Result>())
        {
            yield return new PlannedOutput(
                PathSanitizer.SanitizeComponent(multipart.Filename),
                multipart.Parts.Sum(x => x.FilePartByteRange.Count), multipart.Filename)
            { ArchivedHead = multipart.MediaHead };
        }
    }
}
