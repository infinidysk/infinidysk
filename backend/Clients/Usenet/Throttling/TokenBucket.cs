using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Concurrency;

namespace NzbWebDAV.Clients.Usenet.Throttling;

/// <summary>
/// An async token bucket used to cap sustained throughput (bytes/second). Allows a burst of up
/// to one second's worth of bytes, then throttles to the configured rate.
///
/// Optionally supports a preferential split between two priority classes (matching
/// SemaphorePriority) when both are simultaneously contending for bandwidth: while there's enough
/// budget for everyone, both proceed immediately - the split only decides ordering once the bucket
/// is actually contended, using the same odds-based dice roll as PrioritizedSemaphore so neither
/// class can fully starve the other. Unused capacity from either class always remains available to
/// the other; this is not a hard per-class sub-cap.
/// </summary>
public class TokenBucket
{
    private sealed record Waiter(int ByteCount, TaskCompletionSource<bool> Tcs);

    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _highPriorityWaiters = [];
    private readonly LinkedList<Waiter> _lowPriorityWaiters = [];
    private SemaphorePriorityOdds _priorityOdds;
    private double _bytesPerSecond;
    private double _availableBytes;
    private long _lastRefillTimestamp;
    private long _totalBytesConsumed;
    private int _accumulatedOdds;
    private bool _dispatcherRunning;

    public TokenBucket(double bytesPerSecond, SemaphorePriorityOdds? priorityOdds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _availableBytes = bytesPerSecond;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
    }

    /// <summary>
    /// Running total of bytes consumed since this bucket was created. Used to derive a
    /// live throughput reading (by sampling the delta over a time window) for the UI.
    /// </summary>
    public long TotalBytesConsumed
    {
        get { lock (_lock) return _totalBytesConsumed; }
    }

    public async Task ConsumeAsync(int byteCount, SemaphorePriority priority, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs;
        LinkedList<Waiter> queue;
        LinkedListNode<Waiter> node;

        lock (_lock)
        {
            Refill();

            // fast path: nobody is queued and there's enough budget - no need to involve the
            // dispatcher or worry about priority at all.
            if (_highPriorityWaiters.Count == 0 && _lowPriorityWaiters.Count == 0 && _availableBytes >= byteCount)
            {
                _availableBytes -= byteCount;
                _totalBytesConsumed += byteCount;
                return;
            }

            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue = priority == SemaphorePriority.High ? _highPriorityWaiters : _lowPriorityWaiters;
            node = queue.AddLast(new Waiter(byteCount, tcs));
            EnsureDispatcherRunning();

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    var removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            queue.Remove(node);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // already dispatched by the time cancellation ran
                        }
                    }

                    if (removed)
                        tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
        }

        await tcs.Task.ConfigureAwait(false);
    }

    public void UpdateRate(double bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        lock (_lock)
        {
            Refill();
            _bytesPerSecond = bytesPerSecond;
            _availableBytes = Math.Min(_availableBytes, _bytesPerSecond);
        }
    }

    public void UpdatePriorityOdds(SemaphorePriorityOdds priorityOdds)
    {
        lock (_lock) _priorityOdds = priorityOdds;
    }

    // must be called while holding _lock
    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        // burst capacity is capped at one second's worth of bytes
        _availableBytes = Math.Min(_bytesPerSecond, _availableBytes + elapsedSeconds * _bytesPerSecond);
    }

    // must be called while holding _lock
    private void EnsureDispatcherRunning()
    {
        if (_dispatcherRunning) return;
        _dispatcherRunning = true;
        _ = Task.Run(RunDispatcher);
    }

    /// <summary>
    /// Services queued waiters (both priority classes) as budget becomes available, until both
    /// queues are empty. Runs as a single background loop per bucket, started lazily.
    /// </summary>
    private async Task RunDispatcher()
    {
        while (true)
        {
            TimeSpan waitTime;
            lock (_lock)
            {
                Refill();
                if (!TryDispatch(out waitTime))
                {
                    _dispatcherRunning = false;
                    return;
                }
            }

            if (waitTime > TimeSpan.Zero)
                await Task.Delay(waitTime).ConfigureAwait(false);
        }
    }

    // must be called while holding _lock.
    // Returns false if both queues are empty (nothing left to dispatch, caller should stop).
    // Otherwise dispatches whatever it can right now and sets waitTime to how long to sleep
    // before trying again (TimeSpan.Zero if progress was made and the loop should retry immediately).
    private bool TryDispatch(out TimeSpan waitTime)
    {
        waitTime = TimeSpan.Zero;
        var highHead = _highPriorityWaiters.First?.Value;
        var lowHead = _lowPriorityWaiters.First?.Value;
        if (highHead is null && lowHead is null) return false;

        var highReady = highHead is not null && _availableBytes >= highHead.ByteCount;
        var lowReady = lowHead is not null && _availableBytes >= lowHead.ByteCount;

        if (highReady && lowReady)
        {
            // both are immediately servable - roll the dice using the configured odds, same
            // mechanic as PrioritizedSemaphore.Release, so neither class starves the other.
            _accumulatedOdds += _priorityOdds.LowPriorityOdds;
            var serveLow = _accumulatedOdds >= 100;
            if (serveLow) _accumulatedOdds -= 100;
            Dispatch(serveLow ? _lowPriorityWaiters : _highPriorityWaiters, serveLow ? lowHead! : highHead!);
            return true;
        }

        if (highReady)
        {
            Dispatch(_highPriorityWaiters, highHead!);
            return true;
        }

        if (lowReady)
        {
            Dispatch(_lowPriorityWaiters, lowHead!);
            return true;
        }

        // neither is servable yet - wake up as soon as whichever needs the least additional
        // budget becomes servable, so unused capacity always goes to whoever can use it first.
        var waitHigh = highHead is not null
            ? (highHead.ByteCount - _availableBytes) / _bytesPerSecond
            : double.PositiveInfinity;
        var waitLow = lowHead is not null
            ? (lowHead.ByteCount - _availableBytes) / _bytesPerSecond
            : double.PositiveInfinity;
        waitTime = TimeSpan.FromSeconds(Math.Min(waitHigh, waitLow));
        return true;
    }

    // must be called while holding _lock
    private void Dispatch(LinkedList<Waiter> queue, Waiter waiter)
    {
        queue.RemoveFirst();
        _availableBytes -= waiter.ByteCount;
        _totalBytesConsumed += waiter.ByteCount;
        waiter.Tcs.TrySetResult(true);
    }
}
