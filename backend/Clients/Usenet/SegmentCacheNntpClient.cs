using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Contexts;
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
    internal static readonly TimeSpan TemporaryFileGracePeriod = TimeSpan.FromHours(1);
    private const int CommitStripeCount = 64;

    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly ProviderUsageTracker? _usageTracker;
    private readonly MetricsWriter? _metricsWriter;
    private readonly SegmentCacheStatistics _statistics;
    private readonly SegmentCacheGeneration _generation;
    private readonly ConcurrentDictionary<string, CacheEntry> _index = new();
    private readonly object _evictLock = new();
    private readonly object[] _commitStripes = CreateCommitStripes();
    private readonly Func<IEnumerable<string>> _enumerateCacheFiles;
    private readonly Func<string, SegmentCacheDeleteResult?>? _tryDelete;
    private readonly SegmentCacheWriteBehind? _writeBehind;
    private readonly Func<CancellationToken, Task>? _beforeWriteBehindPersist;
    private long _currentBytes;
    private int _catalogReady;
    private int _catalogDegraded;
    private long _lastWriteWarningTicks;

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
        SegmentCacheStatistics? statistics = null,
        Func<string, SegmentCacheDeleteResult?>? tryDelete = null,
        long writeBehindBytes = 0,
        Func<CancellationToken, Task>? beforeWriteBehindPersist = null) : base(inner)
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
        _tryDelete = tryDelete;
        _beforeWriteBehindPersist = beforeWriteBehindPersist;
        if (writeBehindBytes > 0)
        {
            _writeBehind = new SegmentCacheWriteBehind(
                writeBehindBytes,
                PersistWriteBehindAsync,
                WarnCacheWriteFailure,
                _generation.SetWriterSnapshot);
        }
        CatalogLoadTask = Task.Run(LoadIndex);
    }

    public bool IsCatalogReady => Volatile.Read(ref _catalogReady) != 0;
    internal Task CatalogLoadTask { get; }
    internal long CurrentBytes => Interlocked.Read(ref _currentBytes);
    internal Task DrainWriteBehindForTestsAsync() =>
        _writeBehind?.DrainForTestsAsync() ?? Task.CompletedTask;

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

        var local = TryOpenCacheResponse(segmentId, ct);
        if (local.Found)
        {
            ArticleBodyCompletion.InvokeContained(onConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return local.Response!;
        }

        var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, ct).ConfigureAwait(false);
        return await TransformRemoteForCachingAsync(segmentId, response, ct).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId segmentId, CancellationToken ct)
    {
        if (MultiProviderNntpClient.AttributionContext.Value == null)
        {
            var local = TryOpenCacheResponse(segmentId, ct);
            if (local.Found)
                return local.Response;
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

        var local = TryOpenCacheResponse(segmentId, ct);
        if (local.Found)
        {
            ArticleBodyCompletion.InvokeContained(
                exclusiveConnection.OnConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return local.Response!;
        }

        var response = await base.DecodedBodyAsync(segmentId, exclusiveConnection, ct).ConfigureAwait(false);
        return await TransformRemoteForCachingAsync(segmentId, response, ct).ConfigureAwait(false);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        if (MultiProviderNntpClient.AttributionContext.Value is not null)
        {
            _statistics.RecordBatchBypass(segmentIds.Count);
            return base.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);
        }

        return LocalDataBatchOverlay.ExecuteAsync(
            segmentIds,
            onConnectionReadyAgain,
            segmentId => TryOpenCacheResponse(segmentId, cancellationToken),
            (misses, callback, token) => base.DecodedBodiesAsync(misses, callback, token),
            TransformRemoteForCachingAsync,
            cancellationToken);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken)
    {
        if (MultiProviderNntpClient.AttributionContext.Value is not null)
        {
            _statistics.RecordBatchBypass(segmentIds.Count);
            return base.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);
        }

        return LocalDataBatchOverlay.ExecuteAsync(
            segmentIds,
            exclusiveConnection.OnConnectionReadyAgain,
            segmentId => TryOpenCacheResponse(segmentId, cancellationToken),
            (misses, callback, token) =>
                base.DecodedBodiesAsync(misses, new UsenetExclusiveConnection(callback), token),
            TransformRemoteForCachingAsync,
            cancellationToken);
    }

    private LocalLookupResult TryOpenCacheResponse(SegmentId segmentId, CancellationToken cancellationToken)
    {
        string id = segmentId;
        var lookup = TryServeFromCache(id, out var cached, out var servedBytes);
        if (lookup == CacheLookupResult.Hit)
        {
            _statistics.RecordHit(servedBytes);
            RecordCacheHit(DownloadWorkloadClassifier.ClassifyForMetrics(cancellationToken));
            return LocalLookupResult.Hit(cached!);
        }

        RecordLookup(lookup);
        return LocalLookupResult.Miss;
    }

    private async Task<UsenetDecodedBodyResponse> TransformRemoteForCachingAsync(
        SegmentId requestedId,
        UsenetDecodedBodyResponse response,
        CancellationToken cancellationToken)
    {
        if (response.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows ||
            response.Stream is null)
        {
            return response;
        }

        if (!string.Equals(requestedId.ToString(), response.SegmentId, StringComparison.Ordinal))
            return response;

        UsenetYencHeader? header;
        try
        {
            header = await response.Stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return response;
        }

        if (header is null || !IsCoherentHeader(header))
            return response;

        var hash = Hash(requestedId.ToString());
        var attempt = _statistics.BeginWriteAttempt();
        if (_writeBehind is not null)
        {
            PooledBufferStream? body = null;
            long reservedCapacity = 0;
            if (header.PartSize > int.MaxValue)
            {
                attempt.Complete(SegmentCacheWriteOutcome.Skipped, 0);
                return response;
            }

            try
            {
#pragma warning disable CA2000 // The finally releases ownership unless it is transferred to ValidatedCacheBufferingStream.
                if (!_writeBehind.TryRentBuffer(
                        (int)header.PartSize,
                        out body,
                        out reservedCapacity))
#pragma warning restore CA2000
                {
                    attempt.Complete(SegmentCacheWriteOutcome.Skipped, 0);
                    return response;
                }

                var stream = new ValidatedCacheBufferingStream(
                    response.Stream,
                    header,
                    hash,
                    body,
                    reservedCapacity,
                    _writeBehind,
                    attempt);
                body = null;
                return response with { Stream = stream };
            }
            finally
            {
                if (body is not null)
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                    _writeBehind.ReleaseReservation(reservedCapacity);
                }
            }
        }

        return response with
        {
            Stream = new ValidatedCacheWriteStream(
                response.Stream,
                header,
                BlobPath(hash),
                hash,
                CommitPublishedPair,
                attempt,
                WarnCacheWriteFailure),
        };
    }

    private async Task<SegmentCacheCommitResult> PersistWriteBehindAsync(
        PendingSegmentCacheWrite write,
        CancellationToken cancellationToken)
    {
        if (_beforeWriteBehindPersist is not null)
            await _beforeWriteBehindPersist(cancellationToken).ConfigureAwait(false);

        var blobPath = BlobPath(write.Hash);
        var unique = Guid.NewGuid().ToString("N");
        var blobTempPath = blobPath + "." + unique + ".tmp";
        var headerTempPath = blobPath + ".h." + unique + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            await using (var stream = new FileStream(
                             blobTempPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                                 BufferSize = 81920,
                                 PreallocationSize = write.Header.PartSize,
                             }))
            {
                await stream.WriteAsync(write.Body.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                    headerTempPath,
                    JsonSerializer.Serialize(write.Header, HeaderJsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            return CommitPublishedPair(
                write.Hash,
                blobTempPath,
                headerTempPath,
                write.Header,
                write.Body.Length);
        }
        finally
        {
            TryDelete(blobTempPath);
            TryDelete(headerTempPath);
        }
    }

    internal override void Retire()
    {
        _writeBehind?.Retire();
        base.Retire();
    }

    public override void Dispose()
    {
        _writeBehind?.Dispose();
        base.Dispose();
    }

    internal static bool IsCoherentHeader(UsenetYencHeader header)
    {
        if (header.PartSize <= 0 || header.PartOffset < 0 || header.FileSize < 0 || header.LineLength < 0)
            return false;
        if (header.PartNumber < 0 || header.TotalParts < 0)
            return false;
        if (header.PartNumber > 0 && header.TotalParts > 0 && header.PartNumber > header.TotalParts)
            return false;

        try
        {
            return header.FileSize >= checked(header.PartOffset + header.PartSize);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private CacheLookupResult TryServeFromCache(string id, out UsenetDecodedBodyResponse? response, out long servedBytes)
    {
        response = null;
        servedBytes = 0;
        if (!IsCatalogReady) return CacheLookupResult.NotReady;

        var hash = Hash(id);
        if (!_index.TryGetValue(hash, out var entry)) return CacheLookupResult.Miss;

        var blobPath = BlobPath(hash);
        FileStream? fileStream = null;
        try
        {
            var header = JsonSerializer.Deserialize<UsenetYencHeader>(
                File.ReadAllText(blobPath + ".h"), HeaderJsonOptions);
            if (header == null || header.PartSize != entry.Size || !IsCoherentHeader(header))
            {
                RecordReadFailureAndDrop(hash);
                return CacheLookupResult.ReadFailure;
            }

            fileStream = new FileStream(blobPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 81920, useAsync: true);
            if (fileStream.Length != header.PartSize)
            {
                fileStream.Dispose();
                fileStream = null;
                RecordReadFailureAndDrop(hash);
                return CacheLookupResult.ReadFailure;
            }

            entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            servedBytes = header.PartSize;
            response = new UsenetDecodedBodyResponse
            {
                SegmentId = id,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Article retrieved from segment cache",
                Stream = new CachedYencStream(header, fileStream),
            };
            fileStream = null;
            return CacheLookupResult.Hit;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            fileStream?.Dispose();
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

    private void RecordCacheHit(SegmentFetch.FetchWorkload workload)
    {
        _usageTracker?.RecordSuccess(CacheProviderName);
        PrometheusMetrics.Current?.RecordSegmentFetch(CacheProviderName, "ok", TimeSpan.Zero);
        _metricsWriter?.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = CacheProviderName,
            ReadSessionId = MultiProviderNntpClient.CurrentReadSessionId,
            Workload = workload,
            Bytes = 0,
            DurationMs = 0,
            Status = SegmentFetch.FetchStatus.Ok,
            Retries = 0,
        });
    }

    private SegmentCacheCommitResult CommitPublishedPair(
        string hash,
        string blobTempPath,
        string headerTempPath,
        UsenetYencHeader header,
        long decodedBytes)
    {
        if (decodedBytes != header.PartSize)
            return SegmentCacheCommitResult.InvalidLength;

        var blobPath = BlobPath(hash);
        var headerPath = blobPath + ".h";
        var commitLock = StripeFor(hash);
        var result = SegmentCacheCommitResult.Failed;
        lock (commitLock)
        {
            if (TryValidatePublishedEntry(hash, out var existingSize))
            {
                TryDelete(blobTempPath);
                TryDelete(headerTempPath);
                PublishIndexEntry(hash, existingSize);
                result = SegmentCacheCommitResult.AlreadyPresent;
            }
            else
            {
                // Catalog leaves recent malformed pairs on disk without indexing them.
                // Remove that residue while we hold the stripe so this commit can publish.
                TryDelete(blobPath);
                TryDelete(headerPath);

                var blobPublished = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
                    File.Move(blobTempPath, blobPath, overwrite: false);
                    blobPublished = true;
                    File.Move(headerTempPath, headerPath, overwrite: false);
                    PublishIndexEntry(hash, decodedBytes);
                    result = SegmentCacheCommitResult.Committed;
                }
                catch (IOException) when (TryValidatePublishedEntry(hash, out var racedSize))
                {
                    PublishIndexEntry(hash, racedSize);
                    result = SegmentCacheCommitResult.AlreadyPresent;
                }
                catch
                {
                    if (blobPublished && !_index.ContainsKey(hash))
                        TryDelete(blobPath);
                    throw;
                }
                finally
                {
                    TryDelete(blobTempPath);
                    TryDelete(headerTempPath);
                }
            }
        }

        if (result is SegmentCacheCommitResult.Committed or SegmentCacheCommitResult.AlreadyPresent)
        {
            EvictIfNeeded();
            PublishIndexGauges();
        }

        return result;
    }

    private void PublishIndexEntry(string hash, long size)
    {
        lock (_evictLock)
        {
            if (_index.TryGetValue(hash, out var existing))
            {
                if (existing.Size == size)
                {
                    existing.LastAccessTicks = DateTime.UtcNow.Ticks;
                    return;
                }

                _currentBytes -= existing.Size;
            }

            _index[hash] = new CacheEntry { Size = size, LastAccessTicks = DateTime.UtcNow.Ticks };
            _currentBytes += size;
        }
    }

    private bool TryValidatePublishedEntry(string hash, out long size)
    {
        size = 0;
        var blobPath = BlobPath(hash);
        var headerPath = blobPath + ".h";
        if (!File.Exists(blobPath) || !File.Exists(headerPath))
            return false;

        try
        {
            var info = new FileInfo(blobPath);
            var header = JsonSerializer.Deserialize<UsenetYencHeader>(
                File.ReadAllText(headerPath), HeaderJsonOptions);
            if (header is null || !IsCoherentHeader(header) || header.PartSize != info.Length)
                return false;

            size = info.Length;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
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
            var blobResult = TryDelete(BlobPath(kv.Key));
            if (blobResult == SegmentCacheDeleteResult.Failed)
                continue;

            TryDelete(BlobPath(kv.Key) + ".h");
            if (!_index.TryRemove(kv.Key, out var entry))
                continue;

            _currentBytes -= entry.Size;
            evictedEntries++;
            evictedBytes += entry.Size;
        }
    }

    private void PublishIndexGauges()
    {
        long entries;
        long bytes;
        lock (_evictLock)
        {
            entries = _index.Count;
            bytes = _currentBytes;
        }

        _generation.SetIndex(entries, bytes);
    }

    private void LoadIndex()
    {
        var stopwatch = Stopwatch.StartNew();
        var cleaned = 0;
        try
        {
            foreach (var file in _enumerateCacheFiles())
            {
                try
                {
                    if (file.EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        if (IsStale(file) && TryDelete(file) == SegmentCacheDeleteResult.Deleted)
                        {
                            cleaned++;
                            _statistics.RecordTemporaryFileCleaned();
                        }

                        continue;
                    }

                    if (file.EndsWith(".h", StringComparison.Ordinal))
                    {
                        var blobForHeader = file[..^2];
                        if (!File.Exists(blobForHeader) && IsStale(file))
                            TryDelete(file);
                        continue;
                    }

                    var info = new FileInfo(file);
                    if (!info.Exists) continue;

                    var headerPath = file + ".h";
                    if (!File.Exists(headerPath))
                    {
                        if (IsStale(file))
                            TryDelete(file);
                        continue;
                    }

                    UsenetYencHeader? header;
                    try
                    {
                        header = JsonSerializer.Deserialize<UsenetYencHeader>(
                            File.ReadAllText(headerPath), HeaderJsonOptions);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        _statistics.RecordReadFailure();
                        continue;
                    }

                    if (header is null || !IsCoherentHeader(header) || header.PartSize != info.Length)
                    {
                        _statistics.RecordReadFailure();
                        if (IsStale(file))
                        {
                            TryDelete(file);
                            TryDelete(headerPath);
                        }

                        continue;
                    }

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
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // One malformed entry must not abort the rest of the scan.
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
                long entries;
                long bytes;
                lock (_evictLock)
                {
                    Volatile.Write(ref _catalogReady, 1);
                    entries = _index.Count;
                    bytes = _currentBytes;
                }

                stopwatch.Stop();
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

    private static bool IsStale(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc > TemporaryFileGracePeriod;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string BlobPath(string hash) => Path.Join(_dir, hash[..2], hash);

    /// <summary>
    /// Deletes every cache artifact under <paramref name="cacheDir"/>. Only files that
    /// match the cache layout (two-hex-char shard directory, hex blob name with optional
    /// <c>.h</c> / writer-generated <c>.tmp</c> suffix) are removed so a misconfigured path
    /// never loses unrelated data. Symlinked shard directories are ignored. Empty shard
    /// directories are removed afterwards.
    /// </summary>
    internal static SegmentCachePurgeResult PurgeDirectory(string cacheDir)
    {
        var result = new SegmentCachePurgeResult();
        if (!Directory.Exists(cacheDir)) return result;

        foreach (var shard in Directory.EnumerateDirectories(cacheDir))
        {
            if (!IsShardDirectoryName(Path.GetFileName(shard))) continue;
            if (IsReparsePoint(shard, result)) continue;

            foreach (var file in Directory.EnumerateFiles(shard))
            {
                if (!IsCacheFileName(Path.GetFileName(file)))
                {
                    result.Skipped++;
                    continue;
                }

                switch (DeleteCacheFile(file, out var failure))
                {
                    case SegmentCacheDeleteResult.Deleted:
                        result.Deleted++;
                        break;
                    case SegmentCacheDeleteResult.Failed:
                        result.RecordFailure(failure);
                        break;
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(shard).Any())
                    Directory.Delete(shard);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.RecordFailure(exception);
            }
        }

        return result;
    }

    private static bool IsReparsePoint(string path, SegmentCachePurgeResult result)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.RecordFailure(exception);
            return true;
        }
    }

    private static bool IsShardDirectoryName(string name) =>
        name.Length == 2 && name.All(Uri.IsHexDigit);

    private static bool IsCacheFileName(string name)
    {
        // <hash> | <hash>.h | <hash>.<guid:N>.tmp | <hash>.h.<guid:N>.tmp
        var hashEnd = name.IndexOf('.', StringComparison.Ordinal);
        var hash = hashEnd < 0 ? name : name[..hashEnd];
        if (!IsHex(hash, 64)) return false;
        if (hashEnd < 0) return true;

        var suffix = name.AsSpan(hashEnd);
        if (suffix.SequenceEqual(".h")) return true;
        if (suffix.StartsWith(".h.")) suffix = suffix[2..];
        if (!suffix.EndsWith(".tmp") || suffix.Length != 1 + 32 + 4) return false;
        return IsHex(suffix[1..^4], 32);
    }

    private static bool IsHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length) return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static string Hash(string id)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private object StripeFor(string hash) =>
        _commitStripes[(hash.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % CommitStripeCount];

    private static object[] CreateCommitStripes()
    {
        var stripes = new object[CommitStripeCount];
        for (var index = 0; index < stripes.Length; index++)
            stripes[index] = new object();
        return stripes;
    }

    private void WarnCacheWriteFailure()
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastWriteWarningTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWriteWarningTicks, nowTicks, last) != last)
            return;

        Log.Warning("Segment cache write failed. Reason: {Reason}", "storage-unavailable");
    }

    private SegmentCacheDeleteResult TryDelete(string path)
    {
        var overrideResult = _tryDelete?.Invoke(path);
        if (overrideResult is not null)
            return overrideResult.Value;

        return DeleteCacheFile(path);
    }

    private static SegmentCacheDeleteResult DeleteCacheFile(string path) => DeleteCacheFile(path, out _);

    private static SegmentCacheDeleteResult DeleteCacheFile(string path, out Exception? failure)
    {
        failure = null;
        try
        {
            _ = File.GetAttributes(path);
            File.Delete(path);
            return SegmentCacheDeleteResult.Deleted;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SegmentCacheDeleteResult.Absent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception;
            return SegmentCacheDeleteResult.Failed;
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

    private sealed class ValidatedCacheBufferingStream : YencStream
    {
        private readonly YencStream _source;
        private readonly UsenetYencHeader _header;
        private readonly string _hash;
        private readonly PooledBufferStream _body;
        private readonly long _reservedCapacity;
        private readonly SegmentCacheWriteBehind _writer;
        private readonly SegmentCacheWriteAttempt _attempt;
        private long _readBytes;
        private bool _eof;
        private int _completed;
        private int _disposed;

        internal ValidatedCacheBufferingStream(
            YencStream source,
            UsenetYencHeader header,
            string hash,
            PooledBufferStream body,
            long reservedCapacity,
            SegmentCacheWriteBehind writer,
            SegmentCacheWriteAttempt attempt) : base(Null)
        {
            _source = source;
            _header = header;
            _hash = hash;
            _body = body;
            _reservedCapacity = reservedCapacity;
            _writer = writer;
            _attempt = attempt;
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UsenetYencHeader?>(_header);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                return 0;
            }

            var previous = _readBytes;
            _readBytes = checked(_readBytes + read);
            var remaining = Math.Max(0, _header.PartSize - previous);
            var retained = (int)Math.Min(read, remaining);
            if (retained > 0)
                _body.Write(buffer.Span[..retained]);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                base.Dispose(disposing);
                return;
            }

            Exception? sourceFailure = null;
            try
            {
                _source.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                sourceFailure = exception;
                throw;
            }
            finally
            {
                Complete(sourceFailure is null);
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Exception? sourceFailure = null;
            try
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                sourceFailure = exception;
                throw;
            }
            finally
            {
                Complete(sourceFailure is null);
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void Complete(bool sourceSucceeded)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            if (sourceSucceeded && _eof && _readBytes == _header.PartSize &&
                _writer.TryEnqueue(new PendingSegmentCacheWrite(
                    _hash,
                    _header,
                    _body,
                    _reservedCapacity,
                    _attempt)))
            {
                return;
            }

            _body.Dispose();
            _writer.ReleaseReservation(_reservedCapacity);
            _attempt.Complete(SegmentCacheWriteOutcome.Skipped, _readBytes);
        }
    }

    private sealed class ValidatedCacheWriteStream : YencStream
    {
        private readonly YencStream _source;
        private readonly UsenetYencHeader _header;
        private readonly string _blobPath;
        private readonly string _blobTempPath;
        private readonly string _headerTempPath;
        private readonly string _hash;
        private readonly Func<string, string, string, UsenetYencHeader, long, SegmentCacheCommitResult> _commit;
        private readonly SegmentCacheWriteAttempt _attempt;
        private readonly Action _warnWriteFailure;
        private FileStream? _temp;
        private long _written;
        private bool _eof;
        private bool _writeFailed;
        private bool _writeEnabled = true;
        private int _completed;
        private int _disposed;

        public ValidatedCacheWriteStream(
            YencStream source,
            UsenetYencHeader header,
            string blobPath,
            string hash,
            Func<string, string, string, UsenetYencHeader, long, SegmentCacheCommitResult> commit,
            SegmentCacheWriteAttempt attempt,
            Action warnWriteFailure) : base(Null)
        {
            _source = source;
            _header = header;
            _blobPath = blobPath;
            _hash = hash;
            _commit = commit;
            _attempt = attempt;
            _warnWriteFailure = warnWriteFailure;
            var unique = Guid.NewGuid().ToString("N");
            _blobTempPath = blobPath + "." + unique + ".tmp";
            _headerTempPath = blobPath + ".h." + unique + ".tmp";
        }

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<UsenetYencHeader?>(_header);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                return 0;
            }

            if (_writeEnabled)
            {
                try
                {
                    _temp ??= OpenTemp();
                    await _temp.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false);
                    _written = checked(_written + read);
                    if (_written > _header.PartSize)
                        DisableWrite(failed: false);
                }
                catch (Exception exception) when (IsBestEffortCacheIoFailure(exception))
                {
                    DisableWrite(failed: true);
                }
            }

            return read;
        }

        private FileStream OpenTemp()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_blobPath)!);
            return new FileStream(
                _blobTempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);
        }

        private void DisableWrite(bool failed)
        {
            _writeEnabled = false;
            if (failed)
            {
                _writeFailed = true;
                _warnWriteFailure();
            }

            try
            {
                _temp?.Dispose();
            }
            catch (Exception exception) when (IsBestEffortCacheIoFailure(exception))
            {
                // Best-effort: playback continues without a cache entry.
            }

            _temp = null;
            DeleteCacheFile(_blobTempPath);
            DeleteCacheFile(_headerTempPath);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                base.Dispose(disposing);
                return;
            }

            Exception? sourceFailure = null;
            try
            {
                _source.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                sourceFailure = exception;
                throw;
            }
            finally
            {
                CompleteWrite(sourceFailure is null);
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Exception? sourceFailure = null;
            try
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                sourceFailure = exception;
                throw;
            }
            finally
            {
                CompleteWrite(sourceFailure is null);
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void CompleteWrite(bool sourceSucceeded)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            try
            {
                try
                {
                    _temp?.Flush();
                    _temp?.Dispose();
                }
                catch (Exception exception) when (IsBestEffortCacheIoFailure(exception))
                {
                    _writeFailed = true;
                    _warnWriteFailure();
                }

                _temp = null;

                if (sourceSucceeded && _eof && !_writeFailed && _written == _header.PartSize)
                {
                    File.WriteAllText(_headerTempPath, JsonSerializer.Serialize(_header, HeaderJsonOptions));
                    var result = _commit(_hash, _blobTempPath, _headerTempPath, _header, _written);
                    switch (result)
                    {
                        case SegmentCacheCommitResult.Committed:
                            _attempt.Complete(SegmentCacheWriteOutcome.Committed, _written);
                            break;
                        case SegmentCacheCommitResult.AlreadyPresent:
                        case SegmentCacheCommitResult.InvalidLength:
                            _attempt.Complete(SegmentCacheWriteOutcome.Skipped, _written);
                            break;
                        default:
                            _attempt.Complete(SegmentCacheWriteOutcome.Failed, _written);
                            break;
                    }

                    return;
                }

                DeleteCacheFile(_blobTempPath);
                DeleteCacheFile(_headerTempPath);
                _attempt.Complete(
                    _writeFailed ? SegmentCacheWriteOutcome.Failed : SegmentCacheWriteOutcome.Skipped,
                    _written);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeleteCacheFile(_blobTempPath);
                DeleteCacheFile(_headerTempPath);
                _attempt.Complete(SegmentCacheWriteOutcome.Failed, _written);
                if (IsBestEffortCacheIoFailure(exception))
                    _warnWriteFailure();
            }
        }

        private static bool IsBestEffortCacheIoFailure(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException;
    }
}

internal enum SegmentCacheDeleteResult
{
    Absent,
    Deleted,
    Failed,
}

internal sealed class SegmentCachePurgeResult
{
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; private set; }

    /// <summary>First failure's exception type, without paths or messages.</summary>
    public string? FailureReason { get; private set; }

    public void RecordFailure(Exception? exception)
    {
        Failed++;
        FailureReason ??= exception?.GetType().Name ?? "unknown";
    }
}

internal enum SegmentCacheCommitResult
{
    Committed,
    AlreadyPresent,
    InvalidLength,
    Failed,
}
