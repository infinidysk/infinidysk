using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-sources")]
public class WardenSourcesController(WardenStore warden, ConfigManager configManager) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(Ok(new WardenSourcesResponse
        {
            Status = true,
            Quorum = configManager.GetWardenQuorum(),
            LocalCount = warden.LocalCount,
            EffectiveCount = warden.EffectiveCount(),
            TotalRows = warden.Count,
            Sources = warden.GetSources(),
        }));
    }
}

[ApiController]
[Route("api/warden-source-add")]
public class WardenSourceAddController(WardenStore warden, WardenRemoteSourceService remote) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Missing form body.");
        var form = HttpContext.Request.Form;

        var url = form["url"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new BadHttpRequestException("Enter a valid http(s) URL.");

        var name = form["name"].ToString();
        if (string.IsNullOrWhiteSpace(name)) name = parsed.Host;
        var trust = form["trust"].ToString();
        if (string.IsNullOrWhiteSpace(trust)) trust = WardenStore.TrustCorroborate;
        var refreshHours = int.TryParse(form["refreshHours"].ToString(), out var rh) ? rh : 24;

        var id = warden.AddSource("remote", name, url, trust, refreshHours);
        var source = warden.GetSources().FirstOrDefault(s => s.Id == id);
        var status = source is null ? "error" : await remote.RefreshAsync(source, HttpContext.RequestAborted).ConfigureAwait(false);

        return Ok(new WardenSourceMutateResponse { Status = true, SourceId = id, Message = status });
    }
}

[ApiController]
[Route("api/warden-source-update")]
public class WardenSourceUpdateController(WardenStore warden) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Missing form body.");
        var form = HttpContext.Request.Form;
        var id = form["id"].ToString();
        if (string.IsNullOrWhiteSpace(id)) throw new BadHttpRequestException("Missing source id.");

        bool? enabled = form.ContainsKey("enabled") ? form["enabled"].ToString() == "true" : null;
        var trust = form.ContainsKey("trust") ? form["trust"].ToString() : null;
        int? refreshHours = form.ContainsKey("refreshHours") && int.TryParse(form["refreshHours"].ToString(), out var rh) ? rh : null;
        var name = form.ContainsKey("name") ? form["name"].ToString() : null;

        warden.UpdateSource(id, enabled, trust, refreshHours, name);
        return Task.FromResult<IActionResult>(Ok(new WardenSourceMutateResponse { Status = true, SourceId = id }));
    }
}

[ApiController]
[Route("api/warden-source-remove")]
public class WardenSourceRemoveController(WardenStore warden) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Missing form body.");
        var form = HttpContext.Request.Form;
        var id = form["id"].ToString();
        if (string.IsNullOrWhiteSpace(id)) throw new BadHttpRequestException("Missing source id.");

        var removed = form["action"].ToString() == "clear"
            ? warden.ClearSource(id)
            : warden.RemoveSource(id) ? 1 : 0;

        return Task.FromResult<IActionResult>(Ok(new WardenSourceMutateResponse { Status = true, SourceId = id, Removed = removed }));
    }
}

[ApiController]
[Route("api/warden-source-refresh")]
public class WardenSourceRefreshController(WardenStore warden, WardenRemoteSourceService remote) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Missing form body.");
        var id = HttpContext.Request.Form["id"].ToString();
        var source = warden.GetSources().FirstOrDefault(s => s.Id == id);
        if (source is null) throw new BadHttpRequestException("Unknown source.");

        var status = await remote.RefreshAsync(source, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new WardenSourceMutateResponse { Status = true, SourceId = id, Message = status });
    }
}

public class WardenSourcesResponse : BaseApiResponse
{
    [JsonPropertyName("quorum")] public required int Quorum { get; init; }
    [JsonPropertyName("localCount")] public required int LocalCount { get; init; }
    [JsonPropertyName("effectiveCount")] public required int EffectiveCount { get; init; }
    [JsonPropertyName("totalRows")] public required int TotalRows { get; init; }
    [JsonPropertyName("sources")] public required List<WardenSourceInfo> Sources { get; init; }
}

public class WardenSourceMutateResponse : BaseApiResponse
{
    [JsonPropertyName("sourceId")] public string? SourceId { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("removed")] public int Removed { get; init; }
}
