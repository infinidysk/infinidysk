using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Services.Plex;

public sealed record PlexSnapshot<T>(IReadOnlyList<T> Data, DateTimeOffset? LastSuccess, bool IsStale, string? Error);

/// <summary>Coalesced, bounded snapshots; failed refreshes preserve last-good data.</summary>
public sealed class PlexCatalogueService(PlexApiClient api, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _weights = new(StringComparer.Ordinal);
    private const int MaximumSnapshotBytes = 256 * 1024;
    private const int MaximumRetainedBytes = 8 * 1024 * 1024;
    private int _retainedBytes;

    public Task<PlexSnapshot<PlexLibrary>> GetLibrariesAsync(PlexServer server, bool forceRefresh = false, CancellationToken ct = default) =>
        GetAsync(server, "libraries", forceRefresh, token => api.GetLibrariesAsync(server, token), ct);
    public Task<PlexSnapshot<PlexUser>> GetUsersAsync(PlexServer server, bool forceRefresh = false, CancellationToken ct = default) =>
        GetAsync(server, "users", forceRefresh, token => api.GetUsersAsync(server, token), ct);
    public async Task<PlexSnapshot<PlexSource>> GetSourcesAsync(PlexServer server, string? libraryId,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(libraryId))
            return await GetAsync(server, "hubs", forceRefresh,
                token => api.GetHubsAsync(server, null, token), ct).ConfigureAwait(false);

        var collectionsTask = GetAsync(server, "collections:" + libraryId, forceRefresh,
            token => api.GetCollectionsAsync(server, libraryId, token), ct);
        var hubsTask = GetAsync(server, "hubs:" + libraryId, forceRefresh,
            token => api.GetHubsAsync(server, libraryId, token), ct);
        await Task.WhenAll(collectionsTask, hubsTask).ConfigureAwait(false);
        var collections = await collectionsTask.ConfigureAwait(false);
        var hubs = await hubsTask.ConfigureAwait(false);
        var errors = new[] { collections.Error, hubs.Error }
            .Where(error => !string.IsNullOrEmpty(error)).Distinct(StringComparer.Ordinal).ToArray();
        return new PlexSnapshot<PlexSource>(
            [.. collections.Data, .. hubs.Data],
            new[] { collections.LastSuccess, hubs.LastSuccess }.Max(),
            collections.IsStale || hubs.IsStale,
            errors.Length == 0 ? null : string.Join(" · ", errors));
    }

    private async Task<PlexSnapshot<T>> GetAsync<T>(PlexServer server, string resource, bool force,
        Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken ct)
    {
        // Credentials and endpoint changes cannot reuse an earlier authorization snapshot.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{server.Id}\n{server.Url}\n{server.Token}\n{resource}")));
        Entry<T> entry;
        long version;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var value))
            {
                if (_entries.Count >= 128) Remove(_entries.Keys.First());
                _entries[key] = value = new Entry<T>();
            }
            entry = (Entry<T>)value;
            version = entry.Version;
        }
        await entry.Refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (entry.Snapshot is not null && (entry.Version != version || now < entry.RetryAfter ||
                (!force && now < entry.NextRefresh))) return entry.Snapshot;
            try
            {
                var data = await fetch(ct).ConfigureAwait(false);
                var weight = JsonSerializer.SerializeToUtf8Bytes(data).Length;
                if (weight > MaximumSnapshotBytes)
                    throw new PlexRequestException("Plex catalogue snapshot exceeded the size limit.");
                lock (_gate)
                {
                    // An entry evicted during its network request remains request-local.
                    if (_entries.TryGetValue(key, out var retained) && ReferenceEquals(retained, entry))
                    {
                        _retainedBytes -= _weights.GetValueOrDefault(key);
                        _weights[key] = weight;
                        _retainedBytes += weight;
                        while (_retainedBytes > MaximumRetainedBytes)
                            Remove(_entries.Keys.First(candidate => candidate != key));
                    }
                }
                entry.Snapshot = new(data, clock.GetUtcNow(), false, null);
                entry.NextRefresh = clock.GetUtcNow().AddMinutes(1);
                entry.RetryAfter = DateTimeOffset.MinValue;
            }
            catch (PlexRequestException exception)
            {
                entry.Snapshot = new(entry.Snapshot?.Data ?? [], entry.Snapshot?.LastSuccess, true, exception.Message);
                entry.RetryAfter = clock.GetUtcNow().AddSeconds(30);
            }
            Interlocked.Increment(ref entry.Version);
            return entry.Snapshot;
        }
        finally { entry.Refresh.Release(); }
    }

    // Caller holds _gate. Budgets measure UTF-8 projection weight, not CLR object overhead.
    private void Remove(string key)
    {
        _entries.Remove(key);
        if (_weights.Remove(key, out var weight)) _retainedBytes -= weight;
    }

    private sealed class Entry<T>
    {
        public SemaphoreSlim Refresh { get; } = new(1, 1);
        public PlexSnapshot<T>? Snapshot { get; set; }
        public DateTimeOffset NextRefresh { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
        public long Version;
    }
}
