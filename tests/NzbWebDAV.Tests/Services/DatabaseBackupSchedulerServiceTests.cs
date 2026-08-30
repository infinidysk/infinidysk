using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseBackupSchedulerServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string? _previousConfigPath;
    private readonly List<string> _tempRoots = [];

    public DatabaseBackupSchedulerServiceTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    }

    [Fact]
    public async Task StartAsync_MissingBackupsDirectory_CreatesDirectoryWithoutWarning()
    {
        var configRoot = CreateConfigRoot();
        var store = new DatabaseBackupStore();
        var observer = new InitObserver();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var service = new DatabaseBackupSchedulerService(
            new ConfigManager(),
            new WebsocketManager(),
            store,
            TimeProvider.System,
            observer.Observe);

        try
        {
            await service.StartAsync(CancellationToken.None).WaitAsync(Timeout);
            await observer.Succeeded.Task.WaitAsync(Timeout);

            Assert.True(Directory.Exists(store.BackupsRoot));
            Assert.Equal(1, observer.AttemptCount);
            Assert.Equal(1, observer.SuccessCount);
            Assert.Equal(0, observer.RetryCount);
            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsFaulted);
            Assert.Empty(InitializationWarnings(sink.Events, store.BackupsRoot));
            Assert.StartsWith(configRoot, store.BackupsRoot);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task StartAsync_BackupsPathIsFile_RetriesAndRecoversAfterRepair()
    {
        var configRoot = CreateConfigRoot();
        var blocker = Path.Join(configRoot, "backups");
        File.WriteAllText(blocker, "blocks Directory.CreateDirectory");

        var clock = new ControllableTimeProvider();
        var store = new DatabaseBackupStore();
        var observer = new InitObserver();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var service = new DatabaseBackupSchedulerService(
            new ConfigManager(),
            new WebsocketManager(),
            store,
            clock,
            observer.Observe);

        try
        {
            await service.StartAsync(CancellationToken.None).WaitAsync(Timeout);
            await observer.RetryScheduled.Task.WaitAsync(Timeout);

            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.False(service.ExecuteTask.IsFaulted);

            var warning = Assert.Single(InitializationWarnings(sink.Events, store.BackupsRoot));
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.Null(warning.Exception);
            var reason = warning.Properties["Reason"].LiteralValue() as string;
            Assert.False(string.IsNullOrWhiteSpace(reason));

            File.Delete(blocker);
            clock.Advance(DatabaseBackupSchedulerService.ErrorRetryDelay - TimeSpan.FromMilliseconds(1));
            Assert.False(Directory.Exists(store.BackupsRoot));
            Assert.Equal(1, observer.AttemptCount);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            await observer.Succeeded.Task.WaitAsync(Timeout);

            Assert.Equal(2, observer.AttemptCount);
            Assert.Equal(1, observer.RetryCount);
            Assert.True(Directory.Exists(store.BackupsRoot));
            Assert.False(service.ExecuteTask.IsFaulted);

            var recovery = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Information
                     && e.MessageTemplate.Text.Contains("initialized after")
                     && Equals(e.Properties["BackupPath"].LiteralValue(), store.BackupsRoot));
            var failureCount = Assert.IsType<int>(recovery.Properties["FailureCount"].LiteralValue());
            Assert.True(failureCount > 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task StopAsync_WhileInitializationRetryIsPending_DoesNotHangOrLogCancellation()
    {
        var configRoot = CreateConfigRoot();
        var blocker = Path.Join(configRoot, "backups");
        File.WriteAllText(blocker, "blocks Directory.CreateDirectory");

        var clock = new ControllableTimeProvider();
        var store = new DatabaseBackupStore();
        var observer = new InitObserver();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var service = new DatabaseBackupSchedulerService(
            new ConfigManager(),
            new WebsocketManager(),
            store,
            clock,
            observer.Observe);

        try
        {
            await service.StartAsync(CancellationToken.None).WaitAsync(Timeout);
            await observer.RetryScheduled.Task.WaitAsync(Timeout);

            Assert.Equal(1, observer.AttemptCount);
            Assert.Equal(1, observer.RetryCount);
            Assert.Single(InitializationWarnings(sink.Events, store.BackupsRoot));

            await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

            Assert.True(service.ExecuteTask!.IsCanceled);
            Assert.False(service.ExecuteTask.IsFaulted);
            Assert.Equal(1, observer.AttemptCount);
            Assert.Equal(1, observer.RetryCount);
            Assert.Equal(0, observer.SuccessCount);
            Assert.Single(InitializationWarnings(sink.Events, store.BackupsRoot));
            Assert.DoesNotContain(
                sink.Events,
                e => e.Exception is OperationCanceledException
                     || (e.Level == LogEventLevel.Warning
                         && e.MessageTemplate.Text.Contains("cannot initialize backup directory")
                         && e.Exception is OperationCanceledException));
        }
        finally
        {
            if (service.ExecuteTask is { IsCompleted: false })
                await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task PersistentInitializationFailure_ThrottlesWarningsButContinuesRetrying()
    {
        var configRoot = CreateConfigRoot();
        var blocker = Path.Join(configRoot, "backups");
        File.WriteAllText(blocker, "blocks Directory.CreateDirectory");

        var clock = new ControllableTimeProvider();
        var store = new DatabaseBackupStore();
        var observer = new InitObserver();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var service = new DatabaseBackupSchedulerService(
            new ConfigManager(),
            new WebsocketManager(),
            store,
            clock,
            observer.Observe);

        try
        {
            await service.StartAsync(CancellationToken.None).WaitAsync(Timeout);
            await observer.RetryScheduled.Task.WaitAsync(Timeout);
            Assert.Single(InitializationWarnings(sink.Events, store.BackupsRoot));

            for (var i = 0; i < 3; i++)
            {
                observer.ArmNextRetry();
                clock.Advance(DatabaseBackupSchedulerService.ErrorRetryDelay);
                await observer.RetryScheduled.Task.WaitAsync(Timeout);
            }

            Assert.True(observer.AttemptCount > 1);
            Assert.Equal(observer.AttemptCount, observer.RetryCount);
            Assert.Single(InitializationWarnings(sink.Events, store.BackupsRoot));

            observer.ArmNextRetry();
            clock.Advance(DatabaseBackupSchedulerService.InitializationWarningInterval);
            await observer.RetryScheduled.Task.WaitAsync(Timeout);

            var warnings = InitializationWarnings(sink.Events, store.BackupsRoot);
            Assert.Equal(2, warnings.Count);
            var reminder = warnings[^1];
            Assert.Null(reminder.Exception);
            var suppressed = Assert.IsType<int>(reminder.Properties["Suppressed"].LiteralValue());
            Assert.True(suppressed > 0);

            File.Delete(blocker);
            clock.Advance(DatabaseBackupSchedulerService.ErrorRetryDelay);
            await observer.Succeeded.Task.WaitAsync(Timeout);

            Assert.True(Directory.Exists(store.BackupsRoot));
            Assert.False(service.ExecuteTask!.IsFaulted);
            var recovery = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Information
                     && e.MessageTemplate.Text.Contains("initialized after")
                     && Equals(e.Properties["BackupPath"].LiteralValue(), store.BackupsRoot));
            var failureCount = Assert.IsType<int>(recovery.Properties["FailureCount"].LiteralValue());
            Assert.True(failureCount > 0);
            Assert.Equal(observer.AttemptCount - 1, failureCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            Log.Logger = previous;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // best effort cleanup
            }
        }
    }

    private string CreateConfigRoot()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-backup-scheduler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        return root;
    }

    private static List<LogEvent> InitializationWarnings(IReadOnlyList<LogEvent> events, string backupPath) =>
        events.Where(e =>
                e.Level == LogEventLevel.Warning
                && e.MessageTemplate.Text.Contains("cannot initialize backup directory")
                && Equals(e.Properties.GetValueOrDefault("BackupPath")?.LiteralValue(), backupPath))
            .ToList();

    private sealed class InitObserver
    {
        private int _attempts;
        private int _retries;
        private int _successes;
        private TaskCompletionSource _retryScheduled = NewTcs();

        public int AttemptCount => Volatile.Read(ref _attempts);
        public int RetryCount => Volatile.Read(ref _retries);
        public int SuccessCount => Volatile.Read(ref _successes);
        public TaskCompletionSource RetryScheduled => _retryScheduled;
        public TaskCompletionSource Succeeded { get; } = NewTcs();

        public void Observe(DatabaseBackupSchedulerService.InitializationEvent state)
        {
            switch (state)
            {
                case DatabaseBackupSchedulerService.InitializationEvent.AttemptStarted:
                    Interlocked.Increment(ref _attempts);
                    break;
                case DatabaseBackupSchedulerService.InitializationEvent.RetryScheduled:
                    Interlocked.Increment(ref _retries);
                    _retryScheduled.TrySetResult();
                    break;
                case DatabaseBackupSchedulerService.InitializationEvent.Succeeded:
                    Interlocked.Increment(ref _successes);
                    Succeeded.TrySetResult();
                    break;
            }
        }

        public void ArmNextRetry() =>
            _retryScheduled = NewTcs();

        private static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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

file static class DatabaseBackupSchedulerLogExtensions
{
    public static object? LiteralValue(this LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value : value.ToString();
}
