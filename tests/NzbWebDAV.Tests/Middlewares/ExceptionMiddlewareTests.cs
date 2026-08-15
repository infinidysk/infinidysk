using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Middlewares;

[Collection(nameof(GlobalLoggerCollection))]
public class ExceptionMiddlewareTests
{
    [Fact]
    public async Task UnalignableSegmentMidResponse_AbortsAndLogsWithoutAStackDump()
    {
        var reason =
            $"Segment 7 of 12 could not be downloaded, and its exact length is unknown ({Guid.NewGuid()}).";
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new RetryableDownloadException(
                reason, new TimeoutException("Timeout executing nntp BODY command.")));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        Assert.True(lifetimeFeature.Aborted);
        var logged = Assert.Single(events, e => e.RenderMessage().Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Error, logged.Level);
        Assert.Null(logged.Exception);
    }

    private static async Task<IReadOnlyList<LogEvent>> CaptureLogsAsync(Func<Task> action)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await action();
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    [Fact]
    public async Task MissingArticleAfterResponseStarted_AbortsConnection()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new UsenetArticleNotFoundException("missing-segment"));

        await middleware.InvokeAsync(context);

        Assert.True(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task MissingArticleBeforeResponseStarted_ReturnsNotFoundWithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new UsenetArticleNotFoundException("missing-segment"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task CorruptRarAfterResponseStarted_AbortsConnection()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new CorruptRarException("missing continuation header"));

        await middleware.InvokeAsync(context);

        Assert.True(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task CorruptRarBeforeResponseStarted_ReturnsNotFoundWithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new CorruptRarException("missing continuation header"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task CorruptRarWithDavItem_RecordsStreamingFailure()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var davItem = Assert.IsType<DavItem>(context.Items["DavItem"]);
        var failureTracker = new StreamingFailureTracker();
        var configManager = CreateRepairEnabledConfig();
        var middleware = CreateMiddleware(
            _ => throw new CorruptRarException("missing continuation header"),
            configManager,
            failureTracker);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, failureTracker.GetFailureCount(davItem.Id));
    }

    [Fact]
    public async Task IncompleteMultipartPartBeforeResponseStarted_ReturnsNotFoundWithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteMultipartPartException("volume ended 3071980 bytes early"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task IncompleteMultipartPartAfterResponseStarted_AbortsConnection()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteMultipartPartException("volume ended 3071980 bytes early"));

        await middleware.InvokeAsync(context);

        Assert.True(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task IncompleteMultipartPartWithDavItem_RecordsStreamingFailure()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var davItem = Assert.IsType<DavItem>(context.Items["DavItem"]);
        var failureTracker = new StreamingFailureTracker();
        var middleware = CreateMiddleware(
            _ => throw new IncompleteMultipartPartException("volume ended 3071980 bytes early"),
            CreateRepairEnabledConfig(),
            failureTracker);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, failureTracker.GetFailureCount(davItem.Id));
    }

    [Fact]
    public async Task IncompleteFileContentBeforeResponseStarted_ReturnsNotFoundWithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteFileContentException("/content/tv/show.mkv", 16_777_216, 14_000_000));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task IncompleteFileContentAfterResponseStarted_AbortsConnection()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteFileContentException("/content/tv/show.mkv", 16_777_216, 14_000_000));

        await middleware.InvokeAsync(context);

        Assert.True(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task IncompleteFileContentWithDavItem_RecordsStreamingFailure()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var davItem = Assert.IsType<DavItem>(context.Items["DavItem"]);
        var failureTracker = new StreamingFailureTracker();
        var middleware = CreateMiddleware(
            _ => throw new IncompleteFileContentException("/content/tv/show.mkv", 16_777_216, 14_000_000),
            CreateRepairEnabledConfig(),
            failureTracker);

        await middleware.InvokeAsync(context);

        Assert.Equal(1, failureTracker.GetFailureCount(davItem.Id));
    }

    [Fact]
    public async Task IncompleteFileContent_LogsOneWarningLineWithoutAStackDump()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteFileContentException(
                "/content/tv/show.mkv", 33_554_432, 32_000_000));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        var logged = Assert.Single(
            events,
            e => e.Level == LogEventLevel.Warning
                 && e.RenderMessage().Contains("delivered 32000000", StringComparison.Ordinal)
                 && e.RenderMessage().Contains("33554432 expected bytes", StringComparison.Ordinal));
        Assert.Null(logged.Exception);
    }

    [Fact]
    public async Task IncompleteMultipartPart_LogsOneWarningLineWithoutAStackDump()
    {
        var reason = $"volume ended 3071980 bytes early ({Guid.NewGuid()})";
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new IncompleteMultipartPartException(reason));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        var logged = Assert.Single(
            events, e => e.RenderMessage().Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Warning, logged.Level);
        Assert.Null(logged.Exception);
    }

    [Fact]
    public async Task MissingFilePayloadBeforeResponseStarted_ReturnsTyped404WithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw CreateMissingPayloadException());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("missing-file-payload", context.Response.Headers["X-InfiniDysk-Stream-Error"].ToString());
        Assert.False(lifetimeFeature.Aborted);
    }

    [Fact]
    public async Task MissingFilePayload_LogsOneWarningLineWithoutAStackDump()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: false, lifetimeFeature);
        var payloadId = Guid.NewGuid();
        var middleware = CreateMiddleware(_ => throw CreateMissingPayloadException(payloadId));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        var logged = Assert.Single(events,
            e => e.Level == LogEventLevel.Warning
                 && e.RenderMessage().Contains("streaming payload is missing", StringComparison.Ordinal));
        Assert.Contains(payloadId.ToString(), logged.RenderMessage(), StringComparison.Ordinal);
        Assert.Null(logged.Exception);
        Assert.DoesNotContain(events, e => e.Level >= LogEventLevel.Error);
    }

    [Fact]
    public async Task MissingFilePayloadWithDavItem_DoesNotRecordStreamingFailure()
    {
        // Missing local metadata is not evidence of a bad release; feeding it to
        // the failure tracker would eventually trigger Arr remove-and-blocklist.
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var failureTracker = new StreamingFailureTracker();
        var middleware = CreateMiddleware(
            _ => throw CreateMissingPayloadException(),
            CreateRepairEnabledConfig(),
            failureTracker);

        await middleware.InvokeAsync(context);

        var davItem = Assert.IsType<DavItem>(context.Items["DavItem"]);
        Assert.Equal(0, failureTracker.GetFailureCount(davItem.Id));
    }

    private static MissingFilePayloadException CreateMissingPayloadException(Guid? payloadId = null)
    {
        var id = Guid.NewGuid();
        return new MissingFilePayloadException(
            new DavItem
            {
                Id = id,
                IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
                Path = $"/content/movies/missing-payload-{id:N}.mkv",
                FileBlobId = payloadId,
            },
            DavItem.ItemSubType.MultipartFile);
    }

    [Fact]
    public async Task StreamingReadTimeout_BeforeResponseStarted_Returns503WithRetryAfter()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: false, lifetimeFeature);
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var middleware = CreateMiddleware(
            _ => throw new StreamingReadTimeoutException(
                "WebDAV read exceeded the 5s streaming-read-timeout while waiting for the Usenet backend."));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());
        Assert.False(lifetimeFeature.Aborted);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("streaming-read-timeout", body, StringComparison.OrdinalIgnoreCase);

        var logged = Assert.Single(
            events,
            e => e.RenderMessage().Contains("streaming-read-timeout", StringComparison.Ordinal)
                 && e.Level == LogEventLevel.Warning
                 && e.Exception is null);
        Assert.Contains("failed fast", logged.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingReadTimeout_AfterResponseStarted_AbortsWithoutErrorStack()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: true, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new StreamingReadTimeoutException(
                "WebDAV read exceeded the 5s streaming-read-timeout while waiting for the Usenet backend."));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        Assert.True(lifetimeFeature.Aborted);
        var logged = Assert.Single(
            events,
            e => e.RenderMessage().Contains("streaming-read-timeout-after-headers", StringComparison.Ordinal)
                 && e.Level == LogEventLevel.Warning
                 && e.Exception is null);
        Assert.Contains("aborted after headers", logged.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingWriteTimeout_AfterResponseStarted_AbortsAndLogsWriteStall()
    {
        // SWTE must reach the dedicated StreamingWriteTimeoutException branch and log
        // "streaming-write-timeout", not be wrapped into SRTE and mislabeled as
        // "streaming-read-timeout-after-headers" (issue #949).
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: true, lifetimeFeature);
        context.Request.Path = $"/content/write-stall-after-{Guid.NewGuid():N}.mkv";
        var middleware = CreateMiddleware(
            _ => throw new StreamingWriteTimeoutException(
                "Client stopped reading; streaming write timed out."));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        Assert.True(lifetimeFeature.Aborted);
        var logged = Assert.Single(
            events,
            e => e.RenderMessage().Contains("streaming-write-timeout", StringComparison.Ordinal)
                 && e.Level == LogEventLevel.Warning
                 && e.Exception is null);
        Assert.Contains("write stalled", logged.RenderMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "streaming-read-timeout",
            logged.RenderMessage(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingWriteTimeout_BeforeResponseStarted_Returns499WithoutAborting()
    {
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateContext(hasStarted: false, lifetimeFeature);
        context.Request.Path = $"/content/write-stall-before-{Guid.NewGuid():N}.mkv";
        var middleware = CreateMiddleware(
            _ => throw new StreamingWriteTimeoutException(
                "Client stopped reading; streaming write timed out."));

        var events = await CaptureLogsAsync(() => middleware.InvokeAsync(context));

        Assert.Equal(499, context.Response.StatusCode);
        Assert.False(lifetimeFeature.Aborted);
        Assert.Single(
            events,
            e => e.RenderMessage().Contains("streaming-write-timeout", StringComparison.Ordinal)
                 && e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public async Task MissingArticle_CoalescesUnicodePathVariants()
    {
        var segmentId = $"<{Guid.NewGuid():N}@test>";
        var baseName = $"unicode-{Guid.NewGuid():N}";
        var pathNfc = $"/content/{baseName}\u00E9.mkv";
        var pathNfd = $"/content/{baseName}e\u0301.mkv";
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var contextNfc = CreateContext(hasStarted: false, lifetimeFeature);
        contextNfc.Request.Path = pathNfc;
        var contextNfd = CreateContext(hasStarted: false, lifetimeFeature);
        contextNfd.Request.Path = pathNfd;
        var middleware = CreateMiddleware(_ => throw new UsenetArticleNotFoundException(segmentId));
        var events = await CaptureLogsAsync(async () =>
        {
            await middleware.InvokeAsync(contextNfc);
            await middleware.InvokeAsync(contextNfd);
        });
        Assert.Single(events, e => e.Level == LogEventLevel.Error
            && e.RenderMessage().Contains("missing articles", StringComparison.Ordinal)
            && e.RenderMessage().Contains(segmentId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingArticleWithDavItem_RecordsSegmentForQueueFailFast()
    {
        // Re-grabs of the same release must fail the step-0 queue precheck pre-import
        // instead of importing again and looping through repair (issue #732).
        var segmentId = $"<{Guid.NewGuid():N}@test>";
        var lifetimeFeature = new TestHttpRequestLifetimeFeature();
        var context = CreateDavItemContext(hasStarted: false, lifetimeFeature);
        var middleware = CreateMiddleware(
            _ => throw new UsenetArticleNotFoundException(segmentId));

        await middleware.InvokeAsync(context);

        var ex = Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([segmentId]));
        Assert.Equal(segmentId, ex.SegmentId);
    }

    [Fact]
    public void RecordMissingArticleForFailFast_IgnoresUnimportantFileTypes()
    {
        var segmentId = $"<{Guid.NewGuid():N}@test>";
        var id = Guid.NewGuid();
        var davItem = new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = "release.nfo",
            Path = "/content/release.nfo",
            Type = DavItem.ItemType.UsenetFile,
        };

        ExceptionMiddleware.RecordMissingArticleForFailFast(davItem, segmentId);

        // Must not throw — unimportant files never enter the fail-fast cache.
        HealthCheckService.CheckCachedMissingSegmentIds([segmentId]);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    public void ShouldScheduleUrgentRepair_RequiresConfiguredFailureThreshold(
        int threshold,
        int failureCount,
        bool expected)
    {
        Assert.Equal(expected, ExceptionMiddleware.ShouldScheduleUrgentRepair(threshold, failureCount));
    }

    [Theory]
    [InlineData(false, true, 1, "Enable Background Repairs is off")]
    [InlineData(true, false, 1, "Library Directory is not set")]
    [InlineData(true, true, 0, "no Radarr/Sonarr instances are configured")]
    public void GetRepairDisabledReason_NamesMissingPrerequisite(
        bool isRepairEnabled,
        bool hasLibraryDir,
        int arrInstanceCount,
        string expected)
    {
        Assert.Equal(expected, ConfigManager.GetRepairDisabledReason(isRepairEnabled, hasLibraryDir, arrInstanceCount));
    }

    [Fact]
    public void GetRepairDisabledReason_ReturnsNullWhenFullyEnabled()
    {
        Assert.Null(ConfigManager.GetRepairDisabledReason(true, true, 1));
    }

    [Fact]
    public void GetRepairDisabledReason_InstanceReflectsConfiguredPrerequisites()
    {
        var configManager = CreateRepairEnabledConfig();
        Assert.Null(configManager.GetRepairDisabledReason());
        Assert.True(configManager.IsRepairJobEnabled());
    }

    private static ExceptionMiddleware CreateMiddleware(
        RequestDelegate next,
        ConfigManager? configManager = null,
        StreamingFailureTracker? failureTracker = null)
    {
        return new ExceptionMiddleware(
            next,
            configManager ?? new ConfigManager(),
            failureTracker ?? new StreamingFailureTracker());
    }

    private static DefaultHttpContext CreateContext(
        bool hasStarted,
        TestHttpRequestLifetimeFeature lifetimeFeature)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new TestHttpResponseFeature(hasStarted));
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetimeFeature);
        return context;
    }

    private static DefaultHttpContext CreateDavItemContext(
        bool hasStarted,
        TestHttpRequestLifetimeFeature lifetimeFeature)
    {
        var context = CreateContext(hasStarted, lifetimeFeature);
        var id = Guid.NewGuid();
        context.Items["DavItem"] = new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = "video.mkv",
            Path = "/content/video.mkv",
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.MultipartFile,
        };
        return context;
    }

    private static ConfigManager CreateRepairEnabledConfig()
    {
        var arrConfig = new ArrConfig
        {
            SonarrInstances =
            [
                new ArrConfig.ConnectionDetails
                {
                    Host = "http://sonarr.invalid",
                    ApiKey = "test-api-key",
                },
            ],
        };
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = "/tmp/library" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ArrInstances,
                ConfigValue = JsonSerializer.Serialize(arrConfig),
            },
        ]);
        Assert.True(configManager.IsRepairJobEnabled());
        return configManager;
    }

    private sealed class TestHttpResponseFeature(bool hasStarted) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; } = hasStarted;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    private sealed class TestHttpRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
            Aborted = true;
        }
    }
}
