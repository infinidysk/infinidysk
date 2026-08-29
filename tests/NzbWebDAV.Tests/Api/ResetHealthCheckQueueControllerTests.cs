using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.ResetHealthCheckQueue;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public sealed class ResetHealthCheckQueueControllerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-reset-health-queue-{Guid.NewGuid():N}.sqlite");
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
    public async Task ResetAsync_MarksNonUrgentUsenetFilesForcedRecheck_WhilePreservingUrgentAndDirectories()
    {
        var scheduled = NewItem("scheduled.mkv", DavItem.ItemType.UsenetFile, DateTimeOffset.UtcNow.AddDays(1));
        var uncheckedFile = NewItem("unchecked.mkv", DavItem.ItemType.UsenetFile, null);
        var sidecar = NewItem("cover.jpg", DavItem.ItemType.UsenetFile, DateTimeOffset.UtcNow.AddDays(1));
        var urgent = NewItem("urgent.mkv", DavItem.ItemType.UsenetFile, DateTimeOffset.UnixEpoch);
        var directory = NewItem("directory", DavItem.ItemType.Directory, DateTimeOffset.UtcNow.AddDays(1));
        _context.Items.AddRange(scheduled, uncheckedFile, sidecar, urgent, directory);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await InvokeAsync();

        // Only executable health-check candidates are counted; the non-media sidecar is
        // marked but skipped by the checker's candidate filter.
        Assert.Equal(2, response.ResetCount);
        var updated = await _context.Items.ToDictionaryAsync(x => x.Id);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, updated[scheduled.Id].NextHealthCheck);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, updated[uncheckedFile.Id].NextHealthCheck);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, updated[sidecar.Id].NextHealthCheck);
        Assert.Equal(DateTimeOffset.UnixEpoch, updated[urgent.Id].NextHealthCheck);
        Assert.NotNull(updated[directory.Id].NextHealthCheck);
        Assert.NotEqual(HealthCheckService.ForcedRecheckSentinel, updated[directory.Id].NextHealthCheck);
    }

    [Fact]
    public async Task ResetAsync_GetRequestReturnsMethodNotAllowedWithoutUpdatingItems()
    {
        var scheduled = NewItem("scheduled.mkv", DavItem.ItemType.UsenetFile, DateTimeOffset.UtcNow.AddDays(1));
        _context.Items.Add(scheduled);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await InvokeActionAsync(HttpMethods.Get);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, response.StatusCode);
        var updated = await _context.Items.SingleAsync(x => x.Id == scheduled.Id);
        Assert.NotNull(updated.NextHealthCheck);
    }

    private async Task<ResetHealthCheckQueueResponse> InvokeAsync()
    {
        var result = await InvokeActionAsync(HttpMethods.Post);
        return Assert.IsType<OkObjectResult>(result).Value as ResetHealthCheckQueueResponse
            ?? throw new Xunit.Sdk.XunitException("Expected reset health check queue response.");
    }

    private Task<IActionResult> InvokeActionAsync(string method)
    {
        var controller = new TestController(_dbClient)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Request.Method = method;

        return controller.InvokeAsync();
    }

    private static DavItem NewItem(string name, DavItem.ItemType type, DateTimeOffset? nextHealthCheck)
    {
        var item = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            fileSize: type == DavItem.ItemType.UsenetFile ? 100 : null,
            type,
            type == DavItem.ItemType.UsenetFile ? DavItem.ItemSubType.NzbFile : DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);
        item.NextHealthCheck = nextHealthCheck;
        return item;
    }

    private sealed class TestController(DavDatabaseClient dbClient) : ResetHealthCheckQueueController(dbClient)
    {
        protected override bool RequiresAuthentication => false;

        public Task<IActionResult> InvokeAsync() => HandleApiRequest();
    }
}
