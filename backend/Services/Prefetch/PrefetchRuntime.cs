using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Services.Prefetch;

/// <summary>Optional feature lifetime: cache/index failures cannot prevent ordinary source streaming.</summary>
public sealed class PrefetchRuntime(ConfigManager config, NativeCacheService native, IServiceScopeFactory scopes,
    ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null) : BackgroundService
{
    private readonly Lock _gate = new();
    private PrefetchJobStore? _jobs;
    private PrefetchCoordinator? _coordinator;
    private bool _initialized;
    private sealed record SettingsSnapshot(string? Json, PrefetchSettings Value);
    private SettingsSnapshot? _settings;
    public string? InitializationError { get; private set; }
    private string? _runtimeError;
    public string? RuntimeError => Volatile.Read(ref _runtimeError) ?? _coordinator?.RuntimeError;
    public bool Healthy => InitializationError is null && RuntimeError is null;
    public void ReportMetadataFailure()
    {
        Interlocked.CompareExchange(ref _runtimeError, PrefetchCoordinator.MetadataFailureMessage, null);
        _coordinator?.ReportMetadataFailure();
    }
    public PrefetchJobStore? Jobs { get { Initialize(); return _jobs; } }
    public PrefetchCoordinator? Coordinator { get { Initialize(); return _coordinator; } }

    public async Task WaitForInitializationAsync(CancellationToken cancellationToken)
    {
        await native.WaitForInitializationAsync(cancellationToken).ConfigureAwait(false);
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            if (!Healthy || _initialized || native.Store is null || native.ActiveSettings is null) return;
            _initialized = true;
            try
            {
                _jobs = new PrefetchJobStore(Path.Combine(native.ActiveSettings.MetadataPath, "prefetch.db"), settings: Settings);
                _coordinator = new PrefetchCoordinator(_jobs, new NativePrefetchExecutor(scopes, native, config, _jobs, activeReads, playback, Settings),
                    Settings, () => native.ActiveSettings.Folders.Any(folder => folder.Enabled && !folder.ReadOnly)
                        && (!Settings().PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
            {
                _jobs?.Dispose(); _jobs = null;
                InitializationError = "Prefetch metadata could not initialize. Check local cache metadata storage and permissions.";
            }
        }
    }

    public PrefetchSettings Settings()
    {
        var json = config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings);
        var snapshot = Volatile.Read(ref _settings);
        if (snapshot is not null && string.Equals(snapshot.Json, json, StringComparison.Ordinal)) return snapshot.Value;
        var parsed = PrefetchSettings.Parse(json);
        Volatile.Write(ref _settings, new(json, parsed));
        return parsed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Healthy && Coordinator is { } coordinator) await coordinator.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { ReportMetadataFailure(); }
                await Task.Delay(TimeSpan.FromSeconds(Healthy ? 1 : 30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        base.Dispose();
        _coordinator?.Dispose();
        _jobs?.Dispose();
    }
}
