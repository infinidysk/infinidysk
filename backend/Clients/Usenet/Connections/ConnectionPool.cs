using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using Serilog;

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
    private static readonly TimeSpan DefaultKeepAliveBorrowTimeout =
        TimeSpan.FromMilliseconds(250);

    private enum ConnectionAcquisitionMode
    {
        PreferIdle,
        RequireIdle,
        CreateNew,
    }

    public TimeSpan IdleTimeout { get; }
    public int MaxConnections => _maxConnections;
    public int WarmConnectionFloor => _warmConnectionFloor;
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
    private readonly int _warmConnectionFloor;
    private readonly Func<T, CancellationToken, Task>? _keepAlive;
    private readonly Func<CancellationToken, Task<IDisposable?>>? _keepAliveAdmission;
    private readonly TimeSpan _keepAliveBorrowTimeout;
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
        Action<int, int>? onConnectionLimitLearned = null,
        int warmConnectionFloor = 0,
        Func<T, CancellationToken, Task>? keepAlive = null,
        Func<CancellationToken, Task<IDisposable?>>? keepAliveAdmission = null,
        TimeSpan? keepAliveBorrowTimeout = null)
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
        _warmConnectionFloor = Math.Clamp(warmConnectionFloor, 0, maxConnections);
        _keepAlive = _warmConnectionFloor > 0 ? keepAlive : null;
        _keepAliveAdmission = _warmConnectionFloor > 0 ? keepAliveAdmission : null;
        _keepAliveBorrowTimeout = keepAliveBorrowTimeout ?? DefaultKeepAliveBorrowTimeout;
        if (_keepAliveBorrowTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keepAliveBorrowTimeout));
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
    public Task<ConnectionLock<T>> GetConnectionLockAsync
    (
        SemaphorePriority priority,
        CancellationToken cancellationToken = default
    ) => GetRequiredConnectionLockAsync(priority, cancellationToken);

    private async Task<ConnectionLock<T>> GetRequiredConnectionLockAsync(
        SemaphorePriority priority,
        CancellationToken cancellationToken) =>
        await GetConnectionLockCoreAsync(
                priority,
                ConnectionAcquisitionMode.PreferIdle,
                cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException("A normal pool borrow did not return a connection.");

    private async Task<ConnectionLock<T>?> GetConnectionLockCoreAsync
    (
        SemaphorePriority priority,
        ConnectionAcquisitionMode mode,
        CancellationToken cancellationToken,
        ISet<object>? excludedConnections = null
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

            if (mode != ConnectionAcquisitionMode.CreateNew)
            {
                reusedConnection = TryTakeIdleConnection(
                    out reused!,
                    excludedConnections);
                if (reusedConnection)
                    Interlocked.Increment(ref _connectionsReused);
            }
        }
        if (reusedConnection)
        {
            TriggerConnectionPoolChangedEvent();
            return BuildLock(reused!, wasReused: true);
        }
        if (mode == ConnectionAcquisitionMode.RequireIdle)
        {
            ReleaseGateIfActive();
            return null;
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

                if (mode == ConnectionAcquisitionMode.PreferIdle)
                {
                    reusedConnection = TryTakeIdleConnection(out reused!);
                    if (reusedConnection)
                        Interlocked.Increment(ref _connectionsReused);
                }
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
            catch (Exception factoryError) when (factoryError is not OutOfMemoryException)
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

    private bool TryTakeIdleConnection(
        out T connection,
        ISet<object>? excludedConnections = null)
    {
        List<Pooled>? excluded = null;
        try
        {
            while (_idleConnections.TryPop(out var item))
            {
                if (item.IsExpired(IdleTimeout))
                {
                    // Stale – destroy and continue looking.
                    DisposeConnection(item.Connection);
                    Interlocked.Decrement(ref _live);
                    Interlocked.Increment(ref _staleEvictions);
                    TriggerConnectionPoolChangedEvent();
                    continue;
                }

                if (excludedConnections?.Contains(item.Connection!) == true)
                {
                    (excluded ??= []).Add(item);
                    continue;
                }

                connection = item.Connection;
                return true;
            }
        }
        finally
        {
            if (excluded is not null)
            {
                for (var i = excluded.Count - 1; i >= 0; i--)
                    _idleConnections.Push(excluded[i]);
            }
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
            await EnsureWarmFloorAsync(_sweepCts.Token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(IdleTimeout / 2);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
                await SweepOnceAsync(cancellationToken: _sweepCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* normal on disposal */
        }
    }

    internal Task SweepOnceForTestsAsync(
        long? nowMillis = null,
        CancellationToken cancellationToken = default) =>
        SweepOnceAsync(nowMillis, cancellationToken);

    private async Task SweepOnceAsync(long? nowMillis = null, CancellationToken cancellationToken = default)
    {
        var now = nowMillis ?? Environment.TickCount64;
        var survivors = new List<Pooled>();
        var isAnyConnectionFreed = false;
        var effectiveWarmFloor = Math.Min(_warmConnectionFloor, EffectiveMaxConnections);

        while (_idleConnections.TryPop(out var item))
        {
            if (item.IsExpired(IdleTimeout, now) && Volatile.Read(ref _live) > effectiveWarmFloor)
            {
                DisposeConnection(item.Connection);
                Interlocked.Decrement(ref _live);
                Interlocked.Increment(ref _connectionsDestroyed);
                isAnyConnectionFreed = true;
            }
            else
            {
                survivors.Add(item);
            }
        }

        // Restore survivors before borrowing warm sockets through the normal gate. This
        // prevents hidden keepalive sockets from making the pool open above its ceiling.
        // Preserve original LIFO order.
        for (var i = survivors.Count - 1; i >= 0; i--)
            _idleConnections.Push(survivors[i]);

        // Keep-alive only borrows an already-idle socket and gives up quickly under real
        // traffic. Returned sockets are excluded from the rest of this sweep so each DATE
        // still targets a distinct connection without retaining physical permits.
        if (_keepAlive is not null)
        {
            var warmCount = Math.Min(effectiveWarmFloor, survivors.Count);
            var pingedConnections = new HashSet<object>(
                ReferenceEqualityComparer.Instance);
            for (var i = 0; i < warmCount; i++)
            {
                IDisposable? admission = null;
                ConnectionLock<T>? connection = null;
                try
                {
                    using (var borrowCts = CancellationTokenSource.CreateLinkedTokenSource(
                               cancellationToken))
                    {
                        borrowCts.CancelAfter(_keepAliveBorrowTimeout);
                        if (_keepAliveAdmission is not null)
                        {
                            admission = await _keepAliveAdmission(borrowCts.Token)
                                .ConfigureAwait(false);
                        }

                        connection = await GetConnectionLockCoreAsync(
                                SemaphorePriority.Low,
                                ConnectionAcquisitionMode.RequireIdle,
                                borrowCts.Token,
                                pingedConnections)
                            .ConfigureAwait(false);
                    }

                    if (connection is null)
                    {
                        Log.Debug(
                            "Skipping connection-pool keep-alive because no unpinged idle connection remained.");
                        break;
                    }

                    pingedConnections.Add(connection.Connection!);
                    try
                    {
                        await _keepAlive(connection.Connection, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // An idle DATE failure only proves this socket is stale. Replace
                        // it without recording a provider-traffic failure.
                        connection.Replace();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Log.Debug(
                        "Skipping connection-pool keep-alive because its idle borrow timed out.");
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Debug(
                        e,
                        "Skipping connection-pool keep-alive because admission or idle acquisition failed.");
                    break;
                }
                finally
                {
                    connection?.Dispose();
                    admission?.Dispose();
                }
            }
        }

        if (isAnyConnectionFreed)
            TriggerConnectionPoolChangedEvent();

        await EnsureWarmFloorAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureWarmFloorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               Volatile.Read(ref _live) < Math.Min(_warmConnectionFloor, EffectiveMaxConnections))
        {
            try
            {
                // A warm connection is borrowed only while it is being opened, then
                // returned immediately. Cached warm connections never retain a gate permit.
                using (await GetConnectionLockCoreAsync(
                           SemaphorePriority.Low,
                           ConnectionAcquisitionMode.CreateNew,
                           cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(
                           "A warm-floor connection could not be created."))
                {
                    // Returning the lock to the pool establishes one idle warm connection.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Do not spin on a provider that is unavailable at startup. The next
                // sweep retries the floor; connection-limit learning still applies.
                return;
            }
        }
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
