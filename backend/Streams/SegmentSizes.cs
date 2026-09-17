using System.Collections.Concurrent;

namespace NzbWebDAV.Streams;

/// <summary>
/// Tracks how many bytes each segment of a file must contribute to the reconstructed
/// stream. A segment that fails may only be replaced with gap bytes when its length is
/// known precisely: any other length shifts every following byte in the file, which
/// corrupts playback far beyond the segment that actually failed.
/// </summary>
internal sealed class SegmentSizes
{
    private const long NonUniform = -1;

    private readonly ReadOnlyMemory<long> _exactSizes;
    private readonly int _segmentCount;
    private readonly ConcurrentDictionary<int, long> _discoveredSizes = new();
    private long _observedSize;

    public SegmentSizes(ReadOnlyMemory<long> exactSizes, int segmentCount)
    {
        _exactSizes = Validate(exactSizes, segmentCount);
        _segmentCount = segmentCount;
    }

    /// <summary>
    /// Per-segment sizes recorded at import time, or empty when this file has none.
    /// Rejects anything that cannot describe the segments being read.
    /// </summary>
    public static ReadOnlyMemory<long> Validate(ReadOnlyMemory<long> sizes, int segmentCount)
    {
        if (sizes.Length != segmentCount || segmentCount == 0) return default;
        var span = sizes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] <= 0) return default;
        }

        return sizes;
    }

    public bool TryGetExactSize(int segmentIndex, out long size)
    {
        if (segmentIndex < 0 || segmentIndex >= _segmentCount)
        {
            size = 0;
            return false;
        }

        if (!_exactSizes.IsEmpty)
        {
            size = _exactSizes.Span[segmentIndex];
            return true;
        }

        return _discoveredSizes.TryGetValue(segmentIndex, out size);
    }

    public void RecordExactSize(int segmentIndex, long size)
    {
        if (size <= 0 || segmentIndex < 0 || segmentIndex >= _segmentCount)
            throw new InvalidDataException($"Invalid exact size {size} for segment index {segmentIndex}.");
        if (!_exactSizes.IsEmpty)
        {
            if (_exactSizes.Span[segmentIndex] != size)
                throw new InvalidDataException($"Conflicting exact size for segment index {segmentIndex}.");
            return;
        }

        if (_discoveredSizes.TryGetValue(segmentIndex, out var existing) && existing != size)
            throw new InvalidDataException($"Conflicting exact size for segment index {segmentIndex}.");
        _discoveredSizes[segmentIndex] = size;
    }

    /// <summary>
    /// Remembers the decoded size of a segment that downloaded successfully, for files that
    /// have no recorded ranges to slice. Three things make this a sound stand-in rather than
    /// a guess: a poster splits one file into uniform yEnc parts apart from the final one,
    /// bodies are CRC-validated before they get here so a truncated article fails instead of
    /// being observed at the wrong size, and a second observation that disagrees marks the
    /// file non-uniform so nothing is inferred from it again. It is the same uniformity the
    /// import relies on when it derives per-segment ranges from the first segment
    /// (see NzbFile.GetSegmentByteRanges), applied at read time when that never happened.
    /// The final segment is excluded because its shorter size says nothing about the others.
    /// </summary>
    public void RecordObservedSize(int segmentIndex, long size)
    {
        if (size <= 0 || IsFinalSegment(segmentIndex)) return;

        var current = Interlocked.CompareExchange(ref _observedSize, size, 0);
        if (current == 0 || current == size || current == NonUniform) return;
        Interlocked.Exchange(ref _observedSize, NonUniform);
    }

    /// <summary>
    /// Resolves how many bytes a failed segment must contribute. Returns false when the
    /// length cannot be established, in which case the caller must fail the read instead
    /// of guessing.
    /// </summary>
    public bool TryGetFillLength(int segmentIndex, out long length, out bool isExact)
    {
        if (TryGetExactSize(segmentIndex, out length))
        {
            isExact = true;
            return true;
        }

        isExact = false;
        var observed = Interlocked.Read(ref _observedSize);
        if (observed > 0 && !IsFinalSegment(segmentIndex))
        {
            length = observed;
            return true;
        }

        length = 0;
        return false;
    }

    private bool IsFinalSegment(int segmentIndex) => segmentIndex == _segmentCount - 1;
}
