namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Active-reader invalidation only: entries disappear when readers close, so a
/// large library never becomes an all-content in-memory index. Persistent cache
/// identities use a hash of the source blob, not this process-local notification.
/// </summary>
public static class ContentRevisionTracker
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<Guid, HashSet<RevisionWatch>> Watches = [];
    private static readonly Dictionary<Guid, int> Publications = [];

    public static IDisposable BeginPublication(Guid blobId)
    {
        lock (Sync)
        {
            Publications[blobId] = Publications.GetValueOrDefault(blobId) + 1;
            Publishing(blobId);
            return new Publication(blobId);
        }
    }

    public static RevisionWatch Watch(Guid blobId)
    {
        lock (Sync)
        {
            if (!Watches.TryGetValue(blobId, out var watchers)) Watches[blobId] = watchers = [];
            var watch = new RevisionWatch(blobId);
            if (Publications.ContainsKey(blobId)) watch.Invalidate();
            watchers.Add(watch);
            return watch;
        }
    }

    private sealed class Publication(Guid blobId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync)
            {
                if (Publications[blobId] == 1) Publications.Remove(blobId);
                else Publications[blobId]--;
            }
        }
    }

    public static void Publishing(Guid blobId)
    {
        lock (Sync)
        {
            if (!Watches.TryGetValue(blobId, out var watchers)) return;
            foreach (var watch in watchers) watch.Invalidate();
        }
    }

    public sealed class RevisionWatch : IDisposable
    {
        private readonly Guid _blobId;
        private int _invalid;
        private int _disposed;
        internal RevisionWatch(Guid blobId) => _blobId = blobId;
        public bool IsCurrent => Volatile.Read(ref _invalid) == 0;
        internal void Invalidate() => Interlocked.Exchange(ref _invalid, 1);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync)
            {
                if (Watches.TryGetValue(_blobId, out var watchers))
                {
                    watchers.Remove(this);
                    if (watchers.Count == 0) Watches.Remove(_blobId);
                }
            }
        }
    }
}
