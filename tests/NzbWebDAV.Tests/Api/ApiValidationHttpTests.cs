using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class ApiValidationHttpTests
{
    [Fact]
    public async Task AdminHealthHistory_InvalidQuery_ReturnsValidationProblem()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api/get-health-check-history?page=0");
        using var json = await AdminProblemAssertions.AssertProblemAsync(
            response, HttpStatusCode.BadRequest, "page");
        Assert.Equal(
            "https://www.infinidysk.com/problems/validation",
            json.RootElement.GetProperty("type").GetString());
        Assert.True(json.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("page", out var pageErrors));
        Assert.NotEmpty(pageErrors.EnumerateArray());
    }

    [Fact]
    public async Task SabHistory_MalformedNzoIds_NestsValidationProblem()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/api?mode=history&output=json&nzo_ids=not-a-guid");
        using var json = await SabContractAssertions.AssertFailureAsync(
            response, HttpStatusCode.BadRequest, "Invalid nzo_ids");
        var problem = json.RootElement.GetProperty("problem");
        Assert.Equal(
            "https://www.infinidysk.com/problems/validation",
            problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("nzo_ids", out _));
    }

    [Fact]
    public async Task SabMove_MalformedJsonBody_ReturnsValidationError()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            $"/api?mode=queue&name=move&value={Guid.NewGuid()}&value2=0",
            content);
        using var json = await SabContractAssertions.AssertFailureAsync(
            response, HttpStatusCode.BadRequest, "JSON");
        Assert.True(json.RootElement.GetProperty("problem").GetProperty("errors").TryGetProperty("body", out _));
    }

    [Fact]
    public void UpdateConfigRequest_RejectsArrayAndOversizedNames()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["dup"] = new(["one", "two"]),
            [new string('k', UpdateConfigRequest.MaxConfigNameLength + 1)] = "value",
        });

        var ex = Assert.Throws<ApiValidationException>(() => new UpdateConfigRequest(context));
        Assert.True(ex.Errors.ContainsKey("dup"));
    }
}
