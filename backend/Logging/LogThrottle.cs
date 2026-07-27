using System.Collections.Concurrent;

namespace NzbWebDAV.Logging;

/// <summary>
/// Rate-limits repeating log messages so a condition that recurs on a loop cannot fill
/// the whole in-memory log buffer and evict the events support actually needs
/// (see LogBufferSink). Used for the Watchtower cycle heartbeat, which ticks every
/// 20 seconds forever, and for read-only WebDAV write rejections, which a client can
/// re-attempt many times per second.
/// </summary>
public sealed class LogThrottle
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
