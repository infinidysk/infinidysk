using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Streams;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class SegmentBufferPoolAllocationFailureTests
{
    [Fact]
    public void Rent_AllocationOom_RethrowsSameInstanceAndRecordsTelemetry()
    {
        var expected = new OutOfMemoryException("segment alloc");
        var pool = new SegmentBufferPool(
            maxIdleBytes: 4 * 1024 * 1024,
            retentionPolicy: SegmentBufferRetentionPolicy.Legacy,
            allocator: _ => throw expected);

        OutOfMemoryException? actual = null;
        var events = CaptureLogs(() =>
            actual = Assert.Throws<OutOfMemoryException>(() => pool.Rent(750_000)));

        Assert.Same(expected, actual);
        var snapshot = pool.Snapshot();
        Assert.Equal(1, snapshot.AllocationAttemptCount);
        Assert.Equal(1, snapshot.AllocationFailureCount);
        Assert.Equal(0, snapshot.AllocationCount);
        Assert.Equal(0, snapshot.RentCount);
        Assert.Equal(0, snapshot.CheckedOutBytes);

        var warning = Assert.Single(
            events,
            e => e.Level == LogEventLevel.Warning &&
                 e.MessageTemplate.Text.Contains("allocating segment buffer", StringComparison.Ordinal));
        Assert.Null(warning.Exception);
        Assert.Equal(750_000, ToInt64(warning.Properties["RequestedBytes"]));
        Assert.Equal(768 * 1024, ToInt64(warning.Properties["RoundedBytes"]));
        Assert.Contains(events, e =>
            e.Level == LogEventLevel.Debug &&
            e.Exception is OutOfMemoryException);
    }

    [Fact]
    public void Rent_AllocationOom_DiagnosticHelperThrow_StillRethrowsOriginal()
    {
        var expected = new OutOfMemoryException("segment alloc");
        var pool = new SegmentBufferPool(
            maxIdleBytes: 4 * 1024 * 1024,
            retentionPolicy: SegmentBufferRetentionPolicy.Legacy,
            allocator: _ => throw expected,
            onAllocationFailure: (_, _, _, _) => throw new InvalidOperationException("diag"));

        var actual = Assert.Throws<OutOfMemoryException>(() => pool.Rent(256 * 1024));
        Assert.Same(expected, actual);
        Assert.Equal(1, pool.Snapshot().AllocationFailureCount);
        Assert.Equal(0, pool.Snapshot().AllocationCount);
    }

    private static long ToInt64(LogEventPropertyValue value) =>
        Convert.ToInt64(Assert.IsType<ScalarValue>(value).Value, System.Globalization.CultureInfo.InvariantCulture);

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

public sealed class SegmentBufferPoolSelectorTests
{
    [Theory]
    [InlineData(null, "bounded-capacity", false)]
    [InlineData("", "bounded-capacity", false)]
    [InlineData(" ", "bounded-capacity", false)]
    [InlineData(" bounded-capacity ", "bounded-capacity", false)]
    [InlineData("bounded-legacy", "bounded-legacy", false)]
    [InlineData("BOUNDED-LEGACY", "bounded-legacy", false)]
    [InlineData("bounded-capacity", "bounded-capacity", false)]
    [InlineData("Bounded-Capacity", "bounded-capacity", false)]
    [InlineData("shared", "shared", false)]
    [InlineData("SHARED", "shared", false)]
    [InlineData("experimental", "bounded-capacity", true)]
    [InlineData("legacy", "bounded-capacity", true)]
    public void Resolve_CoversDocumentedValuesAndUnknownFallback(
        string? value,
        string expected,
        bool unknown)
    {
        var mode = SegmentBufferPoolSelector.Resolve(value, out var unknownValue);
        Assert.Equal(expected, SegmentBufferPoolSelector.ToLogValue(mode));
        Assert.Equal(unknown, unknownValue);
    }
}
