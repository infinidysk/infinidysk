using System.Net;
using System.Net.Http.Headers;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
[Trait("Category", "CrossStack")]
public sealed class FrontendProxyContractTests
{
    [SkippableFact]
    public async Task ProductionFrontend_ProxiesSabQueueAndWebDavContracts()
    {
        Skip.IfNot(
            RepoPaths.FrontendProductionBuildExists(),
            "frontend production build is required (npm run build && npm run build:server)");

        await using var backend = new NzbDavWebApplicationFactory();
        backend.UseKestrel(0);
        _ = backend.Services;
        var backendUrl = backend.ClientOptions.BaseAddress;
        Assert.True(backendUrl.Port > 0, "Kestrel did not bind a TCP port for cross-stack tests.");

        await using var frontend = await FrontendProductionProcess.StartAsync(
            backendUrl,
            NzbDavWebApplicationFactory.ApiKey,
            backend.ConfigPath);

        using var client = frontend.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);

        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var queue = await client.GetAsync("/api?mode=queue&output=json");
        using var queueJson = await SabContractAssertions.AssertSuccessAsync(queue);
        JsonContractValidator.AssertMatchesSchema(queueJson.RootElement, "sab/v1/queue.schema.json");

        using var unauthorizedWebDav = new HttpRequestMessage(WebDavContractAssertions.PropFind, "/content");
        unauthorizedWebDav.Headers.TryAddWithoutValidation("Depth", "1");
        using var rejected = await client.SendAsync(unauthorizedWebDav);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var propFind = new HttpRequestMessage(WebDavContractAssertions.PropFind, "/content");
        propFind.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{NzbDavWebApplicationFactory.WebDavUser}:{NzbDavWebApplicationFactory.WebDavPassword}")));
        propFind.Headers.TryAddWithoutValidation("Depth", "1");
        using var content = await client.SendAsync(propFind);
        var document = await WebDavContractAssertions.AssertMultiStatusAsync(content);
        WebDavContractAssertions.AssertCollectionListing(document, "/content");

        using var range = new HttpRequestMessage(HttpMethod.Get, "/README");
        range.Headers.Authorization = propFind.Headers.Authorization;
        range.Headers.Range = new RangeHeaderValue(0, 9);
        using var ranged = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(10, (await ranged.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("application/json", queue.Content.Headers.ContentType?.MediaType);
    }
}
