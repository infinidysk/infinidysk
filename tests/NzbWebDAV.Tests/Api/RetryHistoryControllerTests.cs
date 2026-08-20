using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Api.SabControllers.RetryHistory;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class RetryHistoryControllerTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-retry-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);

        _websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            usenet,
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task RetryHistoryAsync_FailedItemWithBlob_CreatesNewQueueItemAndKeepsHistory()
    {
        const string fileName = "Show.S01E01.nzb";
        const string category = "tv";
        var historyId = Guid.NewGuid();
        await SeedFailedHistoryAsync(historyId, fileName, category, writeBlob: true);

        var response = await CreateController().RetryHistoryAsync(await CreateRequest(historyId));

        Assert.True(response.Status);
        var newId = Guid.Parse(response.NzoId!);
        Assert.NotEqual(historyId, newId);

        Assert.NotNull(await _context.HistoryItems.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId));
        var queueItem = await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Id == newId);
        Assert.Equal(fileName, queueItem.FileName);
        Assert.Equal(category, queueItem.Category);
        Assert.Equal("test-indexer", queueItem.IndexerName);
        Assert.Equal("group-key", queueItem.ContentGroupKey);
        Assert.NotNull(BlobStore.ReadBlob(newId));
    }

    [Fact]
    public async Task RetryHistoryAsync_MissingBlob_ThrowsBadRequest()
    {
        var historyId = Guid.NewGuid();
        await SeedFailedHistoryAsync(historyId, "Missing.Blob.nzb", "tv", writeBlob: false);

        var request = await CreateRequest(historyId);
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => CreateController().RetryHistoryAsync(request));
        Assert.Equal("The NZB file could not be found.", ex.Message);
        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RetryHistoryAsync_CompletedHistory_ThrowsBadRequest()
    {
        var historyId = Guid.NewGuid();
        await SeedHistoryAsync(
            historyId,
            "Completed.Show.nzb",
            "tv",
            HistoryItem.DownloadStatusOption.Completed,
            writeBlob: true);

        var request = await CreateRequest(historyId);
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => CreateController().RetryHistoryAsync(request));
        Assert.Equal("Only failed history items can be retried.", ex.Message);
        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RetryHistoryAsync_UnknownId_ThrowsBadRequest()
    {
        var request = await CreateRequest(Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => CreateController().RetryHistoryAsync(request));
        Assert.Equal("History item not found.", ex.Message);
    }

    [Fact]
    public async Task RetryHistoryRequest_MalformedValue_ThrowsBadRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?value=not-a-guid");
        context.Request.Body = Stream.Null;

        var ex = await Assert.ThrowsAsync<ApiValidationException>(() => RetryHistoryRequest.New(context));
        Assert.Equal("Missing or invalid value (nzo_id).", ex.Message);
    }

    [Fact]
    public async Task RetryHistoryRequest_MissingValue_ThrowsBadRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = Stream.Null;

        var ex = await Assert.ThrowsAsync<ApiValidationException>(() => RetryHistoryRequest.New(context));
        Assert.Equal("Missing or invalid value (nzo_id).", ex.Message);
    }

    [Fact]
    public async Task RetryHistoryAsync_WhileAlreadyQueued_ReplacesExistingQueueItem()
    {
        const string fileName = "Already.Queued.nzb";
        const string category = "tv";
        var historyId = Guid.NewGuid();
        await SeedFailedHistoryAsync(historyId, fileName, category, writeBlob: true);

        var existingQueueId = Guid.NewGuid();
        _context.QueueItems.Add(new QueueItem
        {
            Id = existingQueueId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            FileName = fileName,
            JobName = "Already.Queued",
            NzbFileSize = 10,
            TotalSegmentBytes = 10,
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        });
        _context.NzbNames.Add(new NzbName { Id = existingQueueId, FileName = fileName });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await CreateController().RetryHistoryAsync(await CreateRequest(historyId));

        Assert.True(response.Status);
        var newId = Guid.Parse(response.NzoId!);
        Assert.NotEqual(existingQueueId, newId);
        Assert.Null(await _context.QueueItems.AsNoTracking()
            .SingleOrDefaultAsync(q => q.Id == existingQueueId));
        Assert.Equal(newId, (await _context.QueueItems.AsNoTracking()
            .SingleAsync(q => q.Category == category && q.FileName == fileName)).Id);
        Assert.NotNull(await _context.HistoryItems.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId));
    }


    [Fact]
    public async Task RetryHistoryAsync_PartialFailure_ReturnsSucceededAndFailed()
    {
        var okId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        await SeedFailedHistoryAsync(okId, "Ok.nzb", "tv", writeBlob: true);

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?value={okId}&value={badId}");
        context.Request.Body = Stream.Null;
        var request = await RetryHistoryRequest.New(context);
        var response = await CreateController().RetryHistoryAsync(request);

        Assert.True(response.Status);
        Assert.NotNull(response.NzoIds);
        Assert.Single(response.NzoIds!);
        Assert.NotNull(response.Failed);
        Assert.Single(response.Failed!);
    }

    private RetryHistoryController CreateController() =>
        new(
            new DefaultHttpContext(),
            _dbClient,
            _queueManager,
            _configManager,
            _websocketManager);

    private static async Task<RetryHistoryRequest> CreateRequest(Guid nzoId)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?value={nzoId}");
        context.Request.Body = Stream.Null;
        return await RetryHistoryRequest.New(context);
    }

    private Task SeedFailedHistoryAsync(Guid id, string fileName, string category, bool writeBlob) =>
        SeedHistoryAsync(id, fileName, category, HistoryItem.DownloadStatusOption.Failed, writeBlob);

    private async Task SeedHistoryAsync(
        Guid id,
        string fileName,
        string category,
        HistoryItem.DownloadStatusOption status,
        bool writeBlob)
    {
        if (writeBlob)
        {
            var nzb = """
                <?xml version="1.0" encoding="utf-8"?>
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                  <file subject="test">
                    <groups><group>alt.binaries.test</group></groups>
                    <segments>
                      <segment bytes="100" number="1">seg@example.com</segment>
                    </segments>
                  </file>
                </nzb>
                """;
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nzb));
            await BlobStore.WriteBlob(id, stream);
        }

        _context.HistoryItems.Add(new HistoryItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = category,
            DownloadStatus = status,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 5,
            FailMessage = status == HistoryItem.DownloadStatusOption.Failed
                ? "Timeout reading from NNTP stream."
                : null,
            NzbBlobId = id,
            IndexerName = "test-indexer",
            ContentGroupKey = "group-key",
        });
        _context.NzbNames.Add(new NzbName { Id = id, FileName = fileName });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
