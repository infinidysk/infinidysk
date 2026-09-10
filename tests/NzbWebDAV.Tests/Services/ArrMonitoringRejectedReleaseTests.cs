using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class ArrMonitoringRejectedReleaseTests
{
    [Fact]
    public async Task SuccessfulBlocklist_SeedsRejectedReleaseSegments()
    {
        var segmentId = $"{Guid.NewGuid():N}@test";
        var downloadId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new DavDatabaseContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.HistoryItems.Add(new HistoryItem
            {
                Id = downloadId,
                CreatedAt = DateTime.UtcNow,
                FileName = "synthetic.nzb",
                JobName = "Synthetic.Release",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                NzbBlobId = blobId,
            });
            await context.SaveChangesAsync();
        }
        var blobStore = new MemoryBlobStore(new Dictionary<Guid, byte[]>
        {
            [blobId] = Encoding.UTF8.GetBytes(CreateNzb(segmentId)),
        });
        var config = new ArrConfig
        {
            QueueRules =
            [
                new ArrConfig.QueueRule
                {
                    Message = "not wanted",
                    Action = ArrConfig.QueueAction.RemoveAndBlocklist,
                },
            ],
        };
        using var httpClient = new HttpClient(new DeleteHandler(HttpStatusCode.NoContent));
        var client = new TestArrClient(httpClient);
        var service = new ArrMonitoringService(
            new ConfigManager(),
            new ArrReplacementSearchBudget(),
            new TestDbContextFactory(options),
            blobStore);
        var item = new ArrQueueRecord
        {
            Id = 42,
            Title = "Synthetic.Release",
            Status = "completed",
            DownloadId = downloadId.ToString("D"),
            StatusMessages =
            [
                new ArrQueueStatusMessage { Messages = ["not wanted"] },
            ],
        };

        await service.HandleStuckQueueItem(
            item, config, client, new HashSet<Guid>(), CancellationToken.None);

        Assert.Throws<UsenetArticleNotFoundException>(() =>
            HealthCheckService.CheckCachedMissingSegmentIds([segmentId]));
        HealthCheckService.CheckCachedMissingSegmentIds([$"{Guid.NewGuid():N}@test"]);
                Assert.Equal([blobId], blobStore.ReadIds);
    }

    [Fact]
    public async Task Blocklist_PublishesCapturedSegmentsOnlyAfterSuccessfulResponse()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = await Fixture.CreateAsync(
            HttpStatusCode.OK,
            ArrConfig.QueueAction.RemoveAndBlocklist,
            response);
        await using (fixture)
        {
            var handling = fixture.Service.HandleStuckQueueItem(
                fixture.Item,
                fixture.Config,
                fixture.Client,
                new HashSet<Guid>(),
                CancellationToken.None);
            await fixture.Handler.RequestReceived.Task;

            Assert.Equal([fixture.BlobId], fixture.BlobStore.ReadIds);
            HealthCheckService.CheckCachedMissingSegmentIds([fixture.SegmentId]);

            response.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await handling;

            Assert.Throws<UsenetArticleNotFoundException>(() =>
                HealthCheckService.CheckCachedMissingSegmentIds([fixture.SegmentId]));
            Assert.Contains("blocklist=true", fixture.Handler.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("skipRedownload=true", fixture.Handler.RequestUri.Query, StringComparison.Ordinal);
        }
    }

            [Theory]
            [InlineData(HttpStatusCode.BadRequest)]
            [InlineData(HttpStatusCode.NotFound)]
            [InlineData(HttpStatusCode.InternalServerError)]
            public async Task UnsuccessfulBlocklist_DoesNotSeedRejectedReleaseSegments(HttpStatusCode statusCode)
            {
                var fixture = await Fixture.CreateAsync(statusCode, ArrConfig.QueueAction.RemoveAndBlocklist);
                await using (fixture)
                {
                    await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        new HashSet<Guid>(),
                        CancellationToken.None);

                    HealthCheckService.CheckCachedMissingSegmentIds([fixture.SegmentId]);
                    Assert.Equal([fixture.BlobId], fixture.BlobStore.ReadIds);
                }
            }

            [Fact]
            public async Task PlainRemoval_DoesNotReadBlobOrSeedSegments()
            {
                var fixture = await Fixture.CreateAsync(HttpStatusCode.OK, ArrConfig.QueueAction.Remove);
                await using (fixture)
                {
                    await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        new HashSet<Guid>(),
                        CancellationToken.None);

                    HealthCheckService.CheckCachedMissingSegmentIds([fixture.SegmentId]);
                    Assert.Empty(fixture.BlobStore.ReadIds);
                    Assert.Contains("blocklist=false", fixture.Handler.RequestUri!.Query, StringComparison.Ordinal);
                }
            }

            [Fact]
            public void SelectRejectedReleaseSeedSegments_CoversEachFileBeforeRemainingSegments()
            {
                var document = new NzbDocument();
                var first = new NzbFile { Subject = "first.mkv" };
                first.Segments.AddRange(
                [
                    new NzbSegment { Bytes = 1, MessageId = "first-1" },
                    new NzbSegment { Bytes = 1, MessageId = "first-2" },
                ]);
                var second = new NzbFile { Subject = "second.par2" };
                second.Segments.AddRange(
                [
                    new NzbSegment { Bytes = 1, MessageId = "second-1" },
                    new NzbSegment { Bytes = 1, MessageId = "second-2", FallbackMessageIds = ["fallback"] },
                ]);
                document.Files.AddRange([first, second]);

                var selected = ArrMonitoringService.SelectRejectedReleaseSeedSegments(document, 3);

                Assert.Equal(["first-1", "second-1", "first-2"], selected);
                Assert.DoesNotContain("fallback", selected);
            }

            [Fact]
            public void SelectRejectedReleaseSeedSegments_IsBoundedAndCoversEveryFile()
            {
                var document = new NzbDocument();
                for (var fileIndex = 0; fileIndex < HealthCheckService.RejectedReleaseSeedSegments; fileIndex++)
                {
                    var file = new NzbFile { Subject = $"file-{fileIndex}.bin" };
                    file.Segments.AddRange(
                    [
                        new NzbSegment { Bytes = 1, MessageId = $"file-{fileIndex}-first" },
                        new NzbSegment { Bytes = 1, MessageId = $"file-{fileIndex}-second" },
                    ]);
                    document.Files.Add(file);
                }

                var selected = ArrMonitoringService.SelectRejectedReleaseSeedSegments(
                    document,
                    HealthCheckService.RejectedReleaseSeedSegments);

                Assert.Equal(HealthCheckService.RejectedReleaseSeedSegments, selected.Length);
                Assert.Equal(
                    Enumerable.Range(0, HealthCheckService.RejectedReleaseSeedSegments)
                        .Select(index => $"file-{index}-first"),
                    selected);
            }

            [Theory]
            [InlineData(EvidenceFailure.MissingBlob)]
            [InlineData(EvidenceFailure.MalformedNzb)]
            [InlineData(EvidenceFailure.FailedHistory)]
            [InlineData(EvidenceFailure.InvalidDownloadId)]
            public async Task UnavailableEvidence_DoesNotPreventConfiguredBlocklist(EvidenceFailure failure)
            {
                var fixture = await Fixture.CreateAsync(
                    HttpStatusCode.NoContent,
                    ArrConfig.QueueAction.RemoveAndBlocklist,
                    failure: failure);
                await using (fixture)
                {
                    var resolution = await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        new HashSet<Guid>(),
                        CancellationToken.None);

                    Assert.NotNull(resolution);
                    Assert.NotNull(fixture.Handler.RequestUri);
                    HealthCheckService.CheckCachedMissingSegmentIds([fixture.SegmentId]);
                    if (failure == EvidenceFailure.InvalidDownloadId)
                        Assert.Empty(fixture.BlobStore.ReadIds);
                }
            }

            [Fact]
            public async Task RepeatedDownloadInOnePass_CapturesOnceButDeletesEveryRecord()
            {
                var fixture = await Fixture.CreateAsync(
                    HttpStatusCode.NoContent,
                    ArrConfig.QueueAction.RemoveAndBlocklist);
                await using (fixture)
                {
                    var seededDownloadIds = new HashSet<Guid>();
                    await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        seededDownloadIds,
                        CancellationToken.None);
                    await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        seededDownloadIds,
                        CancellationToken.None);

                    Assert.Equal([fixture.BlobId], fixture.BlobStore.ReadIds);
                    Assert.Equal(2, fixture.Handler.RequestCount);
                }
            }

            [Fact]
            public async Task SuccessfulBlocklist_StopsRegrabBeforeAnyNntpRequest()
            {
                var fixture = await Fixture.CreateAsync(
                    HttpStatusCode.NoContent,
                    ArrConfig.QueueAction.RemoveAndBlocklistAndSearch);
                await using (fixture)
                {
                    await fixture.Service.HandleStuckQueueItem(
                        fixture.Item,
                        fixture.Config,
                        fixture.Client,
                        new HashSet<Guid>(),
                        CancellationToken.None);

                    var queueItem = new QueueItem
                    {
                        Id = Guid.NewGuid(),
                        CreatedAt = DateTime.UtcNow,
                        SortOrder = QueueItem.SortOrderStride,
                        FileName = "Synthetic.Regrab.nzb",
                        JobName = "Synthetic.Regrab",
                        NzbFileSize = 512,
                        TotalSegmentBytes = 128,
                        Category = "movies",
                        Priority = QueueItem.PriorityOption.Normal,
                        PostProcessing = QueueItem.PostProcessingOption.None,
                    };
                    await using var context = new DavDatabaseContext(fixture.Options);
                    context.QueueItems.Add(queueItem);
                    await context.SaveChangesAsync();
                    var dbClient = new DavDatabaseClient(context);
                    var nntpClient = new FakeNntpClient(new Dictionary<string, byte[]>());
                    var configManager = new ConfigManager();
                    using var healthCheckConnectionGate = new HealthCheckConnectionGate(configManager);
                    await using var nzbStream = new MemoryStream(
                        Encoding.UTF8.GetBytes(CreateNzb(fixture.SegmentId)),
                        writable: false);
                    var processor = new QueueItemProcessor(
                        queueItem,
                        nzbStream,
                        dbClient,
                        nntpClient,
                        configManager,
                        new WebsocketManager(),
                        new Progress<int>(),
                        healthCheckConnectionGate,
                        CancellationToken.None);

                    await processor.ProcessAsync();

                    context.ChangeTracker.Clear();
                    Assert.DoesNotContain(await context.QueueItems.AsNoTracking().ToListAsync(), x => x.Id == queueItem.Id);
                    var failed = await context.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == queueItem.Id);
                    Assert.Equal(HistoryItem.DownloadStatusOption.Failed, failed.DownloadStatus);
                    Assert.Contains(fixture.SegmentId, failed.FailMessage, StringComparison.Ordinal);
                    Assert.Equal(0, nntpClient.BodyRequestCount);
                    Assert.Equal(0, nntpClient.BatchRequestCount);
                    Assert.Equal(0, nntpClient.HeaderProbeCount);
                    Assert.Empty(nntpClient.StatRequestCounts);
                    Assert.Contains("blocklist=true", fixture.Handler.RequestUri!.Query, StringComparison.Ordinal);
                    Assert.Contains("skipRedownload=false", fixture.Handler.RequestUri.Query, StringComparison.Ordinal);
                }
            }

        private static string CreateNzb(string segmentId) => $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                    <file subject="Synthetic.Release.mkv">
                        <groups><group>alt.binaries.test</group></groups>
                        <segments><segment bytes="128" number="1">{{segmentId}}</segment></segments>
                    </file>
                </nzb>
                """;

    private sealed class TestArrClient(HttpClient client) : ArrClient("http://arr.test", "test-key")
    {
        protected override HttpClient Client => client;
    }

    private sealed class DeleteHandler(
        HttpStatusCode statusCode,
        TaskCompletionSource<HttpResponseMessage>? response = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }
        public TaskCompletionSource RequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            RequestUri = request.RequestUri;
            RequestCount++;
            RequestReceived.TrySetResult();
            return response is null
                ? new HttpResponseMessage(statusCode)
                : await response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<DavDatabaseContext> options)
        : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() => new(options);
    }

    private sealed class MemoryBlobStore(Dictionary<Guid, byte[]> blobs) : IBlobStore
    {
        public List<Guid> ReadIds { get; } = [];

        public Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Stream? ReadBlob(Guid id)
        {
            ReadIds.Add(id);
            return blobs.TryGetValue(id, out var value) ? new MemoryStream(value, writable: false) : null;
        }

        public Task<T?> ReadBlob<T>(Guid id) => throw new NotSupportedException();
        public bool Exists(Guid id) => blobs.ContainsKey(id);
        public bool Delete(Guid id) => blobs.Remove(id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly HttpClient _httpClient;

        private Fixture(
            SqliteConnection connection,
            HttpClient httpClient,
            DbContextOptions<DavDatabaseContext> options,
            Guid blobId,
            string segmentId,
            MemoryBlobStore blobStore,
            DeleteHandler handler,
            TestArrClient client,
            ArrMonitoringService service,
            ArrQueueRecord item,
            ArrConfig config)
        {
            _connection = connection;
            _httpClient = httpClient;
            Options = options;
            BlobId = blobId;
            SegmentId = segmentId;
            BlobStore = blobStore;
            Handler = handler;
            Client = client;
            Service = service;
            Item = item;
            Config = config;
        }

        public Guid BlobId { get; }
        public DbContextOptions<DavDatabaseContext> Options { get; }
        public string SegmentId { get; }
        public MemoryBlobStore BlobStore { get; }
        public DeleteHandler Handler { get; }
        public TestArrClient Client { get; }
        public ArrMonitoringService Service { get; }
        public ArrQueueRecord Item { get; }
        public ArrConfig Config { get; }

        public static async Task<Fixture> CreateAsync(
            HttpStatusCode statusCode,
            ArrConfig.QueueAction action,
            TaskCompletionSource<HttpResponseMessage>? response = null,
            EvidenceFailure failure = EvidenceFailure.None)
        {
            var segmentId = $"{Guid.NewGuid():N}@test";
            var downloadId = Guid.NewGuid();
            var blobId = Guid.NewGuid();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(connection)
                .Options;
            await using (var context = new DavDatabaseContext(options))
            {
                await context.Database.EnsureCreatedAsync();
                context.HistoryItems.Add(new HistoryItem
                {
                    Id = downloadId,
                    CreatedAt = DateTime.UtcNow,
                    FileName = "synthetic.nzb",
                    JobName = "Synthetic.Release",
                    Category = "movies",
                    DownloadStatus = failure == EvidenceFailure.FailedHistory
                        ? HistoryItem.DownloadStatusOption.Failed
                        : HistoryItem.DownloadStatusOption.Completed,
                    NzbBlobId = blobId,
                });
                await context.SaveChangesAsync();
            }

            var blobs = new Dictionary<Guid, byte[]>();
            if (failure != EvidenceFailure.MissingBlob)
            {
                blobs[blobId] = failure == EvidenceFailure.MalformedNzb
                    ? "<not-an-nzb"u8.ToArray()
                    : Encoding.UTF8.GetBytes(CreateNzb(segmentId));
            }
            var blobStore = new MemoryBlobStore(blobs);
            var handler = new DeleteHandler(statusCode, response);
            var httpClient = new HttpClient(handler);
            var client = new TestArrClient(httpClient);
            var service = new ArrMonitoringService(
                new ConfigManager(),
                new ArrReplacementSearchBudget(),
                new TestDbContextFactory(options),
                blobStore);
            var item = new ArrQueueRecord
            {
                Id = 42,
                Title = "Synthetic.Release",
                Status = "completed",
                DownloadId = failure == EvidenceFailure.InvalidDownloadId
                    ? "not-a-guid"
                    : downloadId.ToString("D"),
                StatusMessages =
                [
                    new ArrQueueStatusMessage { Messages = ["not wanted"] },
                ],
            };
            var config = new ArrConfig
            {
                QueueRules =
                [
                    new ArrConfig.QueueRule { Message = "not wanted", Action = action },
                ],
            };
            return new Fixture(
                connection,
                httpClient,
                options,
                blobId,
                segmentId,
                blobStore,
                handler,
                client,
                service,
                item,
                config);
        }

        public async ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            await _connection.DisposeAsync();
        }
    }

    public enum EvidenceFailure
    {
        None,
        MissingBlob,
        MalformedNzb,
        FailedHistory,
        InvalidDownloadId,
    }
}