using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Streams;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(PlaybackHoleTrackerCollection))]
public sealed class FinalMediaReadinessValidatorTests : IDisposable
{
    private static readonly byte[] EbmlMagic = [0x1A, 0x45, 0xDF, 0xA3];

    public FinalMediaReadinessValidatorTests() => PlaybackHoleTracker.ResetForTests();
    public void Dispose() => PlaybackHoleTracker.ResetForTests();

    [Fact]
    public async Task ValidateAsync_ProbesHeadAndTailWithBoundedUnpipelinedStream()
    {
        var nzbFile = TwoSegmentFile("head@test", "tail@test", out var headBytes, out var tailBytes);
        var client = new ProbeRecordingClient();
        client.Serve("head@test", headBytes);
        client.Serve("tail@test", tailBytes);

        // A tripped playback-hole state for the mounted path must not affect the probe:
        // validation has to read the real provider bytes.
        var davPath = "/content/movies/Job/Movie.mkv";
        for (var i = 0; i < GapFillLimits.MaxConsecutiveZeroFills; i++)
            PlaybackHoleTracker.RecordHole(davPath, $"other-{i}@test", new UsenetArticleNotFoundException($"other-{i}@test"));

        await new FinalMediaReadinessValidator(client, new ConfigManager())
            .ValidateAsync([new FinalMediaReadinessValidator.ProbeTarget("Movie.mkv", nzbFile, 40_000)],
                CancellationToken.None);

        Assert.Equal(1, client.GetFileStreamCalls);
        Assert.Equal(0, client.ArticleBufferSize);
        Assert.Equal(false, client.UsePipelinedBodyRequests);
        Assert.Equal(1, client.StreamingBodyBatchWidth);
        Assert.Equal("import-readiness Movie.mkv", client.ProbeFileName);
        Assert.False(client.ProbeFileName!.StartsWith('/'));
        Assert.Equal(2, client.BodyRequestCount);
        Assert.Equal(0, client.BatchRequestCount);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(davPath, out _));
    }

    [Fact]
    public async Task ValidateAsync_MissingHeadSegment_IsNonRetryable_AndDoesNotPoisonPlaybackTracker()
    {
        var nzbFile = TwoSegmentFile("gone-head@test", "gone-tail@test", out _, out _);
        var client = new ProbeRecordingClient();
        var davPath = "/content/movies/Job/Movie.mkv";

        var exception = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            new FinalMediaReadinessValidator(client, new ConfigManager())
                .ValidateAsync([new FinalMediaReadinessValidator.ProbeTarget("Movie.mkv", nzbFile, 40_000)],
                    CancellationToken.None));

        Assert.Contains("Movie.mkv", exception.Message);
        Assert.IsType<UsenetArticleNotFoundException>(exception.InnerException);
        Assert.False(PlaybackHoleTracker.ShouldFailFast(davPath, out _));
        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(davPath, "gone-head@test"));
    }

    [Fact]
    public async Task ValidateAsync_InvalidContainerSignature_IsNonRetryable()
    {
        var nzbFile = TwoSegmentFile("bad-head@test", "bad-tail@test", out _, out var tailBytes);
        var client = new ProbeRecordingClient();
        client.Serve("bad-head@test", new byte[20_000]);
        client.Serve("bad-tail@test", tailBytes);

        var exception = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            new FinalMediaReadinessValidator(client, new ConfigManager())
                .ValidateAsync([new FinalMediaReadinessValidator.ProbeTarget("Movie.mkv", nzbFile, 40_000)],
                    CancellationToken.None));

        Assert.Contains("unreadable media bytes", exception.Message);
        Assert.IsType<NonRetryableDownloadException>(exception.InnerException);
        Assert.Contains("invalid media container header", exception.InnerException.Message);
    }

    [Fact]
    public async Task ValidateAsync_Cancellation_StaysCancellation()
    {
        var nzbFile = TwoSegmentFile("cancel-head@test", "cancel-tail@test", out var headBytes, out _);
        var client = new ProbeRecordingClient();
        client.Serve("cancel-head@test", headBytes);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FinalMediaReadinessValidator(client, new ConfigManager())
                .ValidateAsync([new FinalMediaReadinessValidator.ProbeTarget("Movie.mkv", nzbFile, 40_000)],
                    cts.Token));
    }

    [Fact]
    public void PlanTargets_MountsOnlyMediaThatSurvivesOutputFiltering()
    {
        var results = new List<BaseProcessor.Result>
        {
            DirectFile("Movie.mkv", 2_000_000_000),
            DirectFile("Movie.unpack.mkv", 1_500_000_000),
            DirectFile("Movie.sample.mkv", 50_000_000),
            DirectFile("notes.txt", 2_000),
            DirectFile("", 1_000_000_000, sniffed: ".mkv"),
        };

        var targets = FinalMediaReadinessValidator.PlanTargets(results, "movies", "Job", new ConfigManager());

        Assert.Equal(
            ["Job.mkv", "Movie.mkv"],
            targets.Select(x => x.Name).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void PlanTargets_SampleHeuristicUsesLargestVideoAcrossArchiveOutputs()
    {
        var sample = DirectFile("tiny.sample.mkv", 50_000_000);
        var archivedMovie = new RarProcessor.Result
        {
            StoredFileSegments =
            [
                new RarProcessor.StoredFileSegment
                {
                    NzbFile = new NzbFile
                    {
                        Subject = "\"archive.rar\" yEnc",
                        Segments = { new NzbSegment { MessageId = "rar-seg@test", Bytes = 1000 } },
                    },
                    PartSize = 1000,
                    ArchiveName = "archive.rar",
                    PartNumber = new RarProcessor.PartNumber { PartNumberFromHeader = 0 },
                    ReleaseDate = DateTimeOffset.UnixEpoch,
                    PathWithinArchive = "Archived.Movie.mkv",
                    ByteRangeWithinPart = new LongRange(0, 1000),
                    AesParams = null,
                    FileUncompressedSize = 5_000_000_000,
                },
            ],
        };

        var withArchive = FinalMediaReadinessValidator.PlanTargets(
            [sample, archivedMovie], "movies", "Job", new ConfigManager());
        var withoutArchive = FinalMediaReadinessValidator.PlanTargets(
            [sample], "movies", "Job", new ConfigManager());

        Assert.Empty(withArchive);
        Assert.Single(withoutArchive);
    }

    private static FileProcessor.Result DirectFile(
        string fileName,
        long fileSize,
        string? sniffed = null)
    {
        return new FileProcessor.Result
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{(string.IsNullOrEmpty(fileName) ? "obfuscated" : fileName)}\" yEnc",
                Segments = { new NzbSegment { MessageId = $"seg-{Guid.NewGuid():N}@test", Bytes = fileSize } },
            },
            FileName = fileName,
            FileSize = fileSize,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            SniffedVideoExtension = sniffed,
        };
    }

    private static NzbFile TwoSegmentFile(
        string headId,
        string tailId,
        out byte[] headBytes,
        out byte[] tailBytes)
    {
        headBytes = new byte[20_000];
        EbmlMagic.CopyTo(headBytes, 0);
        tailBytes = new byte[20_000];
        return new NzbFile
        {
            Subject = "\"Movie.mkv\" yEnc (1/2)",
            Segments =
            {
                new NzbSegment
                {
                    MessageId = headId,
                    Bytes = 20_000,
                    ByteRange = new LongRange(0, 20_000),
                },
                new NzbSegment
                {
                    MessageId = tailId,
                    Bytes = 20_000,
                    ByteRange = new LongRange(20_000, 40_000),
                },
            },
        };
    }

    private sealed class ProbeRecordingClient : NntpClient
    {
        private readonly Dictionary<string, byte[]> _segments = new(StringComparer.Ordinal);

        public int GetFileStreamCalls { get; private set; }
        public int ArticleBufferSize { get; private set; } = -1;
        public bool? UsePipelinedBodyRequests { get; private set; }
        public string? ProbeFileName { get; private set; }
        public int? StreamingBodyBatchWidth { get; private set; }
        public int BodyRequestCount { get; private set; }
        public int BatchRequestCount { get; private set; }

        public void Serve(string segmentId, byte[] bytes) => _segments[segmentId] = bytes;

        public override NzbFileStream GetFileStream(
            NzbFile nzbFile,
            long fileSize,
            int articleBufferSize,
            bool usePipelinedBodyRequests = true,
            string? fileName = null,
            InFlightArticleBudget? inFlightArticleBudget = null,
            bool useContainerAwareFill = false,
            int streamingBodyBatchWidth = 4,
            HashSet<string>? knownCorruptSegmentIds = null,
            IReadOnlySet<int>? knownMissingSegmentIndices = null)
        {
            GetFileStreamCalls++;
            ArticleBufferSize = articleBufferSize;
            UsePipelinedBodyRequests = usePipelinedBodyRequests;
            ProbeFileName = fileName;
            StreamingBodyBatchWidth = streamingBodyBatchWidth;
            return base.GetFileStream(
                nzbFile,
                fileSize,
                articleBufferSize,
                usePipelinedBodyRequests,
                fileName,
                inFlightArticleBudget,
                useContainerAwareFill,
                streamingBodyBatchWidth,
                knownCorruptSegmentIds,
                knownMissingSegmentIndices);
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BodyRequestCount++;
            var key = segmentId.ToString();
            if (!_segments.TryGetValue(key, out var bytes))
            {
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new UsenetArticleNotFoundException(key, "430 No such article"));
            }

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = new CachedYencStream(
                    new UsenetYencHeader
                    {
                        FileName = "probe.bin",
                        FileSize = 40_000,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 2,
                        PartOffset = 0,
                        PartSize = bytes.Length,
                    },
                    new MemoryStream(bytes, writable: false)),
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchRequestCount++;
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

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

/// <summary>
/// Processor-level proof that the readiness probe runs before finalization: while a
/// probe is blocked on the provider, the process-wide finalize lock stays available
/// for an unrelated worker.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class ImportReadinessFinalizeLockTests : IAsyncLifetime
{
    private const string SegmentIdText = "readiness-segment@test";
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-readiness-lock-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Join(_configRoot, "db.sqlite")}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_configRoot))
                Directory.Delete(_configRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task ImportReadiness_DoesNotHoldFinalizeLock()
    {
        var payload = new byte[20_000];
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(payload, 0);
        using var client = new GatedProbeClient(payload);
        var queueItem = await SeedQueueItemAsync();

        using var finalizeLock = new SemaphoreSlim(1, 1);
        var readinessReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new QueueItemProcessor(
            queueItem,
            CreateNzbStream(),
            _dbClient,
            client,
            new ConfigManager(),
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new Progress<int>(),
            new ConcurrentDictionary<Guid, int>(),
            finalizeLock,
            CancellationToken.None,
            stageReporter: stage =>
            {
                if (stage == "import-readiness") readinessReported.TrySetResult();
            });

        var run = Task.Run(() => processor.ProcessAsync());
        try
        {
            // Wait until the probe is blocked inside its BODY fetch. The stage is
            // reported before the validator runs, so both markers are now behind us.
            await readinessReported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.ProbeBodyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Pre-fix, the probe ran inside MarkQueueItemCompleted and held this lock
            // for the whole NNTP read, starving every other worker's completion.
            Assert.True(
                await finalizeLock.WaitAsync(TimeSpan.Zero),
                "Finalize lock was held while the import-readiness probe was blocked on Usenet.");
            finalizeLock.Release();
        }
        finally
        {
            client.ReleaseProbe();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(30));

        _context.ChangeTracker.Clear();
        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await _context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.True(
            historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed,
            $"Expected Completed; got {historyItem.DownloadStatus}: {historyItem.FailMessage}");
    }

    private async Task<QueueItem> SeedQueueItemAsync()
    {
        var nzbBytes = Encoding.UTF8.GetBytes(NzbXml);
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Movie.nzb",
            JobName = "Movie.Release",
            NzbFileSize = nzbBytes.Length,
            TotalSegmentBytes = 20_000,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        _context.QueueItems.Add(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
    }

    private static MemoryStream CreateNzbStream() => new(Encoding.UTF8.GetBytes(NzbXml));

    private const string NzbXml =
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;Movie.mkv&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="20000" number="1">{SegmentIdText}</segment>
            </segments>
          </file>
        </nzb>
        """;

    /// <summary>
    /// Serves the single-segment video normally for import stages, but holds the first
    /// BODY response — which only the import-readiness probe issues — on a gate.
    /// </summary>
    private sealed class GatedProbeClient(byte[] payload) : NntpClient
    {
        private readonly TaskCompletionSource _probeGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ProbeBodyBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseProbe() => _probeGate.TrySetResult();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article",
                Stream = CreatePayloadStream(),
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                    },
                },
            });
        }

        public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) =>
            Task.FromResult((long)payload.Length);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeBodyBlocked.TrySetResult();
            await _probeGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = CreatePayloadStream(),
            };
        }

        private CachedYencStream CreatePayloadStream() =>
            new(
                new UsenetYencHeader
                {
                    FileName = "Movie.mkv",
                    FileSize = payload.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartOffset = 0,
                    PartSize = payload.Length,
                },
                new MemoryStream(payload, writable: false));

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

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
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
