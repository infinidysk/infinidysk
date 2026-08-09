using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This class has four responsibilities that differ from the underlying UsenetClient implementation
///   1. throw `CouldNotConnectToUsenetException` after any connection error.
///   2. throw `CouldNotLoginToUsenetException` after any login error.
///   3. Provide yenc-decoded data for articles retrieved through article/body commands.
///   4. throw `UsenetArticleNotFound` when articles do not exist, within article/body/head commands.
/// </summary>
public class BaseNntpClient : NntpClient
{
    /// <summary>
    /// Sweep chunk size for <see cref="StatsPipelinedAsync"/>. UsenetSharp windows STAT
    /// internally with a sliding MaxPipelineDepth; this bound is only for progress
    /// reporting and call batching (not BODY pipelining depth). The sweep drains the
    /// full enumerable — misses are collected for failover recheck, not early-stopped.
    /// </summary>
    internal const int StatPipelinedSweepChunkSize = 512;

    private readonly IUsenetClient _client;

    public BaseNntpClient() : this(skipTlsVerification: false)
    {
    }

    #pragma warning disable CA2000 // the client is stored in _client and disposed with this instance
    public BaseNntpClient(bool skipTlsVerification) : this(new UsenetClient(new UsenetClientOptions
    #pragma warning restore CA2000
    {
        CrcValidation = EnvironmentUtil.GetEnvironmentVariable("USENET_DISABLE_CRC_VALIDATION") == "1"
            ? YencCrcValidationMode.Off
            : YencCrcValidationMode.WhenPresent,
        SkipTlsVerification = skipTlsVerification,
    }))
    {
    }

    /// <summary>Test seam for injecting a scripted underlying client.</summary>
    internal BaseNntpClient(IUsenetClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public override async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(host, port, useSsl, cancellationToken).ConfigureAwait(false);
        }
        #pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException())
        #pragma warning restore CA2016
        {
            const string message = "Could not connect to usenet host. Check connection settings.";
            throw new CouldNotConnectToUsenetException(message, e);
        }
    }

    public override async Task<UsenetResponse> AuthenticateAsync
    (
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                var message = $"Could not login to usenet host: {response.ResponseMessage}";
                throw new CouldNotLoginToUsenetException(message);
            }

            return response;
        }
        #pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException())
        #pragma warning restore CA2016
        {
            throw new CouldNotLoginToUsenetException("Could not login to usenet host.", e);
        }
    }

    public override async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        segmentId = PrepareSegmentId(segmentId);
        var response = await _client.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);

        // 223 (exists) and definitive-missing (430 / provider 451) are valid STAT
        // outcomes and are conveyed via the response object — callers check
        // ResponseType / IsDefinitiveMissing. Connection/session-level codes
        // (buffered 400 goodbye, 480 auth, 5xx) must not be treated as article verdicts.
        if (response.ResponseType == UsenetResponseType.ArticleExists ||
            UsenetArticleAvailability.IsDefinitiveMissing(response))
        {
            return response;
        }

        throw CreateConnectionLevelException(segmentId, response);
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // `depth` is BODY-oriented interface baggage; STAT sizing uses the fixed sweep
        // chunk so UsenetSharp's internal MaxPipelineDepth window stays continuously full.
        _ = depth;
        if (segmentIds.Count == 0) yield break;

        for (var batchStart = 0; batchStart < segmentIds.Count; batchStart += StatPipelinedSweepChunkSize)
        {
            var batchSize = Math.Min(StatPipelinedSweepChunkSize, segmentIds.Count - batchStart);
            var batchIds = new SegmentId[batchSize];
            for (var index = 0; index < batchSize; index++)
                batchIds[index] = PrepareSegmentId(segmentIds[batchStart + index]);

            var responses = await _client.StatPipelinedAsync(batchIds, cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < responses.Count; index++)
            {
                var segmentId = batchIds[index];
                var response = responses[index];

                // UsenetSharp returns connection-level codes as ordered ArticleExists=false
                // results — classify like StatAsync so a stale 400 goodbye never looks
                // like a missing article (false repair/delete).
                if (response.ResponseType == UsenetResponseType.ArticleExists)
                {
                    yield return new PipelinedStatResult
                    {
                        SegmentId = segmentId,
                        Exists = true,
                    };
                    continue;
                }

                if (UsenetArticleAvailability.IsDefinitiveMissing(response))
                {
                    yield return new PipelinedStatResult
                    {
                        SegmentId = segmentId,
                        Exists = false,
                    };
                    continue;
                }

                throw CreateConnectionLevelException(segmentId, response);
            }
        }
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        segmentId = PrepareSegmentId(segmentId);
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);

        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw CreateArticleFetchException(segmentId, headResponse);

        return new UsenetHeadResponse()
        {
            SegmentId = headResponse.SegmentId,
            ResponseCode = headResponse.ResponseCode,
            ResponseMessage = headResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!
        };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        segmentId = PrepareSegmentId(segmentId);
        var bodyResponse = await _client.DecodedBodyAsync(
            segmentId, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);

        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw CreateArticleFetchException(segmentId, bodyResponse);

        return new UsenetDecodedBodyResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            Stream = bodyResponse.Stream!,
        };
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return _client.DecodedBodiesAsync(
            PrepareSegmentIds(segmentIds), onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        segmentId = PrepareSegmentId(segmentId);
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw CreateArticleFetchException(segmentId, headResponse);

        // UsenetSharp validates CRCs on decoded BODY responses. Pair HEAD + BODY
        // instead of locally decoding ARTICLE so first-segment metadata probes get
        // the same corruption detection as normal streaming.
        var bodyResponse = await _client.DecodedBodyAsync(
            segmentId, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw CreateArticleFetchException(segmentId, bodyResponse);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!,
            Stream = bodyResponse.Stream!,
        };
    }

    private static SegmentId[] PrepareSegmentIds(IReadOnlyList<SegmentId> segmentIds)
    {
        var prepared = new SegmentId[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
            prepared[index] = PrepareSegmentId(segmentIds[index]);
        return prepared;
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    /// <summary>
    /// Definitive missing (430 / provider 451) means the article is gone.
    /// Anything else (e.g. a buffered "400 idle timeout" from a connection the
    /// server already closed) is a connection-level problem and must be retryable.
    /// </summary>
    private static Exception CreateArticleFetchException(SegmentId segmentId, UsenetResponse response)
    {
        return UsenetArticleAvailability.IsDefinitiveMissing(response)
            ? new UsenetArticleNotFoundException(segmentId, response.ResponseMessage)
            : CreateConnectionLevelException(segmentId, response);
    }

    private static UsenetUnexpectedResponseException CreateConnectionLevelException(SegmentId segmentId, UsenetResponse response)
    {
        if (response.ResponseCode == 480)
        {
            return new UsenetUnexpectedResponseException(
                segmentId,
                "Provider requires authentication but no credentials are configured");
        }

        return new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage);
    }

    public override void Dispose()
    {
        if (_client is IAsyncDisposable asyncDisposable)
        {
            // UsenetSharp sends a best-effort QUIT only from DisposeAsync.
            // Pool disposal already runs off the request path, so wait here to
            // release strict providers' connection slots before reconnecting.
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
