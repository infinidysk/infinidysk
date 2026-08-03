namespace UsenetSharp.Clients;

internal sealed class CoalescedReadTimeout : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _operationToken;
    private readonly CancellationTokenSource _timeoutCts;
    private readonly CancellationToken _token;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private ITimer? _timer;
    private long _readStartedTimestamp;
    private bool _readPending;
    private volatile bool _timeoutTriggered;
    private bool _disposed;

    public CoalescedReadTimeout(
        CancellationToken operationToken,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        _operationToken = operationToken;
        _timeout = timeout;
        _timeProvider = timeProvider;
        _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        _token = _timeoutCts.Token;
        _timer = timeProvider.CreateTimer(
            static state => ((CoalescedReadTimeout)state!).CheckForTimeout(),
            this,
            timeout,
            Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _token;

    public bool IsTimeoutCancellation =>
        _timeoutTriggered && !_operationToken.IsCancellationRequested;

    public void BeginIo()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _readStartedTimestamp = _timeProvider.GetTimestamp();
            _readPending = true;
        }
    }

    public void EndIo()
    {
        lock (_gate)
        {
            _readPending = false;
        }
    }

    private void CheckForTimeout()
    {
        var shouldCancel = false;

        lock (_gate)
        {
            if (_disposed || _timeoutCts.IsCancellationRequested)
            {
                return;
            }

            var nextCheck = _timeout;
            if (_readPending)
            {
                var elapsed = _timeProvider.GetElapsedTime(
                    _readStartedTimestamp,
                    _timeProvider.GetTimestamp());
                if (elapsed >= _timeout)
                {
                    _timeoutTriggered = true;
                    shouldCancel = true;
                }
                else
                {
                    nextCheck = _timeout - elapsed;
                }
            }

            if (!shouldCancel)
            {
                _timer!.Change(nextCheck, Timeout.InfiniteTimeSpan);
            }
        }

        if (!shouldCancel)
        {
            return;
        }

        try
        {
            _timeoutCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced ahead of Cancel; timeout is already terminal.
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        CancellationTokenSource? timeoutCts;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            timeoutCts = _timeoutCts;
            _timer = null;
        }

        timer?.Dispose();
        timeoutCts?.Dispose();
    }
}
