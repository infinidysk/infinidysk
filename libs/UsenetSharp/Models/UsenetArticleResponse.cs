namespace UsenetSharp.Models;

public record UsenetArticleResponse : UsenetResponse
{
    public required string SegmentId { get; init; }
    public required Stream? Stream { get; init; }
    public required UsenetArticleHeader? ArticleHeaders { get; init; }
}
