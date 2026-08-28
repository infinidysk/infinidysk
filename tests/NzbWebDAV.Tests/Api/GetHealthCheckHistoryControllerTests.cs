using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.GetHealthCheckHistory;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public sealed class GetHealthCheckHistoryControllerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-history-{Guid.NewGuid():N}.sqlite");
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
    public async Task GetAsync_FiltersPagedItemsWithoutFilteringStats()
    {
        var now = DateTimeOffset.UtcNow;
        var deletedNewest = NewResult(now.AddSeconds(-1), HealthCheckResult.RepairAction.Deleted);
        var repaired = NewResult(now.AddSeconds(-2), HealthCheckResult.RepairAction.Repaired);
        var deletedOldest = NewResult(now.AddSeconds(-3), HealthCheckResult.RepairAction.Deleted);
        var healthy = NewResult(now.AddSeconds(-4), HealthCheckResult.RepairAction.None);
        _context.HealthCheckResults.AddRange(deletedNewest, repaired, deletedOldest, healthy);
        await _context.SaveChangesAsync();

        var response = await InvokeAsync("?repairStatus=deleted,repaired&page=2&pageSize=2");

        Assert.Equal(3, response.TotalCount);
        Assert.Equal([deletedOldest.Id], response.Items.Select(x => x.Id));
        Assert.Contains(response.Stats, x => x.RepairStatus == HealthCheckResult.RepairAction.None && x.Count == 1);
        Assert.Contains(response.Stats, x => x.RepairStatus == HealthCheckResult.RepairAction.Deleted && x.Count == 2);
        Assert.Contains(response.Stats, x => x.RepairStatus == HealthCheckResult.RepairAction.Repaired && x.Count == 1);
    }

    [Theory]
    [InlineData("?repairStatus=repaired", 2)]
    [InlineData("?repairStatus=deleted,repaired", 3)]
    public async Task GetAsync_RepairedFilterIncludesPar2Repairs(string query, int expectedCount)
    {
        var now = DateTimeOffset.UtcNow;
        var par2Repaired = NewResult(now.AddSeconds(-1), HealthCheckResult.RepairAction.RepairedViaPar2);
        var arrRepaired = NewResult(now.AddSeconds(-2), HealthCheckResult.RepairAction.Repaired);
        var deleted = NewResult(now.AddSeconds(-3), HealthCheckResult.RepairAction.Deleted);
        _context.HealthCheckResults.AddRange(par2Repaired, arrRepaired, deleted);
        await _context.SaveChangesAsync();

        var response = await InvokeAsync(query);

        Assert.Equal(expectedCount, response.TotalCount);
        Assert.Contains(response.Items, x => x.Id == par2Repaired.Id);
        Assert.Contains(response.Items, x => x.Id == arrRepaired.Id);
    }

    [Theory]
    [InlineData("?repairStatus=")]
    [InlineData("?repairStatus=,")]
    public async Task GetAsync_EmptyRepairStatusTreatedAsNoFilter(string query)
    {
        var now = DateTimeOffset.UtcNow;
        var deleted = NewResult(now.AddSeconds(-1), HealthCheckResult.RepairAction.Deleted);
        var healthy = NewResult(now.AddSeconds(-2), HealthCheckResult.RepairAction.None);
        _context.HealthCheckResults.AddRange(deleted, healthy);
        await _context.SaveChangesAsync();

        var response = await InvokeAsync(query);

        Assert.Equal(2, response.TotalCount);
        Assert.Equal(2, response.Items.Count);
    }

    [Fact]
    public async Task GetAsync_ActionNeededFilterReturnsMissingPayloadResults()
    {
        var now = DateTimeOffset.UtcNow;
        var actionNeeded = NewResult(now, HealthCheckResult.RepairAction.ActionNeeded);
        actionNeeded.Message = HealthCheckService.MissingPayloadMessagePrefix + " details";
        var otherActionNeeded = NewResult(
            now.AddMilliseconds(-500),
            HealthCheckResult.RepairAction.ActionNeeded);
        otherActionNeeded.Message = "NNTP transport failure.";
        var repaired = NewResult(now.AddSeconds(-1), HealthCheckResult.RepairAction.Repaired);
        _context.HealthCheckResults.AddRange(actionNeeded, otherActionNeeded, repaired);
        await _context.SaveChangesAsync();

        var response = await InvokeAsync("?repairStatus=action-needed");

        Assert.Equal(2, response.TotalCount);
        Assert.Contains(response.Items, item => item.Id == actionNeeded.Id);
        Assert.Contains(response.Items, item => item.Id == otherActionNeeded.Id);
        Assert.DoesNotContain(response.Items, item => item.Id == repaired.Id);
    }

    [Theory]
    [InlineData("?repairStatus=unknown")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=251")]
    public async Task HandleApiRequest_InvalidQueryReturns400(string query)
    {
        var result = await InvokeActionAsync(query);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRepairHistoryIdentityAsync_SnapshotsOriginalNzbIdentity()
    {
        var nzbBlobId = Guid.NewGuid();
        _context.NzbNames.Add(new NzbName { Id = nzbBlobId, FileName = "Example.Release.2026.nzb" });
        await _context.SaveChangesAsync();

        var identity = await HealthCheckService.GetRepairHistoryIdentityAsync(
            _context,
            new DavItem { NzbBlobId = nzbBlobId },
            CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("Example.Release.2026.nzb", identity.NzbFileName);
        Assert.Equal("Example.Release.2026", identity.JobName);
    }

    [Fact]
    public async Task GetRepairHistoryIdentityAsync_ReturnsNullWithoutRetainedProvenance()
    {
        var identity = await HealthCheckService.GetRepairHistoryIdentityAsync(
            _context,
            new DavItem { NzbBlobId = Guid.NewGuid() },
            CancellationToken.None);

        Assert.Null(identity);
    }

    private async Task<GetHealthCheckHistoryResponse> InvokeAsync(string query)
    {
        var result = await InvokeActionAsync(query);
        return Assert.IsType<OkObjectResult>(result).Value as GetHealthCheckHistoryResponse
            ?? throw new Xunit.Sdk.XunitException("Expected health history response.");
    }

    private Task<IActionResult> InvokeActionAsync(string query)
    {
        var controller = new TestController(_dbClient);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.QueryString = new QueryString(query);
        return controller.InvokeAsync();
    }

    private static HealthCheckResult NewResult(DateTimeOffset createdAt, HealthCheckResult.RepairAction repairStatus) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        DavItemId = Guid.NewGuid(),
        Path = "/content/example.mkv",
        Result = repairStatus == HealthCheckResult.RepairAction.None
            ? HealthCheckResult.HealthResult.Healthy
            : HealthCheckResult.HealthResult.Unhealthy,
        RepairStatus = repairStatus,
        Message = null,
    };

    private sealed class TestController(DavDatabaseClient dbClient) : GetHealthCheckHistoryController(dbClient)
    {
        protected override bool RequiresAuthentication => false;

        public Task<IActionResult> InvokeAsync() => HandleApiRequest();
    }
}
