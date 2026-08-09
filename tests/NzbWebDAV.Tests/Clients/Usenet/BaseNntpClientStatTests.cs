using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class BaseNntpClientStatTests
{
    [Fact]
    public async Task StatAsync_With223_ReturnsExistsResponse()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient(223));

        var response = await client.StatAsync("seg@example", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, response.ResponseType);
        Assert.True(response.ArticleExists);
    }

    [Fact]
    public async Task StatAsync_With430_ReturnsDefinitiveMissing()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient(430));

        var response = await client.StatAsync("seg@example", CancellationToken.None);

        Assert.True(UsenetArticleAvailability.IsDefinitiveMissing(response));
        Assert.False(response.ArticleExists);
    }

    [Fact]
    public async Task StatAsync_With451_ReturnsDefinitiveMissing()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient(451));

        var response = await client.StatAsync("seg@example", CancellationToken.None);

        Assert.True(UsenetArticleAvailability.IsDefinitiveMissing(response));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(480)]
    [InlineData(503)]
    public async Task StatAsync_WithConnectionLevelCode_ThrowsUnexpectedResponse(int responseCode)
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient(responseCode));

        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(() =>
            client.StatAsync("seg@example", CancellationToken.None));

        Assert.IsAssignableFrom<RetryableDownloadException>(exception);
        Assert.Equal("seg@example", exception.SegmentId);
    }

    [Fact]
    public async Task StatAsync_With480_UsesClearAuthRequiredMessage()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient(480));

        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(() =>
            client.StatAsync("seg@example", CancellationToken.None));

        Assert.Contains("requires authentication", exception.Message);
    }

    [Fact]
    public async Task StatsPipelinedAsync_WithMixedExistsAndMisses_PreservesOrder()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient([223, 430, 223, 451]));

        var results = new List<(string Id, bool Exists)>();
        await foreach (var result in client.StatsPipelinedAsync(
                           ["a@example", "b@example", "c@example", "d@example"],
                           depth: 8,
                           CancellationToken.None))
        {
            results.Add((result.SegmentId, result.Exists));
        }

        Assert.Equal(
            [("a@example", true), ("b@example", false), ("c@example", true), ("d@example", false)],
            results);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(480)]
    [InlineData(503)]
    public async Task StatsPipelinedAsync_WithConnectionLevelCode_ThrowsAndNeverReportsMiss(int responseCode)
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient([223, responseCode, 223]));

        var reported = new List<bool>();
        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(async () =>
        {
            await foreach (var result in client.StatsPipelinedAsync(
                               ["a@example", "b@example", "c@example"],
                               depth: 8,
                               CancellationToken.None))
            {
                reported.Add(result.Exists);
            }
        });

        Assert.Equal("b@example", exception.SegmentId);
        Assert.Equal([true], reported);
        Assert.DoesNotContain(false, reported);
    }

    [Fact]
    public async Task StatsPipelinedAsync_With480_UsesClearAuthRequiredMessage()
    {
        using var client = new BaseNntpClient(new ScriptedUsenetClient([480]));

        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(async () =>
        {
            await foreach (var _ in client.StatsPipelinedAsync(
                               ["seg@example"], 8, CancellationToken.None))
            {
            }
        });

        Assert.Contains("requires authentication", exception.Message);
    }

    [Fact]
    public async Task StatsPipelinedAsync_ChunksLargeBatches()
    {
        var codes = Enumerable.Repeat(223, BaseNntpClient.StatPipelinedSweepChunkSize + 3).ToArray();
        var underlying = new ScriptedUsenetClient(codes);
        using var client = new BaseNntpClient(underlying);

        var segmentIds = Enumerable.Range(0, codes.Length)
            .Select(i => $"seg{i}@example")
            .ToArray();
        var count = 0;
        await foreach (var result in client.StatsPipelinedAsync(segmentIds, depth: 1, CancellationToken.None))
        {
            Assert.True(result.Exists);
            count++;
        }

        Assert.Equal(codes.Length, count);
        Assert.Equal(2, underlying.StatPipelinedCallCount);
        Assert.Equal(
            [BaseNntpClient.StatPipelinedSweepChunkSize, 3],
            underlying.StatPipelinedBatchSizes);
    }

    [Fact]
    public void Dispose_PrefersUnderlyingAsyncDispose()
    {
        var underlying = new ScriptedUsenetClient(223);
        var client = new BaseNntpClient(underlying);

        client.Dispose();

        Assert.True(underlying.AsyncDisposed);
    }

    private sealed class ScriptedUsenetClient : IUsenetClient, IAsyncDisposable
    {
        private readonly int[] _responseCodes;
        private int _statIndex;

        public ScriptedUsenetClient(int responseCode) : this([responseCode])
        {
        }

        public ScriptedUsenetClient(int[] responseCodes)
        {
            _responseCodes = responseCodes;
        }

        public bool AsyncDisposed { get; private set; }
        public int StatPipelinedCallCount { get; private set; }
        public List<int> StatPipelinedBatchSizes { get; } = [];
        public bool IsConnected => true;
        public bool IsHealthy => true;

        public Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            var responseCode = NextCode();
            return Task.FromResult(MakeStat(responseCode, segmentId));
        }

        public Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<SegmentId> segmentIds,
            CancellationToken cancellationToken)
        {
            StatPipelinedCallCount++;
            StatPipelinedBatchSizes.Add(segmentIds.Count);
            var results = new UsenetStatResponse[segmentIds.Count];
            for (var i = 0; i < segmentIds.Count; i++)
                results[i] = MakeStat(NextCode(), segmentIds[i]);
            return Task.FromResult<IReadOnlyList<UsenetStatResponse>>(results);
        }

        public Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetBodyResponse> BodyAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetBodyResponse> BodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetArticleResponse> ArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetArticleResponse> ArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WaitForReadyAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }

        private int NextCode()
        {
            if (_statIndex >= _responseCodes.Length)
                throw new InvalidOperationException("Unexpected extra STAT request.");
            return _responseCodes[_statIndex++];
        }

        private static UsenetStatResponse MakeStat(int responseCode, SegmentId segmentId) =>
            new()
            {
                ResponseCode = responseCode,
                ResponseMessage = $"{responseCode} <{segmentId}>",
                ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists,
            };
    }
}
