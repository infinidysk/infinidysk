using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.Controllers.BrowseFiles;
using NzbWebDAV.Config;
using NzbWebDAV.Config.Scheduling;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public sealed class BrowseFilesQueryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Join(Path.GetTempPath(), $"files-query-{Guid.NewGuid():N}.sqlite");
    private readonly ConfigManager _config = new();
    private readonly DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private DavDatabaseContext _context = null!;
    private readonly CommandCounter _counter = new();
    private FilesLibrarySnapshot _library = new("not-configured", null, new Dictionary<Guid, string[]>(), null);

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler(), _counter)
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>().Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    private DavItem Add(string name, DavItem? parent = null, bool directory = false, long? size = 100)
    {
        parent ??= DavItem.ContentFolder;
        var item = DavItem.New(Guid.NewGuid(), parent, name, directory ? null : size,
            directory ? DavItem.ItemType.Directory : DavItem.ItemType.UsenetFile,
            directory ? DavItem.ItemSubType.Directory : DavItem.ItemSubType.NzbFile, null, null, null, null);
        _context.Items.Add(item);
        return item;
    }

    private void Result(DavItem file, HealthCheckResult.HealthResult health, int seconds = 0,
        HealthCheckResult.RepairAction repair = HealthCheckResult.RepairAction.None, Guid? id = null) =>
        _context.HealthCheckResults.Add(new HealthCheckResult
        {
            Id = id ?? Guid.NewGuid(), DavItemId = file.Id, Path = file.Path, CreatedAt = _now.AddSeconds(seconds),
            Result = health, RepairStatus = repair,
        });

    private static BrowseFilesRequest Request(string query)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return new BrowseFilesRequest(http);
    }

    private async Task<BrowseFilesResponse> Read(string query = "")
    {
        await _context.SaveChangesAsync();
        return await BrowseFilesQuery.ReadAsync(new DavDatabaseClient(_context), _config, Request(query), _library,
            new Dictionary<Guid, int>(), [], new HealthWorkSchedulePolicy(_config), _now, CancellationToken.None);
    }

    [Fact]
    public async Task Tree_ExpandsOnlyRequestedParent()
    {
        var category = Add("tv", directory: true);
        var release = Add("release", category, true);
        var file = Add("video.mkv", release);
        var root = await Read();
        Assert.Contains(root.Rows, row => row.Id == category.Id);
        Assert.DoesNotContain(root.Rows, row => row.Id == release.Id || row.Id == file.Id);
        var page = await Read("?parentPath=/content/tv");
        Assert.Equal(release.Id, Assert.Single(page.Rows).Id);
        Assert.Equal(1, page.MatchingFileCount);
    }

    [Fact]
    public async Task Tree_HealthFilterRetainsOnlyMatchingAncestorBranches()
    {
        var degradedBranch = Add("degraded", directory: true);
        var nested = Add("nested", degradedBranch, true);
        Result(Add("video.mkv", nested), HealthCheckResult.HealthResult.Degraded);
        Result(Add("video.mkv", Add("healthy", directory: true)), HealthCheckResult.HealthResult.Healthy);
        var page = await Read("?health=degraded");
        Assert.Equal(degradedBranch.Id, Assert.Single(page.Rows).Id);
        Assert.True(page.Rows[0].HasChildren);
    }

    [Fact]
    public async Task List_UsesTheSameFullScopeFiltersAsTree()
    {
        var directory = Add("tv", directory: true);
        var matching = Add("video.mkv", directory);
        Result(matching, HealthCheckResult.HealthResult.Degraded);
        Result(Add("other.mkv", directory), HealthCheckResult.HealthResult.Healthy);
        var tree = await Read("?parentPath=/content/tv&health=degraded");
        var list = await Read("?mode=list&health=degraded");
        Assert.Equal(tree.Rows.Select(row => row.Id), list.Rows.Select(row => row.Id));
        Assert.Equal(tree.MatchingFileCount, list.MatchingFileCount);
    }

    [Fact]
    public async Task Filter_IsAppliedBeforeCountAndPagination()
    {
        for (var index = 0; index < 210; index++) Add($"a-{index:D3}.mkv");
        for (var index = 0; index < 4; index++) Add($"match-{index}.mkv");
        var page = await Read("?mode=list&q=match&limit=2");
        Assert.Equal(4, page.TotalRows);
        Assert.Equal(4, page.MatchingFileCount);
        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.HasMore);
        Assert.All(page.Rows, row => Assert.Contains("match", row.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_UsesLatestRetainedResultAndUrgentRepairPrecedence()
    {
        var recovered = Add("recovered.mkv");
        Result(recovered, HealthCheckResult.HealthResult.Unhealthy, -1);
        Result(recovered, HealthCheckResult.HealthResult.Healthy);
        var urgent = Add("urgent.mkv");
        urgent.NextHealthCheck = DateTimeOffset.UnixEpoch;
        Result(urgent, HealthCheckResult.HealthResult.Healthy);
        var pending = Add("pending.mkv");
        pending.HealthRepairPending = true;
        Result(pending, HealthCheckResult.HealthResult.Healthy);
        var unknown = Add("unknown.mkv");
        unknown.LastHealthCheck = _now;
        var degraded = Add("degraded.mkv");
        Result(degraded, HealthCheckResult.HealthResult.Degraded);
        var page = await Read("?mode=list");
        Assert.Equal("healthy", page.Rows.Single(row => row.Id == recovered.Id).Health);
        Assert.Equal("needs-attention", page.Rows.Single(row => row.Id == urgent.Id).Health);
        Assert.Equal("needs-attention", page.Rows.Single(row => row.Id == pending.Id).Health);
        Assert.Equal("unknown", page.Rows.Single(row => row.Id == unknown.Id).Health);
        Assert.Equal("degraded", page.Rows.Single(row => row.Id == degraded.Id).Health);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_EqualTimestampsPreferResolvedResult(bool actionFirst)
    {
        var file = Add("video.mkv");
        var low = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("f0000000-0000-0000-0000-000000000001");
        Result(file, HealthCheckResult.HealthResult.Unhealthy, repair: HealthCheckResult.RepairAction.ActionNeeded, id: actionFirst ? low : high);
        Result(file, HealthCheckResult.HealthResult.Healthy, id: actionFirst ? high : low);
        Assert.Equal("healthy", Assert.Single((await Read("?mode=list")).Rows).Health);
    }

    [Fact]
    public async Task HistoryMissing_DoesNotHideLiveFile()
    {
        var file = Add("video.mkv");
        var nzb = Guid.NewGuid();
        file.NzbBlobId = nzb;
        _context.NzbNames.Add(new NzbName { Id = nzb, FileName = "synthetic.nzb" });
        var row = Assert.Single((await Read("?mode=list")).Rows);
        Assert.Equal(file.Id, row.Id);
        Assert.Equal(nzb, row.NzbBlobId);
        Assert.Null(row.JobName);
        Assert.Null(row.IndexerName);
        Assert.Null(row.LastPlayedAt);
    }

    [Fact]
    public async Task Dates_PreserveAddedWallClockAndPostedInstant()
    {
        var file = Add("video.mkv");
        file.ReleaseDate = _now.AddYears(-2);
        var row = Assert.Single((await Read("?mode=list")).Rows);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Unspecified)), row.AddedAt);
        Assert.Equal(file.ReleaseDate, row.ReleaseDate);
        Assert.Single((await Read($"?mode=list&postedAfter={file.ReleaseDate.Value.ToUnixTimeSeconds()}")).Rows);
        Assert.Empty((await Read($"?mode=list&postedBefore={file.ReleaseDate.Value.ToUnixTimeSeconds()}")).Rows);
    }

    [Fact]
    public async Task CombinedFilters_PreserveAllInclusiveAndExclusiveBounds()
    {
        var nzbId = Guid.NewGuid();
        var history = new HistoryItem { Id = Guid.NewGuid(), CreatedAt = _now.LocalDateTime, JobName = "Synthetic job", FileName = "Synthetic.nzb",
            Category = "tv", IndexerName = "Synthetic indexer", NzbBlobId = nzbId, LastPlayedAt = _now, DownloadStatus = HistoryItem.DownloadStatusOption.Completed };
        _context.NzbNames.Add(new NzbName { Id = nzbId, FileName = history.FileName });
        _context.HistoryItems.Add(history);
        var file = new DavItem { Id = Guid.NewGuid(), IdPrefix = "tests", ParentId = DavItem.ContentFolder.Id, Name = "Synthetic.mkv", Path = "/content/Synthetic.mkv",
            Type = DavItem.ItemType.UsenetFile, SubType = DavItem.ItemSubType.NzbFile, CreatedAt = DateTime.SpecifyKind(_now.LocalDateTime, DateTimeKind.Unspecified),
            ReleaseDate = _now, LastHealthCheck = _now, FileSize = 100, HistoryItemId = history.Id };
        _context.Items.Add(file);
        Result(file, HealthCheckResult.HealthResult.Degraded, repair: HealthCheckResult.RepairAction.RepairedViaPar2);
        Add("null-values.mkv", size: null);
        var seconds = _now.ToUnixTimeSeconds();
        var combined = $"?mode=list&q=synthetic&category=tv&indexer=Synthetic%20indexer&health=degraded,unknown&subType=201&repairAction=4&hasNzb=true&minSize=100&maxSize=100&addedAfter={seconds}&addedBefore={seconds + 1}&postedAfter={seconds}&postedBefore={seconds + 1}&checkedAfter={seconds}&checkedBefore={seconds + 1}&playedAfter={seconds}&playedBefore={seconds + 1}";
        Assert.Equal(file.Id, Assert.Single((await Read(combined)).Rows).Id);
        foreach (var prefix in new[] { "added", "posted", "checked", "played" })
        {
            Assert.Empty((await Read($"?mode=list&q=Synthetic&{prefix}Before={seconds}")).Rows);
            Assert.Empty((await Read($"?mode=list&q=Synthetic&{prefix}After={seconds + 1}")).Rows);
        }
        Assert.Empty((await Read("?mode=list&minSize=101")).Rows);
        Assert.Empty((await Read("?mode=list&maxSize=99")).Rows);
        Assert.Equal("null-values.mkv", Assert.Single((await Read("?mode=list&hasNzb=false")).Rows).Name);
    }

    [Fact]
    public async Task Schedule_NormalizesSentinelsWithoutLyingAboutDueTimes()
    {
        var urgent = Add("urgent.mkv"); urgent.NextHealthCheck = DateTimeOffset.UnixEpoch;
        var forced = Add("forced.mkv"); forced.NextHealthCheck = HealthCheckService.ForcedRecheckSentinel;
        var due = Add("due.mkv"); due.NextHealthCheck = _now;
        var future = Add("future.mkv"); future.NextHealthCheck = _now.AddDays(1);
        Add("unchecked.mkv");
        var page = await Read("?mode=list");
        Assert.All(page.Rows.Where(row => row.Id == urgent.Id || row.Id == forced.Id || row.Name == "unchecked.mkv"), row => Assert.Null(row.NextCheckAt));
        Assert.Equal(due.Id, Assert.Single((await Read("?mode=list&schedule=due")).Rows).Id);
        Assert.Equal(future.Id, Assert.Single((await Read("?mode=list&schedule=scheduled")).Rows).Id);
        Assert.Equal(forced.Id, Assert.Single((await Read("?mode=list&schedule=recheck-queued")).Rows).Id);
    }

    [Fact]
    public async Task NoScheduledDate_IncludesUnsupportedSidecarsWithoutEnablingRecheck()
    {
        Add("notes.nfo");
        var row = Assert.Single((await Read("?mode=list&schedule=never-scheduled")).Rows);
        Assert.Equal("not-applicable", row.ScanState);
        Assert.False(row.CanRecheck);
    }

    [Fact]
    public async Task Library_UnknownIsNotOutOfLibrary()
    {
        var file = Add("video.mkv");
        _library = new("unknown", null, new Dictionary<Guid, string[]>(), "Scan unavailable.");
        Assert.Empty((await Read("?mode=list&library=not-in-library")).Rows);
        Assert.Single((await Read("?mode=list&library=unknown")).Rows);
        _library = new("ready", _now, new Dictionary<Guid, string[]> { [file.Id] = ["/synthetic/video.mkv"] }, null);
        Assert.Equal(file.Id, Assert.Single((await Read("?mode=list&library=in-library")).Rows).Id);
        Assert.Empty((await Read("?mode=list&library=not-in-library")).Rows);
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("TV")]
    [InlineData("%2C")]
    [InlineData("%_#?")]
    [InlineData("\u00e9pisodes")]
    public async Task Paths_AreLiteralCaseSensitiveAndSegmentBounded(string name)
    {
        var directory = Add(name, directory: true);
        var file = Add("video.mkv", directory);
        Add("video.mkv", Add(name + "2", directory: true));
        if (name == "tv") Add("video.mkv", Add("TV", directory: true));
        var page = await Read("?mode=list&scopePath=" + Uri.EscapeDataString(directory.Path));
        Assert.Equal(file.Id, Assert.Single(page.Rows).Id);
    }

    [Fact]
    public async Task HiddenPolicy_ExcludesHiddenAncestorsAndFiles()
    {
        Add("video.mkv", Add(".hidden", directory: true));
        Add(".hidden.mkv");
        Assert.Empty((await Read("?mode=list")).Rows);
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavShowHiddenFiles, ConfigValue = "true" }]);
        Assert.Equal(2, (await Read("?mode=list")).Rows.Count);
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Pagination_IsStableAcrossEqualSortValues(string direction)
    {
        var first = Add("a.mkv");
        var second = Add("b.mkv");
        var unknown = Add("c.mkv", size: null);
        Assert.Equal(first.Id, Assert.Single((await Read($"?mode=list&sort=size&direction={direction}&limit=1")).Rows).Id);
        Assert.Equal(second.Id, Assert.Single((await Read($"?mode=list&sort=size&direction={direction}&limit=1&offset=1")).Rows).Id);
        Assert.Equal(unknown.Id, Assert.Single((await Read($"?mode=list&sort=size&direction={direction}&limit=1&offset=2")).Rows).Id);
    }

    [Fact]
    public async Task Root_MergesConfiguredEmptyCategoriesWithoutDuplicates()
    {
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.ApiCategories, ConfigValue = "tv" }]);
        var empty = await Read();
        Assert.Equal("category:tv", empty.Rows.Single(row => row.Name == "tv").Key);
        Assert.Empty((await Read("?q=missing")).Rows);
        var real = Add("tv", directory: true);
        Assert.Equal(real.Id, (await Read()).Rows.Single(row => row.Name == "tv").Id);
    }

    [Fact]
    public async Task Capabilities_PreserveProtectedAndReadonlyRules()
    {
        var category = Add("tv", directory: true);
        Add("video.mkv", category);
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "false" }]);
        Assert.False((await Read()).Rows.Single(row => row.Id == category.Id).CanDelete);
        Assert.True(Assert.Single((await Read("?mode=list")).Rows).CanDelete);
        _config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.WebdavEnforceReadonly, ConfigValue = "true" }]);
        Assert.False(Assert.Single((await Read("?mode=list")).Rows).CanDelete);
    }

    [Theory]
    [InlineData("?limit=201")]
    [InlineData("?limit=0")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?health=green")]
    [InlineData("?schedule=queued")]
    [InlineData("?minSize=2&maxSize=1")]
    [InlineData("?offset=1junk")]
    [InlineData("?scopePath=/content/tv&parentPath=/content/tv2")]
    [InlineData("?addedAfter=2&addedBefore=1")]
    [InlineData("?repairAction=55")]
    [InlineData("?subType=101")]
    [InlineData("?hasNzb=1")]
    public void InvalidFilters_AreRejected(string query) => Assert.ThrowsAny<Exception>(() => Request(query));

    [Fact]
    public async Task QueryCount_DoesNotGrowPerReturnedRow()
    {
        Add("first.mkv");
        await Read("?mode=list");
        _counter.Count = 0;
        await Read("?mode=list&limit=200");
        var oneCount = _counter.Count;
        for (var index = 1; index < 200; index++) Add($"file-{index}.mkv");
        await _context.SaveChangesAsync();
        _counter.Count = 0;
        Assert.Equal(200, (await Read("?mode=list&limit=200")).Rows.Count);
        Assert.Equal(oneCount, _counter.Count);
        Assert.InRange(_counter.Count, 1, 5);
    }

    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}