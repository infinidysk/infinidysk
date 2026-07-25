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
