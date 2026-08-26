using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Fakes;

/// <summary>
/// Serves a single-segment video file from memory with a correct yEnc header
/// filename, so the import pipeline deobfuscates to the given file name.
/// </summary>
internal sealed class ScriptedVideoNntpClient(
    string fileName,
    string segmentId,
    byte[] payload) : NntpClient
{
    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = id.ToString() == segmentId;
        return Task.FromResult(new UsenetStatResponse
        {
            ResponseCode = exists
                ? (int)UsenetResponseType.ArticleExists
                : (int)UsenetResponseType.NoArticleWithThatMessageId,
            ResponseMessage = exists ? "223 exists" : "430 missing",
            ArticleExists = exists,
        });
    }

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId id, CancellationToken cancellationToken) =>
        DecodedBodyAsync(id, null, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId id,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.ToString() != segmentId)
            return Task.FromException<UsenetDecodedBodyResponse>(
                new UsenetArticleNotFoundException(id.ToString(), "430 missing"));
        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        return Task.FromResult(new UsenetDecodedBodyResponse
        {
            SegmentId = id.ToString(),
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 body",
            Stream = CreateStream(),
        });
    }

    public override Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId id, CancellationToken cancellationToken) =>
        Task.FromResult<UsenetDecodedBodyResponse?>(null);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        var responses = segmentIds
            .Select(id => DecodedBodyAsync(id, cancellationToken))
            .ToArray();
        return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId id, CancellationToken cancellationToken) =>
        DecodedArticleAsync(id, null, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId id,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.ToString() != segmentId)
            throw new UsenetArticleNotFoundException(id.ToString(), "430 missing");
        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        return Task.FromResult(new UsenetDecodedArticleResponse
        {
            SegmentId = id.ToString(),
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "220 article",
            Stream = CreateStream(),
            ArticleHeaders = new UsenetArticleHeader
            {
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                },
            },
        });
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string id, CancellationToken cancellationToken) =>
        Task.FromResult(new UsenetExclusiveConnection(null));

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        IReadOnlyList<SegmentId> ids, CancellationToken cancellationToken) =>
        Task.FromResult(new UsenetExclusiveConnection(null));

    public override Task<long> GetFileSizeAsync(NzbWebDAV.Models.Nzb.NzbFile file, CancellationToken ct) =>
        Task.FromResult((long)payload.Length);

    public override void Dispose()
    {
    }

    private CachedYencStream CreateStream() =>
        new(
            new UsenetYencHeader
            {
                FileName = fileName,
                FileSize = payload.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = payload.Length,
            },
            new MemoryStream(payload, writable: false));
}
