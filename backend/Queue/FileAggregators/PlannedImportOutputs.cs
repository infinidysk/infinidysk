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
    internal static long GetLargestVideoFileSize(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        return PlanNamesAndSizes(processorResults, mountName)
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .Select(x => x.FileSize)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static IEnumerable<(string Name, long FileSize)> PlanNamesAndSizes(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        foreach (var direct in FileAggregator.PlanDirectFiles(processorResults, mountName))
            yield return (direct.Name, direct.FileSize);

        var rarGroups = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(x => x.StoredFileSegments)
            .GroupBy(x => x.PathWithinArchive)
            .ToList();
        foreach (var group in rarGroups)
        {
            var parts = group.ToList();
            var sniffedVideoExtension = parts
                .Select(x => x.SniffedVideoExtension)
                .FirstOrDefault(x => x is not null);
            yield return (
                ImportableVideoNamer.Normalize(
                    PathSanitizer.SanitizeComponent(Path.GetFileName(group.Key)),
                    sniffedVideoExtension,
                    mountName,
                    allowBaseRename: rarGroups.Count == 1),
                RarAggregator.ResolvePublishedFileSize(parts));
        }

        foreach (var lazy in processorResults.OfType<LazyRarProcessor.Result>())
        {
            yield return (
                ImportableVideoNamer.Normalize(
                    PathSanitizer.SanitizeComponent(Path.GetFileName(lazy.PathInArchive)),
                    lazy.SniffedVideoExtension,
                    mountName,
                    allowBaseRename: true),
                lazy.TotalFileSize);
        }

        foreach (var result in processorResults.OfType<SevenZipProcessor.Result>())
        {
            var sevenZipFiles = result.SevenZipFiles;
            foreach (var sevenZipFile in sevenZipFiles)
            {
                var meta = sevenZipFile.DavMultipartFileMeta;
                yield return (
                    ImportableVideoNamer.Normalize(
                        PathSanitizer.SanitizeComponent(Path.GetFileName(sevenZipFile.PathWithinArchive)),
                        sevenZipFile.SniffedVideoExtension,
                        mountName,
                        allowBaseRename: sevenZipFiles.Count == 1),
                    meta.AesParams?.DecodedSize
                        ?? meta.FileParts.Sum(x => x.FilePartByteRange.Count));
            }
        }

        foreach (var multipart in processorResults.OfType<MultipartMkvProcessor.Result>())
        {
            yield return (
                PathSanitizer.SanitizeComponent(multipart.Filename),
                multipart.Parts.Sum(x => x.FilePartByteRange.Count));
        }
    }
}
