using NzbWebDAV.Clients.Usenet.Models;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Correlates circuit-breaker Open transitions across providers. When every enabled
/// provider trips within a short window of each other, the likely cause is local
/// (network, DNS, TLS interception, ISP routing) rather than simultaneous independent
/// provider outages — a shape per-provider breakers cannot see. Emits one throttled
/// warning so operators get a single actionable event instead of N isolated trips.
/// <para>
/// Lifetime: one instance per <c>MultiProviderNntpClient</c> generation. A config-change
/// rebuild creates a new detector for the new provider set; any in-flight trip state from
/// the old generation is intentionally lost. This means a trip on provider A under the old
/// client followed by a trip on provider B under the new client will not correlate — an
/// acceptable gap since config changes are rare and the correlation window is short.
/// </para>
/// </summary>
public sealed class CorrelatedTripDetector
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultEpisodeIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal); // key -> host
    private readonly Dictionary<string, Action> _onCorrelatedTrip = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _openSinceMs = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly TimeSpan _throttle;
    private readonly TimeSpan _episodeIdleTimeout;
    private long? _lastWarningAtMs;
    private bool _correlatedEpisodeActive;
    private long _correlatedEpisodeLastActivityAtMs;

    public CorrelatedTripDetector(
        TimeSpan? window = null,
        TimeSpan? throttle = null,
        TimeSpan? episodeIdleTimeout = null)
    {
        _window = window ?? DefaultWindow;
        _throttle = throttle ?? DefaultThrottle;
        _episodeIdleTimeout = episodeIdleTimeout ?? DefaultEpisodeIdleTimeout;
    }

    /// <summary>Wall clock, injectable for tests.</summary>
    internal Func<long> Clock { get; set; } =
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Tracks a provider that can carry traffic. Disabled providers never trip, so
    /// registering them would wedge the "all providers tripped" condition forever.
    /// </summary>
    public void Register(string providerKey, string host, Action? onCorrelatedTrip = null)
    {
        lock (_lock)
        {
            _providers[providerKey] = host;
            if (onCorrelatedTrip is not null)
                _onCorrelatedTrip[providerKey] = onCorrelatedTrip;
            else
                _onCorrelatedTrip.Remove(providerKey);
        }
    }

    public void Unregister(string providerKey)
    {
        lock (_lock)
        {
            _providers.Remove(providerKey);
            _onCorrelatedTrip.Remove(providerKey);
            _openSinceMs.Remove(providerKey);
            if (_openSinceMs.Count == 0)
                _correlatedEpisodeActive = false;
        }
    }

    public void OnTransition(string providerKey, ProviderCircuitTransition transition)
    {
        List<Action>? callbacks = null;
        string? providers = null;
        var providerCount = 0;
        var shouldWarn = false;
        lock (_lock)
        {
            var now = Clock();
            ExpireCorrelatedEpisodeIfInactive(now);

            if (transition.State == ProviderCircuitTransitionState.Open)
                _openSinceMs[providerKey] = transition.AtUnixMilliseconds;
            else
                _openSinceMs.Remove(providerKey);

            if (_correlatedEpisodeActive)
                _correlatedEpisodeLastActivityAtMs = now;

            if (_openSinceMs.Count == 0)
            {
                _correlatedEpisodeActive = false;
                return;
            }

            if (transition.State != ProviderCircuitTransitionState.Open ||
                _providers.Count < 2 ||
                !_providers.Keys.All(_openSinceMs.ContainsKey))
            {
                return;
            }

            var opens = _providers.Keys.Select(p => _openSinceMs[p]).ToList();
            var clustered = opens.Max() - opens.Min() <= (long)_window.TotalMilliseconds;
            if (clustered)
            {
                _correlatedEpisodeActive = true;
                _correlatedEpisodeLastActivityAtMs = now;
                providers = string.Join(", ", _providers.Values);
                providerCount = _providers.Count;
                if (_lastWarningAtMs is not { } lastWarning
                    || now - lastWarning >= (long)_throttle.TotalMilliseconds)
                {
                    _lastWarningAtMs = now;
                    shouldWarn = true;
                }
            }

            // The original clustered all-open event starts the episode. While it is
            // active, later re-trips are still part of the same shared-path outage even
            // when their independent cooldowns have drifted apart.
            if (!_correlatedEpisodeActive)
                return;
            callbacks = _onCorrelatedTrip.Values.ToList();
        }

        if (shouldWarn)
        {
            Log.Warning(
                "All {Count} NNTP providers tripped their circuit breakers within {WindowSeconds}s of each other ({Providers}). " +
                "The shared network path may be degraded; providers will probe recovery after their configured cooldown.",
                providerCount,
                _window.TotalSeconds,
                providers);
        }

        foreach (var callback in callbacks!)
        {
            try
            {
                callback();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Warning(exception, "Correlated NNTP provider trip callback failed");
            }
        }
    }

    private void ExpireCorrelatedEpisodeIfInactive(long now)
    {
        if (!_correlatedEpisodeActive ||
            now - _correlatedEpisodeLastActivityAtMs < (long)_episodeIdleTimeout.TotalMilliseconds)
        {
            return;
        }

        _correlatedEpisodeActive = false;
    }
}
