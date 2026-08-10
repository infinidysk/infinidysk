using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Lifetime connection churn for one pool. Distinguishes a pool that opened its
/// connections once from one that keeps replacing them, which the live/idle
/// gauges cannot show.
/// </summary>
public sealed record ConnectionPoolChurn(
    long ConnectionsOpened,
    long ConnectionsReused,
    long ConnectionsDestroyed,
    long StaleEvictions,
    long HandshakeFailures,
    long GateWaitMs,
    long HandshakeWaitMs);

/// <summary>
/// Thread-safe, lazy connection pool.
/// <para>
/// *  Connections are created through a user-supplied factory (sync or async).<br/>
/// *  At most <c>maxConnections</c> live instances exist at any time.<br/>
/// *  Concurrent factory invocations (connect+auth) are capped so a cold burst
///    ramps the pool instead of opening dozens of TLS handshakes at once.<br/>
/// *  Idle connections older than <see cref="IdleTimeout"/> are disposed
///    automatically by a background sweeper.<br/>
/// *  <see cref="Dispose"/> / <see cref="DisposeAsync"/> stop the sweeper and
///    dispose all cached connections.  Borrowed handles returned afterwards are
///    destroyed immediately.
/// *  Note: This class was authored by ChatGPT 3o
/// </para>
/// </summary>
public sealed class ConnectionPool<T> : IDisposable, IAsyncDisposable
{
    /* -------------------------------- configuration -------------------------------- */

    /// <summary>
    /// Caps simultaneous connect+auth factory calls so a cold burst of borrowers
    /// ramps the pool instead of slamming dozens of TLS handshakes at once.
    /// </summary>
    private const int MaxConcurrentHandshakes = 3;

    public TimeSpan IdleTimeout { get; }
    public int MaxConnections => _maxConnections;
    public int EffectiveMaxConnections => Volatile.Read(ref _effectiveMaxConnections);
    public int? LearnedConnectionLimit => _learnedConnectionLimit;
    public int LiveConnections => _live;
    public int IdleConnections => _idleConnections.Count;
    public int ActiveConnections => _live - _idleConnections.Count;
    public int AvailableConnections => Math.Max(0, EffectiveMaxConnections - ActiveConnections);
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly int _maxConnections;
    private readonly Func<Exception, int?>? _connectionLimitDetector;
    private readonly Action<int, int>? _onConnectionLimitLearned;

    /* --------------------------------- state --------------------------------------- */

    private readonly ConcurrentStack<Pooled> _idleConnections = new();
    private readonly PrioritizedSemaphore _gate;
    private readonly SemaphoreSlim _handshakeGate = new(MaxConcurrentHandshakes, MaxConcurrentHandshakes);
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweeperTask; // keeps timer alive
    private readonly Lock _lifecycleLock = new();

    private int _live; // number of connections currently alive
    private int _disposed; // 0 == false, 1 == true
    private int _effectiveMaxConnections;
    private int? _learnedConnectionLimit;

    // Lifetime churn counters. A pool that keeps destroying and re-opening connections
    // pays the handshake cost repeatedly and can never reach its configured width, which
    // is invisible from the live/idle gauges alone.
    private long _connectionsOpened;
    private long _connectionsReused;
    private long _connectionsDestroyed;
    private long _staleEvictions;
    private long _handshakeFailures;
    private long _gateWaitTicks;
    private long _handshakeWaitTicks;

    public ConnectionPoolChurn GetChurn() => new(
        ConnectionsOpened: Interlocked.Read(ref _connectionsOpened),
        ConnectionsReused: Interlocked.Read(ref _connectionsReused),
        ConnectionsDestroyed: Interlocked.Read(ref _connectionsDestroyed),
        StaleEvictions: Interlocked.Read(ref _staleEvictions),
        HandshakeFailures: Interlocked.Read(ref _handshakeFailures),
        GateWaitMs: Interlocked.Read(ref _gateWaitTicks) / TimeSpan.TicksPerMillisecond,
        HandshakeWaitMs: Interlocked.Read(ref _handshakeWaitTicks) / TimeSpan.TicksPerMillisecond);

    /* ------------------------------------------------------------------------------ */

    public ConnectionPool(
        int maxConnections,
        Func<CancellationToken, ValueTask<T>> connectionFactory,
        TimeSpan? idleTimeout = null,
        SemaphorePriorityOdds? priorityOdds = null,
        Func<Exception, int?>? connectionLimitDetector = null,
        Action<int, int>? onConnectionLimitLearned = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);

        _factory = connectionFactory
                   ?? throw new ArgumentNullException(nameof(connectionFactory));
        // Keep this below typical NNTP server-side idle timeouts (30-180s);
        // connections idled longer are closed by the server and fail on next use.
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60);
        if (IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        _maxConnections = maxConnections;
        _effectiveMaxConnections = maxConnections;
        _connectionLimitDetector = connectionLimitDetector;
        _onConnectionLimitLearned = onConnectionLimitLearned;
        _gate = new PrioritizedSemaphore(maxConnections, maxConnections, priorityOdds);
        _sweeperTask = Task.Run(SweepLoop); // background idle-reaper
    }

    /// <summary>
    /// Re-arms the gate's High-vs-Low admission odds (Streaming Priority) in place, so a
    /// settings save changes contention behavior without replacing live TLS connections.
    /// </summary>
    public void UpdatePriorityOdds(SemaphorePriorityOdds odds) => _gate.UpdatePriorityOdds(odds);

    /* ============================== public API ==================================== */

    /// <summary>
    /// Borrow a connection while reserving capacity for higher-priority callers.
    /// Waits until at least (`reservedCount` + 1) slots are free before acquiring one,
    /// ensuring that after acquisition at least `reservedCount` remain available.
    /// </summary>
    public async Task<ConnectionLock<T>> GetConnectionLockAsync
    (
        SemaphorePriority priority,
        CancellationToken cancellationToken = default
    )
    {
        // Make caller cancellation also cancel the wait on the gate.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token);

        var gateWaitStarted = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(priority, linked.Token).ConfigureAwait(false);
        Interlocked.Add(ref _gateWaitTicks, Stopwatch.GetElapsedTime(gateWaitStarted).Ticks);

        // Claim an idle connection atomically with respect to disposal. Once popped,
        // it is active and disposal leaves it for the borrower to return or destroy.
        T? reused = default;
        var reusedConnection = false;
        lock (_lifecycleLock)
        {
            if (_disposed == 1)
                ThrowDisposed();

            reusedConnection = TryTakeIdleConnection(out reused!);
            if (reusedConnection)
                Interlocked.Increment(ref _connectionsReused);
        }
        if (reusedConnection)
        {
            TriggerConnectionPoolChangedEvent();
            return BuildLock(reused!, wasReused: true);
        }

        // Need a fresh connection. Pace handshakes so a cold burst of borrowers
        // does not open dozens of TLS sessions in parallel. While waiting, other
        // connections may return to the idle stack — prefer those over a new handshake.
        try
        {
            var handshakeWaitStarted = Stopwatch.GetTimestamp();
            await _handshakeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _handshakeWaitTicks, Stopwatch.GetElapsedTime(handshakeWaitStarted).Ticks);
        }
        catch
        {
            ReleaseGateIfActive();
            throw;
        }

        try
        {
            reused = default;
            reusedConnection = false;
            lock (_lifecycleLock)
            {
                if (_disposed == 1)
                    ThrowDisposed();

                reusedConnection = TryTakeIdleConnection(out reused!);
                if (reusedConnection)
                    Interlocked.Increment(ref _connectionsReused);
            }
            if (reusedConnection)
            {
                TriggerConnectionPoolChangedEvent();
                return BuildLock(reused!, wasReused: true);
            }

            T conn;
            try
            {
                conn = await _factory(linked.Token).ConfigureAwait(false);
            }
            catch (Exception factoryError)
            {
                Interlocked.Increment(ref _handshakeFailures);
                TryShrinkOnConnectionLimit(factoryError);
                ReleaseGateIfActive(); // free the permit on failure
                throw;
            }

            var disposeConnection = false;
            lock (_lifecycleLock)
            {
                if (_disposed == 1)
                {
                    disposeConnection = true;
                }
                else
                {
                    Interlocked.Increment(ref _connectionsOpened);
                    Interlocked.Increment(ref _live);
                }
            }

            if (disposeConnection)
            {
                DisposeConnection(conn);
                ThrowDisposed();
            }

            TriggerConnectionPoolChangedEvent();
            return BuildLock(conn, wasReused: false);
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (_disposed == 0)
                    _handshakeGate.Release();
            }
        }

        ConnectionLock<T> BuildLock(T c, bool wasReused)
            => new(c, Return, Destroy, wasReused);

        static void ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ConnectionPool<T>));
    }

    private void ReleaseGateIfActive()
    {
        lock (_lifecycleLock)
        {
            if (_disposed == 0)
                _gate.Release();
        }
    }

    private bool TryTakeIdleConnection(out T connection)
    {
        while (_idleConnections.TryPop(out var item))
        {
            if (!item.IsExpired(IdleTimeout))
            {
                connection = item.Connection;
                return true;
            }

            // Stale – destroy and continue looking.
            DisposeConnection(item.Connection);
            Interlocked.Decrement(ref _live);
            Interlocked.Increment(ref _staleEvictions);
            TriggerConnectionPoolChangedEvent();
        }

        connection = default!;
        return false;
    }

    /* ========================== core helpers ====================================== */

    private readonly record struct Pooled(T Connection, long LastTouchedMillis)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(TimeSpan idle, long nowMillis = 0)
        {
            if (nowMillis == 0) nowMillis = Environment.TickCount64;
            return unchecked(nowMillis - LastTouchedMillis) >= idle.TotalMilliseconds;
        }
    }

    private void Return(T connection)
    {
        var disposeConnection = false;
        var notify = false;
        lock (_lifecycleLock)
        {
            if (_disposed == 1)
            {
                Interlocked.Decrement(ref _live);
                disposeConnection = true;
            }
            else
            {
                _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
                _gate.Release();
                notify = true;
            }
        }

        if (disposeConnection)
            DisposeConnection(connection);
        if (notify)
            TriggerConnectionPoolChangedEvent();
    }

    private void Destroy(T connection)
    {
        // When a lock requests replacement, we dispose the connection instead of reusing.
        DisposeConnection(connection);
        var notify = false;
        lock (_lifecycleLock)
        {
            Interlocked.Decrement(ref _live);
            Interlocked.Increment(ref _connectionsDestroyed);
            if (_disposed == 0)
            {
                _gate.Release();
                notify = true;
            }
        }

        if (notify)
            TriggerConnectionPoolChangedEvent();
    }

    private void TriggerConnectionPoolChangedEvent()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        OnConnectionPoolChanged?.Invoke(this, new ConnectionPoolStats.ConnectionPoolChangedEventArgs(
            _live,
            _idleConnections.Count,
            EffectiveMaxConnections
        ));
    }

    /// <summary>
    /// When the server rejects a login with "502 connection limit (N) reached", shrink the
    /// gate so subsequent refills stop hitting the same rejection at the same width.
    /// Monotonic — only ever shrinks, never grows. The check-compute-write is atomic under
    /// <see cref="_lifecycleLock"/> so concurrent factory failures fire the callback at most
    /// once per distinct effective value.
    /// </summary>
    private void TryShrinkOnConnectionLimit(Exception exception)
    {
        if (_connectionLimitDetector?.Invoke(exception) is not { } learned)
            return;

        // ~10% headroom for server-side teardown sockets; hard floor at 1.
        var headroom = Math.Max(2, learned / 10);
        var candidate = Math.Max(learned - headroom, 1);

        int newEffective;
        bool shrank;
        lock (_lifecycleLock)
        {
            newEffective = Math.Min(candidate, _effectiveMaxConnections);
            shrank = newEffective < _effectiveMaxConnections;
            if (shrank)
            {
                _effectiveMaxConnections = newEffective;
                _learnedConnectionLimit = learned;
            }
        }

        if (!shrank) return;

        _gate.UpdateMaxAllowed(newEffective);
        TriggerConnectionPoolChangedEvent();
        _onConnectionLimitLearned?.Invoke(learned, newEffective);
    }

    /* =================== idle sweeper (background) ================================= */

    private async Task SweepLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(IdleTimeout / 2);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
                SweepOnce();
        }
        catch (OperationCanceledException)
        {
            /* normal on disposal */
        }
    }

    private void SweepOnce()
    {
        var now = Environment.TickCount64;
        var survivors = new List<Pooled>();
        var isAnyConnectionFreed = false;

        while (_idleConnections.TryPop(out var item))
        {
            if (item.IsExpired(IdleTimeout, now))
            {
                DisposeConnection(item.Connection);
                Interlocked.Decrement(ref _live);
                isAnyConnectionFreed = true;
            }
            else
            {
                survivors.Add(item);
            }
        }

        // Preserve original LIFO order.
        for (var i = survivors.Count - 1; i >= 0; i--)
            _idleConnections.Push(survivors[i]);

        if (isAnyConnectionFreed)
            TriggerConnectionPoolChangedEvent();
    }

    /* ------------------------- dispose helpers ------------------------------------ */

    private static void DisposeConnection(T conn)
    {
        if (conn is IDisposable d)
            d.Dispose();
    }

    /* -------------------------- IAsyncDisposable ---------------------------------- */

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposed == 1) return;
            _disposed = 1;

            // Drop handlers before draining so late Return/Destroy from in-flight locks
            // cannot overwrite the live generation's connection-count websocket updates.
            OnConnectionPoolChanged = null;
        }

        await _sweepCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _sweeperTask.ConfigureAwait(false); // await clean sweep exit
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }

        // Drain and dispose cached items.
        while (_idleConnections.TryPop(out var item))
            DisposeConnection(item.Connection);

        lock (_lifecycleLock)
        {
            _sweepCts.Dispose();
            _gate.Dispose();
            _handshakeGate.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /* ----------------------------- IDisposable ------------------------------------ */

    public void Dispose()
    {
        _ = DisposeAsync().AsTask(); // fire-and-forget synchronous path
    }
}
