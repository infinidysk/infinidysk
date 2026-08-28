using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Queue;

public sealed class QueueArticleExistenceSelectionTests
{
    [Fact]
    public void SampledMode_IncludesFirstAndLastSegmentOfEveryFile()
    {
        var files = Enumerable.Range(0, 3)
            .Select(file => Segments($"file-{file}", 20_000))
            .Cast<IReadOnlyList<string>>()
            .ToList();

        var sampled = QueueItemProcessor.SelectArticlesForExistenceCheck(files, "sampled");

        Assert.True(sampled.Count < files.Sum(x => x.Count));
        for (var file = 0; file < files.Count; file++)
        {
            Assert.Contains($"file-{file}-0", sampled);
            Assert.Contains($"file-{file}-19999", sampled);
        }
    }

    [Fact]
    public void FullMode_PreservesEverySegment()
    {
        IReadOnlyList<string>[] files =
        [
            Segments("first", 10),
            Segments("second", 15),
        ];

        var selected = QueueItemProcessor.SelectArticlesForExistenceCheck(files, "full");

        Assert.Equal(files.SelectMany(x => x), selected);
    }

    [Fact]
    public async Task SampledMode_MissingLastSegmentFailsExistenceCheck()
    {
        var file = Segments("video", 20_000);
        var selected = QueueItemProcessor.SelectArticlesForExistenceCheck([file], "sampled");
        var available = selected
            .Where(x => x != file[^1])
            .ToDictionary(x => x, _ => Array.Empty<byte>());
        var client = new FakeNntpClient(available);

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            ArticleExistenceChecker.CheckAsync(
                client, selected, concurrency: 8, progress: null, CancellationToken.None));

        Assert.Equal(file[^1], exception.SegmentId);
    }

    [Fact]
    public void MidpointPreflight_SelectsMidpointOfLargestFile()
    {
        IReadOnlyList<IReadOnlyList<string>> files =
        [
            Segments("small", 5),
            Segments("large", 9),
            Segments("medium", 7),
        ];
        Assert.Equal("large-4", QueueItemProcessor.SelectMidpointPreflightSegment(files));
    }

    [Fact]
    public void MidpointPreflight_TieKeepsFirstFile()
    {
        IReadOnlyList<IReadOnlyList<string>> files = [Segments("a", 6), Segments("b", 6)];
        Assert.Equal("a-3", QueueItemProcessor.SelectMidpointPreflightSegment(files));
    }

    [Fact]
    public void MidpointPreflight_SkipsSingleSegmentFilesAndEmptyInput()
    {
        Assert.Null(QueueItemProcessor.SelectMidpointPreflightSegment([Segments("one", 1)]));
        Assert.Null(QueueItemProcessor.SelectMidpointPreflightSegment([]));
    }

    [Theory]
    [InlineData(8, "even-4")]
    [InlineData(9, "odd-4")]
    public void MidpointPreflight_EvenAndOddCountsUseIntegerDivision(int count, string expected)
    {
        var prefix = expected.Split('-')[0];
        Assert.Equal(
            expected,
            QueueItemProcessor.SelectMidpointPreflightSegment([Segments(prefix, count)]));
    }

    [Fact]
    public async Task MidpointPreflight_MissThrowsWithProbedIdBeforeSweep()
    {
        var files = new IReadOnlyList<string>[] { Segments("video", 8), Segments("extra", 4) };
        var articles = QueueItemProcessor.SelectArticlesForExistenceCheck(files, "full");
        var probeId = QueueItemProcessor.SelectMidpointPreflightSegment(files);
        Assert.Equal("video-4", probeId);
        Assert.NotNull(probeId);

        var available = articles
            .Where(id => id != probeId)
            .ToDictionary(id => id, _ => Array.Empty<byte>());
        var client = new FakeNntpClient(available);
        var progress = new List<int>();

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            QueueItemProcessor.CheckExistenceWithOptionalMidpointPreflightAsync(
                client, files, articles, "full", healthCheckConcurrency: 4,
                new CollectingProgress(progress), CancellationToken.None));

        Assert.Equal(probeId, exception.SegmentId);
        Assert.Equal(new[] { probeId }, client.StatRequestOrder);
        Assert.Equal(1, client.StatRequestCounts[probeId]);
    }

    [Fact]
    public async Task MidpointPreflight_HitChecksEveryRemainingIdExactlyOnce()
    {
        var files = new IReadOnlyList<string>[] { Segments("video", 8), Segments("extra", 4) };
        var articles = QueueItemProcessor.SelectArticlesForExistenceCheck(files, "full");
        var probeId = QueueItemProcessor.SelectMidpointPreflightSegment(files)!;
        var available = articles.ToDictionary(id => id, _ => Array.Empty<byte>());
        var client = new FakeNntpClient(available);
        var progress = new List<int>();

        await QueueItemProcessor.CheckExistenceWithOptionalMidpointPreflightAsync(
            client, files, articles, "full", healthCheckConcurrency: 4,
            new CollectingProgress(progress), CancellationToken.None);

        Assert.Equal(probeId, client.StatRequestOrder[0]);
        Assert.Equal(articles.Count, client.StatRequestOrder.Count);
        Assert.Equal(articles.ToHashSet(), client.StatRequestOrder.ToHashSet());
        Assert.All(client.StatRequestCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(articles.Count, progress[^1]);
    }

    [Fact]
    public async Task MidpointPreflight_SampledModeIssuesNoExtraRequest()
    {
        var files = new IReadOnlyList<string>[] { Segments("video", 20_000) };
        var articles = QueueItemProcessor.SelectArticlesForExistenceCheck(files, "sampled");
        var probeId = QueueItemProcessor.SelectMidpointPreflightSegment(files)!;
        var available = articles.ToDictionary(id => id, _ => Array.Empty<byte>());
        var client = new FakeNntpClient(available);

        await QueueItemProcessor.CheckExistenceWithOptionalMidpointPreflightAsync(
            client, files, articles, "sampled", healthCheckConcurrency: 4,
            part3Progress: null, CancellationToken.None);

        Assert.Equal(articles.Count, client.StatRequestOrder.Count);
        Assert.Equal(articles.ToHashSet(), client.StatRequestOrder.ToHashSet());
        if (probeId != articles[0])
            Assert.NotEqual(probeId, client.StatRequestOrder[0]);
    }

    private sealed class CollectingProgress(List<int> values) : IProgress<int>
    {
        public void Report(int value) => values.Add(value);
    }

    private static List<string> Segments(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix}-{i}").ToList();
}
