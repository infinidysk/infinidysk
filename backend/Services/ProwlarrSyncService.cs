using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Prowlarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ProwlarrSyncService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ConnectionConfigKeys =
    [
        ConfigKeys.ProwlarrUrl,
        ConfigKeys.ProwlarrApiKey,
        ConfigKeys.ProwlarrSyncEnabled,
        ConfigKeys.ProwlarrSyncIntervalMinutes,
    ];

    private readonly ConfigManager _configManager;
    private readonly IndexerConfigWriteLock _writeLock;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    internal Func<DavDatabaseContext> FreshContextFactory { get; set; }
    internal IProwlarrClientFactory ClientFactory { get; set; } = new ProwlarrClientFactory();

    public ProwlarrSyncService(
        ConfigManager configManager,
        IndexerConfigWriteLock writeLock,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null)
    {
        _configManager = configManager;
        _writeLock = writeLock;
        FreshContextFactory = dbContextFactory is null
            ? static () => new DavDatabaseContext()
            : dbContextFactory.CreateDbContext;
        _configManager.OnConfigChanged += OnConfigChanged;
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _syncGate.Dispose();
        base.Dispose();
    }

    public ProwlarrSyncSnapshot GetSnapshot()
    {
        var status = _configManager.GetProwlarrSyncStatus();
        return BuildSnapshot(status, null);
    }

    public async Task<ProwlarrSyncSnapshot> SyncNowAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldRunAutomaticSync())
                    await SyncNowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack("Prowlarr indexer sync loop error.");
            }

            try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (!e.ChangedConfig.Keys.Any(ConnectionConfigKeys.Contains)) return;
        if (!_configManager.IsProwlarrSyncEnabled()) return;

        _ = Task.Run(async () =>
        {
            try { await SyncNowAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "Prowlarr indexer sync after configuration change failed");
            }
        });
    }

    private bool ShouldRunAutomaticSync()
    {
        if (!_configManager.IsProwlarrSyncEnabled()) return false;
        if (_configManager.GetProwlarrUrl() is null || _configManager.GetProwlarrApiKey() is null) return false;

        var status = _configManager.GetProwlarrSyncStatus();
        if (status.LastAttemptAt <= 0) return true;
        var nextAttempt = DateTimeOffset.FromUnixTimeSeconds(status.LastAttemptAt)
            + TimeSpan.FromMinutes(_configManager.GetProwlarrSyncIntervalMinutes());
        return DateTimeOffset.UtcNow >= nextAttempt;
    }

    private async Task<ProwlarrSyncSnapshot> SyncLockedAsync(CancellationToken ct)
    {
        var prowlarrUrl = _configManager.GetProwlarrUrl();
        var apiKey = _configManager.GetProwlarrApiKey();
        if (prowlarrUrl is null || apiKey is null)
            return BuildSnapshot(_configManager.GetProwlarrSyncStatus(), "Prowlarr URL and API key are required.");

        if (_configManager.IsEnvironmentManaged(ConfigKeys.IndexersInstances))
            return await FailAsync(
                "Indexer settings are managed by NZBDAV_CONFIG__INDEXERS__INSTANCES, so Prowlarr sync cannot persist changes.",
                ct).ConfigureAwait(false);

        var attemptedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var client = ClientFactory.Create(prowlarrUrl, apiKey);
            var remoteIndexers = await client.GetIndexersAsync(ct).ConfigureAwait(false);
            return await PersistSuccessAsync(
                prowlarrUrl,
                apiKey,
                remoteIndexers,
                attemptedAt,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (IsExpectedSyncFailure(e))
        {
            Log.Warning("Prowlarr indexer sync failed: {Reason}", e.Message);
            Log.Debug(e, "Prowlarr indexer sync failure stack");
            return await FailAsync(e.Message, ct, attemptedAt).ConfigureAwait(false);
        }
    }

    private async Task<ProwlarrSyncSnapshot> PersistSuccessAsync(
        string prowlarrUrl,
        string apiKey,
        IReadOnlyList<ProwlarrIndexer> remoteIndexers,
        long attemptedAt,
        CancellationToken ct)
    {
        return await _writeLock.RunAsync(async () =>
        {
            await using var db = DavDatabaseContexts.Create(FreshContextFactory);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var configItems = await db.ConfigItems
                .Where(x => x.ConfigName == ConfigKeys.IndexersInstances
                            || x.ConfigName == ConfigKeys.ProfilesInstances
                            || x.ConfigName == ConfigKeys.ProwlarrSyncStatus)
                .ToListAsync(ct).ConfigureAwait(false);

            if (!string.Equals(_configManager.GetProwlarrUrl(), prowlarrUrl, StringComparison.Ordinal)
                || !string.Equals(_configManager.GetProwlarrApiKey(), apiKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Prowlarr settings changed while synchronization was in progress; retrying is required.");
            }

            var indexerConfig = DeserializeConfig<IndexerConfig>(configItems, ConfigKeys.IndexersInstances);
            var profileConfig = DeserializeConfig<ProfileConfig>(configItems, ConfigKeys.ProfilesInstances).Normalized();

            var merge = ProwlarrIndexerSync.Merge(
                indexerConfig,
                profileConfig,
                remoteIndexers,
                prowlarrUrl,
                apiKey);

            if (merge.ProfilesChanged && _configManager.IsEnvironmentManaged(ConfigKeys.ProfilesInstances))
            {
                throw new InvalidOperationException(
                    "Prowlarr renamed or removed an indexer referenced by search profiles, but profiles are managed by NZBDAV_CONFIG__PROFILES__INSTANCES.");
            }

            var status = new ProwlarrSyncStatus
            {
                LastAttemptAt = attemptedAt,
                LastSuccessAt = attemptedAt,
                LastError = null,
                RemoteIndexerCount = merge.RemoteIndexerCount,
                Added = merge.Added,
                Updated = merge.Updated,
                Removed = merge.Removed,
                Skipped = merge.Skipped,
            };

            var changedItems = new List<ConfigItem>();
            if (merge.IndexersChanged)
                changedItems.Add(UpsertConfigItem(
                    db,
                    configItems,
                    ConfigKeys.IndexersInstances,
                    JsonSerializer.Serialize(merge.IndexerConfig)));
            if (merge.ProfilesChanged)
                changedItems.Add(UpsertConfigItem(
                    db,
                    configItems,
                    ConfigKeys.ProfilesInstances,
                    JsonSerializer.Serialize(merge.ProfileConfig)));
            changedItems.Add(UpsertConfigItem(
                db,
                configItems,
                ConfigKeys.ProwlarrSyncStatus,
                JsonSerializer.Serialize(status)));

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _configManager.UpdateValues(changedItems);

            if (merge.SkippedDetails.Count > 0)
            {
                Log.Warning(
                    "Prowlarr indexer sync skipped {Count} indexer(s): {Details}",
                    merge.SkippedDetails.Count,
                    string.Join("; ", merge.SkippedDetails));
            }

            Log.Information(
                "Prowlarr indexer sync completed: {Added} added, {Updated} updated, {Removed} removed, {Skipped} skipped",
                merge.Added,
                merge.Updated,
                merge.Removed,
                merge.Skipped);
            return BuildSnapshot(status, null);
        }, ct).ConfigureAwait(false);
    }

    private async Task<ProwlarrSyncSnapshot> FailAsync(
        string error,
        CancellationToken ct,
        long? attemptedAt = null)
    {
        var status = _configManager.GetProwlarrSyncStatus();
        status.LastAttemptAt = attemptedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        status.LastError = error;
        status.Added = 0;
        status.Updated = 0;
        status.Removed = 0;
        status.Skipped = 0;

        await _writeLock.RunAsync(async () =>
        {
            await using var db = DavDatabaseContexts.Create(FreshContextFactory);
            var existing = await db.ConfigItems
                .FirstOrDefaultAsync(x => x.ConfigName == ConfigKeys.ProwlarrSyncStatus, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                db.ConfigItems.Add(new ConfigItem
                {
                    ConfigName = ConfigKeys.ProwlarrSyncStatus,
                    ConfigValue = JsonSerializer.Serialize(status),
                });
            }
            else
            {
                existing.ConfigValue = JsonSerializer.Serialize(status);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        _configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKeys.ProwlarrSyncStatus,
                ConfigValue = JsonSerializer.Serialize(status),
            },
        ]);
        return BuildSnapshot(status, error);
    }

    private ProwlarrSyncSnapshot BuildSnapshot(ProwlarrSyncStatus status, string? currentError)
    {
        return new ProwlarrSyncSnapshot
        {
            Configured = _configManager.GetProwlarrUrl() is not null && _configManager.GetProwlarrApiKey() is not null,
            SyncEnabled = _configManager.IsProwlarrSyncEnabled(),
            IndexersEnvironmentManaged = _configManager.IsEnvironmentManaged(ConfigKeys.IndexersInstances),
            ProfilesEnvironmentManaged = _configManager.IsEnvironmentManaged(ConfigKeys.ProfilesInstances),
            LastAttemptAt = status.LastAttemptAt > 0 ? status.LastAttemptAt : null,
            LastSuccessAt = status.LastSuccessAt,
            LastError = currentError ?? status.LastError,
            RemoteIndexerCount = status.RemoteIndexerCount,
            Added = status.Added,
            Updated = status.Updated,
            Removed = status.Removed,
            Skipped = status.Skipped,
        };
    }

    private static bool IsExpectedSyncFailure(Exception e) =>
        e is ProwlarrClientException
            or HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or InvalidOperationException
            or DbUpdateException
            or IOException;

    private static T DeserializeConfig<T>(List<ConfigItem> configItems, string configName) where T : new()
    {
        var raw = configItems.FirstOrDefault(x => x.ConfigName == configName)?.ConfigValue;
        if (string.IsNullOrWhiteSpace(raw)) return new T();
        return JsonSerializer.Deserialize<T>(raw) ?? new T();
    }

    private static ConfigItem UpsertConfigItem(
        DavDatabaseContext db,
        List<ConfigItem> trackedItems,
        string configName,
        string configValue)
    {
        var item = trackedItems.FirstOrDefault(x => x.ConfigName == configName);
        if (item is null)
        {
            item = new ConfigItem { ConfigName = configName, ConfigValue = configValue };
            db.ConfigItems.Add(item);
            trackedItems.Add(item);
            return item;
        }

        item.ConfigValue = configValue;
        return item;
    }
}

public sealed class ProwlarrSyncSnapshot
{
    public bool Configured { get; set; }
    public bool SyncEnabled { get; set; }
    public bool IndexersEnvironmentManaged { get; set; }
    public bool ProfilesEnvironmentManaged { get; set; }
    public long? LastAttemptAt { get; set; }
    public long? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int RemoteIndexerCount { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Skipped { get; set; }
}
