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

    private static List<string> Segments(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix}-{i}").ToList();
}
