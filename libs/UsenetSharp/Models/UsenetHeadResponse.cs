namespace UsenetSharp.Models;

public record UsenetHeadResponse : UsenetResponse
{
    public required string SegmentId { get; init; }
    public required UsenetArticleHeader? ArticleHeaders { get; init; }
}
