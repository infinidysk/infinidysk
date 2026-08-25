using Microsoft.AspNetCore.Http;
using NzbWebDAV.Middlewares;

namespace NzbWebDAV.Tests.Middlewares;

public class WebDavObservabilityMiddlewareTests : IDisposable
{
    public WebDavObservabilityMiddlewareTests()
    {
        WebDavObservabilityMiddleware.Reset();
    }

    public void Dispose()
    {
        WebDavObservabilityMiddleware.Reset();
        WebDavObservabilityMiddleware.SlowThresholdOverride = null;
    }

    [Fact]
    public async Task NonWebDavPath_IsIgnored()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/queue";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Empty(WebDavObservabilityMiddleware.Snapshot());
    }

    [Fact]
    public async Task WebDavPath_IncrementTotal()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/content/tv/show.mkv";
        context.Response.StatusCode = 200;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.False(counters.ContainsKey("slow"));
        Assert.False(counters.ContainsKey("failed"));
    }

    [Fact]
    public async Task FailedWebDavRequest_IncrementFailed()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/content/tv/show.mkv";
        context.Response.StatusCode = 503;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.Equal(1, counters["failed"]);
    }

    [Fact]
    public async Task AbortedWebDavRequest_IncrementAborted()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/content/tv/show.mkv";
        context.RequestAborted = new CancellationToken(canceled: true);
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.Equal(1, counters["aborted"]);
    }

    [Fact]
    public async Task ViewPath_IsCounted()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/view/.ids/abc/123";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, WebDavObservabilityMiddleware.Snapshot()["total"]);
    }

    [Theory]
    [InlineData("/content")]
    [InlineData("/content/tv/show.mkv")]
    [InlineData("/Content/TV/SHOW.MKV")]
    public async Task RootAndChildPaths_MatchCaseInsensitively(string requestPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = requestPath;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, WebDavObservabilityMiddleware.Snapshot()["total"]);
    }

    [Theory]
    [InlineData("/content-preview/page")]
    [InlineData("/contentious")]
    [InlineData("/viewer")]
    public async Task SimilarPrefixes_AreNotCounted(string requestPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = requestPath;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Empty(WebDavObservabilityMiddleware.Snapshot());
    }

    [Fact]
    public async Task SlowFailedRequest_IncrementsBothCounters()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        var context = new DefaultHttpContext();
        context.Request.Path = "/content/tv/show.mkv";
        context.Response.StatusCode = 503;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["failed"]);
        Assert.Equal(1, counters["slow"]);
    }

    [Fact]
    public async Task FastRequest_WithZeroThreshold_IsNotSlow()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.FromHours(1);
        var context = new DefaultHttpContext();
        context.Request.Path = "/content/tv/show.mkv";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.False(counters.ContainsKey("slow"));
    }
}
