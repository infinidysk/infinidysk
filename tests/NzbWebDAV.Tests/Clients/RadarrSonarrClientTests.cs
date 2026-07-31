using System.Net;
using System.Text;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Tests.Clients;

public class RadarrSonarrClientTests
{
    [Fact]
    public async Task SonarrRepair_BlocklistsHistoryWithoutDirectSearch()
    {
        const string seriesPath = "/library/tv/Stale Show";
        const string filePath = seriesPath + "/Stale Show S01E01.mkv";
        var downloadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse("""[{"id":101,"path":"/library/tv/Stale Show"}]""")),
            ("GET /api/v3/episodefile?seriesId=101", JsonResponse($"[{{\"id\":201,\"seriesId\":101,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/episodefile/201", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("GET /api/v3/episodefile/201", new HttpResponseMessage(HttpStatusCode.NotFound)),
            ("GET /api/v3/series/101", new HttpResponseMessage(HttpStatusCode.NotFound)),
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
    public async Task RadarrRepair_BlocklistsHistoryWithoutDirectSearch()
    {
        const string filePath = "/library/movies/Stale Movie/Stale Movie.mkv";
        var downloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var httpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse($"[{{\"id\":101,\"movieFile\":{{\"id\":201,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={downloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}")),
            ("GET /api/v3/movie/101", new HttpResponseMessage(HttpStatusCode.NotFound)),
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
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":403}]}""")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":404}]}""")),
            ("DELETE /api/v3/episodefile/203", new HttpResponseMessage(HttpStatusCode.OK)),
            ("DELETE /api/v3/episodefile/204", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/403", JsonResponse("{}")),
            ("POST /api/v3/history/failed/404", JsonResponse("{}")),
            ("GET /api/v3/episodefile/203", new HttpResponseMessage(HttpStatusCode.NotFound)),
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
            ($"GET /api/v3/history?downloadId={downloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
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
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie", JsonResponse(
                $"[{{\"id\":102,\"movieFile\":{{\"id\":202,\"path\":\"{filePath}\"}}}}]")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":402}]}""")),
            ("DELETE /api/v3/moviefile/202", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/402", JsonResponse("{}"))));
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
            ($"GET /api/v3/history?downloadId={firstDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/episodefile/201", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/series", JsonResponse($"[{{\"id\":102,\"path\":\"{seriesPath}\"}}]")),
            ("GET /api/v3/episodefile?seriesId=102",
                JsonResponse($"[{{\"id\":202,\"seriesId\":102,\"path\":\"{filePath}\"}}]")),
            ($"GET /api/v3/history?downloadId={secondDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":402}]}""")),
            ("DELETE /api/v3/episodefile/202", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/402", JsonResponse("{}"))));
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
            ($"GET /api/v3/history?downloadId={seedDownloadId:D}&page=1&pageSize=1&sortKey=date&sortDirection=descending",
                JsonResponse("""{"records":[{"id":401}]}""")),
            ("DELETE /api/v3/moviefile/201", new HttpResponseMessage(HttpStatusCode.OK)),
            ("POST /api/v3/history/failed/401", JsonResponse("{}"))));
        var seedClient = new TestRadarrClient(host, seedHttpClient);
        Assert.Equal(
            ArrRepairOutcome.RemoveAndBlocklistSucceeded,
            await seedClient.RemoveAndBlocklist(filePath, seedDownloadId));

        using var firstHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie/101", new HttpResponseMessage(HttpStatusCode.NotFound)),
            ("GET /api/v3/movie", JsonResponse("[]"))));
        using var secondHttpClient = new HttpClient(CreateHandler(
            ("GET /api/v3/movie/101", new HttpResponseMessage(HttpStatusCode.NotFound)),
            ("GET /api/v3/movie", JsonResponse("[]"))));
        var firstClient = new TestRadarrClient(host, firstHttpClient);
        var secondClient = new TestRadarrClient(host, secondHttpClient);

        var outcomes = await Task.WhenAll(
            firstClient.RemoveAndBlocklist(filePath, Guid.NewGuid()),
            secondClient.RemoveAndBlocklist(filePath, Guid.NewGuid()));

        Assert.All(outcomes, outcome => Assert.Equal(ArrRepairOutcome.MediaItemNotFound, outcome));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

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

    private sealed class ResponseQueueHandler(
        Dictionary<string, Queue<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            if (!responses.TryGetValue(key, out var queuedResponses) || !queuedResponses.TryDequeue(out var response))
                throw new InvalidOperationException($"Unexpected request: {key}");

            return Task.FromResult(response);
        }
    }
}
