using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public enum HealthCheckAdmissionPriority
{
    Queue,
    Background,
}

public sealed record HealthCheckConnectionGateSnapshot(
    int Limit,
    int Active,
    int WaitingQueue,
    int WaitingBackground);

/// <summary>
/// Process-wide admission gate for NNTP work that verifies article existence.
/// Queue verification receives newly released capacity before background health work.
/// </summary>
public sealed class HealthCheckConnectionGate : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly Lock _lock = new();
    private readonly Lock _limitRefreshLock = new();
    private readonly LinkedList<Waiter> _queueWaiters = [];
    private readonly LinkedList<Waiter> _backgroundWaiters = [];
    private TaskCompletionSource _idleCompletion = CreateIdleCompletion(completed: true);
    private int _effectiveLimit;
    private int _active;
    private bool _disposed;

    public HealthCheckConnectionGate(ConfigManager configManager)
    {
        _configManager = configManager;
        _configManager.OnConfigChanged += OnConfigChanged;
        lock (_limitRefreshLock)
            _effectiveLimit = _configManager.GetHealthCheckConcurrency();
    }

    public Task<Lease> AcquireAsync(
        HealthCheckAdmissionPriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (CanEnterImmediately(priority))
            {
                BeginBusyPeriodIfIdle();
                _active++;
                return Task.FromResult(new Lease(this));
            }

            var waiter = new Waiter(priority);
            GetQueue(priority).AddLast(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(
                    () => CancelWaiter(waiter, cancellationToken));
                _ = waiter.Completion.Task.ContinueWith(
                    static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                    registration,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }

            return waiter.Completion.Task;
        }
    }

    public HealthCheckConnectionGateSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new HealthCheckConnectionGateSnapshot(
                GetLimit(),
                _active,
                _queueWaiters.Count,
                _backgroundWaiters.Count);
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_active == 0) return Task.CompletedTask;
            return _idleCompletion.Task.WaitAsync(cancellationToken);
        }
    }

    private int GetLimit() => _effectiveLimit;

    private bool CanEnterImmediately(HealthCheckAdmissionPriority priority)
    {
        if (_active >= GetLimit() || GetQueue(priority).Count > 0) return false;
        return priority == HealthCheckAdmissionPriority.Queue || _queueWaiters.Count == 0;
    }

    private void Release()
    {
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready;
        lock (_lock)
        {
            if (_disposed) return;
            _active--;
            ready = DispatchWaiters();
            if (_active == 0)
                _idleCompletion.TrySetResult();
        }

        CompleteReadyWaiters(ready);
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready;
        bool removed;
        lock (_lock)
        {
            removed = GetQueue(waiter.Priority).Remove(waiter);
            ready = removed && !_disposed ? DispatchWaiters() : [];
        }

        if (removed) waiter.Completion.TrySetCanceled(cancellationToken);
        CompleteReadyWaiters(ready);
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs args)
    {
        if (!args.ChangedConfig.ContainsKey(ConfigKeys.RepairHealthcheckConcurrency)
            && !args.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;

        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready;
        lock (_limitRefreshLock)
        {
            var effectiveLimit = _configManager.GetHealthCheckConcurrency();
            lock (_lock)
            {
                if (_disposed) return;
                _effectiveLimit = effectiveLimit;
                ready = DispatchWaiters();
            }
        }

        CompleteReadyWaiters(ready);
    }

    private List<(TaskCompletionSource<Lease> Completion, Lease Lease)> DispatchWaiters()
    {
        List<(TaskCompletionSource<Lease>, Lease)> ready = [];
        while (_active < GetLimit())
        {
            var waiter = TakeFirst(_queueWaiters) ?? TakeFirst(_backgroundWaiters);
            if (waiter is null) break;
            BeginBusyPeriodIfIdle();
            _active++;
            ready.Add((waiter.Completion, new Lease(this)));
        }

        return ready;
    }

    private void BeginBusyPeriodIfIdle()
    {
        if (_active == 0 && _idleCompletion.Task.IsCompleted)
            _idleCompletion = CreateIdleCompletion(completed: false);
    }

    private LinkedList<Waiter> GetQueue(HealthCheckAdmissionPriority priority) => priority switch
    {
        HealthCheckAdmissionPriority.Queue => _queueWaiters,
        HealthCheckAdmissionPriority.Background => _backgroundWaiters,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
    };

    private static Waiter? TakeFirst(LinkedList<Waiter> queue)
    {
        if (queue.First is not { } first) return null;
        queue.RemoveFirst();
        return first.Value;
    }

    private static void CompleteReadyWaiters(
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready)
    {
        foreach (var (completion, lease) in ready)
        {
            if (!completion.TrySetResult(lease)) lease.Dispose();
        }
    }

    public void Dispose()
    {
        List<Waiter> waiters;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _configManager.OnConfigChanged -= OnConfigChanged;
            waiters = _queueWaiters.Concat(_backgroundWaiters).ToList();
            _queueWaiters.Clear();
            _backgroundWaiters.Clear();
            _idleCompletion.TrySetResult();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(
                new ObjectDisposedException(nameof(HealthCheckConnectionGate)));
        }
    }

    private static TaskCompletionSource CreateIdleCompletion(bool completed)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) completion.SetResult();
        return completion;
    }

    private sealed class Waiter(HealthCheckAdmissionPriority priority)
    {
        public HealthCheckAdmissionPriority Priority { get; } = priority;
        public TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Lease : IDisposable
    {
        private HealthCheckConnectionGate? _owner;

        internal Lease(HealthCheckConnectionGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
