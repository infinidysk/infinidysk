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
                // Fail closed at capacity: evicting an active key would forget its
                // reservations and let that media item exceed the configured cap.
                // Denying the new key only withholds a search until entries expire.
                if (_searches.Count >= MaxTrackedMediaItems) return false;
                reservations = [];
                _searches[mediaKey] = reservations;
            }

            if (reservations.Count >= limit) return false;

            reservations.Add(now);
            return true;
        }
    }

    /// <summary>
    /// Refunds the most recent reservation after the Arr action it was reserved for
    /// was definitively rejected, so a failed request cannot consume the budget.
    /// </summary>
    public void ReleaseLastReservation(string mediaKey)
    {
        lock (_gate)
        {
            if (!_searches.TryGetValue(mediaKey, out var reservations)) return;
            if (reservations.Count > 0) reservations.RemoveAt(reservations.Count - 1);
            if (reservations.Count == 0) _searches.Remove(mediaKey);
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
}
