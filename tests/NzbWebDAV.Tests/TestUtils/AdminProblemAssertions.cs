using System.Net;
using System.Text.Json;

namespace NzbWebDAV.Tests.TestUtils;

internal static class AdminProblemAssertions
{
    public static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string? detailOrTitleContains = null)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal((int)statusCode, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("type").ValueKind);
        Assert.StartsWith(
            "https://www.infinidysk.com/problems/",
            json.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("detail").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("traceId").ValueKind);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var correlation));
        Assert.Equal(Assert.Single(correlation), json.RootElement.GetProperty("traceId").GetString());
        if (statusCode == HttpStatusCode.InternalServerError)
        {
            Assert.DoesNotContain(
                "Exception",
                json.RootElement.GetProperty("detail").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (detailOrTitleContains is not null)
        {
            var haystack =
                json.RootElement.GetProperty("detail").GetString()
                + json.RootElement.GetProperty("title").GetString();
            Assert.Contains(detailOrTitleContains, haystack, StringComparison.OrdinalIgnoreCase);
        }

        return json;
    }
}
