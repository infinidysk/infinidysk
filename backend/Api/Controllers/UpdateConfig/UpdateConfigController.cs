using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        // Reject ENV-managed keys before validation so malformed managed payloads
        // cannot leak submitted values through ValidateConfigItems error text.
        var managed = request.ConfigItems
            .Where(item => configManager.IsEnvironmentManaged(item.ConfigName))
            .Select(item => item.ConfigName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (managed.Count > 0)
        {
            var details = string.Join(", ", managed.Select(name =>
            {
                var envName = configManager.GetEnvironmentVariableName(name) ?? name;
                return $"`{name}` (managed by `{envName}`)";
            }));
            throw new BadHttpRequestException(
                $"Cannot update environment-managed setting(s): {details}. " +
                "Change the container environment and restart instead.");
        }

        // Validate SQLite-owned values up-front so a malformed value is rejected here
        // with a clear message instead of throwing later deep inside a request or
        // background task. Run before webdav.pass hashing so validation sees the raw
        // submitted value.
        ConfigManager.ValidateConfigItems(request.ConfigItems);
        configManager.ValidateQueueAdmissionSettings(request.ConfigItems);

        var configNames = request.ConfigItems.Select(x => x.ConfigName).ToHashSet();
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var secretMasker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var resolvedItems = request.ConfigItems.Select(item =>
        {
            var existingValue = existingItemsDict.GetValueOrDefault(item.ConfigName)?.ConfigValue;
            var resolvedValue = secretMasker.ResolveForUpdate(
                item.ConfigName,
                item.ConfigValue,
                existingValue);

            if (item.ConfigName == ConfigKeys.WebdavPass &&
                !ConfigSecretMasker.IsMaskToken(item.ConfigValue))
                resolvedValue = PasswordUtil.Hash(resolvedValue);

            if (item.ConfigName == ConfigKeys.UsenetProviders)
                resolvedValue = NormalizeUsenetProviderIds(resolvedValue, existingValue);

            return new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = resolvedValue
            };
        }).ToList();

        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in resolvedItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                itemsToInsert.Add(item);
            }
        }

        dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        configManager.UpdateValues(resolvedItems);

        return new UpdateConfigResponse { Status = true };
    }

    /// <summary>
    /// Preserve ProviderIds across config saves even when a client omits them
    /// (e.g. older UI that rebuilds ConnectionDetails without the field).
    /// </summary>
    private static string NormalizeUsenetProviderIds(string incomingJson, string? existingJson)
    {
        var incoming = JsonSerializer.Deserialize<UsenetProviderConfig>(incomingJson)
                       ?? new UsenetProviderConfig();
        UsenetProviderConfig? existing = null;
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                existing = JsonSerializer.Deserialize<UsenetProviderConfig>(existingJson);
            }
            catch (JsonException)
            {
                existing = null;
            }
        }

        UsenetProviderIdentity.NormalizeProviderIdsOnSave(incoming, existing);
        return JsonSerializer.Serialize(incoming);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
