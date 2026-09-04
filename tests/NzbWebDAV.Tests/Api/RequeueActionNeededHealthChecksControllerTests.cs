using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.RequeueActionNeededHealthChecks;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public sealed class RequeueActionNeededHealthChecksControllerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-requeue-action-needed-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;

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
        _configManager = new ConfigManager();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task RequeueAsync_QueuesEachLiveFileWhoseLatestResultNeedsActionOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var historyId = Guid.NewGuid();
        _context.HistoryItems.Add(NewHistoryItem(historyId));

        var duplicateActionNeeded = NewItem("duplicate.mkv", now.AddDays(1), historyId);
        var staleActionNeeded = NewItem("stale.mkv", now.AddDays(1));
        var latestActionNeeded = NewItem("latest.mkv", now.AddDays(1));
        var urgent = NewItem("urgent.mkv", DateTimeOffset.UnixEpoch);
        var alreadyQueued = NewItem("queued.mkv", HealthCheckService.ForcedRecheckSentinel);
        var directory = NewItem("directory", now.AddDays(1), type: DavItem.ItemType.Directory);
        _context.Items.AddRange(
            duplicateActionNeeded,
            staleActionNeeded,
            latestActionNeeded,
            urgent,
            alreadyQueued,
            directory);
        _context.HealthCheckResults.AddRange(
            NewResult(duplicateActionNeeded, now.AddMinutes(-2), HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(duplicateActionNeeded, now.AddMinutes(-1), HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(staleActionNeeded, now.AddMinutes(-2), HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(staleActionNeeded, now.AddMinutes(-1), HealthCheckResult.RepairAction.None),
            NewResult(latestActionNeeded, now.AddMinutes(-2), HealthCheckResult.RepairAction.None),
            NewResult(latestActionNeeded, now.AddMinutes(-1), HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(urgent, now, HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(alreadyQueued, now, HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(directory, now, HealthCheckResult.RepairAction.ActionNeeded),
            NewResult(Guid.NewGuid(), "/content/deleted.mkv", now, HealthCheckResult.RepairAction.ActionNeeded));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await InvokeAsync();

        Assert.Equal(2, response.RequeuedCount);
        var updated = await _context.Items.ToDictionaryAsync(x => x.Id);
        Assert.Equal(
            HealthCheckService.ForcedRecheckSentinel,
            updated[duplicateActionNeeded.Id].NextHealthCheck);
        Assert.Equal(
            HealthCheckService.ForcedRecheckSentinel,
            updated[latestActionNeeded.Id].NextHealthCheck);
        Assert.NotEqual(
            HealthCheckService.ForcedRecheckSentinel,
            updated[staleActionNeeded.Id].NextHealthCheck);
        Assert.Equal(DateTimeOffset.UnixEpoch, updated[urgent.Id].NextHealthCheck);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, updated[alreadyQueued.Id].NextHealthCheck);
        Assert.NotEqual(HealthCheckService.ForcedRecheckSentinel, updated[directory.Id].NextHealthCheck);
    }

    [Fact]
    public async Task RequeueAsync_WhenRepairsDisabled_ReturnsConflictWithoutUpdatingItems()
    {
        var item = NewItem("disabled.mkv", DateTimeOffset.UtcNow.AddDays(1));
        _context.Items.Add(item);
        _context.HealthCheckResults.Add(NewResult(
            item,
            DateTimeOffset.UtcNow,
            HealthCheckResult.RepairAction.ActionNeeded));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "false" },
        ]);

        var result = await InvokeActionAsync(HttpMethods.Post);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var updated = await _context.Items.SingleAsync(x => x.Id == item.Id);
        Assert.NotEqual(HealthCheckService.ForcedRecheckSentinel, updated.NextHealthCheck);
    }

    [Fact]
    public async Task RequeueAsync_GetRequestReturnsMethodNotAllowedWithoutUpdatingItems()
    {
        var item = NewItem("method.mkv", DateTimeOffset.UtcNow.AddDays(1));
        _context.Items.Add(item);
        _context.HealthCheckResults.Add(NewResult(
            item,
            DateTimeOffset.UtcNow,
            HealthCheckResult.RepairAction.ActionNeeded));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await InvokeActionAsync(HttpMethods.Get);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.StatusCode);
        var updated = await _context.Items.SingleAsync(x => x.Id == item.Id);
        Assert.NotEqual(HealthCheckService.ForcedRecheckSentinel, updated.NextHealthCheck);
    }

    private async Task<RequeueActionNeededHealthChecksResponse> InvokeAsync()
    {
        var result = await InvokeActionAsync(HttpMethods.Post);
        return Assert.IsType<OkObjectResult>(result).Value as RequeueActionNeededHealthChecksResponse
            ?? throw new Xunit.Sdk.XunitException("Expected requeue action-needed response.");
    }

    private Task<IActionResult> InvokeActionAsync(string method)
    {
        var controller = new TestController(_dbClient, _configManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Request.Method = method;
        return controller.InvokeAsync();
    }

    private static DavItem NewItem(
        string name,
        DateTimeOffset? nextHealthCheck,
        Guid? historyItemId = null,
        DavItem.ItemType type = DavItem.ItemType.UsenetFile)
    {
        var item = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            fileSize: type == DavItem.ItemType.UsenetFile ? 100 : null,
            type,
            type == DavItem.ItemType.UsenetFile
                ? DavItem.ItemSubType.NzbFile
                : DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId,
            fileBlobId: null);
        item.NextHealthCheck = nextHealthCheck;
        return item;
    }

    private static HistoryItem NewHistoryItem(Guid id) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = "linked.nzb",
        JobName = "linked",
        Category = "movies",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
    };

    private static HealthCheckResult NewResult(
        DavItem item,
        DateTimeOffset createdAt,
        HealthCheckResult.RepairAction repairStatus) =>
        NewResult(item.Id, item.Path, createdAt, repairStatus);

    private static HealthCheckResult NewResult(
        Guid davItemId,
        string path,
        DateTimeOffset createdAt,
        HealthCheckResult.RepairAction repairStatus) => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            DavItemId = davItemId,
            Path = path,
            Result = repairStatus == HealthCheckResult.RepairAction.None
                ? HealthCheckResult.HealthResult.Healthy
                : HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = repairStatus,
        };

    private sealed class TestController(
        DavDatabaseClient dbClient,
        ConfigManager configManager
    ) : RequeueActionNeededHealthChecksController(dbClient, configManager)
    {
        protected override bool RequiresAuthentication => false;

        public Task<IActionResult> InvokeAsync() => HandleApiRequest();
    }
}
