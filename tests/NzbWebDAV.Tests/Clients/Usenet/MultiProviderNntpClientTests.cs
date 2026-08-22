using System.IO;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public class MultiProviderNntpClientTests
{
    [Fact]
    public void BeginStreamTraceRangeScope_RestoresNestedContext()
    {
        var rangeA = new StreamTraceRangeContext(Guid.NewGuid(), 1);
        var rangeB = new StreamTraceRangeContext(Guid.NewGuid(), 2);

        Assert.Null(MultiProviderNntpClient.CurrentStreamTraceRange);
        using (MultiProviderNntpClient.BeginStreamTraceRangeScope(rangeA))
        {
            Assert.Equal(rangeA, MultiProviderNntpClient.CurrentStreamTraceRange);
            using (MultiProviderNntpClient.BeginStreamTraceRangeScope(rangeB))
            {
                Assert.Equal(rangeB, MultiProviderNntpClient.CurrentStreamTraceRange);
            }
            Assert.Equal(rangeA, MultiProviderNntpClient.CurrentStreamTraceRange);
        }
        Assert.Null(MultiProviderNntpClient.CurrentStreamTraceRange);
    }

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
    public async Task BatchResponse_SameProviderRetry_DoesNotRecordFailoverRescue()
    {
        // Primary batch miss, then same host succeeds on the singular re-probe.
        // That is a self-retry, not a backup rescue — Overview must not count it.
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection, host: "news.example")],
            metricsWriter: writer);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch.Responses[0]).ResponseType);

        Assert.Equal(0, writer.Stats.QueuedFailoverMisses);
        Assert.Empty(writer.SnapshotQueuedEvents(MetricsWriter.FailoverSaveEventKind));
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_SameProviderTimeoutRetry_DoesNotRecordFailoverRescue()
    {
        var writer = new MetricsWriter();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
            FaultBatchResponsesWith = () => new TimeoutException("nntp read timed out"),
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(connection, host: "solo.example")],
            metricsWriter: writer);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch.Responses[0]).ResponseType);

        Assert.Equal(0, writer.Stats.QueuedFailoverMisses);
        Assert.Empty(writer.SnapshotQueuedEvents(MetricsWriter.FailoverSaveEventKind));
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_TimeoutWithBackup_SkipsPrimaryReprobeAndUsesBackup()
    {
        // After an exhausted streaming/read timeout the primary already burned its
        // per-segment retry budget. Re-probing it before backups delays failover (#723).
        var writer = new MetricsWriter();
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
            FaultBatchResponsesWith = () => new TimeoutException(
                "Timeout executing nntp BODY command after 4 attempts."),
            SingularException = _ => new TimeoutException(
                "Timeout executing nntp BODY command after 4 attempts."),
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
            [
                CreateProvider(primary, host: "a.example"),
                CreateProvider(backup, host: "b.example", providerType: ProviderType.BackupOnly),
            ],
            metricsWriter: writer);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch.Responses[0]).ResponseType);

        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);
        Assert.Equal(1, writer.Stats.QueuedFailoverMisses);
        Assert.Single(writer.SnapshotQueuedEvents(MetricsWriter.FailoverSaveEventKind));
    }

    [Fact]
    public async Task DecodedBodyAsync_OpenPrimaryCircuit_UsesBackupOnly()
    {
        var openPrimary = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var healthyBackup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(openPrimary, host: "a.example", circuitBreaker: OpenBreaker("a.example")),
            CreateProvider(healthyBackup, host: "b.example", providerType: ProviderType.BackupOnly),
        ]);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(0, openPrimary.SingularRequests);
        Assert.Equal(1, healthyBackup.SingularRequests);
    }

    [Fact]
    public async Task DecodedBodyAsync_CrossProviderRescue_RecordsFailoverMiss()
    {
        var writer = new MetricsWriter();
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
            [
                CreateProvider(primary, host: "a.example"),
                CreateProvider(backup, host: "b.example", providerType: ProviderType.BackupOnly),
            ],
            metricsWriter: writer,
            cascadeEnabled: () => true,
            retryPrimaryOnMiss: () => false);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.True(backup.SingularRequests >= 1);
        Assert.Equal(1, writer.Stats.QueuedFailoverMisses);
        Assert.Single(writer.SnapshotQueuedEvents(MetricsWriter.FailoverSaveEventKind));
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
        var firstStream = results[0].Stream;
        if (firstStream != null)
            await firstStream.DisposeAsync();

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
    public async Task StatAsync_ArgumentException_LogsDiagnosticContextAndFallsBack()
    {
        const string segmentId = "<diagnostic-context@example>";
        const string parameterName = "segmentId-context";
        var events = await CaptureLogsAsync(async () =>
        {
            var primary = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularException = _ => new ArgumentException("Segment ID was invalid.", parameterName),
            };
            var backup = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularResponseCode = (int)UsenetResponseType.ArticleExists,
            };
            using var client = new MultiProviderNntpClient(
            [
                CreateProvider(primary, host: "primary.example"),
                CreateProvider(backup, host: "backup.example", providerType: ProviderType.BackupOnly),
            ]);

            var response = await client.StatAsync(segmentId, CancellationToken.None);
            Assert.True(response.ArticleExists);
        });

        var warning = Assert.Single(events, IsUnclassifiedFetchWarning);
        Assert.Equal("primary.example", PropertyText(warning, "ProviderKey"));
        Assert.Equal("stat", PropertyText(warning, "Operation"));
        Assert.Equal(typeof(ArgumentException).FullName, PropertyText(warning, "ExceptionType"));
        Assert.Equal("Segment ID was invalid. (Parameter 'segmentId-context')", PropertyText(warning, "Reason"));
        Assert.Equal(parameterName, PropertyText(warning, "ParameterName"));
        Assert.Equal("0", PropertyText(warning, "AttemptIndex"));
        Assert.Matches("^[0-9A-F]{12}$", PropertyText(warning, "SegmentHash"));
        Assert.Equal(typeof(ArgumentException).FullName, PropertyText(warning, "InnermostExceptionType"));
        Assert.Equal(PropertyText(warning, "Reason"), PropertyText(warning, "InnermostReason"));

        var stack = Assert.Single(events, e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.StartsWith("Unclassified Usenet segment fetch failure stack", StringComparison.Ordinal));
        Assert.NotNull(stack.Exception);
        Assert.Equal("stat", PropertyText(stack, "Operation"));
    }

    [Fact]
    public async Task StatAsync_RepeatedUnclassifiedFailure_IsThrottled()
    {
        const string parameterName = "segmentId-repeat";
        var events = await CaptureLogsAsync(async () =>
        {
            var primary = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularException = _ => new ArgumentException("Repeated failure.", parameterName),
            };
            var backup = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularResponseCode = (int)UsenetResponseType.ArticleExists,
            };
            using var client = new MultiProviderNntpClient(
            [
                CreateProvider(primary, host: "primary.example"),
                CreateProvider(backup, host: "backup.example", providerType: ProviderType.BackupOnly),
            ]);

            await client.StatAsync("<repeat-one@example>", CancellationToken.None);
            await client.StatAsync("<repeat-two@example>", CancellationToken.None);
        });

        Assert.Single(events, IsUnclassifiedFetchWarning);
        Assert.Single(events, e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.StartsWith("Unclassified Usenet segment fetch failure stack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnclassifiedFailures_WithDifferentOperationsOrParameters_AreNotCoalesced()
    {
        var events = await CaptureLogsAsync(async () =>
        {
            await RunFailingRequestAsync("segmentId-operation", body: false);
            await RunFailingRequestAsync("segmentId-operation", body: true);
            await RunFailingRequestAsync("otherParameter", body: false);
        });

        var warnings = events.Where(IsUnclassifiedFetchWarning).ToArray();
        Assert.Equal(3, warnings.Length);
        Assert.Contains(warnings, e =>
            PropertyText(e, "Operation") == "stat" &&
            PropertyText(e, "ParameterName") == "segmentId-operation");
        Assert.Contains(warnings, e =>
            PropertyText(e, "Operation") == "body" &&
            PropertyText(e, "ParameterName") == "segmentId-operation");
        Assert.Contains(warnings, e =>
            PropertyText(e, "Operation") == "stat" &&
            PropertyText(e, "ParameterName") == "otherParameter");

        static async Task RunFailingRequestAsync(string parameterName, bool body)
        {
            var primary = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularException = _ => new ArgumentException("Failure for throttling key.", parameterName),
            };
            var backup = new ScriptedNntpClient
            {
                BatchResponseCode = 222,
                SingularResponseCode = body
                    ? (int)UsenetResponseType.ArticleRetrievedBodyFollows
                    : (int)UsenetResponseType.ArticleExists,
            };
            using var client = new MultiProviderNntpClient(
            [
                CreateProvider(primary, host: "primary.example"),
                CreateProvider(backup, host: "backup.example", providerType: ProviderType.BackupOnly),
            ]);

            if (body)
            {
                var response = await client.DecodedBodyAsync("<operation@example>", CancellationToken.None);
                await response.Stream!.DisposeAsync();
            }
            else
            {
                await client.StatAsync("<parameter@example>", CancellationToken.None);
            }
        }
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
            "segment", (result, _) => callbacks.Add(result), CancellationToken.None);

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
    public async Task ArticleMissCache_SecondFetch_SkipsKnownMissingProvider()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var missing = new ScriptedNntpClient
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
            CreateProvider(missing, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);
        Assert.Equal(1, cache.Entries);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(2, backup.SingularRequests);
        Assert.True(cache.Hits >= 1);
    }

    [Fact]
    public async Task ArticleMissCache_SharedStorageGroup_SkipsSiblingOnSecondFetch()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
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
        var otherGroup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(first, host: "a.example", storageGroup: "omicron"),
            CreateProvider(sibling, host: "b.example", storageGroup: "omicron"),
            CreateProvider(otherGroup, host: "c.example", storageGroup: "eweka"),
        ], articleMissCache: cache);

        // First request: same-group sibling already skipped via request-local missingGroups.
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
        Assert.Equal(1, otherGroup.SingularRequests);

        // Second request: group-scoped negative cache skips both omicron providers.
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
        Assert.Equal(2, otherGroup.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_DifferentStorageGroup_StillProbes()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
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
        ], articleMissCache: cache);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(1, other.SingularRequests);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(2, other.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_AfterTtlExpiry_ReprobesProvider()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetArticleMissCacheTtlSeconds,
                ConfigValue = "30",
            },
        ]);
        var cache = new ArticleMissNegativeCache(config);
        var missing = new ScriptedNntpClient
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
            CreateProvider(missing, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        await client.DecodedBodyAsync("segment", CancellationToken.None);
        Assert.Equal(1, missing.SingularRequests);

        var key = ArticleMissNegativeCache.BuildKey("segment", "a.example", null);
        cache.MarkMissingAtForTests(key, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(31));

        await client.DecodedBodyAsync("segment", CancellationToken.None);
        Assert.Equal(2, missing.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_Timeout_DoesNotCreateCacheEntry()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var flaky = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new TimeoutException("nntp timeout"),
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(flaky, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(0, cache.Entries);

        await client.DecodedBodyAsync("segment", CancellationToken.None);
        Assert.True(flaky.SingularRequests >= 2);
        Assert.Equal(0, cache.Entries);
    }

    [Fact]
    public async Task ArticleMissCache_Batch_FirstPrimary430_StillReprobesOnce()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
            [CreateProvider(primary, host: "a.example")], articleMissCache: cache);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, primary.SingularRequests);
        Assert.Equal(0, cache.Entries);
    }

    [Fact]
    public async Task ArticleMissCache_Batch_CachedPrimaryMiss_SkipsReprobe()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey("segment", "a.example", null));

        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
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
        ], articleMissCache: cache);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);
        Assert.True(cache.Hits >= 1);
    }

    [Fact]
    public async Task ArticleMissCache_Batch_ReprobeMiss_MarksCache()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
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
        ], articleMissCache: cache);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch.Responses[0]).ResponseType);
        Assert.Equal(1, primary.SingularRequests);
        Assert.Equal(1, cache.Entries);

        var batch2 = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await batch2.Responses[0]).ResponseType);
        Assert.Equal(1, primary.SingularRequests);
        Assert.Equal(2, backup.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_Stat_SkipsKnownMissingProvider()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var missing = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 430,
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = (int)UsenetResponseType.ArticleExists,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(missing, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        var first = await client.StatAsync("segment", CancellationToken.None);
        Assert.True(first.ArticleExists);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);
        Assert.Equal(1, cache.Entries);

        var second = await client.StatAsync("segment", CancellationToken.None);
        Assert.True(second.ArticleExists);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(2, backup.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_StreamingPath_SkipsKnownMissingProvider()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var missing = new ScriptedNntpClient
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
            CreateProvider(missing, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", (_, _) => { }, CancellationToken.None)).ResponseType);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", (_, _) => { }, CancellationToken.None)).ResponseType);
        Assert.Equal(1, missing.SingularRequests);
        Assert.Equal(2, backup.SingularRequests);
    }


    [Fact]
    public async Task ArticleMissCache_NetworkFailure_DoesNotCreateCacheEntry()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var flaky = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new IOException("connection reset"),
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(flaky, host: "a.example"),
            CreateProvider(backup, host: "b.example"),
        ], articleMissCache: cache);

        Assert.Equal(
            UsenetResponseType.ArticleRetrievedBodyFollows,
            (await client.DecodedBodyAsync("segment", CancellationToken.None)).ResponseType);
        Assert.Equal(0, cache.Entries);

        await client.DecodedBodyAsync("segment", CancellationToken.None);
        Assert.True(flaky.SingularRequests >= 2);
        Assert.Equal(0, cache.Entries);
    }

    [Fact]
    public async Task ArticleMissCache_SkippedProbe_DoesNotRecordOkFetch()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        var writer = new MetricsWriter();
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
        ], metricsWriter: writer, articleMissCache: cache);

        var first = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await first.Stream!.DisposeAsync();
        var fetchesAfterFirst = writer.Stats.QueuedFetches;

        var second = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await second.Stream!.DisposeAsync();

        Assert.Equal(fetchesAfterFirst + 1, writer.Stats.QueuedFetches);
        Assert.Equal(1, primary.SingularRequests);
    }

    [Fact]
    public async Task ArticleMissCache_AllProvidersCached_ThrowsArticleNotFound()
    {
        var config = new ConfigManager();
        var cache = new ArticleMissNegativeCache(config);
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey("segment", "a.example", null));
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey("segment", "b.example", null));

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
        ], articleMissCache: cache);

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.DecodedBodyAsync("segment", CancellationToken.None));
        Assert.Equal("segment", exception.SegmentId);
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(0, backup.SingularRequests);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.DecodedBodyAsync("segment", (_, _) => { }, CancellationToken.None));
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(0, backup.SingularRequests);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.StatAsync("segment", CancellationToken.None));
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(0, backup.SingularRequests);
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
    public async Task CascadeMode_PreservesPriorityWhenSpareCapacityIsComparable()
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
            CreateProvider(primaryConnection, host: "primary.example", maxConnections: 4, priority: 0),
            CreateProvider(secondaryConnection, host: "secondary.example", maxConnections: 4, priority: 1),
        ], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, primaryConnection.SingularRequests);
        Assert.Equal(0, secondaryConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_PriorityBeatsLargerIdlePool()
    {
        var primaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var largerLowerPriority = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        // Reproduce the field failure: Priority 0 / max 20 was losing to Priority 3 / max 32
        // while both were idle because absolute spare outweighed Priority.
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primaryConnection, host: "primary.example", maxConnections: 20, priority: 0),
            CreateProvider(largerLowerPriority, host: "large.example", maxConnections: 32, priority: 3),
        ], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, primaryConnection.SingularRequests);
        Assert.Equal(0, largerLowerPriority.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_PrefersIdlePeerWhenPrimaryIsContended()
    {
        var primaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var idleConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var primary = CreateProvider(primaryConnection, host: "primary.example", maxConnections: 8, priority: 0);
        var idle = CreateProvider(idleConnection, host: "idle.example", maxConnections: 8, priority: 1);
        // Leave primary with a single spare connection (12.5% <= 25%) so thin-spare demotes it.
        for (var i = 0; i < 7; i++)
            primary.ReservePending();
        using var client = new MultiProviderNntpClient([primary, idle], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(0, primaryConnection.SingularRequests);
        Assert.Equal(1, idleConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_KeepsPrimaryAboveThinSpareThreshold()
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
        var primary = CreateProvider(primaryConnection, host: "primary.example", maxConnections: 8, priority: 0);
        var secondary = CreateProvider(secondaryConnection, host: "secondary.example", maxConnections: 8, priority: 1);
        // 3/8 unreserved = 37.5% spare — just above the 25% thin-spare band.
        for (var i = 0; i < 5; i++)
            primary.ReservePending();
        using var client = new MultiProviderNntpClient([primary, secondary], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, primaryConnection.SingularRequests);
        Assert.Equal(0, secondaryConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_YieldsAtThinSpareThreshold()
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
        var primary = CreateProvider(primaryConnection, host: "primary.example", maxConnections: 8, priority: 0);
        var secondary = CreateProvider(secondaryConnection, host: "secondary.example", maxConnections: 8, priority: 1);
        // 2/8 unreserved = exactly 25%, so the idle next-priority peer wins.
        for (var i = 0; i < 6; i++)
            primary.ReservePending();
        using var client = new MultiProviderNntpClient([primary, secondary], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(0, primaryConnection.SingularRequests);
        Assert.Equal(1, secondaryConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_TieBreakUsesSpareFractionNotAbsoluteSpare()
    {
        var smallerConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var largerConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var larger = CreateProvider(largerConnection, host: "large.example", maxConnections: 32, priority: 0);
        var smaller = CreateProvider(smallerConnection, host: "small.example", maxConnections: 20, priority: 0);
        // Equal utilization (50% spare) but unequal absolute spare. Absolute spare would
        // pick the larger pool (16 > 10). Fraction tie-break keeps list order, so the
        // smaller pool listed first must win.
        for (var i = 0; i < 16; i++)
            larger.ReservePending();
        for (var i = 0; i < 10; i++)
            smaller.ReservePending();
        using var client = new MultiProviderNntpClient([smaller, larger], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, smallerConnection.SingularRequests);
        Assert.Equal(0, largerConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_SkipsFullySaturatedPrimary()
    {
        var primaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var backupConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var primary = CreateProvider(primaryConnection, host: "primary.example", maxConnections: 4, priority: 0);
        var backup = CreateProvider(backupConnection, host: "backup.example", maxConnections: 4, priority: 1);
        for (var i = 0; i < 4; i++)
            primary.ReservePending();
        using var client = new MultiProviderNntpClient([primary, backup], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(0, primaryConnection.SingularRequests);
        Assert.Equal(1, backupConnection.SingularRequests);
    }

    [Fact]
    public async Task CascadeMode_PooledTierStillPrecedesBackupOnly()
    {
        var backupConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        var primaryConnection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        // Backup Only with a larger pool must not leapfrog a pooled Priority 0 primary.
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(
                backupConnection,
                host: "backup.example",
                maxConnections: 32,
                priority: 0,
                providerType: ProviderType.BackupOnly),
            CreateProvider(primaryConnection, host: "primary.example", maxConnections: 20, priority: 0),
        ], cascadeEnabled: () => true);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream!.DisposeAsync();

        Assert.Equal(1, primaryConnection.SingularRequests);
        Assert.Equal(0, backupConnection.SingularRequests);
    }

    [Fact]
    public async Task BatchFailover_StartsNextSegmentWithoutWaitingForPriorBodyCompletion()
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
            DeferSingularCompletion = true,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "primary.example", maxConnections: 4),
            CreateProvider(backup, host: "backup.example", maxConnections: 4),
        ]);

        UsenetDecodedBodyResponse? first = null;
        UsenetDecodedBodyResponse? second = null;
        try
        {
            var batch = await client.DecodedBodiesAsync(
                ["seg-0", "seg-1"], onConnectionReadyAgain: null, CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // Both fallback starts must proceed while segment 0's body callback is still deferred.
            while (backup.SingularRequests < 2)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, timeout.Token);
            }

            Assert.Equal(2, backup.SingularRequests);

            var firstTask = batch.Responses[0];
            var secondTask = batch.Responses[1];
            Assert.True(firstTask.IsCompleted);
            Assert.True(secondTask.IsCompleted);
            first = await firstTask;
            second = await secondTask;
            Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, first.ResponseType);
            Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, second.ResponseType);
            Assert.Equal("seg-0", first.SegmentId);
            Assert.Equal("seg-1", second.SegmentId);
        }
        finally
        {
            backup.CompletePendingSingularRequests();
            if (first?.Stream != null) await first.Stream.DisposeAsync();
            if (second?.Stream != null) await second.Stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task BatchResponse_WithCleanNotFound_SkipsPrimaryReprobeWhenDisabled()
    {
        var primary = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        var backup = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient(
        [
            CreateProvider(primary, host: "primary.example"),
            CreateProvider(backup, host: "backup.example"),
        ], retryPrimaryOnMiss: () => false);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(0, primary.SingularRequests);
        Assert.Equal(1, backup.SingularRequests);
    }

    [Fact]
    public void ClassifyException_ArticleNotFound_ReturnsMissing()
    {
        // Singular BODY/HEAD and streaming paths throw on a definitive 430/451 instead of
        // returning a response; it must classify the same as a response-path miss.
        var exception = new UsenetArticleNotFoundException("<seg@example>", "430 No Such Article");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Missing, status);
    }

    [Fact]
    public void ClassifyException_ArticleNotFoundWrapped_StillReturnsMissing()
    {
        var inner = new UsenetArticleNotFoundException("<seg@example>", "430 No Such Article");
        var wrapped = new InvalidOperationException("stream read failed", inner);
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
        Assert.Equal(SegmentFetch.FetchStatus.Missing, status);
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
    public void ClassifyException_InvalidData_ReturnsCorrupt()
    {
        // UsenetSharp yEnc header/decode failures escape as InvalidDataException.
        var status = MultiProviderNntpClient.ClassifyException(new InvalidDataException("CRC mismatch"));
        Assert.Equal(SegmentFetch.FetchStatus.Corrupt, status);
    }

    [Fact]
    public void ClassifyException_InvalidDataWrapped_StillReturnsCorruptNotNetwork()
    {
        // InvalidDataException derives from IOException; the Corrupt case must win
        // over the IOException -> Network case regardless of wrapping.
        var wrapped = new InvalidOperationException("stream read failed", new InvalidDataException("bad yenc"));
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
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
    public void ClassifyException_UsenetNotConnected_ReturnsNetwork()
    {
        var exception = new UsenetNotConnectedException("The NNTP connection closed before the article body was read.");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Network, status);
    }

    [Fact]
    public void ClassifyException_UsenetConnection_ReturnsNetwork()
    {
        var exception = new UsenetConnectionException("Server responded: 502") { ResponseCode = 502 };
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
    public void ClassifyException_UnexpectedResponse_ReturnsProtocol()
    {
        var exception = new UsenetUnexpectedResponseException("<seg@example>", "400 too much time between commands");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Protocol, status);
    }

    [Fact]
    public void ClassifyException_UnexpectedResponseWrapped_StillReturnsProtocol()
    {
        var inner = new UsenetUnexpectedResponseException("<seg@example>", "400 idle timeout");
        var wrapped = new InvalidOperationException("stream read failed", inner);
        var status = MultiProviderNntpClient.ClassifyException(wrapped);
        Assert.Equal(SegmentFetch.FetchStatus.Protocol, status);
    }

    [Fact]
    public void ClassifyException_UsenetProtocol_ReturnsProtocol()
    {
        var exception = new UsenetProtocolException("Invalid NNTP response: missing article headers.");
        var status = MultiProviderNntpClient.ClassifyException(exception);
        Assert.Equal(SegmentFetch.FetchStatus.Protocol, status);
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

    [Fact]
    public void ClassifyException_CorruptArticleInsideAggregateException_StillReturnsCorrupt()
    {
        // Task/NNTP wrappers often surface AggregateException; the known cause must
        // still be found among InnerExceptions, not only InnerException.
        var inner = new UsenetCorruptArticleException("segment", "provider", new Exception("bad crc"));
        var aggregate = new AggregateException("one or more errors", inner);
        var status = MultiProviderNntpClient.ClassifyException(aggregate);
        Assert.Equal(SegmentFetch.FetchStatus.Corrupt, status);
    }

    private static bool IsUnclassifiedFetchWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.MessageTemplate.Text.StartsWith(
            "Unclassified Usenet segment fetch failure.", StringComparison.Ordinal);

    private static string PropertyText(LogEvent logEvent, string name)
    {
        if (!logEvent.Properties.TryGetValue(name, out var value))
            return "";
        return value is ScalarValue { Value: { } raw }
            ? raw.ToString() ?? ""
            : value.ToString();
    }

    private static async Task<IReadOnlyList<LogEvent>> CaptureLogsAsync(Func<Task> act)
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await act().ConfigureAwait(false);
        }
        finally
        {
            Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    internal static MultiConnectionNntpClient CreateProvider(
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

    internal sealed class ScriptedNntpClient : NntpClient
    {
        public required int BatchResponseCode { get; init; }
        public int SingularResponseCode { get; init; } = 222;
        public Func<int, Exception?>? BatchException { get; init; }
        public Func<Exception>? FaultBatchResponsesWith { get; init; }
        public Func<string, Exception>? SingularException { get; init; }
        public bool DeferSingularCompletion { get; init; }
        public int BatchRequests { get; private set; }
        public int SingularRequests { get; private set; }
        private readonly Queue<ArticleBodyCompletionHandler> _pendingSingularCallbacks = new();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchRequests++;
            var exception = BatchException?.Invoke(BatchRequests);
            if (exception != null)
                throw exception;

            var responses = segmentIds
                .Select(segmentId =>
                {
                    if (FaultBatchResponsesWith != null)
                        return Task.FromException<UsenetDecodedBodyResponse>(FaultBatchResponsesWith());
                    return Task.FromResult(CreateResponse(segmentId, BatchResponseCode));
                })
                .ToArray();
            // Faulted per-segment tasks are resolved by MultiProvider failover; do not claim
            // Retrieved here or the batch coordinator will treat the body as already done.
            onConnectionReadyAgain?.Invoke(
                FaultBatchResponsesWith != null
                    ? ArticleBodyResult.NotRetrieved
                    : ToArticleBodyResult(BatchResponseCode));
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
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
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
