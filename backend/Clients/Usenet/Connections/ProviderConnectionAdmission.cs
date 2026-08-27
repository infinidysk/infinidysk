using NzbWebDAV.Clients.Usenet.Concurrency;

namespace NzbWebDAV.Clients.Usenet.Connections;

internal enum ProviderConnectionKind
{
    Transfer,
    Metadata,
}

/// <summary>
/// Operation-aware admission in front of a single physical provider pool.
/// Transfers have a hard cap; metadata can use its base allocation plus a bounded
/// burst. Waiting transfers are admitted before metadata can re-borrow capacity.
/// </summary>
internal sealed class ProviderConnectionAdmission : IDisposable
{
    private readonly Func<int> _getEffectiveProviderLimit;
    private readonly int _configuredTransferLimit;
    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _transferHighWaiters = [];
    private readonly LinkedList<Waiter> _transferLowWaiters = [];
    private readonly LinkedList<Waiter> _metadataHighWaiters = [];
    private readonly LinkedList<Waiter> _metadataLowWaiters = [];

    private SemaphorePriorityOdds _priorityOdds;
    private int _activeTransfers;
    private int _activeMetadata;
    private int _transferAccumulatedOdds;
    private int _metadataAccumulatedOdds;
    private bool _disposed;

    internal bool IsDisposed
    {
        get
        {
            lock (_lock)
                return _disposed;
        }
    }

    public ProviderConnectionAdmission(
        Func<int> getEffectiveProviderLimit,
        int configuredTransferLimit,
        SemaphorePriorityOdds? priorityOdds = null)
    {
        _getEffectiveProviderLimit = getEffectiveProviderLimit
            ?? throw new ArgumentNullException(nameof(getEffectiveProviderLimit));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredTransferLimit);
        _configuredTransferLimit = configuredTransferLimit;
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
    }

    public Task<Lease> AcquireAsync(
        ProviderConnectionKind kind,
        SemaphorePriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (CanEnterImmediately(kind))
            {
                Enter(kind);
                return Task.FromResult(new Lease(this, kind));
            }

            var waiter = new Waiter(kind, priority);
            GetQueue(kind, priority).AddLast(waiter);

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

    public void UpdatePriorityOdds(SemaphorePriorityOdds priorityOdds)
    {
        ArgumentNullException.ThrowIfNull(priorityOdds);
        lock (_lock)
        {
            if (_disposed) return;
            _priorityOdds = priorityOdds;
        }
    }

    public ProviderConnectionAdmissionSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var budget = ProviderConnectionBudget.Calculate(
                _getEffectiveProviderLimit(),
                _configuredTransferLimit);
            return new ProviderConnectionAdmissionSnapshot(
                _configuredTransferLimit,
                budget.EffectiveTransferLimit,
                budget.BaseMetadataCapacity,
                budget.MetadataBurstAllowance,
                budget.MaxMetadataCapacity,
                _activeTransfers,
                _activeMetadata,
                _transferHighWaiters.Count + _transferLowWaiters.Count,
                _metadataHighWaiters.Count + _metadataLowWaiters.Count);
        }
    }

    private bool CanEnterImmediately(ProviderConnectionKind kind)
    {
        if (HasWaiters(kind)) return false;
        if (kind == ProviderConnectionKind.Metadata
            && HasWaiters(ProviderConnectionKind.Transfer)
            && CanEnter(ProviderConnectionKind.Transfer))
        {
            return false;
        }

        return CanEnter(kind);
    }

    private bool CanEnter(ProviderConnectionKind kind)
    {
        var budget = ProviderConnectionBudget.Calculate(
            _getEffectiveProviderLimit(),
            _configuredTransferLimit);
        if (_activeTransfers + _activeMetadata >= budget.EffectiveProviderLimit)
            return false;

        return kind switch
        {
            ProviderConnectionKind.Transfer =>
                _activeTransfers < budget.EffectiveTransferLimit,
            ProviderConnectionKind.Metadata =>
                _activeMetadata < budget.MaxMetadataCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private void Enter(ProviderConnectionKind kind)
    {
        if (kind == ProviderConnectionKind.Transfer)
            _activeTransfers++;
        else
            _activeMetadata++;
    }

    private void Release(ProviderConnectionKind kind)
    {
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready;
        lock (_lock)
        {
            if (kind == ProviderConnectionKind.Transfer)
                _activeTransfers--;
            else
                _activeMetadata--;

            if (_disposed) return;
            ready = DispatchWaiters();
        }

        CompleteReadyWaiters(ready);
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready;
        var removed = false;
        lock (_lock)
        {
            removed = GetQueue(waiter.Kind, waiter.Priority).Remove(waiter);
            ready = removed && !_disposed ? DispatchWaiters() : [];
        }

        if (removed)
            waiter.Completion.TrySetCanceled(cancellationToken);
        CompleteReadyWaiters(ready);
    }

    private List<(TaskCompletionSource<Lease> Completion, Lease Lease)> DispatchWaiters()
    {
        List<(TaskCompletionSource<Lease>, Lease)> ready = [];
        while (true)
        {
            ProviderConnectionKind? kind = null;
            if (HasWaiters(ProviderConnectionKind.Transfer)
                && CanEnter(ProviderConnectionKind.Transfer))
            {
                kind = ProviderConnectionKind.Transfer;
            }
            else if (HasWaiters(ProviderConnectionKind.Metadata)
                     && CanEnter(ProviderConnectionKind.Metadata))
            {
                kind = ProviderConnectionKind.Metadata;
            }

            if (kind is not { } selectedKind) break;

            var waiter = Dequeue(selectedKind);
            if (waiter is null) continue;
            Enter(selectedKind);
            ready.Add((waiter.Completion, new Lease(this, selectedKind)));
        }

        return ready;
    }

    private Waiter? Dequeue(ProviderConnectionKind kind)
    {
        var high = GetQueue(kind, SemaphorePriority.High);
        var low = GetQueue(kind, SemaphorePriority.Low);
        LinkedList<Waiter> preferred;
        LinkedList<Waiter> fallback;

        if (high.Count == 0)
        {
            preferred = low;
            fallback = high;
        }
        else if (low.Count == 0)
        {
            preferred = high;
            fallback = low;
        }
        else
        {
            ref var accumulatedOdds = ref kind == ProviderConnectionKind.Transfer
                ? ref _transferAccumulatedOdds
                : ref _metadataAccumulatedOdds;
            accumulatedOdds += _priorityOdds.LowPriorityOdds;
            preferred = high;
            fallback = low;
            if (accumulatedOdds >= 100)
            {
                (preferred, fallback) = (fallback, preferred);
                accumulatedOdds -= 100;
            }
        }

        return TakeFirst(preferred) ?? TakeFirst(fallback);
    }

    private static Waiter? TakeFirst(LinkedList<Waiter> queue)
    {
        if (queue.First is not { } first) return null;
        queue.RemoveFirst();
        return first.Value;
    }

    private bool HasWaiters(ProviderConnectionKind kind) =>
        GetQueue(kind, SemaphorePriority.High).Count > 0
        || GetQueue(kind, SemaphorePriority.Low).Count > 0;

    private LinkedList<Waiter> GetQueue(
        ProviderConnectionKind kind,
        SemaphorePriority priority) => (kind, priority) switch
        {
            (ProviderConnectionKind.Transfer, SemaphorePriority.High) => _transferHighWaiters,
            (ProviderConnectionKind.Transfer, SemaphorePriority.Low) => _transferLowWaiters,
            (ProviderConnectionKind.Metadata, SemaphorePriority.High) => _metadataHighWaiters,
            (ProviderConnectionKind.Metadata, SemaphorePriority.Low) => _metadataLowWaiters,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static void CompleteReadyWaiters(
        List<(TaskCompletionSource<Lease> Completion, Lease Lease)> ready)
    {
        foreach (var (completion, lease) in ready)
        {
            if (!completion.TrySetResult(lease))
                lease.Dispose();
        }
    }

    public void Dispose()
    {
        List<Waiter> waiters;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waiters = _transferHighWaiters
                .Concat(_transferLowWaiters)
                .Concat(_metadataHighWaiters)
                .Concat(_metadataLowWaiters)
                .ToList();
            _transferHighWaiters.Clear();
            _transferLowWaiters.Clear();
            _metadataHighWaiters.Clear();
            _metadataLowWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.Completion.TrySetException(
                new ObjectDisposedException(nameof(ProviderConnectionAdmission)));
    }

    private sealed class Waiter(ProviderConnectionKind kind, SemaphorePriority priority)
    {
        public ProviderConnectionKind Kind { get; } = kind;
        public SemaphorePriority Priority { get; } = priority;
        public TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Lease : IDisposable
    {
        private ProviderConnectionAdmission? _owner;
        private readonly ProviderConnectionKind _kind;

        internal Lease(ProviderConnectionAdmission owner, ProviderConnectionKind kind)
        {
            _owner = owner;
            _kind = kind;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_kind);
        }
    }
}
