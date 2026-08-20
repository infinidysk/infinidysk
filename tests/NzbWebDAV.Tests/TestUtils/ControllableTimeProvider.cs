namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// TimeProvider whose clock is advanced by tests so grace timers fire deterministically.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;
    private readonly List<ManualTimer> _timers = [];

    public ControllableTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? DateTimeOffset.UnixEpoch;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (_gate)
            _now += delta;
        FireDueTimers();
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    internal DateTimeOffset Now
    {
        get { lock (_gate) return _now; }
    }

    private void FireDueTimers()
    {
        while (true)
        {
            ManualTimer? due = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (!timer.IsDue(_now)) continue;
                    if (due is null || timer.NextDue < due.NextDue)
                        due = timer;
                }

                due?.PrepareFire(_now);
            }

            if (due is null)
                return;
            due.Invoke();
        }
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ControllableTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _gate = new();
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private DateTimeOffset? _nextDue;
        private bool _disposed;

        public ManualTimer(ControllableTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? NextDue
        {
            get { lock (_gate) return _nextDue; }
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_gate)
                return !_disposed && _nextDue is { } due && due <= now;
        }

        public void PrepareFire(DateTimeOffset now)
        {
            lock (_gate)
            {
                _nextDue = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan
                    ? now + _period
                    : null;
            }
        }

        public void Invoke() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var fireImmediately = false;
            lock (_gate)
            {
                if (_disposed) return false;
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _nextDue = null;
                    return true;
                }

                _nextDue = _provider.Now + dueTime;
                fireImmediately = dueTime == TimeSpan.Zero;
            }

            if (fireImmediately)
                _callback(_state);
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _nextDue = null;
            }

            _provider.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
