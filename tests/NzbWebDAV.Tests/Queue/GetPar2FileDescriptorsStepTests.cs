using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Par2Recovery;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(GlobalLoggerCollection))]
public class GetPar2FileDescriptorsStepTests
{
    private static object? Scalar(LogEvent logEvent, string property) =>
        Assert.IsType<ScalarValue>(logEvent.Properties[property]).Value;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPar2FileDescriptors_ReadsChecksumPacketsAcrossArticles(bool corruptTail)
    {
        var (index, _) = Par2TestEncoder.EncodeSet("movie.mkv", new byte[8192], 4096, []);
        var split = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(8));
        var first = index[..split];
        var last = index[split..];
        if (corruptTail) last[^1] ^= 1;
        var file = Par2File("index.par2", "first@example.com", first);
        file.NzbFile.Segments.Add(new NzbSegment { MessageId = "last@example.com", Bytes = last.Length });
        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["first@example.com"] = first,
            ["last@example.com"] = last,
        });

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors([file], client);

        Assert.Equal(2, client.RequestedSegmentIds.Count);
        if (corruptTail) Assert.Empty(descriptors);
        else Assert.NotNull(Assert.Single(descriptors).VerificationProof);
    }

    [Fact]
    public async Task GetPar2FileDescriptors_BootstrapsVerifiedIndexWithoutTrustingPositiveMetadata()
    {
        var (index, _) = Par2TestEncoder.EncodeSet("movie.mkv", new byte[4096], 4096, []);
        var file = Par2File("index.par2", "index@example.com", index);
        file.Header!.FileSize = 1;
        file.Header.PartSize = 1;
        file.Header.TotalParts = 999;
        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["index@example.com"] = index,
        });

        var descriptor = Assert.Single(await GetPar2FileDescriptorsStep.GetPar2FileDescriptors([file], client));

        Assert.NotNull(descriptor.VerificationProof);
        Assert.Equal("index@example.com", Assert.Single(client.RequestedSegmentIds));
    }

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

        // Sorted by message-id for deterministic ordering: index-a before index-b
        Assert.Equal(
            [Convert.ToHexString(idA), Convert.ToHexString(idB)],
            descriptors.Select(x => Convert.ToHexString(x.FileID)).ToArray());
        // Recovery volumes duplicate index descriptors; they must not be read.
        Assert.DoesNotContain("vol@example.com", client.RequestedSegmentIds);
    }

    [Fact]
    public async Task GetPar2FileDescriptors_ReportsProgressAsZeroToHundredPercentage()
    {
        var idA = FileId(0x0A);
        var idB = FileId(0x0B);
        var indexA = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(idA, "Show.S01E01.mkv"));
        var indexB = Par2TestPackets.BuildPar2Bytes(Par2TestPackets.BuildFileDescBody(idB, "Show.S01E02.mkv"));

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

        // Progress<int> posts to the captured context asynchronously and can
        // reorder 50/100 under CI load. Collect synchronously so order is
        // the step's Report() sequence.
        var reported = new List<int>();
        await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client, new CollectingProgress(reported));

        // Two index files → 50 then 100, not raw counts 1 then 2.
        Assert.Equal([50, 100], reported);
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
    public async Task GetPar2FileDescriptors_ObfuscatedRecoveryVolume_DoesNotDownloadRecoveryData()
    {
        // Obfuscated releases use hashed subjects with no ".volNN+MM.par2"
        // suffix, so recovery volumes are not filtered out by name. The step
        // must still stop at the first recovery slice instead of streaming
        // the whole volume through the descriptor walker.
        var idVol = FileId(0x0C);
        const int recoveryBodyBytes = 4 * 1024 * 1024; // 4 MiB RecvSlic payload
        var vol = Par2TestPackets.BuildRecoveryVolumeBytes(
            [Par2TestPackets.BuildFileDescBody(idVol, "movie.mkv")],
            recoveryBodyBytes);

        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["vol@example.com"] = vol[..768000],
        });

        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("Release [AAAAAAAA].mkv", "video-a@example.com"),
            Par2File("deadbeef0123.par2", "vol@example.com", vol),
        };

        var descriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);

        var descriptor = Assert.Single(descriptors);
        Assert.Equal("movie.mkv", descriptor.FileName);
        Assert.True(client.TotalBytesRead < recoveryBodyBytes,
            $"Expected recovery body to be skipped, but {client.TotalBytesRead} bytes were read");
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

    [Fact]
    public async Task GetPar2FileDescriptors_LogsDiscoverySummary()
    {
        var (index, _) = Par2TestEncoder.EncodeSet("movie.mkv", new byte[8192], 4096, []);
        var file = Par2File("index.par2", "index@example.com", index);
        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>
        {
            ["index@example.com"] = index,
        });
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            await GetPar2FileDescriptorsStep.GetPar2FileDescriptors([file], client);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        var summary = Assert.Single(sink.Events, logEvent =>
            logEvent.MessageTemplate.Text.StartsWith("PAR2 metadata discovery finished", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Information, summary.Level);
        Assert.Equal(1, Scalar(summary, "Candidates"));
        Assert.Equal(1, Scalar(summary, "IndexCandidates"));
        Assert.Equal(1, Scalar(summary, "MetadataFilesAttempted"));
        Assert.Equal(1, Scalar(summary, "Descriptors"));
        Assert.Equal(1, Scalar(summary, "VerifiedProofs"));
        Assert.Equal(0, Scalar(summary, "UnverifiedDescriptors"));
        Assert.Equal(1, Scalar(summary, "SliceSizeCount"));
        Assert.Equal("4096", Scalar(summary, "SliceSizes"));
        Assert.Equal(false, Scalar(summary, "SliceSizesTruncated"));
        Assert.Equal(1, Scalar(summary, "ArticlesRequested"));
        Assert.Equal((long)index.Length, Scalar(summary, "BytesDownloaded"));
        Assert.Equal(false, Scalar(summary, "DeadlineExceeded"));
    }

    [Fact]
    public async Task GetPar2FileDescriptors_DoesNotLogSummaryWhenNoPar2Present()
    {
        using var client = new Par2ServingNntpClient(new Dictionary<string, byte[]>());
        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>
        {
            VideoFile("Release [AAAAAAAA].mkv", "video-a@example.com"),
        };
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            Assert.Empty(await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client));
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        Assert.DoesNotContain(sink.Events, logEvent =>
            logEvent.MessageTemplate.Text.StartsWith("PAR2 metadata discovery finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPar2FileDescriptors_CapsReportedSliceSizes()
    {
        var files = new List<FetchFirstSegmentsStep.NzbFileWithFirstSegment>();
        var served = new Dictionary<string, byte[]>();
        var expectedSizes = new List<ulong>();
        for (var indexNumber = 0; indexNumber < 9; indexNumber++)
        {
            var sliceSize = 4096UL + (ulong)indexNumber * 4;
            var data = Enumerable.Repeat((byte)(indexNumber + 1), 8192).ToArray();
            var (indexBytes, _) = Par2TestEncoder.EncodeSet(
                $"movie-{indexNumber}.mkv", data, sliceSize, []);
            var messageId = $"index-{indexNumber}@example.com";
            files.Add(Par2File($"index-{indexNumber}.par2", messageId, indexBytes));
            served.Add(messageId, indexBytes);
            expectedSizes.Add(sliceSize);
        }
        using var client = new Par2ServingNntpClient(served);
        var sink = new CollectingLogEventSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        try
        {
            Log.Logger = logger;
            await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(files, client);
        }
        finally
        {
            Log.Logger = previousLogger;
        }

        var summary = Assert.Single(sink.Events, logEvent =>
            logEvent.MessageTemplate.Text.StartsWith("PAR2 metadata discovery finished", StringComparison.Ordinal));
        Assert.Equal(9, Scalar(summary, "SliceSizeCount"));
        Assert.Equal(string.Join(",", expectedSizes.Take(8)), Scalar(summary, "SliceSizes"));
        Assert.Equal(true, Scalar(summary, "SliceSizesTruncated"));
    }

    private sealed class CollectingProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value) => reports.Add(value);
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
        private long _totalBytesRead;
        public HashSet<string> RequestedSegmentIds { get; } = new(StringComparer.Ordinal);
        public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

        private long AddBytesRead(int read) => Interlocked.Add(ref _totalBytesRead, read);

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
                Stream = new CachedYencStream(headers, new ReadCountingStream(bytes, AddBytesRead)),
            });
        }

        private sealed class ReadCountingStream(byte[] bytes, Func<int, long> onRead) : MemoryStream(bytes, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                onRead(read);
                return read;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = base.Read(buffer, offset, count);
                onRead(read);
                return read;
            }
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
