using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Covers two things: that Arr's "unmatched" queue records (series/movie deleted while
/// downloading) no longer crash deserialization of the whole /queue page, and that
/// ArrMonitoringService can optionally reap those records instead of leaving them to
/// occupy a download-client connection slot forever.
/// </summary>
/// <remarks>
/// The Handle* tests below exercise ArrMonitoringService's real Log.Debug/Log.Warning
/// calls against the ambient Serilog Log.Logger, which is exactly what
/// GlobalLoggerCollection exists to serialize against other tests that temporarily
/// swap Log.Logger for a capturing sink (e.g. ArrMonitoringAggregationTests).
/// </remarks>
[Collection(nameof(GlobalLoggerCollection))]
public sealed class ArrMonitoringOrphanedQueueItemTests
{
    [Fact]
    public void SonarrQueueRecord_DeserializesUnmatchedRecord_WithoutThrowing()
    {
        // This is the actual shape Sonarr sends for a queue record it can't match to a
        // series (Sonarr.Api.V3 QueueResource: SeriesId/EpisodeId/SeasonNumber are all
        // `int?`, and go over the wire as null here) — most commonly because the series
        // was deleted from Sonarr while the release was still downloading.
        const string json = """
            {
                "id": 501,
                "title": "Some.Show.S01E01",
                "status": "downloading",
                "downloadId": "abc123",
                "seriesId": null,
                "episodeId": null,
                "seasonNumber": null
            }
            """;

        var record = JsonSerializer.Deserialize<SonarrQueueRecord>(json);

        Assert.NotNull(record);
        Assert.Null(record!.SeriesId);
        Assert.Null(record.EpisodeId);
        Assert.Null(record.SeasonNumber);
        Assert.Null(record.GetMediaIdentity());
    }

    [Theory]
    [InlineData("\"not-a-date\"")]
    [InlineData("\"0000-00-00T00:00:00Z\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("12345")]
    public void SonarrQueueRecord_ToleratesMalformedAddedTimestamp(string addedJson)
    {
        // A nullable DateTime already covers a missing or null field, but System.Text.Json throws
        // on a malformed date string. One such record would fail the whole page and put the
        // monitor back in the blind spot this change exists to remove.
        var json = $$"""
            {
                "id": 501,
                "title": "Some.Show.S01E01",
                "status": "completed",
                "seriesId": null,
                "added": {{addedJson}}
            }
            """;

        var record = JsonSerializer.Deserialize<SonarrQueueRecord>(json);

        Assert.NotNull(record);
        Assert.Null(record!.Added);
        Assert.Equal(501, record.Id);
    }

    [Fact]
    public void RadarrQueueRecord_DeserializesUnmatchedRecord_WithoutThrowing()
    {
        const string json = """
            {
                "id": 502,
                "title": "Some.Movie.2020",
                "status": "downloading",
                "downloadId": "def456",
                "movieId": null
            }
            """;

        var record = JsonSerializer.Deserialize<RadarrQueueRecord>(json);

        Assert.NotNull(record);
        Assert.Null(record!.MovieId);
        Assert.Null(record.GetMediaIdentity());
    }

    [Fact]
    public void ArrQueue_WithOneUnmatchedRecord_DeserializesEveryRecordOnThePage()
    {
        // Before SeriesId/EpisodeId were nullable, a single unmatched record like this
        // one would fail JsonSerializer.Deserialize for the *entire* array, and
        // ArrMonitoringService's catch-all would silently skip the whole monitoring pass
        // for the host — including the two real, matched, actionable records below.
        const string json = """
            {
                "page": 1,
                "pageSize": 5000,
                "totalRecords": 3,
                "records": [
                    { "id": 1, "title": "Matched.Show.S01E01", "seriesId": 10, "episodeId": 100 },
                    { "id": 2, "title": "Deleted.Show.S02E03", "seriesId": null, "episodeId": null },
                    { "id": 3, "title": "Matched.Show.S01E02", "seriesId": 10, "episodeId": 101 }
                ]
            }
            """;

        var queue = JsonSerializer.Deserialize<SonarrQueue>(json);

        Assert.NotNull(queue);
        Assert.Equal(3, queue!.Records.Count);
        Assert.Null(queue.Records[1].GetMediaIdentity());
        Assert.Equal("episode:100", queue.Records[0].GetMediaIdentity());
        Assert.Equal("episode:101", queue.Records[2].GetMediaIdentity());
    }

    [Fact]
    public void GetActionableOrphanedRecords_ExcludesRecordsWithAMediaMatch()
    {
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records =
            [
                new SonarrQueueRecord { Id = 1, EpisodeId = 100, Added = DateTime.UtcNow.AddHours(-1) },
            ],
        };

        var records = ArrMonitoringService.GetActionableOrphanedRecords(
            queue, TimeSpan.FromMinutes(30), new HashSet<int>());

        Assert.Empty(records);
    }

    [Fact]
    public void GetActionableOrphanedRecords_ExcludesRecordsStillWithinGracePeriod()
    {
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records =
            [
                new ArrQueueRecord { Id = 1, Added = DateTime.UtcNow.AddMinutes(-5) },
            ],
        };

        var records = ArrMonitoringService.GetActionableOrphanedRecords(
            queue, TimeSpan.FromMinutes(30), new HashSet<int>());

        Assert.Empty(records);
    }

    [Fact]
    public void GetActionableOrphanedRecords_IncludesRecordsPastGracePeriod()
    {
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records =
            [
                new ArrQueueRecord { Id = 1, Added = DateTime.UtcNow.AddMinutes(-31) },
            ],
        };

        var records = ArrMonitoringService.GetActionableOrphanedRecords(
            queue, TimeSpan.FromMinutes(30), new HashSet<int>());

        Assert.Equal([1], records.Select(x => x.Id));
    }

    [Fact]
    public void GetActionableOrphanedRecords_TreatsMissingAddedAsEligible()
    {
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records = [new ArrQueueRecord { Id = 1, Added = null }],
        };

        var records = ArrMonitoringService.GetActionableOrphanedRecords(
            queue, TimeSpan.FromMinutes(30), new HashSet<int>());

        Assert.Equal([1], records.Select(x => x.Id));
    }

    [Fact]
    public void GetActionableOrphanedRecords_ExcludesAlreadyHandledIds()
    {
        // A record can both match a QueueRule status message and have no media identity
        // (e.g. an unparseable release title). It should only be resolved once.
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records = [new ArrQueueRecord { Id = 1, Added = DateTime.UtcNow.AddHours(-1) }],
        };

        var records = ArrMonitoringService.GetActionableOrphanedRecords(
            queue, TimeSpan.FromMinutes(30), new HashSet<int> { 1 });

        Assert.Empty(records);
    }

    [Fact]
    public async Task HandleOrphanedQueueItem_DoNothing_ReturnsNullWithoutContactingArr()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var httpClient = new HttpClient(handler);
        var client = new TestArrClient(httpClient);
        var service = new ArrMonitoringService(
            new ConfigManager(),
            new ArrReplacementSearchBudget(),
            new ThrowingDbContextFactory(),
            new ThrowingBlobStore());
        var item = new ArrQueueRecord { Id = 7, Title = "Deleted.Show.S01E01" };
        var config = new ArrConfig { OrphanedQueueItemAction = ArrConfig.QueueAction.DoNothing };

        var resolution = await service.HandleOrphanedQueueItem(
            item, config, client, new Dictionary<Guid, string[]?>(), CancellationToken.None);

        Assert.Null(resolution);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task HandleOrphanedQueueItem_ConfiguredRemove_DeletesFromArrQueue()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var httpClient = new HttpClient(handler);
        var client = new TestArrClient(httpClient);
        var service = new ArrMonitoringService(
            new ConfigManager(),
            new ArrReplacementSearchBudget(),
            new ThrowingDbContextFactory(),
            new ThrowingBlobStore());
        var item = new ArrQueueRecord
        {
            Id = 7,
            Title = "Deleted.Show.S01E01",
            DownloadId = Guid.NewGuid().ToString("D"),
        };
        var config = new ArrConfig { OrphanedQueueItemAction = ArrConfig.QueueAction.Remove };

        var resolution = await service.HandleOrphanedQueueItem(
            item, config, client, new Dictionary<Guid, string[]?>(), CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Equal("Deleted.Show.S01E01", resolution!.Value.Title);
        Assert.Equal(ArrConfig.QueueAction.Remove, resolution.Value.Action);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("removeFromClient=true", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("blocklist=false", handler.RequestUri.Query, StringComparison.Ordinal);
        // No Arr media ID was available (the whole point of an orphaned record), so the
        // resolution falls back to the download ID, same as HandleStuckQueueItem does.
        Assert.Equal("download ID fallback", resolution.Value.IdentitySource);
    }

    [Fact]
    public async Task HandleOrphanedQueueItem_RejectedByArr_ReturnsNull()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);
        var client = new TestArrClient(httpClient);
        var service = new ArrMonitoringService(
            new ConfigManager(),
            new ArrReplacementSearchBudget(),
            new ThrowingDbContextFactory(),
            new ThrowingBlobStore());
        var item = new ArrQueueRecord { Id = 7, Title = "Deleted.Show.S01E01" };
        var config = new ArrConfig { OrphanedQueueItemAction = ArrConfig.QueueAction.Remove };

        var resolution = await service.HandleOrphanedQueueItem(
            item, config, client, new Dictionary<Guid, string[]?>(), CancellationToken.None);

        Assert.Null(resolution);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class TestArrClient(HttpClient client) : ArrClient("http://arr.test", "test-key")
    {
        protected override HttpClient Client => client;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    // Plain Remove never reads history or blobs (shouldCapture is only true for
    // RemoveAndBlocklist*), so these prove that path stays untouched.
    private sealed class ThrowingDbContextFactory : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() => throw new NotSupportedException();

        public Task<DavDatabaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingBlobStore : IBlobStore
    {
        public Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Stream? ReadBlob(Guid id) => throw new NotSupportedException();
        public Task<T?> ReadBlob<T>(Guid id) => throw new NotSupportedException();
        public bool Exists(Guid id) => throw new NotSupportedException();
        public bool Delete(Guid id) => throw new NotSupportedException();
    }
}
