using System.IO;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiProviderNntpClientTests
{
    [Fact]
    public async Task BatchResponse_WithUnexpectedResponse_RetriesOnSameProvider()
    {
        // A stale pooled connection surfaces the server's buffered goodbye line
        // (e.g. "400 idle timeout") as the batch response. The segment must be
        // retried on the same provider instead of being reported missing.
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_WithCleanNotFound_RetriesOnSameProvider()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_WithUnexpectedResponse_ThrowsRetryableWhenRetriesFail()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularException = segmentId =>
                new UsenetUnexpectedResponseException(segmentId, "400 idle timeout"),
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);

        // A connection-level failure must surface as retryable,
        // never as a (permanent) missing article.
        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            () => batch.Responses[0]);
        Assert.IsAssignableFrom<RetryableDownloadException>(exception);
    }

    [Fact]
    public async Task BatchSetup_WithStaleCancellation_RetriesOnAnotherConnection()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            BatchException = requestNumber => requestNumber == 1
                ? new TaskCanceledException("Cancellation recorded by an earlier request.")
                : null,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(2, connection.BatchRequests);
    }

    [Fact]
    public async Task BatchSetup_WithCurrentRequestCancellation_DoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            BatchException = _ =>
            {
                cancellation.Cancel();
                return new TaskCanceledException("Current request was cancelled.");
            },
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DecodedBodiesAsync(
                ["segment"], onConnectionReadyAgain: null, cancellation.Token));
        Assert.Equal(1, connection.BatchRequests);
    }

    [Fact]
    public async Task PipelinedBodyResponse_RecordsFetchMetric_OnSuccess()
    {
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection)], metricsWriter: writer);

        var results = await CollectPipelinedAsync(client, ["segment"], 1);
        Assert.Single(results);
        Assert.True(results[0].Found);
        if (results[0].Stream != null)
            await results[0].Stream.DisposeAsync();

        // Exactly one fetch — must not double-count override metrics + DecodedBodiesAsync.
        Assert.Equal(1, writer.Stats.QueuedFetches);
        Assert.Equal(1, connection.BatchRequests);
        Assert.Equal(0, connection.SingularRequests);
    }

    [Fact]
    public async Task StatAsync_UnexpectedResponse_FailsOverToBackup()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularException = segmentId =>
                new UsenetUnexpectedResponseException(segmentId, "400 idle timeout"),
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ]);

        var response = await client.StatAsync("segment", CancellationToken.None);

        Assert.True(response.ArticleExists);
        Assert.True(primary.SingularRequests >= 1);
        Assert.True(backup.SingularRequests >= 1);
    }

    [Fact]
    public async Task StatAsync_Success_DoesNotRecordSegmentFetch()
    {
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection)], metricsWriter: writer);

        var response = await client.StatAsync("segment", CancellationToken.None);
        Assert.True(response.ArticleExists);
        Assert.Equal(0, writer.Stats.QueuedFetches);
    }

    [Fact]
    public async Task StatAsync_DefinitiveMissing_RecordsMissingFetch()
    {
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection)], metricsWriter: writer);

        var response = await client.StatAsync("segment", CancellationToken.None);
        Assert.False(response.ArticleExists);
        Assert.Equal(1, writer.Stats.QueuedFetches);
    }

    [Fact]
    public async Task DecodedBodyAsync_UnexpectedResponseType_RecordsMissingFetch()
    {
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularResponseCode = 400,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection)], metricsWriter: writer);

        var response = await client.DecodedBodyAsync(
            "segment", onConnectionReadyAgain: null, CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(1, writer.Stats.QueuedFetches);
    }

    [Fact]
    public async Task PipelinedBody_PrimaryMiss_FailsOverToBackup()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ]);

        var results = await CollectPipelinedAsync(client, ["segment"], depth: 2);

        Assert.Single(results);
        Assert.True(results[0].Found);
        Assert.NotNull(results[0].Stream);
        await results[0].Stream!.DisposeAsync();
        Assert.True(primary.BatchRequests >= 1);
        Assert.True(primary.SingularRequests >= 1);
        Assert.Equal(1, backup.SingularRequests);
    }

    [Fact]
    public async Task PipelinedBody_SuccessfulPrimary_DoesNotCallBackup()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ]);

        var results = await CollectPipelinedAsync(client, ["seg-a", "seg-b"], depth: 2);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Found));
        foreach (var result in results)
            if (result.Stream != null)
                await result.Stream.DisposeAsync();
        Assert.Equal(1, primary.BatchRequests);
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(0, backup.BatchRequests);
        Assert.Equal(0, backup.SingularRequests);
    }

    private static async Task<List<PipelinedBodyResult>> CollectPipelinedAsync(
        MultiProviderNntpClient client,
        IReadOnlyList<string> segmentIds,
        int depth)
    {
        var results = new List<PipelinedBodyResult>();
        await foreach (var result in client.DecodedBodiesPipelinedAsync(
                           segmentIds, depth, CancellationToken.None))
            results.Add(result);
        return results;
    }

    [Fact]
    public async Task StorageGroup_SameGroupMiss_SkipsSiblingProvider()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var sibling = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, response.ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_ConnectionError_DoesNotSkipSibling()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new IOException("connection reset"),
        };
        var sibling = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        // MultiConnectionNntpClient retries the failed connection once before failing over.
        Assert.True(first.SingularRequests >= 1);
        Assert.Equal(1, sibling.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_DifferentGroups_StillFailsOver()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var other = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(other, host: "b.example", storageGroup: "eweka"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(1, other.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_Empty_PreservesFailover()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var second = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example"),
            CreateProvider(second, host: "b.example"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(1, second.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_BatchPrimaryRetry_NotSkippedBySameGroupSibling()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        var sibling = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
        ]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, primary.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_StreamingTerminalMiss_FiresCompletionCallbackOnce()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var sibling = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var callbacks = new List<ArticleBodyResult>();
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
        ]);

        var response = await client.DecodedBodyAsync(
            "segment", callbacks.Add, CancellationToken.None);

        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, response.ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
        Assert.Single(callbacks);
        Assert.Equal(ArticleBodyResult.NotRetrieved, callbacks[0]);
    }

    [Fact]
    public async Task StorageGroup_SameGroupMiss451_SkipsSiblingProvider()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 451,
            SingularResponseCode = 451,
        };
        var sibling = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(451, response.ResponseCode);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
    }

    [Fact]
    public async Task StorageGroup_DifferentGroups_FailsOverOn451()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 451,
            SingularResponseCode = 451,
        };
        var other = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(other, host: "b.example", storageGroup: "eweka"),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(1, other.SingularRequests);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_With451AcrossProviders_ThrowsArticleNotFound()
    {
        var first = new ScriptedNntpClient
        {
            BatchResponseCode = 451,
            SingularResponseCode = 451,
        };
        var second = new ScriptedNntpClient
        {
            BatchResponseCode = 451,
            SingularResponseCode = 451,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example"),
            CreateProvider(second, host: "b.example"),
        ]);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(["segment"], 1, null, CancellationToken.None));

        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(1, second.SingularRequests);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_With400_ThrowsUnexpectedResponse()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularResponseCode = 400,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(() =>
            client.CheckAllSegmentsAsync(["segment"], 1, null, CancellationToken.None));

        Assert.IsAssignableFrom<RetryableDownloadException>(exception);
    }

    [Fact]
    public async Task BatchResponse_With451Exhausted_ThrowsArticleNotFound()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 451,
            SingularResponseCode = 451,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() => batch.Responses[0]);
    }

    [Fact]
    public async Task DecodedBodiesAsync_WithInvalidSegmentId_DoesNotFailoverOrTripBreaker()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            BatchException = _ => new UsenetArticleNotFoundException("not-a-message-id"),
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
        };
        var primaryProvider = CreateProvider(primary, host: "a.example");
        var backupProvider = CreateProvider(backup, host: "b.example");
        using var client = new MultiProviderNntpClient([primaryProvider, backupProvider]);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.DecodedBodiesAsync(
                ["not-a-message-id"], onConnectionReadyAgain: null, CancellationToken.None));

        Assert.Equal(1, primary.BatchRequests);
        Assert.Equal(0, backup.BatchRequests);
        Assert.False(primaryProvider.IsTripped);
        Assert.False(backupProvider.IsTripped);
    }

    [Fact]
    public async Task Selection_DoesNotSpendTheHalfOpenProbeSlot()
    {
        var recovering = HalfOpenBreaker("a.example");
        var healthyConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(
                new ScriptedNntpClient
                {
                    BatchResponseCode = 223,
                    SingularResponseCode = (int)UsenetResponseType.ArticleExists,
                },
                host: "a.example", circuitBreaker: recovering),
            CreateProvider(healthyConnection, host: "b.example"),
        ]);

        for (var i = 0; i < 5; i++)
            await client.StatAsync($"segment-{i}", CancellationToken.None);

        // Selection must leave the slot unclaimed, otherwise the one admission the
        // recovering provider gets is burned by a request served elsewhere.
        Assert.Equal(ProviderCircuitState.HalfOpen, recovering.GetSnapshot().State);
        Assert.False(recovering.IsTripped);
    }

    [Fact]
    public async Task Selection_PrefersAHealthyProviderOverAHalfOpenOne()
    {
        var recoveringConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        var healthyConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(recoveringConnection, host: "a.example",
                circuitBreaker: HalfOpenBreaker("a.example")),
            CreateProvider(healthyConnection, host: "b.example"),
        ]);

        var response = await client.StatAsync("segment", CancellationToken.None);

        Assert.True(response.ArticleExists);
        Assert.Equal(0, recoveringConnection.SingularRequests);
        Assert.True(healthyConnection.SingularRequests >= 1);
    }

    [Fact]
    public async Task Selection_StillUsesAHalfOpenProviderWhenItIsTheOnlyOne()
    {
        var recoveringConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(recoveringConnection, host: "a.example",
                circuitBreaker: HalfOpenBreaker("a.example")),
        ]);

        var response = await client.StatAsync("segment", CancellationToken.None);

        Assert.True(response.ArticleExists);
        Assert.True(recoveringConnection.SingularRequests >= 1);
    }

    [Fact]
    public async Task Selection_SkipsAProviderStillInsideItsCooldown()
    {
        var openConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        var healthyConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(openConnection, host: "a.example", circuitBreaker: OpenBreaker("a.example")),
            CreateProvider(healthyConnection, host: "b.example"),
        ]);

        await client.StatAsync("segment", CancellationToken.None);

        Assert.Equal(0, openConnection.SingularRequests);
        Assert.True(healthyConnection.SingularRequests >= 1);
    }

    [Fact]
    public async Task Selection_KeepsAHalfOpenPrimaryAheadOfAHealthyBackup()
    {
        var recoveringPrimary = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        var healthyBackup = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(recoveringPrimary, host: "a.example",
                circuitBreaker: HalfOpenBreaker("a.example")),
            CreateProvider(healthyBackup, host: "b.example",
                providerType: ProviderType.BackupOnly),
        ]);

        await client.StatAsync("segment", CancellationToken.None);

        // Demotion must not invert the tiers. A recovering primary is still a better
        // first choice than a metered block account.
        Assert.True(recoveringPrimary.SingularRequests >= 1);
        Assert.Equal(0, healthyBackup.SingularRequests);
    }

    [Fact]
    public async Task Selection_HalfOpenProviderClosesItsBreakerOnceTheFailoverWalkReachesIt()
    {
        var recovering = HalfOpenBreaker("b.example");
        var failingConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularException = segmentId =>
                new UsenetUnexpectedResponseException(segmentId, "400 idle timeout"),
        };
        var recoveredConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 223,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(failingConnection, host: "a.example"),
            CreateProvider(recoveredConnection, host: "b.example", circuitBreaker: recovering),
        ]);

        var response = await client.StatAsync("segment", CancellationToken.None);

        // Pins that a half-open provider stays in the pool rather than being excluded,
        // which is the shape a naive fix gets wrong. It does not discriminate the
        // demotion ordering, since the probe slot admits the provider either way.
        Assert.True(response.ArticleExists);
        Assert.True(recoveredConnection.SingularRequests >= 1);
        Assert.Equal(ProviderCircuitState.Closed, recovering.GetSnapshot().State);
    }

    [Fact]
    public async Task PoolMode_PrefersProviderWithMostUnreservedConnections()
    {
        var bytesTracker = new ProviderBytesTracker();
        bytesTracker.RecordSegmentThroughput("small.example", 1_000_000, 1);
        var smallConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var largeConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(smallConnection, host: "small.example", maxConnections: 1),
            CreateProvider(largeConnection, host: "large.example", maxConnections: 4),
        ], bytesTracker: bytesTracker, cascadeEnabled: () => false);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(0, smallConnection.SingularRequests);
        Assert.Equal(1, largeConnection.SingularRequests);
    }

    [Fact]
    public async Task PoolMode_RoutesAroundSaturatedFasterProvider()
    {
        var bytesTracker = new ProviderBytesTracker();
        bytesTracker.RecordSegmentThroughput("fast.example", 1_000_000, 1);
        var fastConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
            DeferSingularCompletion = true,
        };
        var idleConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(fastConnection, host: "fast.example"),
            CreateProvider(idleConnection, host: "idle.example"),
        ], bytesTracker: bytesTracker, cascadeEnabled: () => false);

        UsenetDecodedBodyResponse? firstResponse = null;
        try
        {
            firstResponse = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var secondResponse = await client.DecodedBodyAsync("segment-2", timeout.Token);
            await secondResponse.Stream!.DisposeAsync();

            Assert.Equal(1, fastConnection.SingularRequests);
            Assert.Equal(1, idleConnection.SingularRequests);
        }
        finally
        {
            fastConnection.CompletePendingSingularRequests();
            if (firstResponse?.Stream != null)
                await firstResponse.Stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task CascadeMode_PreservesPriorityOverAvailableCapacity()
    {
        var primaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var secondaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primaryConnection, host: "primary.example", maxConnections: 1, priority: 0),
            CreateProvider(secondaryConnection, host: "secondary.example", maxConnections: 4, priority: 1),
        ], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, primaryConnection.SingularRequests);
        Assert.Equal(0, secondaryConnection.SingularRequests);
    }

    [Fact]
    public void ClassifyException_Timeout_ReturnsTimeout()
    {
        var status = MultiProviderNntpClient.ClassifyException(new TimeoutException());
        Assert.Equal(SegmentFetch.FetchStatus.Timeout, status);
    }

    [Fact]
    public void ClassifyException_CorruptArticle_ReturnsCorrupt()
    {
        var exception = new UsenetCorruptArticleException("segment", "provider", new Exception("bad crc"));
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Corrupt, status);
    }

    [Fact]
    public void ClassifyException_CouldNotLogin_ReturnsAuth()
    {
        var exception = new CouldNotLoginToUsenetException("bad credentials");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Auth, status);
    }

    [Fact]
    public void ClassifyException_UnauthorizedAccess_ReturnsAuth()
    {
        var status = MultiProviderNntpClient.ClassifyException(new UnauthorizedAccessException());
        Assert.Equal(SegmentFetch.FetchStatus.Auth, status);
    }

    [Fact]
    public void ClassifyException_CouldNotConnect_ReturnsNetwork()
    {
        var exception = new CouldNotConnectToUsenetException("connection refused");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Network, status);
    }

    [Fact]
    public void ClassifyException_IOException_ReturnsNetwork()
    {
        var status = MultiProviderNntpClient.ClassifyException(new IOException("connection reset"));
        Assert.Equal(SegmentFetch.FetchStatus.Network, status);
    }

    [Fact]
    public void ClassifyException_SocketException_ReturnsNetwork()
    {
        var exception = new System.Net.Sockets.SocketException();
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Network, status);
    }

    [Fact]
    public void ClassifyException_UnknownException_ReturnsOther()
    {
        var status = MultiProviderNntpClient.ClassifyException(new Exception("boom"));
        Assert.Equal(SegmentFetch.FetchStatus.Other, status);
    }

    [Fact]
    public void ClassifyException_CorruptArticleWrappedInOuterException_StillReturnsCorrupt()
    {
        // NNTP failures are often re-thrown wrapped by an outer exception; the innermost
        // known cause must still win so it isn't misclassified as Other.
        var inner = new UsenetCorruptArticleException("segment", "provider", new Exception("bad crc"));
        var wrapped = new InvalidOperationException("stream read failed", inner);
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
        Assert.Equal(SegmentFetch.FetchStatus.Corrupt, status);
    }

    [Fact]
    public void ClassifyException_LoginFailureWrappedInOuterException_StillReturnsAuth()
    {
        var inner = new CouldNotLoginToUsenetException("bad credentials");
        var wrapped = new InvalidOperationException("stream read failed", inner);
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
        Assert.Equal(SegmentFetch.FetchStatus.Auth, status);
    }

    [Fact]
    public void ClassifyException_ConnectFailureWrappedInOuterException_StillReturnsNetwork()
    {
        var inner = new CouldNotConnectToUsenetException("connection refused");
        var wrapped = new InvalidOperationException("stream read failed", inner);
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
        Assert.Equal(SegmentFetch.FetchStatus.Network, status);
    }

    private static MultiConnectionNntpClient CreateProvider(
        INntpClient connection,
        string host = "test",
        string storageGroup = "",
        ProviderCircuitBreaker? circuitBreaker = null,
        ProviderType providerType = ProviderType.Pooled,
        int maxConnections = 1,
        int priority = 0)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections, _ => ValueTask.FromResult(connection));
        return new MultiConnectionNntpClient(
            pool,
            providerType,
            circuitBreaker ?? new ProviderCircuitBreaker(host),
            host,
            priority: priority,
            storageGroup: storageGroup);
    }

    /// <summary>Trips a breaker and lets its cooldown lapse so it lands half-open.</summary>
    private static ProviderCircuitBreaker HalfOpenBreaker(string host)
    {
        var breaker = new ProviderCircuitBreaker(host);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.ExpireCooldownForTests();
        return breaker;
    }

    /// <summary>Trips a breaker and leaves it inside its cooldown, fully open.</summary>
    private static ProviderCircuitBreaker OpenBreaker(string host)
    {
        var breaker = new ProviderCircuitBreaker(host);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        return breaker;
    }

    private sealed class ScriptedNntpClient : NntpClient
    {
        public required int BatchResponseCode { get; init; }
        public int SingularResponseCode { get; init; } = 222;
        public Func<int, Exception?>? BatchException { get; init; }
        public Func<string, Exception>? SingularException { get; init; }
        public bool DeferSingularCompletion { get; init; }
        public int BatchRequests { get; private set; }
        public int SingularRequests { get; private set; }
        private readonly Queue<Action<ArticleBodyResult>> _pendingSingularCallbacks = new();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchRequests++;
            var exception = BatchException?.Invoke(BatchRequests);
            if (exception != null)
                throw exception;

            var responses = segmentIds
                .Select(segmentId => Task.FromResult(CreateResponse(segmentId, BatchResponseCode)))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ToArticleBodyResult(BatchResponseCode));
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            SingularRequests++;
            if (SingularException != null)
                throw SingularException(segmentId.ToString());

            var response = CreateResponse(segmentId, SingularResponseCode);
            if (DeferSingularCompletion && onConnectionReadyAgain != null)
                _pendingSingularCallbacks.Enqueue(onConnectionReadyAgain);
            else
                onConnectionReadyAgain?.Invoke(ToArticleBodyResult(SingularResponseCode));
            return Task.FromResult(response);
        }

        public void CompletePendingSingularRequests()
        {
            while (_pendingSingularCallbacks.TryDequeue(out var callback))
                callback(ToArticleBodyResult(SingularResponseCode));
        }

        private static ArticleBodyResult ToArticleBodyResult(int responseCode) => responseCode switch
        {
            (int)UsenetResponseType.ArticleRetrievedBodyFollows => ArticleBodyResult.Retrieved,
            (int)UsenetResponseType.NoArticleWithThatMessageId => ArticleBodyResult.NotFound,
            UsenetArticleAvailability.ArticleUnavailable => ArticleBodyResult.NotFound,
            _ => ArticleBodyResult.NotRetrieved,
        };

        private static UsenetDecodedBodyResponse CreateResponse(SegmentId segmentId, int responseCode)
        {
            var success = responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows;
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = responseCode,
                ResponseMessage = $"{responseCode} scripted response",
                Stream = success ? new YencStream(new MemoryStream([], writable: false)) : null,
            };
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            SingularRequests++;
            if (SingularException != null)
                throw SingularException(segmentId.ToString());

            return Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = SingularResponseCode,
                ResponseMessage = $"{SingularResponseCode} scripted stat <{segmentId}>",
                ArticleExists = SingularResponseCode == (int)UsenetResponseType.ArticleExists,
            });
        }

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

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
