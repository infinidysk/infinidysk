using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// STRM sidecars publish only after the finalize commit succeeds: a failed
/// SaveChanges leaves no sidecars, a post-commit sidecar failure never
/// re-finalizes the import, and duplicate mark-failed imports publish nothing.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class StrmPublishAfterCommitTests : IAsyncLifetime
{
    private const string Category = "other";
    private const string JobName = "movie-job";
    private const string VideoFileName = "movie.mkv";

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-strm-cfg-{Guid.NewGuid():N}");
    private readonly string _strmDir =
        Path.Join(Path.GetTempPath(), $"nzbdav-strm-out-{Guid.NewGuid():N}");
    private readonly string _segmentId = $"strm-seg-{Guid.NewGuid():N}@example.com";
    private readonly byte[] _payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    private string? _previousConfigPath;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Directory.CreateDirectory(_strmDir);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        await Task.CompletedTask;
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_strmDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task SuccessfulImport_PublishesStrmAfterCommit()
    {
        await using var context = CreateContext();
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);

        var strmPath = Path.Join(_strmDir, Category, JobName, VideoFileName + ".strm");
        Assert.True(File.Exists(strmPath), $"expected sidecar at {strmPath}");
        Assert.Contains("/view/", await File.ReadAllTextAsync(strmPath));

        var videoItem = await context.Items.AsNoTracking()
            .SingleAsync(x => x.Name == VideoFileName);
        Assert.Equal(strmPath, videoItem.GeneratedStrmPath);
    }

    [Fact]
    public async Task FinalizeSaveFailure_LeavesNoStrmFiles()
    {
        await using var context = CreateContext(new ThrowOnQueueDeleteSaveInterceptor());
        var queueItem = await SeedQueueItemAsync(context);

        await ProcessAsync(context, CreateConfig(), queueItem);

        // Both the success finalize and the failed-history finalize fail at the
        // simulated commit boundary; the pre-fix code wrote sidecars inside the
        // staged operations, before that boundary.
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.True(await context.QueueItems.AsNoTracking().AnyAsync(q => q.Id == queueItem.Id));
    }

    [Fact]
    public async Task StrmFailureAfterCommit_KeepsCompletedHistory()
    {
        // Force the sidecar write to fail: a regular file blocks the per-job
        // directory the writer needs to create.
        await File.WriteAllTextAsync(Path.Join(_strmDir, Category), "blocked");

        await using var context = CreateContext();
        var queueItem = await SeedQueueItemAsync(context);

        // Must not throw: a sidecar failure is logged, not re-finalized.
        await ProcessAsync(context, CreateConfig(), queueItem);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(historyItem.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, historyItem.DownloadStatus);
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DuplicateMarkFailed_PublishesNoStrmFiles()
    {
        await using var context = CreateContext();
        var categoryFolder = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, Category, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        var mountFolder = DavItem.New(
            Guid.NewGuid(), categoryFolder, JobName, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, Guid.NewGuid(), null);
        context.Items.Add(categoryFolder);
        context.Items.Add(mountFolder);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var queueItem = await SeedQueueItemAsync(context);

        var config = CreateConfig();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiDuplicateNzbBehavior, ConfigValue = "mark-failed" },
        ]);
        await ProcessAsync(context, config, queueItem);

        context.ChangeTracker.Clear();
        var historyItem = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Contains("Duplicate nzb", historyItem.FailMessage);
        Assert.Empty(Directory.GetFiles(_strmDir, "*.strm", SearchOption.AllDirectories));
    }

    private ConfigManager CreateConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiImportStrategy, ConfigValue = "strm" },
            new ConfigItem { ConfigName = ConfigKeys.ApiCompletedDownloadsDir, ConfigValue = _strmDir },
            new ConfigItem { ConfigName = ConfigKeys.GeneralBaseUrl, ConfigValue = "http://localhost:3000" },
            new ConfigItem { ConfigName = ConfigKeys.ApiStrmKey, ConfigValue = "test-strm-key" },
        ]);
        return config;
    }

    private DavDatabaseContext CreateContext(params IInterceptor[] extraInterceptors) =>
        new(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .AddInterceptors(extraInterceptors)
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options);

    private async Task<QueueItem> SeedQueueItemAsync(DavDatabaseContext context)
    {
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = $"{JobName}.nzb",
            JobName = JobName,
            NzbFileSize = CreateNzbBytes().Length,
            TotalSegmentBytes = _payload.Length,
            Category = Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return await context.QueueItems.SingleAsync(q => q.Id == queueItem.Id);
    }

    private async Task ProcessAsync(DavDatabaseContext context, ConfigManager config, QueueItem queueItem)
    {
        await using var nzbStream = new MemoryStream(CreateNzbBytes());
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            new DavDatabaseClient(context),
            new ScriptedVideoNntpClient(VideoFileName, _segmentId, _payload),
            config,
            new WebsocketManager(),
            new Progress<int>(),
            CancellationToken.None);
        await processor.ProcessAsync();
    }

    private byte[] CreateNzbBytes() => Encoding.UTF8.GetBytes(
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="&quot;{VideoFileName}&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="{_payload.Length}" number="1">{_segmentId}</segment>
            </segments>
          </file>
        </nzb>
        """);

    /// <summary>
    /// Simulates a finalize-commit failure: any save that deletes a queue item
    /// (success or failed-history finalize) faults with a DbUpdateException.
    /// </summary>
    private sealed class ThrowOnQueueDeleteSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<QueueItem>()
                    .Any(e => e.State == EntityState.Deleted))
                throw new DbUpdateException("simulated finalize commit failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Serves a single-segment video file from memory with a correct yEnc header
    /// filename, so the import pipeline deobfuscates to <see cref="VideoFileName"/>.
    /// </summary>
    private sealed class ScriptedVideoNntpClient(
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
}
