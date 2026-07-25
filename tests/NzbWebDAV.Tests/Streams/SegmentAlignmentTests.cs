using System.Collections.Concurrent;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// A segment that fails must never change how many bytes the stream delivers at each
/// offset. These tests pin that down: recover when the segment can still be fetched,
/// substitute exactly the recorded length when it cannot, and fail the read outright
/// rather than emit a guessed length that shifts the rest of the file.
/// </summary>
public class SegmentAlignmentTests
{
    private static readonly byte[] First = "aaaaa"u8.ToArray();
    private static readonly byte[] Second = "bbbbbbb"u8.ToArray();
    private static readonly byte[] Third = "ccccc"u8.ToArray();

    [Fact]
    public async Task PipelinedFailure_IsRescuedIndividually_WithoutZeroFilling()
    {
        var client = CreateClient();
        client.BatchFailures["two"] = 1;

        await using var stream = CreateStream(client, exactSizes: [5, 7, 5]);
        var output = await ReadAllAsync(stream);

        Assert.Equal("aaaaabbbbbbbccccc", Encoding.ASCII.GetString(output));
        Assert.Contains("two", client.IndividualRequests);
    }

    [Fact]
    public async Task ExhaustedRetries_ZeroFillExactlyOneSegment_KeepingFollowingOffsets()
    {
        var client = CreateClient();
        client.BatchFailures["two"] = int.MaxValue;
        client.IndividualFailures["two"] = int.MaxValue;

        await using var stream = CreateStream(client, exactSizes: [5, 7, 5]);
        var output = await ReadAllAsync(stream);

        // The middle segment is 7 bytes, not the 5.66-byte average of this file.
        Assert.Equal(17, output.Length);
        Assert.Equal("aaaaa", Encoding.ASCII.GetString(output, 0, 5));
        Assert.Equal(new byte[7], output[5..12]);
        Assert.Equal("ccccc", Encoding.ASCII.GetString(output, 12, 5));
    }

    [Fact]
    public async Task UnknownSegmentLength_FailsTheReadInsteadOfGuessing()
    {
        var client = CreateClient(new Dictionary<string, byte[]>
        {
            // "one" is absent on every provider and nothing has been read yet, so no
            // length for it can be established.
            ["two"] = Second,
            ["three"] = Third,
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            fileName: "unknown-length.bin");

        var failure = await Assert.ThrowsAsync<NonRetryableDownloadException>(
            () => ReadAllAsync(stream));

        Assert.Contains("exact length is unknown", failure.Message, StringComparison.Ordinal);
        Assert.IsType<UsenetArticleNotFoundException>(failure.InnerException);
    }

    [Fact]
    public async Task ObservedUniformSegmentSize_StandsInForAMissingSegment()
    {
        var client = CreateClient(new Dictionary<string, byte[]>
        {
            ["one"] = First,
            // "two" is missing, but "one" proves this file's segments are 5 bytes.
            ["three"] = Third,
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            fileName: "observed-length.bin");

        var output = await ReadAllAsync(stream);

        Assert.Equal(15, output.Length);
        Assert.Equal(new byte[5], output[5..10]);
        Assert.Equal("ccccc", Encoding.ASCII.GetString(output, 10, 5));
    }

    [Fact]
    public async Task ResponseForAnotherSegment_IsRejectedAndRefetched()
    {
        var client = CreateClient();
        client.BatchResponseIdOverrides["two"] = "some-other-segment";

        await using var stream = CreateStream(client, exactSizes: [5, 7, 5]);
        var output = await ReadAllAsync(stream);

        Assert.Equal("aaaaabbbbbbbccccc", Encoding.ASCII.GetString(output));
        Assert.Contains("two", client.IndividualRequests);
    }

    [Fact]
    public async Task UnbufferedResponseForAnotherSegment_FailsInsteadOfServingItsBytes()
    {
        var client = CreateClient();
        client.IndividualResponseIdOverrides["two"] = "some-other-segment";

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            cancellationToken: CancellationToken.None,
            fileName: "unbuffered-mismatch.bin",
            exactSegmentSizes: new long[] { 5, 7, 5 });

        var failure = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            () => ReadAllAsync(stream));

        Assert.Contains("some-other-segment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptRetryReturningAnotherSegment_IsRejected()
    {
        var client = CreateClient();
        client.BatchCorruption["two"] = 1;
        client.IndividualResponseIdOverrides["two"] = "some-other-segment";

        await using var stream = CreateStream(client, exactSizes: [5, 7, 5]);

        var failure = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            () => ReadAllAsync(stream));

        Assert.Contains("some-other-segment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchResponsesCompletingOutOfOrder_AreStillDeliveredInOrder()
    {
        var client = CreateClient();
        // Make the first response the slowest so a consumer that took whatever finished
        // first would emit the segments transposed.
        client.BatchDelays["one"] = TimeSpan.FromMilliseconds(150);
        client.BatchDelays["two"] = TimeSpan.FromMilliseconds(50);

        await using var stream = CreateStream(client, exactSizes: [5, 7, 5]);
        var output = await ReadAllAsync(stream);

        Assert.Equal("aaaaabbbbbbbccccc", Encoding.ASCII.GetString(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task BodyShorterThanItsRecordedLength_IsPaddedToKeepOffsets(
        int articleBufferSize)
    {
        var client = CreateClient(new Dictionary<string, byte[]>
        {
            ["one"] = "aa"u8.ToArray(),
            ["two"] = Second,
            ["three"] = Third,
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: articleBufferSize,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: articleBufferSize > 0,
            cancellationToken: CancellationToken.None,
            fileName: $"short-body-{articleBufferSize}.bin",
            exactSegmentSizes: new long[] { 5, 7, 5 });
        var output = await ReadAllAsync(stream);

        Assert.Equal(17, output.Length);
        Assert.Equal("aa", Encoding.ASCII.GetString(output, 0, 2));
        Assert.Equal(new byte[3], output[2..5]);
        Assert.Equal("bbbbbbb", Encoding.ASCII.GetString(output, 5, 7));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task BodyLongerThanItsRecordedLength_IsTruncatedToKeepOffsets(
        int articleBufferSize)
    {
        var client = CreateClient(new Dictionary<string, byte[]>
        {
            ["one"] = "aaaaaEXTRA"u8.ToArray(),
            ["two"] = Second,
            ["three"] = Third,
        });

        await using var stream = MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: articleBufferSize,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: articleBufferSize > 0,
            cancellationToken: CancellationToken.None,
            fileName: $"long-body-{articleBufferSize}.bin",
            exactSegmentSizes: new long[] { 5, 7, 5 });
        var output = await ReadAllAsync(stream);

        Assert.Equal(17, output.Length);
        Assert.Equal("aaaaabbbbbbbccccc", Encoding.ASCII.GetString(output));
    }

    [Fact]
    public async Task ReadBudget_WithIrregularSegments_StillDeliversTheWholeRange()
    {
        var segments = new Dictionary<string, byte[]>();
        var sizes = new long[12];
        for (var i = 0; i < sizes.Length; i++)
        {
            // Irregular on purpose: an average-based budget would stop short.
            var size = 100 + i * 10;
            segments[$"seg-{i}"] = Enumerable.Repeat((byte)(i + 1), size).ToArray();
            sizes[i] = size;
        }

        var client = CreateClient(segments);
        const long readBudget = 900;
        await using var stream = MultiSegmentStream.Create(
            segments.Keys.ToArray().AsMemory(),
            client,
            articleBufferSize: 8,
            estimatedSegmentSize: 100,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cancellationToken: CancellationToken.None,
            fileName: "irregular-budget.bin",
            readBudget: readBudget,
            exactSegmentSizes: sizes);

        var buffer = new byte[readBudget];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) break;
            total += read;
        }

        Assert.Equal(readBudget, total);
    }

    private static Stream CreateStream(ScriptedNntpClient client, long[] exactSizes) =>
        MultiSegmentStream.Create(
            new[] { "one", "two", "three" }.AsMemory(),
            client,
            articleBufferSize: 4,
            estimatedSegmentSize: 6,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: true,
            cancellationToken: CancellationToken.None,
            fileName: "alignment.bin",
            exactSegmentSizes: exactSizes);

    private static ScriptedNntpClient CreateClient(IReadOnlyDictionary<string, byte[]>? segments = null) =>
        new(segments ?? new Dictionary<string, byte[]>
        {
            ["one"] = First,
            ["two"] = Second,
            ["three"] = Third,
        });

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    /// <summary>
    /// Serves already-decoded bodies (no rapidyenc needed) and can fail, delay, or
    /// mislabel individual responses to reproduce the ways a shared batch connection
    /// breaks in production.
    /// </summary>
    private sealed class ScriptedNntpClient(IReadOnlyDictionary<string, byte[]> segments) : NntpClient
    {
        public Dictionary<string, int> BatchFailures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> BatchCorruption { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> IndividualFailures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> BatchResponseIdOverrides { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> IndividualResponseIdOverrides { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan> BatchDelays { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> IndividualRequests { get; } = new();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            IndividualRequests.Enqueue(key);
            if (TakeFailure(IndividualFailures, key))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new TimeoutException($"Timeout executing nntp BODY command for {key}."));
            }

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return CreateResponse(key, IndividualResponseIdOverrides.GetValueOrDefault(key, key));
        }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds.Select(CreateBatchResponse).ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        private async Task<UsenetDecodedBodyResponse> CreateBatchResponse(SegmentId segmentId)
        {
            var key = segmentId.ToString();
            if (BatchDelays.TryGetValue(key, out var delay))
                await Task.Delay(delay);

            if (TakeFailure(BatchFailures, key))
            {
                throw new TimeoutException(
                    $"Timeout executing pipelined nntp BODY command for {key}.");
            }

            if (TakeFailure(BatchCorruption, key))
            {
                throw new UsenetCorruptArticleException(
                    key, "provider-1", new InvalidDataException("yEnc crc mismatch"));
            }

            var reportedId = BatchResponseIdOverrides.GetValueOrDefault(key, key);
            return await CreateResponse(key, reportedId);
        }

        private Task<UsenetDecodedBodyResponse> CreateResponse(string key, string reportedId)
        {
            if (!segments.TryGetValue(key, out var bytes))
            {
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new UsenetArticleNotFoundException(key, "430 No such article"));
            }

            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = reportedId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = $"222 0 <{reportedId}> body follows",
                Stream = new DecodedBytesStream(bytes),
            });
        }

        private static bool TakeFailure(Dictionary<string, int> failures, string key)
        {
            lock (failures)
            {
                if (!failures.TryGetValue(key, out var remaining) || remaining <= 0) return false;
                if (remaining != int.MaxValue) failures[key] = remaining - 1;
                return true;
            }
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            DecodedBodiesAsync(segmentIds, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class DecodedBytesStream(byte[] bytes) : YencStream(Null)
    {
        private int _position;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= bytes.Length) return ValueTask.FromResult(0);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }
}
