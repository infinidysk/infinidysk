using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services.Plex;

public sealed record PlexLibraryMetadataSnapshot(DateTimeOffset SyncedAt,
    IReadOnlyList<PlexLibraryMedia> Entries);

public sealed record PlexLibraryMetadataStatus(bool Ready, DateTimeOffset? SyncedAt,
    int EntryCount, string? Warning, bool Syncing);

public interface IPlexLibraryMetadataIndex
{
    PlexLibraryMetadataStatus Status { get; }
    PlexLibraryMedia? Match(string? fileName);
}

/// <summary>Complete, persisted Plex filename index for Media Library matching.</summary>
public sealed class PlexLibraryMetadataService : BackgroundService, IPlexLibraryMetadataIndex
{
    private static readonly Action<ILogger, Exception?> LogLoadFailure =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "PlexLibraryMetadataLoadFailure"),
            "Could not load Plex library metadata snapshot");
    private static readonly Action<ILogger, Exception?> LogSyncFailure =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "PlexLibraryMetadataSyncFailure"),
            "Plex library metadata sync failed; retaining previous snapshot");
    private readonly ConfigManager _config;
    private readonly PlexApiClient _api;
    private readonly ILogger<PlexLibraryMetadataService> _logger;
    private readonly IDisposable _configSubscription;
    private readonly string _path;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _syncSignal = new(0, 1);
    private volatile Index _index = Index.Empty;
    private HashSet<string> _allowedServerIds;
    private volatile bool _syncing;
    private string? _warning;

    public PlexLibraryMetadataService(ConfigManager config, PlexApiClient api,
        ILogger<PlexLibraryMetadataService> logger)
    {
        _config = config;
        _api = api;
        _logger = logger;
        _path = Path.Combine(DavDatabaseContext.ConfigPath, "plex-library-metadata.json");
        _allowedServerIds = AllowedServerIds();
        try
        {
            if (File.Exists(_path))
            {
                var snapshot = JsonSerializer.Deserialize<PlexLibraryMetadataSnapshot>(File.ReadAllText(_path));
                if (snapshot?.Entries is null) throw new JsonException("Plex metadata snapshot has no entries.");
                _index = Index.From(snapshot);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _warning = "Saved Plex library metadata could not be loaded. Sync Plex again.";
            LogLoadFailure(_logger, error);
        }
        _configSubscription = _config.Subscribe((_, args) =>
        {
            if (args.ChangedConfig.ContainsKey(ConfigKeys.MediaLibraryEnabled) ||
                args.ChangedConfig.ContainsKey(ConfigKeys.MediaLibraryPlexServerIds) ||
                args.ChangedConfig.ContainsKey(PlexSettings.ServersKey))
            {
                Volatile.Write(ref _allowedServerIds, AllowedServerIds());
                RequestSync();
            }
        });
    }

    public PlexLibraryMetadataStatus Status => new(_index.SyncedAt.HasValue,
        _index.SyncedAt, _index.Count, _warning ??
            (_index.SyncedAt is { } synced && DateTimeOffset.UtcNow - synced > TimeSpan.FromHours(24)
                ? "Plex matching is more than 24 hours old. Sync Plex to refresh it." : null),
        _syncing || _syncSignal.CurrentCount > 0);

    public PlexLibraryMetadataStatus RequestSync()
    {
        if (!_config.IsMediaLibraryEnabled()) return Status;
        try { _syncSignal.Release(); }
        catch (SemaphoreFullException) { /* An existing request is already queued. */ }
        catch (ObjectDisposedException) { /* Shutdown raced a settings change. */ }
        return Status;
    }

    public PlexLibraryMedia? Match(string? fileName)
    {
        if (!_config.IsMediaLibraryEnabled() || string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (!_index.ByFileName.TryGetValue(name, out var indexed)) return null;
        var allowed = Volatile.Read(ref _allowedServerIds);
        var candidates = indexed.Where(candidate => allowed.Contains(candidate.ServerId)).ToArray();
        if (candidates.Length == 0) return null;
        var first = candidates[0];
        // A filename reused for unrelated Plex media has no trustworthy match.
        return candidates.All(candidate => candidate.MediaType == first.MediaType &&
            string.Equals(candidate.ShowName, first.ShowName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Title, first.Title, StringComparison.OrdinalIgnoreCase) &&
            candidate.Season == first.Season && candidate.Episode == first.Episode &&
            candidate.Year == first.Year) ? first : null;
    }

    private HashSet<string> AllowedServerIds()
    {
        try
        {
            var selected = _config.GetMediaLibraryPlexServerIds();
            return PlexSettings.ParseServers(_config.GetEffectiveConfigValue(PlexSettings.ServersKey))
                .Where(server => server.Enabled && (selected is null || selected.Contains(server.Id)))
                .Select(server => server.Id).ToHashSet(StringComparer.Ordinal);
        }
        catch (ArgumentException) { return []; }
    }

    public async Task<PlexLibraryMetadataStatus> SyncAsync(CancellationToken ct = default)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_config.IsMediaLibraryEnabled()) return Status;
            var selectedServerIds = _config.GetMediaLibraryPlexServerIds();
            var servers = PlexSettings.ParseServers(_config.GetEffectiveConfigValue(PlexSettings.ServersKey))
                .Where(server => server.Enabled &&
                    (selectedServerIds is null || selectedServerIds.Contains(server.Id))).ToArray();
            if (servers.Length == 0)
            {
                _warning = selectedServerIds is { Count: 0 }
                    ? "Select a Plex server for Media Library matching."
                    : "Configure and enable a selected Plex server to match the Media Library.";
                return Status;
            }
            var entries = new List<PlexLibraryMedia>();
            foreach (var server in servers)
            {
                var libraries = await _api.GetLibrariesAsync(server, ct).ConfigureAwait(false);
                if (libraries.Count == 0)
                    throw new PlexRequestException($"No TV or movie libraries are available on {server.Name}.");
                foreach (var library in libraries)
                    entries.AddRange(await _api.GetLibraryMediaAsync(server, library, ct).ConfigureAwait(false));
            }
            var snapshot = new PlexLibraryMetadataSnapshot(DateTimeOffset.UtcNow, entries);
            var temporary = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot), ct).ConfigureAwait(false);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            _index = Index.From(snapshot);
            _warning = null;
            return Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is PlexRequestException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogSyncFailure(_logger, error);
            _warning = $"Plex sync failed: {error.Message} The previous matching snapshot is retained.";
            return Status;
        }
        finally { _syncGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            DateTimeOffset? lastSyncFinishedAt = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_config.IsMediaLibraryEnabled())
                {
                    lastSyncFinishedAt = null;
                    _ = _syncSignal.Wait(0);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }
                if (lastSyncFinishedAt is null ||
                    DateTimeOffset.UtcNow - lastSyncFinishedAt.Value >= TimeSpan.FromHours(6))
                {
                    _syncing = true;
                    try { await SyncAsync(stoppingToken).ConfigureAwait(false); }
                    finally { _syncing = false; }
                    lastSyncFinishedAt = DateTimeOffset.UtcNow;
                }
                if (await _syncSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false))
                    lastSyncFinishedAt = null;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        _configSubscription.Dispose();
        _syncGate.Dispose();
        _syncSignal.Dispose();
        base.Dispose();
    }

    private sealed record Index(DateTimeOffset? SyncedAt,
        IReadOnlyDictionary<string, PlexLibraryMedia[]> ByFileName, int Count)
    {
        public static Index Empty { get; } = new(null,
            new Dictionary<string, PlexLibraryMedia[]>(StringComparer.OrdinalIgnoreCase), 0);

        public static Index From(PlexLibraryMetadataSnapshot snapshot) => new(snapshot.SyncedAt,
            snapshot.Entries.GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase),
            snapshot.Entries.Count);
    }
}
