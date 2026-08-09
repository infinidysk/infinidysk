using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpClientPipelinedMappingTests
{
    [Theory]
    [InlineData("seg@example.com", "seg@example.com")]
    [InlineData("<seg@example.com>", "seg@example.com")]
    [InlineData(" <seg@example.com> ", "seg@example.com")]
    [InlineData("seg@example.com>", "seg@example.com")]
    [InlineData("<seg@example.com", "seg@example.com")]
    [InlineData("< seg@example.com >", "seg@example.com")]
    public void NormalizeSegmentId_StripsAngleBrackets(string input, string expected)
    {
        Assert.Equal(expected, NntpClient.NormalizeSegmentId(input));
    }

    [Theory]
    [InlineData("seg@example.com")]
    [InlineData("<seg@example.com>")]
    [InlineData("  seg@example.com  ")]
    [InlineData("<seg@example.com>\n")]
    [InlineData("\n<seg@example.com>")]
    public void IsValidSegmentId_AcceptsNormalizedMessageIds(string input)
    {
        Assert.True(NntpClient.IsValidSegmentId(input));
        Assert.Equal("seg@example.com", NntpClient.PrepareSegmentId(input).ToString());
    }

    [Theory]
    [InlineData("<seg@example.com>\n")]
    [InlineData("\n<seg@example.com>")]
    public void PrepareSegmentId_NormalizesBracketAndWhitespaceCombos_AfterSegmentIdConversion(string raw)
    {
        // Mimic the production path: the raw DB string is converted to a SegmentId
        // (which strips at most one bracket per end) before PrepareSegmentId runs.
        SegmentId segmentId = raw;
        Assert.Equal("seg@example.com", NntpClient.PrepareSegmentId(segmentId).ToString());
    }

    [Theory]
    [InlineData("not-a-message-id")]
    [InlineData("@example.com")]
    [InlineData("local@")]
    [InlineData("bad<id>@example.com")]
    [InlineData("has space@example.com")]
    [InlineData("")]
    [InlineData("ab")]
    public void PrepareSegmentId_ThrowsNotFound_ForInvalidIds(string input)
    {
        Assert.False(NntpClient.IsValidSegmentId(input));
        Assert.Throws<UsenetArticleNotFoundException>(() => NntpClient.PrepareSegmentId(input));
    }

    [Fact]
    public void IsValidSegmentId_RejectsBareIdsLongerThan248Octets()
    {
        // RFC 5536 limits a message-id to 250 octets including client-added brackets.
        var tooLong = new string('a', 245) + "@x.y"; // 249 octets
        Assert.Equal(249, tooLong.Length);
        Assert.False(NntpClient.IsValidSegmentId(tooLong));

        var maxOk = new string('a', 244) + "@x.y"; // 248 octets
        Assert.Equal(248, maxOk.Length);
        Assert.True(NntpClient.IsValidSegmentId(maxOk));
    }

    [Fact]
    public void HasSegmentIdMismatch_IgnoresSyntheticMessagesWithoutWireId()
    {
        Assert.False(NntpClient.HasSegmentIdMismatch(
            "seg@example.com",
            "seg@example.com",
            "222 - Article retrieved from file cache",
            out _));
    }

    [Fact]
    public void HasSegmentIdMismatch_DetectsWrongResponseSegmentId()
    {
        Assert.True(NntpClient.HasSegmentIdMismatch(
            "expected@example.com",
            "wrong@example.com",
            "222 scripted response",
            out var actual));
        Assert.Equal("wrong@example.com", actual);
    }

    [Fact]
    public void HasSegmentIdMismatch_DetectsWrongWireMessageId()
    {
        Assert.True(NntpClient.HasSegmentIdMismatch(
            "expected@example.com",
            "expected@example.com", // echoed request id would hide a scramble
            "222 0 <wrong@example.com>",
            out var actual));
        Assert.Equal("wrong@example.com", actual);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_MismatchedWireId_ReturnsNotFound()
    {
        using var client = new MismatchedSegmentNntpClient(
            requestedId: "expected@example.com",
            responseSegmentId: "expected@example.com",
            responseMessage: "222 0 <wrong@example.com>");

        PipelinedBodyResult? result = null;
        await foreach (var item in client.DecodedBodiesPipelinedAsync(
                           ["expected@example.com"], 1, CancellationToken.None))
            result = item;

        Assert.NotNull(result);
        Assert.False(result.Found);
        Assert.Null(result.Stream);
        Assert.Equal("expected@example.com", result.SegmentId);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_MatchingIds_ReturnsFound()
    {
        using var client = new MismatchedSegmentNntpClient(
            requestedId: "seg@example.com",
            responseSegmentId: "seg@example.com",
            responseMessage: "222 0 <seg@example.com>");

        PipelinedBodyResult? result = null;
        await foreach (var item in client.DecodedBodiesPipelinedAsync(
                           ["seg@example.com"], 1, CancellationToken.None))
            result = item;

        Assert.NotNull(result);
        Assert.True(result.Found);
        Assert.NotNull(result.Stream);
        await result.Stream!.DisposeAsync();
    }

    private sealed class MismatchedSegmentNntpClient(
        string requestedId,
        string responseSegmentId,
        string responseMessage) : NntpClient
    {
        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Assert.Equal(requestedId, segmentId.ToString());
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(CreateResponse());
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Assert.Single(segmentIds);
            Assert.Equal(requestedId, segmentIds[0].ToString());
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch
            {
                Responses = [Task.FromResult(CreateResponse())],
            });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }

        private UsenetDecodedBodyResponse CreateResponse()
        {
            var payload = Encoding.ASCII.GetBytes("payload");
            var headers = new UsenetYencHeader
            {
                FileName = "fake.bin",
                FileSize = payload.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = payload.Length,
            };
            return new UsenetDecodedBodyResponse
            {
                SegmentId = responseSegmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = responseMessage,
                Stream = new CachedYencStream(headers, new MemoryStream(payload, writable: false)),
            };
        }
    }
}
