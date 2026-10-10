using System.Globalization;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Fakes;

/// <summary>
/// Serves whole-article requests (used by first-segment fetches for their Date header)
/// from BODY responses of a fake that only implements BODY. Optionally hangs until
/// cancelled so callers can exercise timeouts.
/// </summary>
internal sealed class ArticleFromBodyNntpClient(INntpClient inner, bool hangUntilCancelled = false)
    : WrappingNntpClient(inner)
{
    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        DecodedArticleAsync(segmentId, null, cancellationToken);

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        if (hangUntilCancelled)
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        var body = await InnerClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);
        return new UsenetDecodedArticleResponse
        {
            SegmentId = body.SegmentId,
            ResponseCode = 220,
            ResponseMessage = "220 test article",
            Stream = body.Stream ?? throw new InvalidOperationException("BODY response had no stream."),
            ArticleHeaders = new UsenetArticleHeader
            {
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Date"] = DateTimeOffset.UnixEpoch.ToString("R", CultureInfo.InvariantCulture),
                },
            },
        };
    }
}
