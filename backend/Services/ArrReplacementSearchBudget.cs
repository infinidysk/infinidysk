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

    public bool TryReserve(string mediaKey, int limit, TimeSpan window) =>
        TryReserveAll([mediaKey], limit, window);

    /// <summary>
    /// Reserves every media key or none of them. A season-pack <c>EpisodeSearch</c>
    /// must not start when any linked episode is already at its limit.
    /// </summary>
    public bool TryReserveAll(IReadOnlyList<string> mediaKeys, int limit, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(mediaKeys);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var uniqueKeys = new List<string>(mediaKeys.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mediaKey in mediaKeys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
            if (seen.Add(mediaKey)) uniqueKeys.Add(mediaKey);
        }

        if (uniqueKeys.Count == 0) return true;

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            Prune(now - window);

            var newKeyCount = 0;
            foreach (var mediaKey in uniqueKeys)
            {
                if (_searches.TryGetValue(mediaKey, out var reservations))
                {
                    if (reservations.Count >= limit) return false;
                    continue;
                }

                newKeyCount++;
            }

            // Fail closed at capacity: evicting an active key would forget its
            // reservations and let that media item exceed the configured cap.
            if (_searches.Count + newKeyCount > MaxTrackedMediaItems) return false;

            foreach (var mediaKey in uniqueKeys)
            {
                if (!_searches.TryGetValue(mediaKey, out var reservations))
                {
                    reservations = [];
                    _searches[mediaKey] = reservations;
                }

                reservations.Add(now);
            }

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
