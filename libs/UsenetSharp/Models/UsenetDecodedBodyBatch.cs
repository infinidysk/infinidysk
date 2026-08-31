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

    /// <summary>
    /// Gets a task that completes after the producer has released this batch's transport and
    /// connection lifecycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a substitute for consuming or disposing each response stream. A later
    /// response still cannot become ready while an earlier stream remains undrained.
    /// </para>
    /// <para>
    /// The task may fault or cancel when the batch pump fails. Callers that abandon a batch
    /// must first cancel and dispose or drain handed-out streams, then await this task.
    /// Wrappers that construct a replacement batch must compose and expose the inner task.
    /// Consumers must observe it even when an individual response task has already failed.
    /// </para>
    /// <para>
    /// The default value is an already-completed task so existing construction sites remain
    /// source-compatible. Replacement wrappers that omit an assignment silently drop inner
    /// lifecycle; construction-site audits must assign a composed task whenever the wrapper
    /// replaces <see cref="Responses"/>.
    /// </para>
    /// </remarks>
    public Task Completion { get; init; } = Task.CompletedTask;
}
