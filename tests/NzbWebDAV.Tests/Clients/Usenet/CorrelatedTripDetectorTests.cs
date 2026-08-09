using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public class CorrelatedTripDetectorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(5);

    [Fact]
    public void SingleProvider_NeverWarns()
    {
        var events = Capture((d, _) =>
        {
            d.Unregister("b");
            d.OnTransition("a", Open(atMs: 1_000));
        });

        Assert.Empty(events);
    }

    [Fact]
    public void AllProvidersTrippingWithinTheWindow_WarnsOnce()
    {
        var events = Capture((d, _) =>
        {
            d.OnTransition("a", Open(atMs: 1_000));
            d.OnTransition("b", Open(atMs: 2_000));
        });

        Assert.Single(events, IsCorrelationWarning);
    }

    [Fact]
    public void TripsOutsideTheWindow_DoNotWarn()
    {
        var events = Capture((d, _) =>
        {
            d.OnTransition("a", Open(atMs: 1_000));
            d.OnTransition("b", Open(atMs: 1_000 + (long)Window.TotalMilliseconds + 1));
        });

        Assert.Empty(events);
    }

    [Fact]
    public void ClosedBetweenOpens_ResetsTheCorrelation()
    {
        var events = Capture((d, _) =>
        {
            d.OnTransition("a", Open(atMs: 1_000));
            d.OnTransition("a", Closed(atMs: 2_000));
            d.OnTransition("b", Open(atMs: 3_000));
        });

        Assert.Empty(events);
    }

    [Fact]
    public void OnlySomeProvidersTripped_DoesNotWarn()
    {
        var events = Capture((d, _) =>
        {
            d.Register("c", "c.example.com");
            d.OnTransition("a", Open(atMs: 1_000));
            d.OnTransition("b", Open(atMs: 2_000));
        });

        Assert.Empty(events);
    }

    [Fact]
    public void SecondCorrelation_WithinThrottle_DoesNotWarnAgain()
    {
        var events = Capture((d, clock) =>
        {
            d.OnTransition("a", Open(atMs: 1_000));
            clock.Now = 2_000;
            d.OnTransition("b", Open(atMs: 2_000));

            // Recover, then both trip again one minute later — still throttled.
            var t1 = 61_000L;
            clock.Now = t1;
            d.OnTransition("a", Closed(atMs: t1));
            d.OnTransition("b", Closed(atMs: t1));
            d.OnTransition("a", Open(atMs: t1 + 100));
            clock.Now = t1 + 200;
            d.OnTransition("b", Open(atMs: t1 + 200));
        });

        Assert.Single(events, IsCorrelationWarning);
    }

    [Fact]
    public void SecondCorrelation_AfterThrottle_WarnsAgain()
    {
        var events = Capture((d, clock) =>
        {
            d.OnTransition("a", Open(atMs: 1_000));
            clock.Now = 2_000;
            d.OnTransition("b", Open(atMs: 2_000));

            var t1 = 1_000 + (long)Throttle.TotalMilliseconds + 1_000;
            clock.Now = t1;
            d.OnTransition("a", Closed(atMs: t1));
            d.OnTransition("b", Closed(atMs: t1));
            d.OnTransition("a", Open(atMs: t1 + 100));
            clock.Now = t1 + 200;
            d.OnTransition("b", Open(atMs: t1 + 200));
        });

        Assert.Equal(2, events.Count(IsCorrelationWarning));
    }

    private static bool IsCorrelationWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.MessageTemplate.Text.Contains("circuit breakers", StringComparison.Ordinal);

    private static ProviderCircuitTransition Open(long atMs) =>
        new(ProviderCircuitTransitionState.Open, atMs, TimeSpan.FromSeconds(60));

    private static ProviderCircuitTransition Closed(long atMs) =>
        new(ProviderCircuitTransitionState.Closed, atMs, null);

    private static IReadOnlyList<LogEvent> Capture(Action<CorrelatedTripDetector, TestClock> act)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var clock = new TestClock();
        var detector = new CorrelatedTripDetector(window: Window, throttle: Throttle)
        {
            Clock = () => clock.Now,
        };
        detector.Register("a", "a.example.com");
        detector.Register("b", "b.example.com");

        try
        {
            act(detector, clock);
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class TestClock
    {
        public long Now;
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
