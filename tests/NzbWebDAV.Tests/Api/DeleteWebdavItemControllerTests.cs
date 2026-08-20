using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.DeleteWebdavItem;
using NzbWebDAV.Api.Controllers.DeleteWebdavItemPreview;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
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
public sealed class DeleteWebdavItemControllerTests : IAsyncLifetime
{
    private const string ApiKey = "delete-webdav-item-test-key";

    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-del-webdav-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private string? _previousApiKey;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;
    private ServiceProvider? _serviceProvider;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", ApiKey);

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
            new ConfigItem
            {
                ConfigName = ConfigKeys.WebdavEnforceReadonly,
                ConfigValue = "false",
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
        _serviceProvider?.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("/content/tv/Show.S01E01/episode.mkv", "tv", "Show.S01E01", true)]
    [InlineData("/content/tv/Show.S01E01/episode.mkv", "tv", "Other.Show", false)]
    [InlineData("/content/tv/Show.S01E01/episode.mkv", "movies", "Show.S01E01", false)]
    [InlineData("/nzbs/tv/Show.S01E01.nzb", "tv", "Show.S01E01", false)]
    [InlineData("/content/tv", "tv", "Show.S01E01", false)]
    public void HasInProgressDownload_MatchesCategoryAndJobName(
        string path,
        string category,
        string jobName,
        bool expected)
    {
        var inProgress = new[]
        {
            new QueueManager.InProgressQueueItemSnapshot(
                new QueueItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    FileName = $"{jobName}.nzb",
                    JobName = jobName,
                    NzbFileSize = 1,
                    TotalSegmentBytes = 1,
                    Category = category,
                    Priority = QueueItem.PriorityOption.Normal,
                    PostProcessing = QueueItem.PostProcessingOption.None,
                },
                42,
                true),
        };

        Assert.Equal(expected, DeleteWebdavItemSupport.HasInProgressDownload(path, inProgress));
    }

    [Theory]
    [InlineData("/nzbs")]
    [InlineData("/completed-symlinks")]
    [InlineData("/.ids")]
    public async Task DeleteAsync_RootScopedPaths_Returns400(string path)
    {
        var result = await InvokeDeleteAsync(path);
        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task DeleteAsync_ProtectedContentCategory_Returns403()
    {
        var category = NewDir(Guid.NewGuid(), DavItem.ContentFolder, "tv");
        _context.Items.Add(category);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await InvokeDeleteAsync(category.Path);
        Assert.Equal(403, GetStatusCode(result));
        var response = ReadResponse<BaseApiResponse>(result);
        Assert.Equal("Cannot delete protected item.", response.Error);
    }

    [Fact]
    public async Task DeleteAsync_File_RemovesRowAndPrunesUnreferencedHistory()
    {
        var (_, file, historyId) = await SeedContentReleaseAsync();

        var result = await InvokeDeleteAsync(file.Path);
        Assert.Equal(200, GetStatusCode(result));
        Assert.True(ReadResponse<BaseApiResponse>(result).Status);

        Assert.Null(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == file.Id));
        Assert.Null(await _context.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task DeleteAsync_FileNameWithPercentSequence_RemovesRow()
    {
        var (_, file, historyId) = await SeedContentReleaseAsync(
            fileName: "S02E14.Such.Sweet.Sorrow%2C.Part.2.1080.mkv");

        var result = await InvokeDeleteAsync(file.Path);
        Assert.Equal(200, GetStatusCode(result));
        Assert.True(ReadResponse<BaseApiResponse>(result).Status);

        Assert.Null(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == file.Id));
        Assert.Null(await _context.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId));
    }

    [Fact]
    public async Task PreviewAsync_FileNameWithPercentSequence_FindsItem()
    {
        var (_, file, _) = await SeedContentReleaseAsync(
            fileName: "S02E14.Such.Sweet.Sorrow%2C.Part.2.1080.mkv");

        var result = await InvokePreviewAsync(file.Path);
        Assert.Equal(200, GetStatusCode(result));
        var response = ReadResponse<DeleteWebdavItemPreviewResponse>(result);
        Assert.True(response.Status);
        Assert.Equal(1, response.FileCount);
    }

    [Fact]
    public async Task DeleteAsync_PercentEncodedSpaceFallback_RemovesRow()
    {
        var (_, file, _) = await SeedContentReleaseAsync(fileName: "file name.mkv");
        var encodedPath = file.Path.Replace("file name.mkv", "file%20name.mkv", StringComparison.Ordinal);

        var result = await InvokeDeleteAsync(encodedPath);
        Assert.Equal(200, GetStatusCode(result));
        Assert.Null(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == file.Id));
    }

    [Fact]
    public async Task DeleteAsync_File_KeepsHistoryWhenSiblingRemains()
    {
        var (jobDir, file, historyId) = await SeedContentReleaseAsync();
        var sibling = DavItem.New(
            Guid.NewGuid(),
            jobDir,
            "sibling.mkv",
            200,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            historyId,
            Guid.NewGuid());
        _context.Items.Add(sibling);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await InvokeDeleteAsync(file.Path);
        Assert.Equal(200, GetStatusCode(result));

        Assert.Null(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == file.Id));
        Assert.NotNull(await _context.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.NotNull(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sibling.Id));
    }

    [Fact]
    public async Task DeleteAsync_RecursiveTree_DeletesAllLevelsInOneCall()
    {
        var (jobDir, _, _) = await SeedContentReleaseAsync();
        var seasonDir = NewDir(Guid.NewGuid(), jobDir, "Season 01");
        var nestedFile = DavItem.New(
            Guid.NewGuid(),
            seasonDir,
            "nested.mkv",
            50,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            Guid.NewGuid());
        _context.Items.AddRange(seasonDir, nestedFile);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ids = new[] { jobDir.Id, seasonDir.Id, nestedFile.Id };
        var result = await InvokeDeleteAsync(jobDir.Path);
        Assert.Equal(200, GetStatusCode(result));

        foreach (var id in ids)
            Assert.Null(await _context.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id));
    }

    [Fact]
    public async Task DeleteAsync_InProgressQueueItem_Returns409()
    {
        using var cts = new CancellationTokenSource();
        var (jobDir, _, _) = await SeedContentReleaseAsync(category: "tv", jobName: "Show.S01E01");
        AddInProgressForTest(
            _queueManager,
            new QueueItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "Show.S01E01.nzb",
                JobName = "Show.S01E01",
                NzbFileSize = 1,
                TotalSegmentBytes = 1,
                Category = "tv",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
            },
            cts);

        var result = await InvokeDeleteAsync(jobDir.Path);
        Assert.Equal(409, GetStatusCode(result));
        var response = ReadResponse<BaseApiResponse>(result);
        Assert.Equal("Cannot delete while a matching download is in progress.", response.Error);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_FiresBlobCleanupTrigger()
    {
        var blobId = Guid.NewGuid();
        await using (var stream = new MemoryStream("blob-payload"u8.ToArray()))
            await BlobStore.WriteBlob(blobId, stream);

        var category = await EnsureCategoryAsync("movies");
        var jobDir = NewDir(Guid.NewGuid(), category, "Blob.Job");
        var file = DavItem.New(
            Guid.NewGuid(),
            jobDir,
            "blob.mkv",
            10,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            null,
            blobId);
        _context.Items.AddRange(jobDir, file);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _context.Items
            .Where(x => x.Id == file.Id)
            .ExecuteDeleteAsync();

        Assert.NotNull(await _context.BlobCleanupItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == blobId));
    }

    [Fact]
    public async Task DeleteAsync_StalePath_Returns404()
    {
        var result = await InvokeDeleteAsync("/content/tv/missing-release/episode.mkv");
        Assert.Equal(404, GetStatusCode(result));
        var response = ReadResponse<BaseApiResponse>(result);
        Assert.Equal("Item not found.", response.Error);
    }

    [Fact]
    public async Task DeleteAsync_ReadonlySetting_Returns403()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.WebdavEnforceReadonly,
                ConfigValue = "true",
            },
        ]);

        var (_, file, _) = await SeedContentReleaseAsync();
        var result = await InvokeDeleteAsync(file.Path);
        Assert.Equal(403, GetStatusCode(result));
        var response = ReadResponse<BaseApiResponse>(result);
        Assert.Contains("read-only", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_ReturnsCountsBytesAndHistory()
    {
        var historyId = Guid.NewGuid();
        var nzbBlobId = Guid.NewGuid();
        var category = await EnsureCategoryAsync("tv");
        var jobDir = NewDir(Guid.NewGuid(), category, "Show.S01E01");
        var seasonDir = NewDir(Guid.NewGuid(), jobDir, "Season 01");
        var fileOne = DavItem.New(
            Guid.NewGuid(),
            seasonDir,
            "S01E01.mkv",
            100,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            historyId,
            Guid.NewGuid(),
            nzbBlobId);
        var fileTwo = DavItem.New(
            Guid.NewGuid(),
            seasonDir,
            "S01E02.mkv",
            250,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            historyId,
            Guid.NewGuid(),
            nzbBlobId);
        _context.HistoryItems.Add(CreateHistory(historyId, "Show.S01E01.nzb", "tv", nzbBlobId));
        _context.NzbNames.Add(new NzbName { Id = nzbBlobId, FileName = "Show.S01E01.nzb" });
        _context.Items.AddRange(jobDir, seasonDir, fileOne, fileTwo);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await InvokePreviewAsync(jobDir.Path);
        Assert.Equal(200, GetStatusCode(result));
        var response = ReadResponse<DeleteWebdavItemPreviewResponse>(result);

        Assert.True(response.Status);
        Assert.Equal(2, response.FileCount);
        Assert.Equal(2, response.DirCount);
        Assert.Equal(350, response.TotalBytes);
        Assert.Equal(1, response.LinkedHistoryCount);
    }

    [Fact]
    public async Task PruneUnreferencedHistoryItemsAsync_EnqueuesHistoryCleanupAndNzbBlobCleanup()
    {
        var historyId = Guid.NewGuid();
        var nzbBlobId = Guid.NewGuid();
        _context.HistoryItems.Add(CreateHistory(historyId, "orphan.nzb", "tv", nzbBlobId));
        _context.NzbNames.Add(new NzbName { Id = nzbBlobId, FileName = "orphan.nzb" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var pruned = await _dbClient.PruneUnreferencedHistoryItemsAsync([historyId]);
        await _context.SaveChangesAsync();

        Assert.Equal([historyId], pruned);
        Assert.Null(await _context.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.NotNull(await _context.HistoryCleanupItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.NotNull(await _context.NzbBlobCleanupItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == nzbBlobId));
    }

    private DeleteWebdavItemController CreateDeleteController()
    {
        var controller = new DeleteWebdavItemController(
            _dbClient,
            _configManager,
            _queueManager,
            _websocketManager);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    private DeleteWebdavItemPreviewController CreatePreviewController()
    {
        var controller = new DeleteWebdavItemPreviewController(
            _dbClient,
            _configManager,
            _queueManager);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    private HttpContext CreateHttpContext()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_configManager)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = _serviceProvider };
        httpContext.Request.Headers["x-api-key"] = ApiKey;
        return httpContext;
    }

    private async Task<IActionResult> InvokeDeleteAsync(string path)
    {
        var controller = CreateDeleteController();
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.HttpContext.Request.Form = new FormCollection(
            new Dictionary<string, StringValues> { ["path"] = path });
        return await controller.HandleApiRequest();
    }

    private async Task<IActionResult> InvokePreviewAsync(string path)
    {
        var controller = CreatePreviewController();
        controller.HttpContext.Request.Method = HttpMethods.Get;
        controller.HttpContext.Request.QueryString = new QueryString($"?path={Uri.EscapeDataString(path)}");
        return await controller.HandleApiRequest();
    }

    private async Task<(DavItem JobDir, DavItem File, Guid HistoryId)> SeedContentReleaseAsync(
        string category = "tv",
        string jobName = "Show.S01E01",
        string fileName = "episode.mkv",
        long fileSize = 100)
    {
        var historyId = Guid.NewGuid();
        var nzbBlobId = Guid.NewGuid();
        var categoryDir = await EnsureCategoryAsync(category);
        var jobDir = NewDir(Guid.NewGuid(), categoryDir, jobName);
        var file = DavItem.New(
            Guid.NewGuid(),
            jobDir,
            fileName,
            fileSize,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            historyId,
            Guid.NewGuid(),
            nzbBlobId);
        _context.HistoryItems.Add(CreateHistory(historyId, $"{jobName}.nzb", category, nzbBlobId));
        _context.NzbNames.Add(new NzbName { Id = nzbBlobId, FileName = $"{jobName}.nzb" });
        _context.Items.AddRange(jobDir, file);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (jobDir, file, historyId);
    }

    private async Task<DavItem> EnsureCategoryAsync(string category)
    {
        var existing = await _context.Items.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ParentId == DavItem.ContentFolder.Id && x.Name == category);
        if (existing is not null)
            return existing;

        var categoryDir = NewDir(Guid.NewGuid(), DavItem.ContentFolder, category);
        _context.Items.Add(categoryDir);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return categoryDir;
    }

    private static DavItem NewDir(Guid id, DavItem parent, string name) =>
        DavItem.New(
            id,
            parent,
            name,
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);

    private static HistoryItem CreateHistory(Guid id, string fileName, string category, Guid nzbBlobId) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = fileName,
        JobName = Path.GetFileNameWithoutExtension(fileName),
        Category = category,
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 100,
        DownloadTimeSeconds = 1,
        NzbBlobId = nzbBlobId,
    };

    private static int GetStatusCode(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        return objectResult.StatusCode ?? 0;
    }

    private static T ReadResponse<T>(IActionResult result) where T : class
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        return Assert.IsType<T>(objectResult.Value);
    }

    private static void AddInProgressForTest(
        QueueManager queueManager, QueueItem queueItem, CancellationTokenSource cancellation)
    {
        var managerType = typeof(QueueManager);
        var inProgressField = managerType.GetField("_inProgress", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QueueManager._inProgress not found");
        var inProgressDict = inProgressField.GetValue(queueManager)
            ?? throw new InvalidOperationException("QueueManager._inProgress was null");
        var itemType = managerType.GetNestedType("InProgressQueueItem", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InProgressQueueItem type not found");
        var inProgressItem = Activator.CreateInstance(itemType, nonPublic: true)
            ?? throw new InvalidOperationException("Failed to create InProgressQueueItem");

        itemType.GetProperty("QueueItem")!.SetValue(inProgressItem, queueItem);
        itemType.GetProperty("ProgressPercentage")!.SetValue(inProgressItem, 10);
        itemType.GetProperty("ProcessingTask")!.SetValue(inProgressItem, Task.CompletedTask);
        itemType.GetProperty("CompletionSignal")!.SetValue(inProgressItem, new TaskCompletionSource());
        itemType.GetProperty("CancellationTokenSource")!.SetValue(inProgressItem, cancellation);
        itemType.GetProperty("QueueDownloadContext")!.SetValue(inProgressItem, new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 1,
        });

        var tryAdd = inProgressDict.GetType().GetMethod("TryAdd")
            ?? throw new InvalidOperationException("TryAdd not found on _inProgress dictionary");
        if (!(bool)tryAdd.Invoke(inProgressDict, [queueItem.Id, inProgressItem])!)
            throw new InvalidOperationException("Failed to add in-progress queue item for test");
    }
}
