namespace NzbWebDAV.Streams;

/// <summary>
/// Reports whether a buffered stream already holds every article-budget lease it needs to
/// reach its end, so speculative work elsewhere can no longer starve it of credits.
/// </summary>
internal interface ISegmentIssueProgress
{
    bool AllSegmentsIssued { get; }
}
