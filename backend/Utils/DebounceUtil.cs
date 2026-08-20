using Serilog;

namespace NzbWebDAV.Utils;

public static class DebounceUtil
{
    public static Action<Action> CreateDebounce(TimeSpan timespan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timespan, TimeSpan.Zero);
        var debouncer = new Debouncer(timespan);
        return debouncer.Invoke;
    }

    public static Action<Action> RunOnlyOnce()
    {
        var isAlreadyRan = false;
        return actionToMaybeInvoke =>
        {
            if (isAlreadyRan) return;
            isAlreadyRan = true;
            actionToMaybeInvoke?.Invoke();
        };
    }

    private sealed class Debouncer(TimeSpan timespan)
    {
        private readonly object _synchronizationLock = new();
        private DateTime _lastInvocationTime;
        private bool _isFlushScheduled;
        private Action? _pendingAction;
        private Timer? _flushTimer;

        public void Invoke(Action actionToInvoke)
        {
            Action? invokeNow = null;
            lock (_synchronizationLock)
            {
                var now = DateTime.Now;
                var elapsed = now - _lastInvocationTime;
                if (elapsed >= timespan && !_isFlushScheduled)
                {
                    _lastInvocationTime = now;
                    invokeNow = actionToInvoke;
                }
                else
                {
                    _pendingAction = actionToInvoke;
                    if (!_isFlushScheduled)
                    {
                        _isFlushScheduled = true;
                        var delay = timespan - elapsed;
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        _flushTimer ??= new Timer(_ =>
                        {
                            Action? trailingAction;
                            lock (_synchronizationLock)
                            {
                                _isFlushScheduled = false;
                                _lastInvocationTime = DateTime.Now;
                                trailingAction = _pendingAction;
                                _pendingAction = null;
                            }

                            try
                            {
                                trailingAction?.Invoke();
                            }
                            catch (Exception e) when (e is not OutOfMemoryException)
                            {
                                Log.Warning(e, "Debounced trailing action failed");
                            }
                        });
                        _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
                    }
                }
            }

            try
            {
                invokeNow?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning(e, "Debounced action failed");
            }
        }
    }
}
