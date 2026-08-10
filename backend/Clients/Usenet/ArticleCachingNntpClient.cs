using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for caching Article/Body commands to disk.
/// It is intended to be short-lived. It will delete all cached articles on disposal.
/// </summary>
/// <param name="usenetClient">The underlying client to cache.</param>
/// <param name="leaveOpen">Indicates whether disposing this client also disposes the underlying client.</param>
public class ArticleCachingNntpClient(
    INntpClient usenetClient,
    bool leaveOpen = true
) : WrappingNntpClient(usenetClient)
{
    private readonly string _cacheDir = Directory.CreateTempSubdirectory().FullName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<NzbSegment>> _trackedSegments = new();

    private record CacheEntry(
        UsenetYencHeader YencHeaders,
        bool HasArticleHeaders,
        UsenetArticleHeader? ArticleHeaders);
    private sealed record CachedBatchItem(int Index, string Key, CacheEntry Entry);
    private sealed record MissingBatchItem(int Index, SegmentId SegmentId);
    private sealed record BatchCachePartition(
        IReadOnlyList<CachedBatchItem> Cached,
        IReadOnlyList<MissingBatchItem> Missing);

    public void TrackNzbFiles(IEnumerable<NzbFile> nzbFiles)
    {
        foreach (var segment in nzbFiles.SelectMany(x => x.Segments))
        {
            _trackedSegments
                .GetOrAdd(segment.MessageId, _ => [])
                .Add(segment);

            if (_cachedSegments.TryGetValue(segment.MessageId, out var cacheEntry))
                segment.ByteRange = GetByteRange(cacheEntry.YencHeaders);
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            // Check if already cached
            if (_cachedSegments.TryGetValue(segmentId, out var existingEntry))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                return ReadCachedBodyAsync(segmentId, existingEntry.YencHeaders);
            }

            // Fetch and cache the body
            var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);

            // Get the decoded stream
            await using var stream = response.Stream;

            // Get yenc headers before caching the decoded stream
            var yencHeaders = await stream!.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");

            await CacheDecodedStreamAsync(segmentId, stream, cancellationToken).ConfigureAwait(false);

            // Mark as cached (body only, no article headers yet)
            AddCacheEntry(segmentId, new CacheEntry(
                YencHeaders: yencHeaders,
                HasArticleHeaders: false,
                ArticleHeaders: null));

            // Return a new stream from the cached file
            return ReadCachedBodyAsync(segmentId, yencHeaders);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        var partition = PartitionBatch(segmentIds);
        if (partition.Missing.Count == 0)
        {
            InvokeCompletionCallback(
                onConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return MergeBatchForCaching(
                segmentIds.Count, partition, null, cancellationToken);
        }

        var missingSegmentIds = partition.Missing.Select(item => item.SegmentId).ToArray();
        var batch = await base.DecodedBodiesAsync(
            missingSegmentIds, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        return MergeBatchForCaching(
            segmentIds.Count, partition, batch, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            // Check if already cached with headers
            if (_cachedSegments.TryGetValue(segmentId, out var cacheEntry))
            {
                if (cacheEntry.HasArticleHeaders)
                {
                    // Full article is cached, read from cache
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    return ReadCachedArticleAsync(segmentId, cacheEntry.YencHeaders, cacheEntry.ArticleHeaders!);
                }
                else
                {
                    // Only body is cached, fetch article headers separately
                    UsenetHeadResponse? headResponse = null;
                    try
                    {
                        headResponse = await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    }

                    // Update cache entry to include article headers
                    var updatedEntry = new CacheEntry(
                        YencHeaders: cacheEntry.YencHeaders,
                        HasArticleHeaders: true,
                        ArticleHeaders: headResponse.ArticleHeaders);

                    _cachedSegments.TryUpdate(segmentId, updatedEntry, cacheEntry);

                    return ReadCachedArticleAsync(segmentId, cacheEntry.YencHeaders, headResponse.ArticleHeaders!);
                }
            }

            // Fetch and cache the full article
            var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);

            // Get the decoded stream
            await using var stream = response.Stream;

            // Get yenc headers before caching the decoded stream
            var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (yencHeaders == null)
            {
                throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");
            }

            await CacheDecodedStreamAsync(segmentId, stream, cancellationToken).ConfigureAwait(false);

            // Mark as cached with both yenc and article headers
            AddCacheEntry(segmentId, new CacheEntry(
                YencHeaders: yencHeaders,
                HasArticleHeaders: true,
                ArticleHeaders: response.ArticleHeaders));

            // Return a new stream from the cached file
            return ReadCachedArticleAsync(segmentId, yencHeaders, response.ArticleHeaders);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cachedSegments.ContainsKey(segmentId)
                ? new UsenetExclusiveConnection(onConnectionReadyAgain: null)
                : await base.AcquireExclusiveConnectionAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        return segmentIds.All(segmentId => _cachedSegments.ContainsKey(segmentId))
            ? Task.FromResult(new UsenetExclusiveConnection(onConnectionReadyAgain: null))
            : base.AcquireExclusiveConnectionAsync(segmentIds, cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var partition = PartitionBatch(segmentIds);
        if (partition.Missing.Count == 0)
        {
            InvokeCompletionCallback(
                exclusiveConnection.OnConnectionReadyAgain, ArticleBodyResult.Retrieved);
            return MergeBatchForCaching(
                segmentIds.Count, partition, null, cancellationToken);
        }

        var missingSegmentIds = partition.Missing.Select(item => item.SegmentId).ToArray();
        var batch = await base.DecodedBodiesAsync(
            missingSegmentIds, exclusiveConnection, cancellationToken).ConfigureAwait(false);
        return MergeBatchForCaching(
            segmentIds.Count, partition, batch, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        return _cachedSegments.TryGetValue(segmentId, out var existingEntry)
            ? Task.FromResult(existingEntry.YencHeaders)
            : base.GetYencHeadersAsync(segmentId, ct);
    }

    private void AddCacheEntry(string segmentId, CacheEntry cacheEntry)
    {
        _cachedSegments.TryAdd(segmentId, cacheEntry);
        if (!_trackedSegments.TryGetValue(segmentId, out var trackedSegments)) return;

        var byteRange = GetByteRange(cacheEntry.YencHeaders);
        foreach (var segment in trackedSegments)
            segment.ByteRange = byteRange;
    }

    private UsenetDecodedBodyBatch MergeBatchForCaching(
        int responseCount,
        BatchCachePartition partition,
        UsenetDecodedBodyBatch? uncachedBatch,
        CancellationToken cancellationToken)
    {
        var responses = new Task<UsenetDecodedBodyResponse>[responseCount];
        foreach (var cached in partition.Cached)
        {
            responses[cached.Index] = Task.FromResult(
                ReadCachedBodyAsync(cached.Key, cached.Entry.YencHeaders));
        }

        if (uncachedBatch != null)
        {
            if (uncachedBatch.Responses.Count != partition.Missing.Count)
            {
                throw new InvalidOperationException(
                    "The NNTP batch response count did not match the request count.");
            }

            for (var index = 0; index < partition.Missing.Count; index++)
            {
                var missing = partition.Missing[index];
                responses[missing.Index] = CacheBatchResponseAsync(
                    missing.SegmentId,
                    uncachedBatch.Responses[index],
                    cancellationToken);
            }
        }

        Task previousCompletion = Task.CompletedTask;
        for (var index = 0; index < responses.Length; index++)
        {
            responses[index] = CompleteInOrderAsync(
                responses[index], previousCompletion);
            previousCompletion = responses[index];
        }

        return new UsenetDecodedBodyBatch { Responses = responses };
    }

    private static async Task<UsenetDecodedBodyResponse> CompleteInOrderAsync(
        Task<UsenetDecodedBodyResponse> response,
        Task previousCompletion)
    {
        try
        {
            await previousCompletion.ConfigureAwait(false);
        }
        catch
        {
            // Previous response failures surface on their own task; only ordering matters here.
        }

        return await response.ConfigureAwait(false);
    }

    private BatchCachePartition PartitionBatch(IReadOnlyList<SegmentId> segmentIds)
    {
        var cached = new List<CachedBatchItem>(segmentIds.Count);
        var missing = new List<MissingBatchItem>();
        for (var index = 0; index < segmentIds.Count; index++)
        {
            var key = segmentIds[index].ToString();
            if (_cachedSegments.TryGetValue(key, out var cacheEntry))
            {
                cached.Add(new CachedBatchItem(index, key, cacheEntry));
            }
            else
            {
                missing.Add(new MissingBatchItem(index, segmentIds[index]));
            }
        }

        return new BatchCachePartition(cached, missing);
    }

    private static void InvokeCompletionCallback(
        ArticleBodyCompletionHandler? callback,
        ArticleBodyResult result,
        string? failureReason = null)
    {
        try
        {
            callback?.Invoke(result, failureReason);
        }
        catch
        {
            // Callback exceptions must not fault transport tasks (streaming lifecycle invariant).
        }
    }

    private async Task<UsenetDecodedBodyResponse> CacheBatchResponseAsync(
        SegmentId segmentId,
        Task<UsenetDecodedBodyResponse> responseTask,
        CancellationToken cancellationToken)
    {
        var key = segmentId.ToString();
        var semaphore = _pendingRequests.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            if (_cachedSegments.TryGetValue(key, out var existingEntry))
            {
                if (response.Stream != null)
                {
                    await response.Stream.DisposeAsync().ConfigureAwait(false);
                }

                return ReadCachedBodyAsync(key, existingEntry.YencHeaders);
            }

            if (response.Stream == null)
            {
                return response;
            }

            await using var stream = response.Stream;
            var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    $"Failed to read yenc headers for segment {key}");
            await CacheDecodedStreamAsync(key, stream, cancellationToken).ConfigureAwait(false);
            AddCacheEntry(key, new CacheEntry(
                YencHeaders: yencHeaders,
                HasArticleHeaders: false,
                ArticleHeaders: null));
            return ReadCachedBodyAsync(key, yencHeaders);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static LongRange GetByteRange(UsenetYencHeader headers)
    {
        return LongRange.FromStartAndSize(headers.PartOffset, headers.PartSize);
    }

    private async Task CacheDecodedStreamAsync(string segmentId, YencStream stream, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(segmentId);
        await using var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private UsenetDecodedBodyResponse ReadCachedBodyAsync(string segmentId, UsenetYencHeader yencHeaders)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedBodyResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 - Article retrieved from file cache",
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private UsenetDecodedArticleResponse ReadCachedArticleAsync(
        string segmentId, UsenetYencHeader yencHeaders, UsenetArticleHeader articleHeaders)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedArticleResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "220 - Article retrieved from cache",
            ArticleHeaders = articleHeaders,
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private string GetCachePath(string segmentId)
    {
        // Use SHA256 hash of segment ID to create a valid filename
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var filename = Convert.ToHexString(hash);
        return Path.Join(_cacheDir, filename);
    }

    public override void Dispose()
    {
        // Dispose the underlying client
        // only when leaveOpen is false.
        if (!leaveOpen)
            base.Dispose();

        // Clean up semaphores
        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();

        _pendingRequests.Clear();
        _cachedSegments.Clear();
        _trackedSegments.Clear();

        Task.Run(async () => await DeleteCacheDir(_cacheDir).ConfigureAwait(false));
        GC.SuppressFinalize(this);
    }

    // Initial retry delay; shortened in tests so capped-failure coverage does not wait seconds.
    internal static int DeleteCacheDirInitialDelayMs { get; set; } = 1000;

    internal static async Task DeleteCacheDir(string cacheDir)
    {
        var ct = SigtermUtil.GetCancellationToken();
        var delay = DeleteCacheDirInitialDelayMs;
        const int maxAttempts = 10; // ~1m40s worst case with the existing backoff ladder
        for (var attempt = 1; attempt <= maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return; // already gone — success
            }
            catch (IOException e)
            {
                if (attempt == maxAttempts)
                {
                    Log.Warning(e,
                        "Giving up deleting article cache dir {CacheDir} after {Attempts} attempts",
                        cacheDir, attempt);
                    return;
                }

                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                delay = Math.Min(delay * 2, 10000);
            }
        }
    }
}
