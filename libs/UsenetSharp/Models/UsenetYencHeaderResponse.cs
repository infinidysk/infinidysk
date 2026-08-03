namespace UsenetSharp.Models;

/// <summary>
/// Response to a yEnc header probe: the segment's yEnc metadata without the body.
/// </summary>
public record UsenetYencHeaderResponse : UsenetResponse
{
    public required string SegmentId { get; init; }

    /// <summary>
    /// Parsed yEnc headers (<c>=ybegin</c> and, when multipart, <c>=ypart</c>),
    /// or <see langword="null"/> when the article was not retrieved (non-222)
    /// or contained no yEnc content.
    /// </summary>
    public required UsenetYencHeader? YencHeader { get; init; }
}
