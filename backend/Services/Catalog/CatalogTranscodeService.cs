using System.Diagnostics;
using System.Globalization;
using System.Net;
using NzbWebDAV.Database;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Compatibility-mode streaming: resolves the session's signed <c>/view</c> location once,
/// probes its duration once, then serves an on-demand six-second M3U8 segment cache. Every
/// source URI is rebuilt on the loopback backend, so a redirect target can never turn this
/// endpoint into an arbitrary URL fetcher.
/// </summary>
public sealed class CatalogTranscodeService
{
    internal const string PlaybackClientName = "catalog-playback";

    internal const string ExpiredSessionMessage = "Playback session expired. Start playback again.";
    internal const string NotPlayableMessage = "The selected video does not report a playable duration.";
    internal const string StillPreparingMessage = "The stream is still being prepared.";
    internal const string SegmentNotFoundMessage = "Segment not found.";
    internal const string SegmentFailedMessage = "The compatibility stream could not be prepared.";
    internal const string RedirectRejectedMessage = "The playback backend returned an unexpected redirect.";

    private static readonly TimeSpan ResolverTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResolvedSourceTtl = TimeSpan.FromHours(1);

    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly CatalogPlaybackSessionStore _sessionStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CatalogTranscodeOptions _options;
    private readonly MetadataMemo _memo = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid SessionId, int Index), SegmentEntry> _segments = [];
    private readonly Dictionary<Guid, SessionRun> _runs = [];
    private long _cacheBytes;

    /// <summary>
    /// Segments an active run keeps producing ahead of the requested one. Playback consumes
    /// them in order, so this bounds both encoder work and the mount's read-ahead.
    /// </summary>
    private const int LookaheadSegments = 8;

    private sealed class SessionRun
    {
        public required CatalogTranscodeRun Run { get; init; }
        public required int FirstIndex { get; init; }
        public required int LastIndex { get; init; }
    }

    public CatalogTranscodeService(
        IFFmpegRunner ffmpegRunner,
        CatalogPlaybackSessionStore sessionStore,
        IHttpClientFactory httpClientFactory,
        CatalogTranscodeOptions options)
    {
        _ffmpegRunner = ffmpegRunner;
        _sessionStore = sessionStore;
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    /// <summary>Root of the segment cache; also the cleanup sweep's scan root.</summary>
    internal static string CacheRoot => Path.Join(DavDatabaseContext.ConfigPath, "transcode-cache");

    /// <summary>Completed segment bytes currently counted against the cache bound.</summary>
    internal long CachedBytes
    {
        get
        {
            lock (_gate) return _cacheBytes;
        }
    }

    public async Task<string> GetPlaylistAsync(Guid sessionId, CancellationToken ct)
    {
        var session = RequireSession(sessionId);
        var duration = await GetDurationAsync(session, ct).ConfigureAwait(false);
        return CatalogHlsManifest.Build(duration);
    }

    public async Task<CatalogSegmentLease> GetSegmentAsync(Guid sessionId, int index, CancellationToken ct)
    {
        if (index < 0)
            throw CatalogException.Validation("Segment index must be zero or greater.");

        var session = RequireSession(sessionId);
        var duration = await GetDurationAsync(session, ct).ConfigureAwait(false);
        if (index >= CatalogHlsManifest.SegmentCount(duration))
            throw CatalogException.NotFound(SegmentNotFoundMessage);
        _ = await ResolveSourceAsync(session, ct).ConfigureAwait(false);

        var key = (session.Id, index);
        var path = await GetOrProduceAsync(session, index, ct).ConfigureAwait(false);
        return AcquireLease(key, path);
    }

    /// <summary>
    /// Removes expired sessions, idle or orphaned segments, and leftover temporary files.
    /// Exposed for the periodic hosted sweep and for tests with an explicit clock.
    /// </summary>
    public void Prune(DateTimeOffset now)
    {
        _sessionStore.Prune(now);

        List<string> directories;
        List<CatalogTranscodeRun> stopped = [];
        lock (_gate)
        {
            foreach (var (sessionId, sessionRun) in _runs)
            {
                if (_sessionStore.Contains(sessionId)) continue;
                stopped.Add(sessionRun.Run);
            }

            foreach (var run in stopped)
            {
                var key = _runs.First(pair => ReferenceEquals(pair.Value.Run, run)).Key;
                _runs.Remove(key);
            }

            var expired = new List<(Guid SessionId, int Index)>();
            foreach (var (key, entry) in _segments)
            {
                // Never touch a segment a response is reading or an encode is writing.
                if (entry.ActiveReaders > 0) continue;
                if (_runs.TryGetValue(key.SessionId, out var active)
                    && key.Index >= active.FirstIndex && key.Index <= active.LastIndex
                    && entry.ProducedPath is null)
                {
                    continue;
                }

                var idle = entry.LastAccess + CatalogTranscodeOptions.SegmentIdleExpiry <= now;
                var orphaned = !_sessionStore.Contains(key.SessionId);
                if (!idle && !orphaned) continue;
                expired.Add(key);
            }

            foreach (var key in expired)
            {
                var entry = _segments[key];
                _segments.Remove(key);
                if (!entry.Transient) _cacheBytes -= entry.Size;
                TryDelete(entry.CachePath);
            }

            directories = CollectDeletableDirectories();
        }

        foreach (var run in stopped)
            _ = DisposeRunAsync(run);

        foreach (var directory in directories)
            TryDeleteDirectory(directory);
    }

    private CatalogPlaybackSession RequireSession(Guid sessionId)
        => _sessionStore.TryGet(sessionId, out var session)
            ? session
            : throw new CatalogException(CatalogException.TitleNotFound, ExpiredSessionMessage);

    private async Task<TimeSpan> GetDurationAsync(CatalogPlaybackSession session, CancellationToken ct)
    {
        if (session.MediaDuration is { } cached) return cached;

        var key = string.Create(CultureInfo.InvariantCulture, $"probe:{session.Id}");
        var duration = await _memo.GetOrCreateAsync(key, ResolvedSourceTtl, async token =>
        {
            var sourceUri = await ResolveSourceAsync(session, token).ConfigureAwait(false);
            var probed = await _ffmpegRunner.ProbeDurationAsync(sourceUri, token).ConfigureAwait(false);
            if (probed is null || probed <= TimeSpan.Zero)
                throw new CatalogException(CatalogException.Unprocessable, NotPlayableMessage);
            return probed.Value;
        }, ct).ConfigureAwait(false);

        session.MediaDuration = duration;
        return duration;
    }

    private async Task<string> ResolveSourceAsync(CatalogPlaybackSession session, CancellationToken ct)
    {
        if (session.ResolvedViewUri is { } cached) return cached;

        var key = string.Create(CultureInfo.InvariantCulture, $"source:{session.Id}");
        var resolved = await _memo.GetOrCreateAsync(key, ResolvedSourceTtl, token => ResolveAsync(session, token), ct)
            .ConfigureAwait(false);

        session.ResolvedViewUri = resolved;
        return resolved;
    }

    /// <summary>
    /// Reads only the response head of the internal play URL: the play controller answers
    /// with a redirect to signed <c>/view</c> bytes, or 503 while it is still preparing.
    /// </summary>
    private async Task<string> ResolveAsync(CatalogPlaybackSession session, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(PlaybackClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, session.InternalDirectUrl);
        using var timeout = new CancellationTokenSource(ResolverTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The resolver budget elapsed while the backend was still preparing the release.
            throw new CatalogException(CatalogException.NotConfigured, StillPreparingMessage, DefaultRetryAfter);
        }
        catch (HttpRequestException)
        {
            throw CatalogException.Provider("The playback backend is unavailable.", DefaultRetryAfter);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw new CatalogException(
                    CatalogException.NotConfigured,
                    StillPreparingMessage,
                    ReadRetryAfter(response) ?? DefaultRetryAfter);
            }

            if (!IsRedirect(response.StatusCode))
                throw CatalogException.Provider("The playback backend returned an unexpected response.");

            return BuildViewUri(session, response.Headers.Location);
        }
    }

    /// <summary>
    /// Accepts only a rooted relative redirect to <c>/view/…</c> (optionally behind the public
    /// path prefix) and rebuilds it on the loopback backend. Absolute, network-path
    /// (<c>//host</c>), traversal, and non-<c>/view</c> targets are rejected.
    /// </summary>
    internal static string BuildViewUri(CatalogPlaybackSession session, Uri? location)
    {
        if (location is null || location.IsAbsoluteUri)
            throw CatalogException.Provider(RedirectRejectedMessage);

        var pathAndQuery = location.OriginalString;
        if (!pathAndQuery.StartsWith('/') || pathAndQuery.StartsWith("//", StringComparison.Ordinal))
            throw CatalogException.Provider(RedirectRejectedMessage);
        if (pathAndQuery.Contains("..", StringComparison.Ordinal))
            throw CatalogException.Provider(RedirectRejectedMessage);

        var prefix = session.PublicPathPrefix;
        if (prefix.Length > 0 && pathAndQuery.StartsWith(prefix, StringComparison.Ordinal))
            pathAndQuery = pathAndQuery[prefix.Length..];

        if (!pathAndQuery.StartsWith("/view/", StringComparison.Ordinal))
            throw CatalogException.Provider(RedirectRejectedMessage);

        var loopback = CatalogBackendUrl.GetLoopbackBase(EnvironmentUtil.GetEnvironmentVariable("BACKEND_URL"));
        return CatalogBackendUrl.Combine(loopback, pathAndQuery);
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.Found
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }

        return null;
    }

    private async Task<string> GetOrProduceAsync(
        CatalogPlaybackSession session,
        int index,
        CancellationToken ct)
    {
        var key = (session.Id, index);
        CatalogTranscodeRun run;
        int runRelativeIndex;
        lock (_gate)
        {
            if (!_segments.TryGetValue(key, out var entry))
            {
                entry = new SegmentEntry { CachePath = CachePathFor(session.Id, index) };
                _segments[key] = entry;
            }

            entry.LastAccess = _options.TimeProvider.GetUtcNow();
            if (entry.ProducedPath is { } produced && File.Exists(produced))
                return produced;

            // Pin the segment being awaited: a run publishes later segments eagerly, and the
            // eviction pass must never evict the very segment this caller is waiting for.
            entry.PendingRequests++;

            // A seek moves the session to a new run; the old one is disposed outside the lock.
            var current = _runs.GetValueOrDefault(session.Id);
            if (current is null || index < current.FirstIndex || index > current.LastIndex)
            {
                var duration = session.MediaDuration
                    ?? throw CatalogException.Provider(SegmentFailedMessage);
                var total = CatalogHlsManifest.SegmentCount(duration);
                var count = Math.Min(LookaheadSegments, total - index);
                if (count <= 0) throw CatalogException.NotFound(SegmentNotFoundMessage);

                var directory = Path.GetDirectoryName(entry.CachePath)!;
                Directory.CreateDirectory(directory);
                var runDirectory = Path.Join(directory, $"run-{Guid.NewGuid():N}");
                Directory.CreateDirectory(runDirectory);

                var sourceUri = session.ResolvedViewUri
                    ?? throw CatalogException.Provider(SegmentFailedMessage);
                var started = CatalogTranscodeRun.StartNew(
                    _ffmpegRunner,
                    sourceUri,
                    CatalogHlsManifest.SegmentStart(index),
                    count,
                    runDirectory,
                    relative => PublishSegment(session.Id, index + relative,
                        Path.Join(runDirectory, $"{relative}.ts")));
                _runs[session.Id] = new SessionRun
                {
                    Run = started,
                    FirstIndex = index,
                    LastIndex = index + count - 1,
                };
                if (current is not null) _ = DisposeRunAsync(current.Run);
                current = _runs[session.Id];
            }

            run = current.Run;
            runRelativeIndex = index - current.FirstIndex;
        }

        try
        {
            return await run.WaitForSegmentAsync(runRelativeIndex, ct).ConfigureAwait(false);
        }
        catch (CatalogException)
        {
            // A failed run is forgotten so the player's next request starts a fresh encode
            // instead of replaying the same failure forever.
            lock (_gate)
            {
                if (_runs.TryGetValue(session.Id, out var current) && ReferenceEquals(current.Run, run))
                    _runs.Remove(session.Id);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (_segments.TryGetValue(key, out var entry) && entry.PendingRequests > 0)
                    entry.PendingRequests--;
            }
        }
    }

    /// <summary>
    /// Moves a finished run segment into the session cache and applies the storage bound.
    /// Called by the encoder as each segment is completed, so playback can start while the
    /// rest of the run is still being produced.
    /// </summary>
    private string PublishSegment(Guid sessionId, int index, string producedPath)
    {
        var key = (sessionId, index);
        var cachePath = CachePathFor(sessionId, index);
        try
        {
            File.Move(producedPath, cachePath, overwrite: true);
        }
        catch (IOException)
        {
            return producedPath;
        }

        lock (_gate)
        {
            if (!_segments.TryGetValue(key, out var entry))
            {
                entry = new SegmentEntry { CachePath = cachePath };
                _segments[key] = entry;
            }

            entry.LastAccess = _options.TimeProvider.GetUtcNow();
            var size = new FileInfo(cachePath).Length;
            if (size <= 0) throw CatalogException.Provider(SegmentFailedMessage);
            RecordProduced(entry, size);
            return cachePath;
        }
    }

    private static async Task DisposeRunAsync(CatalogTranscodeRun run)
    {
        await run.DisposeAsync().ConfigureAwait(false);
    }

    private void RecordProduced(SegmentEntry entry, long size)
    {
        var now = _options.TimeProvider.GetUtcNow();
        lock (_gate)
        {
            entry.Size = size;
            entry.ProducedPath = entry.CachePath;
            entry.LastAccess = now;
            _cacheBytes += size;

            EvictOverBoundLocked(producing: entry);

            // Nothing closed was evictable: serve this segment transiently (deleted once the
            // response finishes) instead of letting the cache exceed its bound.
            if (_cacheBytes > _options.MaxCacheBytes)
            {
                entry.Transient = true;
                _cacheBytes -= size;
            }
        }
    }

    private void EvictOverBoundLocked(SegmentEntry? producing = null)
    {
        while (_cacheBytes > _options.MaxCacheBytes)
        {
            (Guid SessionId, int Index)? victim = null;
            var oldest = DateTimeOffset.MaxValue;
            foreach (var (key, entry) in _segments)
            {
                // Never evict the segment just produced: its own publication must not delete it.
                if (ReferenceEquals(entry, producing)) continue;
                if (entry.ActiveReaders > 0 || entry.PendingRequests > 0 || entry.Transient || entry.Size <= 0) continue;
                if (entry.LastAccess >= oldest) continue;
                oldest = entry.LastAccess;
                victim = key;
            }

            if (victim is not { } evict) return;
            var entryToEvict = _segments[evict];
            _cacheBytes -= entryToEvict.Size;
            entryToEvict.Size = 0;
            entryToEvict.ProducedPath = null;
            _segments.Remove(evict);
            TryDelete(entryToEvict.CachePath);
        }
    }

    private CatalogSegmentLease AcquireLease((Guid SessionId, int Index) key, string path)
    {
        lock (_gate)
        {
            if (_segments.TryGetValue(key, out var entry) && entry.ProducedPath == path)
            {
                entry.ActiveReaders++;
                entry.LastAccess = _options.TimeProvider.GetUtcNow();
                return new CatalogSegmentLease(path, () => ReleaseLease(key, entry));
            }
        }

        // The entry was evicted between production and acquisition: stream, then delete.
        return new CatalogSegmentLease(path, () => TryDelete(path));
    }

    private void ReleaseLease((Guid SessionId, int Index) key, SegmentEntry entry)
    {
        lock (_gate)
        {
            if (entry.ActiveReaders > 0) entry.ActiveReaders--;
            entry.LastAccess = _options.TimeProvider.GetUtcNow();
            if (!entry.Transient || entry.ActiveReaders > 0) return;

            TryDelete(entry.CachePath);
            entry.Size = 0;
            entry.ProducedPath = null;
            if (_segments.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _segments.Remove(key);
        }
    }

    private List<string> CollectDeletableDirectories()
    {
        var directories = new List<string>();
        if (!Directory.Exists(CacheRoot)) return directories;

        var now = _options.TimeProvider.GetUtcNow();
        foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
        {
            var name = Path.GetFileName(directory);
            var sessionAlive = Guid.TryParseExact(name, "N", out var sessionId)
                               && _sessionStore.Contains(sessionId);
            if (!sessionAlive)
            {
                directories.Add(directory);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp"))
            {
                TryDeleteIfOlderThan(file, now);
            }
        }

        return directories;
    }

    private void TryDeleteIfOlderThan(string path, DateTimeOffset now)
    {
        try
        {
            if (now - File.GetLastWriteTimeUtc(path) >= CatalogTranscodeOptions.StaleTemporaryFileAge)
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Catalog transcode cleanup could not remove a stale temporary file.");
        }
    }

    private static string CachePathFor(Guid sessionId, int index)
        => Path.Join(
            CacheRoot,
            sessionId.ToString("N"),
            string.Create(CultureInfo.InvariantCulture, $"{index}.ts"));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Catalog transcode cleanup could not remove a segment file.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Catalog transcode cleanup could not remove an expired session directory.");
        }
    }

    private sealed class SegmentEntry
    {
        public required string CachePath { get; init; }
        public string? ProducedPath { get; set; }
        public long Size { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public int ActiveReaders { get; set; }

        /// <summary>Callers currently waiting for this segment; pinned against eviction.</summary>
        public int PendingRequests { get; set; }

        /// <summary>Produced while the cache was full of active readers; deleted after streaming.</summary>
        public bool Transient { get; set; }
    }
}
