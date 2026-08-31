using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Contexts;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Cancels, drains, and observes a decoded BODY batch that cannot be returned to
/// its caller. The layer that rejects a batch owns this protocol.
/// </summary>
internal static class DecodedBodyBatchCleanup
{
    public static async Task AbandonAsync(
        UsenetDecodedBodyBatch batch,
        ContextualCancellationTokenSource owner)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(owner);

        ExceptionDispatchInfo? fatal = null;
        try
        {
            try
            {
                await owner.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException)
                    fatal = ExceptionDispatchInfo.Capture(exception);
            }

            foreach (var responseTask in batch.Responses)
            {
                try
                {
                    var response = await responseTask.ConfigureAwait(false);
                    if (response.Stream is not null)
                        await response.Stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (exception is OutOfMemoryException)
                        fatal = BatchLifecycle.PreferFailure(fatal, exception);
                }
            }

            try
            {
                await batch.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException)
                    fatal = BatchLifecycle.PreferFailure(fatal, exception);
            }
        }
        finally
        {
            owner.Dispose();
        }

        fatal?.Throw();
    }
}
