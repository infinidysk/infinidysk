using System.Net;
using NzbWebDAV.Clients;

namespace NzbWebDAV.Tests.Clients;

public class ArrHttpTransportTests
{
    [Theory]
    [InlineData("http://sonarr:8989")]
    [InlineData("https://Radarr-4K:7878/api/v3/queue")]
    [InlineData("http://prowlarr/prowlarr/api/v1/indexer")]
    public void SingleLabelHost_BypassesProxy_WithoutConsultingDefault(string url)
    {
        var inner = new RecordingProxy(new Uri("http://proxy.internal:3128"));
        var proxy = new ArrHttpTransport.SingleLabelBypassProxy(inner);
        var destination = new Uri(url);

        Assert.True(ArrHttpTransport.IsSingleLabelHost(destination));
        Assert.True(proxy.IsBypassed(destination));
        Assert.Null(proxy.GetProxy(destination));
        Assert.Empty(inner.Seen);
        Assert.Equal("direct (single-label hostname)", ArrHttpTransport.DescribeRouting(destination));
    }

    [Theory]
    [InlineData("http://sonarr.media.lan:8989")]
    [InlineData("http://sonarr.:8989")]
    [InlineData("http://192.168.1.20:8989")]
    [InlineData("http://[fd00::20]:8989")]
    [InlineData("http://[::ffff:192.168.1.20]:8989")]
    [InlineData("http://[fe80::1%25eth0]:8989")]
    public void OtherHosts_DelegateToDefaultProxyPolicy(string url)
    {
        var proxyUri = new Uri("http://proxy.internal:3128");
        var inner = new RecordingProxy(proxyUri);
        var proxy = new ArrHttpTransport.SingleLabelBypassProxy(inner);
        var destination = new Uri(url);

        Assert.False(ArrHttpTransport.IsSingleLabelHost(destination));
        Assert.False(proxy.IsBypassed(destination));
        Assert.Equal(proxyUri, proxy.GetProxy(destination));
        Assert.Equal([destination, destination], inner.Seen);
        Assert.Equal("default proxy policy", ArrHttpTransport.DescribeRouting(destination));
    }

    [Fact]
    public void DottedHost_HonorsDefaultBypassList()
    {
        var inner = new RecordingProxy(new Uri("http://proxy.internal:3128"), bypass: "sonarr.media.lan");
        var proxy = new ArrHttpTransport.SingleLabelBypassProxy(inner);

        Assert.True(proxy.IsBypassed(new Uri("http://sonarr.media.lan:8989")));
        Assert.False(proxy.IsBypassed(new Uri("http://radarr.media.lan:7878")));
    }

    [Fact]
    public void Credentials_ForwardToDefaultProxy()
    {
        var inner = new RecordingProxy(new Uri("http://proxy.internal:3128"))
        {
            Credentials = new NetworkCredential("user", "secret"),
        };
        var proxy = new ArrHttpTransport.SingleLabelBypassProxy(inner);
        Assert.Same(inner.Credentials, proxy.Credentials);

        var replacement = new NetworkCredential("other", "secret2");
        proxy.Credentials = replacement;
        Assert.Same(replacement, inner.Credentials);
    }

    [Fact]
    public void CreateHandler_UsesBypassProxy_AndBoundedConnectionLifetime()
    {
        var inner = new RecordingProxy(new Uri("http://proxy.internal:3128"));
        using var handler = ArrHttpTransport.CreateHandler(inner);

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<ArrHttpTransport.SingleLabelBypassProxy>(handler.Proxy);
        Assert.Same(inner, proxy.Inner);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void CreateHandler_Default_WrapsProcessDefaultProxy()
    {
        using var handler = ArrHttpTransport.CreateHandler();
        var proxy = Assert.IsType<ArrHttpTransport.SingleLabelBypassProxy>(handler.Proxy);
        Assert.Same(HttpClient.DefaultProxy, proxy.Inner);
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

    private sealed class RecordingProxy(Uri proxyUri, string? bypass = null) : IWebProxy
    {
        public List<Uri> Seen { get; } = [];
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            Seen.Add(destination);
            return proxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            Seen.Add(host);
            return bypass is not null && string.Equals(host.Host, bypass, StringComparison.OrdinalIgnoreCase);
        }
    }
}
