using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Api.Controllers.ViewerProfiles;

/// <summary>
/// The login picker's profile list. Public by frontend design (the picker renders before any
/// credential is known), so the response holds nothing but an id and a display name.
/// </summary>
[ApiController]
[Route("api/viewer-profiles")]
[ProducesResponseType(typeof(ViewerProfilesResponse), 200)]
public sealed class ViewerProfilesController(ViewerProfileService profiles) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var items = await profiles.ListAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new ViewerProfilesResponse
        {
            Items = items.Select(ViewerProfileContractMapper.ToDto).ToList(),
        });
    }
}

/// <summary>Creates one viewer profile and signs into it from the picker.</summary>
[ApiController]
[Route("api/viewer-profiles")]
[ProducesResponseType(typeof(ViewerProfileResponse), 200)]
public sealed class CreateViewerProfileController(ViewerProfileService profiles) : PostOnlyApiController
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        Response.Headers.CacheControl = "no-store";
        var request = await ReadRequestAsync(HttpContext).ConfigureAwait(false);
        var profile = await profiles
            .CreateAsync(request.Name, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new ViewerProfileResponse
        {
            Profile = ViewerProfileContractMapper.ToDto(profile),
        });
    }

    internal static async Task<CreateViewerProfileRequest> ReadRequestAsync(HttpContext context)
    {
        CreateViewerProfileRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync<CreateViewerProfileRequest>(context.Request.Body, RequestJsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw CatalogException.Validation("The profile request could not be read.");
        }

        if (request is null)
            throw CatalogException.Validation("The profile request could not be read.");

        return request;
    }
}

public sealed class ViewerProfilesResponse : BaseApiResponse
{
    public List<ViewerProfileDto> Items { get; set; } = [];
}

public sealed class ViewerProfileResponse : BaseApiResponse
{
    public ViewerProfileDto? Profile { get; set; }
}

public sealed class ViewerProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed record CreateViewerProfileRequest(string? Name);

internal static class ViewerProfileContractMapper
{
    internal static ViewerProfileDto ToDto(ViewerProfile profile)
        => new() { Id = profile.Id, Name = profile.Name };
}
