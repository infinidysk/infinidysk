using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Services.Metrics;

public sealed class ProviderQuotaService : BackgroundService
{
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(1);
    private readonly ConfigManager _configManager;
    private readonly ProviderBytesTracker _tracker;
    private readonly Func<MetricsDbContext> _dbFactory;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Dictionary<string, long> _resetAt = new(StringComparer.Ordinal);
    private readonly Lock _configGate = new();
    private readonly IDisposable _subscription;

    public ProviderQuotaService(
        ConfigManager configManager,
        ProviderBytesTracker tracker,
        Func<MetricsDbContext>? dbFactory = null)
    {
        _configManager = configManager;
        _tracker = tracker;
        _dbFactory = dbFactory ?? (() => new MetricsDbContext());
        foreach (var provider in configManager.GetUsenetProviderConfig().Providers
                     .Where(p => p.ProviderId != Guid.Empty))
            _resetAt[UsenetProviderIdentity.MetricsKey(provider)] = provider.BytesUsedResetAt;
        _subscription = configManager.Subscribe(OnConfigChanged);
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs args)
    {
        if (!args.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;

        lock (_configGate)
        {
            foreach (var provider in _configManager.GetUsenetProviderConfig().Providers
                         .Where(p => p.ProviderId != Guid.Empty))
            {
                var key = UsenetProviderIdentity.MetricsKey(provider);
                if (!_resetAt.TryGetValue(key, out var previous))
                {
                    _resetAt[key] = provider.BytesUsedResetAt;
                    _tracker.InitializeQuota(key, provider.BytesUsedResetAt, 0);
                    continue;
                }

                if (previous == provider.BytesUsedResetAt) continue;
                _resetAt[key] = provider.BytesUsedResetAt;
                _tracker.ResetQuota(key, provider.BytesUsedResetAt);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PersistInterval, stoppingToken).ConfigureAwait(false);
                await PersistAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                e.LogWarningKnownOrStack(
                    "Provider quota persistence failed; the current counters remain in memory and will retry next minute.");
            }
        }
    }

    internal async Task PersistAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _dbFactory();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var snapshot in _tracker.SnapshotQuota())
                await ProviderUsageHelper.PersistQuotaSnapshotAsync(db, snapshot, now, ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription.Dispose();
        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.LogWarningKnownOrStack("Final provider quota persistence failed.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _subscription.Dispose();
        _persistGate.Dispose();
        base.Dispose();
    }
}