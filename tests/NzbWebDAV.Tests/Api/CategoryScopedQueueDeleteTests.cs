using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.RemoveFromQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class CategoryScopedQueueDeleteTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-cat-del-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;

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

        _context.QueueItems.AddRange(
            CreateItem("tv", "a.nzb"),
            CreateItem("movies", "b.nzb"),
            CreateItem("tv", "three.nzb"));
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task RemoveFromQueueRequest_AllWithCategory_FiltersCategory()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?value=all&cat=tv");
        context.Request.Body = Stream.Null;

        var request = await RemoveFromQueueRequest.New(context);

        Assert.True(request.DeleteAll);
        Assert.Equal("tv", request.Category);
    }

    private static QueueItem CreateItem(string category, string fileName) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = fileName,
        JobName = category,
        NzbFileSize = 1,
        TotalSegmentBytes = 1,
        Category = category,
        Priority = QueueItem.PriorityOption.Normal,
        PostProcessing = QueueItem.PostProcessingOption.None,
    };
}
