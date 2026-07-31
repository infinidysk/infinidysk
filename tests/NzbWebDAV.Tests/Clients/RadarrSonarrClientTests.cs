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

    private sealed class TestSonarrClient(HttpClient client) : SonarrClient("http://arr.test", "test-key")
    {
        protected override HttpClient Client => client;
    }

    private sealed class TestRadarrClient(HttpClient client) : RadarrClient("http://arr.test", "test-key")
    {
        protected override HttpClient Client => client;
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
