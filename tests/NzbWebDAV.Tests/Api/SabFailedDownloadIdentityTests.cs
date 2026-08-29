using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Api.SabControllers.GetHistory;
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
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// Regression coverage for https://github.com/infinidysk/infinidysk/issues/922 —
/// Sonarr correlates downloads to its own grabs by the nzo_id returned from
/// mode=addfile, and its failed-download handling only works when mode=history
/// later reports the FAILED item under that same nzo_id and category.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class SabFailedDownloadIdentityTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-failed-identity-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;
    private QueueManager _queueManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
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
        _queueManager = QueueManager.CreateForTests(
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
    public async Task FailedDownload_SabHistoryReportsSameNzoIdCategoryAndFailedStatus()
    {
        const string category = "tv";
        const string fileName = "Some.Show.S01E01.1080p.WEB-DL.nzb";

        // 1. Grab: the Arr sends mode=addfile and records the returned nzo_id.
        var addResponse = await CreateAddFileController().AddFileAsync(CreateRequest(fileName, category));
        Assert.True(addResponse.Status);
        var nzoId = Assert.Single(addResponse.NzoIds);

        // 2. Processing fails non-retryably: the important file's first segment is
        //    missing on every provider (empty fake = DMCA'd/expired content).
        await ProcessToFailureAsync(nzoId);

        // 3. mode=history must echo the SAME nzo_id and category with Failed status.
        var response = await CreateGetHistoryController()
            .GetHistoryAsync(BuildHistoryRequest($"?nzo_ids={nzoId}"));

        var slot = Assert.Single(response.History.Slots);
        Assert.Equal(nzoId, slot.NzoId);
        Assert.Equal(category, slot.Category);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, slot.Status);
        Assert.False(string.IsNullOrWhiteSpace(slot.FailMessage));
    }

    [Fact]
    public async Task FailedDownload_HistoryIsScopedToTheRequestingCategory()
    {
        const string category = "tv";
        var addResponse = await CreateAddFileController()
            .AddFileAsync(CreateRequest("Some.Show.S01E02.1080p.WEB-DL.nzb", category));
        var nzoId = Assert.Single(addResponse.NzoIds);
        await ProcessToFailureAsync(nzoId);

        // The Arr polls history filtered by its configured download-client category,
        // so a failure is only visible to an instance whose category matches.
        var sameCategory = await CreateGetHistoryController()
            .GetHistoryAsync(BuildHistoryRequest("?category=tv"));
        Assert.Contains(sameCategory.History.Slots, s => s.NzoId == nzoId);

        var otherCategory = await CreateGetHistoryController()
            .GetHistoryAsync(BuildHistoryRequest("?category=tv4k"));
        Assert.DoesNotContain(otherCategory.History.Slots, s => s.NzoId == nzoId);
    }

    private async Task ProcessToFailureAsync(string nzoId)
    {
        _context.ChangeTracker.Clear();
        var queueItem = await _context.QueueItems.SingleAsync(q => q.Id == Guid.Parse(nzoId));
        await using var nzbStream = BlobStore.ReadBlob(queueItem.Id)!;
        using var healthCheckConnectionGate = new HealthCheckConnectionGate(_configManager);
        var processor = new QueueItemProcessor(
            queueItem,
            nzbStream,
            _dbClient,
            new FakeNntpClient(new Dictionary<string, byte[]>()),
            _configManager,
            _websocketManager,
            new Progress<int>(),
            healthCheckConnectionGate,
            CancellationToken.None);
        await processor.ProcessAsync();

        Assert.Empty(await _context.QueueItems.AsNoTracking().ToListAsync());
        var historyItem = await _context.HistoryItems.AsNoTracking().SingleAsync();
        Assert.Equal(Guid.Parse(nzoId), historyItem.Id);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        _context.ChangeTracker.Clear();
    }

    private GetHistoryRequest BuildHistoryRequest(string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        return new GetHistoryRequest(httpContext, _configManager);
    }

    private AddFileController CreateAddFileController() =>
        new(new DefaultHttpContext(), _dbClient, _queueManager, _configManager, _websocketManager);

    private GetHistoryController CreateGetHistoryController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager, new ProviderUsageTracker());

    private static AddFileRequest CreateRequest(string fileName, string category)
    {
        var nzb = """
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="&quot;Some.Show.S01E01.1080p.WEB-DL.mkv&quot; yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="100" number="1">issue-922-missing-segment@example.com</segment>
                </segments>
              </file>
            </nzb>
            """;
        return new AddFileRequest
        {
            FileName = fileName,
            ContentType = "application/x-nzb",
            NzbFileStream = new MemoryStream(Encoding.UTF8.GetBytes(nzb)),
            Category = category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            CancellationToken = CancellationToken.None,
        };
    }
}
