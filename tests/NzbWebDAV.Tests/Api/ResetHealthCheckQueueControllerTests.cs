using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.ResetHealthCheckQueue;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

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
    public async Task ResetAsync_NullsScheduledUsenetFiles_WhilePreservingUrgentAndDirectories()
    {
        var scheduled = NewItem("scheduled.mkv", DavItem.ItemType.UsenetFile, DateTimeOffset.UtcNow.AddDays(1));
        var uncheckedFile = NewItem("unchecked.mkv", DavItem.ItemType.UsenetFile, null);
        var urgent = NewItem("urgent.mkv", DavItem.ItemType.UsenetFile, DateTimeOffset.UnixEpoch);
        var directory = NewItem("directory", DavItem.ItemType.Directory, DateTimeOffset.UtcNow.AddDays(1));
        _context.Items.AddRange(scheduled, uncheckedFile, urgent, directory);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await InvokeAsync();

        Assert.Equal(1, response.ResetCount);
        var updated = await _context.Items.ToDictionaryAsync(x => x.Id);
        Assert.Null(updated[scheduled.Id].NextHealthCheck);
        Assert.Null(updated[uncheckedFile.Id].NextHealthCheck);
        Assert.Equal(DateTimeOffset.UnixEpoch, updated[urgent.Id].NextHealthCheck);
        Assert.NotNull(updated[directory.Id].NextHealthCheck);
    }

    private async Task<ResetHealthCheckQueueResponse> InvokeAsync()
    {
        var controller = new TestController(_dbClient)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.InvokeAsync();
        return Assert.IsType<OkObjectResult>(result).Value as ResetHealthCheckQueueResponse
            ?? throw new Xunit.Sdk.XunitException("Expected reset health check queue response.");
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
