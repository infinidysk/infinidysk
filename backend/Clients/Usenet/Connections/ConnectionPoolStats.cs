using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    // Pool-changed events fire on every connection borrow/return — hundreds per second under
    // load. Websocket updates are coalesced: events only update in-memory counters, and a
    // single flush task emits the latest per-provider stats at most once per interval.
    // The flush is trailing-edge, so the final state after a burst is always sent.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly int[] _latestLive;
    private readonly int[] _latestIdle;
    private readonly bool[] _dirty;
    private readonly int _max;
    private int _totalLive;
    private int _totalIdle;
    private int _flushScheduled; // 0 == false, 1 == true
    private int _active = 1;
    private readonly Lock _lock = new();
    private readonly UsenetProviderConfig _providerConfig;
    private readonly WebsocketManager _websocketManager;

    internal bool IsActive => Volatile.Read(ref _active) == 1;

    public ConnectionPoolStats(UsenetProviderConfig providerConfig, WebsocketManager websocketManager)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _latestLive = new int[count];
        _latestIdle = new int[count];
        _dirty = new bool[count];
        _max = providerConfig.Providers
            .Where(x => x.Type == ProviderType.Pooled)
            .Select(x => x.MaxConnections)
            .Sum();

        _providerConfig = providerConfig;
        _websocketManager = websocketManager;
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            lock (_lock)
            {
                if (_active == 0)
                    return;

                _latestLive[providerIndex] = args.Live;
                _latestIdle[providerIndex] = args.Idle;
                _dirty[providerIndex] = true;

                if (_providerConfig.Providers[providerIndex].Type == ProviderType.Pooled)
                {
                    _live[providerIndex] = args.Live;
                    _idle[providerIndex] = args.Idle;
                    _totalLive = _live.Sum();
                    _totalIdle = _idle.Sum();
                }
            }

            if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
                _ = FlushAfterDelayAsync();
        }
    }

    private async Task FlushAfterDelayAsync()
    {
        await Task.Delay(FlushInterval).ConfigureAwait(false);

        // allow a new flush to be scheduled *before* taking the snapshot,
        // so events arriving after the snapshot are never lost.
        Volatile.Write(ref _flushScheduled, 0);

        lock (_lock)
        {
            // Publish while holding the same lock used by Deactivate(). SendMessage is
            // synchronous, so once Deactivate returns no stale flush can still win the
            // last-message race against the replacement generation.
            if (_active == 0)
                return;

            for (var i = 0; i < _dirty.Length; i++)
            {
                if (!_dirty[i]) continue;
                _dirty[i] = false;
                var message =
                    $"{i}|{_latestLive[i]}|{_latestIdle[i]}|{_totalLive}|{_max}|{_totalIdle}";
                _ = _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message);
            }
        }
    }

    /// <summary>
    /// Stops a retired client generation from overwriting connection totals published by
    /// its replacement while its existing streams finish draining.
    /// </summary>
    internal void Deactivate()
    {
        lock (_lock)
        {
            _active = 0;
            Array.Clear(_dirty);
        }
    }

    public sealed class ConnectionPoolChangedEventArgs(int live, int idle, int max) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Active => Live - Idle;
    }
}
