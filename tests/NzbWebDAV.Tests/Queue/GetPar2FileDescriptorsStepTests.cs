using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Par2Recovery;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class GetPar2FileDescriptorsStepTests
{
    [Fact]
    public async Task GetPar2FileDescriptors_MergesDescriptorsFromAllIndexFiles()
    {
        // Per-episode season-pack layout: one single-segment par2 index per
        // content file. Every index must be read, not just the first/smallest.
        var idA = FileId(0x0A);
        var idB = FileId(0x0B);
        var indexA = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(idA, "Show.S01E01.mkv"));
        var indexB = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(idB, "Show.S01E02.mkv"));
        var vol = Par2TestPackets.BuildPar2Bytes(
            Par2TestPackets.BuildFileDescBody(FileId(0x0C), "volume-descriptor.mkv"));

        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["index-a@example.com"] = indexA,
            ["index-b@example.com"] = indexB,
            ["vol@example.com"] = vol,
        });

        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("Release [AAAAAAAA].mkv", "video-a@example.com"),
            Par2File("Release [BBBBBBBB].par2", "index-b@example.com", indexB),
            Par2File("Release [AAAAAAAA].par2", "index-a@example.com", indexA),
            Par2File("Release [AAAAAAAA].vol00+01.par2", "vol@example.com", vol),
        };

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);

        Assert.Equal(
            [Convert.ToHexString(idB), Convert.ToHexString(idA)],
            descriptors.Select(x => Convert.ToHexString(x.FileID)).ToArray());
        // Recovery volumes duplicate index descriptors; they must not be read.
        Assert.DoesNotContain("vol@example.com", client.RequestedSegmentIds);
    }

    [Fact]
    public async Task GetPar2FileDescriptors_FallsBackToVolumeWhenNoIndexIdentifiable()
    {
        var idVol = FileId(0x0C);
        var vol = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(idVol, "movie.mkv"));

        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["vol@example.com"] = vol,
        });

        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("Release [AAAAAAAA].mkv", "video-a@example.com"),
            Par2File("Release.vol00+01.par2", "vol@example.com", vol),
        };

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);

        var descriptor = Assert.Single(descriptors);
        Assert.Equal("movie.mkv", descriptor.FileName);
    }

    [Fact]
    public async Task GetPar2FileDescriptors_DedupesDescriptorsByFileId()
    {
        var id = FileId(0x0A);
        var indexA = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(id, "Show.S01E01.mkv"));
        var indexB = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(id, "Show.S01E01.mkv"));

        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["index-a@example.com"] = indexA,
            ["index-b@example.com"] = indexB,
        });

        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            Par2File("Release [AAAAAAAA].par2", "index-a@example.com", indexA),
            Par2File("Release [BBBBBBBB].par2", "index-b@example.com", indexB),
        };

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);

        Assert.Single(descriptors);
    }

    [Fact]
    public async Task GetPar2FileDescriptors_ReturnsEmptyWhenNoPar2Present()
    {
        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>());
        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("Release [AAAAAAAA].mkv", "video-a@example.com"),
        };

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);

        Assert.Empty(descriptors);
    }

    private static byte[] FileId(byte fill) => Enumerable.Repeat(fill, 16).ToArray();

    private static FetchFirstSegmentsStep.NzbFileWithFirstSegment Par2File(
        string subjectName, string messageId, byte[] par2Bytes)
    {
        return new()
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{subjectName}\" yEnc (1/1)",
                Segments = { new NzbSegment { MessageId = messageId, Bytes = par2Bytes.Length } },
            },
            Header = new UsenetYencHeader
            {
                FileName = subjectName,
                FileSize = par2Bytes.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = par2Bytes.Length,
            },
            First16KB = par2Bytes,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
    }

    private static FetchFirstSegmentsStep.NzbFileWithFirstSegment VideoFile(string subjectName, string messageId)
    {
        return new()
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{subjectName}\" yEnc (1/1)",
                Segments = { new NzbSegment { MessageId = messageId, Bytes = 1000 } },
            },
            Header = null,
            First16KB = new byte[64], // no par2 magic
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
    }

    /// <summary>
    /// Serves raw decoded bytes via CachedYencStream so tests do not depend on
    /// rapidyenc native (same approach as LazyRarProcessorTests).
    /// </summary>
    private sealed class Par2ServingNntpClient(IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
        public HashSet<string> RequestedSegmentIds { get; } = new(StringComparer.Ordinal);

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            RequestedSegmentIds.Add(key);
            if (!segments.TryGetValue(key, out var bytes))
                throw new UsenetArticleNotFoundException(key);

            var headers = new UsenetYencHeader
            {
                FileName = "file.par2",
                FileSize = bytes.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = bytes.Length,
            };
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = new CachedYencStream(headers, new MemoryStream(bytes, writable: false)),
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
