using System.Net;
using System.Text;
using NzbWebDAV.Clients.Prowlarr;

namespace NzbWebDAV.Tests.Clients;

public class ProwlarrClientTests
{
    [Fact]
    public void BuildIndexerApiUrl_PreservesProwlarrUrlBase()
    {
        Assert.Equal(
            "http://prowlarr:9696/prowlarr/42/api",
            ProwlarrClient.BuildIndexerApiUrl("http://prowlarr:9696/prowlarr/", 42));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://prowlarr:9696")]
    [InlineData("https://user:secret@prowlarr:9696")]
    [InlineData("http://prowlarr:9696/?x=1")]
    public void NormalizeBaseUrl_RejectsUnsafeOrUnsupportedUrls(string url)
    {
        Assert.Throws<ArgumentException>(() => ProwlarrClient.NormalizeBaseUrl(url));
    }

    [Fact]
    public async Task GetIndexers_UsesApiKeyAndParsesSyncFields()
    {
        using var handler = new CaptureHandler(request =>
        {
            Assert.Equal("/prowlarr/api/v1/indexer", request.RequestUri?.AbsolutePath);
            Assert.Equal("prowlarr-secret", request.Headers.GetValues("X-Api-Key").Single());
            return JsonResponse("""
                [
                  {"id":7,"name":"Usenet","enable":true,"supportsSearch":true,"protocol":"usenet"},
                  {"id":8,"name":"Torrent","enable":true,"supportsSearch":true,"protocol":"torrent"}
                ]
                """);
        });
        using var http = new HttpClient(handler);
        var client = new ProwlarrClient(http, "http://prowlarr:9696/prowlarr/", "prowlarr-secret");

        var indexers = await client.GetIndexersAsync();

        Assert.Equal([7, 8], indexers.Select(x => x.Id));
        Assert.Equal("Usenet", indexers[0].Name);
        Assert.True(indexers[0].SupportsSearch);
        Assert.Equal("torrent", indexers[1].Protocol);
    }

    [Fact]
    public async Task GetIndexers_UnauthorizedDoesNotLeakApiKey()
    {
        using var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var client = new ProwlarrClient(http, "http://prowlarr:9696", "super-secret-key");

        var ex = await Assert.ThrowsAsync<ProwlarrClientException>(() => client.GetIndexersAsync());

        Assert.Equal("Prowlarr rejected the API key.", ex.Message);
        Assert.DoesNotContain("super-secret-key", ex.Message);
    }

    [Fact]
    public async Task GetIndexers_RejectsInvalidRemoteIndexerShape()
    {
        using var handler = new CaptureHandler(_ => JsonResponse("""[{"id":0,"name":""}]"""));
        using var http = new HttpClient(handler);
        var client = new ProwlarrClient(http, "http://prowlarr:9696", "key");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetIndexersAsync());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
