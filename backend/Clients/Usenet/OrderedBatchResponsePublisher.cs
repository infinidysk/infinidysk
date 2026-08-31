using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Publishes already-started batch responses so callers observe them strictly in
/// request order, while fallback/start work may already be running concurrently.
/// </summary>
internal static class OrderedBatchResponsePublisher
{
    public static async Task PublishAsync(
        IReadOnlyList<Task<UsenetDecodedBodyResponse>> rawResponses,
        TaskCompletionSource<UsenetDecodedBodyResponse>[] output,
        Action<Exception?>? observeTerminal = null)
    {
        ArgumentNullException.ThrowIfNull(rawResponses);
        ArgumentNullException.ThrowIfNull(output);
        if (rawResponses.Count != output.Length)
        {
            throw new ArgumentException(
                "Ordered publication requires one output source per raw response.");
        }

        Task previousTerminal = Task.CompletedTask;
        for (var index = 0; index < rawResponses.Count; index++)
        {
            await ObservePredecessorAsync(previousTerminal).ConfigureAwait(false);
            try
            {
                var response = await rawResponses[index].ConfigureAwait(false);
                if (response.Stream is null)
                {
                    output[index].TrySetResult(response);
                    previousTerminal = Task.CompletedTask;
                    continue;
                }

                var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output[index].TrySetResult(response with
                {
                    Stream = new OrderedBatchYencStream(
                        response.Stream,
                        terminal,
                        observeTerminal ?? Noop),
                });
                previousTerminal = terminal.Task;
            }
            catch (Exception exception)
            {
                output[index].TrySetException(exception);
                previousTerminal = Task.CompletedTask;
            }
        }

        await ObservePredecessorAsync(previousTerminal).ConfigureAwait(false);
    }

    private static void Noop(Exception? _)
    {
    }

    private static async Task ObservePredecessorAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Predecessor already surfaced on its own response or stream.
        }
    }
}
