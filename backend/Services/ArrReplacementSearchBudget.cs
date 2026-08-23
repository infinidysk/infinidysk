namespace NzbWebDAV.Services;

/// <summary>
/// Bounds automatic Arr replacement searches for one media item. Queue removals
/// and blocklisting still happen after the limit; only the next search is withheld.
/// </summary>
public sealed class ArrReplacementSearchBudget
{
    private const int MaxTrackedMediaItems = 4096;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, List<DateTimeOffset>> _searches = new(StringComparer.Ordinal);

    public ArrReplacementSearchBudget(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryReserve(string mediaKey, int limit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var cutoff = now - window;
            Prune(cutoff);

            if (!_searches.TryGetValue(mediaKey, out var reservations))
            {
                MakeRoom();
                reservations = [];
                _searches[mediaKey] = reservations;
            }

            if (reservations.Count >= limit) return false;

            reservations.Add(now);
            return true;
        }
    }

    private void Prune(DateTimeOffset cutoff)
    {
        foreach (var (key, reservations) in _searches.ToArray())
        {
            reservations.RemoveAll(x => x < cutoff);
            if (reservations.Count == 0) _searches.Remove(key);
        }
    }

    private void MakeRoom()
    {
        if (_searches.Count < MaxTrackedMediaItems) return;

        var oldest = _searches
            .OrderBy(x => x.Value[0])
            .First();
        _searches.Remove(oldest.Key);
    }
}
