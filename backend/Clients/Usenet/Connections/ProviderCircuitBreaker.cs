using NzbWebDAV.Clients.Usenet.Models;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

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
        _initialCooldown = initialCooldown ?? DefaultInitialCooldown;
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

    public bool IsTripped
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return false;
            if (Clock() < trippedUntil) return true;

            // Cooldown expired → half-open: exactly one caller wins the probe slot.
            TryReclaimAbandonedProbe();
            if (Interlocked.CompareExchange(ref _halfOpenProbeInFlight, 1, 0) == 0)
            {
                Volatile.Write(ref _probeStartedMs, Clock());
                return false; // this caller is the probe
            }

            return true; // another probe is already in flight
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
    public void RecordSuccess(bool resetsCooldownLadder = true)
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
            if (wasCircuitActive)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _window.Clear();
            _failureBurstStartedAtMs = long.MinValue;
            _trippedUntilMs = 0;
            if (resetsCooldownLadder)
                _currentCooldown = _initialCooldown;
            _lastFailureReason = null;
            Volatile.Write(ref _halfOpenProbeInFlight, 0);
            Volatile.Write(ref _probeStartedMs, 0);
            if (wasCircuitActive)
                NotifyTransition(ProviderCircuitTransitionState.Closed, cooldown: null);
        }
    }

    /// <summary>
    /// Article permanently missing from retention. A 430 is a clean server response and
    /// says nothing about provider health, so it counts as a miss for diagnostics and
    /// nothing else: it must not undo a trip, reset the cooldown ladder, satisfy the
    /// half-open probe, or emit a Closed transition. On a closed circuit it does clear the
    /// failure sampling window, because the provider demonstrably answered.
    /// <para>
    /// A miss recorded during a half-open probe leaves the probe slot claimed. It is not
    /// evidence of recovery, so the slot is released by <see cref="ProbeAbandonTimeout"/>
    /// rather than here.
    /// </para>
    /// </summary>
    public void RecordArticleNotFound()
    {
        Interlocked.Increment(ref _articleMissCount);

        lock (_lock)
        {
            if (_trippedUntilMs != 0 || Volatile.Read(ref _halfOpenProbeInFlight) != 0)
                return;

            _window.Clear();
            _failureBurstStartedAtMs = long.MinValue;
        }
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
    public void RecordConnectionFailure(string? reason = null)
    {
        lock (_lock)
        {
            var now = Clock();

            // Ignore failures already in flight when the provider first tripped.
            if (_trippedUntilMs > 0 && now < _trippedUntilMs)
                return;

            var wasHalfOpen = Volatile.Read(ref _halfOpenProbeInFlight) == 1
                              || _trippedUntilMs > 0;
            Volatile.Write(ref _halfOpenProbeInFlight, 0);
            Volatile.Write(ref _probeStartedMs, 0);
            Interlocked.Increment(ref _failureCount);

            var failureReason = wasHalfOpen
                ? "half-open connection failure"
                : "connection failure";
            Trip(now, reason is null
                ? failureReason
                : $"{failureReason} ({reason})");
        }
    }

    public void RecordFailure(string? reason = null)
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
                Volatile.Write(ref _halfOpenProbeInFlight, 0);
                Volatile.Write(ref _probeStartedMs, 0);
                Interlocked.Increment(ref _failureCount);
                Trip(now, reason is null
                    ? "half-open failure"
                    : $"half-open failure ({reason})");
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
                Trip(now, tripReason);
            }
        }
    }

    private void Trip(long nowMs, string reason)
    {
        var appliedCooldown = _currentCooldown;
        _lastFailureReason = reason;
        Interlocked.Increment(ref _tripCount);
        _trippedUntilMs = nowMs + (long)appliedCooldown.TotalMilliseconds;
        Log.Warning(
            "Provider {Provider} tripped ({Reason}). Skipping for {Cooldown}s.",
            _providerName, reason, appliedCooldown.TotalSeconds);
        NotifyTransition(ProviderCircuitTransitionState.Open, appliedCooldown);

        _window.Clear();
        _failureBurstStartedAtMs = long.MinValue;
        _currentCooldown = TimeSpan.FromMilliseconds(
            Math.Min(_currentCooldown.TotalMilliseconds * 2, _maxCooldown.TotalMilliseconds));
    }

    private void NotifyTransition(
        ProviderCircuitTransitionState state,
        TimeSpan? cooldown)
    {
        if (_onTransition is null)
            return;

        try
        {
            _onTransition(new ProviderCircuitTransition(
                state,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cooldown));
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

    private void TryReclaimAbandonedProbe()
    {
        if (Volatile.Read(ref _halfOpenProbeInFlight) != 1) return;
        var started = Volatile.Read(ref _probeStartedMs);
        if (started == 0) return;
        if (Clock() - started < (long)ProbeAbandonTimeout.TotalMilliseconds) return;

        // Abandoned probe (cancelled request, etc.): free the slot so another
        // caller can retry. CompareExchange so we don't clear a just-resolved probe.
        if (Interlocked.CompareExchange(ref _halfOpenProbeInFlight, 0, 1) == 1)
            Volatile.Write(ref _probeStartedMs, 0);
    }
}
