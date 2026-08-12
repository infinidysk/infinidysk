using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

// Nested RAR expansion still passes this stream to SharpCompress's synchronous
// header reader. FastReadOnlyStream centralizes that compatibility fallback while
// WebDAV GET/range handlers use the Memory<byte> async path below.
public class DavMultipartFileStream : FastReadOnlyStream
{
    private readonly DavMultipartFile _mpf;
    private readonly INntpClient _usenetClient;
    private readonly int _articleBufferSize;
    private readonly LazyRarResolver? _resolver;
    private readonly bool _usePipelinedBodyRequests;
    private readonly string? _fileName;
    private readonly InFlightArticleBudget? _inFlightArticleBudget;
    private readonly long _length;

    private long _position;
    private CombinedStream? _innerStream;
    private bool _disposed;
    // Teardown of the inner stream a Seek replaced is started non-blocking (Seek is
    // synchronous); the next ReadAsync joins it before opening a new inner stream so
    // rapid scrubbing cannot overlap generations and pin the article budget.
    private Task? _pendingInnerDispose;

    public DavMultipartFileStream(
        DavMultipartFile mpf,
        INntpClient usenetClient,
        int articleBufferSize,
        LazyRarResolver? resolver,
        bool usePipelinedBodyRequests,
        string? fileName = null,
        InFlightArticleBudget? inFlightArticleBudget = null)
    {
        _mpf = mpf;
        _usenetClient = usenetClient;
        _articleBufferSize = articleBufferSize;
        _resolver = resolver;
        _usePipelinedBodyRequests = usePipelinedBodyRequests;
        _fileName = fileName;
        _inFlightArticleBudget = inFlightArticleBudget;
        _length = ComputeLength(mpf.Metadata);

        if (_resolver != null
            && _mpf.Metadata.IsLazy
            && (_mpf.Metadata.PendingParts?.Length ?? 0) > 0)
        {
            // Fire-and-forget: resolve every trailing volume's header in the
            // BACKGROUND so reads never block on it. The first volume is already
            // resolved at import, so byte 0 streams immediately while the rest
            // fill in behind the player at Low priority (CancellationToken.None
            // carries no High-priority context, so these fetches always yield to
            // live playback). A seek that outruns this pass is covered on demand
            // by EnsureCoveringAsync — the resolver coalesces the two by segment
            // id so a volume is never fetched twice, and persists the result so
            // the next open of this file resolves nothing at all.
            _ = PreWarmAsync();
        }
    }

    // Background resolution of every trailing volume. Self-observing: a missing
    // or unreachable trailing volume must neither surface as an unobserved task
    // fault nor break playback — byte 0 and every volume up to the failure still
    // stream fine. If the player actually reaches the bad volume, the on-demand
    // read path raises the error there, in context.
    private async Task PreWarmAsync()
    {
        try
        {
            await _resolver!
                .EnsureResolvedThroughAsync(_mpf, long.MaxValue, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e,
                "Background RAR pre-warm for {Id} did not finish; trailing volumes will resolve on demand.",
                _mpf.Id);
        }
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_pendingInnerDispose is { } pendingDispose)
        {
            _pendingInnerDispose = null;
            try { await pendingDispose.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Teardown-only.
            }
        }
        _innerStream ??= await GetFileStreamAsync(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0 && _position < _length)
        {
            throw new IncompleteFileContentException(
                _fileName ?? "unknown", _length, _position);
        }

        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long absoluteOffset;
        try
        {
            absoluteOffset = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (absoluteOffset < 0 || absoluteOffset > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        if (_innerStream is { } replaced)
        {
            _pendingInnerDispose = replaced.DisposeAsync().AsTask();
            _innerStream = null;
        }
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    // Walks resolved FileParts + pending estimates so HEAD/Length-aware
    // clients see the stable inner-file size from the moment of mount. The
    // estimates are adjusted at import time so this matches the real
    // uncompressed size byte-exact.
    // Old MemoryPack blobs predate the lazy fields, so PendingParts can be
    // null after deserialization despite the property initializer. Guard
    // every iteration with ?? [] to stay safe.
    private static long ComputeLength(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        foreach (var p in meta.PendingParts ?? []) sum += p.EstimatedDataSize;
        return sum;
    }

    private (int filePartIndex, long filePartOffset) SeekFilePart(
        DavMultipartFile.Meta meta,
        long byteOffset)
    {
        long offset = 0;
        var fileParts = meta.FileParts ?? [];
        for (var i = 0; i < fileParts.Length; i++)
        {
            var filePart = fileParts[i];
            var nextOffset = offset + filePart.FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return (i, offset);
            offset = nextOffset;
        }

        throw new SeekPositionNotFoundException($"Corrupt file. Cannot seek to byte position {byteOffset}.");
    }

    private async Task<CombinedStream> GetFileStreamAsync(long rangeStart, CancellationToken ct)
    {
        // Resolve only enough trailing volumes to cover the requested offset —
        // no waiting on the background pre-warm. For byte 0 that's nothing (the
        // first volume is resolved at import), so playback starts immediately.
        // A seek into a not-yet-resolved volume resolves the gap up to it here,
        // sharing in-flight work with the pre-warm via the resolver, so the
        // player only ever waits for volumes up to where it actually jumped —
        // never the whole archive.
        var meta = await EnsureCoveringAsync(rangeStart, ct).ConfigureAwait(false);

        if (rangeStart == 0)
            return new CombinedStream(EnumerateFromPart(0, 0, ct));

        var (filePartIndex, filePartOffset) = SeekFilePart(meta, rangeStart);
        return new CombinedStream(EnumerateFromPart(filePartIndex, rangeStart - filePartOffset, ct));
    }

    // Resolve trailing volumes up to (and including) the one that contains
    // `byteOffset` so SeekFilePart can map the offset to an exact slot.
    // No-op for non-lazy archives.
    private async Task<DavMultipartFile.Meta> EnsureCoveringAsync(long byteOffset, CancellationToken ct)
    {
        if (_resolver is null || !_mpf.Metadata.IsLazy) return _mpf.Metadata;
        return await _resolver.EnsureResolvedThroughAsync(_mpf, byteOffset, ct).ConfigureAwait(false);
    }

    // Lazy iterator over the file's volume sequence. Each yielded Task opens
    // one volume's segment range. When we run out of resolved FileParts but
    // PendingParts remain, the next yield triggers lazy resolution before
    // opening — so the player keeps streaming across volume boundaries
    // without having paid for them at mount time.
    private IEnumerable<Task<Stream>> EnumerateFromPart(int firstFilePartIndex, long firstOffset, CancellationToken ct)
    {
        var i = firstFilePartIndex;
        while (true)
        {
            var meta = _mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            if (i < fileParts.Length)
            {
                var part = fileParts[i];
                var extraOffset = (i == firstFilePartIndex) ? firstOffset : 0;
                yield return Task.FromResult<System.IO.Stream>(OpenPart(part, extraOffset, i));
                i++;
                continue;
            }

            if (_resolver != null && meta.IsLazy && (meta.PendingParts?.Length ?? 0) > 0)
            {
                yield return ResolveAndOpenAsync(i, ct);
                i++;
                continue;
            }

            yield break;
        }
    }

    private PaddedLengthStream OpenPart(DavMultipartFile.FilePart part, long extraOffset, int partIndex)
    {
        if (part.SegmentIdByteRange.StartInclusive != 0 ||
            part.SegmentIdByteRange.Count < 0 ||
            part.FilePartByteRange.StartInclusive < 0 ||
            part.FilePartByteRange.Count < 0 ||
            extraOffset < 0 ||
            extraOffset > part.FilePartByteRange.Count)
        {
            throw new SeekPositionNotFoundException(
                $"Corrupt file. Invalid multipart ranges while reading {_fileName ?? "unknown"}.");
        }

        var effectivePartLength = GetEffectivePartLength(part);
        if (effectivePartLength > part.SegmentIdByteRange.Count)
        {
            Log.Debug(
                "Multipart volume length {DeclaredLength} was too small for packed range ending at {RequiredLength} while reading {FileName}; using packed range as the length.",
                part.SegmentIdByteRange.Count,
                effectivePartLength,
                _fileName ?? "unknown");
        }

        var stream = _usenetClient.GetFileStream(
            part.SegmentIds,
            effectivePartLength,
            _articleBufferSize,
            part.SegmentByteRanges,
            _usePipelinedBodyRequests,
            _fileName,
            part.SegmentFallbackIds,
            _inFlightArticleBudget);
        stream.Seek(part.FilePartByteRange.StartInclusive + extraOffset, SeekOrigin.Begin);
        var expectedLength = part.FilePartByteRange.Count - extraOffset;
        var partId = part.SegmentIds.FirstOrDefault()
                     ?? $"range:{part.FilePartByteRange.StartInclusive}-{part.FilePartByteRange.EndExclusive}";
        var totalParts = (_mpf.Metadata.FileParts?.Length ?? 0) +
                         (_mpf.Metadata.PendingParts?.Length ?? 0);
        return new PaddedLengthStream(stream, expectedLength, partId, _fileName, new MultipartPartContext
        {
            PartNumber = partIndex + 1,
            PartCount = totalParts,
            SeekOffsetWithinPart = extraOffset,
            DeclaredVolumeLength = effectivePartLength,
            IsEncrypted = _mpf.Metadata.AesParams is not null,
        });
    }

    internal static long GetEffectivePartLength(DavMultipartFile.FilePart part) =>
        Math.Max(part.SegmentIdByteRange.Count, part.FilePartByteRange.EndExclusive);

    private async Task<Stream> ResolveAndOpenAsync(int targetIndex, CancellationToken ct)
    {
        await _resolver!.ResolveNextAsync(_mpf, ct).ConfigureAwait(false);
        var meta = _mpf.Metadata;
        if (targetIndex >= meta.FileParts.Length)
        {
            // The resolver always grows FileParts when pending parts remain, so landing
            // here means the volume could not be resolved. Returning an empty stream
            // would look like a clean end of file and hand the player a silently
            // truncated download.
            throw new IncompleteMultipartPartException(
                $"Volume {targetIndex + 1} of \"{_fileName ?? "unknown"}\" could not be resolved from its " +
                $"metadata ({meta.FileParts.Length} resolved, {meta.PendingParts?.Length ?? 0} pending). " +
                "The archive layout could not be read, so the rest of the file cannot be streamed.");
        }

        return OpenPart(meta.FileParts[targetIndex], 0, targetIndex);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _innerStream?.Dispose();
            var pending = _pendingInnerDispose;
            if (pending is not null)
            {
                _pendingInnerDispose = null;
                pending.ContinueWith(
                    t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pendingInnerDispose is { } pending)
        {
            _pendingInnerDispose = null;
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Teardown-only.
            }
        }
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
