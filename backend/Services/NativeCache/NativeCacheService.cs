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
    private readonly SemaphoreSlim? _bufferSlots;
    private readonly int _capacity;
    private bool _disposed;

    public NativeCacheService(ConfigManager config, IBlobStore blobs, RepairPatchStore repairs)
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
            Store = new NativeCacheStore(Path.Combine(ActiveSettings.MetadataPath, "catalogue.db"), ActiveSettings.Folders);
            _capacity = Math.Max(1, ActiveSettings.BufferMb / 4);
            _bufferSlots = new SemaphoreSlim(_capacity, _capacity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            InitializationError = "Native cache could not initialize. Check folder permissions and the local metadata path.";
            Log.Warning("Native cache initialization failed ({ErrorType}); source streaming remains available", exception.GetType().Name);
        }
    }

    public CacheMode ActiveMode { get; }
    public NativeCacheSettings? ActiveSettings { get; }
    public string? InitializationError { get; }
    public NativeCacheStore? Store { get; }
    public long ReservedBufferBytes => _bufferSlots is null ? 0 : (_capacity - _bufferSlots.CurrentCount) * (long)NativeCacheStore.BlockSize;

    public bool RequiresRestart(ConfigManager config) => ActiveMode != config.GetCacheMode()
        || (ActiveMode == CacheMode.Native && System.Text.Json.JsonSerializer.Serialize(ActiveSettings)
            != System.Text.Json.JsonSerializer.Serialize(NativeCacheSettings.FromConfig(config)));

    public async Task<Stream> WrapAsync(DavItem item, Func<CancellationToken, Task<Stream>> open, CancellationToken cancellationToken, bool requireNative = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Store is null || _bufferSlots is null || item.FileBlobId is not { } blobId || item.FileSize is not > 0
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
                    var stream = new NativeCachedStream(Store, identity, open,
                        () => watch.IsCurrent && repairRevision.IsCurrent, admission);
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
        if (_disposed) return;
        _disposed = true;
        if (Store is not null) await Store.DisposeAsync().ConfigureAwait(false);
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
