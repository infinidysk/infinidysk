using System.Xml;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Tests.Extensions;

[Collection(nameof(GlobalLoggerCollection))]
public class RemoteResponseExceptionLoggingTests
{
    private const string Secret = "DO_NOT_LOG_BODY_MARKER";

    [Fact]
    public void LogWarningKnownOrStack_RemoteTooLarge_IsWarningWithoutException()
    {
        var inner = new NzbResponseTooLargeException(100);
        var ex = new RemoteResponseTooLargeException(100, null, inner);
        var events = Capture(() =>
            ex.LogWarningKnownOrStack("Watchtower: sync failed for source {Name}.", "My Catalog"));

        var warning = Assert.Single(events, e => e.Level == LogEventLevel.Warning);
        Assert.Null(warning.Exception);
        var rendered = warning.RenderMessage();
        Assert.Contains("My Catalog", rendered);
        Assert.Contains("Reason:", rendered);
        Assert.DoesNotContain(Secret, rendered);
        Assert.DoesNotContain("NZB response", rendered);
    }

    [Fact]
    public void LogWarningKnownOrStack_RemoteFormat_IsWarningWithoutException()
    {
        var ex = new RemoteResponseFormatException(
            "Indexer returned invalid XML.",
            new XmlException($"bad xml {Secret}"));
        var events = Capture(() =>
            ex.LogWarningKnownOrStack("Indexer {Indexer} search failed.", "ExampleIndexer"));

        var warning = Assert.Single(events, e => e.Level == LogEventLevel.Warning);
        Assert.Null(warning.Exception);
        var rendered = warning.RenderMessage();
        Assert.Contains("ExampleIndexer", rendered);
        Assert.Contains("Indexer returned invalid XML.", rendered);
        Assert.DoesNotContain(Secret, rendered);
    }

    [Fact]
    public void LogWarningKnownOrStack_Unexpected_KeepsStackWithoutSecretInTemplate()
    {
        var ex = new InvalidOperationException($"boom {Secret}");
        var events = Capture(() =>
            ex.LogWarningKnownOrStack("Watchtower: sync failed for source {Name}.", "My Catalog"));

        var warning = Assert.Single(events, e => e.Level == LogEventLevel.Warning);
        Assert.Same(ex, warning.Exception);
        Assert.Contains("My Catalog", warning.RenderMessage());
        Assert.DoesNotContain("https://", warning.RenderMessage());
    }

    private static IReadOnlyList<LogEvent> Capture(Action action)
    {
        var sink = new CollectingLogEventSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }
}
