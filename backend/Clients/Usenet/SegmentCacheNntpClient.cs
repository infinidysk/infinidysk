using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

public sealed class SegmentCacheNntpClient : WrappingNntpClient
{
    public const string CacheProviderName = "segment-cache";
    internal const string DefaultCachePath = "/config/segment-cache";

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly ProviderUsageTracker? _usageTracker;
    private readonly MetricsWriter? _metricsWriter;
    private readonly SegmentCacheStatistics _statistics;
    private readonly SegmentCacheGeneration _generation;
    private readonly ConcurrentDictionary<string, CacheEntry> _index = new();
    private readonly object _evictLock = new();
    private readonly Func<IEnumerable<string>> _enumerateCacheFiles;
    private long _currentBytes;
    private int _catalogReady;
    private int _catalogDegraded;

    private static readonly JsonSerializerOptions HeaderJsonOptions = new() { IncludeFields = true };

    public SegmentCacheNntpClient(
        INntpClient inner,
        string cacheDir,
        long maxBytes,
        ProviderUsageTracker? usageTracker = null,
        MetricsWriter? metricsWriter = null)
        : this(inner, cacheDir, maxBytes, usageTracker, metricsWriter, enumerateCacheFiles: null, statistics: null)
    {
    }

    internal SegmentCacheNntpClient(
        INntpClient inner,
        string cacheDir,
        long maxBytes,
        ProviderUsageTracker? usageTracker,
        MetricsWriter? metricsWriter,
        Func<IEnumerable<string>>? enumerateCacheFiles,
        SegmentCacheStatistics? statistics = null) : base(inner)
    {
        _dir = cacheDir;
        _maxBytes = maxBytes;
        _usageTracker = usageTracker;
        _metricsWriter = metricsWriter;
        _statistics = statistics ?? new SegmentCacheStatistics();
        _generation = _statistics.BeginGeneration(enabled: true, maxBytes);
        Directory.CreateDirectory(_dir);
        _enumerateCacheFiles = enumerateCacheFiles
                               ?? (() => Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories));
        CatalogLoadTask = Task.Run(LoadIndex);
    }

    public bool IsCatalogReady => Volatile.Read(ref _catalogReady) != 0;
    internal Task CatalogLoadTask { get; }
    internal long CurrentBytes => Interlocked.Read(ref _currentBytes);

    internal static string ClassifyCachePath(string path) =>
        string.Equals(path, DefaultCachePath, StringComparison.Ordinal)
            ? "[CONFIG_PATH]/segment-cache"
            : "[CUSTOM_PATH]";

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, ct);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);

        string id = segmentId;
        var lookup = TryServeFromCache(id, out var cached, out var servedBytes);
        if (lookup == CacheLookupResult.Hit)
        {
            _statistics.RecordHit(servedBytes);
            RecordCacheHit();
            ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return cached!;
        }

        RecordLookup(lookup);
        var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
        return await WrapForCachingAsync(id, response, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId segmentId, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value == null)
        {
            var lookup = TryServeFromCache(segmentId.ToString(), out var cached, out var servedBytes);
            if (lookup == CacheLookupResult.Hit)
            {
                _statistics.RecordHit(servedBytes);
                RecordCacheHit();
                return cached;
            }

            RecordLookup(lookup);
        }

        return await base.TryGetLocalDecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value == null
            && IsCatalogReady
            && _index.ContainsKey(Hash(segmentId)))
            return new UsenetExclusiveConnection(onConnectionReadyAgain: null);
        return await base.AcquireExclusiveConnectionAsync(segmentId, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value != null)
            return await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);

        string id = segmentId;
        var lookup = TryServeFromCache(id, out var cached, out var servedBytes);
        if (lookup == CacheLookupResult.Hit)
        {
            _statistics.RecordHit(servedBytes);
            RecordCacheHit();
            ArticleBodyCompletion.InvokeContained(
                exclusiveConnection.OnConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return cached!;
        }

        RecordLookup(lookup);
        var response = await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);
        return await WrapForCachingAsync(id, response, ct).ConfigureAwait(false);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        _statistics.RecordBatchBypass(segmentIds.Count);
        return base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken)
    {
        _statistics.RecordBatchBypass(segmentIds.Count);
        return base.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);
    }

    private async Task<UsenetDecodedBodyResponse> WrapForCachingAsync(
        string id, UsenetDecodedBodyResponse response, CancellationToken ct)
    {
        if (response.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows ||
            response.Stream == null)
            return response;

        var source = response.Stream;
        UsenetYencHeader? header = null;
        try
        {
            header = await source.GetYencHeadersAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            header = null;
        }

        if (header == null) return response;
        var attempt = _statistics.BeginWriteAttempt();
        return response with
        {
            Stream = new WriteThroughStream(source, header, BlobPath(Hash(id)), OnCommitted, attempt),
        };
    }

    private CacheLookupResult TryServeFromCache(string id, out UsenetDecodedBodyResponse? response, out long servedBytes)
    {
        response = null;
        servedBytes = 0;
        if (!IsCatalogReady) return CacheLookupResult.NotReady;

        var hash = Hash(id);
        if (!_index.TryGetValue(hash, out var entry)) return CacheLookupResult.Miss;

        var blobPath = BlobPath(hash);
        try
        {
            var header = JsonSerializer.Deserialize<UsenetYencHeader>(
                File.ReadAllText(blobPath + ".h"), HeaderJsonOptions);
            if (header == null || header.PartSize != entry.Size)
            {
                RecordReadFailureAndDrop(hash);
                return CacheLookupResult.ReadFailure;
            }

            var fileStream = new FileStream(blobPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 81920, useAsync: true);
            entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            servedBytes = header.PartSize;
            response = new UsenetDecodedBodyResponse
            {
                SegmentId = id,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Article retrieved from segment cache",
                Stream = new CachedYencStream(header, fileStream),
            };
            return CacheLookupResult.Hit;
        }
        catch
        {
            RecordReadFailureAndDrop(hash);
            return CacheLookupResult.ReadFailure;
        }
    }

    private void RecordLookup(CacheLookupResult lookup)
    {
        switch (lookup)
        {
            case CacheLookupResult.NotReady:
                _statistics.RecordLookupUnavailable();
                break;
            case CacheLookupResult.Miss:
                _statistics.RecordMiss();
                break;
        }
    }

    private void RecordReadFailureAndDrop(string hash)
    {
        _statistics.RecordReadFailure();
        Drop(hash);
        PublishIndexGauges();
    }

    private void RecordCacheHit()
    {
        _usageTracker?.RecordSuccess(CacheProviderName);
        PrometheusMetrics.Current?.RecordSegmentFetch(CacheProviderName, "ok", TimeSpan.Zero);
        _metricsWriter?.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = CacheProviderName,
            ReadSessionId = MultiProviderNntpClient.CurrentReadSessionId,
            Bytes = 0,
            DurationMs = 0,
            Status = SegmentFetch.FetchStatus.Ok,
            Retries = 0,
        });
    }

    private void OnCommitted(string hash, long size)
    {
        long evictedEntries = 0;
        long evictedBytes = 0;
        lock (_evictLock)
        {
            if (_index.TryGetValue(hash, out var existing)) _currentBytes -= existing.Size;
            _index[hash] = new CacheEntry { Size = size, LastAccessTicks = DateTime.UtcNow.Ticks };
            _currentBytes += size;
            EvictWhileLocked(ref evictedEntries, ref evictedBytes);
        }

        if (evictedEntries > 0)
            _statistics.RecordEviction(evictedEntries, evictedBytes);
        PublishIndexGauges();
    }

    private void Drop(string hash)
    {
        lock (_evictLock)
        {
            if (_index.TryRemove(hash, out var entry)) _currentBytes -= entry.Size;
        }

        TryDelete(BlobPath(hash));
        TryDelete(BlobPath(hash) + ".h");
    }

    private void EvictIfNeeded()
    {
        if (Interlocked.Read(ref _currentBytes) <= _maxBytes) return;
        long evictedEntries = 0;
        long evictedBytes = 0;
        lock (_evictLock)
        {
            if (_currentBytes <= _maxBytes) return;
            EvictWhileLocked(ref evictedEntries, ref evictedBytes);
        }

        if (evictedEntries > 0)
            _statistics.RecordEviction(evictedEntries, evictedBytes);
        PublishIndexGauges();
    }

    private void EvictWhileLocked(ref long evictedEntries, ref long evictedBytes)
    {
        if (_currentBytes <= _maxBytes) return;
        foreach (var kv in _index.OrderBy(x => x.Value.LastAccessTicks).ToList())
        {
            if (_currentBytes <= _maxBytes) break;
            if (!_index.TryRemove(kv.Key, out var entry)) continue;
            _currentBytes -= entry.Size;
            evictedEntries++;
            evictedBytes += entry.Size;
            TryDelete(BlobPath(kv.Key));
            TryDelete(BlobPath(kv.Key) + ".h");
        }
    }

    private void PublishIndexGauges() =>
        _generation.SetIndex(_index.Count, Interlocked.Read(ref _currentBytes));

    private void LoadIndex()
    {
        var stopwatch = Stopwatch.StartNew();
        var cleaned = 0;
        try
        {
            foreach (var file in _enumerateCacheFiles())
            {
                if (file.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    if (TryDelete(file))
                    {
                        cleaned++;
                        _statistics.RecordTemporaryFileCleaned();
                    }

                    continue;
                }

                if (file.EndsWith(".h", StringComparison.Ordinal)) continue;
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                var entry = new CacheEntry
                {
                    Size = info.Length,
                    LastAccessTicks = info.LastWriteTimeUtc.Ticks,
                };

                lock (_evictLock)
                {
                    if (_index.TryAdd(Path.GetFileName(file), entry))
                        _currentBytes += info.Length;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _catalogDegraded, 1);
            Log.Warning("Segment cache catalog scan failed; starting empty.");
            Log.Debug(e, "Segment cache catalog scan failure stack");
        }
        finally
        {
            try
            {
                EvictIfNeeded();
            }
            finally
            {
                Volatile.Write(ref _catalogReady, 1);
                stopwatch.Stop();
                var entries = _index.Count;
                var bytes = Interlocked.Read(ref _currentBytes);
                _generation.SetCatalogReady(stopwatch.ElapsedMilliseconds, entries, bytes);
                Log.Information(
                    "Segment cache catalog ready. Enabled: {Enabled}. Path: {PathClass}. MaxBytes: {MaxBytes}. " +
                    "Entries: {Count}. Bytes: {Size}. DurationMs: {Elapsed}. TemporaryFilesCleaned: {Cleaned}. " +
                    "Degraded: {Degraded}.",
                    true,
                    ClassifyCachePath(_dir),
                    _maxBytes,
                    entries,
                    bytes,
                    stopwatch.ElapsedMilliseconds,
                    cleaned,
                    Volatile.Read(ref _catalogDegraded) != 0);
            }
        }
    }

    private string BlobPath(string hash) => Path.Join(_dir, hash[..2], hash);

    private static string Hash(string id)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum CacheLookupResult
    {
        NotReady,
        Miss,
        ReadFailure,
        Hit,
    }

    private sealed class CacheEntry
    {
        public long Size;
        public long LastAccessTicks;
    }

    private sealed class WriteThroughStream : YencStream
    {
        private readonly YencStream _source;
        private readonly UsenetYencHeader _header;
        private readonly string _blobPath;
        private readonly string _tempPath;
        private readonly Action<string, long> _onCommitted;
        private readonly SegmentCacheWriteAttempt _attempt;
        private FileStream? _temp;
        private long _written;
        private bool _eof;
        private bool _writeFailed;

        public WriteThroughStream(
            YencStream source,
            UsenetYencHeader header,
            string blobPath,
            Action<string, long> onCommitted,
            SegmentCacheWriteAttempt attempt) : base(Null)
        {
            _source = source;
            _header = header;
            _blobPath = blobPath;
            _tempPath = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            _onCommitted = onCommitted;
            _attempt = attempt;
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<UsenetYencHeader?>(_header);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                if (!_writeFailed)
                {
                    try
                    {
                        _temp ??= OpenTemp();
                        await _temp.WriteAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
                        _written += n;
                    }
                    catch
                    {
                        _writeFailed = true;
                    }
                }
            }
            else
            {
                _eof = true;
            }

            return n;
        }

        private FileStream OpenTemp()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_blobPath)!);
            return new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
                try
                {
                    _temp?.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    if (_eof && !_writeFailed && _temp != null && _written == _header.PartSize)
                    {
                        File.WriteAllText(_blobPath + ".h", JsonSerializer.Serialize(_header, HeaderJsonOptions));
                        File.Move(_tempPath, _blobPath, overwrite: true);
                        _onCommitted(Path.GetFileName(_blobPath), _written);
                        _attempt.Complete(SegmentCacheWriteOutcome.Committed, _written);
                    }
                    else
                    {
                        TryDelete(_tempPath);
                        _attempt.Complete(
                            _writeFailed ? SegmentCacheWriteOutcome.Failed : SegmentCacheWriteOutcome.Skipped,
                            _written);
                    }
                }
                catch
                {
                    TryDelete(_tempPath);
                    TryDelete(_blobPath + ".h");
                    _attempt.Complete(SegmentCacheWriteOutcome.Failed, _written);
                }
            }

            base.Dispose(disposing);
        }
    }
}
