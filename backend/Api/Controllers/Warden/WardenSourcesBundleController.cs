using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-sources-import")]
public class WardenSourcesImportController(WardenStore warden, WardenRemoteSourceService remote) : BaseApiController
{
    private const int MaxItems = 1000;
    private const long MaxUploadBytes = 5L * 1024 * 1024;
    private const string InvalidFormDetail = "Invalid form data.";

    protected override void AuthenticateRequest(ConfigManager configManager)
        => ApiKeyValidator.ValidateWithoutFormParse(HttpContext, configManager);

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = HttpContext.Request;
        if (!request.HasFormContentType)
            throw new BadHttpRequestException("Missing form body.");

        var ct = HttpContext.RequestAborted;
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            throw new BadHttpRequestException(InvalidFormDetail, e);
        }

        var defaultTrust = form["trust"].ToString();
        if (string.IsNullOrWhiteSpace(defaultTrust)) defaultTrust = WardenStore.TrustCorroborate;
        var defaultRefresh = int.TryParse(form["refreshHours"].ToString(), out var drh) ? drh : 24;

        string content;
        if (form.Files.Count > 0)
        {
            var file = form.Files[0];
            if (file.Length > MaxUploadBytes)
                throw new BadHttpRequestException("File is too large.");
            using var reader = new StreamReader(file.OpenReadStream());
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        else
        {
            content = form["text"].ToString();
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new BadHttpRequestException("Paste some entries or choose a file.");
        if (Encoding.UTF8.GetByteCount(content) > MaxUploadBytes)
            throw new BadHttpRequestException("Input is too large.");

        var specs = Parse(content, defaultTrust, defaultRefresh, out var invalid);
        if (specs.Count == 0 && invalid == 0)
            throw new BadHttpRequestException("Nothing found.");

        var (added, skipped) = warden.ImportRemoteSources(specs);

        if (added > 0)
            _ = Task.Run(async () =>
            {
                try { await remote.RefreshDueAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException) { Log.Debug(e, "Warden: post-import refresh failed"); }
            });

        return Ok(new WardenSourcesImportResponse
        {
            Status = true,
            Added = added,
            Skipped = skipped,
            Invalid = invalid,
        });
    }

    private static List<RemoteSourceSpec> Parse(string content, string defaultTrust, int defaultRefresh, out int invalid)
    {
        invalid = 0;
        var specs = new List<RemoteSourceSpec>();
        var trimmed = content.TrimStart();
        var looksJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');

        if (looksJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var it)
                    ? it
                    : root;

                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in items.EnumerateArray())
                    {
                        if (specs.Count >= MaxItems) break;
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            AddSpec(specs, el.GetString(), null, defaultTrust, defaultRefresh, ref invalid);
                        }
                        else if (el.ValueKind == JsonValueKind.Object)
                        {
                            var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
                            var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var trust = el.TryGetProperty("trust", out var t) ? t.GetString() : null;
                            var rh = el.TryGetProperty("refreshHours", out var r) && r.TryGetInt32(out var rv) ? rv : defaultRefresh;
                            AddSpec(specs, url, name, string.IsNullOrWhiteSpace(trust) ? defaultTrust : trust, rh, ref invalid);
                        }
                    }
                    return specs;
                }
            }
            catch (JsonException)
            {
                // Not a JSON bundle; fall through to line-based parsing.
            }
        }

        foreach (var raw in content.Split('\n'))
        {
            if (specs.Count >= MaxItems) break;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            AddSpec(specs, line, null, defaultTrust, defaultRefresh, ref invalid);
        }
        return specs;
    }

    private static void AddSpec(List<RemoteSourceSpec> specs, string? url, string? name, string? trust, int refreshHours, ref int invalid)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            invalid++;
            return;
        }
        specs.Add(new RemoteSourceSpec { Url = url, Name = name, Trust = trust, RefreshHours = refreshHours });
    }
}

[ApiController]
[Route("api/warden-sources-export")]
public class WardenSourcesExportController(WardenStore warden) : BaseApiController
{
    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override Task<IActionResult> HandleRequest()
    {
        var items = warden.GetSources()
            .Where(s => s.Kind == "remote" && !string.IsNullOrWhiteSpace(s.Url))
            .Select(s => new BundleItem
            {
                Url = s.Url!,
                Name = s.Name,
                Trust = s.Trust,
                RefreshHours = s.RefreshHours,
            })
            .ToList();

        var bundle = new Bundle { Version = 1, Items = items };
        var json = JsonSerializer.Serialize(bundle, BundleJsonOptions);

        return Task.FromResult<IActionResult>(File(Encoding.UTF8.GetBytes(json), "application/json", "bundle.json"));
    }

    private class Bundle
    {
        [JsonPropertyName("version")] public int Version { get; init; } = 1;
        [JsonPropertyName("items")] public List<BundleItem> Items { get; init; } = new();
    }

    private class BundleItem
    {
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("trust")] public string? Trust { get; init; }
        [JsonPropertyName("refreshHours")] public int RefreshHours { get; init; }
    }
}

public class WardenSourcesImportResponse : BaseApiResponse
{
    [JsonPropertyName("added")] public required int Added { get; init; }
    [JsonPropertyName("skipped")] public required int Skipped { get; init; }
    [JsonPropertyName("invalid")] public required int Invalid { get; init; }
}
