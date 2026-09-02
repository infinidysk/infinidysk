using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

public sealed class ConfigUpdateService(
    DavDatabaseClient dbClient,
    ConfigManager configManager)
{
    public async Task<ConfigUpdateBatch> StageAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default)
    {
        RejectEnvironmentManagedItems(configItems);
        ConfigManager.ValidateConfigItems(configItems);
        configManager.ValidateQueueAdmissionSettings(configItems);

        if (configItems.Count == 0)
            return new ConfigUpdateBatch([]);

        var configNames = configItems
            .Select(item => item.ConfigName)
            .ToHashSet(StringComparer.Ordinal);
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(item => configNames.Contains(item.ConfigName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingItemsByName = existingItems
            .ToDictionary(item => item.ConfigName, StringComparer.Ordinal);

        var secretMasker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var resolvedItems = configItems.Select(item =>
        {
            var existingValue = existingItemsByName
                .GetValueOrDefault(item.ConfigName)
                ?.ConfigValue;
            var resolvedValue = secretMasker.ResolveForUpdate(
                item.ConfigName,
                item.ConfigValue,
                existingValue);

            if (item.ConfigName == ConfigKeys.WebdavPass &&
                !ConfigSecretMasker.IsMaskToken(item.ConfigValue))
            {
                resolvedValue = PasswordUtil.Hash(resolvedValue);
            }

            if (item.ConfigName == ConfigKeys.UsenetProviders)
                resolvedValue = NormalizeUsenetProviderIds(resolvedValue, existingValue);

            return new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = resolvedValue,
            };
        }).ToList();

        foreach (var item in resolvedItems)
        {
            if (existingItemsByName.TryGetValue(item.ConfigName, out var existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
            }
            else
            {
                dbClient.Ctx.ConfigItems.Add(item);
            }
        }

        return new ConfigUpdateBatch(resolvedItems);
    }

    public async Task<ConfigUpdateBatch> ApplyAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default)
    {
        var batch = await StageAsync(configItems, cancellationToken).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(batch);
        return batch;
    }

    public void Publish(ConfigUpdateBatch batch) =>
        configManager.UpdateValues(batch.ResolvedItems.ToList());

    private void RejectEnvironmentManagedItems(IEnumerable<ConfigItem> configItems)
    {
        var managed = configItems
            .Where(item => configManager.IsEnvironmentManaged(item.ConfigName))
            .Select(item => item.ConfigName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (managed.Count == 0) return;

        var details = string.Join(", ", managed.Select(name =>
        {
            var environmentName = configManager.GetEnvironmentVariableName(name) ?? name;
            return $"`{name}` (managed by `{environmentName}`)";
        }));
        throw new BadHttpRequestException(
            $"Cannot update environment-managed setting(s): {details}. " +
            "Change the container environment and restart instead.");
    }

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
}

public sealed class ConfigUpdateBatch(IReadOnlyList<ConfigItem> resolvedItems)
{
    public IReadOnlyList<ConfigItem> ResolvedItems { get; } = resolvedItems;
}