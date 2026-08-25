using Microsoft.AspNetCore.Http;
using NzbWebDAV.Middlewares;

namespace NzbWebDAV.Tests.Middlewares;

public class WebDavObservabilityMiddlewareTests : IDisposable
{
    public WebDavObservabilityMiddlewareTests()
    {
        WebDavObservabilityMiddleware.Reset();
    }

    public void Dispose() => WebDavObservabilityMiddleware.Reset();

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
}
