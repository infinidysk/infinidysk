using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class NativePrefetchIntegrationTests
{
    [Fact]
    public async Task QueuedWarm_UsesSharedLowPriorityFactory_ThenServesNativeHit()
    {
        var root = Path.Combine(Path.GetTempPath(), "warm-integration-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(Path.Combine(root, "media"));
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        try
        {
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
                new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] { new NativeCacheFolder { Id = "disk", Path = Path.Combine(root, "media"), MinFreeBytes = 0 } }) }
            ]);
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
            await using var context = new DavDatabaseContext(options);
            await context.Database.EnsureCreatedAsync();
            using var blobs = new FileBlobStore();
            var blobId = Guid.NewGuid();
            await blobs.WriteBlob(blobId, new DavNzbFile { Id = blobId, SegmentIds = ["source"] });
            var id = Guid.NewGuid();
            var item = new DavItem { Id = id, IdPrefix = id.ToString("N")[..5], Path = "/movie.mkv", Name = "movie.mkv",
                FileBlobId = blobId, FileSize = 3, Type = DavItem.ItemType.UsenetFile, SubType = DavItem.ItemSubType.NzbFile };
            context.Items.Add(item);
            await context.SaveChangesAsync();
            using var repairs = new RepairPatchStore(Path.Combine(root, "repairs"), 100);
            await using var native = new NativeCacheService(config, blobs, repairs);
            using var jobs = new PrefetchJobStore(Path.Combine(root, "jobs.db"));
            var factory = new FakeFactory();
            var services = new ServiceCollection();
            services.AddScoped(_ => new DavDatabaseContext(options));
            services.AddScoped(sp => new DavDatabaseClient(sp.GetRequiredService<DavDatabaseContext>(), blobs));
            services.AddSingleton<IDavContentStreamFactory>(factory);
            await using var provider = services.BuildServiceProvider();
            var executor = new NativePrefetchExecutor(provider.GetRequiredService<IServiceScopeFactory>(), native, config, jobs, new ActiveReadRegistry());
            jobs.Enqueue(id, "manual", 1);
            using var cancellation = new CancellationTokenSource();
            await executor.ExecuteAsync(jobs.ClaimNext()!, cancellation.Token);
            Assert.Equal(1, factory.Opens);
            Assert.Equal(3, jobs.List().Single().CommittedBytes);
            await using var hit = await native.WrapAsync(item, _ => throw new InvalidOperationException("Cache hit opened source"), CancellationToken.None);
            var bytes = new byte[3];
            Assert.Equal(3, await hit.ReadAsync(bytes));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);

            // Saved Plex source -> exact imported path -> the same queue/executor.
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.PlexServers, ConfigValue = JsonSerializer.Serialize(new[] { new PlexServer
                    { Id = "machine", Name = "Plex", Url = "http://plex.test:32400", Token = "test-token", PathMappings = [new("/plex", "/")] } }) },
                new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = JsonSerializer.Serialize(new PrefetchSettings
                    { Enabled = true, RealtimeEnabled = true, Sources = [new() { ServerId = "machine", Key = "/hubs/movies", Title = "Movies", Type = "movie" }] }) }
            ]);
            using var handler = new PlexHandler();
            using var http = new HttpClient(handler);
            var api = new PlexApiClient(http, "native-warm-test");
            var reads = new ActiveReadRegistry();
            using var runtime = new PrefetchRuntime(config, native, provider.GetRequiredService<IServiceScopeFactory>(), reads);
            using var policies = new PlexPrefetchService(config, api, runtime, provider.GetRequiredService<IServiceScopeFactory>(), reads);
            var preview = await policies.PreviewAsync(CancellationToken.None);
            Assert.Equal(id, Assert.Single(preview).ItemId);
            Assert.Empty(runtime.Jobs!.List());
            await policies.SyncAsync(true, CancellationToken.None);
            Assert.Equal(id, Assert.Single(runtime.Jobs!.List()).ItemId);
            var playback = new PlexPlaybackRegistry(TimeProvider.System);
            playback.Record("machine", [new PlexSession("session", "user", "playing", "/plex/movie.mkv",
                new PlexMediaItem("movie", "movie", "Movie", null, null, null, "/plex/movie.mkv", 1, 10, null))], TimeSpan.FromSeconds(30));
            using var pressured = new PrefetchRuntime(config, native, provider.GetRequiredService<IServiceScopeFactory>(), reads, playback);
            pressured.Jobs!.Enqueue(id, "manual", 100);
            await pressured.Coordinator!.RunOnceAsync(CancellationToken.None);
            Assert.DoesNotContain(pressured.Jobs.List(), job => job.State == "completed");
            playback.Record("machine", [], TimeSpan.FromSeconds(30));
            await runtime.Coordinator!.RunOnceAsync(CancellationToken.None);
            Assert.Equal("completed", Assert.Single(runtime.Jobs.List()).State);
            Assert.Equal(1, factory.Opens); // Already verified bytes do not consume NNTP again.
        }
        finally { Environment.SetEnvironmentVariable("CONFIG_PATH", previous); Directory.Delete(root, true); }
    }

    private sealed class FakeFactory : IDavContentStreamFactory
    {
        public int Opens { get; private set; }
        public Task<Stream> OpenAsync(DavItem item, CancellationToken cancellationToken)
        {
            Assert.Equal(SemaphorePriority.Low, cancellationToken.GetContext<DownloadPriorityContext>()?.Priority);
            Opens++;
            return Task.FromResult<Stream>(new VerifiedStream());
        }
    }
    private sealed class VerifiedStream() : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    { public bool LastReadCacheable => true; }

    private sealed class PlexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/status/sessions" ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                """<MediaContainer size="1" totalSize="1"><Video ratingKey="movie" type="movie" title="Movie"><Media selected="1"><Part selected="1" file="/plex/movie.mkv"/></Media></Video></MediaContainer>""") });
    }
}
