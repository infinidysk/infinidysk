using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Queue;

public class QueueRarTimeoutTests
{
    [Fact]
    public async Task LazyRarTimeout_ThrowsRetryable_DoesNotReturnNull()
    {
        var first = FileInfoFor("vol.rar", "first@example.com");
        var trailing = FileInfoFor("vol.r00", "r00@example.com");
        using var client = new ScriptedRarNntpClient { ThrowOnGetFileStream = true };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            new LazyRarProcessor([first, trailing], client, password: null, CancellationToken.None)
                .ProcessAsync());

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(1, client.GetFileStreamCalls);
        Assert.Equal(["first@example.com"], client.GetFileStreamIds.ToArray());
    }

    [Fact]
    public async Task LazyRarTimeout_DoesNotDoubleWrapRetryable()
    {
        var inner = new TimeoutException("already classified");
        var first = FileInfoFor("vol.rar", "first@example.com");
        using var client = new ScriptedRarNntpClient
        {
            GetFileStreamException = new RetryableDownloadException("provider retry", inner),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            new LazyRarProcessor([first], client, password: null, CancellationToken.None)
                .ProcessAsync());

        Assert.Same(inner, ex.InnerException);
        Assert.Equal("provider retry", ex.Message);
    }

    [Fact]
    public async Task LazyRarCancellation_StaysCancellation()
    {
        var first = FileInfoFor("vol.rar", "first@example.com");
        using var client = new ScriptedRarNntpClient();
        client.Serve("first@example.com", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LazyRarProcessor([first], client, password: null, cts.Token).ProcessAsync());
    }

    [Fact]
    public async Task LazyRarCorruptHeaders_ReturnsNullForEagerFallback()
    {
        var first = FileInfoFor("vol.rar", "first@example.com");
        using var client = new ScriptedRarNntpClient();
        client.Serve("first@example.com", Encoding.ASCII.GetBytes("not a rar archive"));

        var result = await new LazyRarProcessor([first], client, password: null, CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task EagerFirstTimeout_CancelsStartedSiblings()
    {
        using var client = new ScriptedRarNntpClient
        {
            HoldId = "hold@example.com",
            FailId = "fail@example.com",
        };
        using var workerCts = new CancellationTokenSource();
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
        var processors = new BaseProcessor[]
        {
            new RarProcessor(FileInfoFor("vol.rar", "hold@example.com"), client, null, processorCts.Token),
            new RarProcessor(FileInfoFor("vol.r00", "fail@example.com"), client, null, processorCts.Token),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            RunEagerStageAsync(processors, processorCts, workerCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.IsType<TimeoutException>(ex.InnerException);
        await client.HoldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(processorCts.IsCancellationRequested);
        Assert.Equal(2, client.GetFileStreamCalls);
    }

    [Fact]
    public async Task EagerRetryable_IsNotDoubleWrapped()
    {
        var inner = new TimeoutException("inner");
        using var client = new ScriptedRarNntpClient
        {
            HoldId = "hold@example.com",
            FailId = "fail@example.com",
            BodyException = new RetryableDownloadException("already retryable", inner),
        };
        using var workerCts = new CancellationTokenSource();
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
        var processors = new BaseProcessor[]
        {
            new RarProcessor(FileInfoFor("vol.rar", "hold@example.com"), client, null, processorCts.Token),
            new RarProcessor(FileInfoFor("vol.r00", "fail@example.com"), client, null, processorCts.Token),
        };

        var ex = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            RunEagerStageAsync(processors, processorCts, workerCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal("already retryable", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public async Task EagerExternalCancellation_DoesNotBecomeRetryable()
    {
        using var client = new ScriptedRarNntpClient { HoldAll = true };
        using var workerCts = new CancellationTokenSource();
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
        var processors = new BaseProcessor[]
        {
            new RarProcessor(FileInfoFor("vol.rar", "a@example.com"), client, null, processorCts.Token),
            new RarProcessor(FileInfoFor("vol.r00", "b@example.com"), client, null, processorCts.Token),
        };

        var run = RunEagerStageAsync(processors, processorCts, workerCts.Token);
        await client.HoldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Both processors must be inside BODY before we cancel, otherwise one may
        // still be starting and the enumerator can observe a different exception.
        await client.WaitForStartedBodiesAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        await workerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task NonRarProcessorCancellation_IsNotSwallowed()
    {
        using var workerCts = new CancellationTokenSource();
        using var processorCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(workerCts.Token);
        await processorCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QueueItemProcessor.RunProcessorWithRarSiblingAbortAsync(
                new CancellingNonRarProcessor(processorCts.Token),
                new Progress<int>(),
                processorCts,
                workerCts.Token));
    }

    private sealed class CancellingNonRarProcessor(CancellationToken token) : BaseProcessor
    {
        public override Task<Result?> ProcessAsync()
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<Result?>(null);
        }
    }

    private static Task<List<BaseProcessor.Result?>> RunEagerStageAsync(
        IReadOnlyList<BaseProcessor> processors,
        ContextualCancellationTokenSource processorCts,
        CancellationToken workerToken)
    {
        var progress = new Progress<int>();
        return processors
            .Select(processor => QueueItemProcessor.RunProcessorWithRarSiblingAbortAsync(
                processor, progress, processorCts, workerToken))
            .WithConcurrencyAsync(2, workerToken)
            .GetAllAsync(ct: workerToken);
    }

    private static GetFileInfosStep.FileInfo FileInfoFor(string fileName, string messageId)
    {
        return new GetFileInfosStep.FileInfo
        {
            NzbFile = new NzbFile
            {
                Subject = $"\"{fileName}\" yEnc",
                Segments = { new NzbSegment { MessageId = messageId, Bytes = 1024 } },
            },
            FileName = fileName,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            FileSize = 1024,
            IsRar = true,
        };
    }

    /// <summary>
    /// Scripted NNTP client for import-time RAR header tests. GetFileStream is counted
    /// so lazy-timeout tests can prove eager per-volume scans never started.
    /// </summary>
    internal sealed class ScriptedRarNntpClient : NntpClient
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private readonly Dictionary<string, byte[]> _segments = new(StringComparer.Ordinal);
        private int _startedBodies;

        private int _getFileStreamCalls;
        public int GetFileStreamCalls => Volatile.Read(ref _getFileStreamCalls);
        public ConcurrentQueue<string> GetFileStreamIds { get; } = new();
        public bool ThrowOnGetFileStream { get; init; }
        public Exception? GetFileStreamException { get; init; }
        public string? HoldId { get; init; }
        public string? FailId { get; init; }
        public bool HoldAll { get; init; }
        public Exception? BodyException { get; init; }
        public TaskCompletionSource HoldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Serve(string segmentId, byte[] bytes) => _segments[segmentId] = bytes;

        public Task WaitForStartedBodiesAsync(int count)
        {
            return Task.Run(async () =>
            {
                while (Volatile.Read(ref _startedBodies) < count)
                    await Task.Delay(10);
            });
        }

        public override NzbFileStream GetFileStream(
            NzbFile nzbFile,
            long fileSize,
            int articleBufferSize,
            bool usePipelinedBodyRequests = true,
            string? fileName = null,
            InFlightArticleBudget? inFlightArticleBudget = null,
            bool useContainerAwareFill = false,
            int streamingBodyBatchWidth = 4,
            HashSet<string>? knownCorruptSegmentIds = null,
            IReadOnlySet<int>? knownMissingSegmentIndices = null)
        {
            Interlocked.Increment(ref _getFileStreamCalls);
            var id = nzbFile.Segments[0].MessageId;
            GetFileStreamIds.Enqueue(id);
            if (GetFileStreamException is not null)
                throw GetFileStreamException;
            if (ThrowOnGetFileStream)
                throw new TimeoutException("simulated RAR header timeout");
            return base.GetFileStream(
                nzbFile,
                fileSize,
                articleBufferSize,
                usePipelinedBodyRequests,
                fileName,
                inFlightArticleBudget,
                useContainerAwareFill,
                streamingBodyBatchWidth,
                knownCorruptSegmentIds,
                knownMissingSegmentIndices);
        }

        public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct) =>
            Task.FromResult(file.Segments.Count == 0 ? 0L : 1024L);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            Interlocked.Increment(ref _startedBodies);

            if (HoldAll || key == HoldId)
            {
                HoldStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    HoldCancelled.TrySetResult();
                    throw;
                }
            }

            if (key == FailId)
            {
                await HoldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                throw BodyException ?? new TimeoutException("simulated RAR header timeout");
            }

            if (BodyException is not null)
                throw BodyException;

            if (_segments.TryGetValue(key, out var bytes))
            {
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                return CreateBodyResponse(key, bytes);
            }

            throw new NotSupportedException($"Unexpected BODY for {key}");
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = segmentId.ToString();
            var payload = _segments.TryGetValue(key, out var bytes) ? bytes : Rar4Magic;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article",
                Stream = CreateCachedStream(payload),
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R"),
                    },
                },
            });
        }

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
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var responses = segmentIds
                .Select(id => DecodedBodyAsync(id, cancellationToken))
                .ToArray();
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override void Dispose()
        {
        }

        private static UsenetDecodedBodyResponse CreateBodyResponse(string key, byte[] bytes) =>
            new()
            {
                SegmentId = key,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body",
                Stream = CreateCachedStream(bytes),
            };

        private static CachedYencStream CreateCachedStream(byte[] payload)
        {
            var headers = new UsenetYencHeader
            {
                FileName = "vol.rar",
                FileSize = payload.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartOffset = 0,
                PartSize = payload.Length,
            };
            return new CachedYencStream(headers, new MemoryStream(payload, writable: false));
        }
    }
}

public sealed class QueueRarTimeoutQueueItemTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-rar-timeout-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task LazyRarTimeout_SetsPauseUntilAndLeavesItemQueued()
    {
        var firstId = $"rar-timeout-first-{Guid.NewGuid():N}@example.com";
        var secondId = $"rar-timeout-second-{Guid.NewGuid():N}@example.com";
        using var client = new QueueRarTimeoutTests.ScriptedRarNntpClient { ThrowOnGetFileStream = true };
        var queueItem = await SeedQueueItemAsync(firstId, secondId);

        await using var nzbStream = CreateNzbStream(firstId, secondId);
        var config = new ConfigManager();
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            _dbClient,
            client,
            config,
            new WebsocketManager(),
            new Progress<int>(),
            healthCheckConnectionGate,
            CancellationToken.None);
        await processor.ProcessAsync();

        _context.ChangeTracker.Clear();
        var remaining = await _context.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(queueItem.Id, remaining.Id);
        Assert.NotNull(remaining.PauseUntil);
        Assert.True(remaining.PauseUntil > DateTime.Now.AddSeconds(-1));
        Assert.Empty(await _context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Equal(1, client.GetFileStreamCalls);
        Assert.Equal([firstId], client.GetFileStreamIds.ToArray());
    }

    [Fact]
    public async Task ExternalCancellation_DoesNotSetPauseUntil()
    {
        var firstId = $"rar-cancel-first-{Guid.NewGuid():N}@example.com";
        var secondId = $"rar-cancel-second-{Guid.NewGuid():N}@example.com";
        using var client = new QueueRarTimeoutTests.ScriptedRarNntpClient();
        var queueItem = await SeedQueueItemAsync(firstId, secondId);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var nzbStream = CreateNzbStream(firstId, secondId);
        var config = new ConfigManager();
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            _dbClient,
            client,
            config,
            new WebsocketManager(),
            new Progress<int>(),
            healthCheckConnectionGate,
            cts.Token);
        await processor.ProcessAsync();

        _context.ChangeTracker.Clear();
        var remaining = await _context.QueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(queueItem.Id, remaining.Id);
        Assert.Null(remaining.PauseUntil);
        Assert.Empty(await _context.HistoryItems.AsNoTracking().ToListAsync());
    }

    private async Task<QueueItem> SeedQueueItemAsync(string firstId, string secondId)
    {
        var nzbBytes = Encoding.UTF8.GetBytes(CreateNzbXml(firstId, secondId));
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "archive.nzb",
            JobName = "archive",
            NzbFileSize = nzbBytes.Length,
            TotalSegmentBytes = 2048,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        _context.QueueItems.Add(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.QueueItems.SingleAsync(q => q.Id == queueItem.Id);
    }

    private static MemoryStream CreateNzbStream(string firstId, string secondId) =>
        new(Encoding.UTF8.GetBytes(CreateNzbXml(firstId, secondId)));

    private static string CreateNzbXml(string firstId, string secondId) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;vol.rar&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="1024" number="1">{firstId}</segment>
            </segments>
          </file>
          <file subject="&quot;vol.r00&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="1024" number="1">{secondId}</segment>
            </segments>
          </file>
        </nzb>
        """;
}

[Collection(nameof(GlobalLoggerCollection))]
public sealed class QueueRarTimeoutLoggingTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-rar-timeout-log-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task LazyRarTimeout_LogsWarningWithoutErrorStack()
    {
        var firstId = $"rar-log-first-{Guid.NewGuid():N}@example.com";
        var secondId = $"rar-log-second-{Guid.NewGuid():N}@example.com";
        using var client = new QueueRarTimeoutTests.ScriptedRarNntpClient { ThrowOnGetFileStream = true };
        var nzb = Encoding.UTF8.GetBytes(
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="&quot;vol.rar&quot; yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="1024" number="1">{firstId}</segment>
                </segments>
              </file>
              <file subject="&quot;vol.r00&quot; yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="1024" number="1">{secondId}</segment>
                </segments>
              </file>
            </nzb>
            """);
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "archive.nzb",
            JobName = "archive-log",
            NzbFileSize = nzb.Length,
            TotalSegmentBytes = 2048,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        _context.QueueItems.Add(queueItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        queueItem = await _context.QueueItems.SingleAsync(q => q.Id == queueItem.Id);

        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            await using var nzbStream = new MemoryStream(nzb);
            var config = new ConfigManager();
            using var healthCheckConnectionGate = new HealthCheckConnectionGate(config);
            var processor = new QueueItemProcessor(
                queueItem,
                nzbStream,
                _dbClient,
                client,
                config,
                new WebsocketManager(),
                new Progress<int>(),
                healthCheckConnectionGate,
                CancellationToken.None);
            await processor.ProcessAsync();
        }
        finally
        {
            Log.Logger = previous;
        }

        var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
        Assert.Contains(warnings, e => e.MessageTemplate.Text.Contains("Provider connection issue"));
        Assert.Contains(warnings, e => e.Properties.ContainsKey("Reason"));
        Assert.DoesNotContain(
            sink.Events.Where(e => HasJobName(e, queueItem.JobName)),
            e => e.Level >= LogEventLevel.Error);
    }

    private static bool HasJobName(LogEvent logEvent, string jobName)
        => logEvent.Properties.TryGetValue("JobName", out var value)
           && value is ScalarValue { Value: string name }
           && name == jobName;

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
}
