using System.Runtime.ExceptionServices;
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
        ExceptionDispatchInfo? fatal = null;
        void ObserveTerminal(Exception? exception)
        {
            observeTerminal?.Invoke(exception);
            if (exception is OutOfMemoryException)
                fatal = BatchLifecycle.PreferFailure(fatal, exception);
        }

        for (var index = 0; index < rawResponses.Count; index++)
        {
            CaptureFatal(
                ref fatal,
                await ObservePredecessorAsync(previousTerminal).ConfigureAwait(false));
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
                        ObserveTerminal),
                });
                previousTerminal = terminal.Task;
            }
            catch (Exception exception)
            {
                output[index].TrySetException(exception);
                if (exception is OutOfMemoryException)
                    fatal = BatchLifecycle.PreferFailure(fatal, exception);
                previousTerminal = Task.CompletedTask;
            }
        }

        CaptureFatal(
            ref fatal,
            await ObservePredecessorAsync(previousTerminal).ConfigureAwait(false));
        fatal?.Throw();
    }

    private static void CaptureFatal(
        ref ExceptionDispatchInfo? current,
        ExceptionDispatchInfo? candidate)
    {
        if (candidate?.SourceException is OutOfMemoryException exception)
            current = BatchLifecycle.PreferFailure(current, exception);
    }

    private static Task<ExceptionDispatchInfo?> ObservePredecessorAsync(Task previous) =>
        BatchLifecycle.ObserveAsync(previous);
}
