using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// Client for interacting with rclone's remote control (RC) API.
/// See https://rclone.org/rc/ for API documentation.
/// </summary>
public sealed class RcloneClient : IRcloneClient, IDisposable
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private ForgetErrorEntry? _lastForgetError;
    private readonly ConfigManager? _configManager;
    private readonly EventHandler<ConfigManager.ConfigEventArgs>? _onConfigChanged;
    private IDisposable? _subscription;

    internal static HttpMessageHandler? TestHandler { get; set; }
    internal static Func<int, TimeSpan>? BackoffOverride { get; set; }

    internal static RcloneClient? Current { get; private set; }

    /// <summary>
    /// The built-in daemon's client while it is running, or null.
    ///
    /// Published here because cache invalidation happens from static call sites
    /// deep in the database layer, which cannot take a dependency on the
    /// supervisor. Whatever is serving the mount people read through is what
    /// needs its directory cache dropped.
    /// </summary>
    internal static RcloneClient? Builtin { get; set; }

    public (string Message, DateTimeOffset At)? LastForgetError
    {
        get
        {
            var entry = Volatile.Read(ref _lastForgetError);
            return entry is null ? null : (entry.Message, entry.At);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string? Host { get; private set; }
    private string? User { get; set; }
    private string? Pass { get; set; }
    public bool IsRemoteControlEnabled { get; private set; }

    private static HttpClient CreateHttpClient() => new() { Timeout = RequestTimeout };

    public RcloneClient(ConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        _configManager = configManager;
        Host = configManager.GetRcloneHost();
        User = configManager.GetRcloneUser();
        Pass = configManager.GetRclonePass();
        IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
        _onConfigChanged = OnConfigChanged;
        _subscription = configManager.Subscribe(_onConfigChanged);
    }

    /// <summary>
    /// A client bound to an explicit endpoint rather than to the configured
    /// external rclone.
    ///
    /// The built-in daemon is a second rclone, on loopback, with credentials
    /// generated at process start. Giving it its own instance means the user's
    /// saved external settings are never overwritten, and the generated password
    /// never has to be persisted. It costs nothing at runtime: every instance
    /// shares the one process-wide <see cref="HttpClient"/>.
    /// </summary>
    public static RcloneClient ForEndpoint(string host, string? user, string? pass) =>
        new(host, user, pass);

    private RcloneClient(string host, string? user, string? pass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _configManager = null;
        Host = host;
        User = user;
        Pass = pass;
        IsRemoteControlEnabled = true;
        _onConfigChanged = null;
    }

    /// <summary>
    /// Process-wide instance used by remaining static call sites and tests.
    /// Production also registers this instance as <see cref="IRcloneClient"/>.
    /// </summary>
    public static void Initialize(ConfigManager configManager)
    {
        Current?.Dispose();
        Current = new RcloneClient(configManager);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs configEventArgs)
    {
        var changedConfig = configEventArgs.ChangedConfig;
        if (changedConfig.TryGetValue(ConfigKeys.RcloneHost, out var host)) Host = host;
        if (changedConfig.TryGetValue(ConfigKeys.RcloneUser, out var user)) User = user;
        if (changedConfig.TryGetValue(ConfigKeys.RclonePass, out var pass)) Pass = pass;
        if (changedConfig.ContainsKey(ConfigKeys.RcloneRcEnabled) && _configManager is not null)
            IsRemoteControlEnabled = _configManager.IsRcloneRemoteControlEnabled();
    }

    /// <summary>
    /// Refresh the VFS directory cache for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to refresh</param>
    /// <param name="recursive">Whether to refresh recursively</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public async Task<RcloneResponse> RefreshVfsPaths(
        IEnumerable<string> paths,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new RcloneResponse { Success = true };

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        if (recursive)
            request["recursive"] = true;

        Log.Debug("Rclone vfs/refresh: {0}", paths.ToIndentedJson());
        return await Post<RcloneResponse>("vfs/refresh", request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forget (clear) VFS directory cache entries for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to forget</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public async Task<VfsForgetResponse> ForgetVfsPaths(
        IEnumerable<string> paths,
        string? fs = null,
        CancellationToken cancellationToken = default)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new VfsForgetResponse { Success = true, Forgotten = new List<string>() };

        var request = new Dictionary<string, object?>();

        // rclone refuses an unqualified vfs/forget once more than one VFS is
        // active, and retrying the same ambiguous request cannot fix it.
        if (!string.IsNullOrWhiteSpace(fs)) request["fs"] = fs;

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        Log.Debug("Rclone vfs/forget: {0}", paths.ToIndentedJson());

        const int maxAttempts = 4;
        VfsForgetResponse? lastResponse = null;
        var pathsDisplay = string.Join(", ", pathList);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            lastResponse = await Post<VfsForgetResponse>("vfs/forget", request, cancellationToken).ConfigureAwait(false);

            if (lastResponse.Success)
            {
                Interlocked.Exchange(ref _lastForgetError, null);
                return lastResponse;
            }

            if (lastResponse.Error == "Authentication failed")
            {
                Interlocked.Exchange(ref _lastForgetError, null);
                return lastResponse;
            }

            if (attempt < maxAttempts)
                await Task.Delay(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
        }

        var reason = lastResponse?.Error ?? "Unknown error";
        Interlocked.Exchange(ref _lastForgetError, new ForgetErrorEntry(reason, DateTimeOffset.UtcNow));
        Log.Warning(
            "Rclone vfs/forget failed after {Attempts} attempts for paths {Paths}. Reason: {Reason}; mounted clients may show stale entries until rclone's dir-cache expires",
            maxAttempts,
            pathsDisplay,
            reason);

        return lastResponse!;
    }

    private static TimeSpan GetForgetBackoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60d, 5d * Math.Pow(2, attempt - 1)));

    private static TimeSpan GetBackoff(int attempt) =>
        BackoffOverride?.Invoke(attempt) ?? GetForgetBackoff(attempt);

    /// <summary>
    /// Get VFS statistics including cache information.
    /// </summary>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public async Task<VfsStatsResponse> GetVfsStats(
        string? fs = null,
        CancellationToken cancellationToken = default)
    {
        var request = fs != null ? new { fs } : null;
        return await Post<VfsStatsResponse>("vfs/stats", request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// List the filesystems rclone currently has mounted.
    /// </summary>
    public async Task<ListMountsResponse> ListMounts(CancellationToken cancellationToken = default)
    {
        return await Post<ListMountsResponse>("mount/listmounts", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mount a remote onto a directory.
    /// </summary>
    /// <param name="fs">The remote and path to serve, for example <c>infinidysk:/</c>.</param>
    /// <param name="mountPoint">The directory to mount onto.</param>
    /// <param name="mountOpt">Mount options (<c>AllowOther</c> and similar), or null for rclone's defaults.</param>
    /// <param name="vfsOpt">VFS options (<c>CacheMode</c>, <c>DirCacheTime</c> and similar), or null for rclone's defaults.</param>
    public async Task<MountResponse> MountFs(
        string fs,
        string mountPoint,
        IReadOnlyDictionary<string, object?>? mountOpt,
        IReadOnlyDictionary<string, object?>? vfsOpt,
        CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object?>
        {
            ["fs"] = fs,
            ["mountPoint"] = mountPoint,
        };

        if (mountOpt is { Count: > 0 }) request["mountOpt"] = mountOpt;
        if (vfsOpt is { Count: > 0 }) request["vfsOpt"] = vfsOpt;

        Log.Debug("Rclone mount/mount: {Fs} on {MountPoint}", fs, mountPoint);
        return await Post<MountResponse>("mount/mount", request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unmount a single mount point.
    /// </summary>
    public async Task<RcloneResponse> UnmountFs(string mountPoint, CancellationToken cancellationToken = default)
    {
        Log.Debug("Rclone mount/unmount: {MountPoint}", mountPoint);
        return await Post<RcloneResponse>(
            "mount/unmount",
            new Dictionary<string, object?> { ["mountPoint"] = mountPoint },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unmount everything this daemon has mounted.
    /// </summary>
    /// <remarks>
    /// Used on shutdown. A FUSE mount outlives the process that created it, so
    /// releasing the mounts explicitly is more reliable than relying on rclone
    /// acting on a signal before the host tears the process down.
    /// </remarks>
    public async Task<RcloneResponse> UnmountAll(CancellationToken cancellationToken = default)
    {
        Log.Debug("Rclone mount/unmountall");
        return await Post<RcloneResponse>("mount/unmountall", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Create or replace a remote in rclone's config.
    /// </summary>
    /// <remarks>
    /// <c>obscure</c> is always requested so rclone applies its own password
    /// obscuring. Reimplementing that here would be a second place to get
    /// credential handling wrong.
    /// </remarks>
    public async Task<RcloneResponse> CreateRemote(
        string name,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["type"] = type,
            ["parameters"] = parameters,
            ["opt"] = new Dictionary<string, object?>
            {
                ["obscure"] = true,
                ["nonInteractive"] = true,
            },
        };

        // The parameters carry the WebDAV password, so only the remote name is
        // logged, and the response is redacted: rclone includes the request it
        // could not process in some error bodies, and that body would otherwise
        // reach both the log and the admin UI.
        Log.Debug("Rclone config/create: {Name} ({Type})", name, type);
        return await Post<RcloneResponse>("config/create", request, cancellationToken, redactResponseBody: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// List the remotes configured in rclone's config file.
    /// </summary>
    public async Task<ListRemotesResponse> ListRemotes(CancellationToken cancellationToken = default)
    {
        return await Post<ListRemotesResponse>("config/listremotes", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Get rclone version information.
    /// </summary>
    public async Task<CoreVersionResponse> GetVersion(CancellationToken cancellationToken = default)
    {
        return await Post<CoreVersionResponse>("core/version", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test connectivity - a no-operation call.
    /// </summary>
    public async Task<RcloneResponse> NoOp(CancellationToken cancellationToken = default)
    {
        return await Post<RcloneResponse>("rc/noop", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if the rclone RC server is reachable and authenticated.
    /// </summary>
    public async Task<bool> IsAvailable(CancellationToken cancellationToken = default)
    {
        try
        {
            await NoOp(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Test connectivity to a rclone RC server with the given credentials.
    /// </summary>
    public static async Task<RcloneResponse> TestConnection(
        string host,
        string? user,
        string? pass,
        CancellationToken cancellationToken = default)
    {
        var result = await Post<CoreVersionResponse>(host, user, pass, "core/version", null, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success && string.IsNullOrEmpty(result.Version))
            return new RcloneResponse { Success = false, Error = "Connected but received empty version" };
        return result;
    }

    public static Task<VfsStatsResponse> GetVfsStats(
        string host,
        string? user,
        string? pass,
        CancellationToken cancellationToken = default) =>
        Post<VfsStatsResponse>(host, user, pass, "vfs/stats", null, cancellationToken);

    private static async Task<T> Post<T>
    (
        string host,
        string? user,
        string? pass,
        string endpoint,
        object? body,
        CancellationToken cancellationToken,
        bool redactResponseBody = false
    ) where T : RcloneResponse, new()
    {
        var url = $"{host}/{endpoint}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (body != null)
        {
            var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        AddAuthHeader(request, user, pass);

        try
        {
            using var response = await SendRequest(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // rclone echoes the request back in some error bodies, so a
                // request carrying a credential must not have its response
                // logged or handed to a caller that renders it.
                Log.Warning("Rclone RC request to {Endpoint} failed with status {StatusCode}: {Content}",
                    endpoint, response.StatusCode, redactResponseBody ? "<redacted>" : content);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new T { Success = false, Error = "Authentication failed" };
                }

                if (redactResponseBody)
                    return new T { Success = false, Error = $"HTTP {response.StatusCode}" };

                try
                {
                    var errorResponse = JsonSerializer.Deserialize<RcloneErrorResponse>(content, JsonOptions);
                    return new T { Success = false, Error = errorResponse?.Error ?? $"HTTP {response.StatusCode}" };
                }
                catch
                {
                    return new T { Success = false, Error = $"HTTP {response.StatusCode}: {content}" };
                }
            }

            if (string.IsNullOrWhiteSpace(content) || content == "{}")
            {
                return new T { Success = true };
            }

            var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
            if (result is null)
            {
                Log.Warning(
                    "Rclone RC request to {Endpoint} returned an incompatible response. Reason: response body was null",
                    endpoint);
                return new T { Success = false, Error = "Rclone returned an incompatible response" };
            }

            result.Success = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning("Rclone RC request to {Endpoint} failed. Reason: {Reason}", endpoint, ex.Message);
            return new T { Success = false, Error = ex.Message };
        }
        catch (TaskCanceledException)
        {
            Log.Warning(
                "Rclone RC request to {Endpoint} timed out after {TimeoutSeconds}s",
                endpoint,
                (int)HttpClient.Timeout.TotalSeconds);
            return new T { Success = false, Error = "Request timed out" };
        }
        catch (JsonException ex)
        {
            Log.Warning(
                "Rclone RC request to {Endpoint} returned an incompatible response. Reason: {Reason}",
                endpoint,
                ex.Message);
            return new T { Success = false, Error = "Rclone returned an incompatible response" };
        }
    }

    private Task<T> Post<T>(
        string endpoint,
        object? body,
        CancellationToken cancellationToken,
        bool redactResponseBody = false)
        where T : RcloneResponse, new()
        => Post<T>(Host!, User, Pass, endpoint, body, cancellationToken, redactResponseBody);

    private static async Task<HttpResponseMessage> SendRequest(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (TestHandler != null)
        {
            using var client = new HttpClient(TestHandler, disposeHandler: false)
            {
                Timeout = RequestTimeout,
            };
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void AddAuthHeader(HttpRequestMessage request, string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
            return;

        var credentials = $"{user ?? ""}:{pass ?? ""}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
    }

    private sealed class ForgetErrorEntry(string message, DateTimeOffset at)
    {
        public string Message { get; } = message;
        public DateTimeOffset At { get; } = at;
    }
}
