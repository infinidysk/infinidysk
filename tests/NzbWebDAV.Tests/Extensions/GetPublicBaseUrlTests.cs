using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class GetPublicBaseUrlTests
{
    [Fact]
    public void PrefersConfiguredBaseUrlOverRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("evil.example");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "evil.example";

        var result = context.GetPublicBaseUrl("https://nzbdav.example");

        Assert.Equal("https://nzbdav.example", result);
    }

    [Fact]
    public void UsesRequestSchemeAndHostWhenBaseUrlIsDefault()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nzbdav.example");
        // Spoofed headers must be ignored — only Scheme/Host (post ForwardedHeaders) count.
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        context.Request.Headers["X-Forwarded-Host"] = "evil.example";

        var result = context.GetPublicBaseUrl("http://localhost:3000");

        Assert.Equal("https://nzbdav.example", result);
    }

    [Fact]
    public void UsesRequestSchemeAndHostWhenBaseUrlBlank()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:8080");

        var result = context.GetPublicBaseUrl("");

        Assert.Equal("http://localhost:8080", result);
    }

    [Fact]
    public void AddsFrontendUrlBaseToCopiedPublicUrls()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nzbdav.example");
        context.Request.Headers["X-Forwarded-Prefix"] = "/infinidysk";

        var result = context.GetPublicBaseUrl("http://localhost:3000");

        Assert.Equal("https://nzbdav.example/infinidysk", result);
    }

    [Fact]
    public void DoesNotDuplicateConfiguredUrlBase()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Prefix"] = "/infinidysk";

        var result = context.GetPublicBaseUrl("https://nzbdav.example/infinidysk");

        Assert.Equal("https://nzbdav.example/infinidysk", result);
    }

    [Fact]
    public void IgnoresUnsafeForwardedPrefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nzbdav.example");
        context.Request.Headers["X-Forwarded-Prefix"] = "https://evil.example";

        var result = context.GetPublicBaseUrl("http://localhost:3000");

        Assert.Equal("https://nzbdav.example", result);
    }
}
