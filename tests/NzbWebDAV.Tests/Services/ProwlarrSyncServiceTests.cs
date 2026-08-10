using System.Collections;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Prowlarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class ProwlarrSyncServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-prowlarr-sync-{Guid.NewGuid():N}.sqlite");
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private ConfigManager _configManager = null!;
    private IndexerConfigWriteLock _writeLock = null!;
    private FakeProwlarrClientFactory _clientFactory = null!;
    private ProwlarrSyncService _service = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        await using (var setup = new DavDatabaseContext(_options))
        {
            await setup.Database.MigrateAsync();
        }

        _configManager = new ConfigManager();
        _writeLock = new IndexerConfigWriteLock();
        _clientFactory = new FakeProwlarrClientFactory();
        _service = new ProwlarrSyncService(_configManager, _writeLock)
        {
            FreshContextFactory = () => new DavDatabaseContext(_options),
            ClientFactory = _clientFactory,
        };
        ConfigureProwlarr();
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        _writeLock.Dispose();
        await Task.Run(() =>
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        });
    }

    [Fact]
    public async Task SyncNow_PersistsManagedIndexersAndSuccessStatus()
    {
        await PersistConfig(new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Manual",
                    Url = "https://manual.example/api",
                    ApiKey = "manual-key",
                },
            ],
        });
        _clientFactory.Enqueue([
            Remote(7, "Prowlarr One", enable: true),
        ]);

        var snapshot = await _service.SyncNowAsync();

        Assert.Null(snapshot.LastError);
        Assert.NotNull(snapshot.LastSuccessAt);
        Assert.Equal(1, snapshot.Added);
        Assert.Equal(1, snapshot.RemoteIndexerCount);
        Assert.Equal(1, _clientFactory.Calls);

        var persisted = await ReadConfig<IndexerConfig>(ConfigKeys.IndexersInstances);
        Assert.Equal(["Manual", "Prowlarr One"], persisted.Indexers.Select(x => x.Name));
        var managed = persisted.Indexers.Single(x => x.Name == "Prowlarr One");
        Assert.Equal(7, managed.ProwlarrIndexerId);
        Assert.Equal("http://prowlarr:9696/7/api", managed.Url);
        Assert.Equal("prowlarr-key", managed.ApiKey);
    }

    [Fact]
    public async Task SyncNow_FailedFetchKeepsLastGoodConfigurationAndSuccessTimestamp()
    {
        _clientFactory.Enqueue([Remote(7, "Good", enable: true)]);
        var first = await _service.SyncNowAsync();
        _clientFactory.Enqueue(_ => throw new ProwlarrClientException("Prowlarr returned HTTP 503."));

        var failed = await _service.SyncNowAsync();

        Assert.Equal("Prowlarr returned HTTP 503.", failed.LastError);
        Assert.Equal(first.LastSuccessAt, failed.LastSuccessAt);
        Assert.True(failed.LastAttemptAt >= first.LastAttemptAt);

        var persisted = await ReadConfig<IndexerConfig>(ConfigKeys.IndexersInstances);
        var managed = Assert.Single(persisted.Indexers);
        Assert.Equal("Good", managed.Name);
        Assert.Equal(7, managed.ProwlarrIndexerId);
    }

    [Fact]
    public async Task SyncNow_RejectsEnvironmentManagedIndexerConfigWithoutFetching()
    {
        _configManager.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__INDEXERS__INSTANCES"] = """{"Indexers":[]}""",
        }));

        var snapshot = await _service.SyncNowAsync();

        Assert.Equal(0, _clientFactory.Calls);
        Assert.True(snapshot.IndexersEnvironmentManaged);
        Assert.Contains("NZBDAV_CONFIG__INDEXERS__INSTANCES", snapshot.LastError);
        await AssertNoConfigItem(ConfigKeys.IndexersInstances);
    }

    [Fact]
    public async Task SyncNow_RollsBackRenameWhenProfilesAreEnvironmentManaged()
    {
        await PersistConfig(new IndexerConfig { Indexers = [Managed(7, "Before")] });
        await PersistConfig(new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile
                {
                    Token = "token",
                    Name = "profile",
                    IndexerNames = ["Before"],
                },
            ],
        });
        _configManager.ApplyEnvironmentOverlay(ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__PROFILES__INSTANCES"] = """
                {"Profiles":[{"Token":"token","Name":"profile","IndexerNames":["Before"]}]}
                """,
        }));
        _clientFactory.Enqueue([Remote(7, "After", enable: true)]);

        var snapshot = await _service.SyncNowAsync();

        Assert.Contains("NZBDAV_CONFIG__PROFILES__INSTANCES", snapshot.LastError);
        var persistedIndexers = await ReadConfig<IndexerConfig>(ConfigKeys.IndexersInstances);
        Assert.Equal("Before", Assert.Single(persistedIndexers.Indexers).Name);
        var persistedProfiles = await ReadConfig<ProfileConfig>(ConfigKeys.ProfilesInstances);
        Assert.Equal(["Before"], persistedProfiles.Profiles[0].IndexerNames);
    }

    [Fact]
    public async Task SyncNow_ReadsLatestConfigUnderWriteLockAndPreservesConcurrentManualSave()
    {
        await PersistConfig(new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Original",
                    Url = "https://original.example/api",
                    ApiKey = "original-key",
                },
            ],
        });
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientFactory.Enqueue(async _ =>
        {
            fetchStarted.SetResult();
            await releaseFetch.Task;
            return [Remote(7, "Managed", enable: true)];
        });

        var syncTask = _service.SyncNowAsync();
        await fetchStarted.Task;

        await _writeLock.RunAsync(async () =>
        {
            await using var db = new DavDatabaseContext(_options);
            var item = await db.ConfigItems.SingleAsync(x => x.ConfigName == ConfigKeys.IndexersInstances);
            var latest = JsonSerializer.Deserialize<IndexerConfig>(item.ConfigValue)!;
            latest.Indexers.Add(new IndexerConfig.ConnectionDetails
            {
                Name = "Concurrent Manual",
                Url = "https://concurrent.example/api",
                ApiKey = "concurrent-key",
            });
            item.ConfigValue = JsonSerializer.Serialize(latest);
            await db.SaveChangesAsync();
            _configManager.UpdateValues([item]);
            return true;
        });
        releaseFetch.SetResult();

        var snapshot = await syncTask;

        Assert.Null(snapshot.LastError);
        var persisted = await ReadConfig<IndexerConfig>(ConfigKeys.IndexersInstances);
        Assert.Equal(
            ["Original", "Concurrent Manual", "Managed"],
            persisted.Indexers.Select(x => x.Name));
    }

    private void ConfigureProwlarr()
    {
        _configManager.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrUrl, ConfigValue = "http://prowlarr:9696" },
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrApiKey, ConfigValue = "prowlarr-key" },
            new ConfigItem { ConfigName = ConfigKeys.ProwlarrSyncEnabled, ConfigValue = "false" },
        ]);
    }

    private async Task PersistConfig<T>(T config) where T : notnull
    {
        var configName = typeof(T) == typeof(IndexerConfig)
            ? ConfigKeys.IndexersInstances
            : ConfigKeys.ProfilesInstances;
        await using var db = new DavDatabaseContext(_options);
        var item = await db.ConfigItems.FirstOrDefaultAsync(x => x.ConfigName == configName);
        if (item is null)
        {
            item = new ConfigItem
            {
                ConfigName = configName,
                ConfigValue = JsonSerializer.Serialize(config),
            };
            db.ConfigItems.Add(item);
        }
        else
        {
            item.ConfigValue = JsonSerializer.Serialize(config);
        }

        await db.SaveChangesAsync();
    }

    private async Task<T> ReadConfig<T>(string configName) where T : new()
    {
        await using var db = new DavDatabaseContext(_options);
        var raw = await db.ConfigItems
            .Where(x => x.ConfigName == configName)
            .Select(x => x.ConfigValue)
            .SingleAsync();
        return JsonSerializer.Deserialize<T>(raw) ?? new T();
    }

    private async Task AssertNoConfigItem(string configName)
    {
        await using var db = new DavDatabaseContext(_options);
        Assert.False(await db.ConfigItems.AnyAsync(x => x.ConfigName == configName));
    }

    private static ProwlarrIndexer Remote(int id, string name, bool enable) => new()
    {
        Id = id,
        Name = name,
        Enable = enable,
        SupportsSearch = true,
        Protocol = "usenet",
    };

    private static IndexerConfig.ConnectionDetails Managed(int id, string name) => new()
    {
        Name = name,
        Url = $"http://prowlarr/{id}/api",
        ApiKey = "old-key",
        ProwlarrIndexerId = id,
    };

    private sealed class FakeProwlarrClientFactory : IProwlarrClientFactory
    {
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<ProwlarrIndexer>>>> _responses = new();

        public int Calls { get; private set; }

        public void Enqueue(
            IReadOnlyList<ProwlarrIndexer> indexers) =>
            Enqueue(_ => Task.FromResult(indexers));

        public void Enqueue(
            Func<CancellationToken, Task<IReadOnlyList<ProwlarrIndexer>>> response) =>
            _responses.Enqueue(response);

        public IProwlarrClient Create(string baseUrl, string apiKey)
        {
            Calls++;
            Assert.Equal("http://prowlarr:9696", baseUrl);
            Assert.Equal("prowlarr-key", apiKey);
            return new FakeProwlarrClient(_responses.Dequeue());
        }
    }

    private sealed class FakeProwlarrClient(
        Func<CancellationToken, Task<IReadOnlyList<ProwlarrIndexer>>> getIndexers) : IProwlarrClient
    {
        public Task<ProwlarrSystemStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProwlarrSystemStatus { Version = "1.0" });

        public Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(CancellationToken ct = default) =>
            getIndexers(ct);
    }
}
