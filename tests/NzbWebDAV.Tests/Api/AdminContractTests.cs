using System.Net;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class AdminContractTests
{
    [Fact]
    public async Task SettingsReadUpdateRead_UsesStableAdminContracts()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var initial = await PostConfigKeysAsync(client, ConfigKeys.WebdavShowHiddenFiles);
        using var initialJson = await JsonDocument.ParseAsync(await initial.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.True(initialJson.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(JsonValueKind.Array, initialJson.RootElement.GetProperty("configItems").ValueKind);

        using var updateForm = new MultipartFormDataContent();
        updateForm.Add(new StringContent("true"), ConfigKeys.WebdavShowHiddenFiles);
        using var update = await client.PostAsync("/api/update-config", updateForm);
        using var updateJson = await SabContractAssertions.AssertSuccessAsync(update);

        using var reread = await PostConfigKeysAsync(client, ConfigKeys.WebdavShowHiddenFiles);
        using var rereadJson = await JsonDocument.ParseAsync(await reread.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        var item = Assert.Single(rereadJson.RootElement.GetProperty("configItems").EnumerateArray());
        Assert.Equal(ConfigKeys.WebdavShowHiddenFiles, item.GetProperty("configName").GetString());
        Assert.Equal("true", item.GetProperty("configValue").GetString());
        Assert.True(item.TryGetProperty("environmentVariableName", out _));
    }

    [Fact]
    public async Task HealthCheckTrigger_ResetsScheduledItemIntoObservableQueue()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        var scheduled = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "contract-health.mkv",
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-1),
            historyItemId: null,
            fileBlobId: null);
        await factory.AddDavItemsAsync(scheduled);

        using var before = await client.GetAsync("/api/get-health-check-queue?pageSize=30");
        using var beforeJson = await JsonDocument.ParseAsync(await before.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.True(beforeJson.RootElement.GetProperty("status").GetBoolean());
        Assert.Equal(JsonValueKind.Number, beforeJson.RootElement.GetProperty("uncheckedCount").ValueKind);
        var queued = Assert.Single(
            beforeJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == scheduled.Id.ToString());
        Assert.Equal("contract-health.mkv", queued.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.String, queued.GetProperty("path").ValueKind);

        using var trigger = await client.PostAsync("/api/reset-health-check-queue", content: null);
        using var triggerJson = await SabContractAssertions.AssertSuccessAsync(trigger);
        Assert.Equal(JsonValueKind.Number, triggerJson.RootElement.GetProperty("resetCount").ValueKind);
        Assert.Equal(1, triggerJson.RootElement.GetProperty("resetCount").GetInt32());

        using var after = await client.GetAsync("/api/get-health-check-queue?pageSize=30");
        using var afterJson = await JsonDocument.ParseAsync(await after.Content.ReadAsStreamAsync());
        var resetItem = Assert.Single(
            afterJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == scheduled.Id.ToString());
        Assert.Equal(JsonValueKind.Null, resetItem.GetProperty("nextHealthCheck").ValueKind);
        Assert.True(afterJson.RootElement.GetProperty("uncheckedCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task WebDavItemList_ReturnsSeededDirectoryChildren()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        var folder = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "contract-library",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
        await factory.AddDavItemsAsync(folder);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("/content"), "directory");
        using var response = await client.PostAsync("/api/list-webdav-directory", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("items").ValueKind);
        Assert.Contains(
            json.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "contract-library"
                    && item.GetProperty("isDirectory").GetBoolean());
    }

    [Fact]
    public async Task AdminAuthenticationAndErrorEnvelopes_StayStatusErrorShaped()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateAuthenticatedClient();

        using var missingKey = await anonymous.GetAsync("/api/get-health-check-queue");
        await SabContractAssertions.AssertFailureAsync(
            missingKey, HttpStatusCode.Unauthorized, "API Key Required");

        using var missingDirectoryForm = new MultipartFormDataContent();
        missingDirectoryForm.Add(new StringContent("/content/does-not-exist"), "directory");
        using var missingDirectory = await client.PostAsync(
            "/api/list-webdav-directory", missingDirectoryForm);
        await SabContractAssertions.AssertFailureAsync(
            missingDirectory, HttpStatusCode.BadRequest, "does not exist");

        using var readonlyDeleteForm = new MultipartFormDataContent();
        readonlyDeleteForm.Add(new StringContent("/content/does-not-exist"), "path");
        using var readonlyDelete = await client.PostAsync("/api/delete-webdav-item", readonlyDeleteForm);
        await SabContractAssertions.AssertFailureAsync(
            readonlyDelete, HttpStatusCode.Forbidden, "read-only");

        using var disableReadonly = new MultipartFormDataContent();
        disableReadonly.Add(new StringContent("false"), ConfigKeys.WebdavEnforceReadonly);
        using var updated = await client.PostAsync("/api/update-config", disableReadonly);
        await SabContractAssertions.AssertSuccessAsync(updated);

        using var missingItemForm = new MultipartFormDataContent();
        missingItemForm.Add(new StringContent("/content/does-not-exist"), "path");
        using var missingItem = await client.PostAsync("/api/delete-webdav-item", missingItemForm);
        await SabContractAssertions.AssertFailureAsync(
            missingItem, HttpStatusCode.NotFound, "Item not found");

        using var serverError = await client.GetAsync("/api/get-config");
        await SabContractAssertions.AssertFailureAsync(
            serverError, HttpStatusCode.InternalServerError, "internal server error");
        Assert.Equal(
            "application/json",
            serverError.Content.Headers.ContentType?.MediaType);
    }

    private static Task<HttpResponseMessage> PostConfigKeysAsync(HttpClient client, params string[] keys)
    {
        var form = new MultipartFormDataContent();
        foreach (var key in keys)
            form.Add(new StringContent(key), "config-keys");
        return client.PostAsync("/api/get-config", form);
    }
}
