namespace UsenetSharp.Models;

public record UsenetDateResponse : UsenetResponse
{
    public DateTimeOffset? DateTime { get; init; }
}
