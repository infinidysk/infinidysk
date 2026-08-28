using System.Net;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
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
        JsonContractValidator.AssertMatchesSchema(initialJson.RootElement, "admin/v1/get-config.schema.json");

        using var updateForm = new MultipartFormDataContent();
        updateForm.Add(new StringContent("true"), ConfigKeys.WebdavShowHiddenFiles);
        using var update = await client.PostAsync("/api/update-config", updateForm);
        using var updateJson = await SabContractAssertions.AssertSuccessAsync(update);
        JsonContractValidator.AssertMatchesSchema(updateJson.RootElement, "admin/v1/update-config.schema.json");

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
        JsonContractValidator.AssertMatchesSchema(
            beforeJson.RootElement, "admin/v1/health-check-queue.schema.json");
        Assert.Equal(JsonValueKind.Number, beforeJson.RootElement.GetProperty("uncheckedCount").ValueKind);
        var queued = Assert.Single(
            beforeJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == scheduled.Id.ToString());
        Assert.Equal("contract-health.mkv", queued.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.String, queued.GetProperty("path").ValueKind);

        using var trigger = await client.PostAsync("/api/reset-health-check-queue", content: null);
        using var triggerJson = await SabContractAssertions.AssertSuccessAsync(trigger);
        JsonContractValidator.AssertMatchesSchema(
            triggerJson.RootElement, "admin/v1/reset-health-check-queue.schema.json");
        Assert.Equal(JsonValueKind.Number, triggerJson.RootElement.GetProperty("resetCount").ValueKind);
        Assert.Equal(1, triggerJson.RootElement.GetProperty("resetCount").GetInt32());

        using var after = await client.GetAsync("/api/get-health-check-queue?pageSize=30");
        using var afterJson = await JsonDocument.ParseAsync(await after.Content.ReadAsStreamAsync());
        var resetItem = Assert.Single(
            afterJson.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == scheduled.Id.ToString());
        // The reset marks files with the forced-recheck sentinel (not null) so the re-check
        // also covers files still linked to SAB history; the queue API surfaces that marker.
        Assert.Equal(JsonValueKind.String, resetItem.GetProperty("nextHealthCheck").ValueKind);
        Assert.Equal(
            HealthCheckService.ForcedRecheckSentinel,
            resetItem.GetProperty("nextHealthCheck").GetDateTimeOffset());
        Assert.True(afterJson.RootElement.GetProperty("uncheckedCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task HealthCheckQueue_UncheckedCountExcludesNonMediaFiles()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        // Three health-check candidates and four sidecar files, all never checked.
        // Only candidates may count toward the Health UI "initial scan pending" banner.
        await factory.AddDavItemsAsync(
            NewUncheckedUsenetFile("movie.mkv"),
            NewUncheckedUsenetFile("track.flac"),
            NewUncheckedUsenetFile("archive.rar"),
            NewUncheckedUsenetFile("cover.jpg"),
            NewUncheckedUsenetFile("subs.srt"),
            NewUncheckedUsenetFile("info.nfo"),
            NewUncheckedUsenetFile("checksums.par2"));

        using var response = await client.GetAsync("/api/get-health-check-queue?pageSize=30");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, json.RootElement.GetProperty("uncheckedCount").GetInt32());
    }

    private static DavItem NewUncheckedUsenetFile(string name) => DavItem.New(
        Guid.NewGuid(),
        DavItem.ContentFolder,
        name,
        fileSize: 100,
        DavItem.ItemType.UsenetFile,
        DavItem.ItemSubType.NzbFile,
        releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
        lastHealthCheck: null,
        historyItemId: null,
        fileBlobId: null);

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
        JsonContractValidator.AssertMatchesSchema(
            json.RootElement, "admin/v1/list-webdav-directory.schema.json");
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
        await AdminProblemAssertions.AssertProblemAsync(
            missingKey, HttpStatusCode.Unauthorized, "API Key Required");

        using var missingDirectoryForm = new MultipartFormDataContent();
        missingDirectoryForm.Add(new StringContent("/content/does-not-exist"), "directory");
        using var missingDirectory = await client.PostAsync(
            "/api/list-webdav-directory", missingDirectoryForm);
        await AdminProblemAssertions.AssertProblemAsync(
            missingDirectory, HttpStatusCode.BadRequest, "does not exist");

        using var readonlyDeleteForm = new MultipartFormDataContent();
        readonlyDeleteForm.Add(new StringContent("/content/does-not-exist"), "path");
        using var readonlyDelete = await client.PostAsync("/api/delete-webdav-item", readonlyDeleteForm);
        await AdminProblemAssertions.AssertProblemAsync(
            readonlyDelete, HttpStatusCode.Forbidden, "read-only");

        using var disableReadonly = new MultipartFormDataContent();
        disableReadonly.Add(new StringContent("false"), ConfigKeys.WebdavEnforceReadonly);
        using var updated = await client.PostAsync("/api/update-config", disableReadonly);
        await SabContractAssertions.AssertSuccessAsync(updated);

        using var missingItemForm = new MultipartFormDataContent();
        missingItemForm.Add(new StringContent("/content/does-not-exist"), "path");
        using var missingItem = await client.PostAsync("/api/delete-webdav-item", missingItemForm);
        await AdminProblemAssertions.AssertProblemAsync(
            missingItem, HttpStatusCode.NotFound, "Item not found");

        using var serverError = await client.GetAsync("/api/get-config");
        await AdminProblemAssertions.AssertProblemAsync(
            serverError, HttpStatusCode.InternalServerError, "trace ID");
        Assert.Equal(
            "application/problem+json",
            serverError.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<HttpResponseMessage> PostConfigKeysAsync(HttpClient client, params string[] keys)
    {
        using var form = new MultipartFormDataContent();
        foreach (var key in keys)
            form.Add(new StringContent(key), "config-keys");
        return await client.PostAsync("/api/get-config", form);
    }
}
