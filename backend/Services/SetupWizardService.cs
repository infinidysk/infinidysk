using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;

namespace NzbWebDAV.Services;

public sealed class SetupWizardService(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ConfigUpdateService configUpdateService)
{
    public const int CurrentWizardVersion = 1;

    private static readonly HashSet<string> AllowedConfigKeys =
    [
        ConfigKeys.ApiImportStrategy,
        ConfigKeys.UsenetSegmentCacheEnabled,
        ConfigKeys.RcloneMountDir,
        ConfigKeys.RcloneRcEnabled,
        ConfigKeys.RcloneHost,
        ConfigKeys.RcloneUser,
        ConfigKeys.RclonePass,
        ConfigKeys.ApiCompletedDownloadsDir,
        ConfigKeys.GeneralBaseUrl,
        ConfigKeys.ArrInstances,
        ConfigKeys.BackupScheduleEnabled,
        ConfigKeys.BackupScheduleTime,
        ConfigKeys.BackupRetentionCount,
        ConfigKeys.MediaLibraryDir,
    ];

    private static readonly HashSet<string> AllowedIngestionMethods =
        ["arrs", "search", "manual"];

    public async Task<SetupWizardSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await dbClient.Ctx.SetupWizardStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == SetupWizardState.SingletonId,
                cancellationToken)
            .ConfigureAwait(false);

        var isResolved = state is not null &&
            state.WizardVersion >= CurrentWizardVersion &&
            state.Disposition is SetupWizardDisposition.Completed or SetupWizardDisposition.Skipped;

        return new SetupWizardSnapshot
        {
            CurrentVersion = CurrentWizardVersion,
            RecordedVersion = state?.WizardVersion,
            Disposition = state?.Disposition,
            SetupRequired = !isResolved,
            IngestionMethods = ParseIngestionMethods(state?.IngestionMethods),
            UpdatedAt = state?.UpdatedAt,
        };
    }

    public async Task<CompleteSetupWizardResult> CompleteAsync(
        CompleteSetupWizardCommand command,
        CancellationToken cancellationToken = default)
    {
        var strategy = command.Strategy.Trim().ToLowerInvariant();
        if (strategy is not ("symlinks" or "strm"))
            throw new BadHttpRequestException("Library strategy must be 'symlinks' or 'strm'.");

        var ingestionMethods = command.IngestionMethods
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ingestionMethods.Length == 0 ||
            ingestionMethods.Any(value => !AllowedIngestionMethods.Contains(value)))
        {
            throw new BadHttpRequestException(
                "Select at least one supported ingestion method: arrs, search, or manual.");
        }

        var requested = command.ConfigItems
            .ToDictionary(item => item.ConfigName, item => item.ConfigValue, StringComparer.Ordinal);
        var unsupported = requested.Keys
            .Where(key => !AllowedConfigKeys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new BadHttpRequestException(
                $"Setup cannot update unsupported setting(s): {string.Join(", ", unsupported)}.");
        }

        SetEnforcedValue(requested, ConfigKeys.ApiImportStrategy, strategy);
        SetEnforcedValue(
            requested,
            ConfigKeys.UsenetSegmentCacheEnabled,
            strategy == "strm" ? "true" : "false");
        ValidateBranch(strategy, requested);

        var currentCacheEnabled = configManager.IsSegmentCacheEnabled();
        var configItems = requested
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ConfigItem
            {
                ConfigName = pair.Key,
                ConfigValue = pair.Value,
            })
            .ToList();

        var batch = await configUpdateService
            .StageAsync(configItems, cancellationToken)
            .ConfigureAwait(false);
        var state = await GetTrackedStateAsync(cancellationToken).ConfigureAwait(false);
        state.WizardVersion = CurrentWizardVersion;
        state.Disposition = SetupWizardDisposition.Completed;
        state.IngestionMethods = JsonSerializer.Serialize(ingestionMethods);
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        configUpdateService.Publish(batch);

        return new CompleteSetupWizardResult
        {
            ChangedConfigKeys = configItems
                .Select(item => item.ConfigName)
                .ToArray(),
            RestartRequired = currentCacheEnabled != (strategy == "strm"),
        };
    }

    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetTrackedStateAsync(cancellationToken).ConfigureAwait(false);
        state.WizardVersion = CurrentWizardVersion;
        state.Disposition = SetupWizardDisposition.Skipped;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SetEnforcedValue(
        Dictionary<string, string> requested,
        string configKey,
        string requiredValue)
    {
        if (!configManager.IsEnvironmentManaged(configKey))
        {
            requested[configKey] = requiredValue;
            return;
        }

        var effectiveValue = configManager.GetEffectiveConfigValue(configKey);
        var matches = string.Equals(
            effectiveValue,
            requiredValue,
            StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            var environmentName = configManager.GetEnvironmentVariableName(configKey) ?? configKey;
            throw new BadHttpRequestException(
                $"The selected library strategy requires `{configKey}={requiredValue}`, but " +
                $"`{environmentName}` currently controls that setting. Update the environment and restart.");
        }

        requested.Remove(configKey);
    }

    private void ValidateBranch(string strategy, IReadOnlyDictionary<string, string> requested)
    {
        if (strategy == "symlinks")
        {
            var mountDir = ProposedValue(
                requested,
                ConfigKeys.RcloneMountDir,
                configManager.GetRcloneMountDir());
            if (string.IsNullOrWhiteSpace(mountDir))
                throw new BadHttpRequestException("Rclone mount directory is required for Symlinks.");

            var rcEnabled = bool.Parse(ProposedValue(
                requested,
                ConfigKeys.RcloneRcEnabled,
                configManager.IsRcloneRemoteControlEnabled().ToString()));
            var rcHost = ProposedValue(
                requested,
                ConfigKeys.RcloneHost,
                configManager.GetRcloneHost() ?? "");
            if (rcEnabled && string.IsNullOrWhiteSpace(rcHost))
                throw new BadHttpRequestException("Rclone RC host is required when notifications are enabled.");
        }
        else
        {
            var completedDir = ProposedValue(
                requested,
                ConfigKeys.ApiCompletedDownloadsDir,
                configManager.GetStrmCompletedDownloadDir());
            if (string.IsNullOrWhiteSpace(completedDir))
                throw new BadHttpRequestException("Completed Downloads Dir is required for STRM.");

            var baseUrl = ProposedValue(
                requested,
                ConfigKeys.GeneralBaseUrl,
                configManager.GetBaseUrl());
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new BadHttpRequestException(
                    "Base URL must be an absolute http(s) URL without credentials, query, or fragment.");
            }
        }

        var libraryDir = ProposedValue(
            requested,
            ConfigKeys.MediaLibraryDir,
            configManager.GetLibraryDir() ?? "");
        var proposedMountDir = ProposedValue(
            requested,
            ConfigKeys.RcloneMountDir,
            configManager.GetRcloneMountDir());
        if (RemoveUnlinkedFilesTask.IsLibraryDirInsideRcloneMount(
                libraryDir,
                proposedMountDir,
                out _,
                out _))
        {
            throw new BadHttpRequestException(
                "Library Directory must be outside the rclone mount and contain the organized media library.");
        }
    }

    private async Task<SetupWizardState> GetTrackedStateAsync(CancellationToken cancellationToken)
    {
        var state = await dbClient.Ctx.SetupWizardStates
            .SingleOrDefaultAsync(
                item => item.Id == SetupWizardState.SingletonId,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is not null) return state;

        state = new SetupWizardState();
        dbClient.Ctx.SetupWizardStates.Add(state);
        return state;
    }

    private static string ProposedValue(
        IReadOnlyDictionary<string, string> requested,
        string configKey,
        string fallback) =>
        requested.GetValueOrDefault(configKey) ?? fallback;

    private static string[] ParseIngestionMethods(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}


public sealed class SetupWizardSnapshot
{
    public required int CurrentVersion { get; init; }
    public int? RecordedVersion { get; init; }
    public SetupWizardDisposition? Disposition { get; init; }
    public required bool SetupRequired { get; init; }
    public required string[] IngestionMethods { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class CompleteSetupWizardCommand
{
    public required string Strategy { get; init; }
    public required string[] IngestionMethods { get; init; }
    public required IReadOnlyCollection<ConfigItem> ConfigItems { get; init; }
}

public sealed class CompleteSetupWizardResult
{
    public required string[] ChangedConfigKeys { get; init; }
    public required bool RestartRequired { get; init; }
}