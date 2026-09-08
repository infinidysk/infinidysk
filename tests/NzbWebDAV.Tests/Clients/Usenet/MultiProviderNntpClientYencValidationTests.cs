using NzbWebDAV.Config;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class MultiProviderNntpClientYencValidationTests
{
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(3, 0, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 931, false)]
    public void MatchesExpectedFile_ValidatesTotalParts(
        int expectedTotalParts,
        int actualTotalParts,
        bool expected)
    {
        using var validation = YencFileValidationContext.Begin(expectedTotalParts);
        var header = CreateHeader(partNumber: 1, totalParts: actualTotalParts);

        Assert.Equal(expected, YencFileValidationContext.MatchesExpectedFile(header));
    }

    [Fact]
    public async Task GetYencHeadersAsync_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 557, totalParts: 931);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(1, header.PartNumber);
        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Fact]
    public async Task GetYencHeadersAsync_MultipartHeaderWithoutTotal_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 1, totalParts: 0);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = CreateProviderClient(wrongPost, correctPost);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Fact]
    public async Task GetFileSizeAsync_MismatchedLastSegment_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = CreateClient(
            segments,
            partNumber: 557,
            totalParts: 931,
            segmentId: "third",
            partOffset: 187_525_120);
        var correctPost = CreateClient(
            segments,
            partNumber: 3,
            totalParts: 3,
            segmentId: "third",
            partOffset: 6);
        using var client = CreateProviderClient(wrongPost, correctPost);
        var file = new NzbFile { Subject = "fake.bin" };
        foreach (var (segmentId, index) in segments.Keys.Select((id, index) => (id, index)))
        {
            file.Segments.Add(new NzbSegment
            {
                Bytes = 3,
                MessageId = segmentId,
                Number = index + 1,
            });
        }

        var fileSize = await client.GetFileSizeAsync(file, CancellationToken.None);

        Assert.Equal(9, fileSize);
        Assert.Equal(new LongRange(6, 9), file.Segments[^1].ByteRange);
        Assert.Equal(1, wrongPost.BodyRequestCounts["third"]);
        Assert.Equal(1, correctPost.BodyRequestCounts["third"]);
    }

    [Fact]
    public async Task DecodedArticleAsync_MismatchedTotalParts_UsesBackupProvider()
    {
        var innerSegments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        using var wrongInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        using var correctInner = new FakeNntpClient(innerSegments, useCachedYencStreams: true);
        using var wrongPost = new ArticleNntpClient(
            wrongInner, CreateHeader(partNumber: 557, totalParts: 931));
        using var correctPost = new ArticleNntpClient(
            correctInner, CreateHeader(partNumber: 1, totalParts: 3));
        using var client = CreateProviderClient(wrongPost, correctPost);
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var response = await client.DecodedArticleAsync("segment", CancellationToken.None);
        await using var responseStream = response.Stream;
        var header = await responseStream.GetYencHeadersAsync();

        Assert.Equal(3, header!.TotalParts);
        Assert.Equal(1, wrongPost.ArticleRequestCount);
        Assert.Equal(1, correctPost.ArticleRequestCount);
    }

    [Fact]
    public async Task GetYencHeadersAsync_MismatchedTotalParts_DoesNotSkipStorageGroupSibling()
    {
        var segments = new Dictionary<string, byte[]> { ["segment"] = [1, 2, 3] };
        var wrongPost = CreateClient(segments, partNumber: 557, totalParts: 931);
        var correctPost = CreateClient(segments, partNumber: 1, totalParts: 3);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(
                    wrongPost, host: "wrong.example", storageGroup: "shared"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost, host: "correct.example", storageGroup: "shared"),
            ],
            articleMissCache: new ArticleMissNegativeCache(new ConfigManager()));
        using var validation = YencFileValidationContext.Begin(expectedTotalParts: 3);

        var header = await client.GetYencHeadersAsync("segment", CancellationToken.None);

        Assert.Equal(3, header.TotalParts);
        Assert.Equal(1, wrongPost.BodyRequestCount);
        Assert.Equal(1, correctPost.BodyRequestCount);
    }

    [Fact]
    public async Task NzbFileStream_ByteZero_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                ["first"] = new()
                {
                    FileName = "wrong.bin",
                    FileSize = 318_803_968,
                    LineLength = 128,
                    PartNumber = 557,
                    TotalParts = 931,
                    PartOffset = 187_525_120,
                    PartSize = 3,
                },
            });
        var correctPost = new FakeNntpClient(segments, useCachedYencStreams: true);
        using var client = new MultiProviderNntpClient(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
        await using var stream = new NzbFileStream(
            ["first", "second", "third"],
            fileSize: 9,
            client,
            articleBufferSize: 4,
            usePipelinedBodyRequests: true);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));

        Assert.Equal(1, buffer[0]);
        Assert.Equal(1, wrongPost.BodyRequestCounts["first"]);
        Assert.Equal(1, correctPost.BodyRequestCounts["first"]);
    }

    [Fact]
    public async Task NzbFileStream_SeekProbe_MismatchedTotalParts_UsesBackupProvider()
    {
        var segments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongPost = CreateClient(
            segments, partNumber: 557, totalParts: 931, segmentId: "first");
        var correctPost = new FakeNntpClient(segments, useCachedYencStreams: true);
        using var client = CreateProviderClient(wrongPost, correctPost);
        var previousBudget = NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(1);
        try
        {
            await using var stream = new NzbFileStream(
                ["first", "second", "third"],
                fileSize: 9,
                client,
                articleBufferSize: 0,
                usePipelinedBodyRequests: false);
            stream.Seek(1, SeekOrigin.Begin);

            var buffer = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(buffer));

            Assert.Equal(2, buffer[0]);
            Assert.True(wrongPost.BodyRequestCounts["first"] >= 1);
            Assert.True(correctPost.BodyRequestCounts["first"] >= 1);
        }
        finally
        {
            NzbWebDAV.WebDav.Requests.RangeContext.SetReadBudget(previousBudget);
        }
    }

    [Fact]
    public async Task NzbFileStream_PipelinedMismatch_UsesBackupProvider()
    {
        var correctSegments = new Dictionary<string, byte[]>
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5, 6],
            ["third"] = [7, 8, 9],
        };
        var wrongSegments = correctSegments.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        wrongSegments["second"] = [99, 99, 99];
        var wrongPost = new FakeNntpClient(
            wrongSegments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                ["second"] = new()
                {
                    FileName = "wrong.bin",
                    FileSize = 318_803_968,
                    LineLength = 128,
                    PartNumber = 557,
                    TotalParts = 931,
                    PartOffset = 187_525_120,
                    PartSize = 3,
                },
            });
        var correctPost = new FakeNntpClient(correctSegments, useCachedYencStreams: true);
        using var client = CreateProviderClient(wrongPost, correctPost);
        await using var stream = new NzbFileStream(
            ["first", "second", "third"],
            fileSize: 9,
            client,
            articleBufferSize: 4,
            usePipelinedBodyRequests: true);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], output.ToArray());
        Assert.True(wrongPost.BodyRequestCounts["second"] >= 1);
        Assert.True(correctPost.BodyRequestCounts["second"] >= 1);
    }

    private static FakeNntpClient CreateClient(
        IReadOnlyDictionary<string, byte[]> segments,
        int partNumber,
        int totalParts,
        string segmentId = "segment",
        long partOffset = 0) =>
        new(
            segments,
            useCachedYencStreams: true,
            yencHeaders: new Dictionary<string, UsenetYencHeader>
            {
                [segmentId] = new()
                {
                    FileName = "fake.bin",
                    FileSize = 9,
                    LineLength = 128,
                    PartNumber = partNumber,
                    TotalParts = totalParts,
                    PartOffset = partOffset,
                    PartSize = 3,
                },
            });

    private static UsenetYencHeader CreateHeader(int partNumber, int totalParts) =>
        new()
        {
            FileName = "fake.bin",
            FileSize = 9,
            LineLength = 128,
            PartNumber = partNumber,
            TotalParts = totalParts,
            PartOffset = 0,
            PartSize = 3,
        };

    private sealed class ArticleNntpClient(
        INntpClient inner,
        UsenetYencHeader header) : WrappingNntpClient(inner)
    {
        public int ArticleRequestCount { get; private set; }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArticleRequestCount++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 fake article",
                ArticleHeaders = new UsenetArticleHeader { Headers = [] },
                Stream = new CachedYencStream(
                    header,
                    new MemoryStream([1, 2, 3], writable: false)),
            });
        }
    }

    private static MultiProviderNntpClient CreateProviderClient(
        INntpClient wrongPost,
        INntpClient correctPost) =>
        new(
            [
                MultiProviderNntpClientTests.CreateProvider(wrongPost, host: "wrong.example"),
                MultiProviderNntpClientTests.CreateProvider(
                    correctPost,
                    host: "correct.example",
                    providerType: ProviderType.BackupOnly),
            ],
            cascadeEnabled: () => true);
}