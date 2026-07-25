using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize,
    LongRange[]? segmentByteRanges = null,
    bool usePipelinedBodyRequests = true,
    string? fileName = null,
    string[][]? segmentFallbacks = null,
    InFlightArticleBudget? inFlightArticleBudget = null
) : FastReadOnlyStream
{
    private const long MaximumForwardDrainBytes = 1024 * 1024;
    private long _position;
    private long _pendingForwardDrain;
    private bool _disposed;
    private Stream? _innerStream;
    private readonly LongRange[]? _segmentByteRanges =
        AreSegmentByteRangesValid(segmentByteRanges, fileSegmentIds.Length, fileSize)
            ? segmentByteRanges
            : null;

    private long[]? _exactSegmentSizes;

    // Average yEnc-decoded size per segment in this file, used to guess which segment
    // covers a byte offset (seek probes and capacity hints). It is only ever an
    // approximation — the tail segment is shorter, so the average is off by a few bytes
    // for every segment — and must never decide how many bytes the stream emits.
    private long EstimatedSegmentSize =>
        fileSegmentIds.Length > 0 ? Math.Max(1, fileSize / fileSegmentIds.Length) : 0;

    /// <summary>
    /// Exact decoded size of each segment, when the import recorded per-segment byte
    /// ranges. This is what lets a failed segment be replaced by the right number of
    /// bytes instead of an approximation that shifts the rest of the file.
    /// </summary>
    private long[]? ExactSegmentSizes
    {
        get
        {
            if (_segmentByteRanges is null) return null;
            return _exactSegmentSizes ??= _segmentByteRanges
                .Select(range => range.Count)
                .ToArray();
        }
    }

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_position >= fileSize) return 0;
        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        if (_pendingForwardDrain > 0)
        {
            try
            {
                // Exact: a partial skip would leave the stream short of the position the
                // caller seeked to, and every byte it then read would be misattributed.
                await _innerStream.DiscardExactBytesAsync(
                    _pendingForwardDrain, cancellationToken).ConfigureAwait(false);
                _pendingForwardDrain = 0;
            }
            catch
            {
                await _innerStream.DisposeAsync().ConfigureAwait(false);
                _innerStream = null;
                _pendingForwardDrain = 0;
                throw;
            }
        }

        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
                SeekOrigin.End => checked(fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (absoluteOffset < 0 || absoluteOffset > fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_position == absoluteOffset) return _position;
        if (_innerStream is not null &&
            absoluteOffset > _position &&
            absoluteOffset - _position <= MaximumForwardDrainBytes)
        {
            _pendingForwardDrain += absoluteOffset - _position;
            _position = absoluteOffset;
            if (MultiProviderNntpClient.CurrentReadSessionId is { } drainSession)
                StreamTrace.TrySeek(drainSession, _position);
            return _position;
        }

        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        _pendingForwardDrain = 0;
        if (MultiProviderNntpClient.CurrentReadSessionId is { } seekSession)
            StreamTrace.TrySeek(seekSession, _position);
        return _position;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        if (_segmentByteRanges is not null)
        {
            return InterpolationSearch.Find(
                byteOffset,
                new LongRange(0, _segmentByteRanges.Length),
                new LongRange(0, fileSize),
                guess => _segmentByteRanges[guess]
            );
        }

        var avg = EstimatedSegmentSize;
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                try
                {
                    var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                    return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
                }
                catch (UsenetArticleNotFoundException e)
                {
                    // The probe segment itself is missing — fall back to a
                    // synthetic uniform-size range so interpolation can still
                    // converge. The actual body read of this segment (if it
                    // turns out to be the seek target) gets zero-filled by
                    // MultiSegmentStream.
                    Log.Warning(
                        "Seek probe hit missing article {SegmentId} (segment index {Index}) while reading {FileName}. Using estimated range.",
                        e.SegmentId, guess, string.IsNullOrEmpty(fileName) ? "unknown" : fileName);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
                catch (Exception e) when (articleBufferSize > 0 && !ct.IsCancellationRequested)
                {
                    e.LogWarningKnownOrStack(
                        "Seek probe transient failure on segment index {Index}. Using estimated range.", guess);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
            },
            ct
        ).ConfigureAwait(false);
    }

    private static bool AreSegmentByteRangesValid(LongRange[]? ranges, int segmentCount, long expectedFileSize)
    {
        if (ranges is null || ranges.Length != segmentCount || ranges.Length == 0) return false;
        if (ranges[0].StartInclusive != 0 || ranges[^1].EndExclusive != expectedFileSize) return false;

        for (var i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Count <= 0) return false;
            if (i > 0 && ranges[i - 1].EndExclusive != ranges[i].StartInclusive) return false;
        }

        return true;
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, failFastOnFirstSegment: true, cancellationToken);
        var fast = await TryGetSeekStreamFast(rangeStart, cancellationToken).ConfigureAwait(false);
        if (fast != null) return fast;

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, failFastOnFirstSegment: false, cancellationToken);
        var prefix = rangeStart - foundSegment.FoundByteRange.StartInclusive;
        try
        {
            await stream.DiscardExactBytesAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            // The segment that should contain this offset delivered fewer bytes than the
            // index says it holds. Returning the exhausted stream would answer the range
            // request with zeros or nothing at all, so report the seek as impossible.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new SeekPositionNotFoundException(
                $"Byte position {rangeStart} of \"{fileName ?? "unknown"}\" is past the data " +
                $"available in segment {foundSegment.FoundIndex + 1}. {e.Message}");
        }

        return stream;
    }

    private const int MaxSeekGuessCorrection = 3;

    private async Task<Stream?> TryGetSeekStreamFast(long rangeStart, CancellationToken ct)
    {
        var avg = EstimatedSegmentSize;
        if (avg <= 0 || fileSegmentIds.Length == 0) return null;

        var index = (int)Math.Clamp(rangeStart / avg, 0, fileSegmentIds.Length - 1);

        for (var step = 0; step <= MaxSeekGuessCorrection; step++)
        {
            UsenetDecodedBodyResponse response;
            try
            {
                response = await usenetClient.DecodedBodyAsync(fileSegmentIds[index], ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            var body = response.Stream!;
            UsenetYencHeader? header;
            try
            {
                header = await body.GetYencHeadersAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (header == null)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            var start = header.PartOffset;
            var end = header.PartOffset + header.PartSize;

            if (rangeStart < start || rangeStart >= end)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                var next = rangeStart < start ? index - 1 : index + 1;
                if (next < 0 || next >= fileSegmentIds.Length) return null;
                index = next;
                continue;
            }

            MemoryStream head;
            try
            {
                await body.DiscardExactBytesAsync(rangeStart - start, ct).ConfigureAwait(false);
                var tail = end - rangeStart;
                var capacity = tail is > 0 and <= int.MaxValue ? (int)tail : 0;
                head = new MemoryStream(capacity);
                await body.CopyToAsync(head, ct).ConfigureAwait(false);
                head.Position = 0;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // The guess was right (headers matched) but the body read failed,
                // e.g. a mid-stream NNTP read timeout. Fall back to the slow seek
                // path, whose MultiSegmentStream retries and zero-fills instead of
                // surfacing a hard error to the WebDAV client.
                e.LogWarningKnownOrStack(
                    "Fast seek failed mid-segment while reading {FileName}. Falling back to segment-index seek.",
                    string.IsNullOrEmpty(fileName) ? "unknown" : fileName);
                return null;
            }
            finally
            {
                await body.DisposeAsync().ConfigureAwait(false);
            }

            return new CombinedStream(SpliceHeadThenRest(head, index + 1, ct));
        }

        return null;
    }

    private IEnumerable<Task<Stream>> SpliceHeadThenRest(Stream head, int restFirstIndex, CancellationToken ct)
    {
        yield return Task.FromResult(head);
        yield return Task.FromResult(GetMultiSegmentStream(restFirstIndex, failFastOnFirstSegment: false, ct));
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, bool failFastOnFirstSegment,
        CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        string[][]? fallbacks = null;
        if (segmentFallbacks is { Length: > 0 } && firstSegmentIndex < segmentFallbacks.Length)
            fallbacks = segmentFallbacks[firstSegmentIndex..];

        var exactSizes = ExactSegmentSizes is { } sizes
            ? sizes.AsMemory(firstSegmentIndex)
            : default;

        return MultiSegmentStream.Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            EstimatedSegmentSize,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            segmentFallbacks: fallbacks,
            exactSegmentSizes: exactSizes,
            inFlightArticleBudget: inFlightArticleBudget);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) _innerStream?.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
