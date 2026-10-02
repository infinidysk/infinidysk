namespace NzbWebDAV.Services.Prefetch;

/// <summary>
/// Attributes existing BODY payload observations to one warming job. The observer
/// is memory-only; the awaited transport flush hook settles local SQLite credits.
/// Credits survive crashes conservatively and are refunded only after all body
/// completion callbacks have run, including failed/cancelled operations.
/// </summary>
public sealed class PrefetchWireBudget : IAsyncDisposable
{
    private const long CreditSize = 1024 * 1024;
    private static readonly AsyncLocal<PrefetchWireBudget?> Ambient = new();
    private readonly PrefetchJobStore _store;
    private readonly Func<long> _limit;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cancellation;
    private readonly SemaphoreSlim _settlement = new(1, 1);
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _closing;
    private long _pending;
    private long _received;
    private long _credit;
    private string? _creditDay;
    private int _exceeded;
    private int _accountingFailed;
    private long _previousLimit;

    public PrefetchWireBudget(PrefetchJobStore store, Func<long> limit, CancellationToken ct, TimeProvider? clock = null)
    { _store = store; _limit = limit; _clock = clock ?? TimeProvider.System; _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, store.WireBudgetFailure); }
    public static PrefetchWireBudget? Current => Ambient.Value;
    public CancellationToken Token => _cancellation.Token;
    public long ReceivedBytes => Interlocked.Read(ref _received);
    public bool Exceeded => Volatile.Read(ref _exceeded) != 0;
    public bool AccountingFailed => Volatile.Read(ref _accountingFailed) != 0;
    public IDisposable Enter() => new AmbientLease(this);
    public OperationLease BeginOperation()
    {
        lock (_gate)
        {
            if (_closing) throw new OperationCanceledException(Token);
            _operations++;
            return new(this);
        }
    }
    public void Observe(int bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _received, bytes);
        Interlocked.Add(ref _pending, bytes);
    }

    public async ValueTask SettleAsync(CancellationToken ct)
    {
        await SettleCoreAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async ValueTask<bool> PrepareReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await SettleCoreAsync(prepare: true).ConfigureAwait(false);
    }

    private async Task<bool> SettleCoreAsync(bool prepare = false)
    {
        await _settlement.WaitAsync().ConfigureAwait(false);
        long observed = 0;
        try
        {
            if (_store.WireBudgetBlocked)
            {
                Volatile.Write(ref _accountingFailed, 1);
                Volatile.Write(ref _exceeded, 1);
                _cancellation.Cancel();
            }
            if (AccountingFailed) throw new OperationCanceledException(Token);
            var pending = Interlocked.Exchange(ref _pending, 0);
            observed = pending;
            if (pending == 0 && !prepare) return !Exceeded;
            var day = _clock.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var limit = _limit();
            if (_creditDay != day || limit != 0 && (_previousLimit == 0 || limit < _previousLimit))
            {
                if (_creditDay is { } previous && _credit > 0)
                    await Task.Run(() => _store.ReturnDailyCredit(_credit, previous)).ConfigureAwait(false);
                _credit = 0;
                _creditDay = day;
            }
            _previousLimit = limit;
            var covered = Math.Min(_credit, pending);
            _credit -= covered;
            pending -= covered;
            if (pending == 0 && (!prepare || _credit > 0)) return !Exceeded;
            var grant = await Task.Run(() => _store.ReserveDailyCredit(Math.Max(CreditSize, pending), limit, day)).ConfigureAwait(false);
            if (grant < pending || prepare && grant == 0)
            {
                // The observer measures bytes already received. Record the bounded
                // in-flight excess too; never hide failed/cancelled provider traffic.
                await Task.Run(() => _store.ReserveDailyCredit(pending - grant, 0, day)).ConfigureAwait(false);
                Volatile.Write(ref _exceeded, 1);
                _cancellation.Cancel();
                _credit = 0;
            }
            else _credit = grant - pending;
            return !Exceeded;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // The durable result may be uncertain. Keep debt and all outstanding
            // reservations conservative; never refund or continue this budget.
            Interlocked.Add(ref _pending, observed);
            Volatile.Write(ref _accountingFailed, 1);
            Volatile.Write(ref _exceeded, 1);
            _store.BlockWireBudget();
            _cancellation.Cancel();
            throw new OperationCanceledException("Warming payload accounting is unavailable.", exception, Token);
        }
        finally { _settlement.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            if (_operations == 0) _completed.TrySetResult();
        }
        _cancellation.Cancel();
        await _completed.Task.ConfigureAwait(false);
        if (!AccountingFailed && !_store.WireBudgetBlocked)
        {
            try
            {
                await SettleCoreAsync().ConfigureAwait(false);
                if (_creditDay is { } day && _credit > 0)
                    await Task.Run(() => _store.ReturnDailyCredit(_credit, day)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { _store.BlockWireBudget(); }
        }
        _credit = 0;
        _cancellation.Dispose();
        _settlement.Dispose();
    }

    public sealed class OperationLease : IDisposable
    {
        private PrefetchWireBudget? _owner;
        internal OperationLease(PrefetchWireBudget owner) => _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner) return;
            lock (owner._gate)
                if (--owner._operations == 0 && owner._closing) owner._completed.TrySetResult();
        }
    }
    private sealed class AmbientLease : IDisposable
    {
        private readonly PrefetchWireBudget? _previous;
        public AmbientLease(PrefetchWireBudget budget) { _previous = Ambient.Value; Ambient.Value = budget; }
        public void Dispose() => Ambient.Value = _previous;
    }
}
