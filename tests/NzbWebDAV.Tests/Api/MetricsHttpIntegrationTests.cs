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
}
