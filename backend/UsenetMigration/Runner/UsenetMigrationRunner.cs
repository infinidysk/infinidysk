using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.UsenetMigration.Symlinks;
using NzbWebDAV.Config;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Runner;

/// <summary>
/// The migration's single <see cref="BackgroundService"/> — the state machine that
/// drives the wizard forward from the session's <c>Status</c>. The API only
/// flips <c>Status</c>; this loop performs the actual work, so a process restart
/// resumes wherever the status left off:
/// <list type="bullet">
/// <item><c>scanning</c> → runs a full scan (idempotent; rebuilds all artifacts).</item>
/// <item><c>running</c> → submits up to the queue-depth gate, then reconciles;
///   marks <c>complete</c> when no submission remains in flight.</item>
/// <item><c>paused</c> → reconciles in-flight submissions only, submits nothing.</item>
/// </list>
/// Owns the scan runner, worker pool, and reconciler; nothing else touches the
/// submit/reconcile cadence.
/// </summary>
public sealed class UsenetMigrationRunner : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);

    private readonly UsenetMigrationStore _store;
    private readonly ConfigManager _configManager;
    private readonly AltmountScanRunner _scanRunner;
    private readonly SubmissionWorkerPool _workerPool;
    private readonly SubmissionReconciler _reconciler;
    private readonly SymlinkPlanner _symlinkPlanner;
    private readonly SymlinkRewriter _symlinkRewriter;

    public UsenetMigrationRunner(
        UsenetMigrationStore store,
        QueueManager queueManager,
        ConfigManager configManager,
        WebsocketManager websocketManager)
    {
        _store = store;
        _configManager = configManager;
        _scanRunner = new AltmountScanRunner(store, configManager);
        _workerPool = new SubmissionWorkerPool(store, queueManager, configManager, websocketManager);
        _reconciler = new SubmissionReconciler(store);
        _symlinkPlanner = new SymlinkPlanner(store, configManager);
        _symlinkRewriter = new SymlinkRewriter(store);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stay dormant until an existing migration DB is present or an
        // authenticated migration API request creates it on first use.
        while (!stoppingToken.IsCancellationRequested && !_store.DatabaseFileExists())
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.EnsureDatabaseAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "Usenet migration DB could not be initialised; runner idle.");
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Usenet migration tick failed: {Message}", e.Message);
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var session = await _store.GetSessionAsync(ct).ConfigureAwait(false);
        switch (session.Status)
        {
            case "scanning":
                await _scanRunner.RunAsync(ct).ConfigureAwait(false);
                break;

            case "running":
                await RunTickAsync(ct).ConfigureAwait(false);
                break;

            case "paused":
                await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
                break;

            case "linking":
                // Step 6 — build the rewrite plan (dry-run), then rest at "linked".
                await _symlinkPlanner.PlanAsync(ct).ConfigureAwait(false);
                await _store.UpdateSessionAsync(s => s.Status = "linked", ct).ConfigureAwait(false);
                break;

            case "applying":
                // Step 6 — apply the reviewed plan (backup + retarget), then rest at
                // "linked" so the UI can show applied/failed results and allow re-plan.
                await _symlinkRewriter.ApplyAsync(ct).ConfigureAwait(false);
                await _store.UpdateSessionAsync(s => s.Status = "linked", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        // The two global settings sampled during Scan must still hold, or the
        // computed naming/paths may be stale. If either changed, fall back to
        // Review rather than submitting against different rules.
        if (await GlobalsDriftedAsync(ct).ConfigureAwait(false))
        {
            await _store.UpdateSessionAsync(s => s.Status = "scanned", ct).ConfigureAwait(false);
            Log.Warning(
                "Usenet migration paused to Review: a global (lazy-RAR parsing or Windows-safe paths) " +
                "changed since Scan. Re-scan before running.");
            return;
        }

        await _store.BeginRunAsync(ct).ConfigureAwait(false);

        await _workerPool.SubmitBatchAsync(ct).ConfigureAwait(false);
        await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
        await MaybeCompleteAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> GlobalsDriftedAsync(CancellationToken ct)
    {
        var session = await _store.GetSessionAsync(ct).ConfigureAwait(false);
        // Nothing captured yet ⇒ no basis to compare; let the run proceed.
        if (session.ScanLazyRarEnabled is null && session.ScanWindowsSafePaths is null)
            return false;

        var lazyNow = _configManager.IsLazyRarParsingEnabled();
        var safeNow = PathSanitizer.IsWindowsSafePathsEnabled;
        return (session.ScanLazyRarEnabled is { } lazyThen && lazyThen != lazyNow)
               || (session.ScanWindowsSafePaths is { } safeThen && safeThen != safeNow);
    }

    /// <summary>Marks the session complete once no submission is still in flight.</summary>
    private async Task MaybeCompleteAsync(CancellationToken ct)
    {
        await using var ctx = _store.NewContext();
        var inFlight = await ctx.Submissions
            .CountAsync(s => s.State == "pending" || s.State == "submitted" || s.State == "processing", ct)
            .ConfigureAwait(false);
        if (inFlight > 0)
            return;

        await _store.CompleteRunAsync(ct).ConfigureAwait(false);

        Log.Information("Usenet migration complete: no submissions remain in flight.");
    }
}
