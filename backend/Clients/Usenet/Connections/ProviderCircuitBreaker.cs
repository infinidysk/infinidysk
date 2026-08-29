using NzbWebDAV.Clients.Usenet.Models;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Identifies the caller that claimed a half-open probe slot.
/// Generation 0 means the caller is not the admitted probe.
/// </summary>
public readonly record struct CircuitProbeLease(long Generation)
{
    public static CircuitProbeLease None => default;
    public bool IsNone => Generation == 0;
}

/// <summary>
/// Tracks recent BODY/ARTICLE outcomes for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// Failures accumulate in a short sliding window between successes. A success
/// clears the window and fully resets the cooldown ladder (same recovery
/// semantics as the former consecutive-failure breaker). After tripping, the
/// provider enters a cooldown during which it is skipped and additional
/// failures are ignored (latched). When the cooldown expires the trip stays
/// latched and the provider is half-open. Any recorded failure then re-trips
/// immediately with the doubled cooldown and any recorded success closes the
/// circuit. <see cref="GetSnapshot"/> reports that state without altering it.
/// <see cref="IsTripped"/> reports it while also admitting exactly one half-open
/// probe per cooldown lapse, and an abandoned probe with no outcome within
/// <see cref="ProbeAbandonTimeout"/> can be retaken.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int WindowSeconds = 30;
    private const int MinFailuresToTrip = 3;
    private const double TripFailureRate = 0.5;

    private static readonly TimeSpan DefaultInitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultMaxCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBurstCoalesceWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultProbeAbandonTimeout = TimeSpan.FromSeconds(60);

    private readonly string _providerName;
    private readonly Action<ProviderCircuitTransition>? _onTransition;
    private readonly bool _coalesceFailureBursts;
    private readonly TimeSpan _initialCooldown;
    private readonly TimeSpan _maxCooldown;
    private readonly object _lock = new();
    private readonly Queue<(long AtMs, bool Failed)> _window = new();

    private long _trippedUntilMs;
    private long _failureBurstStartedAtMs = long.MinValue;
    private TimeSpan _currentCooldown;
    private int _halfOpenProbeInFlight; // 0/1
    private long _probeStartedMs;
    private long _probeGeneration;
    private long _admittedProbeGeneration;
    private static readonly AsyncLocal<CircuitProbeLease> AmbientProbe = new();
    private string? _lastFailureReason;
    private long _tripCount;
    private long _failureCount;
    private long _articleMissCount;

    public ProviderCircuitBreaker(
        string providerName,
        Action<ProviderCircuitTransition>? onTransition = null,
        bool coalesceFailureBursts = false,
        TimeSpan? initialCooldown = null,
        TimeSpan? maxCooldown = null)
    {
        _providerName = providerName;
        _onTransition = onTransition;
        _coalesceFailureBursts = coalesceFailureBursts;
        // A non-positive initial cooldown would trip straight into half-open and the
        // doubling ladder would never grow, silently disabling the breaker.
        var configuredInitial = initialCooldown ?? DefaultInitialCooldown;
        _initialCooldown = configuredInitial > TimeSpan.Zero
            ? configuredInitial
            : DefaultInitialCooldown;
        // A ceiling under the initial cooldown would shorten the first trip instead of
        // bounding the ladder, so keep it at or above the value it caps.
        var ceiling = maxCooldown ?? DefaultMaxCooldown;
        _maxCooldown = ceiling < _initialCooldown ? _initialCooldown : ceiling;
        _currentCooldown = _initialCooldown;
    }

    /// <summary>How long an unanswered half-open probe may hold the slot. For tests.</summary>
    internal TimeSpan ProbeAbandonTimeout { get; set; } = DefaultProbeAbandonTimeout;

    /// <summary>Monotonic clock, injectable for tests.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// True when this provider should not take new work. Reading this claims the
    /// half-open probe slot as a side effect; prefer <see cref="TryAdmit"/> when the
    /// caller will record an outcome.
    /// </summary>
    public bool IsTripped => !TryAdmit(out _);

    /// <summary>
    /// Admits a caller onto the provider. When the cooldown has lapsed, exactly one
    /// caller receives a probe lease that can resolve half-open state.
    /// Takes the same lock as <see cref="RecordFailure"/> so a new probe cannot be
    /// claimed in the window after an in-flight probe is cleared and before the
    /// circuit reopens.
    /// </summary>
    public bool TryAdmit(out CircuitProbeLease probe)
    {
        probe = CircuitProbeLease.None;
        lock (_lock)
        {
            if (_trippedUntilMs == 0)
            {
                AmbientProbe.Value = CircuitProbeLease.None;
                return true;
            }

            if (Clock() < _trippedUntilMs)
                return false;

            TryReclaimAbandonedProbe();
            if (_halfOpenProbeInFlight != 0)
                return false;

            _halfOpenProbeInFlight = 1;
            var generation = ++_probeGeneration;
            _admittedProbeGeneration = generation;
            _probeStartedMs = Clock();
            probe = new CircuitProbeLease(generation);
            AmbientProbe.Value = probe;
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="probe"/> is the currently admitted half-open lease.
    /// Does not claim a slot. A <see cref="CircuitProbeLease.None"/> lease never owns
    /// the probe, so a closed-circuit admission cannot continue into someone else's
    /// half-open window.
    /// </summary>
    internal bool OwnsAdmittedProbe(CircuitProbeLease probe)
    {
        if (probe.IsNone)
            return false;

        lock (_lock)
            return _halfOpenProbeInFlight == 1 && _admittedProbeGeneration == probe.Generation;
    }

    /// <summary>
    /// Releases a half-open probe whose caller is abandoning it (caller cancellation,
    /// pool retirement) without recording an outcome, so the next caller can probe
    /// immediately instead of waiting out <see cref="ProbeAbandonTimeout"/>. Only the
    /// owning generation can release; a stale or closed-circuit lease is ignored and
    /// the latched trip is left untouched.
    /// </summary>
    internal void ReleaseProbe(CircuitProbeLease probe)
    {
        if (probe.IsNone)
            return;

        lock (_lock)
        {
            if (_halfOpenProbeInFlight == 1 && _admittedProbeGeneration == probe.Generation)
                ClearAdmittedProbe();
        }
    }

    /// <summary>
    /// True once a trip has latched, whether still cooling down or waiting on a probe.
    /// Unlike <see cref="IsTripped"/> this claims no probe slot, so callers can read it
    /// without altering state.
    /// </summary>
    public bool IsLatched => Volatile.Read(ref _trippedUntilMs) != 0;

    /// <summary>TickCount64 deadline while latched open; 0 when not tripped. For tests.</summary>
    internal long TrippedUntilMs => Volatile.Read(ref _trippedUntilMs);

    /// <summary>Cooldown that will apply on the next trip. For tests.</summary>
    internal TimeSpan CurrentCooldown
    {
        get
        {
            lock (_lock) return _currentCooldown;
        }
    }

    /// <summary>
    /// Limits the remaining open cooldown without resetting the escalation ladder.
    /// Used when all providers fail together and no provider remains to serve traffic.
    /// </summary>
    internal void CapCooldown(TimeSpan maximumRemaining)
    {
        lock (_lock)
        {
            var now = Clock();
            if (_trippedUntilMs <= now)
                return;

            var cappedUntil = now + (long)maximumRemaining.TotalMilliseconds;
            if (_trippedUntilMs > cappedUntil)
                _trippedUntilMs = cappedUntil;
        }
    }

    /// <summary>Force the open cooldown into the past so half-open tests can proceed.</summary>
    internal void ExpireCooldownForTests()
    {
        lock (_lock)
        {
            if (_trippedUntilMs > 0)
                _trippedUntilMs = Clock() - 1;
        }
    }

    /// <summary>
    /// Records a successful command.
    /// </summary>
    /// <param name="resetsCooldownLadder">
    /// True when the success proves the download path works (a BODY/ARTICLE fetch),
    /// fully resetting the escalation ladder. False for reachability-only successes
    /// (STAT/HEAD/DATE): the trip is cleared so the provider rejoins rotation, but the
    /// current cooldown is preserved for the next trip. Health-check STAT traffic would
    /// otherwise close every latched breaker seconds after cooldown expiry and pin a
    /// provider with a persistently broken BODY path at the minimum cooldown forever.
    /// </param>
    public void RecordSuccess(bool resetsCooldownLadder = true, CircuitProbeLease? probe = null)
    {
        lock (_lock)
        {
            // Commands that started before a trip can still complete while the
            // provider is cooling down. They are not half-open probes and must
            // not return the provider to normal rotation early.
            if (_trippedUntilMs > Clock())
                return;

            // Only a circuit that opened can recover. Failures that cleared without tripping
            // are routine, and announcing a recovery for them implies an outage that never
            // happened. Matches the transition notification below.
            var wasCircuitActive = _trippedUntilMs > 0 || _halfOpenProbeInFlight != 0;
            if (wasCircuitActive && !CanResolveHalfOpen(probe))
                return;
            if (wasCircuitActive)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _window.Clear();
            _failureBurstStartedAtMs = long.MinValue;
            _trippedUntilMs = 0;
            if (resetsCooldownLadder)
                _currentCooldown = _initialCooldown;
            _lastFailureReason = null;
            ClearAdmittedProbe();
            if (wasCircuitActive)
                NotifyTransition(ProviderCircuitTransitionState.Closed, cooldown: null);
        }
    }

    /// <summary>
    /// Article permanently missing from retention. A 430 is a clean server response and
    /// says nothing about provider health, so it counts as a miss for diagnostics and
    /// nothing else: it must not undo an open trip or reset the cooldown ladder. On a
    /// closed circuit it does clear the
    /// failure sampling window, because the provider demonstrably answered.
    /// <para>
    /// A clean 430 received by the admitted half-open probe closes the circuit without
    /// resetting the cooldown ladder. A stale in-flight 430 cannot close another
    /// caller's probe. When no probe has been claimed, a clean 430 after cooldown
    /// still closes because production routing does not claim the slot at selection.
    /// </para>
    /// </summary>
    public void RecordArticleNotFound(CircuitProbeLease? probe = null)
    {
        Interlocked.Increment(ref _articleMissCount);
        var closesHalfOpenCircuit = false;

        lock (_lock)
        {
            if (_trippedUntilMs > Clock())
                return;

            if (_trippedUntilMs != 0 || Volatile.Read(ref _halfOpenProbeInFlight) != 0)
            {
                if (CanResolveHalfOpen(probe))
                    closesHalfOpenCircuit = true;
            }
            else
            {
                _window.Clear();
                _failureBurstStartedAtMs = long.MinValue;
            }
        }

        if (closesHalfOpenCircuit)
            RecordSuccess(resetsCooldownLadder: false, probe);
    }

    /// <summary>Read-only snapshot for dashboards. Does not claim a half-open probe.</summary>
    public ProviderCircuitBreakerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var now = Clock();
            var trippedUntil = _trippedUntilMs;
            var probeInFlight = Volatile.Read(ref _halfOpenProbeInFlight) == 1;

            ProviderCircuitState state;
            int? cooldownRemainingSeconds = null;
            if (trippedUntil > 0 && now < trippedUntil)
            {
                state = ProviderCircuitState.Open;
                cooldownRemainingSeconds = Math.Max(0, (int)Math.Ceiling((trippedUntil - now) / 1000.0));
            }
            else if (trippedUntil > 0 || probeInFlight)
            {
                state = ProviderCircuitState.HalfOpen;
            }
            else
            {
                state = ProviderCircuitState.Closed;
            }

            return new ProviderCircuitBreakerSnapshot(
                state,
                cooldownRemainingSeconds,
                _lastFailureReason,
                Volatile.Read(ref _tripCount),
                Volatile.Read(ref _failureCount),
                Volatile.Read(ref _articleMissCount));
        }
    }

    /// <summary>
    /// Records a connection-establishment failure. Unlike command failures, one failed
    /// connect is enough to trip because concurrent retries would otherwise consume the
    /// provider's connection capacity while it is unreachable.
    /// </summary>
    public void RecordConnectionFailure(
        string? reason = null,
        ProviderCircuitPoolDiagnostics? pool = null,
        CircuitProbeLease? probe = null)
    {
        lock (_lock)
        {
            var now = Clock();

            // Ignore failures already in flight when the provider first tripped.
            if (_trippedUntilMs > 0 && now < _trippedUntilMs)
                return;

            var wasHalfOpen = Volatile.Read(ref _halfOpenProbeInFlight) == 1
                              || _trippedUntilMs > 0;
            if (wasHalfOpen && !CanResolveHalfOpen(probe))
                return;

            ClearAdmittedProbe();
            Interlocked.Increment(ref _failureCount);

            var failureReason = wasHalfOpen
                ? "half-open connection failure"
                : "connection failure";
            Trip(now, reason is null
                ? failureReason
                : $"{failureReason} ({reason})", pool);
        }
    }

    public void RecordFailure(
        string? reason = null,
        ProviderCircuitPoolDiagnostics? pool = null,
        CircuitProbeLease? probe = null)
    {
        lock (_lock)
        {
            var now = Clock();

            // Already latched open: ignore in-flight failures from the same burst
            // so they cannot extend the window, double the cooldown, or spam logs.
            if (_trippedUntilMs > 0 && now < _trippedUntilMs)
                return;

            // Half-open once a probe is claimed or once the cooldown lapses with the trip
            // still latched. A failure here reopens on the current cooldown rather than
            // joining the sampling window below, which would return a provider that is
            // still down to normal rotation until that window tripped again.
            if (Volatile.Read(ref _halfOpenProbeInFlight) == 1 || _trippedUntilMs > 0)
            {
                if (!CanResolveHalfOpen(probe))
                    return;

                ClearAdmittedProbe();
                Interlocked.Increment(ref _failureCount);
                Trip(now, reason is null
                    ? "half-open failure"
                    : $"half-open failure ({reason})", pool);
                return;
            }

            Interlocked.Increment(ref _failureCount);

            EvictOldEntries(now);
            if (!_coalesceFailureBursts
                || _failureBurstStartedAtMs == long.MinValue
                || now - _failureBurstStartedAtMs >= (long)FailureBurstCoalesceWindow.TotalMilliseconds)
            {
                _failureBurstStartedAtMs = now;
                _window.Enqueue((now, true));
            }
            else
            {
                return;
            }

            var failures = 0;
            foreach (var entry in _window.Where(entry => entry.Failed))
                failures++;

            if (failures >= MinFailuresToTrip
                && failures / (double)_window.Count >= TripFailureRate)
            {
                var tripReason = reason is null
                    ? $"{failures} failures in {_window.Count}-sample window"
                    : $"{failures} failures in {_window.Count}-sample window ({reason})";
                Trip(now, tripReason, pool);
            }
        }
    }

    private void Trip(long nowMs, string reason, ProviderCircuitPoolDiagnostics? pool = null)
    {
        var appliedCooldown = _currentCooldown;
        _lastFailureReason = reason;
        Interlocked.Increment(ref _tripCount);
        _trippedUntilMs = nowMs + (long)appliedCooldown.TotalMilliseconds;
        if (pool is null)
        {
            Log.Warning(
                "Provider {Provider} tripped ({Reason}). Skipping for {Cooldown}s.",
                _providerName, reason, appliedCooldown.TotalSeconds);
        }
        else
        {
            Log.Warning(
                "Provider {Provider} tripped ({Reason}). Pool live={LiveConnections}, idle={IdleConnections}, active={ActiveConnections}. Skipping for {Cooldown}s.",
                _providerName, reason, pool.LiveConnections, pool.IdleConnections,
                pool.ActiveConnections, appliedCooldown.TotalSeconds);
        }
        NotifyTransition(ProviderCircuitTransitionState.Open, appliedCooldown, reason, pool);

        _window.Clear();
        _failureBurstStartedAtMs = long.MinValue;
        _currentCooldown = TimeSpan.FromMilliseconds(
            Math.Min(_currentCooldown.TotalMilliseconds * 2, _maxCooldown.TotalMilliseconds));
    }

    private void NotifyTransition(
        ProviderCircuitTransitionState state,
        TimeSpan? cooldown,
        string? failureReason = null,
        ProviderCircuitPoolDiagnostics? pool = null)
    {
        if (_onTransition is null)
            return;

        try
        {
            _onTransition(new ProviderCircuitTransition(
                state,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cooldown,
                failureReason,
                pool));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(
                exception,
                "Provider {Provider} circuit transition callback failed",
                _providerName);
        }
    }

    private void EvictOldEntries(long nowMs)
    {
        var cutoff = nowMs - WindowSeconds * 1000L;
        while (_window.Count > 0 && _window.Peek().AtMs < cutoff)
            _window.Dequeue();
    }

    private bool CanResolveHalfOpen(CircuitProbeLease? probe)
    {
        var admitted = Volatile.Read(ref _admittedProbeGeneration);
        if (admitted == 0)
            return true;

        var lease = probe ?? AmbientProbe.Value;
        return lease.Generation == admitted;
    }

    private void ClearAdmittedProbe()
    {
        Volatile.Write(ref _halfOpenProbeInFlight, 0);
        Volatile.Write(ref _probeStartedMs, 0);
        Volatile.Write(ref _admittedProbeGeneration, 0);
        AmbientProbe.Value = CircuitProbeLease.None;
    }

    /// <summary>Must run while holding <see cref="_lock"/>.</summary>
    private void TryReclaimAbandonedProbe()
    {
        if (_halfOpenProbeInFlight != 1) return;
        if (_probeStartedMs == 0) return;
        if (Clock() - _probeStartedMs < (long)ProbeAbandonTimeout.TotalMilliseconds) return;

        // Abandoned probe (cancelled request, etc.): free the slot so another
        // caller can retry. Serialized with TryAdmit/Record* via _lock.
        _halfOpenProbeInFlight = 0;
        _probeStartedMs = 0;
        _admittedProbeGeneration = 0;
    }
}
