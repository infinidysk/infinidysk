using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    protected static readonly HttpClient HttpClient = new();
    protected virtual HttpClient Client => HttpClient;

    public string Host { get; } = host;
    private string ApiKey { get; } = apiKey;
    private const string BasePath = "/api/v3";

    protected static bool Is2xx(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and < 300;

    public Task<ArrApiInfoResponse> GetApiInfo(CancellationToken ct = default) =>
        GetRoot<ArrApiInfoResponse>($"/api", ct);

    public virtual Task<ArrRepairOutcome> RemoveAndBlocklist(
        string symlinkOrStrmPath,
        Guid downloadId,
        CancellationToken ct = default) =>
        throw new InvalidOperationException();

    public virtual Task<List<ArrRootFolder>> GetRootFolders() =>
        GetRootFolders(CancellationToken.None);

    public virtual Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct) =>
        Get<List<ArrRootFolder>>($"/rootfolder", ct);

    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default) =>
        Get<List<ArrDownloadClient>>($"/downloadClient", ct);

    public Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default) =>
        CommandAsync(new { name = "RefreshMonitoredDownloads" }, ct);

    public Task<ArrQueueStatus> GetQueueStatusAsync(CancellationToken ct = default) =>
        Get<ArrQueueStatus>($"/queue/status", ct);

    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) =>
        Get<ArrQueue<ArrQueueRecord>>($"/queue?protocol=usenet&pageSize=5000", ct);

    public async Task<int> GetQueueCountAsync() =>
        (await Get<ArrQueue<ArrQueueRecord>>($"/queue?pageSize=1").ConfigureAwait(false)).TotalRecords;

    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request) =>
        Delete($"/queue/{id}", request.GetQueryParams());

    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request) =>
        request is not ArrConfig.QueueAction.DoNothing
            ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams())
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

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Add("X-Api-Key", ApiKey);
        return Client.SendAsync(request, ct);
    }
}
