using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

public partial class WardenBackupService : BackgroundService
{
    private const string ApiBase = "https://api.github.com";
    private const long MaxRestoreBytes = 256L * 1024 * 1024;
    private const long MaxPushBytes = 95L * 1024 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
    {
        Timeout = TimeSpan.FromSeconds(100),
    };

    private readonly WardenStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WardenBackupService(WardenStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PushDueAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Debug(e, "Warden backup: loop error"); }

            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task PushDueAsync(CancellationToken ct)
    {
        var s = _store.GetBackupSettings();
        if (!s.Enabled || !s.HasToken || string.IsNullOrWhiteSpace(s.Repo)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - s.LastAt < (long)s.IntervalHours * 3600) return;
        await PushAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> PushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await PushInnerAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string> PushInnerAsync(CancellationToken ct)
    {
        var s = _store.GetBackupSettings();
        var token = _store.GetBackupToken();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (string.IsNullOrWhiteSpace(s.Repo) || string.IsNullOrWhiteSpace(s.Path) || token is null)
            return Fail(now, "error: not configured");
        if (!RepoPattern().IsMatch(s.Repo))
            return Fail(now, "error: invalid repo (expected owner/name)");

        byte[] bytes;
        int count;
        try
        {
            var ids = s.Scope == "merged"
                ? _store.GetSources().Select(x => x.Id).ToList()
                : new List<string> { WardenStore.LocalSourceId };
            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                await _store.ExportToAsync(gz, ids, dedup: true, ct).ConfigureAwait(false);
            bytes = ms.ToArray();
            count = s.Scope == "merged" ? _store.Count : _store.LocalCount;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden backup: export failed");
            return Fail(now, "error: export failed");
        }

        if (bytes.Length > MaxPushBytes)
            return Fail(now, $"error: backup {FormatSize(bytes.Length)} exceeds GitHub's 100 MB file limit. Trim sources or use Releases mode.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var (existingSha, existingHash) = _store.GetBackupSyncState();
        if (hash == existingHash)
        {
            _store.SetBackupResult(now, $"up to date ({count:N0} fps)", null, null);
            return "up to date";
        }

        try
        {
            var sha = existingSha ?? await GetRemoteShaAsync(s, token, ct).ConfigureAwait(false);
            var put = await PutFileAsync(s, token, bytes, sha, ct).ConfigureAwait(false);
            if (put.StaleConflict)
            {
                sha = await GetRemoteShaAsync(s, token, ct).ConfigureAwait(false);
                put = await PutFileAsync(s, token, bytes, sha, ct).ConfigureAwait(false);
            }
            if (!put.Ok) return Fail(now, "error: could not update file");

            var storedSha = string.IsNullOrEmpty(put.Sha) ? null : put.Sha;
            _store.SetBackupResult(now, $"ok ({count:N0} fps · {FormatSize(bytes.Length)})", storedSha, hash);
            Log.Information("Warden backup: pushed {Count} fingerprints to {Repo}", count, s.Repo);
            return "ok";
        }
        catch (GithubException ge)
        {
            return Fail(now, $"error: {ge.Message}");
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden backup: push failed");
            return Fail(now, "error: push failed");
        }
    }

    public async Task<string> RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _store.GetBackupSettings();
            var token = _store.GetBackupToken();
            if (string.IsNullOrWhiteSpace(s.Repo) || string.IsNullOrWhiteSpace(s.Path) || token is null)
                throw new BadHttpRequestException("Backup is not configured.");
            if (!RepoPattern().IsMatch(s.Repo))
                throw new BadHttpRequestException("Invalid repo (expected owner/name).");

            using var req = NewRequest(HttpMethod.Get, ContentsUrl(s) + "?ref=" + Uri.EscapeDataString(s.Branch), token);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new BadHttpRequestException("No backup file was found in the repo.");
            if (!resp.IsSuccessStatusCode)
                throw new BadHttpRequestException($"GitHub returned {(int)resp.StatusCode}: {await GithubMessage(resp).ConfigureAwait(false)}");

            await using var raw = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = await BufferAsync(raw, ct).ConfigureAwait(false);
            using Stream body = LooksGzip(buffer) ? new GZipStream(buffer, CompressionMode.Decompress) : buffer;
            using var limitedBody = new LimitedReadStream(body, WardenInputLimits.MaxDecompressedBytes);
            var count = await _store.ReplaceSourceAsync(WardenStore.LocalSourceId, limitedBody, ct).ConfigureAwait(false);
            Log.Information("Warden backup: restored {Count} fingerprints from {Repo}", count, s.Repo);
            return $"Restored {count:N0} fingerprints into your list.";
        }
        finally { _gate.Release(); }
    }

    private string Fail(long now, string status)
    {
        _store.SetBackupResult(now, status, null, null);
        return status;
    }

    private static string ContentsUrl(WardenBackupSettings s) =>
        $"{ApiBase}/repos/{s.Repo}/contents/{EncodePath(s.Path)}";

    private async Task<string?> GetRemoteShaAsync(WardenBackupSettings s, string token, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, ContentsUrl(s) + "?ref=" + Uri.EscapeDataString(s.Branch), token);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw new GithubException($"{(int)resp.StatusCode} {await GithubMessage(resp).ConfigureAwait(false)}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
    }

    private async Task<PutResult> PutFileAsync(WardenBackupSettings s, string token, byte[] bytes, string? sha, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message"] = $"warden backup {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC",
            ["content"] = Convert.ToBase64String(bytes),
            ["branch"] = s.Branch,
        };
        if (sha is not null) payload["sha"] = sha;

        using var req = NewRequest(HttpMethod.Put, ContentsUrl(s), token);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity && sha is not null)
            return new PutResult { StaleConflict = true };
        if (!resp.IsSuccessStatusCode)
            throw new GithubException($"{(int)resp.StatusCode} {await GithubMessage(resp).ConfigureAwait(false)}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var newSha = doc.RootElement.TryGetProperty("content", out var c) && c.TryGetProperty("sha", out var sh)
            ? sh.GetString()
            : null;
        return new PutResult { Ok = true, Sha = newSha };
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("nzbdav-warden");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static async Task<string> GithubMessage(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } msg)
            {
                msg = msg.Replace('\n', ' ').Replace('\r', ' ').Trim();
                return msg.Length > 120 ? msg[..120] : msg;
            }
        }
        catch
        {
            // Body isn't GitHub's JSON error shape; fall back to the reason phrase.
        }
        return resp.ReasonPhrase ?? "request failed";
    }

    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(seg => seg != "." && seg != "..")
            .Select(Uri.EscapeDataString));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private static async Task<MemoryStream> BufferAsync(Stream input, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxRestoreBytes)
                throw new InvalidOperationException("Backup file exceeds the size limit.");
            await ms.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        ms.Position = 0;
        return ms;
    }

    private static bool LooksGzip(MemoryStream ms)
    {
        if (ms.Length < 2) return false;
        var buf = ms.GetBuffer();
        var isGz = buf[0] == 0x1f && buf[1] == 0x8b;
        ms.Position = 0;
        return isGz;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepoPattern();

    private struct PutResult
    {
        public bool Ok { get; init; }
        public string? Sha { get; init; }
        public bool StaleConflict { get; init; }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    private sealed class GithubException(string message) : Exception(message);
}
