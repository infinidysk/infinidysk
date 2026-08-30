using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Logging;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class SynchronousObserverInvokerTests : IDisposable
{
    private const string SentinelSecret = "sentinel-secret-value-do-not-log";
    private const string CallbackArgument = "callback-argument-must-not-appear";
    private static readonly object Sender = new();

    public SynchronousObserverInvokerTests()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    public void Dispose()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    [Fact]
    public void EventHandlers_ThrowingFirstSubscriber_InvokesLaterSubscriberInOrder()
    {
        var order = new List<string>();
        EventHandler<EventArgs> subscribers = (_, _) =>
        {
            order.Add("first");
            throw new InvalidOperationException("first");
        };
        subscribers += (_, _) => order.Add("second");

        SynchronousObserverInvoker.Invoke(
            subscribers, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void Action_ThrowingFirstSubscriber_InvokesLaterSubscriberInOrder()
    {
        var order = new List<string>();
        Action<int> subscribers = _ =>
        {
            order.Add("first");
            throw new InvalidOperationException("first");
        };
        subscribers += _ => order.Add("second");

        SynchronousObserverInvoker.Invoke(
            subscribers, 1, SynchronousObserverSource.SharedStreamRingRetainedBytes);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void TwoArgumentAction_ThrowingFirstSubscriber_InvokesLaterSubscriberInOrder()
    {
        var order = new List<string>();
        Action<int, int> subscribers = (_, _) =>
        {
            order.Add("first");
            throw new InvalidOperationException("first");
        };
        subscribers += (_, _) => order.Add("second");

        SynchronousObserverInvoker.Invoke(
            subscribers, 1, 2, SynchronousObserverSource.ConnectionLimitLearned);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void Snapshot_RemovedSubscriberRunsCurrentPublicationOnly()
    {
        var order = new List<string>();
        EventHandler<EventArgs>? source = null;
        void First(object? _, EventArgs __)
        {
            order.Add("first");
            source -= Second;
        }

        void Second(object? _, EventArgs __) => order.Add("second");

        source += First;
        source += Second;

        SynchronousObserverInvoker.Invoke(
            source, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);
        Assert.Equal(["first", "second"], order);

        order.Clear();
        SynchronousObserverInvoker.Invoke(
            source, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);
        Assert.Equal(["first"], order);
    }

    [Fact]
    public void Snapshot_AddedSubscriberStartsWithNextPublication()
    {
        var order = new List<string>();
        EventHandler<EventArgs>? source = null;
        void Third(object? _, EventArgs __) => order.Add("third");
        void First(object? _, EventArgs __)
        {
            order.Add("first");
            source += Third;
        }

        source += First;
        source += (_, _) => order.Add("second");

        SynchronousObserverInvoker.Invoke(
            source, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);
        Assert.Equal(["first", "second"], order);

        order.Clear();
        SynchronousObserverInvoker.Invoke(
            source, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);
        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void DuplicateRegistration_InvokesEachInvocationListEntry()
    {
        var calls = 0;
        Action<int> handler = _ => calls++;
        var subscribers = handler + handler;

        SynchronousObserverInvoker.Invoke(
            subscribers, 0, SynchronousObserverSource.SharedStreamForceEvictions);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void NullSubscriber_IsANoOp_EventHandler()
    {
        SynchronousObserverInvoker.Invoke<EventArgs>(
            null, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged);
    }

    [Fact]
    public void NullSubscriber_IsANoOp_Action()
    {
        SynchronousObserverInvoker.Invoke<int>(
            null, 0, SynchronousObserverSource.SharedStreamRingRetainedBytes);
    }

    [Fact]
    public void NullSubscriber_IsANoOp_TwoArgumentAction()
    {
        SynchronousObserverInvoker.Invoke<int, int>(
            null, 0, 0, SynchronousObserverSource.ConnectionLimitLearned);
    }

    [Fact]
    public void OutOfMemoryException_PropagatesAndIsNotLoggedAsContained()
    {
        var oom = new OutOfMemoryException("fatal");
        var laterCalled = false;
        EventHandler<EventArgs> subscribers = (_, _) => throw oom;
        subscribers += (_, _) => laterCalled = true;

        var events = CaptureLogs(() =>
        {
            var thrown = Assert.Throws<OutOfMemoryException>(() =>
                SynchronousObserverInvoker.Invoke(
                    subscribers, Sender, EventArgs.Empty, SynchronousObserverSource.ConfigChanged));
            Assert.Same(oom, thrown);
        });

        Assert.False(laterCalled);
        Assert.DoesNotContain(events, IsObserverFailureLog);
    }

    [Fact]
    public void KnownFailureBurst_LogsOneOperatorFacingWarning()
    {
        Action<int> subscribers = _ => throw new TimeoutException("known");
        var events = CaptureLogs(() =>
        {
            for (var i = 0; i < 8; i++)
            {
                SynchronousObserverInvoker.Invoke(
                    subscribers, i, SynchronousObserverSource.ConnectionPoolChanged);
            }
        });

        Assert.Equal(1, events.Count(e => e.Level == LogEventLevel.Warning && IsObserverFailureLog(e)));
    }

    [Fact]
    public void UnexpectedFailure_LogsStackWithoutExceptionMessageOrCallbackArgument()
    {
        Action<string> subscribers = _ => throw new InvalidOperationException(SentinelSecret);
        var events = CaptureLogs(() =>
            SynchronousObserverInvoker.Invoke(
                subscribers, CallbackArgument, SynchronousObserverSource.ConfigChanged));

        var error = Assert.Single(events, e => e.Level == LogEventLevel.Error);
        Assert.True(error.Properties.ContainsKey("ObserverStackTrace"));
        var rendered = Render(error);
        Assert.DoesNotContain(SentinelSecret, rendered);
        Assert.DoesNotContain(CallbackArgument, rendered);
    }

    [Fact]
    public void ResetFailureLogThrottleForTests_AllowsSameSourceToLogAgain()
    {
        Action<int> subscribers = _ => throw new InvalidOperationException("burst");
        var events = CaptureLogs(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                SynchronousObserverInvoker.Invoke(
                    subscribers, i, SynchronousObserverSource.ConnectionLimitLearned);
            }

            SynchronousObserverInvoker.ResetFailureLogThrottleForTests();
            SynchronousObserverInvoker.Invoke(
                subscribers, 0, SynchronousObserverSource.ConnectionLimitLearned);
        });

        Assert.Equal(2, events.Count(e => e.Level == LogEventLevel.Error && IsObserverFailureLog(e)));
    }

    private static bool IsObserverFailureLog(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.Contains("synchronous observer", StringComparison.OrdinalIgnoreCase);

    private static string Render(LogEvent logEvent)
    {
        var rendered = logEvent.RenderMessage();
        foreach (var property in logEvent.Properties)
            rendered += $" {property.Key}={property.Value}";
        if (logEvent.Exception is not null)
            rendered += logEvent.Exception.ToString();
        return rendered;
    }

    private static IReadOnlyList<LogEvent> CaptureLogs(Action act)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            act();
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
