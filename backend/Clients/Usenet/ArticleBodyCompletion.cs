using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Invokes an <see cref="ArticleBodyCompletionHandler"/> with non-fatal exception
/// containment so observer failures cannot escape into cache, repair, admission, or
/// NNTP transport control flow.
/// </summary>
/// <remarks>
/// Addresses https://github.com/infinidysk/infinidysk/issues/1239 (#1128 F10).
/// Adjacent callback hardening found while validating
/// https://github.com/infinidysk/infinidysk/issues/1185; that issue's
/// queue-stage/provider-pool admission deadlines remain separate work.
/// </remarks>
internal static class ArticleBodyCompletion
{
    public static void InvokeContained(
        ArticleBodyCompletionHandler? callback,
        ArticleBodyResult result,
        string? failureReason = null)
    {
        try
        {
            callback?.Invoke(result, failureReason);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(
                exception,
                "NNTP completion callback failed for {Result}",
                result);
        }
    }
}
