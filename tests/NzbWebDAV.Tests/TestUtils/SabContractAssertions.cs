using System.Net;
using System.Text.Json;

namespace NzbWebDAV.Tests.TestUtils;

internal static class SabContractAssertions
{
    public static async Task<JsonDocument> AssertSuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await ParseJsonAsync(response);
        Assert.True(json.RootElement.GetProperty("status").GetBoolean());
        if (json.RootElement.TryGetProperty("error", out var error) &&
            error.ValueKind is not JsonValueKind.Null)
        {
            Assert.True(string.IsNullOrEmpty(error.GetString()));
        }

        return json;
    }

    public static async Task<JsonDocument> AssertFailureAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string? errorContains = null)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await ParseJsonAsync(response);
        Assert.False(json.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("error").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("type", out _));
        Assert.False(json.RootElement.TryGetProperty("title", out _));
        Assert.False(json.RootElement.TryGetProperty("traceId", out _));
        if (errorContains is not null)
        {
            Assert.Contains(
                errorContains,
                json.RootElement.GetProperty("error").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }

        return json;
    }

    public static void AssertQueueSlotShape(JsonElement slot)
    {
        Assert.Equal(JsonValueKind.Number, slot.GetProperty("index").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("nzo_id").ValueKind);
        Assert.True(Guid.TryParse(slot.GetProperty("nzo_id").GetString(), out _));
        Assert.Equal(JsonValueKind.String, slot.GetProperty("priority").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("filename").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("cat").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("percentage").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("true_percentage").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("timeleft").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("mb").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("mbleft").ValueKind);
    }

    public static void AssertHistorySlotShape(JsonElement slot)
    {
        Assert.Equal(JsonValueKind.String, slot.GetProperty("nzo_id").ValueKind);
        Assert.True(Guid.TryParse(slot.GetProperty("nzo_id").GetString(), out _));
        Assert.Equal(JsonValueKind.String, slot.GetProperty("nzb_name").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.String, slot.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Number, slot.GetProperty("bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, slot.GetProperty("download_time").ValueKind);
        Assert.Equal(JsonValueKind.Number, slot.GetProperty("completed").ValueKind);
        Assert.True(
            slot.GetProperty("fail_message").ValueKind is JsonValueKind.String or JsonValueKind.Null);
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response)
    {
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }
}
