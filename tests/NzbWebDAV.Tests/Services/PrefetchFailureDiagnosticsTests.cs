using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Prefetch;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchFailureDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("io", "source-or-cache-io", "queued")]
    [InlineData("article", "source-unavailable", "failed")]
    [InlineData("seek", "source-layout", "failed")]
    [InlineData("access", "storage-access", "failed")]
    [InlineData("cancel", "unexpected-cancellation", "failed")]
    [InlineData("argument", "invalid-request", "failed")]
    [InlineData("unexpected", "unexpected-failure", "failed")]
    public async Task Failure_PersistsSafeCategoryAndLogsJobIdentity_WithoutSecretMessages(string failure, string category, string state)
    {
        const string secret = "private-token-and-media-path";
        var inner = new Exception(secret);
        Exception exception = failure switch
        {
            "io" => new IOException(secret, inner),
            "article" => new UsenetArticleNotFoundException(secret, secret),
            "seek" => new SeekPositionNotFoundException(secret, inner),
            "access" => new UnauthorizedAccessException(secret, inner),
            "cancel" => new OperationCanceledException(secret, inner),
            "argument" => new ArgumentException(secret, inner),
            _ => new InvalidOperationException(secret, inner)
        };
        var sink = new RecordingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var job = store.Enqueue(Guid.NewGuid(), "manual", 0);
        using var coordinator = new PrefetchCoordinator(store, new ThrowingExecutor(exception), () => new(), () => true, logger: logger);
        await coordinator.RunOnceAsync(CancellationToken.None);
        var saved = Assert.Single(store.List());
        Assert.Equal(state, saved.State);
        Assert.Contains(category, saved.Error);
        Assert.Contains(exception.GetType().Name, saved.Error);
        Assert.DoesNotContain(secret, saved.Error);
        var warning = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning);
        Assert.Contains(job.Id, warning.RenderMessage());
        Assert.Contains(job.ItemId.ToString(), warning.RenderMessage());
        Assert.All(sink.Events, e =>
        {
            Assert.Null(e.Exception);
            Assert.DoesNotContain(secret, e.RenderMessage());
        });
        Assert.Null(coordinator.RuntimeError);
    }

    private sealed class ThrowingExecutor(Exception exception) : IPrefetchExecutor
    {
        public Task ExecuteAsync(PrefetchJob job, CancellationToken ct) => Task.FromException(exception);
    }
    private sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
