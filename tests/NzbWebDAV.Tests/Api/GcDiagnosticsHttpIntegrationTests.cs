using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        using var host = CreateIsolatedHost(factory, new ControllableTimeProvider(), new ImmediateGcDiagnosticsExecutor());
        using var client = host.CreateClient();
        using var request = AuthenticatedPost();
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        Assert.True(root.GetProperty("status").GetBoolean());
        Assert.True(root.GetProperty("pauseMs").GetInt64() >= 0);
        var warning = root.GetProperty("warning").GetString();
        Assert.Contains("two aggressive full blocking collections", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOH", warning, StringComparison.Ordinal);
        Assert.Contains("paused", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Aggressive", root.GetProperty("collectionMode").GetString());
        Assert.Equal(2, root.GetProperty("fullBlockingCollectionsRequested").GetInt32());
        AssertSnapshotShape(root.GetProperty("before"));
        AssertSnapshotShape(root.GetProperty("after"));
        Assert.Equal(JsonValueKind.Object, root.GetProperty("retention").ValueKind);
        Assert.True(root.TryGetProperty("segmentBufferPool", out var segmentPool));
        Assert.True(
            segmentPool.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
            $"segmentBufferPool should be an object or null, was {segmentPool.ValueKind}");

        var store = host.Services.GetRequiredService<GcDiagnosticsStore>();
        Assert.NotNull(store.LastResult);
        Assert.Equal("Aggressive", store.LastResult!.CollectionMode);
        Assert.Equal(2, store.LastResult.FullBlockingCollectionsRequested);
    }

    [Fact]
    public async Task GcDiagnostics_ConcurrentPost_Returns429()
    {
        var executor = new BlockingGcDiagnosticsExecutor();
        using var host = CreateIsolatedHost(factory, new ControllableTimeProvider(), executor);
        using var client = host.CreateClient();

        var firstTask = client.SendAsync(AuthenticatedPost());
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var concurrent = await client.SendAsync(AuthenticatedPost());
        Assert.Equal(HttpStatusCode.TooManyRequests, concurrent.StatusCode);
        using var concurrentJson = await JsonDocument.ParseAsync(await concurrent.Content.ReadAsStreamAsync());
        Assert.Equal(StatusCodes.Status429TooManyRequests, concurrentJson.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(
            "already in progress",
            concurrentJson.RootElement.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);

        executor.Release.TrySetResult();
        using var first = await firstTask;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    [Fact]
    public async Task GcDiagnostics_CooldownPost_Returns429WithRetryAfter()
    {
        var clock = new ControllableTimeProvider();
        using var host = CreateIsolatedHost(factory, clock, new ImmediateGcDiagnosticsExecutor());
        using var client = host.CreateClient();

        using var first = await client.SendAsync(AuthenticatedPost());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var cooldown = await client.SendAsync(AuthenticatedPost());
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldown.StatusCode);
        Assert.NotNull(cooldown.Headers.RetryAfter);
        Assert.True(cooldown.Headers.RetryAfter!.Delta is { } delay && delay > TimeSpan.Zero);
        using var cooldownJson = await JsonDocument.ParseAsync(await cooldown.Content.ReadAsStreamAsync());
        Assert.Equal(StatusCodes.Status429TooManyRequests, cooldownJson.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(
            "recently completed",
            cooldownJson.RootElement.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);

        clock.Advance(GcDiagnosticsStore.Cooldown);
        using var afterCooldown = await client.SendAsync(AuthenticatedPost());
        Assert.Equal(HttpStatusCode.OK, afterCooldown.StatusCode);
    }

    private static void AssertSnapshotShape(JsonElement snapshot)
    {
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("generations").ValueKind);
        Assert.True(snapshot.TryGetProperty("memoryLoadBytes", out _));
        Assert.True(snapshot.TryGetProperty("highMemoryLoadThresholdBytes", out _));
        Assert.True(snapshot.TryGetProperty("index", out _));
        Assert.True(snapshot.TryGetProperty("generation", out _));
        Assert.True(snapshot.TryGetProperty("compacted", out _));
        Assert.True(snapshot.TryGetProperty("concurrent", out _));
        var names = snapshot.GetProperty("generations").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToList();
        if (names.Count > 3)
            Assert.Contains("loh", names);
    }

    private static HttpRequestMessage AuthenticatedPost()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/gc-diagnostics");
        request.Headers.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        return request;
    }

    private static WebApplicationFactory<Program> CreateIsolatedHost(
        NzbDavWebApplicationFactory factory,
        TimeProvider clock,
        IGcDiagnosticsExecutor executor)
    {
        var store = new GcDiagnosticsStore(clock);
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GcDiagnosticsStore>();
                services.RemoveAll<IGcDiagnosticsExecutor>();
                services.AddSingleton(store);
                services.AddSingleton<IGcDiagnosticsExecutor>(executor);
            });
        });
    }

    private sealed class ImmediateGcDiagnosticsExecutor : IGcDiagnosticsExecutor
    {
        public GcCollectionExecution Execute()
        {
            var snapshot = GcSnapshotBuilder.Capture();
            return new GcCollectionExecution(snapshot, snapshot, PauseMs: 0);
        }
    }

    private sealed class BlockingGcDiagnosticsExecutor : IGcDiagnosticsExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GcCollectionExecution Execute()
        {
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            var snapshot = GcSnapshotBuilder.Capture();
            return new GcCollectionExecution(snapshot, snapshot, PauseMs: 1);
        }
    }
}
