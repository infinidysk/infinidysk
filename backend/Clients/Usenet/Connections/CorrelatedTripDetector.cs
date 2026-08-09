using NzbWebDAV.Clients.Usenet.Models;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Correlates circuit-breaker Open transitions across providers. When every enabled
/// provider trips within a short window of each other, the likely cause is local
/// (network, DNS, TLS interception, ISP routing) rather than simultaneous independent
/// provider outages — a shape per-provider breakers cannot see. Emits one throttled
/// warning so operators get a single actionable event instead of N isolated trips.
/// </summary>
public sealed class CorrelatedTripDetector
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultThrottle = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal); // key -> host
    private readonly Dictionary<string, long> _openSinceMs = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly TimeSpan _throttle;
    private long? _lastWarningAtMs;

    public CorrelatedTripDetector(TimeSpan? window = null, TimeSpan? throttle = null)
    {
        _window = window ?? DefaultWindow;
        _throttle = throttle ?? DefaultThrottle;
    }

    /// <summary>Wall clock, injectable for tests.</summary>
    internal Func<long> Clock { get; set; } =
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Tracks a provider that can carry traffic. Disabled providers never trip, so
    /// registering them would wedge the "all providers tripped" condition forever.
    /// </summary>
    public void Register(string providerKey, string host)
    {
        lock (_lock)
            _providers[providerKey] = host;
    }

    public void Unregister(string providerKey)
    {
        lock (_lock)
        {
            _providers.Remove(providerKey);
            _openSinceMs.Remove(providerKey);
        }
    }

    public void OnTransition(string providerKey, ProviderCircuitTransition transition)
    {
        lock (_lock)
        {
            if (transition.State == ProviderCircuitTransitionState.Open)
                _openSinceMs[providerKey] = transition.AtUnixMilliseconds;
            else
                _openSinceMs.Remove(providerKey);

            if (transition.State != ProviderCircuitTransitionState.Open) return;
            if (_providers.Count < 2) return;
            if (!_providers.Keys.All(_openSinceMs.ContainsKey)) return;

            // All providers are open; only correlated if the trips cluster in time.
            var opens = _providers.Keys.Select(p => _openSinceMs[p]).ToList();
            if (opens.Max() - opens.Min() > (long)_window.TotalMilliseconds) return;

            var now = Clock();
            if (_lastWarningAtMs is { } lastWarning
                && now - lastWarning < (long)_throttle.TotalMilliseconds) return;
            _lastWarningAtMs = now;

            Log.Warning(
                "All {Count} NNTP providers tripped their circuit breakers within {WindowSeconds}s of each other ({Providers}). " +
                "Simultaneous independent provider outages are unlikely — check local network, DNS, or TLS interception.",
                _providers.Count,
                _window.TotalSeconds,
                string.Join(", ", _providers.Values));
        }
    }
}
