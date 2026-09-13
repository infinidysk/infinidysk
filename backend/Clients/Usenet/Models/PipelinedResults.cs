using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet.Models;

public sealed record PipelinedBodyResult
{
    public required string SegmentId { get; init; }
    public required bool Found { get; init; }
    public YencStream? Stream { get; init; }

    /// <summary>
    /// True when every provider was tried and returned a definitive miss (430/451).
    /// False for single-provider verdicts and segment-id mismatches, which must
    /// remain eligible for rescue/failover.
    /// </summary>
    public bool DefinitivelyMissing { get; init; }
    public long? ProviderGeneration { get; init; }
}

public sealed record PipelinedArticleResult
{
    public required string SegmentId { get; init; }
    public required bool Found { get; init; }
    public YencStream? Stream { get; init; }
    public UsenetArticleHeader? ArticleHeaders { get; init; }

    /// <summary>
    /// True when every provider was tried and returned a definitive miss (430/451).
    /// False for single-provider verdicts and segment-id mismatches, which must
    /// remain eligible for rescue/failover.
    /// </summary>
    public bool DefinitivelyMissing { get; init; }
    public long? ProviderGeneration { get; init; }
}

public sealed record PipelinedStatResult
{
    public required string SegmentId { get; init; }
    public required bool Exists { get; init; }
}
