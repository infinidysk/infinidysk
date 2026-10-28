namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Single-flight memo for provider metadata: concurrent callers share one in-flight
/// request, and a successful result is reused for its TTL. Failures are removed from the
/// map immediately, so the next request retries instead of replaying the error.
/// </summary>
/// <remarks>
/// The shared request runs with <see cref="CancellationToken.None"/> — a caller that
/// disconnects must not cancel metadata other callers are waiting on — while each caller
/// still observes its own cancellation through <see cref="Task.WaitAsync(CancellationToken)"/>.
/// </remarks>
internal sealed class MetadataMemo(TimeProvider? timeProvider = null)
{
    private const int PruneThreshold = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_slots.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
                return AwaitAsync<T>(existing.Shared, ct);

            if (_slots.Count >= PruneThreshold)
                PruneExpired(now);

            // The TTL starts when the request starts so an in-flight entry is also the
            // single-flight guard. Failed entries are dropped below, so retries stay prompt.
            var slot = new Slot { ExpiresAt = now.Add(ttl) };
            _slots[key] = slot;
            slot.Shared = RunAsync(slot, key, factory);
            return AwaitAsync<T>(slot.Shared, ct);
        }
    }

    private async Task<object?> RunAsync<T>(
        Slot slot,
        string key,
        Func<CancellationToken, Task<T>> factory)
    {
        try
        {
            return await factory(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_slots.TryGetValue(key, out var current) && ReferenceEquals(current, slot))
                    _slots.Remove(key);
            }

            throw;
        }
    }

    private static async Task<T> AwaitAsync<T>(Task<object?> shared, CancellationToken ct)
        => (T)(await shared.WaitAsync(ct).ConfigureAwait(false))!;

    private void PruneExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, slot) in _slots)
        {
            if (slot.ExpiresAt > now) continue;
            (expired ??= []).Add(key);
        }

        if (expired is null) return;
        foreach (var key in expired)
            _slots.Remove(key);
    }

    private sealed class Slot
    {
        public Task<object?> Shared { get; set; } = Task.FromResult<object?>(null);
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
