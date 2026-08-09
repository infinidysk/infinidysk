using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpClientNonYencHeadersTests
{
    [Fact]
    public async Task GetYencHeadersAsync_NonYencBody_ThrowsNonRetryable()
    {
        var client = new PlainTextBodyClient();

        var exception = await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            client.GetYencHeadersAsync("plain@example.com", CancellationToken.None));

        Assert.Contains("plain@example.com", exception.Message);
        Assert.Contains("not yEnc-encoded", exception.Message);
    }

    [Fact]
    public async Task GetFileSizeAsync_NonYencLastSegment_PropagatesNonRetryable()
    {
        var client = new PlainTextBodyClient();
        var file = new NzbFile { Subject = "test" };
        file.Segments.Add(new NzbSegment
        {
            Bytes = 100,
            MessageId = "plain@example.com",
            Number = 1,
        });

        await Assert.ThrowsAsync<NonRetryableDownloadException>(() =>
            client.GetFileSizeAsync(file, CancellationToken.None));
    }

    private sealed class PlainTextBodyClient : NntpClient
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
            var bytes = Encoding.ASCII.GetBytes("this is not a yEnc article\r\n.\r\n");
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new NullHeaderYencStream(bytes),
            });
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
    }

    private sealed class NullHeaderYencStream(byte[] bytes) : YencStream(new MemoryStream(bytes, writable: false))
    {
        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UsenetYencHeader?>(null);
    }
}
