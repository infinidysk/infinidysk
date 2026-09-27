using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Plex;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class PlexPolicyIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plex-policy-" + Guid.NewGuid().ToString("N"));
    private string? _previous;
    private string? _previousApiKey;
    private SqliteConnection _connection = null!;
    private FileBlobStore _blobs = null!;
    private RepairPatchStore _repairs = null!;
    private NativeCacheService _native = null!;
    private ServiceProvider _services = null!;
    private ConfigManager _config = null!;
    private PrefetchRuntime _runtime = null!;
    private PlexServer _server = new() { Id = "machine", Name = "Plex", Url = "http://plex.test", Token = "owner-token", AccountId = "owner",
        PathMappings = [new("/plex", "/")] };

    public async Task InitializeAsync()
    {
        _previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "isolated-policy-tests");
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        _config = new ConfigManager();
        Set(ConfigKeys.CacheMode, "native");
        Set(ConfigKeys.NativeCacheFolders, JsonSerializer.Serialize(new[] { new NativeCacheFolder { Id = "disk", Path = Path.Combine(_root, "media"), MinFreeBytes = 0 } }));
        Set(ConfigKeys.PlexServers, JsonSerializer.Serialize(new[] { _server }));
        _blobs = new FileBlobStore();
        _repairs = new RepairPatchStore(Path.Combine(_root, "repairs"), 100);
        _native = new NativeCacheService(_config, _blobs, _repairs);
        Assert.True(await _native.WaitForInitializationAsync());
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(_connection).Options;
        await using (var context = new DavDatabaseContext(options)) await context.Database.EnsureCreatedAsync();
        _services = new ServiceCollection().AddScoped(_ => new DavDatabaseContext(options))
            .AddScoped(sp => new DavDatabaseClient(sp.GetRequiredService<DavDatabaseContext>(), _blobs)).BuildServiceProvider();
        _runtime = new PrefetchRuntime(_config, _native, _services.GetRequiredService<IServiceScopeFactory>(), new ActiveReadRegistry());
    }

    [Fact]
    public async Task NativeSettingsValidation_TimeoutRetainsSingleAdmissionUntilIoCompletes()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = ConfigUpdateService.ValidateNativeStorageAsync(_config, () =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            release.Wait();
            finished.SetResult();
        }, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ArgumentException>(() => first);
            await Assert.ThrowsAsync<ArgumentException>(() => ConfigUpdateService.ValidateNativeStorageAsync(_config,
                () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task EnablePrefetch_RequiresActiveNativeAndWritableReadinessBeforeStaging()
    {
        using var scope = _services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var updates = new ConfigUpdateService(database, _config);
        Set(ConfigKeys.CacheMode, "off");
        await Assert.ThrowsAsync<ArgumentException>(() => updates.StageAsync([
            new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = "{\"Enabled\":true}" }
        ]));
        Assert.False(database.Ctx.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task EnablePrefetch_ProbesActiveStorageAndRejectsOfflineStorageOrPendingFolderChanges()
    {
        using var scope = _services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var updates = new ConfigUpdateService(database, _config, _native);
        ConfigItem enabled = new() { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = "{\"Enabled\":true}" };
        using (var batch = await updates.StageAsync([enabled])) { }
        database.Ctx.ChangeTracker.Clear();
        var path = Path.Combine(_root, "media");
        Directory.Move(path, path + "-offline");
        try { await Assert.ThrowsAsync<ArgumentException>(() => updates.StageAsync([enabled])); }
        finally { Directory.Move(path + "-offline", path); }
        Assert.False(database.Ctx.ChangeTracker.HasChanges());
        await Assert.ThrowsAsync<ArgumentException>(() => updates.StageAsync([enabled,
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] {
                new NativeCacheFolder { Id = "disk", Path = path, ReadOnly = true, MinFreeBytes = 0 } }) }
        ]));
        Assert.False(database.Ctx.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task EnabledPrefetch_RequiresAtomicDisableBeforeLeavingNativeMode()
    {
        using var scope = _services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var updates = new ConfigUpdateService(database, _config, _native);
        Set(ConfigKeys.SmartPrefetchSettings, "{\"Enabled\":true}");
        await Assert.ThrowsAsync<ArgumentException>(() => updates.StageAsync([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "off" }
        ]));
        using var batch = await updates.StageAsync([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "off" },
            new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = "{\"Enabled\":false}" }
        ]);
    }

    [Fact]
    public async Task ShowSource_UsesSelectedHomeUsersDiscoveredServerCredential()
    {
        var id = await AddItem("episode.mkv");
        Set(ConfigKeys.PlexAccounts, JsonSerializer.Serialize(new[] { new PlexAccount("child", "Child", "child-account-token") }));
        SetPolicy("show", ["machine:child"]);
        using var handler = new FakePlexHandler(request =>
        {
            if (request.RequestUri!.Host == "plex.tv")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"clientIdentifier":"machine","name":"Plex","provides":"server","accessToken":"child-server-token","connections":[{"uri":"http://plex.test","local":true}]}]""") };
            if (request.RequestUri.AbsolutePath == "/hubs/source") return PlexApiClientTests.Xml("""<MediaContainer><Directory ratingKey="show" type="show" title="Show"/></MediaContainer>""");
            if (request.RequestUri.AbsolutePath.EndsWith("/allLeaves", StringComparison.Ordinal))
            {
                Assert.Equal("child-server-token", request.Headers.GetValues("X-Plex-Token").Single());
                return PlexApiClientTests.Xml("""<MediaContainer><Video ratingKey="episode" type="episode" grandparentRatingKey="show" parentIndex="20" index="1"><Media><Part file="/plex/episode.mkv"/></Media></Video></MediaContainer>""");
            }
            return PlexApiClientTests.Xml("<MediaContainer/>");
        });
        using var policy = Policy(handler);
        await policy.SyncAsync(true, CancellationToken.None);
        Assert.Equal(id, Assert.Single(_runtime.Jobs!.List()).ItemId);
        Assert.Contains(handler.Requests, request => request.Uri.Host == "plex.tv" && request.Token == "child-account-token");
    }

    [Fact]
    public async Task MappingChangedDuringPlexFetch_DoesNotQueueStaleImportedTarget()
    {
        await AddItem("movie.mkv");
        SetPolicy("movie", []);
        using var handler = new FakePlexHandler(_ =>
        {
            Set(ConfigKeys.PlexServers, JsonSerializer.Serialize(new[] { _server with { PathMappings = [new("/plex", "/new-root")] } }));
            return PlexApiClientTests.Xml("""<MediaContainer><Video ratingKey="movie" type="movie" title="Movie"><Media><Part file="/plex/movie.mkv"/></Media></Video></MediaContainer>""");
        });
        using var policy = Policy(handler);
        await policy.SyncAsync(true, CancellationToken.None);
        Assert.Empty(_runtime.Jobs!.List());
    }

    [Fact]
    public async Task Preview_ExplainsUnmappedCandidatesWithoutQueueMutation()
    {
        SetPolicy("movie", []);
        using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml("""<MediaContainer><Video ratingKey="movie" type="movie" title="Unmapped movie"><Media><Part file="/elsewhere/movie.mkv"/></Media></Video></MediaContainer>"""));
        using var policy = Policy(handler);
        var preview = await policy.PreviewAsync(CancellationToken.None);
        Assert.Contains("mapping", Assert.Single(preview).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runtime.Jobs!.List());
    }

    [Fact]
    public async Task DisabledLibrary_SkipsSourceUntilReenabledWithoutRewritingIt()
    {
        var itemId = await AddItem("movie.mkv");
        PrefetchSource source = new()
        {
            ServerId = "machine", LibraryId = "2", Key = "/hubs/source", Title = "Recent Movies", Type = "movie",
            Limit = 7, ExcludedShows = ["retained"]
        };
        Set(ConfigKeys.SmartPrefetchSettings, JsonSerializer.Serialize(new PrefetchSettings
        {
            Enabled = true, RealtimeEnabled = false, Sources = [source],
            DisabledLibraries = [new("machine", "2", "movie")]
        }));
        using var handler = new FakePlexHandler(_ => PlexApiClientTests.Xml(
            """<MediaContainer><Video ratingKey="movie" type="movie" title="Movie"><Media><Part file="/plex/movie.mkv"/></Media></Video></MediaContainer>"""));
        using var policy = Policy(handler);

        await policy.SyncAsync(true, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Empty(_runtime.Jobs!.List());

        Set(ConfigKeys.SmartPrefetchSettings, JsonSerializer.Serialize(new PrefetchSettings
        {
            Enabled = true, RealtimeEnabled = false, Sources = [source], DisabledLibraries = []
        }));
        await policy.SyncAsync(true, CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/hubs/source");
        Assert.Equal(itemId, Assert.Single(_runtime.Jobs!.List()).ItemId);
        var saved = Assert.Single(_runtime.Settings().Sources);
        Assert.Equal(7, saved.Limit);
        Assert.Equal(["retained"], saved.ExcludedShows);
    }

    private PlexPrefetchService Policy(FakePlexHandler handler) => new(_config, new PlexApiClient(new HttpClient(handler), "policy-test"),
        _runtime, _services.GetRequiredService<IServiceScopeFactory>(), new ActiveReadRegistry());
    private void SetPolicy(string type, string[] users) => Set(ConfigKeys.SmartPrefetchSettings, JsonSerializer.Serialize(new PrefetchSettings
    {
        Enabled = true, RealtimeEnabled = false, Users = users, MaxQueueAhead = 1,
        Sources = [new() { ServerId = "machine", Key = "/hubs/source", Title = "Source", Type = type }]
    }));
    private void Set(string key, string value) => _config.UpdateValues([new ConfigItem { ConfigName = key, ConfigValue = value }]);
    private async Task<Guid> AddItem(string name)
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var id = Guid.NewGuid();
        context.Items.Add(new DavItem { Id = id, IdPrefix = id.ToString("N")[..5], Name = name, Path = "/" + name,
            Type = DavItem.ItemType.UsenetFile, SubType = DavItem.ItemSubType.NzbFile, FileBlobId = Guid.NewGuid(), FileSize = 100 });
        await context.SaveChangesAsync();
        return id;
    }
    public async Task DisposeAsync()
    {
        _runtime.Dispose();
        await _services.DisposeAsync();
        await _native.DisposeAsync();
        _repairs.Dispose(); _blobs.Dispose(); await _connection.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previous);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);
        Directory.Delete(_root, true);
    }
}
