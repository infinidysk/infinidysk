namespace NzbWebDAV.Streams;

/// <summary>
/// Tracks how many bytes each segment of a file must contribute to the reconstructed
/// stream. A segment that fails may only be replaced with zeros when its length is
/// known precisely: any other length shifts every following byte in the file, which
/// corrupts playback far beyond the segment that actually failed.
/// </summary>
internal sealed class SegmentSizes(ReadOnlyMemory<long> exactSizes, int segmentCount)
{
    private const long NonUniform = -1;

    private readonly ReadOnlyMemory<long> _exactSizes = Validate(exactSizes, segmentCount);
    private long _observedSize;

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
        if (_exactSizes.IsEmpty || segmentIndex < 0 || segmentIndex >= _exactSizes.Length)
        {
            size = 0;
            return false;
        }

        size = _exactSizes.Span[segmentIndex];
        return true;
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

    /// <summary>
    /// The uniform non-final segment size learned during this read session, or null if
    /// no uniform size was established (no observations, non-uniform, or file already had
    /// exact sizes from import).
    /// </summary>
    public long? LearnedUniformSize
    {
        get
        {
            if (!_exactSizes.IsEmpty) return null;
            var observed = Interlocked.Read(ref _observedSize);
            return observed > 0 ? observed : null;
        }
    }

    private bool IsFinalSegment(int segmentIndex) => segmentIndex == segmentCount - 1;
}
