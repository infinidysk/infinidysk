using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class SqliteMaintenanceServiceTests
{
    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_BusyThenSuccess_RetriesOnce()
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var busy = new SqliteException("sqlite contention", 5, 261);

        await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
            _ =>
            {
                calls++;
                if (calls == 1)
                    throw busy;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            delayAsync);

        Assert.Equal(2, calls);
        Assert.Equal([SqliteMaintenanceService.TransientRetryBaseDelay], delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_LockedThenSuccess_RetriesOnce()
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var locked = new SqliteException("sqlite contention", 6, 262);

        await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
            _ =>
            {
                calls++;
                if (calls == 1)
                    throw locked;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            delayAsync);

        Assert.Equal(2, calls);
        Assert.Equal([SqliteMaintenanceService.TransientRetryBaseDelay], delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_WrappedContention_Retries()
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var wrapped = new DbUpdateException(
            "wrapper",
            new SqliteException("sqlite contention", 5, 261));

        await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
            _ =>
            {
                calls++;
                if (calls == 1)
                    throw wrapped;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            delayAsync);

        Assert.Equal(2, calls);
        Assert.Equal([SqliteMaintenanceService.TransientRetryBaseDelay], delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_PersistentContention_StopsAtBudget()
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var busy = new SqliteException("sqlite contention", 5, 261);

        await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
            _ =>
            {
                calls++;
                throw busy;
            },
            CancellationToken.None,
            delayAsync);

        Assert.Equal(SqliteMaintenanceService.MaxTransientRetries + 1, calls);
        Assert.Equal(
            [
                SqliteMaintenanceService.TransientRetryBaseDelay,
                SqliteMaintenanceService.TransientRetryBaseDelay * 2,
                SqliteMaintenanceService.TransientRetryBaseDelay * 3,
            ],
            delays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(19)]
    [InlineData(26)]
    public async Task RunWithSqliteContentionRetryAsync_UnexpectedSqliteError_Propagates(int primaryCode)
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var thrown = new SqliteException("unexpected sqlite error", primaryCode);

        var actual = await Assert.ThrowsAsync<SqliteException>(() =>
            SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ =>
                {
                    calls++;
                    throw thrown;
                },
                CancellationToken.None,
                delayAsync));

        Assert.Same(thrown, actual);
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_CancellationDuringBackoff_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var delayCalls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ =>
                {
                    calls++;
                    throw new SqliteException("sqlite contention", 5, 261);
                },
                cts.Token,
                (_, token) =>
                {
                    delayCalls++;
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }));

        Assert.Equal(1, calls);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_PreCancelledToken_DoesNotInvokeSweep()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ =>
                {
                    calls++;
                    return Task.CompletedTask;
                },
                cts.Token,
                delayAsync));

        Assert.Equal(0, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_OutOfMemory_PropagatesImmediately()
    {
        var calls = 0;
        var (delays, delayAsync) = CreateRecordingDelay();
        var thrown = new OutOfMemoryException("oom");

        var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ =>
                {
                    calls++;
                    throw thrown;
                },
                CancellationToken.None,
                delayAsync));

        Assert.Same(thrown, actual);
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_ContentionWarningIsSingleLineWithoutStack()
    {
        var events = await CaptureLogsAsync(async () =>
        {
            var calls = 0;
            var busy = new SqliteException("database is locked", 5, 261);
            await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ =>
                {
                    calls++;
                    if (calls == 1)
                        throw busy;
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                CreateRecordingDelay().DelayAsync);
        });

        var warning = Assert.Single(
            events,
            e => e.Level == LogEventLevel.Warning
                 && e.MessageTemplate.Text.Contains("deferred by database contention"));
        Assert.Null(warning.Exception);
        var rendered = warning.RenderMessage();
        Assert.Contains("attempt 1/4", rendered, StringComparison.Ordinal);
        Assert.Contains("250", rendered, StringComparison.Ordinal);
        Assert.Contains("database is locked", rendered, StringComparison.Ordinal);
        Assert.True(warning.Properties.ContainsKey("Reason"));
    }

    [Fact]
    public async Task RunWithSqliteContentionRetryAsync_PersistentContention_LogsFirstAndTerminalWarnings()
    {
        var events = await CaptureLogsAsync(async () =>
        {
            var busy = new SqliteException("database is locked", 5, 261);
            await SqliteMaintenanceService.RunWithSqliteContentionRetryAsync(
                _ => throw busy,
                CancellationToken.None,
                CreateRecordingDelay().DelayAsync);
        });

        var warnings = events
            .Where(e => e.Level == LogEventLevel.Warning)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning =>
        {
            Assert.Null(warning.Exception);
            Assert.True(warning.Properties.ContainsKey("Reason"));
        });
        Assert.Contains(
            warnings,
            e => e.MessageTemplate.Text.Contains("deferred by database contention"));
        Assert.Contains(
            warnings,
            e => e.MessageTemplate.Text.Contains("skipped after"));
    }

    private static (List<TimeSpan> Delays, Func<TimeSpan, CancellationToken, Task> DelayAsync) CreateRecordingDelay()
    {
        var delays = new List<TimeSpan>();
        Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        }

        return (delays, DelayAsync);
    }

    private static async Task<IReadOnlyList<LogEvent>> CaptureLogsAsync(Func<Task> action)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();

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
