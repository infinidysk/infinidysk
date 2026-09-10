using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue;

internal sealed record ArchiveSetDescriptor(
    string ArchiveSetId,
    List<GetFileInfosStep.FileInfo> FileInfos,
    bool IsSevenZip);

internal static class ArchiveSetGrouping
{
    internal static List<ArchiveSetDescriptor> Resolve(
        IReadOnlyList<GetFileInfosStep.FileInfo> fileInfos,
        ArchiveSetIdAllocator allocator)
    {
        var descriptors = new List<ArchiveSetDescriptor>();
        var rarGroups = new Dictionary<(string BaseName, FilenameUtil.RarVolumeScheme Scheme), List<ArchiveSetDescriptor>>();
        var sevenZipGroups = new Dictionary<string, List<ArchiveSetDescriptor>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileInfo in fileInfos)
        {
            if (fileInfo.IsRar || FilenameUtil.IsRarFile(fileInfo.FileName))
            {
                var volume = FilenameUtil.GetRarVolumeName(fileInfo.FileName);
                if (volume is null)
                {
                    descriptors.Add(new ArchiveSetDescriptor(allocator.Allocate(), [fileInfo], false));
                    continue;
                }

                var volumeValue = volume.Value;
                var volumeOrdinal = volumeValue.Ordinal;
                var key = (volumeValue.BaseName.ToLowerInvariant(), volumeValue.Scheme);
                if (!rarGroups.TryGetValue(key, out var candidates))
                {
                    candidates = [];
                    rarGroups.Add(key, candidates);
                }

                var descriptor = volumeValue.Scheme == FilenameUtil.RarVolumeScheme.Part &&
                                 volumeOrdinal == 0
                    ? null
                    : candidates.FirstOrDefault(candidate => candidate.FileInfos.All(existing =>
                        FilenameUtil.GetRarVolumeName(existing.FileName)?.Ordinal != volumeOrdinal));
                if (descriptor is null)
                {
                    descriptor = new ArchiveSetDescriptor(allocator.Allocate(), [], false);
                    candidates.Add(descriptor);
                    descriptors.Add(descriptor);
                }

                descriptor.FileInfos.Add(fileInfo);
                continue;
            }

            if (!FilenameUtil.Is7zFile(fileInfo.FileName))
                continue;

            var sevenZip = FilenameUtil.GetSevenZipVolumeName(fileInfo.FileName);
            if (sevenZip is null)
            {
                descriptors.Add(new ArchiveSetDescriptor(allocator.Allocate(), [fileInfo], true));
                continue;
            }

            var sevenZipValue = sevenZip.Value;
            if (!sevenZipValue.IsMultipart)
            {
                descriptors.Add(new ArchiveSetDescriptor(allocator.Allocate(), [fileInfo], true));
                continue;
            }

            var sevenZipKey = sevenZipValue.BaseName;
            if (!sevenZipGroups.TryGetValue(sevenZipKey, out var sevenZipCandidates))
            {
                sevenZipCandidates = [];
                sevenZipGroups.Add(sevenZipKey, sevenZipCandidates);
            }

            var sevenZipDescriptor = sevenZipCandidates.FirstOrDefault(candidate => candidate.FileInfos.All(existing =>
                FilenameUtil.GetSevenZipVolumeName(existing.FileName)?.Ordinal != sevenZipValue.Ordinal));
            if (sevenZipDescriptor is null)
            {
                sevenZipDescriptor = new ArchiveSetDescriptor(allocator.Allocate(), [], true);
                sevenZipCandidates.Add(sevenZipDescriptor);
                descriptors.Add(sevenZipDescriptor);
            }

            sevenZipDescriptor.FileInfos.Add(fileInfo);
        }

        return descriptors;
    }
}
