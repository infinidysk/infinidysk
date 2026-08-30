using System.Net;
using System.Text;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Clients;

public class RadarrSonarrClientTests
{
    [Fact]
    public async Task SonarrRepair_RequestsEpisodeSearchAfterBlocklist()
    {
        const string seriesPath = "/library/tv/Stale Show";
        const string filePath = seriesPath + "/Stale Show S01E01.mkv";
        var downloadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse("""[{"id":101,"path":"/library/tv/Stale Show"}]""")),
            ("GET /api/v3/episodefile?seriesId=101", JsonResponse($"[{{\"id\":201,\"seriesId\":101,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("GET /api/v3/episode?episodeFileId=201", JsonResponse("""[{"id":301,"seriesId":101}]""")),
            ("DELETE /api/v3/episodefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}")),
            ("GET /api/v3/episodefile/201", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/series/101", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/series", JsonResponse("[]"))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
        Assert.Equal(
            ArrRepairOutcome.MediaItemNotFound,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task SonarrRepair_SeasonPackEpisodeSearchIncludesAllLinkedEpisodes()
    {
        const string seriesPath = "/library/tv/Season Pack Show";
        const string filePath = seriesPath + "/Season Pack Show S01.mkv";
        var downloadId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":105,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=105",
                JsonResponse($"[{{\"id\":205,\"seriesId\":105,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":405}]}""")),
            ("GET /api/v3/episode?episodeFileId=205",
                JsonResponse("""[{"id":305,"seriesId":105},{"id":306,"seriesId":105}]""")),
            ("DELETE /api/v3/episodefile/205", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/405", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task RadarrRepair_RequestsMoviesSearchAfterBlocklist()
    {
        const string filePath = "/library/movies/Stale Movie/Stale Movie.mkv";
        var downloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse($"[{{\"id\":101,\"movieFile\":{{\"id\":201,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}")),
            ("GET /api/v3/movie/101", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/movie", JsonResponse("[]"))));
        var client = new TestRadarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
        Assert.Equal(
            ArrRepairOutcome.MediaItemNotFound,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task SonarrRepair_TwoReplacementDownloadsAreEachBlocklistedOnce()
    {
        const string seriesPath = "/library/tv/Loop Show";
        const string filePath = seriesPath + "/Loop Show S01E01.mkv";
        var firstDownloadId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var secondDownloadId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":103,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=103",
                JsonResponse($"[{{\"id\":203,\"seriesId\":103,\"path\":\"{filePath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=103",
                JsonResponse($"[{{\"id\":204,\"seriesId\":103,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":403}]}""")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":404}]}""")),
            ("GET /api/v3/episode?episodeFileId=203", JsonResponse("""[{"id":303,"seriesId":103}]""")),
            ("GET /api/v3/episode?episodeFileId=204", JsonResponse("""[{"id":304,"seriesId":103}]""")),
            ("DELETE /api/v3/episodefile/203", Status(HttpStatusCode.OK)),
            ("DELETE /api/v3/episodefile/204", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/403", JsonResponse("{}")),
            ("POST /api/v3/history/failed/404", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}")),
            ("GET /api/v3/episodefile/203", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/series/103", JsonResponse($"{{\"id\":103,\"path\":\"{seriesPath}\"}}"))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, firstDownloadId));
        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, secondDownloadId));
    }

    [Fact]
    public async Task SonarrRepair_MissingHistoryDoesNotDeleteMedia()
    {
        const string seriesPath = "/library/tv/No History Show";
        const string filePath = seriesPath + "/No History Show S01E01.mkv";
        var downloadId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":102,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=102",
                JsonResponse($"[{{\"id\":202,\"seriesId\":102,\"path\":\"{filePath}\"}}]")),
            ("GET /api/v3/episode?episodeFileId=202", JsonResponse("""[{"id":302,"seriesId":102}]""")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[]}"""))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.DownloadHistoryNotFound,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task RadarrRepair_CacheIsIsolatedByHost()
    {
        const string filePath = "/library/movies/Shared Path/Shared Path.mkv";
        const string firstHost = "http://radarr-cache-one.test";
        const string secondHost = "http://radarr-cache-two.test";
        var firstDownloadId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondDownloadId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using var firstHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse(
                $"[{{\"id\":101,\"movieFile\":{{\"id\":201,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse(
                $"[{{\"id\":102,\"movieFile\":{{\"id\":202,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":402}]}""")),
            ("DELETE /api/v3/moviefile/202", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/402", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var firstClient = new TestRadarrClient(firstHost, firstHttpClient);
        var secondClient = new TestRadarrClient(secondHost, secondHttpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await firstClient.RemoveAndBlocklist(filePath, firstDownloadId));
        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await secondClient.RemoveAndBlocklist(filePath, secondDownloadId));
    }

    [Fact]
    public async Task SonarrRepair_CacheIsIsolatedByHost()
    {
        const string seriesPath = "/library/tv/Shared Show";
        const string filePath = seriesPath + "/Shared Show S01E01.mkv";
        const string firstHost = "http://sonarr-cache-one.test";
        const string secondHost = "http://sonarr-cache-two.test";
        var firstDownloadId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var secondDownloadId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using var firstHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":101,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=101",
                JsonResponse($"[{{\"id\":201,\"seriesId\":101,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("GET /api/v3/episode?episodeFileId=201", JsonResponse("""[{"id":301,"seriesId":101}]""")),
            ("DELETE /api/v3/episodefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":102,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=102",
                JsonResponse($"[{{\"id\":202,\"seriesId\":102,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":402}]}""")),
            ("GET /api/v3/episode?episodeFileId=202", JsonResponse("""[{"id":302,"seriesId":102}]""")),
            ("DELETE /api/v3/episodefile/202", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/402", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var firstClient = new TestSonarrClient(firstHost, firstHttpClient);
        var secondClient = new TestSonarrClient(secondHost, secondHttpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await firstClient.RemoveAndBlocklist(filePath, firstDownloadId));
        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await secondClient.RemoveAndBlocklist(filePath, secondDownloadId));
    }

    [Fact]
    public async Task RadarrRepair_ConcurrentStaleCacheInvalidationIsSafe()
    {
        const string host = "http://radarr-concurrent-cache.test";
        const string filePath = "/library/movies/Concurrent Cache/Concurrent Cache.mkv";
        var seedDownloadId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        using var seedHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse(
                $"[{{\"id\":101,\"movieFile\":{{\"id\":201,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={seedDownloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var seedClient = new TestRadarrClient(host, seedHttpClient);
        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await seedClient.RemoveAndBlocklist(filePath, seedDownloadId));

        using var firstHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie/101", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/movie", JsonResponse("[]"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie/101", Status(HttpStatusCode.NotFound)),
            ("GET /api/v3/movie", JsonResponse("[]"))));
        var firstClient = new TestRadarrClient(host, firstHttpClient);
        var secondClient = new TestRadarrClient(host, secondHttpClient);

        var outcomes = await Task.WhenAll(
            firstClient.RemoveAndBlocklist(filePath, Guid.NewGuid()),
            secondClient.RemoveAndBlocklist(filePath, Guid.NewGuid()));

        Assert.All(outcomes, outcome => Assert.Equal(ArrRepairOutcome.MediaItemNotFound, outcome));
    }

    [Fact]
    public async Task SonarrRepair_AcceptsNonOk2xxDeleteResponse()
    {
        const string seriesPath = "/library/tv/No Content Show";
        const string filePath = seriesPath + "/No Content Show S01E01.mkv";
        var downloadId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":106,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=106",
                JsonResponse($"[{{\"id\":206,\"seriesId\":106,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":406}]}""")),
            ("GET /api/v3/episode?episodeFileId=206", JsonResponse("""[{"id":306,"seriesId":106}]""")),
            ("DELETE /api/v3/episodefile/206", Status(HttpStatusCode.NoContent)),
            ("POST /api/v3/history/failed/406", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task RadarrRepair_RetriesTransientSearchCommandFailures()
    {
        const string filePath = "/library/movies/Retry Movie/Retry Movie.mkv";
        var downloadId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse($"[{{\"id\":107,\"movieFile\":{{\"id\":207,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":407}]}""")),
            ("DELETE /api/v3/moviefile/207", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/407", JsonResponse("{}")),
            ("POST /api/v3/command", Status(HttpStatusCode.ServiceUnavailable)),
            ("POST /api/v3/command", JsonResponse("{}"))));
        var client = new TestRadarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task SonarrRepair_SearchCommandFailureStillReturnsSuccess()
    {
        const string seriesPath = "/library/tv/Search Fail Show";
        const string filePath = seriesPath + "/Search Fail Show S01E01.mkv";
        var downloadId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":108,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=108",
                JsonResponse($"[{{\"id\":208,\"seriesId\":108,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":408}]}""")),
            ("GET /api/v3/episode?episodeFileId=208", JsonResponse("""[{"id":308,"seriesId":108}]""")),
            ("DELETE /api/v3/episodefile/208", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/408", JsonResponse("{}")),
            ("POST /api/v3/command", Status(HttpStatusCode.ServiceUnavailable)),
            ("POST /api/v3/command", Status(HttpStatusCode.ServiceUnavailable)),
            ("POST /api/v3/command", Status(HttpStatusCode.ServiceUnavailable))));
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));
    }

    [Fact]
    public async Task RadarrMissingPayloadCleanup_DeletesAndSearchesWithoutBlocklisting()
    {
        const string filePath = "/library/movies/Lost Blob/Lost Blob.mkv";
        var handler = CreateHandler(
            ("GET /api/v3/movie",
                JsonResponse($"[{{\"id\":111,\"movieFile\":{{\"id\":211,\"path\":\"{filePath}\"}}}}]")),
            ("DELETE /api/v3/moviefile/211", Status(HttpStatusCode.NoContent)),
            ("POST /api/v3/command", JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var client = new TestRadarrClient("http://radarr-missing-payload.test", httpClient);

        var match = await client.FindMediaFileAsync(filePath);
        Assert.NotNull(match);
        IReadOnlyList<string>? mediaKeys = null;
        var outcome = await client.RemoveMissingPayloadAndSearchAsync(
            match!,
            keys =>
            {
                mediaKeys = keys;
                return true;
            });

        Assert.Equal(ArrMissingPayloadCleanupOutcome.RemovedSearchRequested, outcome);
        Assert.Equal(["movie:111"], mediaKeys);
        Assert.Contains("DELETE /api/v3/moviefile/211", handler.Requests);
        Assert.Contains("POST /api/v3/command", handler.Requests);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.StartsWith("POST /api/v3/history/failed/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SonarrMissingPayloadCleanup_WithholdsSearchWithoutBlocklisting()
    {
        const string seriesPath = "/library/tv/Lost Blob Show";
        const string filePath = seriesPath + "/Lost Blob Show S01E01-E02.mkv";
        var handler = CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":112,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=112",
                JsonResponse($"[{{\"id\":212,\"seriesId\":112,\"path\":\"{filePath}\"}}]")),
            ("GET /api/v3/episode?episodeFileId=212",
                JsonResponse("""[{"id":312,"seriesId":112},{"id":313,"seriesId":112}]""")),
            ("DELETE /api/v3/episodefile/212", Status(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler);
        var client = new TestSonarrClient("http://sonarr-missing-payload.test", httpClient);

        var match = await client.FindMediaFileAsync(filePath);
        Assert.NotNull(match);
        IReadOnlyList<string>? mediaKeys = null;
        var outcome = await client.RemoveMissingPayloadAndSearchAsync(
            match!,
            keys =>
            {
                mediaKeys = keys;
                return false;
            });

        Assert.Equal(ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld, outcome);
        Assert.Equal(["episode:312", "episode:313"], mediaKeys);
        Assert.Contains("DELETE /api/v3/episodefile/212", handler.Requests);
        Assert.DoesNotContain(
            handler.Requests,
            request => request.StartsWith("POST /api/v3/history/failed/", StringComparison.Ordinal));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.StartsWith("POST /api/v3/command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetQueueCountAsync_HonorsCancellationToken()
    {
        using var httpClient = new HttpClient(new HangUntilCancelledHandler());
        var client = new TestArrClient(httpClient);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetQueueCountAsync(cts.Token));
    }

    [Fact]
    public async Task RadarrRepair_WithholdsSearchWhenBudgetDenied()
    {
        const string filePath = "/library/movies/Budget Movie/Budget Movie.mkv";
        var downloadId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var handler = CreateHandler(
            ("GET /api/v3/movie", JsonResponse($"[{{\"id\":101,\"movieFile\":{{\"id\":201,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var client = new TestRadarrClient("http://radarr-budget.test", httpClient);

        IReadOnlyList<string>? mediaIdentities = null;
        var outcome = await client.RemoveAndBlocklist(
            filePath,
            downloadId,
            identities =>
            {
                mediaIdentities = identities;
                return false;
            });

        Assert.Equal(ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld, outcome);
        Assert.Equal(["movie:101"], mediaIdentities);
        AssertRemoveAndBlocklistWithoutSearch(handler);
    }

    [Fact]
    public async Task SonarrRepair_WithholdsSearchWhenBudgetDenied()
    {
        const string seriesPath = "/library/tv/Budget Show";
        const string filePath = seriesPath + "/Budget Show S01E01.mkv";
        var downloadId = Guid.Parse("34343434-3434-3434-3434-343434343434");
        var handler = CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":101,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=101",
                JsonResponse($"[{{\"id\":201,\"seriesId\":101,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("GET /api/v3/episode?episodeFileId=201", JsonResponse("""[{"id":301,"seriesId":101}]""")),
            ("DELETE /api/v3/episodefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var client = new TestSonarrClient("http://sonarr-budget.test", httpClient);

        IReadOnlyList<string>? mediaIdentities = null;
        var outcome = await client.RemoveAndBlocklist(
            filePath,
            downloadId,
            identities =>
            {
                mediaIdentities = identities;
                return false;
            });

        Assert.Equal(ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld, outcome);
        Assert.Equal(["episode:301"], mediaIdentities);
        AssertRemoveAndBlocklistWithoutSearch(handler);
    }

    [Fact]
    public async Task SonarrRepair_WithholdsSearchWhenAnySeasonPackEpisodeIsExhausted()
    {
        const string seriesPath = "/library/tv/Season Pack Show";
        const string filePath = seriesPath + "/Season Pack Show S01E01-E02.mkv";
        var downloadId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        var handler = CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":101,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=101",
                JsonResponse($"[{{\"id\":201,\"seriesId\":101,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("GET /api/v3/episode?episodeFileId=201",
                JsonResponse("""[{"id":301,"seriesId":101},{"id":302,"seriesId":101}]""")),
            ("DELETE /api/v3/episodefile/201", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var client = new TestSonarrClient("http://sonarr-pack.test", httpClient);
        var budget = new ArrReplacementSearchBudget();
        Assert.True(budget.TryReserve("episode:302", limit: 1, TimeSpan.FromMinutes(30)));

        IReadOnlyList<string>? mediaIdentities = null;
        var outcome = await client.RemoveAndBlocklist(
            filePath,
            downloadId,
            identities =>
            {
                mediaIdentities = identities;
                return budget.TryReserveAll(identities, limit: 1, TimeSpan.FromMinutes(30));
            });

        Assert.Equal(ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld, outcome);
        Assert.Equal(["episode:301", "episode:302"], mediaIdentities);
        AssertRemoveAndBlocklistWithoutSearch(handler);
        Assert.True(budget.TryReserve("episode:301", limit: 1, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task RadarrQueue_PreservesMovieIdForReplacementSearchBudget()
    {
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/queue?protocol=usenet&pageSize=5000",
                JsonResponse("""{"records":[{"id":1,"movieId":42}]}"""))));
        var client = new TestRadarrClient(httpClient);

        var record = Assert.Single((await client.GetQueueAsync()).Records);

        Assert.Equal("movie:42", record.GetMediaIdentity());
    }

    [Fact]
    public async Task SonarrQueue_PreservesEpisodeIdForReplacementSearchBudget()
    {
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/queue?protocol=usenet&pageSize=5000",
                JsonResponse("""{"records":[{"id":1,"seriesId":42,"episodeId":99}]}"""))));
        var client = new TestSonarrClient(httpClient);

        var record = Assert.Single((await client.GetQueueAsync()).Records);

        Assert.Equal("episode:99", record.GetMediaIdentity());
    }

    [Fact]
    public async Task SonarrImportHistory_IsScopedToEpisodeAndImportEventType()
    {
        var match = new ArrMediaFileMatch(ArrMediaKind.Episode, FileId: 201, MediaIds: [301]);
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/history?episodeId=301&eventType=3&page=1&pageSize=50&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[],"totalRecords":0}"""))));
        var client = new TestSonarrClient(httpClient);

        var history = await client.GetMediaImportHistoryAsync(match, page: 1, pageSize: 50);

        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task RadarrImportHistory_IsScopedToMovieAndImportEventType()
    {
        var match = new ArrMediaFileMatch(ArrMediaKind.Movie, FileId: 201, MediaIds: [101]);
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/history?movieId=101&eventType=3&page=1&pageSize=50&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[],"totalRecords":0}"""))));
        var client = new TestRadarrClient(httpClient);

        var history = await client.GetMediaImportHistoryAsync(match, page: 1, pageSize: 50);

        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task CollectMediaImportHistoryAsync_HonorsCancellationToken()
    {
        using var httpClient = new HttpClient(new HangUntilCancelledHandler());
        var client = new TestRadarrClient(httpClient);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var match = new ArrMediaFileMatch(ArrMediaKind.Movie, FileId: 1, MediaIds: [1]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CollectMediaImportHistoryAsync(match, cts.Token));
    }

    [Fact]
    public async Task CollectMediaImportHistoryAsync_ShortPageWithLargerTotal_IsNotExhausted()
    {
        var match = new ArrMediaFileMatch(ArrMediaKind.Movie, FileId: 201, MediaIds: [101]);
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/history?movieId=101&eventType=3&page=1&pageSize=50&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"downloadId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}],"totalRecords":100}"""))));
        var client = new TestRadarrClient(httpClient);

        var collected = await client.CollectMediaImportHistoryAsync(match);

        Assert.False(collected.Exhausted);
        Assert.Single(collected.Records);
    }

    [Fact]
    public async Task CollectMediaImportHistoryAsync_ShortPageWithoutTotal_IsExhausted()
    {
        var match = new ArrMediaFileMatch(ArrMediaKind.Movie, FileId: 201, MediaIds: [101]);
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/history?movieId=101&eventType=3&page=1&pageSize=50&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"downloadId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}]}"""))));
        var client = new TestRadarrClient(httpClient);

        var collected = await client.CollectMediaImportHistoryAsync(match);

        Assert.True(collected.Exhausted);
        Assert.Single(collected.Records);
    }

    [Fact]
    public async Task CollectMediaImportHistoryAsync_ShortPageMatchingTotal_IsExhausted()
    {
        var match = new ArrMediaFileMatch(ArrMediaKind.Movie, FileId: 201, MediaIds: [101]);
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/history?movieId=101&eventType=3&page=1&pageSize=50&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"downloadId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}],"totalRecords":1}"""))));
        var client = new TestRadarrClient(httpClient);

        var collected = await client.CollectMediaImportHistoryAsync(match);

        Assert.True(collected.Exhausted);
        Assert.Single(collected.Records);
    }

    [Fact]
    public async Task SonarrRepair_LooksUpMediaBeforeGrabbedHistory()
    {
        const string seriesPath = "/library/tv/Order Show";
        const string filePath = seriesPath + "/Order Show S01E01.mkv";
        var downloadId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var handler = CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":109,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=109",
                JsonResponse($"[{{\"id\":209,\"seriesId\":109,\"path\":\"{filePath}\"}}]")),
            ("GET /api/v3/episode?episodeFileId=209", JsonResponse("""[{"id":309,"seriesId":109}]""")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":409}]}""")),
            ("DELETE /api/v3/episodefile/209", Status(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/409", JsonResponse("{}")),
            ("POST /api/v3/command", JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var client = new TestSonarrClient(httpClient);

        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await client.RemoveAndBlocklist(filePath, downloadId));

        var mediaIndex = handler.Requests.FindIndex(request =>
            request.StartsWith("GET /api/v3/episode?episodeFileId=209", StringComparison.Ordinal));
        var historyIndex = handler.Requests.FindIndex(request =>
            request.Contains("eventType=1", StringComparison.Ordinal));
        var deleteIndex = handler.Requests.FindIndex(request =>
            request.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.InRange(mediaIndex, 0, historyIndex - 1);
        Assert.InRange(historyIndex, mediaIndex + 1, deleteIndex - 1);
    }

    [Fact]
    public void SharedHttpClient_HasExplicitTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ArrClient.RequestTimeout);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static void AssertRemoveAndBlocklistWithoutSearch(ResponseQueueHandler handler)
    {
        Assert.Equal(1, handler.Requests.Count(request =>
            request.StartsWith("DELETE ", StringComparison.Ordinal)));
        Assert.Equal(1, handler.Requests.Count(request =>
            request.StartsWith("POST /api/v3/history/failed/", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request =>
            request.StartsWith("POST /api/v3/command", StringComparison.Ordinal));
    }

    private static ResponseQueueHandler CreateHandler(
        params (string request, HttpResponseMessage response)[] responses) =>
        new(responses
            .GroupBy(x => x.request)
            .ToDictionary(
                x => x.Key,
                x => new Queue<HttpResponseMessage>(x.Select(y => y.response))));

    private sealed class TestSonarrClient : SonarrClient
    {
        private readonly HttpClient _client;

        public TestSonarrClient(HttpClient client)
            : this("http://arr.test", client)
        {
        }

        public TestSonarrClient(string host, HttpClient client)
            : base(host, "test-key")
        {
            _client = client;
        }

        protected override HttpClient Client => _client;
    }

    private sealed class TestRadarrClient : RadarrClient
    {
        private readonly HttpClient _client;

        public TestRadarrClient(HttpClient client)
            : this("http://arr.test", client)
        {
        }

        public TestRadarrClient(string host, HttpClient client)
            : base(host, "test-key")
        {
            _client = client;
        }

        protected override HttpClient Client => _client;
    }

    private sealed class TestArrClient : ArrClient
    {
        private readonly HttpClient _client;

        public TestArrClient(HttpClient client)
            : base("http://arr.test", "test-key")
        {
            _client = client;
        }

        protected override HttpClient Client => _client;
    }

    private sealed class ResponseQueueHandler(
        Dictionary<string, Queue<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            Requests.Add(key);
            if (!responses.TryGetValue(key, out var queuedResponses) || !queuedResponses.TryDequeue(out var response))
                throw new InvalidOperationException($"Unexpected request: {key}");

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var queue in responses.Values)
                {
                    while (queue.TryDequeue(out var leftover))
                        leftover.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class HangUntilCancelledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected cancellation before a response.");
        }
    }
}
