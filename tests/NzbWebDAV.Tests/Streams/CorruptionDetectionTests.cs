using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class CorruptionDetectionTests
{
    [Fact]
    public async Task CorruptionDetectingStream_AddsSegmentAndProviderContext()
    {
        await using var stream = new CorruptionDetectingYencStream(
            new ThrowingYencStream(new InvalidDataException("CRC mismatch")),
            "segment@example",
            "provider-a");

        var exception = await Assert.ThrowsAsync<UsenetCorruptArticleException>(async () =>
            await stream.ReadExactlyAsync(new byte[1]));

        Assert.Equal("segment@example", exception.SegmentId);
        Assert.Equal("provider-a", exception.ProviderKey);
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task BufferedStream_RetriesCorruptionWithoutZeroFilling()
    {
        var expected = "validated payload"u8.ToArray();
        using var client = new CorruptThenValidNntpClient(expected, corruptResponses: 3);
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment@example" }.AsMemory(),
            client,
            articleBufferSize: 1,
            estimatedSegmentSize: expected.Length,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(4, client.BodyRequestCount);
    }

    private sealed class ThrowingYencStream(Exception exception) : YencStream(Null)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
    }

    private sealed class BytesYencStream(byte[] bytes) : YencStream(Null)
    {
        private int _position;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= bytes.Length)
                return ValueTask.FromResult(0);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class CorruptThenValidNntpClient(
        byte[] validPayload,
        int corruptResponses) : NntpClient
    {
        public int BodyRequestCount { get; private set; }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        private Task<UsenetDecodedBodyResponse> CreateResponse(SegmentId segmentId)
        {
            BodyRequestCount++;
            YencStream stream = BodyRequestCount <= corruptResponses
                ? new ThrowingYencStream(
                    new UsenetCorruptArticleException(segmentId, "provider-a",
                        new InvalidDataException("CRC mismatch")))
                : new BytesYencStream(validPayload);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "body follows",
                Stream = stream,
            });
        }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

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

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
