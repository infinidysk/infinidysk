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
        WebDavObservabilityMiddleware.StallThresholdOverride = null;
        WebDavObservabilityMiddleware.WarningIntervalOverride = null;
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
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        context.Response.StatusCode = 503;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["failed"]);
        Assert.Equal(1, counters["slow"]);
        Assert.Equal(1, counters["slowFirstByte"]);
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

    [Fact]
    public async Task SlowGet_WithoutFirstByte_CountsSlowFirstByte()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["slowFirstByte"]);
        Assert.Equal(1, counters["slow"]);
    }

    [Fact]
    public async Task Get_WithFirstByte_AndFastThresholds_IsNotSlow()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.FromHours(1);
        WebDavObservabilityMiddleware.StallThresholdOverride = TimeSpan.FromHours(2);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        var middleware = new WebDavObservabilityMiddleware(async ctx =>
            await ctx.Response.Body.WriteAsync(new byte[] { 1 }));

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.False(counters.ContainsKey("slow"));
        Assert.False(counters.ContainsKey("slowFirstByte"));
        Assert.False(counters.ContainsKey("longStreams"));
    }

    [Fact]
    public async Task Get_WithFirstByte_PastStallThreshold_CountsStalledStream()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.FromHours(1);
        // Stalled means strictly over the threshold; a negative override keeps the
        // test deterministic instead of racing a zero-millisecond request.
        WebDavObservabilityMiddleware.StallThresholdOverride = TimeSpan.FromMilliseconds(-1);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        var middleware = new WebDavObservabilityMiddleware(async ctx =>
            await ctx.Response.Body.WriteAsync(new byte[] { 1 }));

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["stalledStreams"]);
        Assert.Equal(1, counters["slow"]);
        Assert.False(counters.ContainsKey("slowFirstByte"));
    }

    [Fact]
    public async Task SlowPropfind_CountsSlowMetadata()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        var context = new DefaultHttpContext();
        context.Request.Method = "PROPFIND";
        context.Request.Path = "/content/tv";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["slowMetadata"]);
        Assert.Equal(1, counters["slow"]);
        Assert.False(counters.ContainsKey("slowFirstByte"));
    }

    [Fact]
    public async Task SlowNonMetadataMethod_IsNotCountedAsSlowMetadata()
    {
        // Only PROPFIND/HEAD keep total-duration semantics; other non-GET WebDAV
        // methods are not part of the slow taxonomy.
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        var context = new DefaultHttpContext();
        context.Request.Method = "DELETE";
        context.Request.Path = "/content/tv/show.mkv";
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["total"]);
        Assert.False(counters.ContainsKey("slowMetadata"));
        Assert.False(counters.ContainsKey("slow"));
    }

    [Fact]
    public async Task AbortedGet_WithoutFirstByte_CountsAbortedBeforeFirstByte()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        context.RequestAborted = new CancellationToken(canceled: true);
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["aborted"]);
        Assert.Equal(1, counters["abortedBeforeFirstByte"]);
    }

    [Fact]
    public async Task AbortedGet_AfterFirstByte_IsNotAbortedBeforeFirstByte()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        context.RequestAborted = new CancellationToken(canceled: true);
        var middleware = new WebDavObservabilityMiddleware(async ctx =>
            await ctx.Response.Body.WriteAsync(new byte[] { 1 }));

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["aborted"]);
        Assert.False(counters.ContainsKey("abortedBeforeFirstByte"));
    }

    [Fact]
    public async Task AbortedGet_AfterZeroLengthWrite_StillCountsAbortedBeforeFirstByte()
    {
        // A zero-length write carries no body byte: it must not mark the first
        // byte, or an aborted-before-first-byte request would go uncounted.
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        context.RequestAborted = new CancellationToken(canceled: true);
        var middleware = new WebDavObservabilityMiddleware(async ctx =>
            await ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte>.Empty));

        await middleware.InvokeAsync(context);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(1, counters["aborted"]);
        Assert.Equal(1, counters["abortedBeforeFirstByte"]);
    }

    [Fact]
    public async Task SlowWarnings_AreThrottledPerCategory()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        WebDavObservabilityMiddleware.WarningIntervalOverride = TimeSpan.FromHours(1);
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(NewSlowGet());
        await middleware.InvokeAsync(NewSlowGet());

        // A different category owns a separate throttle, so it must still emit.
        var propfind = new DefaultHttpContext();
        propfind.Request.Method = "PROPFIND";
        propfind.Request.Path = "/content/tv";
        await middleware.InvokeAsync(propfind);

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(2, counters["slowFirstByte"]);
        Assert.Equal(1, counters["slowMetadata"]);
        Assert.Equal(1, counters["suppressedSlowWarnings"]);
    }

    [Fact]
    public async Task SlowWarnings_WithZeroInterval_AreNotThrottled()
    {
        WebDavObservabilityMiddleware.SlowThresholdOverride = TimeSpan.Zero;
        WebDavObservabilityMiddleware.WarningIntervalOverride = TimeSpan.Zero;
        var middleware = new WebDavObservabilityMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(NewSlowGet());
        await middleware.InvokeAsync(NewSlowGet());

        var counters = WebDavObservabilityMiddleware.Snapshot();
        Assert.Equal(2, counters["slowFirstByte"]);
        Assert.False(counters.ContainsKey("suppressedSlowWarnings"));
    }

    private static DefaultHttpContext NewSlowGet()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/content/tv/show.mkv";
        return context;
    }

    // firstByteOrMinusOne stands in for long? because InlineData cannot convert int
    // constants to Nullable<long> through reflection.
    [Theory]
    [InlineData("PROPFIND", -1, 6_000, WebDavObservabilityMiddleware.SlowKind.Metadata)]
    [InlineData("PROPFIND", -1, 100, WebDavObservabilityMiddleware.SlowKind.None)]
    [InlineData("HEAD", -1, 6_000, WebDavObservabilityMiddleware.SlowKind.Metadata)]
    [InlineData("DELETE", -1, 6_000, WebDavObservabilityMiddleware.SlowKind.None)]
    [InlineData("GET", -1, 6_000, WebDavObservabilityMiddleware.SlowKind.FirstByte)]
    [InlineData("GET", -1, 100, WebDavObservabilityMiddleware.SlowKind.None)]
    [InlineData("GET", 6_000, 7_000, WebDavObservabilityMiddleware.SlowKind.FirstByte)]
    [InlineData("GET", 10, 10_000, WebDavObservabilityMiddleware.SlowKind.LongStream)]
    [InlineData("GET", 10, 60_000, WebDavObservabilityMiddleware.SlowKind.LongStream)]
    [InlineData("GET", 10, 61_000, WebDavObservabilityMiddleware.SlowKind.Stalled)]
    [InlineData("GET", 10, 100, WebDavObservabilityMiddleware.SlowKind.None)]
    public void ClassifySlow_AttributesByWhereTimeWent(
        string method, long firstByteOrMinusOne, long elapsedMs, WebDavObservabilityMiddleware.SlowKind expected)
    {
        long? firstByteMs = firstByteOrMinusOne >= 0 ? firstByteOrMinusOne : null;

        Assert.Equal(expected, WebDavObservabilityMiddleware.ClassifySlow(method, firstByteMs, elapsedMs));
    }

    [Fact]
    public void ClassifySlow_AbortedLongGetWithFastFirstByte_IsNotSlow()
    {
        // Regression: a healthy stream that outlives the slow threshold and ends by
        // client close must not be attributed as server latency.
        var kind = WebDavObservabilityMiddleware.ClassifySlow("GET", firstByteMs: 10, elapsedMs: 10_000);

        Assert.Equal(WebDavObservabilityMiddleware.SlowKind.LongStream, kind);
    }
}
