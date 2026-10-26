using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tasks;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Runs the RemoveUnlinkedFilesTask daily at the configured time when scheduling is enabled.
/// </summary>
public class RemoveOrphanedFilesSchedulerService : BackgroundService
{
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly IDbContextFactory<DavDatabaseContext> _dbContextFactory;
    private CancellationTokenSource _rescheduleCts = new();

    public RemoveOrphanedFilesSchedulerService(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        IDbContextFactory<DavDatabaseContext> dbContextFactory)
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _dbContextFactory = dbContextFactory;

        _configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey(ConfigKeys.MaintenanceRemoveOrphanedScheduleEnabled) &&
                !args.ChangedConfig.ContainsKey(ConfigKeys.MaintenanceRemoveOrphanedScheduleTime))
                return;

            var old = Interlocked.Exchange(ref _rescheduleCts, CreateRescheduleSource());
            old.Cancel();
            // Not disposed: ExecuteAsync may access .Token on this source after the swap, which
            // throws ObjectDisposedException once disposed. Cancelling wakes the loop; the old
            // source is then unreferenced and GC'd.
        };
    }

    private DateTime? _lastLoggedNextRun;
    private DateTime? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Read once: the config handler can swap _rescheduleCts between the field load and
                // the .Token access, leaving us reading .Token off an instance we never observed.
                var reschedule = Volatile.Read(ref _rescheduleCts);

                if (!_configManager.IsRemoveOrphanedFilesScheduleEnabled())
                {
                    // Forget the logged target so re-enabling re-announces the next run, even when
                    // the recomputed target is the same instant as before the disable.
                    _lastLoggedNextRun = null;
                    using var disabledLinked = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                    await Task.Delay(Timeout.Infinite, disabledLinked.Token).ConfigureAwait(false);
                    continue;
                }

                var scheduleTime = _configManager.RemoveOrphanedFilesSchedule();
                var now = DateTime.Now;
                var todayRun = now.Date + scheduleTime;
                // Guard against re-running the same slot after a backward clock shift (DST fall-back
                // or NTP step) re-exposes a time we already ran: treat todayRun as pending only if it
                // is both in the future and strictly later than the last run we fired.
                var lastRun = _lastRun ?? DateTime.MinValue;
                var nextRun = todayRun > now && todayRun > lastRun ? todayRun : todayRun.AddDays(1);

                // Only log when the target actually changes.
                if (_lastLoggedNextRun != nextRun)
                {
                    Log.Information("RemoveOrphanedFilesScheduler: next run scheduled at {NextRun}", nextRun);
                    _lastLoggedNextRun = nextRun;
                }

                using var delayLinked = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                await ScheduleUtil.DelayUntilAsync(nextRun, TimeProvider.System, delayLinked.Token)
                    .ConfigureAwait(false);

                Log.Information("RemoveOrphanedFilesScheduler: running scheduled Remove Orphaned Files task");
                var task = new RemoveUnlinkedFilesTask(
                    _configManager,
                    _websocketManager,
                    isDryRun: false,
                    createContext: () => _dbContextFactory.CreateDbContext());
                var executed = await task.Execute().ConfigureAwait(false);
                if (!executed)
                {
                    // BaseTask's single-flight slot is shared across all maintenance tasks.
                    // Do not mark the slot as completed — retry shortly so a concurrent
                    // backup/strm task does not silently skip the day's run.
                    Log.Warning(
                        "RemoveOrphanedFilesScheduler: another maintenance task is running; " +
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
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (OperationCanceledException)
            {
                // Config changed — loop and recompute the next run time
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack("RemoveOrphanedFilesScheduler: error running scheduled task");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
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
