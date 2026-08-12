using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;

namespace NzbWebDAV.Tests.Queue;

public class SplitVideoGroupingTests
{
    [Fact]
    public void GroupFilesForProcessing_KeepsIndependentSplitSetsSeparate()
    {
        var files = new[]
        {
            Info("EP01.mkv.001"),
            Info("EP01.mkv.002"),
            Info("EP02.mkv.001"),
            Info("EP02.mkv.002"),
        };

        var splitGroups = SplitGroups(files);

        Assert.Equal(2, splitGroups.Count);
        Assert.Contains(splitGroups, g => g.Key == "split-video:ep01.mkv" && g.Count() == 2);
        Assert.Contains(splitGroups, g => g.Key == "split-video:ep02.mkv" && g.Count() == 2);
    }

    [Fact]
    public void GroupFilesForProcessing_SharesKeyAcrossMixedCaseBaseNames()
    {
        var files = new[]
        {
            Info("EP01.mkv.001"),
            Info("ep01.MKV.002"),
        };

        var splitGroups = SplitGroups(files);

        var group = Assert.Single(splitGroups);
        Assert.Equal("split-video:ep01.mkv", group.Key);
        Assert.Equal(2, group.Count());
    }

    [Fact]
    public void GroupFilesForProcessing_MergesDisjointContiguousMisnamedSet()
    {
        var files = new[]
        {
            Info("A.mkv.001"),
            Info("B.mkv.002"),
            Info("B.mkv.003"),
            Info("release.nfo"),
        };

        var groups = QueueItemProcessor.GroupFilesForProcessing(files);
        var splitGroups = groups
            .Where(g => g.Key.StartsWith("split-video:", StringComparison.Ordinal))
            .ToList();

        var merged = Assert.Single(splitGroups);
        Assert.Equal(
            ["A.mkv.001", "B.mkv.002", "B.mkv.003"],
            merged.Select(f => f.FileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Contains(groups, g => g.Key == "other" && g.Any(f => f.FileName == "release.nfo"));
    }

    [Fact]
    public void GroupFilesForProcessing_DoesNotMergeSeasonPackWithCollidingPartNumbers()
    {
        var files = new[]
        {
            Info("EP01.mkv.001"),
            Info("EP01.mkv.002"),
            Info("EP02.mkv.001"),
            Info("EP02.mkv.002"),
        };

        Assert.Equal(2, SplitGroups(files).Count);
    }

    [Fact]
    public void GroupFilesForProcessing_DoesNotMergeDisjointGappedSets()
    {
        var files = new[]
        {
            Info("A.mkv.001"),
            Info("B.mkv.005"),
        };

        Assert.Equal(2, SplitGroups(files).Count);
    }

    private static List<IGrouping<string, GetFileInfosStep.FileInfo>> SplitGroups(
        IReadOnlyList<GetFileInfosStep.FileInfo> files) =>
        QueueItemProcessor.GroupFilesForProcessing(files)
            .Where(g => g.Key.StartsWith("split-video:", StringComparison.Ordinal))
            .ToList();

    private static GetFileInfosStep.FileInfo Info(string fileName) => new()
    {
        NzbFile = new NzbFile { Subject = $"\"{fileName}\"" },
        FileName = fileName,
        ReleaseDate = DateTimeOffset.UnixEpoch,
    };
}
