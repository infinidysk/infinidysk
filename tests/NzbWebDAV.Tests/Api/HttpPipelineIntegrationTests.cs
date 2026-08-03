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
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var rejectedJson = await JsonDocument.ParseAsync(
            await rejected.Content.ReadAsStreamAsync());
        Assert.False(rejectedJson.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal("API Key Required", rejectedJson.RootElement.GetProperty("error").GetString());

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
    public async Task HealthEndpoint_IsAvailableWithoutAuthentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

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
