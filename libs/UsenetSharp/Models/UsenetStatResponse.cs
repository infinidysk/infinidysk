namespace UsenetSharp.Models;

public record UsenetStatResponse : UsenetResponse
{
    public required bool ArticleExists { get; init; }
}
