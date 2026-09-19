using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

namespace NzbWebDAV.Tests.Queue;

public class SevenZipProcessorTests
{
    private const string ArchiveSegmentId = "archive@example.invalid";
    private const string ArchiveSetId = "empty-sevenzip-regression";
    private const string MixedStoredArchiveBase64 =
        "N3q8ryccAARu4/hlFQAAAAAAAACEAAAAAAAAACp/hIpzdG9yZWQtc2V2ZW56aXAtZW50cnkBBAYAAQkVAAcLAQABAQAMFQAICgHuw9/ZAAAFBA4B0A8BYBFdAGQAaQByAAAAZABpAHIALwBiAGUAZgBvAHIAZQAuAHQAeAB0AAAAZABpAHIALwBkAGEAdABhAC4AYgBpAG4AAABkAGkAcgAvAGEAZgB0AGUAcgAuAHQAeAB0AAAAAAA=";

    private static SevenZipProcessor CreateProcessor(byte[] archiveBytes, FakeNntpClient client)
    {
        var fileInfo = new GetFileInfosStep.FileInfo
        {
            NzbFile = new NzbFile
            {
                Subject = "fixture.7z",
                Segments =
                {
                    new NzbSegment
                    {
                        MessageId = ArchiveSegmentId,
                        Bytes = archiveBytes.Length,
                    },
                },
            },
            FileName = "fixture.7z",
            FileSize = archiveBytes.Length,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };

        return new SevenZipProcessor(
            [fileInfo],
            client,
            new ConfigManager(),
            archivePassword: null,
            archiveSetId: ArchiveSetId,
            ct: CancellationToken.None);
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    [Fact]
    public async Task ProcessAsync_StoredArchiveWithEmptyMembers_PreservesAllFiles()
    {
        var archiveBytes = Convert.FromBase64String(MixedStoredArchiveBase64);
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { [ArchiveSegmentId] = archiveBytes },
            useCachedYencStreams: true);
        var progress = new RecordingProgress();

        var result = Assert.IsType<SevenZipProcessor.Result>(
            await CreateProcessor(archiveBytes, client).ProcessAsync(progress));

        Assert.Equal(
            new[] { "dir/after.txt", "dir/before.txt", "dir/data.bin" },
            result.SevenZipFiles.Select(file => file.PathWithinArchive)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.All(result.SevenZipFiles, file =>
        {
            Assert.Equal(ArchiveSetId, file.ArchiveSetId);
            Assert.Equal(DateTimeOffset.UnixEpoch, file.ReleaseDate);
        });
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1]);

        foreach (var path in new[] { "dir/before.txt", "dir/after.txt" })
        {
            var empty = Assert.Single(
                result.SevenZipFiles, file => file.PathWithinArchive == path);
            Assert.Empty(empty.DavMultipartFileMeta.FileParts);
            Assert.Null(empty.DavMultipartFileMeta.AesParams);
            Assert.False(empty.DavMultipartFileMeta.IsLazy);
            Assert.Empty(empty.DavMultipartFileMeta.PendingParts);
            Assert.Null(empty.SniffedVideoExtension);

            var requestsBeforeRead = client.BodyRequestCount;
            await using var emptyStream = new DavMultipartFileStream(
                new DavMultipartFile
                {
                    Id = Guid.NewGuid(),
                    Metadata = empty.DavMultipartFileMeta,
                },
                client,
                articleBufferSize: 0,
                resolver: null,
                usePipelinedBodyRequests: false,
                fileName: path);
            Assert.Equal(0L, emptyStream.Length);
            Assert.Equal(0, await emptyStream.ReadAsync(new byte[1]));
            Assert.Equal(requestsBeforeRead, client.BodyRequestCount);
        }

        var stored = Assert.Single(
            result.SevenZipFiles,
            file => file.PathWithinArchive == "dir/data.bin");
        var part = Assert.Single(stored.DavMultipartFileMeta.FileParts);
        Assert.Equal(21L, part.FilePartByteRange.Count);
        Assert.Null(stored.DavMultipartFileMeta.AesParams);

        await using var payloadStream = new DavMultipartFileStream(
            new DavMultipartFile
            {
                Id = Guid.NewGuid(),
                Metadata = stored.DavMultipartFileMeta,
            },
            client,
            articleBufferSize: 0,
            resolver: null,
            usePipelinedBodyRequests: false,
            fileName: stored.PathWithinArchive);
        using var output = new MemoryStream();
        await payloadStream.CopyToAsync(output);
        Assert.Equal(21L, payloadStream.Length);
        Assert.Equal("stored-sevenzip-entry"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task ProcessAsync_AllEmptyArchive_PreservesEveryFile()
    {
        var archiveBytes = await File.ReadAllBytesAsync(Path.Join(
            RepoPaths.FindRepoRoot(), "tests", "TestArchives", "Archives",
            "7Zip.EmptyStream.7z"));
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { [ArchiveSegmentId] = archiveBytes },
            useCachedYencStreams: true);
        var progress = new RecordingProgress();

        var result = Assert.IsType<SevenZipProcessor.Result>(
            await CreateProcessor(archiveBytes, client).ProcessAsync(progress));

        Assert.Equal(
            new[] { "000", "001", "NewFolder/A", "NewFolder/B" },
            result.SevenZipFiles.Select(file => file.PathWithinArchive)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.All(result.SevenZipFiles, file =>
        {
            Assert.Empty(file.DavMultipartFileMeta.FileParts);
            Assert.Null(file.DavMultipartFileMeta.AesParams);
            Assert.False(file.DavMultipartFileMeta.IsLazy);
            Assert.Null(file.SniffedVideoExtension);
        });
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public async Task ProcessAsync_CompressedArchiveWithEmptyMember_RemainsNonRetryable()
    {
        using var archiveStream = new MemoryStream();
        using (var writer = new SevenZipWriter(
            archiveStream,
            new SevenZipWriterOptions(CompressionType.LZMA2)
            {
                LeaveStreamOpen = true,
                CompressHeader = false,
                Solid = false,
            }))
        {
            using var empty = new MemoryStream();
            writer.Write("empty.txt", empty, DateTime.UnixEpoch);
            using var payload = new MemoryStream("compressed-payload"u8.ToArray());
            writer.Write("data.txt", payload, DateTime.UnixEpoch);
        }

        var archiveBytes = archiveStream.ToArray();
        using var client = new FakeNntpClient(
            new Dictionary<string, byte[]> { [ArchiveSegmentId] = archiveBytes },
            useCachedYencStreams: true);
        var progress = new RecordingProgress();

        var exception = await Assert.ThrowsAsync<Unsupported7zCompressionMethodException>(
            () => CreateProcessor(archiveBytes, client).ProcessAsync(progress));

        Assert.True(exception.IsNonRetryableDownloadException());
        Assert.Contains("Copy/store", exception.Message);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public void CompressionRejection_ExplainsStreamingPolicyAndRemainsNonRetryable()
    {
        var exception = new Unsupported7zCompressionMethodException();

        Assert.Contains("intentionally unsupported", exception.Message);
        Assert.Contains("streaming and seeking", exception.Message);
        Assert.Contains("Copy/store", exception.Message);
        Assert.Contains("not an application error", exception.Message);
        Assert.Contains("Choose a different release", exception.Message);
        Assert.True(exception.IsNonRetryableDownloadException());
        Assert.False(exception.IsRetryableDownloadException());
    }

    [Theory]
    [InlineData(new[] { "A.7z.003", "A.7z.001", "A.7z.002" }, new[] { "A.7z.001", "A.7z.002", "A.7z.003" })]
    [InlineData(new[] { "Movie.7z" }, new[] { "Movie.7z" })]
    public void OrderVolumes_SortsByOrdinal(string[] names, string[] expected)
    {
        var ordered = SevenZipProcessor.OrderVolumes(names.Select(name => Info(name)).ToList());

        Assert.Equal(expected, ordered.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void OrderVolumes_MultipartBaseIsCaseInsensitive()
    {
        var ordered = SevenZipProcessor.OrderVolumes([
            Info("A.7z.002"),
            Info("a.7Z.001"),
        ]);

        Assert.Equal(["a.7Z.001", "A.7z.002"], ordered.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void OrderVolumes_DuplicateOrdinalIsRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z.001", "a@example.com"),
            Info("A.7z.001", "b@example.com"),
            Info("A.7z.002"),
        ]));
    }

    [Theory]
    [InlineData("A.7z.001", "A.7z.003")]
    [InlineData("A.7z.002", "A.7z.003")]
    [InlineData("A.7z.002")]
    public void OrderVolumes_GapOrMissingFirstVolumeIsRejected(params string[] names)
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes(names.Select(name => Info(name)).ToList()));
    }

    [Fact]
    public void OrderVolumes_DifferentBasesAreRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z.001"),
            Info("B.7z.002"),
        ]));
    }

    [Fact]
    public void OrderVolumes_StandaloneAndMultipartAreRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z"),
            Info("A.7z.001"),
        ]));
    }

    [Theory]
    [InlineData("A.7z.000")]
    [InlineData("A.7z.999999999999")]
    [InlineData("A.7z.001\n")]
    public void OrderVolumes_InvalidNamesAreRejected(string name)
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([Info(name)]));
    }

    private static GetFileInfosStep.FileInfo Info(string filename, string messageId = "archive@example.com") =>
        new()
        {
            NzbFile = new NzbFile
            {
                Subject = filename,
                Segments =
                {
                    new NzbSegment { MessageId = messageId, Bytes = 1024 }
                },
            },
            FileName = filename,
            FileSize = 1024,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
}
