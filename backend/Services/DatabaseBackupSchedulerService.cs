using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Runs a database backup daily at the configured local time when scheduling is enabled.
/// </summary>
public class DatabaseBackupSchedulerService : BackgroundService
{
    internal static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan InitializationWarningInterval = TimeSpan.FromMinutes(5);

    internal enum InitializationEvent
    {
        AttemptStarted,
        Succeeded,
        RetryScheduled,
    }

    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly DatabaseBackupStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Action<InitializationEvent>? _initializationObserver;
    private CancellationTokenSource _rescheduleCts = new();
    // NOTE: swapped-out instances are intentionally not disposed (ExecuteAsync may
    // still read .Token after the swap); the current instance is disposed in Dispose.

    private static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMinutes(30);
    private DateTime? _lastLoggedNextRun;
    private DateTime? _lastRun;
    private DateTimeOffset? _nextInitializationWarningAt;
    private int _initializationFailureCount;
    private int _suppressedInitializationWarnings;

    public DatabaseBackupSchedulerService(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        DatabaseBackupStore store)
        : this(configManager, websocketManager, store, TimeProvider.System, null)
    {
    }

    internal DatabaseBackupSchedulerService(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        DatabaseBackupStore store,
        TimeProvider timeProvider,
        Action<InitializationEvent>? initializationObserver = null)
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _store = store;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _initializationObserver = initializationObserver;

        _configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey(ConfigKeys.BackupScheduleEnabled) &&
                !args.ChangedConfig.ContainsKey(ConfigKeys.BackupScheduleTime))
                return;

            var old = Interlocked.Exchange(ref _rescheduleCts, CreateRescheduleSource());
            old.Cancel();
            // Not disposed: ExecuteAsync may access .Token on this source after the swap, which
            // throws ObjectDisposedException once disposed. Cancelling wakes the loop; the old
            // source is then unreferenced and GC'd.
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var storeInitialized = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!storeInitialized)
                {
                    _initializationObserver?.Invoke(InitializationEvent.AttemptStarted);
                    _store.EnsureInitialized();
                    storeInitialized = true;
                    LogInitializationRecoveryIfNeeded();
                    _initializationObserver?.Invoke(InitializationEvent.Succeeded);
                }

                var reschedule = Volatile.Read(ref _rescheduleCts);

                if (!_configManager.IsDatabaseBackupScheduleEnabled())
                {
                    _lastLoggedNextRun = null;
                    using var disabledLinked = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                    await Task.Delay(Timeout.Infinite, disabledLinked.Token).ConfigureAwait(false);
                    continue;
                }

                var scheduleTime = _configManager.DatabaseBackupSchedule();
                var now = DateTime.Now;
                var todayRun = now.Date + scheduleTime;
                var lastRun = _lastRun ?? DateTime.MinValue;
                var nextRun = todayRun > now && todayRun > lastRun ? todayRun : todayRun.AddDays(1);
                var delay = nextRun - now;

                if (_lastLoggedNextRun != nextRun)
                {
                    Log.Information("DatabaseBackupScheduler: next run scheduled at {NextRun}", nextRun);
                    _lastLoggedNextRun = nextRun;
                }

                using var delayLinked = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                await Task.Delay(delay < MaxSleepSlice ? delay : MaxSleepSlice, delayLinked.Token)
                    .ConfigureAwait(false);

                if (DateTime.Now < nextRun) continue;

                Log.Information("DatabaseBackupScheduler: running scheduled database backup");
                var task = new DatabaseBackupTask(
                    _configManager,
                    _websocketManager,
                    _store,
                    DatabaseBackupKinds.Scheduled);
                var executed = await task.Execute().ConfigureAwait(false);
                if (!executed)
                {
                    // BaseTask's single-flight slot is shared across all maintenance tasks.
                    // Do not mark the slot as completed — retry shortly so a concurrent
                    // orphaned-files/strm task does not silently skip the day's run.
                    Log.Warning(
                        "DatabaseBackupScheduler: another maintenance task is running; " +
                        "will retry in 5 minutes");
                    using var retryLinked = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                    await Task.Delay(TimeSpan.FromMinutes(5), retryLinked.Token).ConfigureAwait(false);
                    continue;
                }

                _lastRun = nextRun;
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Config changed — loop and recompute the next run time
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var initializationFailed = !storeInitialized;
                if (initializationFailed)
                    LogInitializationFailure(e);
                else
                    e.LogWarningKnownOrStack(
                        "DatabaseBackupScheduler: error running scheduled task");

                var retryDelay = Task.Delay(ErrorRetryDelay, _timeProvider, stoppingToken);
                if (initializationFailed)
                    _initializationObserver?.Invoke(InitializationEvent.RetryScheduled);
                await retryDelay.ConfigureAwait(false);
            }
        }
    }

    private void LogInitializationFailure(Exception exception)
    {
        _initializationFailureCount++;
        var now = _timeProvider.GetUtcNow();
        if (_nextInitializationWarningAt is { } next && now < next)
        {
            _suppressedInitializationWarnings++;
            return;
        }

        var suppressed = _suppressedInitializationWarnings;
        _suppressedInitializationWarnings = 0;
        _nextInitializationWarningAt = now + InitializationWarningInterval;

        exception.LogWarningKnownOrStack(
            "DatabaseBackupScheduler: cannot initialize backup directory {BackupPath}; " +
            "scheduled backups are paused and initialization will retry; " +
            "{Suppressed} repeat(s) suppressed",
            _store.BackupsRoot,
            suppressed);
    }

    private void LogInitializationRecoveryIfNeeded()
    {
        if (_initializationFailureCount == 0)
            return;

        Log.Information(
            "DatabaseBackupScheduler: backup directory {BackupPath} initialized after " +
            "{FailureCount} failed attempt(s)",
            _store.BackupsRoot,
            _initializationFailureCount);

        _nextInitializationWarningAt = null;
        _initializationFailureCount = 0;
        _suppressedInitializationWarnings = 0;
    }

    public override void Dispose()
    {
        // ExecuteAsync has stopped when the host disposes the service, so the
        // current reschedule source is safe to dispose. Swapped-out instances
        // are intentionally leaked (see the swap note in ExecuteAsync).
        _rescheduleCts.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    // Returned so Interlocked.Exchange is not a local allocation; swapped sources
    // must stay undisposed while ExecuteAsync may still read .Token.
    private static CancellationTokenSource CreateRescheduleSource() => new();
}
