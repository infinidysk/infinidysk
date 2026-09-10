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
        var sevenZipGroups = new Dictionary<(string BaseName, int? Ordinal), ArchiveSetDescriptor>();

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

                var key = (volume.Value.BaseName.ToLowerInvariant(), volume.Value.Scheme);
                if (!rarGroups.TryGetValue(key, out var candidates))
                {
                    candidates = [];
                    rarGroups.Add(key, candidates);
                }

                var descriptor = volume.Value.Scheme == FilenameUtil.RarVolumeScheme.Classic &&
                                 volume.Value.Ordinal == 0
                    ? null
                    : candidates.LastOrDefault();
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

              var sevenZipKey = (sevenZip.Value.BaseName.ToLowerInvariant(), sevenZip.Value.IsMultipart ? 1 : descriptors.Count);
            if (!sevenZipGroups.TryGetValue(sevenZipKey, out var sevenZipDescriptor))
            {
                sevenZipDescriptor = new ArchiveSetDescriptor(allocator.Allocate(), [], true);
                sevenZipGroups.Add(sevenZipKey, sevenZipDescriptor);
                descriptors.Add(sevenZipDescriptor);
            }

            sevenZipDescriptor.FileInfos.Add(fileInfo);
        }

        return descriptors;
    }
}
