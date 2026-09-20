using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class NativeCacheAdminTests
{
    [Theory]
    [InlineData("/api/native-cache")]
    [InlineData("/api/native-cache/entries?folderId=disk")]
    [InlineData("/api/prefetch")]
    public async Task CacheAndPrefetchViews_RequireAuthentication(string path)
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        var store = factory.Services.GetRequiredService<NativeCacheService>().Store!;
        var identity = new NativeCacheIdentity(Guid.NewGuid().ToString("N"), "test", 3);
        Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        using var response = await client.GetAsync("/api/native-cache/entries?folderId=disk&limit=1");
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(identity.Key, page.RootElement.GetProperty("entries")[0].GetProperty("key").GetString());
        using var pin = await client.PostAsJsonAsync("/api/native-cache/operations", new { operation = "pin", cacheKey = identity.Key, pinned = true });
        pin.EnsureSuccessStatusCode();
        Assert.True((await store.ListEntriesAsync("disk", null, 1))[0].Pinned);
    }
}
