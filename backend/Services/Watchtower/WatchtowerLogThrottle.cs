using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

/// <summary>
/// Rate-limits repeating Watchtower heartbeat messages. The cycle loop ticks every
/// 20 seconds forever, so an idle-but-enabled Watchtower would otherwise fill the
/// whole in-memory log buffer with the same two lines and evict the events support
/// actually needs (see LogBufferSink).
/// </summary>
public sealed class WatchtowerLogThrottle
{
    private readonly ConcurrentDictionary<string, State> _states = new();

    /// <summary>
    /// Returns true when the caller should emit the message for <paramref name="key"/>,
    /// which is at most once per <paramref name="interval"/>. <paramref name="suppressed"/>
    /// reports how many calls were swallowed since the last emitted one so the message can
    /// say what was skipped.
    /// </summary>
    public bool ShouldLog(string key, TimeSpan interval, out int suppressed)
    {
        var now = Environment.TickCount64;
        var intervalMs = (long)interval.TotalMilliseconds;
        var state = _states.GetOrAdd(key, _ => new State());

        lock (state)
        {
            if (state.HasLogged && now - state.LastLoggedAtMs < intervalMs)
            {
                state.Suppressed++;
                suppressed = 0;
                return false;
            }

            suppressed = state.Suppressed;
            state.Suppressed = 0;
            state.LastLoggedAtMs = now;
            state.HasLogged = true;
            return true;
        }
    }

    /// <summary>
    /// Forgets the throttle window for <paramref name="key"/> so the next call logs
    /// immediately. Used when the underlying condition changes and the fresh state is
    /// worth reporting right away.
    /// </summary>
    public void Reset(string key) => _states.TryRemove(key, out _);

    private sealed class State
    {
        public long LastLoggedAtMs;
        public bool HasLogged;
        public int Suppressed;
    }
}
