using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Services.Diagnostics;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class GcDiagnosticsHttpIntegrationTests(NzbDavWebApplicationFactory factory)
{
    [Fact]
    public async Task GcDiagnostics_RejectsMissingApiKey()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/gc-diagnostics", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GcDiagnostics_RejectsGet()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/gc-diagnostics");
        request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task GcDiagnostics_AuthorizedPost_ReturnsSnapshotsAndStoresResult()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/gc-diagnostics");
        request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        Assert.True(root.GetProperty("status").GetBoolean());
        Assert.True(root.GetProperty("pauseMs").GetInt64() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("warning").GetString()));
        Assert.Equal(JsonValueKind.Object, root.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("retention").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("after").GetProperty("generations").ValueKind);
        Assert.True(root.TryGetProperty("segmentBufferPool", out var segmentPool));
        Assert.True(
            segmentPool.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
            $"segmentBufferPool should be an object or null, was {segmentPool.ValueKind}");

        var store = factory.Services.GetRequiredService<GcDiagnosticsStore>();
        Assert.NotNull(store.LastResult);
    }
}
