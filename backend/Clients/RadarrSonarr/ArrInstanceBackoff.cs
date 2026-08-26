using System.Collections.Concurrent;
using System.Net.Sockets;

namespace NzbWebDAV.Clients.RadarrSonarr;

/// <summary>
/// Per-host exponential backoff for Arr instance polls. When an instance starts
/// timing out or refusing connections (an overloaded or dying peer), polling it on
/// the fixed cadence just adds work to a host that cannot answer. After
/// <see cref="FailureThreshold"/> consecutive reachability failures the instance is
/// considered "in backoff" and callers should skip non-essential polls until
/// <see cref="GetRemainingBackoff"/> elapses. Any successful call resets the counter.
///
/// Only reachability failures (timeout, socket EAGAIN/refused/unreachable) count —
/// a 4xx/5xx response means the host is alive and answering, so it does not back off.
/// </summary>
public sealed class ArrInstanceBackoff
{
    internal const int FailureThreshold = 2;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset BackoffUntil;
    }

    private readonly ConcurrentDictionary<string, State> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public ArrInstanceBackoff(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private static string Key(string host) => host.TrimEnd('/').ToLowerInvariant();

    /// <summary>True while the host is within its current backoff window.</summary>
    public bool IsInBackoff(string host)
    {
        if (!_byHost.TryGetValue(Key(host), out var state)) return false;
        lock (state)
            return state.ConsecutiveFailures >= FailureThreshold && _timeProvider.GetUtcNow() < state.BackoffUntil;
    }

    /// <summary>Remaining backoff for the host, or zero if not backing off.</summary>
    public TimeSpan GetRemainingBackoff(string host)
    {
        if (!_byHost.TryGetValue(Key(host), out var state)) return TimeSpan.Zero;
        lock (state)
        {
            if (state.ConsecutiveFailures < FailureThreshold) return TimeSpan.Zero;
            var remaining = state.BackoffUntil - _timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public void RecordSuccess(string host)
    {
        if (!_byHost.TryGetValue(Key(host), out var state)) return;
        lock (state)
        {
            state.ConsecutiveFailures = 0;
            state.BackoffUntil = default;
        }
    }

    /// <summary>
    /// Record a failure. Only reachability failures advance the backoff counter;
    /// other exceptions are ignored so a live-but-erroring host keeps its cadence.
    /// </summary>
    public void RecordFailure(string host, Exception exception)
    {
        if (!IsReachabilityFailure(exception)) return;
        var state = _byHost.GetOrAdd(Key(host), _ => new State());
        lock (state)
        {
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures < FailureThreshold) return;
            var backoff = ComputeBackoff(state.ConsecutiveFailures);
            state.BackoffUntil = _timeProvider.GetUtcNow() + backoff;
        }
    }

    private static TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var steps = Math.Min(consecutiveFailures - FailureThreshold, 10);
        var backoff = TimeSpan.FromTicks(MinBackoff.Ticks << steps);
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    internal static bool IsReachabilityFailure(Exception exception)
    {
        // A per-call timeout surfaces as TaskCanceledException/OperationCanceledException
        // whose token is the linked call token, not the shutdown token.
        if (exception is OperationCanceledException) return true;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException) return true;
            if (e is HttpRequestException httpEx && httpEx.StatusCode is null) return true;
        }
        return false;
    }
}
