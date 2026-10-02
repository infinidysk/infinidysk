using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class NativeCacheAdminTests
{
    [Theory]
    [InlineData("/api/native-cache")]
    [InlineData("/api/native-cache/entries?folderId=disk")]
    [InlineData("/api/native-cache/ranges?key=test")]
    [InlineData("/api/native-cache/summary")]
    [InlineData("/api/native-cache/files")]
    [InlineData("/api/native-cache/activity")]
    [InlineData("/api/native-cache/transfers")]
    [InlineData("/api/native-cache/evictions")]
    [InlineData("/api/prefetch")]
    [InlineData("/api/prefetch/preview")]
    public async Task CacheAndPrefetchViews_RequireAuthentication(string path)
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/native-cache/operations")]
    [InlineData("/api/prefetch/operations")]
    public async Task CacheOperations_RequireAuthentication(string path)
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(path, new { operation = "pause-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PrefetchOperations_ExposeImportedNamesAndRespectPauseAndCancellation()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var folder = Path.Combine(factory.ConfigPath, "media");
        Directory.CreateDirectory(folder);
        await using (var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=" + Path.Combine(factory.ConfigPath, "db.sqlite")).Options))
        {
            foreach (var (name, value) in new[] { (ConfigKeys.CacheMode, "native"), (ConfigKeys.NativeCacheFolders,
                JsonSerializer.Serialize(new[] { new NativeCacheFolder { Id = "disk", Path = folder, MinFreeBytes = 0 } })) })
            {
                var existing = await context.ConfigItems.SingleOrDefaultAsync(item => item.ConfigName == name);
                if (existing is null) context.ConfigItems.Add(new ConfigItem { ConfigName = name, ConfigValue = value });
                else existing.ConfigValue = value;
            }
            await context.SaveChangesAsync();
        }
        using var client = factory.CreateAuthenticatedClient();
        var runtime = factory.Services.GetRequiredService<PrefetchRuntime>();
        using var paused = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "pause-all" });
        paused.EnsureSuccessStatusCode();
        Assert.True(runtime.Jobs!.Paused);
        var id = Guid.NewGuid();
        await factory.AddDavItemsAsync(new DavItem { Id = id, IdPrefix = id.ToString("N")[..5], Name = "Episode.mkv",
            Path = "/Episode.mkv", Type = DavItem.ItemType.UsenetFile, SubType = DavItem.ItemSubType.NzbFile,
            FileBlobId = Guid.NewGuid(), FileSize = 100 });
        using var warmed = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "warm", itemIds = new[] { id } });
        Assert.Equal(HttpStatusCode.Accepted, warmed.StatusCode);
        using (var result = JsonDocument.Parse(await warmed.Content.ReadAsStringAsync()))
            Assert.Equal("accepted", result.RootElement.GetProperty("outcomes")[0].GetProperty("status").GetString());
        var absent = Guid.NewGuid();
        using (var bulk = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "warm", itemIds = new[] { id, absent } }))
        {
            Assert.Equal(HttpStatusCode.Accepted, bulk.StatusCode);
            using var result = JsonDocument.Parse(await bulk.Content.ReadAsStringAsync());
            var outcomes = result.RootElement.GetProperty("outcomes");
            Assert.Equal("deduplicated", outcomes[0].GetProperty("status").GetString());
            Assert.Equal("rejected", outcomes[1].GetProperty("status").GetString());
            Assert.Contains("imported", outcomes[1].GetProperty("reason").GetString());
        }
        var job = Assert.Single(runtime.Jobs.List());
        Assert.Equal("queued", job.State);
        using var status = await client.GetAsync("/api/prefetch");
        status.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("Episode.mkv", json.RootElement.GetProperty("jobs")[0].GetProperty("displayName").GetString());
        using var preview = await client.GetAsync("/api/prefetch/preview");
        preview.EnsureSuccessStatusCode();
        Assert.Single(runtime.Jobs.List()); // Preview cannot enqueue or mutate paused manual work.
        using var cancelled = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "cancel", jobId = job.Id });
        cancelled.EnsureSuccessStatusCode();
        Assert.Equal("cancelled", Assert.Single(runtime.Jobs.List()).State);
        using var invalid = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "warm", itemIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Accepted, invalid.StatusCode);
        runtime.ReportMetadataFailure();
        using var unhealthy = await client.GetAsync("/api/prefetch");
        unhealthy.EnsureSuccessStatusCode();
        using var unhealthyJson = JsonDocument.Parse(await unhealthy.Content.ReadAsStringAsync());
        Assert.False(unhealthyJson.RootElement.GetProperty("healthy").GetBoolean());
        Assert.NotNull(unhealthyJson.RootElement.GetProperty("runtimeError").GetString());
        using var stopped = await client.PostAsJsonAsync("/api/prefetch/operations", new { operation = "sync" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stopped.StatusCode);
        using var stillAlive = await client.GetAsync("/health");
        stillAlive.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NativeCatalogueAndPin_AreAvailableThroughAuthenticatedApi()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var folder = Path.Combine(factory.ConfigPath, "media");
        Directory.CreateDirectory(folder);
        await using (var context = new DavDatabaseContext(new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=" + Path.Combine(factory.ConfigPath, "db.sqlite")).Options))
        {
            foreach (var (name, value) in new[] { (ConfigKeys.CacheMode, "native"), (ConfigKeys.NativeCacheFolders,
                JsonSerializer.Serialize(new[] { new NativeCacheFolder { Id = "disk", Path = folder, MinFreeBytes = 0 } })) })
            {
                var existing = await context.ConfigItems.SingleOrDefaultAsync(item => item.ConfigName == name);
                if (existing is null) context.ConfigItems.Add(new ConfigItem { ConfigName = name, ConfigValue = value });
                else existing.ConfigValue = value;
            }
            await context.SaveChangesAsync();
        }
        using var client = factory.CreateAuthenticatedClient();
        var native = factory.Services.GetRequiredService<NativeCacheService>();
        Assert.True(await native.WaitForInitializationAsync());
        var store = native.Store!;
        var identity = new NativeCacheIdentity(Guid.NewGuid().ToString("N"), "test", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        using var summary = await client.GetAsync("/api/native-cache/summary");
        summary.EnsureSuccessStatusCode();
        using (var payload = JsonDocument.Parse(await summary.Content.ReadAsStringAsync()))
            Assert.Equal(1, payload.RootElement.GetProperty("liveFiles").GetInt64());
        using var files = await client.GetAsync("/api/native-cache/files?limit=10");
        files.EnsureSuccessStatusCode();
        using (var payload = JsonDocument.Parse(await files.Content.ReadAsStringAsync()))
            Assert.Equal(identity.Key, payload.RootElement.GetProperty("items")[0].GetProperty("key").GetString());
        using var invalidBrowser = await client.GetAsync("/api/native-cache/files?sort=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalidBrowser.StatusCode);
        using var response = await client.GetAsync("/api/native-cache/entries?folderId=disk&limit=1");
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(identity.Key, page.RootElement.GetProperty("entries")[0].GetProperty("key").GetString());
        Assert.Equal("test", page.RootElement.GetProperty("entries")[0].GetProperty("generation").GetString());
        using var ranges = await client.GetAsync($"/api/native-cache/ranges?key={identity.Key}&limit=1");
        ranges.EnsureSuccessStatusCode();
        using var coverage = JsonDocument.Parse(await ranges.Content.ReadAsStringAsync());
        Assert.Equal(0, coverage.RootElement.GetProperty("ranges")[0].GetProperty("offset").GetInt64());
        Assert.Equal(3, coverage.RootElement.GetProperty("ranges")[0].GetProperty("count").GetInt32());
        using var invalidPage = await client.GetAsync($"/api/native-cache/ranges?key={identity.Key}&limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        using var pin = await client.PostAsJsonAsync("/api/native-cache/operations", new { operation = "pin", cacheKey = identity.Key, pinned = true });
        pin.EnsureSuccessStatusCode();
        Assert.True((await store.ListEntriesAsync("disk", null, 1))[0].Pinned);
        using var rejected = await client.PostAsJsonAsync("/api/native-cache/operations",
            new { operation = "evict", folderId = "disk", cacheKey = identity.Key });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var unpin = await client.PostAsJsonAsync("/api/native-cache/operations", new { operation = "pin", cacheKey = identity.Key, pinned = false });
        unpin.EnsureSuccessStatusCode();
        using var evict = await client.PostAsJsonAsync("/api/native-cache/operations",
            new { operation = "evict", folderId = "disk", cacheKey = identity.Key, confirmCacheKey = identity.Key });
        Assert.Equal(HttpStatusCode.Accepted, evict.StatusCode);
        var operations = factory.Services.GetRequiredService<NativeCacheOperations>();
        for (var attempt = 0; attempt < 100 &&
            (Assert.Single(operations.GetJobs()).State is "queued" or "running"); attempt++)
            await Task.Delay(100);
        Assert.Equal("completed", operations.GetJobs().Single().State);
        Assert.Equal(1, operations.GetJobs().Single().Result);
        Assert.Empty(await store.ListEntriesAsync("disk", null, 1));
    }
}
