using System.Net;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class MetricsHttpIntegrationTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public async Task MetricsEndpoint_IsPrometheusTextAndBypassesWebDavAuthentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("# HELP", content);
        Assert.Contains("nzbdav_active_reads", content);
    }

    [Fact]
    public async Task MetricsEndpoint_RequiresApiKeyWhenEnabled()
    {
        var original = Environment.GetEnvironmentVariable("METRICS_REQUIRE_API_KEY");
        Environment.SetEnvironmentVariable("METRICS_REQUIRE_API_KEY", "true");
        try
        {
            using var client = factory.CreateClient();

            using var rejected = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
            request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
            using var accepted = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("METRICS_REQUIRE_API_KEY", original);
        }
    }
}
