using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class SabHttpContractTests
{
    [Fact]
    public async Task AddFileQueueDeleteHistoryRetry_UsesStableSabContracts()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var pauseResponse = await client.PostAsync("/api?mode=pause&output=json", content: null);
        await SabContractAssertions.AssertSuccessAsync(pauseResponse);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(TestNzbs.SingleFile), "name", "sample.nzb");
        using var addResponse = await client.PostAsync("/api?mode=addfile&output=json&cat=tv", form);
        using var addJson = await SabContractAssertions.AssertSuccessAsync(addResponse);
        JsonContractValidator.AssertMatchesSchema(addJson.RootElement, "sab/v1/addfile.schema.json");
        var nzoId = Assert.Single(addJson.RootElement.GetProperty("nzo_ids").EnumerateArray())
            .GetString();
        Assert.True(Guid.TryParse(nzoId, out var queuedId));

        using var queueResponse = await client.GetAsync("/api?mode=queue&output=json");
        using var queueJson = await SabContractAssertions.AssertSuccessAsync(queueResponse);
        JsonContractValidator.AssertMatchesSchema(queueJson.RootElement, "sab/v1/queue.schema.json");
        var queue = queueJson.RootElement.GetProperty("queue");
        Assert.Equal(JsonValueKind.True, queue.GetProperty("paused").ValueKind);
        Assert.Equal(JsonValueKind.Number, queue.GetProperty("noofslots").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("speedlimit").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("speedlimit_abs").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("pause_int").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("speed").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("kbpersec").ValueKind);
        Assert.Equal(JsonValueKind.String, queue.GetProperty("timeleft").ValueKind);
        var slot = Assert.Single(queue.GetProperty("slots").EnumerateArray());
        SabContractAssertions.AssertQueueSlotShape(slot);
        Assert.Equal(nzoId, slot.GetProperty("nzo_id").GetString());
        Assert.Equal("sample.nzb", slot.GetProperty("filename").GetString());
        Assert.Equal("tv", slot.GetProperty("cat").GetString());

        using var deleteResponse = await client.GetAsync(
            $"/api?mode=queue&name=delete&value={queuedId}&output=json");
        await SabContractAssertions.AssertSuccessAsync(deleteResponse);

        using var emptyQueueResponse = await client.GetAsync("/api?mode=queue&output=json");
        using var emptyQueueJson = await SabContractAssertions.AssertSuccessAsync(emptyQueueResponse);
        JsonContractValidator.AssertMatchesSchema(emptyQueueJson.RootElement, "sab/v1/queue.schema.json");
        Assert.Empty(emptyQueueJson.RootElement.GetProperty("queue").GetProperty("slots").EnumerateArray());
        Assert.Equal(0, emptyQueueJson.RootElement.GetProperty("queue").GetProperty("noofslots").GetInt32());

        using var emptyHistoryResponse = await client.GetAsync("/api?mode=history&output=json");
        using var emptyHistoryJson = await SabContractAssertions.AssertSuccessAsync(emptyHistoryResponse);
        JsonContractValidator.AssertMatchesSchema(emptyHistoryJson.RootElement, "sab/v1/history.schema.json");
        Assert.Empty(
            emptyHistoryJson.RootElement.GetProperty("history").GetProperty("slots").EnumerateArray());

        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(historyId, fileName: "sample.nzb", category: "tv");

        using var historyResponse = await client.GetAsync("/api?mode=history&output=json");
        using var historyJson = await SabContractAssertions.AssertSuccessAsync(historyResponse);
        JsonContractValidator.AssertMatchesSchema(historyJson.RootElement, "sab/v1/history.schema.json");
        var history = historyJson.RootElement.GetProperty("history");
        Assert.Equal(JsonValueKind.Number, history.GetProperty("noofslots").ValueKind);
        var historySlot = Assert.Single(history.GetProperty("slots").EnumerateArray());
        SabContractAssertions.AssertHistorySlotShape(historySlot);
        Assert.Equal(historyId.ToString(), historySlot.GetProperty("nzo_id").GetString());
        Assert.Equal("Failed", historySlot.GetProperty("status").GetString());
        Assert.Equal("sample.nzb", historySlot.GetProperty("nzb_name").GetString());

        using var retryResponse = await client.GetAsync(
            $"/api?mode=retry&value={historyId}&output=json");
        using var retryJson = await SabContractAssertions.AssertSuccessAsync(retryResponse);
        JsonContractValidator.AssertMatchesSchema(retryJson.RootElement, "sab/v1/retry.schema.json");
        var retriedId = retryJson.RootElement.GetProperty("nzo_id").GetString();
        Assert.True(Guid.TryParse(retriedId, out var newQueueId));
        Assert.NotEqual(historyId, newQueueId);
        var retriedIds = retryJson.RootElement.GetProperty("nzo_ids").EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        Assert.Contains(retriedId, retriedIds);

        using var retriedQueueResponse = await client.GetAsync("/api?mode=queue&output=json");
        using var retriedQueueJson = await SabContractAssertions.AssertSuccessAsync(retriedQueueResponse);
        var retriedSlot = Assert.Single(
            retriedQueueJson.RootElement.GetProperty("queue").GetProperty("slots").EnumerateArray());
        Assert.Equal(retriedId, retriedSlot.GetProperty("nzo_id").GetString());
        Assert.Equal("sample.nzb", retriedSlot.GetProperty("filename").GetString());

        using var keptHistoryResponse = await client.GetAsync("/api?mode=history&output=json");
        using var keptHistoryJson = await SabContractAssertions.AssertSuccessAsync(keptHistoryResponse);
        var keptSlot = Assert.Single(
            keptHistoryJson.RootElement.GetProperty("history").GetProperty("slots").EnumerateArray());
        Assert.Equal(historyId.ToString(), keptSlot.GetProperty("nzo_id").GetString());
    }

    [Fact]
    public async Task AuthenticationInvalidModeAndMalformedRequests_UseStatusErrorEnvelope()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateAuthenticatedClient();

        using var missingKey = await anonymous.GetAsync("/api?mode=queue&output=json");
        using var missingKeyJson = await SabContractAssertions.AssertFailureAsync(
            missingKey, HttpStatusCode.Unauthorized, "API Key Required");
        JsonContractValidator.AssertMatchesSchema(missingKeyJson.RootElement, "sab/v1/error.schema.json");

        using var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api?mode=queue&output=json");
        wrongKeyRequest.Headers.Add("x-api-key", "not-the-key");
        using var wrongKey = await anonymous.SendAsync(wrongKeyRequest);
        await SabContractAssertions.AssertFailureAsync(
            wrongKey, HttpStatusCode.Unauthorized, "API Key Incorrect");

        using var queryKey = await anonymous.GetAsync(
            $"/api?mode=queue&output=json&apikey={NzbDavWebApplicationFactory.ApiKey}");
        await SabContractAssertions.AssertSuccessAsync(queryKey);

        using var invalidMode = await client.GetAsync("/api?mode=not-a-mode&output=json");
        await SabContractAssertions.AssertFailureAsync(
            invalidMode, HttpStatusCode.BadRequest, "Invalid mode");

        using var emptyForm = new MultipartFormDataContent();
        emptyForm.Add(new StringContent("ignored"), "notfile");
        using var malformedAdd = await client.PostAsync("/api?mode=addfile&output=json", emptyForm);
        await SabContractAssertions.AssertFailureAsync(
            malformedAdd, HttpStatusCode.BadRequest, "nzbFile");

        using var malformedHistory = await client.GetAsync(
            "/api?mode=history&output=json&nzo_ids=not-a-guid");
        await SabContractAssertions.AssertFailureAsync(
            malformedHistory, HttpStatusCode.BadRequest, "Invalid nzo_ids");

        using var serverErrorBody = new StringContent(
            "{}", new MediaTypeHeaderValue("application/json"));
        using var serverError = await client.PostAsync(
            "/api?mode=addfile&output=json",
            serverErrorBody);
        await SabContractAssertions.AssertFailureAsync(
            serverError, HttpStatusCode.InternalServerError, "internal server error");
    }
}
