namespace UsenetSharp.Models;

public record UsenetBodyResponse : UsenetResponse
{
    public required string SegmentId { get; init; }
    public required Stream? Stream { get; init; }
}
