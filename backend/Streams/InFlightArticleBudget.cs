using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Services.Metrics;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// Process-wide cap on decoded article bytes retained in RAM, including pipe copies
/// from all workloads (streaming, queue, health, benchmark, and seek). Streaming
/// leases gate admission; queue/health/seek pipe bytes are accounted but not gated.
/// Connection semaphores bound concurrency; this bounds retained bytes so concurrent
/// streams cannot OOM the host.
/// </summary>
public sealed class InFlightArticleBudget
{
    private long _leased;
    private long _capBytes;
    private long _throttleEvents;
    private long _lastWarningTicks;
    private readonly ProviderLatencyTracker? _latencyTracker;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();

    /// <summary>
    /// Process-wide instance set at startup. Streams fall back to this when no
    /// budget is passed explicitly (tests pass their own).
    /// </summary>
    public static InFlightArticleBudget? Current { get; set; }

    public InFlightArticleBudget(long capBytes, ProviderLatencyTracker? latencyTracker = null)
    {
        _capBytes = Math.Max(1, capBytes);
        _latencyTracker = latencyTracker;
    }

    public long LeasedBytes => Interlocked.Read(ref _leased);
    public long CapBytes => Interlocked.Read(ref _capBytes);
    public long ThrottleEvents => Interlocked.Read(ref _throttleEvents);

    /// <summary>
    /// Updates the cap (e.g. after a settings change). Wakes waiters when the cap grows.
    /// </summary>
    public void SetCapBytes(long capBytes)
    {
        var next = Math.Max(1, capBytes);
        Interlocked.Exchange(ref _capBytes, next);
        SignalWaiters();
    }

    /// <summary>
    /// Blocks until <paramref name="bytes"/> can be leased, or cancels.
    /// Returns a lease that releases exactly once on dispose.
    /// </summary>
    public async ValueTask<ArticleByteLease> LeaseAsync(long bytes, CancellationToken ct)
    {
        if (bytes <= 0) return ArticleByteLease.Empty;

        Waiter? waiter = null;
        Stopwatch? waitTimer = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (TryLease(bytes))
                {
                    if (waitTimer is not null)
                    {
                        _latencyTracker?.Record(
                            providerKey: null,
                            LatencyPhase.LocalCapWait,
                            DownloadWorkloadClassifier.Classify(ct),
                            NntpOperation.Admission,
                            waitTimer.Elapsed);
                    }
                    return new ArticleByteLease(this, bytes);
                }

                Interlocked.Increment(ref _throttleEvents);
                MaybeWarn(bytes);
                waitTimer ??= Stopwatch.StartNew();

                waiter ??= new Waiter(bytes);
                lock (_gate)
                {
                    if (TryLease(bytes))
                        return new ArticleByteLease(this, bytes);

                    if (waiter.Node is null)
                        waiter.Node = _waiters.AddLast(waiter);

                    // Fresh TCS each wait so a prior wake cannot complete a later wait.
                    waiter.Reset();
                }

                try
                {
                    await waiter.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    RemoveWaiter(waiter);
                    throw;
                }
            }
        }
        finally
        {
            RemoveWaiter(waiter);
        }
    }

    private void RemoveWaiter(Waiter? waiter)
    {
        if (waiter?.Node is null) return;
        TaskCompletionSource<bool>? nextTcs = null;
        lock (_gate)
        {
            if (waiter.Node is null) return;
            var wasHead = ReferenceEquals(_waiters.First, waiter.Node);
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            // A Release that raced with cancellation may have signalled only this
            // waiter; wake the new head so FIFO waiters do not stall forever.
            if (wasHead)
                nextTcs = _waiters.First?.Value.Tcs;
        }

        nextTcs?.TrySetResult(true);
    }

    private bool TryLease(long bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _leased);
            var cap = Interlocked.Read(ref _capBytes);

            // A single segment larger than the cap must still progress when nothing
            // else is leased; otherwise drain would stall forever.
            if (bytes > cap)
            {
                if (current != 0) return false;
            }
            else if (current + bytes > cap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _leased, current + bytes, current) == current)
                return true;
        }
    }

    internal void Release(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _leased, -bytes);
        SignalWaiters();
    }

    internal void AccountExtra(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _leased, bytes);
    }

    /// <summary>
    /// Accounts decoded bytes buffered in UsenetSharp body pipes — the second resident
    /// copy of each in-flight segment. Positive deltas never block or wake waiters;
    /// negative deltas release and wake the FIFO head. Deltas sum to zero per body,
    /// so the counter self-balances across success, cancellation, and dispose.
    /// </summary>
    public void AccountBufferedPipeBytes(long delta)
    {
        if (delta > 0) AccountExtra(delta);
        else if (delta < 0) Release(-delta);
    }

    /// <summary>
    /// Wake the head waiter only; they re-attempt <see cref="TryLease"/> so accounting
    /// stays single-owner. Fairness comes from FIFO queue order.
    /// </summary>
    private void SignalWaiters()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
            tcs = _waiters.First?.Value.Tcs;
        tcs?.TrySetResult(true);
    }

    private void MaybeWarn(long requested)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWarningTicks, nowTicks, last) != last) return;

        Log.Warning(
            "In-flight article memory budget saturated. Leased={Leased:N0} Cap={Cap:N0} Requested={Requested:N0}. Reason: {Reason}",
            LeasedBytes, CapBytes, requested, "backpressure");
    }

    private sealed class Waiter(long bytes)
    {
        public long Bytes { get; } = bytes;
        public LinkedListNode<Waiter>? Node { get; set; }
        public TaskCompletionSource<bool> Tcs { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset() =>
            Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Holds <see cref="InFlightArticleBudget"/> credits for one drained segment.
/// </summary>
public sealed class ArticleByteLease : IDisposable
{
    public static readonly ArticleByteLease Empty = new(null, 0);

    private readonly InFlightArticleBudget? _owner;
    private long _remaining;

    internal ArticleByteLease(InFlightArticleBudget? owner, long bytes)
    {
        _owner = owner;
        _remaining = bytes;
    }

    /// <summary>
    /// Adjusts the lease when drained length differs from the estimate.
    /// Negative releases surplus; positive accounts for an underestimate (non-blocking).
    /// </summary>
    public void Adjust(long delta)
    {
        if (_owner is null || delta == 0) return;
        if (delta < 0)
        {
            var release = Math.Min(Interlocked.Read(ref _remaining), -delta);
            if (release <= 0) return;
            Interlocked.Add(ref _remaining, -release);
            _owner.Release(release);
            return;
        }

        Interlocked.Add(ref _remaining, delta);
        _owner.AccountExtra(delta);
    }

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _remaining, 0);
        if (release > 0) _owner?.Release(release);
    }
}
