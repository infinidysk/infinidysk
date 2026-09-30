using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class CacheModeResponseTests
{
    [Fact]
    public async Task Save_ReportsConfiguredModeAndRestartWithoutChangingActiveMode()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["cache.mode"] = "segment" });
        using var response = await client.PostAsync("/api/update-config", form);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("off", json.RootElement.GetProperty("activeCacheMode").GetString());
        Assert.Equal("segment", json.RootElement.GetProperty("configuredCacheMode").GetString());
        Assert.True(json.RootElement.GetProperty("restartRequired").GetBoolean());
    }
}
