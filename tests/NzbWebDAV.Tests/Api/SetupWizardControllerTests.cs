using System.Net;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class SetupWizardControllerTests
{
    [Fact]
    public async Task Complete_SymlinksDisablesSegmentCacheAndResolvesSetup()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var initial = await client.GetAsync("/api/setup-wizard-state");
        using var initialJson = await JsonDocument.ParseAsync(
            await initial.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.True(initialJson.RootElement.GetProperty("setupRequired").GetBoolean());

        using var completionForm = new MultipartFormDataContent
        {
            { new StringContent("symlinks"), "strategy" },
            { new StringContent("[\"manual\"]"), "ingestionMethods" },
            {
                new StringContent("{\"usenet.segment-cache.enabled\":\"true\",\"rclone.rc-enabled\":\"false\"}"),
                "config"
            },
        };
        using var completion = await client.PostAsync(
            "/api/setup-wizard/complete",
            completionForm);
        using var completionJson = await JsonDocument.ParseAsync(
            await completion.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        Assert.True(completionJson.RootElement.GetProperty("status").GetBoolean());
        Assert.True(completionJson.RootElement.GetProperty("restartRequired").GetBoolean());

        using var configForm = new MultipartFormDataContent
        {
            { new StringContent(ConfigKeys.ApiImportStrategy), "config-keys" },
            { new StringContent(ConfigKeys.UsenetSegmentCacheEnabled), "config-keys" },
        };
        using var configResponse = await client.PostAsync("/api/get-config", configForm);
        using var configJson = await JsonDocument.ParseAsync(
            await configResponse.Content.ReadAsStreamAsync());
        var values = configJson.RootElement.GetProperty("configItems")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("configName").GetString()!,
                item => item.GetProperty("configValue").GetString());
        Assert.Equal("symlinks", values[ConfigKeys.ApiImportStrategy]);
        Assert.Equal("false", values[ConfigKeys.UsenetSegmentCacheEnabled]);

        using var resolved = await client.GetAsync("/api/setup-wizard-state");
        using var resolvedJson = await JsonDocument.ParseAsync(
            await resolved.Content.ReadAsStreamAsync());
        Assert.False(resolvedJson.RootElement.GetProperty("setupRequired").GetBoolean());
        Assert.Equal("completed", resolvedJson.RootElement.GetProperty("disposition").GetString());
    }
}