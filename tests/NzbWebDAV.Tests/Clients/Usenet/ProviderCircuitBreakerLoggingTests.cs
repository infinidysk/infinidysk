using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public class ProviderCircuitBreakerLoggingTests
{
    [Fact]
    public void RecordSuccess_AfterFailuresThatNeverTripped_DoesNotAnnounceRecovery()
    {
        var events = CaptureLogs(breaker =>
        {
            // One short of the trip threshold, so the circuit never opened.
            breaker.RecordFailure();
            breaker.RecordFailure();
            Assert.False(breaker.IsTripped);

            breaker.RecordSuccess();
        });

        Assert.DoesNotContain(events, RecoveryWasAnnounced);
    }

    [Fact]
    public void RecordSuccess_AfterATrip_AnnouncesRecovery()
    {
        var events = CaptureLogs(breaker =>
        {
            breaker.RecordFailure();
            breaker.RecordFailure();
            breaker.RecordFailure();
            Assert.True(breaker.IsTripped);

            breaker.ExpireCooldownForTests();
            breaker.RecordSuccess();
        });

        Assert.Contains(events, RecoveryWasAnnounced);
    }

    [Fact]
    public void RecordSuccess_DuringOpenCooldown_DoesNotAnnounceRecovery()
    {
        var events = CaptureLogs(breaker =>
        {
            breaker.RecordFailure();
            breaker.RecordFailure();
            breaker.RecordFailure();

            breaker.RecordSuccess();
        });

        Assert.DoesNotContain(events, RecoveryWasAnnounced);
    }

    private static bool RecoveryWasAnnounced(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.Contains("recovered", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<LogEvent> CaptureLogs(Action<ProviderCircuitBreaker> act)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            act(new ProviderCircuitBreaker("logging-test"));
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
