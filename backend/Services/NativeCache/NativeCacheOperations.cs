using Microsoft.Extensions.Hosting;
using System.Threading.Channels;

namespace NzbWebDAV.Services.NativeCache;

public sealed record NativeCacheOperation(string Id, string FolderId, string Operation, string State, int? Result = null, string? Error = null,
    NativeCacheProbeResult? Probe = null);

public sealed class NativeCacheOperations : BackgroundService
{
    private readonly NativeCacheService _native;
    private readonly Lock _gate = new();
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { SingleReader = true });
    private readonly Dictionary<string, NativeCacheOperation> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = [];
    public NativeCacheOperations(NativeCacheService native) => _native = native;

    internal static async Task<NativeCacheProbeResult> RunProbeWithDeadlineAsync(Func<CancellationToken, Task<NativeCacheProbeResult>> probe,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workToken = work.Token;
        // Even opening an NFS file can block synchronously. Isolate that call so the
        // admin operation has a deadline; the store's writer gate bounds stalled IO.
        var pending = Task.Run(() => probe(workToken), CancellationToken.None);
        _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IOException("Storage probe exceeded its deadline."); }
        // Request cancellation before disposing the linked source; do not await the
        // isolated NAS operation after its deadline. Its eventual fault is observed above.
#pragma warning disable CA1849 // Synchronous cancellation request must precede linked-source teardown.
        finally { if (!pending.IsCompleted) work.Cancel(); }
#pragma warning restore CA1849
    }

    public NativeCacheOperation Enqueue(string folderId, string operation, string? confirmFolderId = null)
    {
        if (_native.Store is null || _native.ActiveSettings?.Folders.FirstOrDefault(folder => folder.Id == folderId && folder.Enabled) is not { } folder)
            throw new ArgumentException("Native cache and the selected folder must be active.");
        if (operation is not ("probe" or "scan" or "clear")) throw new ArgumentException("Unknown native cache operation.");
        if (operation == "clear" && (confirmFolderId != folderId || folder.ReadOnly))
            throw new ArgumentException("Confirm the exact writable folder before clearing its cached files.");
        lock (_gate)
        {
            var job = new NativeCacheOperation(Guid.NewGuid().ToString("N"), folderId, operation, "queued");
            if (!_pending.Writer.TryWrite(job.Id)) throw new ArgumentException("Native cache operation queue is full.");
            foreach (var id in _jobs.Values.Where(job => job.State is not ("queued" or "running")).Take(Math.Max(0, _jobs.Count - 63)).Select(job => job.Id).ToArray())
                _jobs.Remove(id);
            _jobs[job.Id] = job;
            return job;
        }
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State is not ("queued" or "running")) return false;
            _jobs[id] = job with { State = "cancelled" };
            _cancellations.GetValueOrDefault(id)?.Cancel();
            return true;
        }
    }

    public IReadOnlyList<NativeCacheOperation> GetJobs() { lock (_gate) return _jobs.Values.Reverse().ToArray(); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var key in running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
            {
                _ = running[key].Exception;
                running.Remove(key);
            }
            for (var index = 0; index < 8 && _pending.Reader.TryRead(out var id); index++)
            {
                NativeCacheOperation? job;
                lock (_gate) job = _jobs.GetValueOrDefault(id);
                if (job is null || job.State == "cancelled") continue;
                if (running.ContainsKey(job.FolderId))
                {
                    if (!_pending.Writer.TryWrite(id))
                        lock (_gate) _jobs[id] = job with { State = "failed", Error = "Storage operation queue is full; retry later." };
                    continue;
                }
                var jobId = id;
                running[job.FolderId] = Task.Run(() => RunAsync(jobId, stoppingToken), CancellationToken.None);
            }
            var pressure = ++tick % 30 == 0;
            if (_native.Store is not { } store || _native.ActiveSettings is not { } settings) continue;
            foreach (var folder in settings.Folders.Where(folder => folder.Enabled && !folder.ReadOnly))
            {
                if (running.ContainsKey(folder.Id)) continue;
                // At most one owned task per configured folder, even if an OS call
                // ignores cancellation. Completed tasks are observed on the next tick.
                running[folder.Id] = Task.Run(async () =>
                {
                    try
                    {
                        await store.ProcessOneCheckpointAsync(folder.Id, stoppingToken).ConfigureAwait(false);
                        if (pressure)
                        {
                            await store.ReclaimRetiredAsync(folder.Id, ct: stoppingToken).ConfigureAwait(false);
                            await store.EvictAsync(folder.Id, cancellationToken: stoppingToken).ConfigureAwait(false);
                            await store.EvictPressureAsync(folder.Id, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException or ObjectDisposedException)
                    { /* Offline maintenance stays isolated to this folder. */ }
                }, CancellationToken.None);
            }
        }
    }

    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        NativeCacheOperation job;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!) || job.State == "cancelled") return;
            _jobs[id] = job = job with { State = "running" };
            _cancellations[id] = cancellation;
        }
        try
        {
            if (job.Operation == "probe")
            {
                var probe = await RunProbeWithDeadlineAsync(ct => _native.Store!.ProbeAsync(job.FolderId, ct),
                    TimeSpan.FromSeconds(15), cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                    if (_jobs[id].State != "cancelled")
                        _jobs[id] = job with { State = probe.Error is null ? "completed" : "failed", Result = probe.Error is null ? 1 : 0,
                            Probe = probe, Error = probe.Error };
                return;
            }
            var result = job.Operation switch
            {
                "scan" => await _native.Store!.ScanAsync(job.FolderId, cancellation.Token).ConfigureAwait(false),
                "clear" => await ClearAsync(job.FolderId, cancellation.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unknown native cache operation.")
            };
            lock (_gate) if (_jobs[id].State != "cancelled") _jobs[id] = job with { State = "completed", Result = result };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { lock (_gate) _jobs[id] = job with { State = "cancelled" }; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { lock (_gate) _jobs[id] = job with { State = "failed", Error = "Storage operation failed. Check folder availability, permissions, and local metadata space." }; }
        finally { lock (_gate) _cancellations.Remove(id); }
    }

    private async Task<int> ClearAsync(string folderId, CancellationToken cancellationToken)
    {
        var total = 0;
        while (await _native.Store!.ReclaimRetiredAsync(folderId, clear: true, ct: cancellationToken).ConfigureAwait(false) is var reclaimed && reclaimed > 0)
            total += reclaimed;
        _native.Store!.ResetClearCursor(folderId);
        while (true)
        {
            var count = await _native.Store!.EvictAsync(folderId, clear: true, cancellationToken).ConfigureAwait(false);
            total += count;
            if (count == 0 && !_native.Store.HasPendingClearPage(folderId)) return total;
            await Task.Yield();
        }
    }
}
