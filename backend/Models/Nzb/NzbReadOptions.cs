namespace NzbWebDAV.Models.Nzb;

internal sealed class NzbReadOptions(long maxXmlCharacters, Action<long> charge, Func<long, IDisposable> reserve)
{
    public long MaxXmlCharacters { get; } = maxXmlCharacters;
    public int MaxFiles { get; init; } = 10_000;
    public int MaxSegments { get; init; } = 1_302_083;
    public int MaxSubjectLength { get; init; } = 1024;
    public int MaxMessageIdLength { get; init; } = 248;
    public int MaxMetadataLength { get; init; } = 16 * 1024;
    public int SegmentCount { get; private set; }

    public IDisposable ReserveReader() => reserve(checked(MaxXmlCharacters * 8 + 64 * 1024));

    public void AddFile(int count, string subject)
    {
        if (count >= MaxFiles || subject.Length > MaxSubjectLength)
            throw new InvalidDataException("NZB file count or subject exceeds repair parsing limits.");
        charge(checked(512L + subject.Length * 4L));
    }

    public void AddSegment(string messageId)
    {
        if (++SegmentCount > MaxSegments || messageId.Length > MaxMessageIdLength)
            throw new InvalidDataException("NZB segment count or Message-ID exceeds repair parsing limits.");
        charge(checked(512L + messageId.Length * 4L));
    }

    public void AddMetadata(string key, string value)
    {
        if (key.Length > MaxMetadataLength || value.Length > MaxMetadataLength)
            throw new InvalidDataException("NZB metadata exceeds repair parsing limits.");
        charge(checked(256L + 4L * (key.Length + value.Length)));
    }
}