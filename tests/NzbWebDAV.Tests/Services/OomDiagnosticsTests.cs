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
        Assert.Contains("LastGcAvailableCeiling", entry.MessageTemplate.Text);
        Assert.Null(entry.Exception);
        Assert.True(entry.Properties.ContainsKey("GcIndex"));
        Assert.True(entry.Properties.ContainsKey("GcGeneration"));
        Assert.True(entry.Properties.ContainsKey("LohSize"));
        Assert.True(entry.Properties.ContainsKey("LohFragmentation"));
        Assert.True(entry.Properties.ContainsKey("MemoryLoad"));
        Assert.True(entry.Properties.ContainsKey("HighMemoryLoadThreshold"));
        Assert.True(entry.Properties.ContainsKey("WorkingSet"));
        Assert.True(entry.Properties.ContainsKey("LohHardLimit"));
        Assert.True(entry.Properties.ContainsKey("LohHardLimitPercent"));
        Assert.True(entry.Properties.ContainsKey("PoolOutstanding"));
        Assert.Contains(events, e =>
            e.Level == LogEventLevel.Debug &&
            e.MessageTemplate.Text.Contains("OutOfMemoryException stack") &&
            e.Exception is OutOfMemoryException);
    }

    [Fact]
    public void LogHeapStateOnOom_NonOom_IsNoOp()
    {
        var events = CaptureLogs(() =>
            OomDiagnostics.LogHeapStateOnOom(new InvalidOperationException("test"), "test operation"));

        Assert.DoesNotContain(
            events,
            e => e.MessageTemplate.Text.Contains("OutOfMemoryException during"));
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
