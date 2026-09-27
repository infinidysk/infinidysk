using System.Text.Json;
using System.Runtime.CompilerServices;
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
    // ConfigManager is the process singleton; services/DbContexts are scoped. A weak key also
    // keeps isolated managers independent in tests. Only async waits are used, never WaitHandle.
    private static readonly ConditionalWeakTable<ConfigManager, SemaphoreSlim> WriteGates = new();
    private static readonly ConditionalWeakTable<ConfigManager, SemaphoreSlim> NativeValidationGates = new();

    internal static async Task ValidateNativeStorageAsync(ConfigManager config, Action validate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var admission = NativeValidationGates.GetValue(config, static _ => new SemaphoreSlim(1, 1));
        if (!admission.Wait(0, cancellationToken)) throw new ArgumentException("A previous native storage validation is still waiting on the filesystem. Restore the mount before retrying.");
        var pending = Task.Run(() =>
        {
            try { validate(); }
            finally { admission.Release(); }
        }, CancellationToken.None);
        _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ArgumentException("Native storage validation timed out. Check mounts and local metadata storage before retrying."); }
    }

    public Task<ConfigUpdateBatch> StageAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default) =>
        StageAsync(() => configItems, cancellationToken);

    /// <summary>Prepares and stages settings under a lease held until publish or batch disposal.</summary>
    public async Task<ConfigUpdateBatch> StageAsync(
        Func<IReadOnlyCollection<ConfigItem>> prepare,
        CancellationToken cancellationToken = default)
    {
        var gate = WriteGates.GetValue(configManager, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StageCoreAsync(prepare(), () => gate.Release(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private async Task<ConfigUpdateBatch> StageCoreAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        Action release,
        CancellationToken cancellationToken)
    {
        RejectEnvironmentManagedItems(configItems);
        ConfigManager.ValidateConfigItems(configItems);
        configItems = CacheModeResolver.NormalizeUpdate(configManager, configItems);
        RejectEnvironmentManagedItems(configItems);
        var nativeSettings = NzbWebDAV.Services.NativeCache.NativeCacheSettings.ProposedForUpdate(configManager, configItems);
        if (nativeSettings is not null)
            await ValidateNativeStorageAsync(configManager,
                () => NzbWebDAV.Services.NativeCache.NativeCacheSettings.ValidateStorage(nativeSettings),
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        configManager.ValidateQueueAdmissionSettings(configItems);
        var activeMode = configManager.GetActiveCacheMode();
        var submittedMode = configItems.FirstOrDefault(item => item.ConfigName == ConfigKeys.CacheMode);
        var configuredMode = submittedMode is null ? configManager.GetCacheMode()
            : CacheModeResolver.Parse(submittedMode.ConfigValue);

        if (configItems.Count == 0)
            return new ConfigUpdateBatch([], activeMode, configuredMode, release);

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

        return new ConfigUpdateBatch(resolvedItems, activeMode, configuredMode, release);
    }

    public async Task<ConfigUpdateBatch> ApplyAsync(
        IReadOnlyCollection<ConfigItem> configItems,
        CancellationToken cancellationToken = default)
    {
        using var batch = await StageAsync(configItems, cancellationToken).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(batch);
        return batch;
    }

    public void Publish(ConfigUpdateBatch batch)
    {
        try
        {
            configManager.UpdateValues(batch.ResolvedItems.ToList());
        }
        finally
        {
            batch.Dispose();
        }
    }

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


public sealed class ConfigUpdateBatch(
    IReadOnlyList<ConfigItem> resolvedItems,
    CacheMode activeCacheMode,
    CacheMode configuredCacheMode,
    Action release) : IDisposable
{
    private Action? _release = release;
    public IReadOnlyList<ConfigItem> ResolvedItems { get; } = resolvedItems;
    public CacheMode ActiveCacheMode { get; } = activeCacheMode;
    public CacheMode ConfiguredCacheMode { get; } = configuredCacheMode;
    public bool RestartRequired => ActiveCacheMode != ConfiguredCacheMode
        || (ActiveCacheMode == CacheMode.Native && ResolvedItems.Any(item => item.ConfigName is
            ConfigKeys.NativeCacheFolders or ConfigKeys.NativeCacheMetadataPath or ConfigKeys.NativeCacheWriterMb
            or ConfigKeys.NativeCacheChunkMb));
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
