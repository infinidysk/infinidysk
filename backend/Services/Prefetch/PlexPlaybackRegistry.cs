using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Services.Prefetch;

public sealed class PlexPlaybackRegistry(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _expiry = new(StringComparer.Ordinal);
    public bool HasActivePlayback
    {
        get { lock (_gate) return _expiry.Values.Any(expiry => expiry > clock.GetUtcNow()); }
    }
    public void Record(string serverId, IReadOnlyList<PlexSession> sessions, TimeSpan lifetime)
    {
        lock (_gate)
        {
            if (!sessions.Any(session => session.State == "playing") || lifetime <= TimeSpan.Zero)
            { _expiry.Remove(serverId); return; }
            if (_expiry.Count >= 32 && !_expiry.ContainsKey(serverId)) _expiry.Remove(_expiry.MinBy(pair => pair.Value).Key);
            _expiry[serverId] = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(lifetime.TotalSeconds, 300));
        }
    }
    public void RetainServers(IReadOnlyCollection<string> enabled)
    {
        lock (_gate)
            foreach (var id in _expiry.Keys.Where(id => !enabled.Contains(id, StringComparer.Ordinal)).ToArray())
                _expiry.Remove(id);
    }
}
