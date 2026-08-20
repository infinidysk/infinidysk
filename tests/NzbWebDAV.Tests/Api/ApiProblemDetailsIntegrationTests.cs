using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class ApiProblemDetailsIntegrationTests
{
    [Fact]
    public async Task AdminUnhandledException_ReturnsSanitizedProblemWithMatchingTrace()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/get-config");
        using var json = await AdminProblemAssertions.AssertProblemAsync(
            response, HttpStatusCode.InternalServerError, "trace ID");

        var traceId = json.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        var sink = factory.Services.GetRequiredService<LogBufferSink>();
        Assert.Contains(
            sink.Snapshot(50, ["Error"], source: null, search: null, beforeSequence: null).Entries,
            entry => entry.TraceId == traceId);
    }

    [Fact]
    public async Task IncomingCorrelationHeader_IsEchoedWhenValid()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/get-health-check-queue");
        request.Headers.Add("X-Correlation-ID", "ops.trace_1");
        using var response = await client.SendAsync(request);
        using var json = await AdminProblemAssertions.AssertProblemAsync(
            response, HttpStatusCode.Unauthorized, "API Key");
        Assert.Equal("ops.trace_1", json.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task IncomingCorrelationHeader_IsIgnoredWhenUnsafe()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/get-health-check-queue");
        request.Headers.Add("X-Correlation-ID", "not a valid id!");
        using var response = await client.SendAsync(request);
        using var json = await AdminProblemAssertions.AssertProblemAsync(
            response, HttpStatusCode.Unauthorized, "API Key");
        Assert.NotEqual("not a valid id!", json.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public void ValidationProblem_IncludesPerFieldArrays()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-validation";
        var problem = ApiProblemDetailsFactory.Validation(
            context,
            new Dictionary<string, string[]> { ["host"] = ["Host is required."] });
        var payload = ApiProblemDetailsFactory.ToWritablePayload(problem);
        Assert.Equal(400, payload["status"]);
        var errors = Assert.IsAssignableFrom<IDictionary<string, string[]>>(payload["errors"]);
        Assert.Equal(["Host is required."], errors["host"]);
        Assert.Equal("trace-validation", payload["traceId"]);
    }

    [Fact]
    public void RequestCorrelation_RejectsOversizedIncomingIds()
    {
        Assert.False(RequestCorrelation.TrySanitizeIncoming(new string('a', 129), out _));
        Assert.True(RequestCorrelation.TrySanitizeIncoming("abc.DEF-12_3", out var sanitized));
        Assert.Equal("abc.DEF-12_3", sanitized);
    }
}
