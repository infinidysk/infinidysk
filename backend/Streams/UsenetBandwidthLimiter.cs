namespace NzbWebDAV.Streams;

/// <summary>
/// Process-wide token bucket for live Usenet payload ingress (issue #375).
/// Bytes/second refill with ~250ms burst capacity. Unlimited when the rate is 0.
/// </summary>
public sealed class UsenetBandwidthLimiter
{
    public static UsenetBandwidthLimiter? Current { get; set; }

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private long _bytesPerSecond;
    private double _availableTokens;
    private long _lastRefillTimestamp;
    private long _totalChargedBytes;
    private ITimer? _headTimer;

    public UsenetBandwidthLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastRefillTimestamp = _timeProvider.GetTimestamp();
    }

    public long BytesPerSecond => Interlocked.Read(ref _bytesPerSecond);
    public long TotalChargedBytes => Interlocked.Read(ref _totalChargedBytes);

    public void UpdateLimit(long bytesPerSecond)
    {
        var next = Math.Max(0, bytesPerSecond);
        lock (_gate)
        {
            RefillLocked();
            var previous = Interlocked.Exchange(ref _bytesPerSecond, next);
            if (next == 0)
            {
                _availableTokens = 0;
                CompleteAllWaitersLocked();
                CancelHeadTimerLocked();
                return;
            }

            var burst = BurstCapacityLocked();
            _availableTokens = previous <= 0 ? burst : Math.Min(_availableTokens, burst);
            PumpLocked();
        }
    }

    /// <summary>
    /// Waits until <paramref name="bytes"/> tokens are available, then charges them.
    /// Requests larger than the 250ms burst are granted in burst-sized chunks so a
    /// low rate cap stays effective for a single payload.
    /// </summary>
    public ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || Volatile.Read(ref _bytesPerSecond) <= 0)
            return ValueTask.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (Volatile.Read(ref _bytesPerSecond) <= 0)
                return ValueTask.CompletedTask;

            RefillLocked();
            var burst = BurstCapacityLocked();
            if (_waiters.Count == 0 && bytes <= burst && _availableTokens >= bytes
                && TryGrantChunkLocked(bytes) == bytes)
                return ValueTask.CompletedTask;

            waiter = new Waiter(this, bytes);
            waiter.Node = _waiters.AddLast(waiter);
            PumpLocked();
        }

        if (waiter.Completion.Task.IsCompleted)
            return new ValueTask(waiter.Completion.Task);

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(
                static state => ((Waiter)state!).Cancel(), waiter);
        }

        return new ValueTask(waiter.Completion.Task);
    }

    private int TryGrantChunkLocked(int remaining)
    {
        var rate = Volatile.Read(ref _bytesPerSecond);
        if (rate <= 0)
            return remaining;

        var burst = BurstCapacityLocked();
        var grant = (int)Math.Min(remaining, Math.Min(_availableTokens, burst));
        if (grant <= 0)
            return 0;

        _availableTokens -= grant;
        Interlocked.Add(ref _totalChargedBytes, grant);
        return grant;
    }

    private bool TryGrantWaiterLocked(Waiter waiter)
    {
        if (Volatile.Read(ref _bytesPerSecond) <= 0)
        {
            waiter.Remaining = 0;
            return true;
        }

        while (waiter.Remaining > 0)
        {
            var granted = TryGrantChunkLocked(waiter.Remaining);
            if (granted <= 0)
                return false;
            waiter.Remaining -= granted;
        }

        return true;
    }

    private void PumpLocked()
    {
        CancelHeadTimerLocked();
        while (_waiters.First is { } node)
        {
            var waiter = node.Value;
            if (waiter.Completion.Task.IsCompleted)
            {
                RemoveWaiterLocked(waiter);
                continue;
            }

            if (Volatile.Read(ref _bytesPerSecond) <= 0 || TryGrantWaiterLocked(waiter))
            {
                RemoveWaiterLocked(waiter);
                waiter.TryComplete();
                continue;
            }

            ScheduleHeadTimerLocked(waiter);
            return;
        }
    }

    private void ScheduleHeadTimerLocked(Waiter waiter)
    {
        var rate = Volatile.Read(ref _bytesPerSecond);
        if (rate <= 0)
        {
            waiter.TryComplete();
            RemoveWaiterLocked(waiter);
            return;
        }

        var needed = Math.Max(0, Math.Min(waiter.Remaining, BurstCapacityLocked()) - _availableTokens);
        var seconds = needed / rate;
        var delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.001, 60));
        _headTimer = _timeProvider.CreateTimer(
            static state => ((UsenetBandwidthLimiter)state!).OnHeadTimer(),
            this,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void OnHeadTimer()
    {
        lock (_gate)
        {
            RefillLocked();
            PumpLocked();
        }
    }

    private void RefillLocked()
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        var rate = Volatile.Read(ref _bytesPerSecond);
        if (rate <= 0 || elapsed <= TimeSpan.Zero)
            return;

        _availableTokens = Math.Min(
            BurstCapacityLocked(),
            _availableTokens + elapsed.TotalSeconds * rate);
    }

    private double BurstCapacityLocked() =>
        Math.Max(1, Volatile.Read(ref _bytesPerSecond) * 0.25);

    private void CompleteAllWaitersLocked()
    {
        while (_waiters.First is { } node)
        {
            var waiter = node.Value;
            RemoveWaiterLocked(waiter);
            waiter.TryComplete();
        }
    }

    private void RemoveWaiterLocked(Waiter waiter)
    {
        if (waiter.Node is { } node)
        {
            _waiters.Remove(node);
            waiter.Node = null;
        }
    }

    private void CancelHeadTimerLocked()
    {
        _headTimer?.Dispose();
        _headTimer = null;
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null)
                return;
            RemoveWaiterLocked(waiter);
            waiter.TryCancel();
            PumpLocked();
        }
    }

    private sealed class Waiter
    {
        private readonly UsenetBandwidthLimiter _owner;
        public int Remaining;
        public readonly TaskCompletionSource Completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;

        public Waiter(UsenetBandwidthLimiter owner, int bytes)
        {
            _owner = owner;
            Remaining = bytes;
        }

        public void TryComplete()
        {
            Registration.Dispose();
            Completion.TrySetResult();
        }

        public void TryCancel()
        {
            Registration.Dispose();
            Completion.TrySetCanceled();
        }

        public void Cancel() => _owner.CancelWaiter(this);
    }
}
