namespace UsenetSharpTest.Support;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private readonly SemaphoreSlim _timerCreated = new(0);
    private long _timestamp;
    private int _createdTimerCount;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_timestamp);
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period);
            _createdTimerCount++;
        }

        _timerCreated.Release();
        return timer;
    }

    public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);

    public async Task WaitForCreatedTimerCountAsync(
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _createdTimerCount) < expectedCount)
        {
            await _timerCreated.WaitAsync(cancellationToken);
        }
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        long target;
        lock (_gate)
        {
            target = checked(_timestamp + amount.Ticks);
        }

        while (true)
        {
            ManualTimer[] dueTimers;
            lock (_gate)
            {
                var nextTimestamp = _timers
                    .Where(timer => timer.DueTimestamp <= target)
                    .Select(timer => timer.DueTimestamp)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min();
                if (nextTimestamp == long.MaxValue)
                {
                    _timestamp = target;
                    return;
                }

                _timestamp = nextTimestamp;
                dueTimers = _timers
                    .Where(timer => timer.DueTimestamp == nextTimestamp)
                    .ToArray();
                foreach (var timer in dueTimers)
                {
                    timer.PrepareForCallbackUnderLock();
                }
            }

            foreach (var timer in dueTimers)
            {
                timer.InvokeCallback();
            }
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider provider,
        TimerCallback callback,
        object? state) : ITimer
    {
        private bool _disposed;
        private long _periodTicks = Timeout.Infinite;

        public long DueTimestamp { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (provider._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                ChangeUnderLock(dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (provider._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DueTimestamp = long.MaxValue;
                provider._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
        {
            var dueTicks = GetTimerTicks(dueTime, nameof(dueTime));
            _periodTicks = GetTimerTicks(period, nameof(period));
            DueTimestamp = dueTicks == Timeout.Infinite
                ? long.MaxValue
                : checked(provider._timestamp + dueTicks);
        }

        public void PrepareForCallbackUnderLock()
        {
            DueTimestamp = _periodTicks == Timeout.Infinite
                ? long.MaxValue
                : checked(provider._timestamp + _periodTicks);
        }

        public void InvokeCallback()
        {
            lock (provider._gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            callback(state);
        }

        private static long GetTimerTicks(TimeSpan value, string parameterName)
        {
            if (value == Timeout.InfiniteTimeSpan)
            {
                return Timeout.Infinite;
            }

            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return value.Ticks;
        }
    }
}
