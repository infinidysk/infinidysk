namespace NzbWebDAV.Exceptions;

/// <summary>
/// The same yEnc CRC pair was confirmed on at least two providers. Retrying the
/// queue item cannot recover the segment — the article itself is corrupt.
/// </summary>
public sealed class PersistentUsenetCorruptionException(
    string segmentId,
    uint actualCrc,
    uint expectedCrc,
    Exception? innerException = null)
    : NonRetryableDownloadException(
        $"Segment {segmentId} failed yEnc CRC identically on multiple providers " +
        $"(actual {actualCrc:x8}, expected {expectedCrc:x8}).",
        innerException)
{
    public string SegmentId { get; } = segmentId;
    public uint ActualCrc { get; } = actualCrc;
    public uint ExpectedCrc { get; } = expectedCrc;
}
