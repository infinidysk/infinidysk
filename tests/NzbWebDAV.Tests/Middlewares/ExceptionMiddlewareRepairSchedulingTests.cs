using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Middlewares;

[Collection(nameof(ConfigPathCollection))]
public sealed class ExceptionMiddlewareRepairSchedulingTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(), $"infinidysk-repair-schedule-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "db.sqlite")}")
            .Options;
        await using var db = new DavDatabaseContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<DavDatabaseContext> options)
        : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() => new(options);

        public Task<DavDatabaseContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static ConfigManager Config(int threshold)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.RepairAutoRemoveAfterFailures,
                ConfigValue = threshold.ToString(CultureInfo.InvariantCulture),
            },
        ]);
        return config;
    }

    private async Task<DavItem> SeedItemAsync(int? qualifyingFailures = null)
    {
        var id = Guid.NewGuid();
        var item = new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = "video.mkv",
            Path = $"/content/{id:N}/video.mkv",
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.MultipartFile,
            NextHealthCheck = qualifyingFailures is null ? null : DateTimeOffset.UnixEpoch,
            UrgentRepairFailures = qualifyingFailures,
        };
        await using var db = new DavDatabaseContext(_options);
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static DefaultHttpContext ContextFor(DavItem item)
    {
        var context = new DefaultHttpContext();
        context.Items["DavItem"] = item;
        return context;
    }

    private async Task<DavItem> LoadItemAsync(Guid id)
    {
        await using var db = new DavDatabaseContext(_options);
        return await db.Items.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task TriggerAsync(
        DavItem item,
        int count,
        int threshold,
        TaskCompletionSource<Guid>? scheduled = null)
    {
        var middleware = new ExceptionMiddleware(
            _ => throw new UsenetArticleNotFoundException("seg@x")
            {
                ProviderGeneration = 1,
            },
            Config(threshold),
            new StreamingFailureTracker(),
            new TestDbContextFactory(_options));
        if (scheduled is not null)
            middleware.RepairScheduleCompletionHook = id =>
            {
                scheduled.TrySetResult(id);
                return Task.CompletedTask;
            };

        for (var attempt = 0; attempt < count; attempt++)
            await middleware.InvokeAsync(ContextFor(item));
    }

    [Fact]
    public async Task ScheduleRepair_AtThreshold_PersistsQualifyingCountWithUrgentSentinel()
    {
        var item = await SeedItemAsync();
        var scheduled = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await TriggerAsync(item, count: 3, threshold: 3, scheduled);
        Assert.Equal(item.Id, await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var persisted = await LoadItemAsync(item.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, persisted.NextHealthCheck);
        Assert.Equal(3, persisted.UrgentRepairFailures);
    }

    [Fact]
    public async Task ScheduleRepair_BelowThreshold_LeavesQualificationNull()
    {
        var item = await SeedItemAsync();
        await TriggerAsync(item, count: 2, threshold: 3);

        var persisted = await LoadItemAsync(item.Id);
        Assert.Null(persisted.NextHealthCheck);
        Assert.Null(persisted.UrgentRepairFailures);
    }

    [Fact]
    public async Task ScheduleRepair_AlreadyUrgent_RaisesPersistedCountButNeverLowersIt()
    {
        var item = await SeedItemAsync(qualifyingFailures: 5);
        var scheduled = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await TriggerAsync(item, count: 3, threshold: 3, scheduled);
        await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, (await LoadItemAsync(item.Id)).UrgentRepairFailures);
    }
}
