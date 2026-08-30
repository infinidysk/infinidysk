using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Watchtower;

[ApiController]
[Route("api/watchtower-discover-catalogs")]
public class WatchtowerDiscoverController(ListSourceEnumerator enumerator) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var url = HttpContext.Request.HasFormContentType
            ? HttpContext.Request.Form["url"].ToString()
            : HttpContext.Request.Query["url"].ToString();
        if (string.IsNullOrWhiteSpace(url))
            throw new BadHttpRequestException("A manifest URL is required.");

        ListSourceEnumerator.DiscoverResult result;
        try
        {
            result = await enumerator.DiscoverCatalogsAsync(url, ct).ConfigureAwait(false);
        }
        catch (RemoteResponseException e)
        {
            throw new BadHttpRequestException(e.Message);
        }
        catch (InvalidOperationException e)
        {
            // User-facing validation/parse problem -> 400 with a readable message.
            throw new BadHttpRequestException(e.Message);
        }

        return Ok(new WatchtowerDiscoverResponse
        {
            Status = true,
            AddonName = result.AddonName,
            Catalogs = result.Catalogs.Select(c => new WatchtowerDiscoverResponse.CatalogDto
            {
                Type = c.Type,
                Id = c.Id,
                Name = c.Name,
                Url = c.Url,
                ExtraRequired = c.ExtraRequired,
            }).ToList(),
        });
    }
}

public class WatchtowerDiscoverResponse : BaseApiResponse
{
    [JsonPropertyName("addonName")] public string? AddonName { get; init; }
    [JsonPropertyName("catalogs")] public required List<CatalogDto> Catalogs { get; init; }

    public class CatalogDto
    {
        [JsonPropertyName("type")] public required string Type { get; init; }
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("url")] public required string Url { get; init; }
        [JsonPropertyName("extraRequired")] public string? ExtraRequired { get; init; }
    }
}
