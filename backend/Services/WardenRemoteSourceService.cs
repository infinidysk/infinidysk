using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace NzbWebDAV.Services;

public class WardenRemoteSourceService : BackgroundService
{
    private const long MaxDownloadBytes = 256L * 1024 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    private readonly WardenStore _store;

    public WardenRemoteSourceService(WardenStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshDueAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Debug(e, "Warden: remote-source loop error"); }

            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RefreshDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var source in _store.GetSources())
        {
            if (ct.IsCancellationRequested) return;
            if (source.Kind != "remote" || string.IsNullOrWhiteSpace(source.Url)) continue;
            if (now - source.LastChecked < (long)source.RefreshHours * 3600) continue;
            await RefreshAsync(source, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> RefreshAsync(WardenSourceInfo source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            _store.TouchChecked(source.Id, now, "error: no url");
            return "error: no url";
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, source.Url);
            var etag = _store.GetSourceEtag(source.Id);
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                _store.TouchChecked(source.Id, now, $"ok ({source.Count})");
                return "not-modified";
            }
            resp.EnsureSuccessStatusCode();

            var newEtag = resp.Headers.ETag?.ToString();
            await using var raw = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = await BufferAsync(raw, ct).ConfigureAwait(false);

            using Stream body = LooksGzip(buffer) ? new GZipStream(buffer, CompressionMode.Decompress) : buffer;

            using var limitedBody = new LimitedReadStream(body, WardenInputLimits.MaxDecompressedBytes);
            var count = await _store.ReplaceSourceAsync(source.Id, limitedBody, ct).ConfigureAwait(false);
            _store.SetSourceStatus(source.Id, newEtag, now, now, $"ok ({count})");
            Log.Information("Warden: refreshed remote list {Name} ({Count} fingerprints)", source.Name, count);
            return $"ok ({count})";
        }
        catch (Exception e)
        {
            var msg = "error: " + e.Message;
            _store.TouchChecked(source.Id, now, msg);
            Log.Debug(e, "Warden: refresh failed for {Url}", source.Url);
            return msg;
        }
    }

    private static async Task<MemoryStream> BufferAsync(Stream input, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxDownloadBytes)
                throw new InvalidOperationException("Download exceeds the size limit.");
            ms.Write(buf, 0, read);
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
}
