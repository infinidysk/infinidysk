using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    protected static readonly HttpClient HttpClient = new() { Timeout = RequestTimeout };
    protected virtual HttpClient Client => HttpClient;

    public string Host { get; } = host;
    private string ApiKey { get; } = apiKey;
    private const string BasePath = "/api/v3";

    protected static bool Is2xx(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and < 300;

    public Task<ArrApiInfoResponse> GetApiInfo(CancellationToken ct = default) =>
        GetRoot<ArrApiInfoResponse>($"/api", ct);

    /// <param name="shouldRequestSearch">
    /// Consulted with the media identity (e.g. "movie:42") right before the
    /// replacement-search command. Returning false withholds only the search;
    /// the file removal, history-failed mark, and blocklist still happen.
    /// </param>
    public virtual Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        Func<string, bool>? shouldRequestSearch = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException();

    public virtual Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct = default) =>
        Get<List<ArrRootFolder>>($"/rootfolder", ct);

    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default) =>
        Get<List<ArrDownloadClient>>($"/downloadClient", ct);

    public Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default) =>
        CommandAsync(new { name = "RefreshMonitoredDownloads" }, ct);

    public virtual Task<ArrQueueStatus> GetQueueStatusAsync(CancellationToken ct = default) =>
        Get<ArrQueueStatus>($"/queue/status", ct);

    public virtual Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) =>
        Get<ArrQueue<ArrQueueRecord>>($"/queue?protocol=usenet&pageSize=5000", ct);

    public virtual Task<ArrHistory> GetImportHistoryAsync(int page, int pageSize, CancellationToken ct = default) =>
        Get<ArrHistory>($"/history?eventType=3&page={page}&pageSize={pageSize}&sortKey=date&sortDirection=descending", ct);

    public async Task<int> GetQueueCountAsync(CancellationToken ct = default) =>
        (await Get<ArrQueue<ArrQueueRecord>>($"/queue?pageSize=1", ct).ConfigureAwait(false)).TotalRecords;

    public Task<HttpStatusCode> DeleteQueueRecord(
        int id,
        DeleteQueueRecordRequest request,
        CancellationToken ct = default) =>
        Delete($"/queue/{id}", request.GetQueryParams(), ct);

    public Task<HttpStatusCode> DeleteQueueRecord(
        int id,
        ArrConfig.QueueAction request,
        CancellationToken ct = default) =>
        request is not ArrConfig.QueueAction.DoNothing
            ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams(), ct)
            : Task.FromResult(HttpStatusCode.OK);

    public Task<ArrCommand> CommandAsync(object command, CancellationToken ct = default) =>
        Post<ArrCommand>($"/command", command, ct);

    protected async Task<int?> GetHistoryRecordId(Guid downloadId, CancellationToken ct = default)
    {
        var history = await Get<ArrHistory>(
            $"/history?downloadId={downloadId:D}&eventType=1&page=1&pageSize=1&sortKey=date&sortDirection=descending",
            ct).ConfigureAwait(false);
        return history.Records.FirstOrDefault()?.Id;
    }

    protected async Task MarkHistoryFailed(int historyId, CancellationToken ct = default)
    {
        _ = await Post<object>($"/history/failed/{historyId}", new { }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retries transient Arr API failures for repair search notifications.
    /// Delete/blocklist steps rely on <see cref="NzbWebDAV.Services.HealthCheckService.DecideArrLinkedRepairAsync"/> fail-safe instead.
    /// </summary>
    protected async Task ExecuteWithTransientRetryAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await operation(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientArrFailure(ex) && attempt < maxAttempts)
            {
#pragma warning disable CA5394 // retry backoff jitter is not security-sensitive
                var delayMs = (int)Math.Pow(2, attempt - 1) * 1000 + Random.Shared.Next(0, 250);
#pragma warning restore CA5394
                Log.Debug(
                    ex,
                    "Transient Arr API failure on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms",
                    attempt,
                    maxAttempts,
                    delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientArrFailure(Exception ex)
    {
        if (ex is TaskCanceledException or OperationCanceledException) return true;
        if (ex is not HttpRequestException httpEx) return false;
        if (!httpEx.StatusCode.HasValue) return true;
        var statusCode = (int)httpEx.StatusCode.Value;
        return statusCode is < 400 or >= 500;
    }

    protected Task<T> Get<T>(string path, CancellationToken ct = default) =>
        GetRoot<T>($"{BasePath}{path}", ct);

    protected Task<T?> GetOrNull<T>(string path, CancellationToken ct = default) where T : class =>
        GetRootOrNull<T>($"{BasePath}{path}", ct);

    protected async Task<T> GetRoot<T>(string rootPath, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}{rootPath}");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false) ?? throw new InvalidDataException("The response deserialized to null.");
    }

    private async Task<T?> GetRootOrNull<T>(string rootPath, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}{rootPath}");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false) ?? throw new InvalidDataException("The response deserialized to null.");
    }

    protected async Task<T> Post<T>(string path, object body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GetRequestUri(path));
        var jsonBody = JsonSerializer.Serialize(body);
        request.Content = new StringContent(jsonBody, new MediaTypeHeaderValue("application/json"));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false) ?? throw new InvalidDataException("The response deserialized to null.");
    }

    protected async Task<HttpStatusCode> Delete(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, GetRequestUri(path, queryParams));
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        return response.StatusCode;
    }

    private string GetRequestUri(string path, Dictionary<string, string>? queryParams = null)
    {
        queryParams ??= new Dictionary<string, string>();
        var resource = $"{Host}{BasePath}{path}";
        var query = queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        if (queryString.Length > 0) resource = $"{resource}?{queryString}";
        return resource;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Add("X-Api-Key", ApiKey);
        return await Client.SendAsync(request, ct).ConfigureAwait(false);
    }
}
