using System.Collections.Concurrent;
using NzbWebDAV.Tasks;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(GlobalLoggerCollection))]
public class ProgressHeartbeatTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(1);

    private const string PayloadSecret = "nzbdav-sentinel-payload-9f3a";
    private const string MessageSecret = "nzbdav-sentinel-message-c21b";
    private const string InnerSecret = "nzbdav-sentinel-inner-a44e";
    private const string DataKeySecret = "nzbdav-sentinel-datakey-7d0c";
    private const string DataValueSecret = "nzbdav-sentinel-datavalue-b8e1";
    private const string PosixPath = "/var/secret-nzbdav/heartbeat-leak.sqlite";
    private const string WindowsPath = @"C:\Users\secret-nzbdav\heartbeat-leak.sqlite";

    private static readonly string[] Sentinels =
    [
        PayloadSecret,
        MessageSecret,
        InnerSecret,
        DataKeySecret,
        DataValueSecret,
        PosixPath,
        WindowsPath,
    ];

    [Fact]
    public async Task ReportsElapsedUntilCompleted_UsingControllableTime()
    {
        var messages = new ConcurrentQueue<string>();
        var clock = new ControllableTimeProvider();
        await using var heartbeat = new ProgressHeartbeat(
            messages.Enqueue,
            Interval,
            ProgressHeartbeatOperation.RemoveUnlinkedFiles,
            clock);

        heartbeat.StartPhase("Scanning all linked files...\nFound 79976...");

        Assert.True(messages.TryPeek(out var startMessage));
        Assert.StartsWith("Scanning all linked files...\nFound 79976...", startMessage);
        Assert.Contains("Elapsed: 1s", startMessage, StringComparison.Ordinal);

        clock.Advance(Interval);
        var afterHeartbeat = messages.ToArray();
        Assert.Equal(2, afterHeartbeat.Length);
        Assert.Contains("Elapsed: 2s", afterHeartbeat[1], StringComparison.Ordinal);
        Assert.StartsWith("Scanning all linked files...\nFound 79976...", afterHeartbeat[1]);

        heartbeat.UpdatePhase("Scanning all linked files...\nFound 79977...");
        var afterUpdate = messages.ToArray();
        Assert.Equal(3, afterUpdate.Length);
        Assert.StartsWith("Scanning all linked files...\nFound 79977...", afterUpdate[^1]);
        Assert.Contains("Elapsed: 2s", afterUpdate[^1], StringComparison.Ordinal);

        heartbeat.Complete("Done.");
        clock.Advance(TimeSpan.FromSeconds(10));
        heartbeat.UpdatePhase("Scanning all linked files...\nFound 79978...");

        var snapshot = messages.ToArray();
        Assert.Equal("Done.", snapshot[^1]);
        Assert.Equal(1, snapshot.Count(message => message == "Done."));
        Assert.DoesNotContain("Elapsed:", snapshot[^1]);
        Assert.Equal(4, snapshot.Length);
    }

    [Fact]
    public async Task ScheduledReporterFailure_IsContained_AndLaterReportsContinue()
    {
        var invocation = 0;
        var messages = new ConcurrentQueue<string>();
        void Report(string message)
        {
            if (Interlocked.Increment(ref invocation) == 2)
                throw new InvalidOperationException("scheduled reporter failure");
            messages.Enqueue(message);
        }

        var clock = new ControllableTimeProvider();
        await using var heartbeat = new ProgressHeartbeat(
            Report,
            Interval,
            ProgressHeartbeatOperation.RemoveUnlinkedFiles,
            clock);

        heartbeat.StartPhase("Scanning all linked files...\nFound 79976...");
        Assert.Null(Record.Exception(() => clock.Advance(Interval)));

        heartbeat.UpdatePhase("Scanning all linked files...\nFound 79977...");
        heartbeat.Complete("Done.");

        var snapshot = messages.ToArray();
        Assert.StartsWith("Scanning all linked files...\nFound 79977...", snapshot[^2]);
        Assert.Equal("Done.", snapshot[^1]);
        Assert.DoesNotContain("Elapsed:", snapshot[^1]);

        var invocationsAfterComplete = invocation;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(invocationsAfterComplete, invocation);
    }

    [Fact]
    public async Task ImmediateAndTerminalReporterFailures_DoNotEscape()
    {
        var invocation = 0;
        void Report(string _)
        {
            Interlocked.Increment(ref invocation);
            throw new InvalidOperationException("always-throw reporter");
        }

        var clock = new ControllableTimeProvider();
        await using var heartbeat = new ProgressHeartbeat(
            Report,
            Interval,
            ProgressHeartbeatOperation.PruneCompletedHistory,
            clock);

        Assert.Null(Record.Exception(() => heartbeat.StartPhase("Counting completed history items...")));
        Assert.Null(Record.Exception(() => heartbeat.UpdatePhase("Counting completed history items...\nFound 1...")));
        Assert.Null(Record.Exception(() => heartbeat.Complete("Done.")));
        Assert.Equal(3, invocation);

        Assert.Null(Record.Exception(() => heartbeat.UpdatePhase("later")));
        Assert.Null(Record.Exception(() => clock.Advance(TimeSpan.FromSeconds(10))));
        Assert.Equal(3, invocation);

        var armedInvocations = 0;
        void ArmedReport(string _)
        {
            Interlocked.Increment(ref armedInvocations);
            throw new InvalidOperationException("always-throw reporter");
        }

        var armedClock = new ControllableTimeProvider();
        await using var armed = new ProgressHeartbeat(
            ArmedReport,
            Interval,
            ProgressHeartbeatOperation.PruneCompletedHistory,
            armedClock);

        Assert.Null(Record.Exception(() => armed.StartPhase("Counting completed history items...")));
        Assert.Equal(1, armedInvocations);
        Assert.Null(Record.Exception(() => armedClock.Advance(Interval)));
        Assert.Equal(2, armedInvocations);
        Assert.Null(Record.Exception(() => armed.Complete("Done.")));
        Assert.Equal(3, armedInvocations);
    }

    [Fact]
    public async Task CompleteRacingWithHeartbeat_ReportsTerminalOnceWithoutOverlap()
    {
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var invocation = 0;
        var activeReporters = 0;
        var maxActiveReporters = 0;
        var overlap = 0;

        void Report(string message)
        {
            var current = Interlocked.Increment(ref activeReporters);
            if (current > 1)
                Interlocked.Exchange(ref overlap, 1);
            int snapshot;
            do
            {
                snapshot = maxActiveReporters;
                if (current <= snapshot) break;
            } while (Interlocked.CompareExchange(ref maxActiveReporters, current, snapshot) != snapshot);

            try
            {
                var n = Interlocked.Increment(ref invocation);
                messages.Enqueue(message);
                if (n != 2) return;
                heartbeatEntered.TrySetResult();
                releaseHeartbeat.Task.GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref activeReporters);
            }
        }

        var clock = new ControllableTimeProvider();
        await using var heartbeat = new ProgressHeartbeat(
            Report,
            Interval,
            ProgressHeartbeatOperation.RemoveUnlinkedFiles,
            clock);

        heartbeat.StartPhase("Scanning all linked files...\nFound 79976...");

        var advanceTask = Task.Run(() => clock.Advance(Interval));
        try
        {
            await heartbeatEntered.Task.WaitAsync(DeadlockGuard);
            var completeTask = Task.Run(() =>
                heartbeat.Complete("Failed: nzbdav is shutting down"));
            Assert.False(completeTask.IsCompleted);
            releaseHeartbeat.TrySetResult();
            await advanceTask.WaitAsync(DeadlockGuard);
            await completeTask.WaitAsync(DeadlockGuard);

            Assert.Equal(0, overlap);
            Assert.Equal(1, maxActiveReporters);

            var snapshot = messages.ToArray();
            Assert.Equal(1, snapshot.Count(message => message == "Failed: nzbdav is shutting down"));
            Assert.Equal("Failed: nzbdav is shutting down", snapshot[^1]);
            var scheduledIndex = Array.FindLastIndex(
                snapshot,
                message => message.Contains("Elapsed:", StringComparison.Ordinal));
            var terminalIndex = Array.FindIndex(
                snapshot,
                message => message == "Failed: nzbdav is shutting down");
            Assert.True(scheduledIndex >= 0 && scheduledIndex < terminalIndex);

            var count = snapshot.Length;
            clock.Advance(TimeSpan.FromSeconds(10));
            heartbeat.UpdatePhase("should not report");
            Assert.Equal(count, messages.Count);
        }
        finally
        {
            releaseHeartbeat.TrySetResult();
            await advanceTask.WaitAsync(DeadlockGuard);
        }
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightHeartbeat_AndPreventsStaleReports()
    {
        var heartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var invocation = 0;
        var activeReporters = 0;
        var maxActiveReporters = 0;
        var overlap = 0;

        void Report(string message)
        {
            var current = Interlocked.Increment(ref activeReporters);
            if (current > 1)
                Interlocked.Exchange(ref overlap, 1);
            int snapshot;
            do
            {
                snapshot = maxActiveReporters;
                if (current <= snapshot) break;
            } while (Interlocked.CompareExchange(ref maxActiveReporters, current, snapshot) != snapshot);

            try
            {
                var n = Interlocked.Increment(ref invocation);
                messages.Enqueue(message);
                if (n != 2) return;
                heartbeatEntered.TrySetResult();
                releaseHeartbeat.Task.GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref activeReporters);
            }
        }

        var clock = new ControllableTimeProvider();
        var heartbeat = new ProgressHeartbeat(
            Report,
            Interval,
            ProgressHeartbeatOperation.RemoveUnlinkedFiles,
            clock);

        heartbeat.StartPhase("Scanning all linked files...\nFound 79976...");

        var advanceTask = Task.Run(() => clock.Advance(Interval));
        Task? disposeTask = null;
        try
        {
            await heartbeatEntered.Task.WaitAsync(DeadlockGuard);
            disposeTask = Task.Run(async () =>
                await heartbeat.DisposeAsync().ConfigureAwait(false));
            Assert.False(disposeTask.IsCompleted);
            releaseHeartbeat.TrySetResult();
            await advanceTask.WaitAsync(DeadlockGuard);
            await disposeTask.WaitAsync(DeadlockGuard);

            Assert.Equal(0, overlap);
            Assert.Equal(1, maxActiveReporters);

            var count = messages.Count;
            clock.Advance(TimeSpan.FromSeconds(10));
            heartbeat.UpdatePhase("stale");
            heartbeat.Complete("Done.");
            Assert.Equal(count, messages.Count);
        }
        finally
        {
            releaseHeartbeat.TrySetResult();
            await advanceTask.WaitAsync(DeadlockGuard);
            if (disposeTask is not null)
                await disposeTask.WaitAsync(DeadlockGuard);
            else
                await heartbeat.DisposeAsync();
        }
    }

    [Fact]
    public async Task KnownReporterFailure_LogsTypeOnlyWithoutSentinels()
    {
        var exception = CreateSecretException(
            (message, inner) => new IOException(message, inner));
        var events = await CaptureLogsAsync(async () =>
        {
            var clock = new ControllableTimeProvider();
            await using var heartbeat = new ProgressHeartbeat(
                _ => throw exception,
                Interval,
                ProgressHeartbeatOperation.PruneCompletedHistory,
                clock);

            heartbeat.StartPhase($"Scanning {PayloadSecret}\n{PosixPath}\n{WindowsPath}");
            clock.Advance(Interval);
            heartbeat.UpdatePhase($"Updating {PayloadSecret}\n{PosixPath}\n{WindowsPath}");
            heartbeat.Complete($"Done. {PayloadSecret}");
        });

        var warning = Assert.Single(
            events,
            logEvent => IsReporterFailureWarning(logEvent)
                && PropertyText(logEvent, "Operation") == nameof(ProgressHeartbeatOperation.PruneCompletedHistory)
                && PropertyText(logEvent, "ReportSource") == "StartPhase"
                && PropertyText(logEvent, "ExceptionType") == typeof(IOException).FullName);
        AssertReporterFailureEvent(
            warning,
            expectedOperation: nameof(ProgressHeartbeatOperation.PruneCompletedHistory),
            expectedSource: "StartPhase",
            expectedExceptionType: typeof(IOException).FullName!);
    }

    [Fact]
    public async Task UnexpectedReporterFailure_LogsTypeOnlyWithoutSentinels()
    {
        var exception = CreateSecretException(
            (message, inner) => new InvalidOperationException(message, inner));
        var invocation = 0;
        var events = await CaptureLogsAsync(async () =>
        {
            var clock = new ControllableTimeProvider();
            await using var heartbeat = new ProgressHeartbeat(
                _ =>
                {
                    if (Interlocked.Increment(ref invocation) == 1)
                        return;
                    throw exception;
                },
                Interval,
                ProgressHeartbeatOperation.RemoveUnlinkedFiles,
                clock);

            heartbeat.StartPhase($"Scanning {PayloadSecret}\n{PosixPath}\n{WindowsPath}");
            clock.Advance(Interval);
            heartbeat.UpdatePhase($"Updating {PayloadSecret}\n{PosixPath}\n{WindowsPath}");
            heartbeat.Complete($"Done. {PayloadSecret}");
        });

        var warning = Assert.Single(
            events,
            logEvent => IsReporterFailureWarning(logEvent)
                && PropertyText(logEvent, "Operation") == nameof(ProgressHeartbeatOperation.RemoveUnlinkedFiles)
                && PropertyText(logEvent, "ReportSource") == "ScheduledHeartbeat"
                && PropertyText(logEvent, "ExceptionType") == typeof(InvalidOperationException).FullName);
        AssertReporterFailureEvent(
            warning,
            expectedOperation: nameof(ProgressHeartbeatOperation.RemoveUnlinkedFiles),
            expectedSource: "ScheduledHeartbeat",
            expectedExceptionType: typeof(InvalidOperationException).FullName!);
    }

    [Fact]
    public async Task ReporterFailureThrottle_IsPerInstance_AndNeedsNoGlobalReset()
    {
        var events = await CaptureLogsAsync(async () =>
        {
            var clock = new ControllableTimeProvider();
            await using var first = new ProgressHeartbeat(
                _ => throw new PerInstanceThrottleException(),
                Interval,
                ProgressHeartbeatOperation.PruneCompletedHistory,
                clock);
            first.StartPhase("first-a");
            first.UpdatePhase("first-b");
            first.Complete("first-done");

            await using var second = new ProgressHeartbeat(
                _ => throw new PerInstanceThrottleException(),
                Interval,
                ProgressHeartbeatOperation.RemoveMissingPayloads,
                clock);
            second.StartPhase("second-a");
            second.UpdatePhase("second-b");
            second.Complete("second-done");
        });

        var warnings = events
            .Where(logEvent =>
                IsReporterFailureWarning(logEvent)
                && PropertyText(logEvent, "ExceptionType") == typeof(PerInstanceThrottleException).FullName)
            .ToArray();
        Assert.Equal(2, warnings.Length);
        AssertReporterFailureEvent(
            warnings[0],
            expectedOperation: nameof(ProgressHeartbeatOperation.PruneCompletedHistory),
            expectedSource: "StartPhase",
            expectedExceptionType: typeof(PerInstanceThrottleException).FullName!,
            sentinels: []);
        AssertReporterFailureEvent(
            warnings[1],
            expectedOperation: nameof(ProgressHeartbeatOperation.RemoveMissingPayloads),
            expectedSource: "StartPhase",
            expectedExceptionType: typeof(PerInstanceThrottleException).FullName!,
            sentinels: []);
    }

    [Fact]
    public async Task OutOfMemoryException_IsNotContained()
    {
        var oom = new OutOfMemoryException("pre-created");
        var heartbeat = new ProgressHeartbeat(
            _ => throw oom,
            Interval,
            ProgressHeartbeatOperation.PruneCompletedHistory);
        try
        {
            var thrown = Assert.Throws<OutOfMemoryException>(
                () => heartbeat.StartPhase("Counting completed history items..."));
            Assert.Same(oom, thrown);
        }
        finally
        {
            await heartbeat.DisposeAsync();
        }
    }

    [Fact]
    public void Constructor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProgressHeartbeat(_ => { }, TimeSpan.Zero, ProgressHeartbeatOperation.PruneCompletedHistory));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProgressHeartbeat(
                _ => { },
                Timeout.InfiniteTimeSpan,
                ProgressHeartbeatOperation.RemoveUnlinkedFiles));
    }

    private static TException CreateSecretException<TException>(
        Func<string, Exception, TException> factory)
        where TException : Exception
    {
        var inner = new IOException($"{InnerSecret} {PosixPath} {WindowsPath}");
        var exception = factory($"{MessageSecret} {PosixPath} {WindowsPath}", inner);
        exception.Data[DataKeySecret] = DataValueSecret;
        return exception;
    }

    private static bool IsReporterFailureWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.MessageTemplate.Text.Contains(
            "Progress reporting failed; maintenance continues.",
            StringComparison.Ordinal);

    private static void AssertReporterFailureEvent(
        LogEvent logEvent,
        string expectedOperation,
        string expectedSource,
        string expectedExceptionType,
        string[]? sentinels = null)
    {
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
        Assert.Null(logEvent.Exception);
        Assert.Contains(
            "Progress reporting failed; maintenance continues.",
            logEvent.MessageTemplate.Text,
            StringComparison.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Operation",
                "ReportSource",
                "ExceptionType",
            },
            logEvent.Properties.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.False(logEvent.Properties.ContainsKey("Reason"));
        Assert.False(logEvent.Properties.ContainsKey("Stack"));
        Assert.False(logEvent.Properties.ContainsKey("Payload"));
        Assert.False(logEvent.Properties.ContainsKey("Exception"));

        Assert.Equal(expectedOperation, PropertyText(logEvent, "Operation"));
        Assert.Equal(expectedSource, PropertyText(logEvent, "ReportSource"));
        Assert.Equal(expectedExceptionType, PropertyText(logEvent, "ExceptionType"));

        foreach (var sentinel in sentinels ?? Sentinels)
            AssertEventHasNoSentinel(logEvent, sentinel);
    }

    private static void AssertEventHasNoSentinel(LogEvent logEvent, string sentinel)
    {
        Assert.DoesNotContain(sentinel, logEvent.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, logEvent.RenderMessage(), StringComparison.Ordinal);
        foreach (var (name, value) in logEvent.Properties)
        {
            Assert.DoesNotContain(sentinel, name, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, FormatPropertyValue(value), StringComparison.Ordinal);
        }

        if (logEvent.Exception is { } exception)
        {
            Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, exception.GetType().FullName ?? "", StringComparison.Ordinal);
        }
    }

    private static string PropertyText(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value))
            return "";
        return value is ScalarValue { Value: { } raw }
            ? raw.ToString() ?? ""
            : value.ToString();
    }

    private static string FormatPropertyValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: { } raw } => raw.ToString() ?? "",
        ScalarValue => "",
        SequenceValue sequence => string.Join(",", sequence.Elements.Select(FormatPropertyValue)),
        DictionaryValue dictionary => string.Join(
            ",",
            dictionary.Elements.Select(pair =>
                $"{FormatPropertyValue(pair.Key)}={FormatPropertyValue(pair.Value)}")),
        StructureValue structure => string.Join(
            ",",
            structure.Properties.Select(property =>
                $"{property.Name}={FormatPropertyValue(property.Value)}")),
        _ => value.ToString(),
    };

    private static async Task<IReadOnlyList<LogEvent>> CaptureLogsAsync(Func<Task> action)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Log.Logger = logger;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class PerInstanceThrottleException : Exception;

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
