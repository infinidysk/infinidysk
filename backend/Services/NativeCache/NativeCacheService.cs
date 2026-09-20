using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;

namespace NzbWebDAV.Services.NativeCache;

public sealed class NativeCacheService : IAsyncDisposable
{
    private readonly IBlobStore _blobs;
    private readonly RepairPatchStore _repairs;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Managed-only semaphore: detached filesystem operations retain admissions after shutdown and must still release them. AvailableWaitHandle is never used.")]
    private readonly SemaphoreSlim? _bufferSlots;
    private readonly int _capacity;
    private readonly object _lifetimeGate = new();
    private Task _initialization = Task.CompletedTask;
    private Task _cleanup = Task.CompletedTask;
    private NativeCacheStore? _store;
    private string? _initializationError;
    private bool _disposed;
    internal TimeSpan InitializationWait { get; set; } = TimeSpan.FromSeconds(1);
    internal Task InitializationCompletion => Task.WhenAll(_initialization, _cleanup);

    public NativeCacheService(ConfigManager config, IBlobStore blobs, RepairPatchStore repairs)
        : this(config, blobs, repairs, settings => new NativeCacheStore(Path.Combine(settings.MetadataPath, "catalogue.db"), settings.Folders)) { }

    internal NativeCacheService(ConfigManager config, IBlobStore blobs, RepairPatchStore repairs,
        Func<NativeCacheSettings, NativeCacheStore> storeFactory)
    {
        _blobs = blobs;
        _repairs = repairs;
        ActiveMode = config.GetActiveCacheMode();
        if (ActiveMode != CacheMode.Native) return;
        try
        {
            ActiveSettings = NativeCacheSettings.FromConfig(config);
            if (!ActiveSettings.Folders.Any(folder => folder.Enabled))
                throw new ArgumentException("Configure at least one enabled native cache folder.");
            _capacity = Math.Max(1, ActiveSettings.BufferMb / 4);
            _bufferSlots = new SemaphoreSlim(_capacity, _capacity);
            _initialization = Task.Run(() => InitializeStoreAsync(ActiveSettings, storeFactory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            _initializationError = "Native cache could not initialize. Check folder permissions and the local metadata path.";
            Log.Warning("Native cache initialization failed ({ErrorType}); source streaming remains available", exception.GetType().Name);
        }
    }

    public CacheMode ActiveMode { get; }
    public NativeCacheStatistics Statistics { get; } = new();
    public NativeCacheSettings? ActiveSettings { get; }
    public string? InitializationError => Volatile.Read(ref _initializationError);
    public bool InitializationPending => !_initialization.IsCompleted;
    public NativeCacheStore? Store => Volatile.Read(ref _store);
    public long ReservedBufferBytes => _bufferSlots is null ? 0 : (_capacity - _bufferSlots.CurrentCount) * (long)NativeCacheStore.BlockSize;

    private async Task InitializeStoreAsync(NativeCacheSettings settings, Func<NativeCacheSettings, NativeCacheStore> factory)
    {
        try
        {
            var store = factory(settings);
            lock (_lifetimeGate)
            {
                if (!_disposed) { Volatile.Write(ref _store, store); return; }
            }
            await store.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _initializationError, "Native cache could not initialize. Check folder permissions and the local metadata path.");
            Log.Warning("Native cache initialization failed ({ErrorType}); source streaming remains available", exception.GetType().Name);
        }
    }

    public async Task<bool> WaitForInitializationAsync(CancellationToken cancellationToken = default)
    {
        try { await _initialization.WaitAsync(InitializationWait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return Store is not null;
    }

    public bool RequiresRestart(ConfigManager config)
    {
        try
        {
            return ActiveMode != config.GetCacheMode()
                || (ActiveMode == CacheMode.Native && System.Text.Json.JsonSerializer.Serialize(ActiveSettings)
                    != System.Text.Json.JsonSerializer.Serialize(NativeCacheSettings.FromConfig(config)));
        }
        catch (ArgumentException) { return true; } // Keep initialization diagnostics available for malformed optional settings.
    }

    public async Task<Stream> WrapAsync(DavItem item, Func<CancellationToken, Task<Stream>> open, CancellationToken cancellationToken, bool requireNative = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (InitializationPending) await WaitForInitializationAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var store = Store;
        if (store is null || _bufferSlots is null || item.FileBlobId is not { } blobId || item.FileSize is not > 0
            || !_bufferSlots.Wait(0))
        {
            if (requireNative) throw new InvalidOperationException("Native cache admission is unavailable; no source bytes were requested.");
            return await open(cancellationToken).ConfigureAwait(false);
        }

        var watch = ContentRevisionTracker.Watch(blobId);
        AdmissionLease? admission = new(_bufferSlots, watch);
        try
        {
            await using var blob = _blobs.ReadBlob(blobId);
            if (blob is not null && watch.IsCurrent)
            {
                var hash = await SHA256.HashDataAsync(blob, cancellationToken).ConfigureAwait(false);
                var dependencies = await GetSourceSegmentsAsync(item, blobId).ConfigureAwait(false);
                if (dependencies is not null && watch.IsCurrent)
                {
                    var repairRevision = _repairs.CaptureNativeRevisions(dependencies);
                    var identity = new NativeCacheIdentity(item.Id.ToString("N"),
                        $"v2:{blobId:N}:{Convert.ToHexString(hash)}:{repairRevision.Fingerprint}", item.FileSize.Value);
                    var stream = new NativeCachedStream(store, identity, open,
                        () => watch.IsCurrent && repairRevision.IsCurrent, admission, background: requireNative, statistics: Statistics);
                    admission = null; // The returned stream owns the watch and buffer admission.
                    return stream;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException
            or NzbWebDAV.Exceptions.CorruptedBlobPayloadException)
        {
            // Only cache metadata failures fall back. A failure opening the actual
            // source below must not be mistaken for a cache failure and retried.
        }
        finally { admission?.Dispose(); }
        if (requireNative) throw new InvalidOperationException("Native cache metadata is unavailable; no source bytes were requested.");
        return await open(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<string>?> GetSourceSegmentsAsync(DavItem item, Guid blobId)
    {
        static IEnumerable<string> Segments(string[] ids, string[][]? fallbacks) => ids.Concat(
            fallbacks?.Where(row => row is not null).SelectMany(row => row) ?? []);
        switch (item.SubType)
        {
            case DavItem.ItemSubType.NzbFile:
                var nzb = await _blobs.ReadBlob<DavNzbFile>(blobId).ConfigureAwait(false);
                return nzb?.SegmentIds is { } ids ? Segments(ids, nzb.SegmentFallbackIds) : null;
            case DavItem.ItemSubType.RarFile:
                var rar = await _blobs.ReadBlob<DavRarFile>(blobId).ConfigureAwait(false);
                return rar?.RarParts?.SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds));
            case DavItem.ItemSubType.MultipartFile:
                var multipart = await _blobs.ReadBlob<DavMultipartFile>(blobId).ConfigureAwait(false);
                if (multipart?.Metadata is not { } metadata) return null;
                return (metadata.FileParts ?? []).SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds))
                    .Concat((metadata.PendingParts ?? []).SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds)));
            default: return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            var store = _store;
            Volatile.Write(ref _store, null);
            if (store is not null) _cleanup = Task.Run(async () =>
            {
                try { await store.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Log.Warning("Native cache shutdown failed ({ErrorType})", exception.GetType().Name); }
            });
        }
        try { await _cleanup.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { /* A stuck NAS must not hold host shutdown; cleanup remains owned. */ }
        // Active response leases can finish during host shutdown. SemaphoreSlim has
        // no native handle here; let remaining leases release it before collection.
    }

    private sealed class AdmissionLease(SemaphoreSlim slots, ContentRevisionTracker.RevisionWatch watch) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            watch.Dispose();
            slots.Release();
        }
    }
}
