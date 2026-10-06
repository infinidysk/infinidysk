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
    // One gate for the whole process, not one per instance: the service is
    // scoped. Cross-setting checks read the saved sibling values, so two updates
    // validated side by side -- a mount list and a cache directory, say -- could
    // each pass against the other's stale value and together persist a pair
    // neither would have been allowed.
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
    private static readonly AsyncLocal<bool> HoldsUpdateGate = new();

    /// <summary>
    /// Runs one configuration update -- validation, save and publish -- while no
    /// other update is in progress anywhere in the process.
    /// </summary>
    public async Task<T> WithUpdateGateAsync<T>(
        Func<Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        await UpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HoldsUpdateGate.Value = true;
            return await work().ConfigureAwait(false);
        }
        finally
        {
            HoldsUpdateGate.Value = false;
            UpdateGate.Release();
        }
    }

    /// <summary>
    /// Validates and stages an update. Must run inside
    /// <see cref="WithUpdateGateAsync{T}"/>, held until the batch is saved and
    /// published.
    /// </summary>
    public async Task<ConfigUpdateBatch> StageAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default)
    {
        if (!HoldsUpdateGate.Value)
        {
            throw new InvalidOperationException(
                "Configuration updates must be staged inside WithUpdateGateAsync.");
        }

        RejectEnvironmentManagedItems(configItems);
        // The saved values are threaded in so cross-setting checks see the
        // configuration this request would produce, not only the keys it happens
        // to carry.
        ConfigManager.ValidateConfigItems(configItems, savedValue: configManager.GetSavedConfigValue);
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
            {
                resolvedValue = NormalizeUsenetProviderIds(resolvedValue, existingValue);
                resolvedValue = configManager.PrepareUsenetProviderConfigForSave(resolvedValue);
            }

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

    public Task<ConfigUpdateBatch> ApplyAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default) =>
        WithUpdateGateAsync(
            async () =>
            {
                var batch = await StageAsync(configItems, cancellationToken).ConfigureAwait(false);
                await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                Publish(batch);
                return batch;
            },
            cancellationToken);

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