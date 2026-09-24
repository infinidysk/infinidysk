using System.Net;
using System.Net.Http.Headers;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
[Trait("Category", "CrossStack")]
public sealed class FrontendProxyContractTests
{
    [SkippableFact]
    public async Task FilesMutationProxy_SessionCannotBypassResourceAction()
    {
        Skip.IfNot(RepoPaths.FrontendProductionBuildExists(), "Frontend production build required.");
        await using var backend = new NzbDavWebApplicationFactory();
        backend.UseKestrel(0);
        using var backendClient = backend.CreateAuthenticatedClient();
        using var accountForm = new MultipartFormDataContent();
        accountForm.Add(new StringContent("files-admin"), "username");
        accountForm.Add(new StringContent("synthetic-files-password"), "password");
        accountForm.Add(new StringContent("Admin"), "type");
        using var created = await backendClient.PostAsync("/api/create-account", accountForm);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await using var frontend = await FrontendProductionProcess.StartAsync(backend.ClientOptions.BaseAddress, NzbDavWebApplicationFactory.ApiKey, backend.ConfigPath);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CheckCertificateRevocationList = true };
        using var client = new HttpClient(handler) { BaseAddress = frontend.BaseAddress };
        using var login = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "files-admin", ["password"] = "synthetic-files-password" }),
        };
        login.Headers.Add("Origin", frontend.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var loggedIn = await client.SendAsync(login);
        Assert.True(loggedIn.Headers.Contains("Set-Cookie"));
        foreach (var path in new[] { "/api/recheck-file", "/api/search-file-in-arr", "/api/RECHECK-FILE/", "/%61pi/search-file-in-arr/" })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(Guid.NewGuid().ToString()), "davItemId");
            using var response = await client.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task FilesMutationProxy_ExplicitApiKeyStillWorks()
    {
        Skip.IfNot(RepoPaths.FrontendProductionBuildExists(), "Frontend production build required.");
        await using var backend = new NzbDavWebApplicationFactory();
        backend.UseKestrel(0);
        _ = backend.Services;
        await using var frontend = await FrontendProductionProcess.StartAsync(backend.ClientOptions.BaseAddress, NzbDavWebApplicationFactory.ApiKey, backend.ConfigPath);
        using var client = frontend.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        foreach (var path in new[] { "/api/recheck-file", "/api/search-file-in-arr" })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(Guid.NewGuid().ToString()), "davItemId");
            using var response = await client.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

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
