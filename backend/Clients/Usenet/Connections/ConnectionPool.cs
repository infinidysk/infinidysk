using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Logging;
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

    /// <summary>
    /// Raised after live/idle/effective-max counts change. This is post-state telemetry:
    /// handlers cannot vote on admission or replacement. Subscriber failures are isolated
    /// and logged. Dispatch snapshots the invocation list at the start of each notification.
    /// </summary>
    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly int _maxConnections;
    private readonly int _warmConnectionFloor;
    private readonly Func<T, CancellationToken, Task>? _keepAlive;
    private readonly Func<CancellationToken, Task<IDisposable?>>? _keepAliveAdmission;
    private readonly TimeSpan _keepAliveBorrowTimeout;
    private readonly Func<Exception, int?>? _connectionLimitDetector;
    private readonly Action<int, int>? _onConnectionLimitLearned;
    private readonly string _diagnosticName;
    private readonly long _replacementHandshakeSpacingMs;
    private readonly TimeProvider _timeProvider;

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
    private long _nextReplacementHandshakeAtMs;
    private long _replacementPacingUntilMs;
    private readonly Dictionary<long, ReplacementPacingReservation> _cancelledPacingReservations = [];
    private int _consecutiveHandshakeFailures;

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
        string? diagnosticName = null,
        TimeSpan? replacementHandshakeSpacing = null,
        TimeProvider? timeProvider = null,
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
        _diagnosticName = string.IsNullOrWhiteSpace(diagnosticName) ? typeof(T).Name : diagnosticName;
        _replacementHandshakeSpacingMs = Math.Max(
            0, (long)(replacementHandshakeSpacing ?? TimeSpan.Zero).TotalMilliseconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
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
    ) => GetConnectionLockCoreAsync(priority, preferIdle: true, cancellationToken);

    private async Task<ConnectionLock<T>> GetConnectionLockCoreAsync
    (
        SemaphorePriority priority,
        bool preferIdle,
        CancellationToken cancellationToken
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
        var staleEvicted = false;
        lock (_lifecycleLock)
        {
            if (_disposed == 1)
                ThrowDisposed();

            if (preferIdle)
            {
                reusedConnection = TryTakeIdleConnection(out reused!, out staleEvicted);
                if (reusedConnection)
                    Interlocked.Increment(ref _connectionsReused);
            }
        }
        if (reusedConnection || staleEvicted)
            TriggerConnectionPoolChangedEvent();
        if (reusedConnection)
            return BuildLock(reused!, wasReused: true);

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
            staleEvicted = false;
            lock (_lifecycleLock)
            {
                if (_disposed == 1)
                    ThrowDisposed();

                if (preferIdle)
                {
                    reusedConnection = TryTakeIdleConnection(out reused!, out staleEvicted);
                    if (reusedConnection)
                        Interlocked.Increment(ref _connectionsReused);
                }
            }
            if (reusedConnection || staleEvicted)
                TriggerConnectionPoolChangedEvent();
            if (reusedConnection)
                return BuildLock(reused!, wasReused: true);

            T conn;
            ReplacementPacingReservation? pacingReservation = null;
            try
            {
                pacingReservation = await PaceReplacementHandshakeAsync(linked.Token)
                    .ConfigureAwait(false);

                // The replacement attempt is now admitted. Its start consumes this slot
                // even if TCP/TLS/authentication is later canceled.
                CommitReplacementPacing(pacingReservation);
                pacingReservation = null;

                conn = await _factory(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // PaceReplacementHandshakeAsync rolls back internally if its delay is
                // canceled. A started factory keeps its spacing reservation.
                ReleaseGateIfActive();
                throw;
            }
            catch (Exception factoryError) when (factoryError is not OutOfMemoryException)
            {
                Interlocked.Increment(ref _handshakeFailures);
                var consecutiveFailures = Interlocked.Increment(ref _consecutiveHandshakeFailures);
                ArmReplacementPacing(GetHandshakeFailureBackoffMs(consecutiveFailures));
                TryShrinkOnConnectionLimit(factoryError);
                ReleaseGateIfActive(); // free the permit on failure
                throw;
            }

            Interlocked.Exchange(ref _consecutiveHandshakeFailures, 0);

            var disposeConnection = false;
            var connectionCreated = false;
            var createdLive = 0;
            var createdIdle = 0;
            var createdMax = 0;
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
                    connectionCreated = true;
                    createdLive = _live;
                    createdIdle = _idleConnections.Count;
                    createdMax = EffectiveMaxConnections;
                }
            }

            if (connectionCreated)
            {
                Log.Debug(
                    "NNTP connection created for {Provider}; connectionHash={ConnectionHash} live={Live} idle={Idle} active={Active} max={Max}",
                    _diagnosticName, ConnectionHash(conn), createdLive, createdIdle,
                    createdLive - createdIdle, createdMax);
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

    private async Task<ConnectionLock<T>?> TryGetIdleConnectionLockAsync(
        SemaphorePriority priority,
        ISet<object> excludedConnections,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token);

        var gateWaitStarted = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(priority, linked.Token).ConfigureAwait(false);
        Interlocked.Add(ref _gateWaitTicks, Stopwatch.GetElapsedTime(gateWaitStarted).Ticks);

        var releaseGate = true;
        try
        {
            T? reused = default;
            var reusedConnection = false;
            var staleEvicted = false;
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed == 1, this);
                reusedConnection = TryTakeIdleConnection(
                    out reused!,
                    out staleEvicted,
                    excludedConnections);
                if (reusedConnection)
                    Interlocked.Increment(ref _connectionsReused);
            }

            if (reusedConnection || staleEvicted)
                TriggerConnectionPoolChangedEvent();

            if (!reusedConnection)
                return null;

            releaseGate = false;
            return new ConnectionLock<T>(
                reused!,
                Return,
                Destroy,
                wasReused: true);
        }
        finally
        {
            if (releaseGate)
                ReleaseGateIfActive();
        }
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
        out bool staleEvicted,
        ISet<object>? excludedConnections = null)
    {
        staleEvicted = false;
        List<Pooled>? excluded = null;
        try
        {
            while (_idleConnections.TryPop(out var item))
            {
                if (item.IsExpired(IdleTimeout))
                {
                    // Stale – destroy and continue looking. Notify after the caller
                    // leaves _lifecycleLock so observer code never runs under it.
                    DisposeConnection(item.Connection);
                    Interlocked.Decrement(ref _live);
                    Interlocked.Increment(ref _staleEvictions);
                    staleEvicted = true;
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
        var returnedLive = 0;
        var returnedIdle = 0;
        var returnedMax = 0;
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
                returnedLive = _live;
                returnedIdle = _idleConnections.Count;
                returnedMax = EffectiveMaxConnections;
            }
        }

        if (notify)
        {
            Log.Debug(
                "NNTP connection returned to pool for {Provider}; connectionHash={ConnectionHash} live={Live} idle={Idle} active={Active} max={Max}",
                _diagnosticName, ConnectionHash(connection), returnedLive, returnedIdle,
                returnedLive - returnedIdle, returnedMax);
        }

        if (disposeConnection)
            DisposeConnection(connection);
        if (notify)
            TriggerConnectionPoolChangedEvent();
    }

    private void Destroy(T connection, string? reason)
    {
        // When a lock requests replacement, we dispose the connection instead of reusing.
        DisposeConnection(connection);
        var notify = false;
        lock (_lifecycleLock)
        {
            Interlocked.Decrement(ref _live);
            Interlocked.Increment(ref _connectionsDestroyed);
            if (_replacementHandshakeSpacingMs > 0)
                ArmReplacementPacingUnderLock(_replacementHandshakeSpacingMs);
            if (_disposed == 0)
            {
                _gate.Release();
                notify = true;
            }
        }

        if (notify)
            TriggerConnectionPoolChangedEvent();

        Log.Debug(
            "NNTP connection disposed for {Provider}; connectionHash={ConnectionHash} reason={Reason} live={Live} idle={Idle} active={Active} max={Max}",
            _diagnosticName, ConnectionHash(connection), reason ?? "replacement requested",
            _live, _idleConnections.Count, _live - _idleConnections.Count, EffectiveMaxConnections);
    }

    private readonly record struct ReplacementPacingReservation(
        long PreviousDeadlineMs,
        long ReservedDeadlineMs,
        long PreviousPacingUntilMs,
        long ReservedPacingUntilMs);

    private async Task<ReplacementPacingReservation?> PaceReplacementHandshakeAsync(
        CancellationToken cancellationToken)
    {
        long delayMs;
        ReplacementPacingReservation? reservation = null;
        lock (_lifecycleLock)
        {
            var now = GetTimestampMilliseconds();
            if (now >= Volatile.Read(ref _replacementPacingUntilMs))
            {
                Volatile.Write(ref _nextReplacementHandshakeAtMs, 0);
                _cancelledPacingReservations.Clear();
                return null;
            }

            var target = Volatile.Read(ref _nextReplacementHandshakeAtMs);
            if (target == 0) return null;

            var previousDeadline = target;
            target = Math.Max(now, target);
            delayMs = Math.Max(0, target - now);

            // Zero ordinary spacing still waits for an armed failure-backoff
            // deadline, but it does not extend the reservation chain.
            if (_replacementHandshakeSpacingMs > 0)
            {
                var reservedDeadline = unchecked(target + _replacementHandshakeSpacingMs);
                var previousPacingUntil = _replacementPacingUntilMs;
                var reservedPacingUntil = Math.Max(
                    previousPacingUntil,
                    unchecked(reservedDeadline + _replacementHandshakeSpacingMs));
                Volatile.Write(ref _nextReplacementHandshakeAtMs, reservedDeadline);
                _replacementPacingUntilMs = reservedPacingUntil;
                reservation = new ReplacementPacingReservation(
                    previousDeadline,
                    reservedDeadline,
                    previousPacingUntil,
                    reservedPacingUntil);
            }
        }

        try
        {
            if (delayMs > 0)
            {
                Log.Debug(
                    "Pacing NNTP reconnect for {Provider} by {DelayMs}ms after connection replacement",
                    _diagnosticName, delayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RollBackReplacementPacing(reservation);
            throw;
        }

        return reservation;
    }

    private void CommitReplacementPacing(ReplacementPacingReservation? reservation)
    {
        if (reservation is not { } value) return;

        lock (_lifecycleLock)
            _cancelledPacingReservations.Remove(value.PreviousDeadlineMs);
    }

    private void RollBackReplacementPacing(ReplacementPacingReservation? reservation)
    {
        if (reservation is not { } value) return;

        lock (_lifecycleLock)
        {
            _cancelledPacingReservations[value.ReservedDeadlineMs] = value;
            while (_cancelledPacingReservations.Remove(
                       _nextReplacementHandshakeAtMs, out var cancelledTail))
            {
                _nextReplacementHandshakeAtMs = cancelledTail.PreviousDeadlineMs;
                if (_replacementPacingUntilMs == cancelledTail.ReservedPacingUntilMs)
                    _replacementPacingUntilMs = cancelledTail.PreviousPacingUntilMs;
            }
        }
    }

    internal const long MinimumHandshakeFailureBackoffMs = 500;

    private long GetHandshakeFailureBackoffMs(int consecutiveFailures)
    {
        // Zero ordinary replacement spacing may skip the delay after a poisoned-socket
        // replacement, but factory failures always keep a nonzero backoff floor so
        // queued callers cannot hammer TCP/TLS/AUTHINFO.
        var baseDelay = Math.Max(_replacementHandshakeSpacingMs, MinimumHandshakeFailureBackoffMs);
        var exponent = Math.Min(Math.Max(0, consecutiveFailures - 1), 6);
        return Math.Min(baseDelay * (1L << exponent), 60_000);
    }

    private void ArmReplacementPacing(long delayMs)
    {
        if (delayMs == 0) return;

        lock (_lifecycleLock)
            ArmReplacementPacingUnderLock(delayMs);
    }

    private void ArmReplacementPacingUnderLock(long delayMs)
    {
        var now = GetTimestampMilliseconds();
        var candidate = unchecked(now + delayMs);
        var pacingWindowMs = Math.Max(5000, Math.Max(delayMs, _replacementHandshakeSpacingMs * 10));
        var pacingUntil = unchecked(now + pacingWindowMs);
        if (pacingUntil > _replacementPacingUntilMs)
            _replacementPacingUntilMs = pacingUntil;

        var current = Volatile.Read(ref _nextReplacementHandshakeAtMs);
        if (current == 0 || candidate > current)
            _nextReplacementHandshakeAtMs = candidate;
    }

    private long GetTimestampMilliseconds() =>
        (long)(_timeProvider.GetTimestamp() * 1000d / _timeProvider.TimestampFrequency);

    // Runtime object hash for log correlation only; not a monotonic socket generation.
    private static int ConnectionHash(T connection) => RuntimeHelpers.GetHashCode(connection!);

    private void TriggerConnectionPoolChangedEvent()
    {
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? subscribers;
        int live;
        int idle;
        int max;
        lock (_lifecycleLock)
        {
            if (_disposed == 1)
                return;

            subscribers = OnConnectionPoolChanged;
            live = _live;
            idle = _idleConnections.Count;
            max = EffectiveMaxConnections;
        }

        SynchronousObserverInvoker.Invoke(
            subscribers,
            this,
            new ConnectionPoolStats.ConnectionPoolChangedEventArgs(live, idle, max),
            SynchronousObserverSource.ConnectionPoolChanged);
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
        SynchronousObserverInvoker.Invoke(
            _onConnectionLimitLearned,
            learned,
            newEffective,
            SynchronousObserverSource.ConnectionLimitLearned);
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

                        connection = await TryGetIdleConnectionLockAsync(
                                SemaphorePriority.Low,
                                pingedConnections,
                                borrowCts.Token)
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
                           preferIdle: false,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
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
