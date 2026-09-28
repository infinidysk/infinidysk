using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services.Prefetch;

public interface IPrefetchExecutor
{
    Task ExecuteAsync(PrefetchJob job, CancellationToken ct);
}

public sealed class PrefetchCoordinator(PrefetchJobStore store, IPrefetchExecutor executor,
    Func<PrefetchSettings> settings, Func<bool> admission, ILogger? logger = null,
    CancellationToken applicationStopping = default) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    internal const string MetadataFailureMessage = "Prefetch is unhealthy and warming is disabled until restart. Check local metadata storage and settings; ordinary playback remains available.";
    private string? _runtimeError;
    public string? RuntimeError => Volatile.Read(ref _runtimeError);
    public void ReportMetadataFailure()
    {
        if (Interlocked.CompareExchange(ref _runtimeError, MetadataFailureMessage, null) is null) CancelRunning();
    }
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, applicationStopping);
        ct = stopping.Token;
        if (RuntimeError is not null || ct.IsCancellationRequested || applicationStopping.IsCancellationRequested) return;
        var tasks = new List<Task>();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!admission() || store.Paused) return;
            for (var index = 0; index < settings().MaxConcurrentJobs; index++)
            {
                applicationStopping.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                if (RuntimeError is not null) break;
                var job = store.ClaimNext();
                if (job is null) break;
                tasks.Add(RunJobAsync(job, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || applicationStopping.IsCancellationRequested)
        { CancelRunning(); await DrainAsync(tasks).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ReportMetadataFailure(); await DrainAsync(tasks).ConfigureAwait(false); }
    }

    private static async Task DrainAsync(List<Task> tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task RunJobAsync(PrefetchJob job, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _running.Add(job.Id, cancellation);
        try
        {
            if (!store.IsRunning(job.Id)) return;
            if (RuntimeError is not null || store.Paused || !admission())
            {
                store.Defer(job.Id, "Warming is paused or foreground playback has priority.", TimeSpan.FromMinutes(1), consumeAttempt: false);
                return;
            }
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await executor.ExecuteAsync(job, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            // Source operations can observe host shutdown before their own token, or
            // surface disposal/transport errors while cancellation is unwinding.
            catch (Exception exception) when (exception is not OutOfMemoryException &&
                (cancellation.IsCancellationRequested || applicationStopping.IsCancellationRequested))
            {
                store.Defer(job.Id, "Interrupted; verified coverage retained.", TimeSpan.FromSeconds(30), consumeAttempt: false);
                return;
            }
            catch (PrefetchDeferredException exception)
            {
                store.Defer(job.Id, exception.Message, TimeSpan.FromMinutes(1), exception.CountsAsFailure);
                return;
            }
            catch (IOException exception)
            {
                store.Defer(job.Id, PrefetchFailureDiagnostics.Report(logger ?? Log.Logger, job, exception, retryable: true),
                    TimeSpan.FromMinutes(1), consumeAttempt: true);
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                store.Finish(job.Id, false, PrefetchFailureDiagnostics.Report(logger ?? Log.Logger, job, exception, retryable: false));
                return;
            }
            store.Finish(job.Id, true, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ReportMetadataFailure(); }
        finally { lock (_gate) _running.Remove(job.Id); }
    }

    private void CancelRunning()
    {
        CancellationTokenSource[] running;
        lock (_gate) running = _running.Values.ToArray();
        foreach (var cancellation in running) CancelSafely(cancellation);
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    public void Change(string id, string operation, int? priority = null)
    {
        store.Change(id, operation, priority);
        if (operation is "pause" or "cancel")
        {
            CancellationTokenSource? cancellation;
            lock (_gate) cancellation = _running.GetValueOrDefault(id);
            CancelSafely(cancellation);
        }
    }

    public void PruneOwners(Func<string, bool> keep)
    {
        store.PruneOwners(keep);
        KeyValuePair<string, CancellationTokenSource>[] running;
        lock (_gate) running = _running.ToArray();
        foreach (var (id, cancellation) in running)
            if (!store.IsRunning(id)) CancelSafely(cancellation);
    }

    public void SetPaused(bool paused)
    {
        store.SetPaused(paused);
        if (paused) CancelRunning();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(RuntimeError is null ? 1 : 30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
