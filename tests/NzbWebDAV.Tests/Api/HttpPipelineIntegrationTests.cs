using System.Net;
using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class HttpPipelineIntegrationTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public async Task AdminApi_RejectsMissingKeyAndAcceptsFrontendKey()
    {
        using var client = factory.CreateClient();

        using var rejected = await client.GetAsync("/api/is-onboarding");
        await AdminProblemAssertions.AssertProblemAsync(
            rejected, HttpStatusCode.Unauthorized, "API Key Required");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/is-onboarding");
        request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        using var accepted = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var acceptedJson = await JsonDocument.ParseAsync(
            await accepted.Content.ReadAsStreamAsync());
        Assert.True(acceptedJson.RootElement.GetProperty("status").GetBoolean());
        Assert.True(acceptedJson.RootElement.GetProperty("isOnboarding").GetBoolean());
    }

    [Fact]
    public async Task AdminApi_AcceptsFormFieldApiKey()
    {
        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["apikey"] = NzbDavWebApplicationFactory.ApiKey,
        });

        using var response = await client.PostAsync("/api/is-onboarding", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.True(json.RootElement.GetProperty("isOnboarding").GetBoolean());
    }

    [Fact]
    public async Task ProwlarrSyncStatus_RequiresApiKey()
    {
        using var client = factory.CreateClient();

        using var rejected = await client.GetAsync("/api/prowlarr-sync");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/prowlarr-sync");
        request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        using var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var json = await JsonDocument.ParseAsync(await accepted.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.False(json.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task HealthEndpoint_IsAvailableWithoutAuthentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadyEndpoint_IsAvailableWithoutAuthentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SabVersionEndpoint_MatchesUnauthenticatedCompatibilityRoute()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api?mode=version&output=json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal("4.5.1", json.RootElement.GetProperty("version").GetString());
    }
}
