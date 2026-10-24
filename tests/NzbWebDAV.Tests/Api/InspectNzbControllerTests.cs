using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Queue.Inspection;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class InspectNzbControllerTests
{
    private const string SegmentId = "inspect-http-movie@example.invalid";
    private const int PayloadSize = 8192;

    [Fact]
    public async Task InspectNzb_RequiresApiKey()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var form = NzbForm(DirectMediaNzb());
        using var response = await client.PostAsync("/api/inspect-nzb", form);
        using var problem = await AdminProblemAssertions.AssertProblemAsync(
            response, HttpStatusCode.Unauthorized, "API Key");
    }

    [Fact]
    public async Task InspectNzb_RejectsRequestsWithoutAnNzbFile()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/inspect-nzb", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InspectNzb_RejectsMalformedNzb()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var form = NzbForm(Encoding.UTF8.GetBytes("this is not an nzb"));
        using var response = await client.PostAsync("/api/inspect-nzb", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InspectNzb_ReturnsTheReport_WithoutQueueingAnything()
    {
        var payload = Enumerable.Range(0, PayloadSize).Select(index => (byte)(index % 251)).ToArray();
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(payload, 0);
        var fake = new FakeNntpClient(
            new Dictionary<string, byte[]> { [SegmentId] = payload }, useCachedYencStreams: true);
        await using var host = new NzbDavWebApplicationFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NzbInspector>();
            services.AddSingleton(sp => new NzbInspector(
                new ArticleFromBodyNntpClient(fake), sp.GetRequiredService<ConfigManager>()));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);
        using var form = NzbForm(DirectMediaNzb(), name: "Movie.2026.1080p");

        using var response = await client.PostAsync("/api/inspect-nzb", form);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var inspection = json.RootElement.GetProperty("inspection");
        Assert.True(inspection.GetProperty("manifestComplete").GetBoolean(), body);
        var file = Assert.Single(inspection.GetProperty("files").EnumerateArray());
        Assert.Equal("movie.mkv", file.GetProperty("rawPath").GetString());
        Assert.Equal(PayloadSize, file.GetProperty("size").GetInt64());
        Assert.Empty(inspection.GetProperty("archives").EnumerateArray());

        await using var context = new DavDatabaseContext();
        Assert.Equal(0, await context.QueueItems.CountAsync());
        Assert.Equal(0, await context.HistoryItems.CountAsync());
    }

    [Fact]
    public async Task InspectNzb_RejectsAConcurrentInspectionWith429_AndAdmitsTheNextOne()
    {
        using var gated = new GatedNntpClient(new ArticleFromBodyNntpClient(DirectMediaFake()));
        await using var host = new NzbDavWebApplicationFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<NzbInspector>();
            services.AddSingleton(sp => new NzbInspector(gated, sp.GetRequiredService<ConfigManager>()));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", NzbDavWebApplicationFactory.ApiKey);

        using var firstForm = NzbForm(DirectMediaNzb());
        var first = client.PostAsync("/api/inspect-nzb", firstForm);
        try
        {
            // The first inspection is now inside the provider fetch and holds the slot.
            await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var concurrentForm = NzbForm(DirectMediaNzb());
            using var concurrent = await client.PostAsync("/api/inspect-nzb", concurrentForm)
                .WaitAsync(TimeSpan.FromSeconds(10));
            using var problem = await AdminProblemAssertions.AssertProblemAsync(
                concurrent, HttpStatusCode.TooManyRequests, "already in progress");
            Assert.Equal(1, gated.Requests);
        }
        finally
        {
            gated.Release.TrySetResult();
        }

        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Positive control: once the running inspection finishes, the next one is admitted.
        using var nextForm = NzbForm(DirectMediaNzb());
        using var next = await client.PostAsync("/api/inspect-nzb", nextForm);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public void AdminContract_DeclaresTheMultipartRequestAndTypedResponses()
    {
        // The committed contract is pinned to the generated document by AdminOpenApiIntegrationTests.
        using var contract = JsonDocument.Parse(File.ReadAllText(
            Path.Join(RepoPaths.FindRepoRoot(), "contracts", "openapi", "admin-v1.json")));
        var post = contract.RootElement.GetProperty("paths").GetProperty("/api/inspect-nzb").GetProperty("post");

        var form = post.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        var fields = form.GetProperty("properties");
        Assert.Equal("string", fields.GetProperty("nzbFile").GetProperty("type").GetString());
        Assert.Equal("binary", fields.GetProperty("nzbFile").GetProperty("format").GetString());
        Assert.Equal("string", fields.GetProperty("password").GetProperty("type").GetString());
        Assert.Equal("string", fields.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("nzbFile", Assert.Single(form.GetProperty("required").EnumerateArray()).GetString());

        var ok = post.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/InspectNzbResponse", ok);
        var schemas = contract.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.GetProperty("InspectNzbResponse").GetProperty("properties").TryGetProperty("inspection", out _));
        Assert.True(post.GetProperty("responses").TryGetProperty("429", out _));
    }

    private static FakeNntpClient DirectMediaFake()
    {
        var payload = Enumerable.Range(0, PayloadSize).Select(index => (byte)(index % 251)).ToArray();
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(payload, 0);
        return new FakeNntpClient(new Dictionary<string, byte[]> { [SegmentId] = payload }, useCachedYencStreams: true);
    }

    private static byte[] DirectMediaNzb() =>
        Encoding.UTF8.GetBytes(
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="&quot;movie.mkv&quot; yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="{PayloadSize}" number="1">{SegmentId}</segment>
                </segments>
              </file>
            </nzb>
            """);

    private static MultipartFormDataContent NzbForm(byte[] nzb, string? name = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(nzb);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-nzb");
        form.Add(file, "nzbFile", "release.nzb");
        if (name is not null)
            form.Add(new StringContent(name), "name");
        return form;
    }
}

/// <summary>Holds every article request until <see cref="Release"/> completes.</summary>
file sealed class GatedNntpClient(INntpClient inner) : WrappingNntpClient(inner)
{
    private int _requests;

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Requests => Volatile.Read(ref _requests);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        DecodedArticleAsync(segmentId, null, cancellationToken);

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }
}
