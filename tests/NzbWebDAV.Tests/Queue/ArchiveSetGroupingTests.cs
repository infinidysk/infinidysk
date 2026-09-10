using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;

namespace NzbWebDAV.Tests.Queue;

public class ArchiveSetGroupingTests
{
    [Fact]
    public void Resolve_IndependentRarBasesGetIndependentIds()
    {
        var descriptors = ArchiveSetGrouping.Resolve([
            Info("Episode01.part01.rar"),
            Info("Episode02.part01.rar"),
        ], new ArchiveSetIdAllocator());

        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor => Assert.Single(descriptor.FileInfos));
        Assert.NotEqual(descriptors[0].ArchiveSetId, descriptors[1].ArchiveSetId);
    }

    [Fact]
    public void Resolve_MixedCaseRarBaseSharesOneSet()
    {
        var descriptors = ArchiveSetGrouping.Resolve([
            Info("Episode.part01.rar"),
            Info("episode.part02.rar"),
        ], new ArchiveSetIdAllocator());

        var descriptor = Assert.Single(descriptors);
        Assert.Equal(2, descriptor.FileInfos.Count);
    }

    [Fact]
    public void Resolve_IndependentSevenZipBasesGetIndependentIds()
    {
        var descriptors = ArchiveSetGrouping.Resolve([
            Info("A.7z.001"),
            Info("A.7z.002"),
            Info("B.7z.001"),
            Info("B.7z.002"),
        ], new ArchiveSetIdAllocator());

        Assert.Equal(2, descriptors.Count);
        Assert.Equal(["A.7z.001", "A.7z.002"], descriptors[0].FileInfos.Select(x => x.FileName));
        Assert.Equal(["B.7z.001", "B.7z.002"], descriptors[1].FileInfos.Select(x => x.FileName));
    }

    [Fact]
    public void Resolve_RepeatedStandaloneRarStartsNewSet()
    {
        var descriptors = ArchiveSetGrouping.Resolve([
            Info("Movie.rar"),
            Info("Movie.rar"),
        ], new ArchiveSetIdAllocator());

        Assert.Equal(2, descriptors.Count);
    }

    private static GetFileInfosStep.FileInfo Info(string filename) =>
        new()
        {
            NzbFile = new NzbFile
            {
                Subject = filename,
                Segments =
                {
                    new NzbSegment { MessageId = $"{filename}@example.com", Bytes = 1024 }
                },
            },
            FileName = filename,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            IsRar = filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase),
        };
}
