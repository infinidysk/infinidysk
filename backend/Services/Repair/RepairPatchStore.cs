using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Services.Observability;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services.Repair;

public sealed class RepairPatchStore
{
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, CacheEntry> _index = new();
    private readonly object _evictLock = new();
    private readonly object _catalogLoadSync = new();
    private readonly Func<CancellationToken, IEnumerable<string>> _enumerateCacheFiles;
    private Task? _catalogLoadInFlight;
    private long _currentBytes;
    private int _catalogReady;
    private long _hitCount;
    private long _evictionCount;

    private static readonly JsonSerializerOptions HeaderJsonOptions = new() { IncludeFields = true };

    public RepairPatchStore(string cacheDir, long maxBytes)
        : this(cacheDir, maxBytes, enumerateCacheFiles: null)
    {
    }

    internal RepairPatchStore(
        string cacheDir,
        long maxBytes,
        Func<CancellationToken, IEnumerable<string>>? enumerateCacheFiles)
    {
        _dir = cacheDir;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(_dir);
        _enumerateCacheFiles = enumerateCacheFiles
            ?? (_ => Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
        Log.Information("PAR2 repair patch store path: {Path}", _dir);
    }

    public bool IsCatalogReady => Volatile.Read(ref _catalogReady) != 0;
    internal long CurrentBytes => Interlocked.Read(ref _currentBytes);
    internal int EntryCount => _index.Count;
    internal long HitCount => Interlocked.Read(ref _hitCount);
    internal long EvictionCount => Interlocked.Read(ref _evictionCount);

    public bool Contains(string segmentId)
        => IsCatalogReady && _index.ContainsKey(Hash(segmentId));

    public bool TryGet(string segmentId, out UsenetDecodedBodyResponse? response)
    {
        response = null;
        if (!IsCatalogReady) return false;

        var hash = Hash(segmentId);
        if (!_index.TryGetValue(hash, out var entry)) return false;

        var blobPath = BlobPath(hash);
        try
        {
            var header = JsonSerializer.Deserialize<UsenetYencHeader>(
                File.ReadAllText(blobPath + ".h"), HeaderJsonOptions);
            if (header == null || header.PartSize != entry.Size)
            {
                Drop(hash);
                return false;
            }

            var fileStream = new FileStream(blobPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 81920, useAsync: true);
            entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            Interlocked.Increment(ref _hitCount);
            response = new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Article retrieved from repair patch store",
                Stream = new CachedYencStream(header, fileStream),
            };
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Repair patch store: dropping unreadable patch for {SegmentId}", segmentId);
            Drop(hash);
            return false;
        }
    }

    public bool IsRepaired(string segmentId, long expectedSize)
    {
        if (!IsCatalogReady) return false;

        var hash = Hash(segmentId);
        if (!_index.TryGetValue(hash, out var entry) || entry.Size != expectedSize)
            return false;

        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath) || !File.Exists(blobPath + ".h"))
            return false;

        try
        {
            var header = JsonSerializer.Deserialize<UsenetYencHeader>(
                File.ReadAllText(blobPath + ".h"), HeaderJsonOptions);
            return header != null && header.PartSize == expectedSize;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Repair patch store: dropping unreadable patch for {SegmentId}", segmentId);
            Drop(hash);
            return false;
        }
    }

    public void CommitPatch(string segmentId, byte[] bytes, UsenetYencHeader header)
    {
        CommitPatches([(segmentId, bytes, header)]);
    }

    /// <summary>
    /// Writes every patch to a temp file first, then publishes them together so a
    /// verification failure before this call cannot leave a partial catalog.
    /// </summary>
    public void CommitPatches(IReadOnlyList<(string SegmentId, byte[] Bytes, UsenetYencHeader Header)> patches)
    {
        var staged = new List<(string Hash, string TempPath, string HeaderPath, long Size)>(patches.Count);
        try
        {
            foreach (var (segmentId, bytes, header) in patches)
            {
                if (bytes.Length != header.PartSize)
                    throw new ArgumentException("Patch bytes length does not match yEnc header PartSize.");

                var hash = Hash(segmentId);
                var blobPath = BlobPath(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                var tempPath = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var headerPath = blobPath + ".h." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.WriteAllText(headerPath, JsonSerializer.Serialize(header, HeaderJsonOptions));
                staged.Add((hash, tempPath, headerPath, bytes.Length));
            }

            foreach (var (hash, tempPath, headerPath, size) in staged)
            {
                var blobPath = BlobPath(hash);
                // Publish bytes before the header so readers never observe a header
                // whose blob is still missing or from a previous generation.
                File.Move(tempPath, blobPath, overwrite: true);
                File.Move(headerPath, blobPath + ".h", overwrite: true);
                OnFinalized(hash, size);
            }
        }
        catch
        {
            foreach (var (_, tempPath, headerPath, _) in staged)
            {
                SafeDelete(tempPath);
                SafeDelete(headerPath);
            }

            throw;
        }
    }

    private void OnFinalized(string hash, long size)
    {
        lock (_evictLock)
        {
            if (_index.TryGetValue(hash, out var existing)) _currentBytes -= existing.Size;
            _index[hash] = new CacheEntry { Size = size, LastAccessTicks = DateTime.UtcNow.Ticks };
            _currentBytes += size;
        }

        EvictIfNeeded();
    }

    private void Drop(string hash)
    {
        lock (_evictLock)
        {
            if (_index.TryRemove(hash, out var entry)) _currentBytes -= entry.Size;
        }

        SafeDelete(BlobPath(hash));
        SafeDelete(BlobPath(hash) + ".h");
    }

    private void EvictIfNeeded()
    {
        if (Interlocked.Read(ref _currentBytes) <= _maxBytes) return;
        lock (_evictLock)
        {
            if (_currentBytes <= _maxBytes) return;
            foreach (var kv in _index.OrderBy(x => x.Value.LastAccessTicks).ToList())
            {
                if (_currentBytes <= _maxBytes) break;
                if (!_index.TryRemove(kv.Key, out var entry)) continue;
                _currentBytes -= entry.Size;
                Interlocked.Increment(ref _evictionCount);
                SafeDelete(BlobPath(kv.Key));
                SafeDelete(BlobPath(kv.Key) + ".h");
                PrometheusMetrics.Current?.RecordPar2PatchEviction();
            }
        }
    }

    internal Task EnsureCatalogLoadedAsync(CancellationToken ct)
    {
        if (IsCatalogReady)
            return Task.CompletedTask;

        Task load;
        lock (_catalogLoadSync)
        {
            if (IsCatalogReady)
                return Task.CompletedTask;

            if (_catalogLoadInFlight is { IsCompleted: false } inflight)
            {
                load = inflight;
            }
            else
            {
                load = LoadCatalogOnceAsync(ct);
                _catalogLoadInFlight = load;
            }
        }

        return AwaitCatalogLoadAsync(load, ct);
    }

    private async Task AwaitCatalogLoadAsync(Task load, CancellationToken ct)
    {
        try
        {
            await load.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_catalogLoadSync)
            {
                if (ReferenceEquals(_catalogLoadInFlight, load) && load.IsCompleted)
                    _catalogLoadInFlight = null;
            }

            throw;
        }
    }

    private async Task LoadCatalogOnceAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var snapshot = await Task.Run(
                () => BuildCatalogSnapshot(ct),
                CancellationToken.None)
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        PublishCatalog(snapshot);

        stopwatch.Stop();
        Log.Information(
            "Repair patch store catalog loaded: {Count} entries, {Size} bytes in {Elapsed}ms.",
            _index.Count,
            Interlocked.Read(ref _currentBytes),
            stopwatch.ElapsedMilliseconds);
    }

    private CatalogSnapshot BuildCatalogSnapshot(CancellationToken ct)
    {
        var entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        long bytes = 0;

        foreach (var file in _enumerateCacheFiles(ct))
        {
            ct.ThrowIfCancellationRequested();

            if (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                // Only reap orphans. A temp file that is still being staged by a
                // concurrent CommitPatches call must survive the scan.
                var temp = new FileInfo(file);
                if (!temp.Exists || DateTime.UtcNow - temp.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    SafeDelete(file);
                continue;
            }

            if (file.EndsWith(".h", StringComparison.Ordinal))
                continue;

            var info = new FileInfo(file);
            if (!info.Exists)
                continue;

            var entry = new CacheEntry
            {
                Size = info.Length,
                LastAccessTicks = info.LastWriteTimeUtc.Ticks,
            };

            if (entries.TryAdd(Path.GetFileName(file), entry))
                bytes = checked(bytes + info.Length);
        }

        return new CatalogSnapshot(entries, bytes);
    }

    private void PublishCatalog(CatalogSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Bytes);

        lock (_evictLock)
        {
            foreach (var (hash, scannedEntry) in snapshot.Entries)
            {
                // A live entry finalized during scanning is newer and wins.
                if (_index.TryAdd(hash, scannedEntry))
                    _currentBytes = checked(_currentBytes + scannedEntry.Size);
            }

            EvictIfNeeded();

            // Final release write: readers that observe ready also observe the index.
            Volatile.Write(ref _catalogReady, 1);
        }
    }

    private sealed class CatalogSnapshot
    {
        public CatalogSnapshot(IReadOnlyDictionary<string, CacheEntry> entries, long bytes)
        {
            Entries = entries;
            Bytes = bytes;
        }

        public IReadOnlyDictionary<string, CacheEntry> Entries { get; }
        public long Bytes { get; }
    }

    private string BlobPath(string hash) => Path.Join(_dir, hash[..2], hash);

    private static string Hash(string id)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug(e, "Repair patch store: could not delete {Path}", path);
        }
    }

    private sealed class CacheEntry
    {
        public long Size;
        public long LastAccessTicks;
    }
}
