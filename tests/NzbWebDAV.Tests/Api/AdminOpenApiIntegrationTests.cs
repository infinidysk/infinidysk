using System.Net;
using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class AdminOpenApiIntegrationTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public async Task DocsAreNotMappedWithoutTheExplicitOptIn()
    {
        using var client = factory.CreateClient();

        using var document = await client.GetAsync("/openapi/admin.json");
        using var scalar = await client.GetAsync("/scalar/");

        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, scalar.StatusCode);
    }

    [Fact]
    public async Task OptedInDocumentIncludesOnlyAdminApiAndApiKeySecurity()
    {
        var previous = Environment.GetEnvironmentVariable("ENABLE_API_DOCS");
        Environment.SetEnvironmentVariable("ENABLE_API_DOCS", "true");
        try
        {
            using var docsFactory = factory.WithWebHostBuilder(_ => { });
            using var client = docsFactory.CreateClient();
            using var rejected = await client.GetAsync("/openapi/admin.json");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/admin.json");
            request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;
            var paths = root.GetProperty("paths");

            Assert.True(paths.TryGetProperty("/api/get-config", out var getConfig));
            Assert.True(getConfig.TryGetProperty("get", out _));
            var getConfigPost = getConfig.GetProperty("post");
            Assert.True(getConfigPost.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("multipart/form-data")
                .GetProperty("schema")
                .GetProperty("properties")
                .TryGetProperty("config-keys", out _));
            Assert.True(paths.TryGetProperty("/api/migration/altmount/categories", out var categories));
            Assert.True(categories.TryGetProperty("get", out _));
            Assert.True(categories.TryGetProperty("put", out _));

            Assert.False(paths.TryGetProperty("/api", out _));
            Assert.False(paths.TryGetProperty("/view/{path}", out _));
            Assert.False(paths.TryGetProperty("/api/search/{token}/lookup", out _));
            Assert.False(paths.TryGetProperty("/api/download-support-pack", out _));

            var apiKey = root.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty("ApiKey");
            Assert.Equal("apiKey", apiKey.GetProperty("type").GetString());
            Assert.Equal("x-api-key", apiKey.GetProperty("name").GetString());
            Assert.Equal("header", apiKey.GetProperty("in").GetString());

            var problem = root.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ProblemDetails");
            Assert.True(problem.GetProperty("properties").TryGetProperty("traceId", out _));
            Assert.Equal(
                "application/problem+json",
                getConfigPost.GetProperty("responses")
                    .GetProperty("401")
                    .GetProperty("content")
                    .EnumerateObject()
                    .First()
                    .Name);

            using var scalarRejected = await client.GetAsync("/scalar/");
            Assert.Equal(HttpStatusCode.Unauthorized, scalarRejected.StatusCode);

            using var scalarRequest = new HttpRequestMessage(HttpMethod.Get, "/scalar/");
            scalarRequest.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
            using var scalar = await client.SendAsync(scalarRequest);
            Assert.Equal(HttpStatusCode.OK, scalar.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENABLE_API_DOCS", previous);
        }
    }
}
