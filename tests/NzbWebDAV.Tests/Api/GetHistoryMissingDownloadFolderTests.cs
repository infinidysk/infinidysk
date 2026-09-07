using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// A Completed history row whose download folder no longer exists (deleted via
/// WebDAV/Explore, or a legacy folder that predates <c>DavItems.HistoryItemId</c>)
/// must be reported as Failed with a reason. SABnzbd never returns a Completed
/// slot with an empty <c>storage</c>; Sonarr/Radarr respond to that shape with
/// "Download doesn't contain intermediate path, Skipping." on every poll.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class GetHistoryMissingDownloadFolderTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-history-missing-folder-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;

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
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/nzbdav" },
        ]);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task CompletedWithMissingFolder_IsReportedAsFailedWithReason()
    {
        var orphanId = await SeedCompletedAsync("Orphan.Show.S01E01.nzb", downloadDirId: Guid.NewGuid());
        var liveFolder = await SeedFolderAsync("Live.Show.S01E01");
        var liveId = await SeedCompletedAsync("Live.Show.S01E01.nzb", downloadDirId: liveFolder.Id);

        var response = await CreateController().GetHistoryAsync(BuildRequest(""));

        var orphan = Assert.Single(response.History.Slots, s => s.NzoId == orphanId.ToString());
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, orphan.Status);
        Assert.Equal(HistoryItemAddedPayload.DownloadFolderMissingFailMessage, orphan.FailMessage);
        Assert.True(string.IsNullOrEmpty(orphan.DownloadPath));

        var live = Assert.Single(response.History.Slots, s => s.NzoId == liveId.ToString());
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, live.Status);
        Assert.False(string.IsNullOrEmpty(live.DownloadPath));
    }

    [Fact]
    public async Task StatusFilters_UseTheReportedStatus()
    {
        var orphanId = await SeedCompletedAsync("Orphan.Show.S01E02.nzb", downloadDirId: Guid.NewGuid());
        var liveFolder = await SeedFolderAsync("Live.Show.S01E02");
        var liveId = await SeedCompletedAsync("Live.Show.S01E02.nzb", downloadDirId: liveFolder.Id);

        // Sonarr/Radarr poll with failed_only=1 to find downloads to clean up.
        var failedOnly = await CreateController().GetHistoryAsync(BuildRequest("?failed_only=1"));
        var failedSlot = Assert.Single(failedOnly.History.Slots);
        Assert.Equal(orphanId.ToString(), failedSlot.NzoId);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, failedSlot.Status);
        Assert.Equal(1, failedOnly.History.TotalCount);

        var completed = await CreateController().GetHistoryAsync(BuildRequest("?status=completed"));
        var completedSlot = Assert.Single(completed.History.Slots);
        Assert.Equal(liveId.ToString(), completedSlot.NzoId);
        Assert.Equal(1, completed.History.TotalCount);
    }

    private async Task<Guid> SeedCompletedAsync(string fileName, Guid? downloadDirId)
    {
        var id = Guid.NewGuid();
        _context.HistoryItems.Add(new HistoryItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 1,
            NzbBlobId = id,
            DownloadDirId = downloadDirId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<DavItem> SeedFolderAsync(string jobName)
    {
        var categoryDir = await _context.Items.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ParentId == DavItem.ContentFolder.Id && x.Name == "tv");
        if (categoryDir is null)
        {
            categoryDir = NewDir(DavItem.ContentFolder, "tv");
            _context.Items.Add(categoryDir);
        }

        var jobDir = NewDir(categoryDir, jobName);
        _context.Items.Add(jobDir);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return jobDir;
    }

    private static DavItem NewDir(DavItem parent, string name) =>
        DavItem.New(
            Guid.NewGuid(),
            parent,
            name,
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);

    private GetHistoryRequest BuildRequest(string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);
        return new GetHistoryRequest(httpContext, _configManager);
    }

    private GetHistoryController CreateController() =>
        new(new DefaultHttpContext(), _dbClient, _configManager, new ProviderUsageTracker());
}
