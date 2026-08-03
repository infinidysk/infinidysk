using UsenetSharp.Streams;

namespace UsenetSharp.Models;

public record UsenetDecodedBodyResponse : UsenetResponse
{
    public required string SegmentId { get; init; }
    public required YencStream? Stream { get; init; }
}
