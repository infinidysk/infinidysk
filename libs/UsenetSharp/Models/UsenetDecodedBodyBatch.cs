namespace UsenetSharp.Models;

/// <summary>
/// Represents an ordered batch of pipelined, decoded NNTP BODY responses.
/// </summary>
/// <remarks>
/// Responses complete strictly in request order. Consumers must await each response and fully
/// consume or dispose its <see cref="UsenetDecodedBodyResponse.Stream"/> before awaiting the next
/// response. A later response intentionally cannot complete while an earlier stream remains
/// undrained, and decoded output uses bounded pipe backpressure.
/// </remarks>
public sealed record UsenetDecodedBodyBatch
{
    /// <summary>
    /// Gets response tasks in the same order as the requested segment IDs.
    /// </summary>
    public required IReadOnlyList<Task<UsenetDecodedBodyResponse>> Responses { get; init; }

    internal Task Completion { get; init; } = Task.CompletedTask;
}
