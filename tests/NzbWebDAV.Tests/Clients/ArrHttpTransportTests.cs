using System.Net;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Tests.Clients;

public class ArrHttpTransportTests
{
    [Theory]
    [InlineData("http://sonarr:8989")]
    [InlineData("https://Radarr-4K:7878/api/v3/queue")]
    [InlineData("http://prowlarr/prowlarr/api/v1/indexer")]
    public async Task SingleLabelHost_BypassesProxy_WithoutConsultingDefault(string url)
    {
        var direct = new RecordingHandler();
        var defaultHandler = new RecordingHandler();
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(direct, defaultHandler));
        var destination = new Uri(url);

        Assert.True(ArrHttpTransport.IsSingleLabelHost(destination));
        using var response = await client.GetAsync(destination);
        Assert.Equal([destination], direct.Seen);
        Assert.Empty(defaultHandler.Seen);
        Assert.Equal("direct (single-label hostname)", ArrHttpTransport.DescribeRouting(destination));
    }

    [Theory]
    [InlineData("http://sonarr.media.lan:8989")]
    [InlineData("http://sonarr.:8989")]
    [InlineData("http://192.168.1.20:8989")]
    [InlineData("http://[fd00::20]:8989")]
    [InlineData("http://[::ffff:192.168.1.20]:8989")]
    [InlineData("http://[fe80::1%25eth0]:8989")]
    public async Task OtherHosts_DelegateToDefaultProxyPolicy(string url)
    {
        var direct = new RecordingHandler();
        var defaultHandler = new RecordingHandler();
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(direct, defaultHandler));
        var destination = new Uri(url);

        Assert.False(ArrHttpTransport.IsSingleLabelHost(destination));
        using var response = await client.GetAsync(destination);
        Assert.Equal([destination], defaultHandler.Seen);
        Assert.Empty(direct.Seen);
        Assert.Equal("default proxy policy", ArrHttpTransport.DescribeRouting(destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateSocketHandler_PreservesNativeProxyPolicy_AndBoundedConnectionLifetime(bool useProxy)
    {
        using var handler = ArrHttpTransport.CreateSocketHandler(useProxy);

        Assert.Equal(useProxy, handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void CreateHandler_UsesRoutingHandler()
    {
        using var handler = ArrHttpTransport.CreateHandler();
        Assert.IsType<ArrHttpTransport.RoutingHandler>(handler);
    }

    [Theory]
    [InlineData("http://sonarr:8989", "http://sonarr.media.lan:8989")]
    [InlineData("http://sonarr.media.lan:8989", "http://sonarr:8989")]
    public async Task Redirect_ReevaluatesProxyPolicy(string source, string target)
    {
        var destination = new Uri(target);
        var initial = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = destination },
        });
        var final = new RecordingHandler();
        var sourceIsDirect = ArrHttpTransport.IsSingleLabelHost(new Uri(source));
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(
            sourceIsDirect ? initial : final, sourceIsDirect ? final : initial));

        using var response = await client.GetAsync(source);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new Uri(source)], initial.Seen);
        Assert.Equal([destination], final.Seen);
    }

    [Theory]
    [InlineData("https://sonarr:8989", "http://sonarr.media.lan:8989")]
    [InlineData("https://sonarr.media.lan:8989", "http://sonarr:8989")]
    public async Task Redirect_DoesNotDowngradeHttps(string source, string target)
    {
        var initial = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(target) },
        });
        var final = new RecordingHandler();
        var sourceIsDirect = ArrHttpTransport.IsSingleLabelHost(new Uri(source));
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(
            sourceIsDirect ? initial : final, sourceIsDirect ? final : initial));

        using var response = await client.GetAsync(source);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Empty(final.Seen);
    }

    [Fact]
    public void TimeoutException_DescribesOperationBudgetAndRouting_WithoutQueryOrUserInfo()
    {
        var single = new ArrRequestTimeoutException(
            "Sonarr queue status", "http://user:pw@sonarr:8989/?apikey=abc", TimeSpan.FromSeconds(10), null);
        Assert.Equal(
            "Sonarr queue status request to http://sonarr:8989 timed out after 10 seconds; routing: direct (single-label hostname).",
            single.Message);
        Assert.DoesNotContain("apikey", single.Message);
        Assert.DoesNotContain("user:pw", single.Message);
        Assert.IsAssignableFrom<TaskCanceledException>(single);

        var dotted = new ArrRequestTimeoutException(
            "Prowlarr indexer list", "https://prowlarr.media.lan/prowlarr", TimeSpan.FromSeconds(15), null);
        Assert.Equal(
            "Prowlarr indexer list request to https://prowlarr.media.lan timed out after 15 seconds; routing: default proxy policy.",
            dotted.Message);

        var fractional = new ArrRequestTimeoutException("Op", "not a url", TimeSpan.FromMilliseconds(250), null);
        Assert.Equal("Op request to the configured instance timed out after 0.25 seconds; routing: unknown.", fractional.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "GET")]
    [InlineData(HttpStatusCode.SeeOther, "GET")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST")]
    [InlineData(HttpStatusCode.PermanentRedirect, "POST")]
    public async Task Redirect_PreservesMethodRules_AndClearsAuthorization(HttpStatusCode status, string expectedMethod)
    {
        var direct = new RecordingHandler(() => new HttpResponseMessage(status)
        {
            Headers = { Location = new Uri("http://sonarr.media.lan/api") },
        });
        var defaultHandler = new RecordingHandler();
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(direct, defaultHandler));
        using var content = new StringContent("payload");
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://sonarr/api") { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMethod, request.Method.Method);
        Assert.Null(request.Headers.Authorization);
        if (expectedMethod == "GET") Assert.Null(request.Content);
        else Assert.Same(content, request.Content);
        Assert.Single(defaultHandler.Seen);
    }

    [Fact]
    public async Task Redirect_StopsAtDefaultLimit()
    {
        var direct = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("/api", UriKind.Relative) },
        });
        var defaultHandler = new RecordingHandler();
        using var client = new HttpClient(new ArrHttpTransport.RoutingHandler(direct, defaultHandler));

        using var response = await client.GetAsync("http://sonarr/api");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(51, direct.Seen.Count);
        Assert.Empty(defaultHandler.Seen);
    }

    private sealed class RecordingHandler(Func<HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
