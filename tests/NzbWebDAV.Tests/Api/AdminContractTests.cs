using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class AdminContractTests
{
    [Fact]
    public async Task FileRecheck_QueuesOnlyTheSelectedFile()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var scheduledAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        var selected = NewUncheckedUsenetFile("selected.mkv");
        selected.NextHealthCheck = scheduledAt;
        var other = NewUncheckedUsenetFile("other.mkv");
        other.NextHealthCheck = scheduledAt;
        await factory.AddDavItemsAsync(selected, other);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(selected.Id.ToString()), "davItemId");
        using var response = await client.PostAsync("/api/recheck-file", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "admin/v1/recheck-file.schema.json");
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>().Ctx;
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, (await context.Items.FindAsync(selected.Id))!.NextHealthCheck);
        Assert.Equal(other.NextHealthCheck, (await context.Items.FindAsync(other.Id))!.NextHealthCheck);
    }

    [Fact]
    public async Task FileRecheck_NeverCheckedFileKeepsInitialScanPriority()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var file = NewUncheckedUsenetFile("unchecked.mkv");
        await factory.AddDavItemsAsync(file);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(file.Id.ToString()), "davItemId");
        using var response = await client.PostAsync("/api/recheck-file", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("already-queued", json.RootElement.GetProperty("state").GetString());
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>().Ctx;
        Assert.Null((await context.Items.FindAsync(file.Id))!.NextHealthCheck);
    }

    [Fact]
    public async Task FileRecheck_PreservesUrgentPendingAndHistoricalState()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var file = NewUncheckedUsenetFile("urgent.mkv");
        file.NextHealthCheck = DateTimeOffset.UnixEpoch;
        file.UrgentRepairFailures = 7;
        file.HealthRepairPending = true;
        file.LastHealthCheck = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        await factory.AddDavItemsAsync(file);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(file.Id.ToString()), "davItemId");
            using var response = await client.PostAsync("/api/recheck-file", form);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("already-queued", json.RootElement.GetProperty("state").GetString());
        }
        using var scope = factory.Services.CreateScope();
        var current = await scope.ServiceProvider.GetRequiredService<DavDatabaseClient>().Ctx.Items.FindAsync(file.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch, current!.NextHealthCheck);
        Assert.Equal(7, current.UrgentRepairFailures);
        Assert.True(current.HealthRepairPending);
        Assert.Equal(file.LastHealthCheck, current.LastHealthCheck);
    }

    [Fact]
    public async Task FileRecheck_RejectsGetInvalidIdDirectoryAndDisabledRepairs()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var get = await client.GetAsync("/api/recheck-file");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        foreach (var id in new[] { "invalid", Guid.Empty.ToString() })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(id), "davItemId");
            using var response = await client.PostAsync("/api/recheck-file", form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var directory = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "directory", null, DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, null, null);
        await factory.AddDavItemsAsync(directory);
        using var directoryForm = new MultipartFormDataContent();
        directoryForm.Add(new StringContent(directory.Id.ToString()), "davItemId");
        using var conflict = await client.PostAsync("/api/recheck-file", directoryForm);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var duplicateForm = new MultipartFormDataContent();
        duplicateForm.Add(new StringContent(directory.Id.ToString()), "davItemId");
        duplicateForm.Add(new StringContent(directory.Id.ToString()), "davItemId");
        using var duplicate = await client.PostAsync("/api/recheck-file", duplicateForm);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var serviceScope = factory.Services.CreateScope();
        serviceScope.ServiceProvider.GetRequiredService<ConfigManager>().UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "false" }]);
        var disabledFile = NewUncheckedUsenetFile("disabled.mkv");
        await factory.AddDavItemsAsync(disabledFile);
        using var disabledForm = new MultipartFormDataContent();
        disabledForm.Add(new StringContent(disabledFile.Id.ToString()), "davItemId");
        using var disabled = await client.PostAsync("/api/recheck-file", disabledForm);
        await AdminProblemAssertions.AssertProblemAsync(
            disabled, HttpStatusCode.Conflict, "Enable Background Repairs is off");
        Assert.Null((await serviceScope.ServiceProvider.GetRequiredService<DavDatabaseClient>().Ctx.Items.FindAsync(disabledFile.Id))!.NextHealthCheck);
    }

    [Fact]
    public async Task FilesBrowse_RequiresAuthenticationAndMatchesSchema()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync("/api/browse-files");
        await AdminProblemAssertions.AssertProblemAsync(denied, HttpStatusCode.Unauthorized, "API Key Required");
        using var client = factory.CreateAuthenticatedClient();
        var file = NewUncheckedUsenetFile("synthetic.mkv");
        await factory.AddDavItemsAsync(file);
        using var response = await client.GetAsync("/api/browse-files?mode=list");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonContractValidator.AssertMatchesSchema(json.RootElement, "admin/v1/browse-files.schema.json");
        var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(file.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("unknown", row.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("nextCheckAt").ValueKind);
        Assert.False(row.TryGetProperty("generatedStrmTarget", out _));
        Assert.False(row.TryGetProperty("fileBlobId", out _));
    }

    [Fact]
    public async Task FilesBrowse_InvalidFiltersAndScopeReturnProblems()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var file = NewUncheckedUsenetFile("synthetic.mkv");
        await factory.AddDavItemsAsync(file);
        foreach (var query in new[] { "limit=201", "limit=1&limit=2", "health=bad", "parentPath=/nzbs", "scopePath=" + file.Path })
        {
            using var response = await client.GetAsync("/api/browse-files?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        using var missing = await client.GetAsync("/api/browse-files?scopePath=/content/missing");
        await AdminProblemAssertions.AssertProblemAsync(missing, HttpStatusCode.NotFound, "Directory not found");
    }

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

    [Fact]
    public async Task ActionNeededHealthChecks_RequeuesOnlyFilesWhoseLatestResultNeedsAction()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        var current = NewScheduledUsenetFile("current-action-needed.mkv");
        var stale = NewScheduledUsenetFile("stale-action-needed.mkv");
        await factory.AddDavItemsAsync(current, stale);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            context.HealthCheckResults.AddRange(
                NewHealthResult(current, now.AddMinutes(-2), HealthCheckResult.RepairAction.None),
                NewHealthResult(current, now.AddMinutes(-1), HealthCheckResult.RepairAction.ActionNeeded),
                NewHealthResult(stale, now.AddMinutes(-2), HealthCheckResult.RepairAction.ActionNeeded),
                NewHealthResult(stale, now.AddMinutes(-1), HealthCheckResult.RepairAction.Repaired));
            await context.SaveChangesAsync();
        }

        using var response = await client.PostAsync(
            "/api/requeue-action-needed-health-checks",
            content: null);
        using var json = await SabContractAssertions.AssertSuccessAsync(response);

        JsonContractValidator.AssertMatchesSchema(
            json.RootElement,
            "admin/v1/requeue-action-needed-health-checks.schema.json");
        Assert.Equal(1, json.RootElement.GetProperty("requeuedCount").GetInt32());
        using var verifyScope = factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var updated = await verifyContext.Items
            .Where(x => x.Id == current.Id || x.Id == stale.Id)
            .ToDictionaryAsync(x => x.Id);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, updated[current.Id].NextHealthCheck);
        Assert.NotEqual(HealthCheckService.ForcedRecheckSentinel, updated[stale.Id].NextHealthCheck);
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

    private static DavItem NewScheduledUsenetFile(string name)
    {
        var item = NewUncheckedUsenetFile(name);
        item.NextHealthCheck = DateTimeOffset.UtcNow.AddDays(1);
        return item;
    }

    private static HealthCheckResult NewHealthResult(
        DavItem item,
        DateTimeOffset createdAt,
        HealthCheckResult.RepairAction repairStatus) => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            DavItemId = item.Id,
            Path = item.Path,
            Result = repairStatus == HealthCheckResult.RepairAction.None
                ? HealthCheckResult.HealthResult.Healthy
                : HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = repairStatus,
        };

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
