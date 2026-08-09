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
public class RcloneClient
{
    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? Host { get; private set; }
    private static string? User { get; set; }
    private static string? Pass { get; set; }
    public static bool IsRemoteControlEnabled { get; private set; } = false;

    public static void Initialize(ConfigManager configManager)
    {
        Host = configManager.GetRcloneHost();
        User = configManager.GetRcloneUser();
        Pass = configManager.GetRclonePass();
        IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();

        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            var changedConfig = configEventArgs.ChangedConfig;
            if (changedConfig.TryGetValue(ConfigKeys.RcloneHost, out var host)) Host = host;
            if (changedConfig.TryGetValue(ConfigKeys.RcloneUser, out var user)) User = user;
            if (changedConfig.TryGetValue(ConfigKeys.RclonePass, out var pass)) Pass = pass;
            if (changedConfig.ContainsKey(ConfigKeys.RcloneRcEnabled))
                IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
        };
    }

    /// <summary>
    /// Refresh the VFS directory cache for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to refresh</param>
    /// <param name="recursive">Whether to refresh recursively</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<RcloneResponse> RefreshVfsPaths(IEnumerable<string> paths, bool recursive = false)
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
        return await Post<RcloneResponse>("vfs/refresh", request).ConfigureAwait(false);
    }

    /// <summary>
    /// Forget (clear) VFS directory cache entries for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to forget</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsForgetResponse> ForgetVfsPaths(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new VfsForgetResponse { Success = true, Forgotten = new List<string>() };

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        Log.Debug("Rclone vfs/forget: {0}", paths.ToIndentedJson());
        return await Post<VfsForgetResponse>("vfs/forget", request).ConfigureAwait(false);
    }

    /// <summary>
    /// Get VFS statistics including cache information.
    /// </summary>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsStatsResponse> GetVfsStats(string? fs = null)
    {
        var request = fs != null ? new { fs } : null;
        return await Post<VfsStatsResponse>("vfs/stats", request).ConfigureAwait(false);
    }

    /// <summary>
    /// Get rclone version information.
    /// </summary>
    public static async Task<CoreVersionResponse> GetVersion()
    {
        return await Post<CoreVersionResponse>("core/version", null).ConfigureAwait(false);
    }

    /// <summary>
    /// Test connectivity - a no-operation call.
    /// </summary>
    public static async Task<RcloneResponse> NoOp()
    {
        return await Post<RcloneResponse>("rc/noop", null).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if the rclone RC server is reachable and authenticated.
    /// </summary>
    public static async Task<bool> IsAvailable()
    {
        try
        {
            await NoOp().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Test connectivity to a rclone RC server with the given credentials.
    /// </summary>
    public static async Task<RcloneResponse> TestConnection(string host, string? user, string? pass)
    {
        var result = await Post<CoreVersionResponse>(host, user, pass, "core/version", null).ConfigureAwait(false);
        if (result.Success && string.IsNullOrEmpty(result.Version))
            return new RcloneResponse { Success = false, Error = "Connected but received empty version" };
        return result;
    }

    private static async Task<T> Post<T>
    (
        string host,
        string? user,
        string? pass,
        string endpoint,
        object? body
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
            using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Rclone RC request to {Endpoint} failed with status {StatusCode}: {Content}",
                    endpoint, response.StatusCode, content);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new T { Success = false, Error = "Authentication failed" };
                }

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

            var result = JsonSerializer.Deserialize<T>(content, JsonOptions) ?? new T();
            result.Success = true;
            return result;
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
    }

    private static Task<T> Post<T>(string endpoint, object? body) where T : RcloneResponse, new()
        => Post<T>(Host!, User, Pass, endpoint, body);

    private static void AddAuthHeader(HttpRequestMessage request, string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
            return;

        var credentials = $"{user ?? ""}:{pass ?? ""}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
    }
}
