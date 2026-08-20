using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class OomDiagnosticsTests
{
    [Fact]
    public void LogHeapStateOnOom_LogsWarning()
    {
        var events = CaptureLogs(() =>
            OomDiagnostics.LogHeapStateOnOom(new OutOfMemoryException("test"), "test operation"));

        var entry = Assert.Single(
            events,
            e => e.MessageTemplate.Text.Contains("OutOfMemoryException during"));
        Assert.Equal(LogEventLevel.Warning, entry.Level);
        Assert.Contains("OutOfMemoryException during", entry.MessageTemplate.Text);
    }

    [Fact]
    public void LogHeapStateOnOom_NonOom_IsNoOp()
    {
        var events = CaptureLogs(() =>
            OomDiagnostics.LogHeapStateOnOom(new InvalidOperationException("test"), "test operation"));

        Assert.Empty(events);
    }

    private static IReadOnlyList<LogEvent> CaptureLogs(Action action)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
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
}
