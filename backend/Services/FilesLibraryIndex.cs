using System.Collections.Frozen;
using NzbWebDAV.Config;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed record FilesLibrarySnapshot(
    string State,
    DateTimeOffset? ScannedAt,
    IReadOnlyDictionary<Guid, string[]> Links,
    string? Error);

public sealed class FilesLibraryIndex : IDisposable
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);
    private sealed record ConfigurationKey(string LibraryRoot, string MountRoot);
    private static readonly IReadOnlyDictionary<Guid, string[]> EmptyLinks =
        new Dictionary<Guid, string[]>().ToFrozenDictionary();
    private readonly ConfigManager _configManager;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private ConfigurationKey? _configuration;
    private FilesLibrarySnapshot _snapshot = new("unknown", null, EmptyLinks, "Library scan pending.");
    private DateTimeOffset? _lastAttempt;
    private Task? _refreshTask;
    private bool _disposed;

    internal Func<string, string, CancellationToken, IReadOnlyDictionary<Guid, string[]>> ReadLinks { get; set; } = Scan;

    public FilesLibraryIndex(ConfigManager configManager, TimeProvider timeProvider)
    {
        _configManager = configManager;
        _timeProvider = timeProvider;
    }

    public FilesLibrarySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfigurationUnderLock();
            if (_configuration is null) return _snapshot;
            var now = _timeProvider.GetUtcNow();
            if (_refreshTask is { IsCompleted: false })
            {
                if (_lastAttempt is { } attemptedAt && now - attemptedAt >= ScanTimeout)
                    _snapshot = new("unknown", _snapshot.ScannedAt, EmptyLinks, "Library scan timed out.");
                return _snapshot;
            }
            if (_lastAttempt is null || now - _lastAttempt.Value >= RefreshInterval)
                StartRefreshUnderLock(_configuration);
            return _snapshot;
        }
    }

    public async Task<FilesLibrarySnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        Task refresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshConfigurationUnderLock();
            if (_configuration is null) return _snapshot;
            if (_refreshTask is null || _refreshTask.IsCompleted) StartRefreshUnderLock(_configuration);
            refresh = _refreshTask!;
        }
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed) RefreshConfigurationUnderLock();
            return _snapshot;
        }
    }

    private void RefreshConfigurationUnderLock()
    {
        var root = _configManager.GetLibraryDir();
        var current = string.IsNullOrWhiteSpace(root) ? null : new ConfigurationKey(root, _configManager.GetRcloneMountDir());
        if (current == _configuration && (current is not null || _snapshot.State == "not-configured")) return;
        _configuration = current;
        _lastAttempt = null;
        _snapshot = current is null
            ? new("not-configured", null, EmptyLinks, null)
            : new("unknown", null, EmptyLinks, "Library scan pending.");
    }

    private void StartRefreshUnderLock(ConfigurationKey configuration)
    {
        _lastAttempt = _timeProvider.GetUtcNow();
        _refreshTask = RefreshCoreAsync(configuration);
    }

    private async Task RefreshCoreAsync(ConfigurationKey configuration)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(ScanTimeout);
        try
        {
            var links = await Task.Run(() => ReadLinks(configuration.LibraryRoot, configuration.MountRoot, timeout.Token),
                timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            Publish(configuration, new("ready", _timeProvider.GetUtcNow(), links, null));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            PublishFailure(configuration, "Library scan timed out.");
            Log.Warning("Files library scan unavailable. Reason: {Reason}", "Scan timed out.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PublishFailure(configuration, "The configured library directory could not be scanned.");
            Log.Warning("Files library scan unavailable. Reason: {Reason}", "The configured library directory could not be scanned.");
        }
        catch (Exception exception)
        {
            PublishFailure(configuration, "Unexpected library scan failure.");
            if (exception is OutOfMemoryException) Log.Fatal(exception, "Files library scan ran out of memory");
            else Log.Error(exception, "Unexpected Files library scan failure");
        }
    }

    private void Publish(ConfigurationKey configuration, FilesLibrarySnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            RefreshConfigurationUnderLock();
            if (_configuration == configuration) _snapshot = snapshot;
        }
    }

    private void PublishFailure(ConfigurationKey configuration, string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            RefreshConfigurationUnderLock();
            if (_configuration == configuration) _snapshot = new("unknown", _snapshot.ScannedAt, EmptyLinks, reason);
        }
    }

    private static IReadOnlyDictionary<Guid, string[]> Scan(string libraryRoot, string mountDir, CancellationToken cancellationToken)
    {
        var links = new Dictionary<Guid, HashSet<string>>();
        foreach (var link in OrganizedLinksUtil.GetLibraryDavItemLinks(libraryRoot, mountDir, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!links.TryGetValue(link.DavItemId, out var paths)) links[link.DavItemId] = paths = new(StringComparer.Ordinal);
            paths.Add(link.LinkPath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return links.ToFrozenDictionary(entry => entry.Key, entry => entry.Value.Order(StringComparer.Ordinal).ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stopping.Cancel();
        _stopping.Dispose();
    }
}